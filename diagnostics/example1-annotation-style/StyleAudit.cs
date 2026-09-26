using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

[assembly: CommandClass(typeof(StyleAudit))]

// Read-only extraction, independent of the production plugin and its releases.
public sealed class StyleAudit
{
    private readonly List<string> errors = new List<string>();
    private readonly Dictionary<string, object> blocks = new Dictionary<string, object>();
    private Transaction transaction = null!;

    [CommandMethod("SD_AUDIT_REFERENCE_STYLE")]
    public void Run()
    {
        var directory = Environment.GetEnvironmentVariable("SD_STYLE_AUDIT_ROOT") ?? throw new InvalidOperationException("Missing output directory");
        try
        {
            var db = Application.DocumentManager.MdiActiveDocument.Database;
            using var tr = db.TransactionManager.StartTransaction(); transaction = tr;
            var result = new Dictionary<string, object>
            {
                ["Source"] = db.Filename,
                ["Database"] = Read(db, "Ltscale,Celtscale,Cannoscale,Insunits,Dimscale,Dimlfac,Dimtxt,Dimstyle,Textstyle,Textsize,Lwdefault"),
                ["Layers"] = Table(db.LayerTableId, "Name,Color,LineWeight,LinetypeObjectId,IsPlottable,IsOff,IsFrozen"),
                ["TextStyles"] = Table(db.TextStyleTableId, "Name,FileName,BigFontFileName,TextSize,XScale,ObliquingAngle,IsVertical,FlagBits"),
                ["DimensionStyles"] = Table(db.DimStyleTableId, "Name", true),
                ["LineTypes"] = LineTypes(db.LinetypeTableId),
                ["Entities"] = Entities(SymbolUtilityServices.GetBlockModelSpaceId(db), 0),
                ["Blocks"] = blocks,
                ["ReadErrors"] = errors
            };
            File.WriteAllText(Path.Combine(directory, "reference-raw.json"), new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(result));
        }
        catch (System.Exception e) { File.WriteAllText(Path.Combine(directory, "audit-failure.txt"), e.ToString()); }
    }

    private List<object> Table(ObjectId id, string fields, bool dimensions = false)
    {
        var table = (SymbolTable)transaction.GetObject(id, OpenMode.ForRead);
        return table.Cast<ObjectId>().Select(key => (object)Read(transaction.GetObject(key, OpenMode.ForRead), fields, dimensions)).ToList();
    }

    private List<object> LineTypes(ObjectId id)
    {
        var table = (LinetypeTable)transaction.GetObject(id, OpenMode.ForRead);
        return table.Cast<ObjectId>().Select(key =>
        {
            var type = (LinetypeTableRecord)transaction.GetObject(key, OpenMode.ForRead);
            var data = Read(type, "Name,AsciiDescription,PatternLength,NumDashes");
            data["Dashes"] = Enumerable.Range(0, type.NumDashes).Select(i => new { Length = type.DashLengthAt(i) }).ToList();
            return (object)data;
        }).ToList();
    }

    private List<object> Entities(ObjectId blockId, int depth)
    {
        var block = (BlockTableRecord)transaction.GetObject(blockId, OpenMode.ForRead);
        var output = new List<object>();
        foreach (ObjectId id in block)
        {
            var entity = (Entity)transaction.GetObject(id, OpenMode.ForRead);
            var data = Read(entity, "Handle,Layer,Color,Linetype,LinetypeScale,LineWeight,Visible,Annotative");
            data["Type"] = entity.GetType().Name;
            if (entity is DBText text)
            { data["Text"] = Read(text, "TextString,Height,WidthFactor,TextStyleId,Rotation,Oblique,Position,AlignmentPoint,HorizontalMode,VerticalMode,IsMirroredInX,IsMirroredInY"); }
            if (entity is MText mtext)
            { data["MText"] = Read(mtext, "Contents,Text,TextHeight,TextStyleId,Location,Rotation,Width,ActualWidth,ActualHeight,Attachment"); }
            if (entity is Dimension dimension)
            {
                data["Dimension"] = Read(dimension, "DimensionStyle,DimensionText,Measurement,TextPosition,TextRotation,Normal,Elevation,DimBlockId", true);
                data["Definition"] = Read(dimension, "XLine1Point,XLine2Point,DimLinePoint,Rotation,Oblique,Center,ChordPoint,FarChordPoint,LeaderLength");
                using var effective = dimension.GetDimstyleData();
                data["EffectiveDimensionStyle"] = Read(effective, "Name", true);
                AddBlock(dimension.DimBlockId, depth);
            }
            if (entity is Line line) { data["Geometry"] = Read(line, "StartPoint,EndPoint,Length"); }
            if (entity is Circle circle) { data["Geometry"] = Read(circle, "Center,Radius"); }
            if (entity is Arc arc) { data["Geometry"] = Read(arc, "Center,Radius,StartAngle,EndAngle"); }
            if (entity is Polyline poly)
            {
                data["Geometry"] = Read(poly, "Closed,ConstantWidth,Elevation,NumberOfVertices");
                data["Vertices"] = Enumerable.Range(0, poly.NumberOfVertices).Select(i => new { X = poly.GetPoint2dAt(i).X, Y = poly.GetPoint2dAt(i).Y, Bulge = poly.GetBulgeAt(i), StartWidth = poly.GetStartWidthAt(i), EndWidth = poly.GetEndWidthAt(i) }).ToList();
            }
            if (entity is BlockReference reference)
            {
                data["Block"] = Read(reference, "Name,BlockTableRecord,Position,ScaleFactors,Rotation,IsDynamicBlock,DynamicBlockTableRecord");
                data["Attributes"] = reference.AttributeCollection.Cast<ObjectId>().Select(a => Read(transaction.GetObject(a, OpenMode.ForRead), "Tag,TextString,Height,WidthFactor,TextStyleId,Position,Rotation")).ToList();
                AddBlock(reference.BlockTableRecord, depth);
            }
            output.Add(data);
        }
        return output;
    }

    private void AddBlock(ObjectId id, int depth)
    {
        if (id.IsNull || blocks.ContainsKey(id.Handle.ToString())) { return; }
        if (depth >= 8) { throw new InvalidOperationException("Unexpected deeply nested reference block"); }
        var block = (BlockTableRecord)transaction.GetObject(id, OpenMode.ForRead);
        blocks[id.Handle.ToString()] = new Dictionary<string, object>();
        blocks[id.Handle.ToString()] = new { block.Name, Entities = Entities(id, depth + 1) };
    }

    private Dictionary<string, object> Read(object obj, string names, bool dimensionProperties = false)
    {
        var requested = new HashSet<string>(names.Split(','), StringComparer.Ordinal);
        var output = new Dictionary<string, object>();
        foreach (var property in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && (requested.Contains(p.Name) || (dimensionProperties && p.Name.StartsWith("Dim", StringComparison.Ordinal)))))
        {
            try { output[property.Name] = Value(property.GetValue(obj, null)); }
            catch (System.Exception e) { var message = obj.GetType().Name + "." + property.Name + ": " + (e.InnerException ?? e).Message; errors.Add(message); output[property.Name] = new { ReadError = message }; }
        }
        return output;
    }

    private object Value(object? value)
    {
        if (value == null) { return "null"; }
        if (value is ObjectId id)
        {
            if (id.IsNull) { return new { Handle = "0", Name = "" }; }
            var obj = transaction.GetObject(id, OpenMode.ForRead);
            return new { Handle = id.Handle.ToString(), Name = obj is SymbolTableRecord symbol ? symbol.Name : obj.GetType().Name };
        }
        if (value is Color color) { return new { Method = color.ColorMethod.ToString(), Index = color.ColorIndex, color.Red, color.Green, color.Blue }; }
        if (value is Point3d p) { return new { p.X, p.Y, p.Z }; }
        if (value is Vector3d v) { return new { v.X, v.Y, v.Z }; }
        if (value is Scale3d scale) { return new { scale.X, scale.Y, scale.Z }; }
        if (value is AnnotationScale annotation) { return new { annotation.Name, annotation.PaperUnits, annotation.DrawingUnits }; }
        if (value is string || value is bool || value is double || value is int || value is short || value is byte) { return value; }
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
    }
}
