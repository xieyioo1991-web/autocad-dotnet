using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AutoCADPlugin;

internal static class ArchitecturalGuides
{
    public static List<string> Write(Database db, Transaction tr, BlockTableRecord space, IReadOnlyList<Entity> source,
        Point2d sourceBase, Point3d output, string owner)
    {
        var warnings = new List<string>();
        var axes = source.OfType<Line>().Where(l => IsAxisLayer(l.Layer)).ToList();
        var texts = source.OfType<DBText>().Where(t => t.Layer.Equals(Standards.ElevationLayer, StringComparison.OrdinalIgnoreCase)).ToList();
        var symbols = source.OfType<Polyline>().Where(p => p.Layer.Equals(Standards.ElevationLayer, StringComparison.OrdinalIgnoreCase) &&
            p.NumberOfVertices >= 3 && !p.Closed).ToList();
        if (axes.Count == 0) { warnings.Add("未识别建筑轴线（支持DOTE或名称含AXIS的图层），尺寸将按对应支撑边缘定位。"); }
        if (texts.Count == 0) { warnings.Add("框选范围未找到DIM_ELEV标高文字；未虚构标高值。请框选完整建筑大样及标高。"); }
        if (axes.Count == 0 && texts.Count == 0) { return warnings; }
        var reference = DetailReference.Load(db, tr);
        foreach (var line in axes)
        {
            if (Math.Abs(line.StartPoint.X - line.EndPoint.X) > .1 && Math.Abs(line.StartPoint.Y - line.EndPoint.Y) > .1)
            { warnings.Add($"轴线{line.Handle}为斜线，本轮仅处理水平/竖直定位。"); continue; }
            using var axis = (Line)reference.Axis.Clone();
            axis.StartPoint = Transform(line.StartPoint, sourceBase, output); axis.EndPoint = Transform(line.EndPoint, sourceBase, output);
            axis.LinetypeId = AxisLinetype(db, tr); axis.ColorIndex = 253;
            var axisLayer = (LayerTableRecord)tr.GetObject(axis.LayerId, OpenMode.ForWrite);
            axisLayer.LinetypeObjectId = axis.LinetypeId; axisLayer.LineWeight = LineWeight.ByLineWeightDefault;
            // Match the template's effective dash rhythm without modifying LTSCALE.
            axis.LinetypeScale = 1000 / db.Ltscale;
            space.AppendEntity(axis); tr.AddNewlyCreatedDBObject(axis, true);
            DetailAnnotationIdentity.Set(axis, tr, owner, "Axis");
        }
        var used = new HashSet<ObjectId>();
        foreach (var text in texts)
        {
            var symbol = symbols.Where(p => !used.Contains(p.ObjectId)).OrderBy(p => p.GetPoint3dAt(0).DistanceTo(text.Position)).FirstOrDefault();
            if (symbol == null) { throw new InvalidOperationException($"标高文字{text.TextString}未找到可关联的原生三角符号，未输出不确定标高。"); }
            used.Add(symbol.ObjectId);
            var tip = Enumerable.Range(0, symbol.NumberOfVertices).Select(symbol.GetPoint3dAt).OrderBy(p => p.Y).First();
            var targetTip = Transform(tip, sourceBase, output);
            var templateSymbol = reference.Elevation.OfType<Polyline>().Single();
            var templateTip = Enumerable.Range(0, templateSymbol.NumberOfVertices).Select(templateSymbol.GetPoint3dAt).OrderBy(p => p.Y).First();
            var displacement = Matrix3d.Displacement(targetTip - templateTip);
            foreach (var prototype in reference.Elevation)
            {
                using var clone = (Entity)prototype.Clone();
                clone.TransformBy(displacement);
                if (clone is DBText label) { label.TextString = text.TextString; label.AdjustAlignment(db); }
                space.AppendEntity(clone); tr.AddNewlyCreatedDBObject(clone, true);
                DetailAnnotationIdentity.Set(clone, tr, owner, "Elevation");
            }
        }
        return warnings;
    }

    private static bool IsAxisLayer(string name) => name.Equals("DOTE", StringComparison.OrdinalIgnoreCase) || name.IndexOf("AXIS", StringComparison.OrdinalIgnoreCase) >= 0;
    private static ObjectId AxisLinetype(Database db, Transaction tr)
    {
        var table = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
        var pattern = new[] { 1.25, -.25, .25, -.25 };
        foreach (var name in new[] { "CENTER", "SD-AXIS-CENTER" })
        {
            if (table.Has(name))
            {
                var record = (LinetypeTableRecord)tr.GetObject(table[name], OpenMode.ForRead);
                if (record.NumDashes == 4 && Math.Abs(record.PatternLength - 2) < 1e-6 && Enumerable.Range(0, 4).All(i => Math.Abs(record.DashLengthAt(i) - pattern[i]) < 1e-6))
                { return record.ObjectId; }
                continue;
            }
            table.UpgradeOpen();
            using var created = new LinetypeTableRecord { Name = name, AsciiDescription = "实例1轴线", PatternLength = 2, NumDashes = 4 };
            for (var i = 0; i < 4; i++) { created.SetDashLengthAt(i, pattern[i]); }
            var id = table.Add(created); tr.AddNewlyCreatedDBObject(created, true); return id;
        }
        throw new InvalidOperationException("CENTER和SD-AXIS-CENTER均存在不兼容定义，请重命名冲突线型。");
    }
    private static Point3d Transform(Point3d point, Point2d origin, Point3d output) =>
        new Point3d((point.X - origin.X) * 4 + output.X, (point.Y - origin.Y) * 4 + output.Y, 0);
}
