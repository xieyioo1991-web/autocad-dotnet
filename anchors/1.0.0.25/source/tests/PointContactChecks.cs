using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AutoCADPlugin;

internal static class PointContactChecks
{
    public static void Run(Action<string, Action> check, string root)
    {
        check("two rectangles touching only at a corner", () =>
        {
            CheckVariations(new[] { Rectangle(0, 0, 600, 500), Rectangle(600, 500, 600, 500) },
                Array.Empty<List<Point2d>>(), new[] { 200000.0, 200000.0 });
        });
        check("screenshot topology: ledge and beam cap meet at one corner after removing supports", () =>
        {
            CheckVariations(ScreenshotContours(), ScreenshotSupports(), new[] { 322000.0, 708000.0 });
        });
        check("point contact with support crosses and labels, tagged and legacy drawings", () =>
        {
            SaveDrawing(root, true);
            SaveDrawing(root, false);
        });
    }

    private static void SaveDrawing(string root, bool tagged)
    {
        using var output = new Database(true, true);
        var previous = HostApplicationServices.WorkingDatabase;
        HostApplicationServices.WorkingDatabase = output;
        try
        {
            using (var transaction = output.TransactionManager.StartTransaction())
            {
                Standards.Ensure(output, transaction);
                var space = (BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(output), OpenMode.ForWrite);
                foreach (var edge in ScreenshotContours().SelectMany(ContourGraph.Edges))
                {
                    using var line = new Line(new Point3d(edge.Start.X, edge.Start.Y, 0), new Point3d(edge.End.X, edge.End.Y, 0))
                    { Layer = Standards.OtherThinLayer, Color = Color.FromColorIndex(ColorMethod.ByAci, 7) };
                    space.AppendEntity(line);
                    transaction.AddNewlyCreatedDBObject(line, true);
                    if (tagged) { OutlineRole.Set(line, transaction, "Contour"); }
                }
                var supports = new List<Polyline>();
                foreach (var ring in ScreenshotSupports())
                {
                    var polyline = new Polyline { Closed = true, Layer = Standards.OtherThinLayer };
                    for (var i = 0; i < ring.Count; i++) { polyline.AddVertexAt(i, ring[i], 0, 0, 0); }
                    space.AppendEntity(polyline);
                    transaction.AddNewlyCreatedDBObject(polyline, true);
                    if (tagged) { OutlineRole.Set(polyline, transaction, "Support"); }
                    supports.Add(polyline);
                }
                DetailWriter.WriteSupportAnnotations(space, transaction, supports, "楼层梁");
                var input = space.Cast<ObjectId>().Select(id => (Entity)transaction.GetObject(id, OpenMode.ForRead)).ToList();
                var plan = OffsetRebar.Create(input);
                Require(plan.SupportCount == 2 && plan.Boundaries.Count == 2, "two supports / two regions");
                Require(OffsetRebar.Write(output, transaction, plan) == 2, "two separate bars");
                var bars = space.Cast<ObjectId>().Select(id => (Entity)transaction.GetObject(id, OpenMode.ForRead))
                    .Where(entity => entity.Layer == Standards.ReinforcementLayer).Cast<Polyline>().ToList();
                Require(bars.Count == 2 && bars.All(bar => !bar.Closed && bar.ConstantWidth == 35), "bar attributes");
                var layers = (LayerTable)transaction.GetObject(output.LayerTableId, OpenMode.ForRead);
                var layer = (LayerTableRecord)transaction.GetObject(layers[Standards.ReinforcementLayer], OpenMode.ForRead);
                Require(layer.Color.ColorIndex == 6 && layer.LinetypeObjectId == output.ContinuousLinetype, "magenta continuous layer");
                transaction.Commit();
            }
            output.SaveAs(Path.Combine(root, tagged ? "point-contact-tagged.dwg" : "point-contact-legacy.dwg"), DwgVersion.Current);
        }
        finally { HostApplicationServices.WorkingDatabase = previous; }
    }

    // Schematic reconstruction of the reported topology, not coordinates from the user's DWG.
    internal static List<Point2d>[] ScreenshotContours() => new[]
    {
        new List<Point2d>
        {
            new Point2d(-1900, 0), new Point2d(0, 0), new Point2d(0, 400),
            new Point2d(-1500, 400), new Point2d(-1500, 960), new Point2d(-1900, 960)
        },
        Rectangle(0, -2360, 800, 3320),
        Rectangle(800, -1100, 3000, 400)
    };

    internal static List<Point2d>[] ScreenshotSupports() => new[]
    {
        Rectangle(0, -2360, 800, 2760), Rectangle(800, -1100, 3000, 400)
    };

    private static void CheckVariations(List<Point2d>[] contours, List<Point2d>[] supports, double[] expectedAreas)
    {
        for (var variant = 0; variant < 8; variant++)
        {
            var angle = variant * .41;
            Point2d Transform(Point2d p) => new Point2d(
                18259826 + p.X * Math.Cos(angle) - p.Y * Math.Sin(angle),
                -1150051 + p.X * Math.Sin(angle) + p.Y * Math.Cos(angle));
            var entities = new List<Entity>();
            try
            {
                var rings = variant % 2 == 0 ? contours : contours.Reverse();
                foreach (var ring in rings)
                {
                    for (var i = 0; i < ring.Count; i++)
                    {
                        var a = Transform(ring[(i + variant) % ring.Count]);
                        var b = Transform(ring[(i + variant + 1) % ring.Count]);
                        if (variant % 2 != 0) { (a, b) = (b, a); }
                        entities.Add(new Line(new Point3d(a.X, a.Y, 0), new Point3d(b.X, b.Y, 0))
                        { Layer = Standards.OtherThinLayer });
                    }
                }
                foreach (var support in supports)
                {
                    var polyline = new Polyline { Closed = true, Layer = Standards.RegionLayer };
                    entities.Add(polyline);
                    for (var i = 0; i < support.Count; i++) { polyline.AddVertexAt(i, Transform(support[i]), 0, 0, 0); }
                }
                var plan = OffsetRebar.Create(entities);
                Require(plan.Boundaries.Count == expectedAreas.Length, "boundary count, variant " + variant);
                Require(plan.Boundaries.All(r => r.Distinct().Count() == r.Count), "self-touching boundary");
                Require(plan.Bars.All(bar => bar.IsClosed == (supports.Length == 0)), "open/closed paths after contact removal");
                var areas = plan.OffsetLoops.Select(r => Math.Abs(ContourGraph.Area(r))).OrderBy(a => a).ToArray();
                Require(areas.Length == expectedAreas.Length, "bar count");
                for (var i = 0; i < areas.Length; i++)
                { Require(Math.Abs(areas[i] - expectedAreas[i]) < .1, "inset area, variant " + variant); }
            }
            finally { foreach (var entity in entities) { entity.Dispose(); } }
        }
    }

    private static List<Point2d> Rectangle(double x, double y, double width, double height) =>
        new List<Point2d> { new Point2d(x, y), new Point2d(x + width, y), new Point2d(x + width, y + height), new Point2d(x, y + height) };

    private static void Require(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}
