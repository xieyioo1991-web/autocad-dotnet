using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AutoCADPlugin;

internal static class CornerBreakChecks
{
    public static void Run(Action<string, Action> check)
    {
        check("corner: concave L cut continues both directions, hits opposite bars at 300", () =>
        {
            var result = Apply(L(300));
            Require(result.CornerCount == 1 && result.Bars.Count == 1 && !result.Bars[0].IsClosed, "one open loop");
            Tips(result, new Point2d(0, 300), new Point2d(300, 0));
            Require(result.Ends.All(end => end.Hit && Math.Abs(end.Length - 300) < .001), "stop at first opposite bar");
            NoElbow(result, new Point2d(300, 300));
        });
        check("corner: wide L extends full 400, convex corners stay bent", () =>
        {
            var result = Apply(L(600));
            Tips(result, new Point2d(200, 600), new Point2d(600, 200));
            Require(result.Ends.All(end => !end.Hit && Math.Abs(end.Length - 400) < .001), "400 cap");
            Require(result.Bars[0].Points.Any(p => Near(p, new Point2d(1400, 0))), "convex vertex retained");
            NoElbow(result, new Point2d(600, 600));
        });
        check("corner: rectangle has no cuts", () =>
        {
            var result = Apply(Points(0, 0, 1000, 0, 1000, 600, 0, 600));
            Require(result.CornerCount == 0 && result.Bars.Single().IsClosed, "convex rectangle unchanged");
        });
        check("corner: non-right concavity remains connected", () =>
        {
            var ring = Points(0, 0, 1000, 0, 1000, 400, 500, 500, 400, 1000, 0, 1000);
            Require(Apply(ring).CornerCount == 0, "oblique corners must not split");
        });
        check("corner: reversed rotated mirrored large coordinates and vertex zero", () =>
        {
            for (var variant = 0; variant < 8; variant++)
            {
                var angle = variant * .37;
                Point2d Transform(Point2d p)
                {
                    var x = variant % 2 == 0 ? p.X : -p.X;
                    return new Point2d(18000000 + x * Math.Cos(angle) - p.Y * Math.Sin(angle),
                        -1100000 + x * Math.Sin(angle) + p.Y * Math.Cos(angle));
                }
                var ring = L(600).Select(Transform).ToList();
                if (variant >= 4) { ring.Reverse(); }
                var start = variant % ring.Count;
                ring = ring.Skip(start).Concat(ring.Take(start)).ToList();
                var bar = ring.AsEnumerable().Reverse().ToList();
                var result = RebarCornerBreak.Apply(new[] { new RebarPath(bar, true) }, new[] { ring }, 0);
                Require(result.CornerCount == 1, "winding independent");
                Tips(result, Transform(new Point2d(200, 600)), Transform(new Point2d(600, 200)));
                NoElbow(result, Transform(new Point2d(600, 600)));
            }
        });
        check("corner: drawing noise accepted but 89-degree corner not changed", () =>
        {
            var noise = L(600); noise[3] = new Point2d(600, 600.001);
            Require(Apply(noise).CornerCount == 1, "noise");
            var oblique = L(600); oblique[3] = new Point2d(600, 607);
            Require(Apply(oblique).CornerCount == 0, "not a right angle");
        });
        check("corner: two concave cuts split closed U into two open bars", () =>
        {
            var ring = Points(0, 0, 1800, 0, 1800, 1600, 1200, 1600, 1200, 600, 600, 600, 600, 1600, 0, 1600);
            var result = Apply(ring);
            Require(result.CornerCount == 2 && result.Bars.Count == 2 && result.Ends.Count == 4, "both corners");
            Require(result.Bars.All(bar => !bar.IsClosed), "no extra closing edges");
            NoElbow(result, new Point2d(600, 600)); NoElbow(result, new Point2d(1200, 600));
        });
        check("corner: anchored open path splits once, preserves both existing anchor tips", () =>
        {
            var ring = L(600);
            var path = new RebarPath(Points(1300, 600, 1000, 600, 600, 600, 600, 1000, 600, 1300, 800, 1300), false);
            var result = RebarCornerBreak.Apply(new[] { path }, new[] { ring }, 0);
            Require(result.CornerCount == 1 && result.Bars.Count == 2, "only material corner");
            Require(result.Bars[0].Points[0] == path.Points[0] && result.Bars[1].Points.Last() == path.Points.Last(), "anchor tips unchanged");
            Require(result.Bars[1].Points.Contains(new Point2d(600, 1300)), "anchor elbow remains");
        });
        check("collision: nearest bar wins, exact 400 hits, beyond 400 ignored", () =>
        {
            var end = End(0, 0, 1, 0);
            RebarExtensionCollision.Trim(new[] { end }, new[] { Bar(250, -100, 250, 100), Bar(100, -100, 100, 100) });
            Require(Math.Abs(end.Length - 100) < .001 && end.Hit, "nearest");
            var exact = End(0, 0, 1, 0); var beyond = End(0, 10, 1, 0);
            RebarExtensionCollision.Trim(new[] { exact, beyond }, new[] { Bar(400, -1, 400, 1), Bar(400.01, 9, 400.01, 11) });
            Require(exact.Hit && exact.Length == 400 && !beyond.Hit && beyond.Length == 400, "400 limit");
        });
        check("collision: endpoints and collinear segments stop, behind and parallel do not", () =>
        {
            var a = End(0, 0, 1, 0); var b = End(0, 10, 1, 0); var c = End(0, 20, 1, 0);
            RebarExtensionCollision.Trim(new[] { a, b, c }, new[] { Bar(100, 0, 100, -50), Bar(150, 10, 200, 10), Bar(-50, 20, -10, 20), Bar(0, 21, 400, 21) });
            Require(a.Length == 100 && b.Length == 150 && c.Length == 400 && !c.Hit, "finite centerline intersections");
        });
        check("collision: crossing new extensions stop later arrival, independent of order", () =>
        {
            for (var reverse = 0; reverse < 2; reverse++)
            {
                var a = End(0, 0, 1, 0, 10); var b = End(200, -100, 0, 1, 11);
                var ends = reverse == 0 ? new[] { a, b } : new[] { b, a };
                RebarExtensionCollision.Trim(ends, Array.Empty<RebarPath>());
                Require(a.Length == 200 && b.Length == 400, "later arrival stops at existing extension");
            }
        });
        check("collision: equal-distance crossing and head-on meeting stop both", () =>
        {
            var a = End(0, 0, 1, 0, 10); var b = End(100, -100, 0, 1, 11);
            RebarExtensionCollision.Trim(new[] { a, b }, Array.Empty<RebarPath>());
            Require(a.Length == 100 && b.Length == 100, "equal crossing");
            a = End(0, 0, 1, 0, 10); b = End(600, 0, -1, 0, 11);
            RebarExtensionCollision.Trim(new[] { a, b }, Array.Empty<RebarPath>());
            Require(a.Length == 300 && b.Length == 300, "head-on midpoint");
        });
        check("collision: trimmed-away proposal cannot stop another extension", () =>
        {
            var a = End(0, 0, 1, 0, 10); var b = End(200, -100, 0, 1, 11);
            var c = End(190, -50, 1, 0, 12);
            RebarExtensionCollision.Trim(new[] { a, b, c }, Array.Empty<RebarPath>());
            Require(a.Length == 400 && b.Length == 50, "no phantom intersection at (200,0)");
        });
        check("collision: head-on meeting recomputed after one end stops early", () =>
        {
            var a = End(0, 0, 1, 0, 10); var b = End(350, 0, -1, 0, 11);
            var c = End(50, -10, 0, 1, 12);
            RebarExtensionCollision.Trim(new[] { a, b, c }, Array.Empty<RebarPath>());
            Require(a.Length == 50 && b.Length == 300, "stationary tip replaces old midpoint");
        });
        check("corner: native inset L breaks at inset coordinate and reaches opposite bar", () =>
        {
            var entities = ContourGraph.Edges(L(400)).Select(edge => (Entity)new Line(
                new Point3d(edge.Start.X, edge.Start.Y, 0), new Point3d(edge.End.X, edge.End.Y, 0)) { Layer = Standards.OtherThinLayer }).ToList();
            try
            {
                var plan = OffsetRebar.Create(entities);
                Require(plan.CornerBreaks.CornerCount == 1 && plan.Bars.Count == 1 && !plan.Bars[0].IsClosed, "native inset cut");
                var tips = plan.CornerBreaks.Ends.Select(end => plan.Origin + new Vector2d(end.Tip.X, end.Tip.Y)).ToList();
                Require(tips.Any(p => Near(p, new Point2d(50, 350))) && tips.Any(p => Near(p, new Point2d(350, 50))), "expected world tips");
            }
            finally { foreach (var entity in entities) { entity.Dispose(); } }
        });
    }

    public static void VerifyOld26Conflict(Database database, Transaction transaction, OffsetRebar.Plan plan)
    {
        var space = (BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForWrite);
        var ids = new List<ObjectId>();
        foreach (var path in plan.UnbrokenBars)
        {
            using var old = new Polyline { Closed = path.IsClosed, Layer = Standards.ReinforcementLayer, ConstantWidth = 35 };
            for (var i = 0; i < path.Points.Count; i++) { old.AddVertexAt(i, plan.Origin + new Vector2d(path.Points[i].X, path.Points[i].Y), 0, 0, 0); }
            ids.Add(space.AppendEntity(old)); transaction.AddNewlyCreatedDBObject(old, true);
        }
        var rejected = false;
        try { OffsetRebar.Write(database, transaction, plan); } catch (InvalidOperationException) { rejected = true; }
        Require(rejected, "old26 must not overlap new results");
        foreach (var id in ids) { transaction.GetObject(id, OpenMode.ForWrite).Erase(); }
    }

    private static void NoElbow(RebarCornerBreak.Result result, Point2d corner)
    {
        foreach (var bar in result.Bars)
        {
            for (var i = 1; i < bar.Points.Count - 1; i++)
            {
                if (!Near(bar.Points[i], corner)) { continue; }
                var a = (bar.Points[i] - bar.Points[i - 1]).GetNormal(); var b = (bar.Points[i + 1] - bar.Points[i]).GetNormal();
                Require(a.DotProduct(b) > .99999, "cut must be straight-through, not joined as an elbow");
            }
        }
    }
    private static void Tips(RebarCornerBreak.Result result, params Point2d[] expected)
    { foreach (var point in expected) { Require(result.Ends.Any(end => Near(end.Tip, point)), "missing tip " + point); } }
    private static bool Near(Point2d a, Point2d b) => a.GetDistanceTo(b) < .001;
    private static RebarCornerBreak.Result Apply(List<Point2d> ring) => RebarCornerBreak.Apply(new[] { new RebarPath(ring, true) }, new[] { ring }, 0);
    private static List<Point2d> L(double thickness) => Points(0, 0, 1400, 0, 1400, thickness, thickness, thickness, thickness, 1400, 0, 1400);
    private static RebarPath Bar(params double[] coordinates) => new RebarPath(Points(coordinates), false);
    private static RebarExtensionCollision.Extension End(double x, double y, double dx, double dy, int id = 100) =>
        new RebarExtensionCollision.Extension(new Point2d(x, y), new Vector2d(dx, dy), 400, id, 0);
    private static List<Point2d> Points(params double[] coordinates)
    {
        var result = new List<Point2d>();
        for (var i = 0; i < coordinates.Length; i += 2) { result.Add(new Point2d(coordinates[i], coordinates[i + 1])); }
        return result;
    }
    private static void Require(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
}
