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

[assembly: CommandClass(typeof(Detail41Checks))]
public sealed class Detail41Checks
{
    private static List<Entity> Local(Database db, Transaction tr) =>
        ((BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead)).Cast<ObjectId>()
        .Select(id => (Entity)tr.GetObject(id, OpenMode.ForRead)).Where(e => Math.Abs(e.GeometricExtents.MinPoint.X) < 40000).ToList();

    [CommandMethod("SD_CHECK41_DETAIL")]
    public void Run()
    {
        var root = Environment.GetEnvironmentVariable("SD_CHECK41_ROOT")!;
        var log = new List<string>();
        DetailDimensions.Trace = log.Add;
        try
        {
            var db = Application.DocumentManager.MdiActiveDocument.Database;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var selected = Local(db, tr);
                Require(selected.Count(e => DetailAnnotationIdentity.Role(e) == "Axis") == 2, "inherit horizontal and vertical architectural axes", log);
                Require(selected.OfType<DBText>().Single(t => DetailAnnotationIdentity.Role(t) == "Elevation").TextString == "10.200", "architectural elevation value preserved", log);
                var axes = selected.OfType<Line>().Where(e => DetailAnnotationIdentity.Role(e) == "Axis").ToList();
                Require(axes.Any(a => Math.Abs(a.StartPoint.X + 480) < .1 && Math.Abs(a.EndPoint.X + 480) < .1) &&
                    axes.Any(a => Math.Abs(a.StartPoint.Y - 680) < .1 && Math.Abs(a.EndPoint.Y - 680) < .1), "architectural axes transformed by four at exact datum coordinates", log);
                Require(selected.OfType<Polyline>().Where(p => DetailAnnotationIdentity.Role(p) == "Elevation")
                    .SelectMany(p => Enumerable.Range(0,p.NumberOfVertices).Select(p.GetPoint2dAt)).Min(p=>p.Y) > 679.9, "elevation datum preserved", log);
                var context = OffsetRebar.Create(selected); OffsetRebar.Write(db, tr, context);
                var points = DistributedPointRebar.Create(context, context.Bars, CornerPointRebar.Create(context, context.Bars));
                PointRebarWriter.Write(db, tr, points.Centers.Select(p => context.Origin + new Vector2d(p.X, p.Y)).ToList());
                log.Add($"bars={context.Bars.Count}; points={points.Centers.Count}"); tr.Commit();
            }
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var result = DetailAnnotation.Write(db, tr, Local(db, tr));
                Require(result.Dimensions > 0, "dimensions generated", log);
                log.Add($"dimensions={result.Dimensions}; local={result.LocalDimensions}; bar labels={result.RebarLabels}; support labels={result.SupportLabels}");
                tr.Commit();
            }
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var entities = Local(db, tr);
                var dims = entities.OfType<RotatedDimension>().ToList();
                foreach (var dim in dims)
                {
                    Require(dim.Dimlfac == .25 && dim.Dimscale == 1 && dim.Dimtxt == 300, "actual dimension style", log);
                    var vector = dim.XLine2Point - dim.XLine1Point;
                    var length = Math.Abs(vector.X * Math.Cos(dim.Rotation) + vector.Y * Math.Sin(dim.Rotation));
                    Require(Math.Abs(dim.Measurement - length * .25) < .1, "dimension measurement is quarter of contour geometry", log);
                    log.Add($"DIM {DetailAnnotationIdentity.Role(dim)} {dim.Rotation:F3} {dim.Measurement:F3} {dim.XLine1Point} {dim.XLine2Point} at {dim.DimLinePoint}");
                }
                ValidateNoTextOverlaps(entities, log);
                var hash = GeometrySignature(entities);
                var count = entities.Count;
                var again = DetailAnnotation.Write(db, tr, entities);
                var after = Local(db, tr);
                Require(count == after.Count, "repeat update has stable entity count", log);
                Require(hash == GeometrySignature(after), "repeat update preserves rebar, points and contour", log);
                tr.Commit();
            }
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var local = Local(db, tr);
                using var ids = new ObjectIdCollection(local.Select(e => e.ObjectId).ToArray());
                using var output = db.Wblock(ids, Point3d.Origin);
                output.SaveAs(Path.Combine(root, "detail41.dwg"), DwgVersion.Current);
            }
            log.Add("PASS");
        }
        catch (System.Exception e) { log.Add(e.ToString()); }
        DetailDimensions.Trace = null;
        File.WriteAllLines(Path.Combine(root, "detail-checks.txt"), log);
    }

    private static string GeometrySignature(List<Entity> entities) => string.Join("|", entities
        .Where(e => e.Layer == Standards.ReinforcementLayer || e.Layer == Standards.PointReinforcementLayer || OutlineRole.Get(e) != "")
        .OrderBy(e => e.Handle.Value).Select(e => e.Handle + ":" + e.GeometricExtents));

    private static void ValidateNoTextOverlaps(List<Entity> entities, List<string> log)
    {
        var text = new List<Box>();
        var dimensionInk = new AnnotationInk();
        foreach (var entity in entities)
        {
            if (entity is RotatedDimension) { dimensionInk.Add(entity); continue; }
            if (entity is DBText) { text.Add(AnnotationInk.Bounds(entity)); }
        }
        var all = text.Concat(dimensionInk.Text).ToList();
        for (var i = 0; i < all.Count; i++)
        for (var j = i + 1; j < all.Count; j++)
        { if (all[i].Overlaps(all[j])) { throw new InvalidOperationException($"text overlap {i}/{j}"); } }
        Require(!text.Any(t => dimensionInk.Lines.Any(e => SupportLabelLayout.Hits(e, t))), "dimension lines avoid all text", log);
        log.Add("OK all native text extents non-overlapping");
    }
    private static void Require(bool condition, string label, List<string> log)
    { if (!condition) { throw new InvalidOperationException(label); } log.Add("OK " + label); }
}
