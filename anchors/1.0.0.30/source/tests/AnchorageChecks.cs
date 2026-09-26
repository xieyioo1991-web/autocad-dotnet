using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AutoCADPlugin;

internal static class AnchorageChecks
{
    public static void Run(Action<string, Action> check)
    {
        check("anchorage: 1200 measured from support face, not old end", () =>
        {
            var end = First(Rect(0, -1000, 2000, 3000));
            Require(end.Kind == RebarAnchorage.AnchorKind.Straight, "straight");
            Point(end.Extension[0], 0, 0);
            Point(end.Extension[1], 1200, 0);
            Near(end.Original.GetDistanceTo(end.Extension[1]), 1250, "50 bridge plus 1200");
        });
        check("anchorage: exact 400 remainder bends, above 400 stays straight", () =>
        {
            Require(First(Rect(0, -1000, 1600, 3000)).Kind == RebarAnchorage.AnchorKind.Bent, "400 must bend");
            Require(First(Rect(0, -1000, 1600.01, 3000)).Kind == RebarAnchorage.AnchorKind.Straight, "400.01 straight");
            Require(First(Rect(0, -1000, 1599.99, 3000)).Kind == RebarAnchorage.AnchorKind.Bent, "399.99 bends");
        });
        check("anchorage: trial straight outside support becomes 800+400 bend", () =>
        {
            var end = First(Rect(0, -1000, 960, 3000));
            Require(end.Kind == RebarAnchorage.AnchorKind.Bent, "bent");
            Point(end.Extension[0], 0, 0);
            Point(end.Extension[1], 800, 0);
            Point(end.Extension[2], 800, -400);
            Near((end.Extension[1] - end.Extension[0]).DotProduct(end.Extension[2] - end.Extension[1]), 0, "90 degrees");
        });
        check("anchorage: choose lower side when it has more room", () =>
        {
            var end = First(Rect(0, -2000, 960, 2100));
            Point(end.Extension[2], 800, -400);
        });
        check("anchorage: tie bends down", () =>
        {
            var end = First(Rect(0, -1000, 960, 2000));
            Point(end.Extension[2], 800, -400);
        });
        check("anchorage: prefer down even when upper space is larger; exactly400 fits", () =>
        {
            Point(First(Rect(0, -400, 960, 3400)).Extension[2], 800, -400);
        });
        check("anchorage: down399.99 falls back upwards", () =>
        {
            Point(First(Rect(0, -399.99, 960, 3400)).Extension[2], 800, 400);
        });
        check("anchorage: 800 cannot fit, preserve failed end without extension", () =>
        {
            var end = First(Rect(0, -1000, 799, 3000));
            Require(end.Kind == RebarAnchorage.AnchorKind.Failed && end.Extension.Count == 0, "failed lead");
        });
        check("anchorage: 400 bend cannot fit on either side", () =>
        {
            var end = First(Rect(0, -300, 960, 600));
            Require(end.Kind == RebarAnchorage.AnchorKind.Failed && end.Extension.Count == 0, "failed leg");
        });
        check("anchorage: one failed end preserves old endpoint, other end still anchors", () =>
        {
            var support = new List<Point2d> { new Point2d(0, -1500), new Point2d(960, -1500),
                new Point2d(960, -150), new Point2d(500, -150), new Point2d(500, 1000), new Point2d(0, 1000) };
            var result = RebarAnchorage.Apply(new[] { TestPath() }, new[] { support });
            Require(result.Ends.Single(end => end.AtStart).Kind == RebarAnchorage.AnchorKind.Failed, "start fails");
            Require(result.Ends.Single(end => !end.AtStart).Kind == RebarAnchorage.AnchorKind.Bent, "end bends");
            Point(result.Bars[0].Points[0], -50, 0);
            Point(result.Bars[0].Points.Last(), 800, -700);
        });
        check("anchorage: bends do not run along opposite boundary", () =>
        {
            Require(First(Rect(0, -1000, 800, 3000)).Kind == RebarAnchorage.AnchorKind.Failed, "elbow on boundary");
        });
        check("anchorage: do not jump to distant lobe of concave support", () =>
        {
            var support = new List<Point2d> { new Point2d(0, -1000), new Point2d(2500, -1000),
                new Point2d(2500, 1000), new Point2d(1500, 1000), new Point2d(1500, -500),
                new Point2d(1000, -500), new Point2d(1000, 1000), new Point2d(0, 1000) };
            var end = First(support);
            Require(end.Kind == RebarAnchorage.AnchorKind.Bent, "first exit governs");
            Point(end.Extension[1], 800, 0);
        });
        check("anchorage: bend tip inside but middle crosses void is rejected", () =>
        {
            var support = new List<Point2d> { new Point2d(0, -100), new Point2d(1600, -100),
                new Point2d(1600, 200), new Point2d(500, 200), new Point2d(500, 350),
                new Point2d(1600, 350), new Point2d(1600, 1000), new Point2d(0, 1000) };
            Require(First(support).Kind == RebarAnchorage.AnchorKind.Failed, "cannot cross concave gap");
        });
        check("anchorage: unrelated second support cannot supply missing depth", () =>
        {
            var path = TestPath();
            var result = RebarAnchorage.Apply(new[] { path }, new[] { Rect(0, -1000, 500, 3000), Rect(500, -1000, 2500, 3000) });
            Require(result.Ends[0].Kind == RebarAnchorage.AnchorKind.Failed, "stay in own support");
        });
        check("anchorage: closed paths unchanged, no fictitious anchor ends", () =>
        {
            var path = new RebarPath(Rect(0, 0, 1000, 1000), true);
            var result = RebarAnchorage.Apply(new[] { path }, new[] { Rect(2000, 0, 1000, 1000) });
            Require(result.Ends.Count == 0 && result.Bars[0].Points.SequenceEqual(path.Points) && result.Bars[0].IsClosed, "closed unchanged");
        });
        check("anchorage: rotated/mirrored/translated/reversed path", () =>
        {
            for (var variant = 0; variant < 8; variant++)
            {
                var angle = .37 * variant;
                Point2d Transform(Point2d p)
                {
                    var x = variant % 2 == 0 ? p.X : -p.X;
                    return new Point2d(18000000 + x * Math.Cos(angle) - p.Y * Math.Sin(angle),
                        -1100000 + x * Math.Sin(angle) + p.Y * Math.Cos(angle));
                }
                var contact = new SupportContact(0, new ContourGraph.Edge(Transform(new Point2d(0, -1000)), Transform(new Point2d(0, 2000))));
                var vertices = TestPath().Points.Select(Transform).ToList();
                if (variant >= 4) { vertices.Reverse(); }
                var path = new RebarPath(vertices, false, new[] { contact }, new[] { contact });
                var result = RebarAnchorage.Apply(new[] { path }, new[] { Rect(0, -1000, 960, 3000).Select(Transform).ToList() });
                var end = result.Ends.Single(item => item.Original.GetDistanceTo(Transform(new Point2d(-50, 0))) < .01);
                Require(end.Kind == RebarAnchorage.AnchorKind.Bent, "transformed bend");
                var firstTip = Transform(new Point2d(800, 400));
                var secondTip = Transform(new Point2d(800, -400));
                var lowerTip = firstTip.Y < secondTip.Y ? firstTip : secondTip;
                Require(end.Extension[2].GetDistanceTo(lowerTip) < .01, "world-down transformed tip");
            }
        });
        check("anchorage: partial contact parallel end reports failure, no remote attachment", () =>
        {
            var contact = new SupportContact(0, new ContourGraph.Edge(new Point2d(0, 0), new Point2d(0, 1000)));
            var path = new RebarPath(new[] { new Point2d(-50, 300), new Point2d(-50, 0) }, false, new[] { contact }, new[] { contact });
            var result = RebarAnchorage.Apply(new[] { path }, new[] { Rect(0, 0, 2000, 3000) });
            Require(result.Ends.All(end => end.Kind == RebarAnchorage.AnchorKind.Failed), "parallel can't enter");
            Require(result.Bars[0].Points.SequenceEqual(path.Points), "failed ends unchanged");
        });
    }

    public static void VerifyPrecisionExample(OffsetRebar.Plan plan)
    {
        Require(plan.AnchorEnds.Count(end => end.Kind == RebarAnchorage.AnchorKind.Straight) == 2, "roof: two straight anchors");
        Require(plan.AnchorEnds.Count(end => end.Kind == RebarAnchorage.AnchorKind.Bent) == 2, "eave: two bent anchors");
        Require(plan.AnchorEnds.All(end => end.Kind != RebarAnchorage.AnchorKind.Failed), "all four ends anchored");
        var tips = plan.AnchorEnds.Select(end => end.Extension.Last()).Select(p => plan.Origin + new Vector2d(p.X, p.Y)).ToList();
        foreach (var expected in new[] { new Point2d(50, -2040), new Point2d(910, -2040), new Point2d(800, -1290), new Point2d(800, -1670) })
        { Require(tips.Any(tip => tip.GetDistanceTo(expected) < .01), "missing actual-example tip " + expected); }
    }

    public static void VerifyOld25Conflict(Database database, Transaction transaction, OffsetRebar.Plan plan)
    {
        var space = (BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForWrite);
        var ids = new List<ObjectId>();
        foreach (var path in plan.UnanchoredBars)
        {
            using var old = new Polyline { Closed = path.IsClosed, Layer = Standards.ReinforcementLayer, ConstantWidth = 35 };
            for (var i = 0; i < path.Points.Count; i++)
            { old.AddVertexAt(i, plan.Origin + new Vector2d(path.Points[i].X, path.Points[i].Y), 0, 0, 0); }
            ids.Add(space.AppendEntity(old));
            transaction.AddNewlyCreatedDBObject(old, true);
        }
        var rejected = false;
        try { OffsetRebar.Write(database, transaction, plan); }
        catch (InvalidOperationException) { rejected = true; }
        Require(rejected, "old25 conflict must reject");
        foreach (var id in ids) { transaction.GetObject(id, OpenMode.ForWrite).Erase(); }
    }

    private static RebarAnchorage.EndResult First(List<Point2d> polygon) =>
        RebarAnchorage.Apply(new[] { TestPath() }, new[] { polygon }).Ends.Single(end => end.AtStart);

    private static RebarPath TestPath()
    {
        var contact = new SupportContact(0, new ContourGraph.Edge(new Point2d(0, -1000), new Point2d(0, 2000)));
        return new RebarPath(new[] { new Point2d(-50, 0), new Point2d(-200, 0), new Point2d(-200, -300), new Point2d(-50, -300) },
            false, new[] { contact }, new[] { contact });
    }

    private static List<Point2d> Rect(double x, double y, double width, double height) =>
        new List<Point2d> { new Point2d(x, y), new Point2d(x + width, y), new Point2d(x + width, y + height), new Point2d(x, y + height) };
    private static void Point(Point2d point, double x, double y) { Require(point.GetDistanceTo(new Point2d(x, y)) < .01, "point " + point); }
    private static void Near(double actual, double expected, string message) { Require(Math.Abs(actual - expected) < .01, message + ": " + actual); }
    private static void Require(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
}
