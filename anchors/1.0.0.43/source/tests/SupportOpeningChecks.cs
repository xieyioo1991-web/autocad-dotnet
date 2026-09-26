using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AutoCADPlugin;

internal static class SupportOpeningChecks
{
    public static void Run(Action<string, Action> check, string root)
    {
        check("no supports: closed offset retained", () =>
        {
            var plan = Make(new[] { Rect(0, 0, 1000, 600) }, Array.Empty<List<Point2d>>());
            Require(plan.Bars.Count == 1 && plan.Bars[0].IsClosed, "closed");
            Near(Length(plan.Bars[0]), 2800, "perimeter");
        });
        check("sub-tolerance vertex drift: side support still opens", () =>
        {
            for (var variant = 0; variant < 6; variant++)
            {
                var angle = variant * .43;
                Point2d Transform(Point2d p)
                {
                    var x = variant % 2 == 0 ? p.X : -p.X;
                    return new Point2d(18000000 + x * Math.Cos(angle) - p.Y * Math.Sin(angle),
                        -1100000 + x * Math.Sin(angle) + p.Y * Math.Cos(angle));
                }
                var contour = Rect(0, 0, 1000, 600);
                contour[2] = new Point2d(999.999, 600);
                var plan = Make(new[] { contour.Select(Transform).ToList() },
                    new[] { Rect(1000, 0, 500, 1600).Select(Transform).ToList() });
                Require(plan.Bars.Count == 1 && !plan.Bars[0].IsClosed, "side cap left closed for 0.001 coordinate drift");
                Endpoints(plan, Transform(new Point2d(950, 50)), Transform(new Point2d(950, 550)));
            }
        });
        check("end support: cap removed, endpoints unchanged", () =>
        {
            var plan = Make(new[] { Rect(0, 0, 1000, 600) }, new[] { Rect(800, 0, 200, 600) });
            OpenPaths(plan, 1, 1900);
            Endpoints(plan, new Point2d(750, 50), new Point2d(750, 550));
        });
        check("supports at both ends: two separate straight bars", () =>
        {
            var plan = Make(new[] { Rect(0, 0, 1000, 600) }, new[] { Rect(0, 0, 200, 600), Rect(800, 0, 200, 600) });
            OpenPaths(plan, 2, 1000);
            Endpoints(plan, new Point2d(250, 50), new Point2d(750, 50), new Point2d(250, 550), new Point2d(750, 550));
        });
        check("partial support contact: remove only shared span", () =>
        {
            var plan = Make(new[] { Rect(0, 0, 1000, 1000) }, new[] { Rect(1000, 300, 200, 400) });
            OpenPaths(plan, 1, 3200);
            Endpoints(plan, new Point2d(950, 300), new Point2d(950, 700));
        });
        check("two separated contacts on one side: no connecting segment", () =>
        {
            var plan = Make(new[] { Rect(0, 0, 1000, 1000) }, new[] { Rect(1000, 200, 200, 100), Rect(1000, 600, 200, 200) });
            OpenPaths(plan, 2, 3300);
            Endpoints(plan, new Point2d(950, 200), new Point2d(950, 300), new Point2d(950, 600), new Point2d(950, 800));
        });
        check("nearby support and point contact do not open rebar", () =>
        {
            foreach (var support in new[] { Rect(1010, 0, 200, 1000), Rect(1000.2, 0, 200, 1000), Rect(1000, 1000, 200, 200) })
            {
                var plan = Make(new[] { Rect(0, 0, 1000, 1000) }, new[] { support });
                Require(plan.Bars.Count == 1 && plan.Bars[0].IsClosed, "spurious opening");
                Near(Length(plan.Bars[0]), 3600, "perimeter");
            }
        });
        check("rotated, mirrored and translated partial contact", () =>
        {
            for (var variant = 0; variant < 6; variant++)
            {
                var angle = .37 * variant;
                Point2d Transform(Point2d p)
                {
                    var x = variant % 2 == 0 ? p.X : -p.X;
                    return new Point2d(18000000 + x * Math.Cos(angle) - p.Y * Math.Sin(angle),
                        -1100000 + x * Math.Sin(angle) + p.Y * Math.Cos(angle));
                }
                var plan = Make(new[] { Rect(0, 0, 1000, 1000).Select(Transform).ToList() },
                    new[] { Rect(1000, 300, 200, 400).Select(Transform).ToList() });
                OpenPaths(plan, 1, 3200);
                Endpoints(plan, Transform(new Point2d(950, 300)), Transform(new Point2d(950, 700)));
            }
        });
        check("sloped shared edge: only its offset cap removed", () =>
        {
            var triangle = new List<Point2d> { new Point2d(0, 0), new Point2d(1000, 0), new Point2d(0, 1000) };
            var support = new List<Point2d> { new Point2d(1000, 0), new Point2d(1200, 200), new Point2d(200, 1200), new Point2d(0, 1000) };
            var plan = Make(new[] { triangle }, new[] { support });
            var end = 950 - 50 * Math.Sqrt(2);
            OpenPaths(plan, 1, 2 * (end - 50));
            Endpoints(plan, new Point2d(end, 50), new Point2d(50, end));
        });
        check("screenshot-style eave and roof: two open polylines, native DWG, duplicate protection", () =>
        { SaveExample(root); });
    }

    private static void SaveExample(string root)
    {
        var contours = new[]
        {
            new List<Point2d> { new Point2d(-3200, 0), new Point2d(-3200, 900), new Point2d(-2700, 900),
                new Point2d(-1200, 0), new Point2d(0, 0), new Point2d(0, -500), new Point2d(-1800, -500),
                new Point2d(-1800, -150), new Point2d(-3070, -150), new Point2d(-3070, 0) },
            new List<Point2d> { new Point2d(0, 0), new Point2d(1000, 0), new Point2d(1000, 800),
                new Point2d(4400, 2300), new Point2d(4200, 2750), new Point2d(0, 900) }
        };
        var supports = new[] { Rect(0, -2500, 1000, 2500), Rect(1000, -750, 3200, 500) };
        var plan = Make(contours, supports);
        Require(plan.Bars.Count == 2 && plan.Bars.All(bar => !bar.IsClosed), "two open paths");
        Endpoints(plan, new Point2d(-50, -50), new Point2d(-50, -450), new Point2d(50, 50), new Point2d(950, 50));
        using var output = new Database(true, true);
        var previous = HostApplicationServices.WorkingDatabase;
        HostApplicationServices.WorkingDatabase = output;
        try
        {
            using (var transaction = output.TransactionManager.StartTransaction())
            {
                Standards.Ensure(output, transaction);
                var space = (BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(output), OpenMode.ForWrite);
                foreach (var edge in contours.SelectMany(ContourGraph.Edges))
                { DetailWriter.WriteOutlineSegment(space, transaction, edge.Start, edge.End, Standards.OtherThinLayer); }
                var supportEntities = new List<Polyline>();
                foreach (var support in supports)
                {
                    var polyline = Poly(support);
                    DetailWriter.WriteControlBoundary(space, transaction, polyline);
                    supportEntities.Add(polyline);
                }
                DetailWriter.WriteSupportAnnotations(space, transaction, supportEntities, "楼层梁");
                var oldBars = new List<ObjectId>();
                foreach (var loop in plan.OffsetLoops)
                {
                    using var old = Poly(loop.Select(p => plan.Origin + new Vector2d(p.X, p.Y)).ToList());
                    old.Layer = Standards.ReinforcementLayer;
                    oldBars.Add(space.AppendEntity(old));
                    transaction.AddNewlyCreatedDBObject(old, true);
                }
                Reject(() => OffsetRebar.Write(output, transaction, plan), "old23 closed bars");
                foreach (var id in oldBars) { transaction.GetObject(id, OpenMode.ForWrite).Erase(); }
                Require(OffsetRebar.Write(output, transaction, plan) == 2, "write count");
                Reject(() => OffsetRebar.Write(output, transaction, plan), "duplicate open bars");
                var bars = space.Cast<ObjectId>().Where(id => !id.IsErased).Select(id => transaction.GetObject(id, OpenMode.ForRead))
                    .OfType<Polyline>().Where(p => p.Layer == Standards.ReinforcementLayer).ToList();
                Require(bars.Count == 2 && bars.All(p => !p.Closed && p.ConstantWidth == 35 && p.ColorIndex == 256), "native open entities");
                File.WriteAllLines(Path.Combine(root, "support-opening-entities.txt"), space.Cast<ObjectId>().Where(id => !id.IsErased)
                    .Select(id => (Entity)transaction.GetObject(id, OpenMode.ForRead)).Select(Describe));
                transaction.Commit();
            }
            output.SaveAs(Path.Combine(root, "support-opening-example.dwg"), DwgVersion.Current);
        }
        finally { HostApplicationServices.WorkingDatabase = previous; }
    }

    private static string Describe(Entity entity)
    {
        if (entity is Line line) { return "L|" + line.StartPoint.X + "," + line.StartPoint.Y + "," + line.EndPoint.X + "," + line.EndPoint.Y; }
        if (entity is Polyline polyline)
        {
            var indices = Enumerable.Range(0, polyline.NumberOfVertices).Concat(polyline.Closed ? new[] { 0 } : Array.Empty<int>());
            return (polyline.Layer == Standards.ReinforcementLayer ? "B|" : "P|") +
                string.Join(",", indices.SelectMany(i => new[] { polyline.GetPoint2dAt(i).X, polyline.GetPoint2dAt(i).Y }));
        }
        return "";
    }

    private static OffsetRebar.Plan Make(IEnumerable<List<Point2d>> contours, IEnumerable<List<Point2d>> supports)
    {
        var entities = new List<Entity>();
        try
        {
            foreach (var edge in contours.SelectMany(ContourGraph.Edges))
            {
                entities.Add(new Line(new Point3d(edge.Start.X, edge.Start.Y, 0), new Point3d(edge.End.X, edge.End.Y, 0))
                { Layer = Standards.OtherThinLayer });
            }
            foreach (var support in supports) { entities.Add(Poly(support)); }
            return OffsetRebar.CreateOutline(entities);
        }
        finally { foreach (var entity in entities) { entity.Dispose(); } }
    }

    private static Polyline Poly(List<Point2d> ring)
    {
        var result = new Polyline { Closed = true, Layer = Standards.RegionLayer };
        for (var i = 0; i < ring.Count; i++) { result.AddVertexAt(i, ring[i], 0, 0, 0); }
        return result;
    }

    private static void OpenPaths(OffsetRebar.Plan plan, int count, double totalLength)
    {
        Require(plan.Bars.Count == count && plan.Bars.All(path => !path.IsClosed), "open path count");
        Near(plan.Bars.Sum(Length), totalLength, "remaining length");
    }

    private static void Endpoints(OffsetRebar.Plan plan, params Point2d[] expected)
    {
        var actual = plan.Bars.SelectMany(path => new[] { path.Points[0], path.Points[path.Points.Count - 1] })
            .Select(p => plan.Origin + new Vector2d(p.X, p.Y)).ToList();
        Require(actual.Count == expected.Length, "endpoint count");
        foreach (var point in expected)
        {
            var index = actual.FindIndex(p => p.GetDistanceTo(point) < .01);
            Require(index >= 0, "missing endpoint " + point);
            actual.RemoveAt(index);
        }
    }

    private static double Length(RebarPath path)
    {
        var length = 0.0;
        for (var i = 1; i < path.Points.Count; i++) { length += path.Points[i - 1].GetDistanceTo(path.Points[i]); }
        if (path.IsClosed) { length += path.Points[path.Points.Count - 1].GetDistanceTo(path.Points[0]); }
        return length;
    }

    private static List<Point2d> Rect(double x, double y, double width, double height) =>
        new List<Point2d> { new Point2d(x, y), new Point2d(x + width, y), new Point2d(x + width, y + height), new Point2d(x, y + height) };
    private static void Require(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    private static void Near(double actual, double expected, string message) { Require(Math.Abs(actual - expected) < .01, message + ": " + actual); }
    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("not rejected: " + message);
    }
}
