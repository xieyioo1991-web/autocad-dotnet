using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Geometry;
using AutoCADPlugin;
using Box = AutoCADPlugin.SupportLabelLayout.Box;

[assembly: CommandClass(typeof(Detail42Checks))]
public sealed class Detail42Checks
{
    internal static List<Entity> Entities(Database db, Transaction tr) =>
        ((BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead)).Cast<ObjectId>()
        .Select(id => (Entity)tr.GetObject(id, OpenMode.ForRead)).ToList();
    internal static List<Entity> Local(Database db, Transaction tr) => Entities(db, tr)
        .Where(e => Math.Abs(e.GeometricExtents.MinPoint.X) < 40000).ToList();

    [CommandMethod("SD_CHECK42_RESULT")]
    public void Check()
    {
        var root = Environment.GetEnvironmentVariable("SD_CHECK42_ROOT")!;
        var log = new List<string>();
        try
        {
            var db = Application.DocumentManager.MdiActiveDocument.Database;
            using var tr = db.TransactionManager.StartTransaction();
            var entities = Local(db, tr);
            var dims = entities.OfType<RotatedDimension>().ToList();
            Require(dims.Count > 0, "actual command generated dimensions", log);
            var axes = entities.OfType<Line>().Where(e => DetailAnnotationIdentity.Role(e) == "Axis").ToList();
            Require(axes.Count == 2, "architectural selection inherited two axes", log);
            Require(axes.Any(a => Math.Abs(a.StartPoint.Y - 680) < .1 && Math.Abs(a.EndPoint.Y - 680) < .1) &&
                axes.Any(a => Math.Abs(a.StartPoint.X + 480) < .1 && Math.Abs(a.EndPoint.X + 480) < .1), "architectural axis positions scaled exactly four", log);
            Require(entities.OfType<DBText>().Single(t => DetailAnnotationIdentity.Role(t) == "Elevation").TextString == "10.200", "architectural elevation value 10.200 preserved", log);
            var barCount = entities.Count(e => e.Layer == Standards.ReinforcementLayer);
            var pointCount = entities.Count(e => e.Layer == Standards.PointReinforcementLayer);
            Require(barCount == 4 && pointCount == 26, "four bars and 26 dots unchanged from 41", log);
            Require(entities.OfType<DBText>().Count(t => t.TextString == AnnotationLayout.RebarText) == barCount, "one specification leader per complete bar", log);
            Require(entities.OfType<DBText>().Count(t => t.TextString == "楼层梁" || t.TextString == "楼层板") == 2, "support labels only generated once", log);
            Require(entities.OfType<DBText>().Count(t => t.TextString == "檐口大样图") == 1, "one native title", log);
            ValidateDimensions(dims, log);
            ValidateText(entities, log);
            var geometry = Signature(entities.Where(e => OutlineRole.Get(e) != "" || e.Layer == Standards.ReinforcementLayer || e.Layer == Standards.PointReinforcementLayer));
            var dimensionLines = DimSignature(dims);
            File.WriteAllLines(Path.Combine(root, "geometry-signature.txt"), entities
                .Where(e => OutlineRole.Get(e) != "" || e.Layer == Standards.ReinforcementLayer || e.Layer == Standards.PointReinforcementLayer || DetailAnnotationIdentity.Role(e) == "Axis" || DetailAnnotationIdentity.Role(e) == "Elevation")
                .Select(e => e.GetType().Name + ":" + e.Layer + ":" + P(e.GeometricExtents.MinPoint) + ":" + P(e.GeometricExtents.MaxPoint)).OrderBy(s => s));
            var initialCount = entities.Count;
            DetailAnnotation.Write(db, tr, entities);
            var after = Local(db, tr);
            Require(initialCount == after.Count, "repeat dimension command replaces annotations without duplicates", log);
            Require(geometry == Signature(after.Where(e => OutlineRole.Get(e) != "" || e.Layer == Standards.ReinforcementLayer || e.Layer == Standards.PointReinforcementLayer)), "repeat preserves geometry", log);
            Require(dimensionLines == DimSignature(after.OfType<RotatedDimension>()), "repeat preserves all dimension line positions and measuring points", log);
            tr.Commit();
            using var exportTr = db.TransactionManager.StartTransaction();
            using var ids = new ObjectIdCollection(Local(db, exportTr).Select(e => e.ObjectId).ToArray());
            using var exported = db.Wblock(ids, Point3d.Origin);
            exported.SaveAs(Path.Combine(root, "detail42.dwg"), DwgVersion.Current);
            log.Add("PASS");
        }
        catch (System.Exception error) { log.Add("FAIL " + error); }
        File.WriteAllLines(Path.Combine(root, "result-checks.txt"), log);
    }

    internal static void ValidateDimensions(List<RotatedDimension> dims, List<string> log)
    {
        foreach (var direction in new[] { true, false })
        {
            var main = dims.Where(d => DetailAnnotationIdentity.Role(d) == "MainDimension" && (Math.Abs(d.Rotation) < .1) == direction).ToList();
            var coordinates = main.Select(d => direction ? d.DimLinePoint.Y : d.DimLinePoint.X).ToList();
            Require(coordinates.Count > 1 && coordinates.Max() - coordinates.Min() < .1, direction ? "all main horizontal dimensions share one line" : "all main vertical dimensions share one line", log);
        }
        foreach (var dim in dims)
        {
            Require(dim.Dimlfac == .25 && dim.Dimscale == 1 && dim.Dimtxt == 300 && dim.Dimtmove == 2, "reference native dimension style", log);
            var vector = dim.XLine2Point - dim.XLine1Point;
            var length = Math.Abs(vector.X * Math.Cos(dim.Rotation) + vector.Y * Math.Sin(dim.Rotation));
            Require(Math.Abs(dim.Measurement - length / 4) < .1, "dimension value follows actual contour", log);
        }
    }

    internal static void ValidateText(List<Entity> entities, List<string> log)
    {
        var dimensions = new AnnotationInk();
        foreach (var e in entities.OfType<Dimension>()) { dimensions.Add(e); }
        var text = entities.OfType<DBText>().Select(AnnotationInk.Bounds).ToList();
        var all = text.Concat(dimensions.Text).ToList();
        for (var i = 0; i < all.Count; i++)
        for (var j = i + 1; j < all.Count; j++)
        { Require(!all[i].Overlaps(all[j]), $"text separation {i}/{j}", null); }
        Require(!text.Any(t => dimensions.Lines.Any(e => SupportLabelLayout.Hits(e, t))), "fixed dimension lines do not cross other text", log);
        Require(!dimensions.Text.Any(t => dimensions.Lines.Any(e => SupportLabelLayout.Hits(e, t))), "dimension glyphs do not overlap dimension lines", log);
        log.Add("OK all native text extents separated");
    }
    private static string P(Point3d p) => $"{p.X:F1},{p.Y:F1},{p.Z:F1}";
    internal static string DimSignature(IEnumerable<RotatedDimension> dims) => string.Join("|", dims.Select(d => $"{d.Rotation:F3}:" + P(d.DimLinePoint) + ":" + P(d.XLine1Point) + ":" + P(d.XLine2Point)).OrderBy(s => s));
    internal static string Signature(IEnumerable<Entity> entities) => string.Join("|", entities.OrderBy(e => e.Handle.Value).Select(e => e.Handle + ":" + e.GeometricExtents));
    internal static void Require(bool condition, string name, List<string>? log)
    { if (!condition) { throw new InvalidOperationException(name); } log?.Add("OK " + name); }
}
