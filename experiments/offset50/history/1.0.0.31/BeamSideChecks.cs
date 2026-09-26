using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AutoCADPlugin;

internal static class BeamSideChecks
{
    public static void Run(Action<string, Action> check, string root)
    {
        check("beam side: upper forced800+400 despite space for1000, lower straight600", () =>
        {
            var plan = Plan(new[] { Rect(-2000, 0, 2000, 400) }, new[] { Rect(0, -3000, 2400, 6000) });
            VerifyPair(plan, 0, 1, 350, 50);
            Point(World(plan, End(plan, RebarAnchorage.AnchorKind.Bent).Extension.Last()), 800, -50);
            Point(World(plan, End(plan, RebarAnchorage.AnchorKind.LowerStraight).Extension.Last()), 600, 50);
        });
        check("beam side: right face, reversed outline and large coordinates", () =>
        {
            var shift = new Vector2d(18000000, -1100000);
            var material = Rect(960, 0, 2000, 400).Select(p => p + shift).Reverse().ToList();
            var beam = Rect(0, -2000, 960, 4000).Select(p => p + shift).Reverse().ToList();
            var plan = Plan(new[] { material }, new[] { beam });
            Require(plan.BeamSides.PairCount == 1, "mirrored pair");
            Point(World(plan, End(plan, RebarAnchorage.AnchorKind.LowerStraight).Extension.Last()) - shift, 360, 50);
            Point(World(plan, End(plan, RebarAnchorage.AnchorKind.Bent).Extension.Last()) - shift, 160, -50);
        });
        check("beam side: lower600 fits narrow beam, upper800 reports failure", () =>
        {
            var plan = Plan(new[] { Rect(-2000, 0, 2000, 400) }, new[] { Rect(0, -2000, 700, 4000) });
            Require(plan.AnchorEnds.Count(end => end.Kind == RebarAnchorage.AnchorKind.Failed) == 1, "only upper fails");
            Point(World(plan, End(plan, RebarAnchorage.AnchorKind.LowerStraight).Extension.Last()), 600, 50);
        });
        check("beam side: lower599.9 cannot fit600, never bends or shortens", () =>
        {
            var plan = Plan(new[] { Rect(-2000, 0, 2000, 400) }, new[] { Rect(0, -2000, 599.9, 4000) });
            Require(plan.AnchorEnds.All(end => end.Kind == RebarAnchorage.AnchorKind.Failed && end.Extension.Count == 0), "both fail");
            Require(plan.AnchorEnds.Any(end => end.Reason.Contains("600直锚")), "explicit lower failure");
        });
        check("beam side: lower600 exact boundary,599.99 accepted with0.1 precision without overshoot", () =>
        {
            foreach (var width in new[] { 600.0, 599.99 })
            {
                var plan = Plan(new[] { Rect(-2000, 0, 2000, 400) }, new[] { Rect(0, -2000, width, 4000) });
                Point(World(plan, End(plan, RebarAnchorage.AnchorKind.LowerStraight).Extension.Last()), width, 50);
            }
        });
        check("beam side: upper bends upward only when down400 is unavailable", () =>
        {
            var plan = Plan(new[] { Rect(-2000, 0, 2000, 300) }, new[] { Rect(0, -100, 960, 3000) });
            Point(World(plan, End(plan, RebarAnchorage.AnchorKind.Bent).Extension.Last()), 800, 650);
            Point(World(plan, End(plan, RebarAnchorage.AnchorKind.LowerStraight).Extension.Last()), 600, 50);
        });
        check("beam side: two separate regions on same beam pair independently", () =>
        {
            var plan = Plan(new[] { Rect(-1500, 0, 1500, 300), Rect(-1500, 800, 1500, 300) }, new[] { Rect(0, -2000, 960, 4000) });
            Require(plan.BeamSides.PairCount == 2 && plan.AnchorEnds.Count(end => end.Kind == RebarAnchorage.AnchorKind.LowerStraight) == 2, "two independent pairs");
        });
        check("beam side unit: one region with two disjoint contacts pairs finite spans", () =>
        {
            var material = Points(-1500, 0, 0, 0, 0, 400, -400, 400, -400, 1200, 0, 1200, 0, 1600, -1500, 1600);
            var supports = new[] { Rect(0, -2000, 960, 4000) };
            var lowerContact = new SupportContact(0, new ContourGraph.Edge(new Point2d(0, 0), new Point2d(0, 400)));
            var upperContact = new SupportContact(0, new ContourGraph.Edge(new Point2d(0, 1200), new Point2d(0, 1600)));
            // Isolate pairing from raw linework face filling around the support.
            var paths = new[] {
                new RebarPath(Points(-50, 1550, -1450, 1550, -1450, 50, -50, 50), false, new[] { upperContact }, new[] { lowerContact }, 0),
                new RebarPath(Points(-50, 350, -350, 350, -350, 1250, -50, 1250), false, new[] { lowerContact }, new[] { upperContact }, 0) };
            var guides = BeamAnchorGuide.Create(supports, new BeamTopRebar.Result(), 50);
            var rules = BeamSideRebarRules.Apply(paths, new[] { material }, supports, guides, 50);
            Require(rules.PairCount == 2, "do not pair outermost endpoints across gap");
            var anchored = RebarAnchorage.Apply(rules.Bars, supports, guides);
            var lowerY = anchored.Ends.Where(end => end.Kind == RebarAnchorage.AnchorKind.LowerStraight).Select(end => end.Extension.Last().Y).OrderBy(y => y).ToArray();
            Require(lowerY.Length == 2 && Math.Abs(lowerY[0] - 50) < .01 && Math.Abs(lowerY[1] - 1250) < .01, "each interface has own lower");
        });
        check("beam side unit: unmatched horizontal ends from different regions never pair", () =>
        {
            var support = Beam();
            var contact = new SupportContact(0, new ContourGraph.Edge(new Point2d(0, 0), new Point2d(0, 500)));
            var paths = new[] { new RebarPath(Points(-50, 450, -1000, 450), false, new[] { contact }, null, 0),
                new RebarPath(Points(-50, 50, -1000, 50), false, new[] { contact }, null, 1) };
            var guides = BeamAnchorGuide.Create(new[] { support }, new BeamTopRebar.Result(), 50);
            var result = BeamSideRebarRules.Apply(paths, new[] { Rect(-1000, 300, 1000, 200), Rect(-1000, 0, 1000, 200) }, new[] { support }, guides, 50);
            Require(result.PairCount == 0 && result.Bars.All(path => path.StartRule == RebarEndRule.Default), "region ownership preserved");
        });
        check("beam side: support and contour collinear subdivisions preserve a single pair", () =>
        {
            var material = Points(-2000, 0, 0, 0, 0, 100, 0, 200, 0, 300, 0, 400, -2000, 400);
            var support = Points(0, -2000, 960, -2000, 960, 2000, 0, 2000, 0, 250, 0, 150);
            var plan = Plan(new[] { material }, new[] { support });
            Require(plan.BeamSides.PairCount == 1, "continuous interface merged before pairing");
        });
        check("beam side: region between two beams has lower600 at both ends", () =>
        {
            var plan = Plan(new[] { Rect(0, 0, 2000, 400) }, new[] { Rect(-960, -2000, 960, 4000), Rect(2000, -2000, 960, 4000) });
            Require(plan.BeamSides.PairCount == 2 && plan.AnchorEnds.Count(end => end.Kind == RebarAnchorage.AnchorKind.LowerStraight) == 2, "opposite ends assigned independently");
        });
        check("beam side: inclined bars and floor slab do not form horizontal beam pair", () =>
        {
            var inclined = Plan(new[] { Points(-2000, 0, 0, 500, 0, 900, -2000, 400) }, new[] { Rect(0, -2000, 960, 4000) });
            Require(inclined.BeamSides.PairCount == 0, "inclined excluded");
            var slab = Plan(new[] { Rect(-2000, 0, 2000, 400) }, new[] { Rect(0, -100, 4000, 600) });
            Require(slab.BeamSides.PairCount == 0 && slab.AnchorEnds.All(end => end.Kind != RebarAnchorage.AnchorKind.LowerStraight), "floor slab excluded");
        });
        check("small side: both contour dimensions599.9 remove lower, preserve upper bent and free return", () =>
        {
            var plan = Plan(new[] { Rect(-599.9, 0, 599.9, 599.9) }, new[] { Beam() });
            Require(plan.BeamSides.SuppressedRegionCount == 1 && plan.AnchorEnds.Count == 1, "one lower removed, free cut not an anchorage failure");
            Require(End(plan, RebarAnchorage.AnchorKind.Bent) != null, "upper still forced bent");
            Require(!WorldEdges(plan).Any(edge => Math.Abs(edge.Start.Y - 50) < .01 && Math.Abs(edge.End.Y - 50) < .01), "no bottom horizontal line");
            Require(WorldEdges(plan).Any(edge => Math.Abs(edge.Start.X + 549.9) < .01 && Math.Abs(edge.End.X + 549.9) < .01), "free end vertical return retained");
        });
        check("small side: either contour dimension600 retains lower, measure before inset", () =>
        {
            foreach (var size in new[] { (600.0, 500.0), (500.0, 600.0), (600.0, 600.0), (600.1, 500.0) })
            {
                var plan = Plan(new[] { Rect(-size.Item1, 0, size.Item1, size.Item2) }, new[] { Beam() });
                Require(plan.BeamSides.SuppressedRegionCount == 0 && plan.AnchorEnds.Count(end => end.Kind == RebarAnchorage.AnchorKind.LowerStraight) == 1, "600 threshold before50 inset");
            }
        });
        check("small side:599.99 rounds600,599.94 rounds599.9", () =>
        {
            foreach (var width in new[] { 599.99, 600.01, 599.94 })
            {
                var plan = Plan(new[] { Rect(-width, 0, width, 400) }, new[] { Beam() });
                Require(plan.BeamSides.SuppressedRegionCount == (width == 599.94 ? 1 : 0), "decimal tail ignored");
            }
        });
        check("small side: full material bounds govern, narrow local neck is not a small region", () =>
        {
            var material = Points(-1200, 0, 0, 0, 0, 400, -400, 400, -400, 900, -1200, 900);
            Require(Plan(new[] { material }, new[] { Beam() }).BeamSides.SuppressedRegionCount == 0, "whole region not neck");
        });
        check("small side: mirrored/sloped bottom removed by outline, no artificial anchor on cut", () =>
        {
            var material = Points(960, 100, 1460, 0, 1460, 500, 960, 500);
            var plan = Plan(new[] { material }, new[] { Beam() });
            Require(plan.BeamSides.SuppressedRegionCount == 1 && plan.AnchorEnds.Count == 1, "sloped lower removed");
            Require(plan.AnchorEnds.All(end => end.Kind != RebarAnchorage.AnchorKind.Failed), "new free end not failed");
        });
        check("small side: detached region, point contact and floor slab keep bottom bars", () =>
        {
            foreach (var material in new[] { Rect(-1000, 0, 400, 400), Rect(-400, 2000, 400, 400) })
            { Require(Plan(new[] { material }, new[] { Beam() }).BeamSides.SuppressedRegionCount == 0, "no real beam side contact"); }
            var slab = Plan(new[] { Rect(-500, 0, 500, 300) }, new[] { Rect(0, -100, 3000, 800) });
            Require(slab.BeamSides.SuppressedRegionCount == 0, "floor slab not beam");
        });
        check("precision: U999.99 clamps to beam boundary,999.9 fails", () =>
        {
            var plan = Plan(new[] { Rect(0, 0, 960, 800) }, new[] { Rect(0, -999.99, 960, 999.99) });
            Point(World(plan, plan.BeamTops.Bars[0].Points[0]), 50, -999.99);
            var failed = false;
            try { Plan(new[] { Rect(0, 0, 960, 800) }, new[] { Rect(0, -999.9, 960, 999.9) }); }
            catch (InvalidOperationException) { failed = true; }
            Require(failed, "significant shortage rejected");
        });
        check("precision: upper bend down399.99 counts as400 and ends at boundary", () =>
        {
            var plan = Plan(new[] { Rect(-2000, 0, 2000, 300) }, new[] { Rect(0, -149.99, 960, 3000) });
            Point(World(plan, End(plan, RebarAnchorage.AnchorKind.Bent).Extension.Last()), 800, -149.99);
        });
        check("native DWG: paired and small beam-side fixtures, only width35 reinforcement", () =>
        {
            Fixture(root, "paired-beam", Rect(-2000, 0, 2000, 400));
            Fixture(root, "small-side-beam", Rect(-500, 0, 500, 400));
        });
    }

    public static void VerifyExample(OffsetRebar.Plan plan)
    {
        Require(plan.BeamSides.PairCount == 1 && plan.BeamSides.SuppressedRegionCount == 0, "sample eave pair, not a small region");
        Point(World(plan, End(plan, RebarAnchorage.AnchorKind.LowerStraight).Extension.Last()), 600, -1270);
        Point(World(plan, End(plan, RebarAnchorage.AnchorKind.Bent).Extension.Last()), 800, -1290);
    }

    private static void VerifyPair(OffsetRebar.Plan plan, double face, int direction, double topY, double bottomY)
    {
        Require(plan.BeamSides.PairCount == 1 && plan.AnchorEnds.Count == 2, "one pair");
        Point(World(plan, End(plan, RebarAnchorage.AnchorKind.Bent).Extension[0]), face, topY);
        var lower = End(plan, RebarAnchorage.AnchorKind.LowerStraight);
        Point(World(plan, lower.Extension[0]), face, bottomY);
        Point(World(plan, lower.Extension.Last()), face + direction * 600, bottomY);
        Require(lower.Extension.Count == 2, "no lower bend");
    }

    private static void Fixture(string root, string name, List<Point2d> material)
    {
        var beam = Beam(); var plan = Plan(new[] { material }, new[] { beam });
        using var db = new Database(true, true);
        var previous = HostApplicationServices.WorkingDatabase; HostApplicationServices.WorkingDatabase = db;
        try
        {
            using (var tr = db.TransactionManager.StartTransaction())
            {
                Standards.Ensure(db, tr);
                var space = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
                foreach (var edge in ContourGraph.Edges(material))
                { DetailWriter.WriteOutlineSegment(space, tr, edge.Start, edge.End, Standards.OtherThinLayer); }
                var support = Poly(beam); DetailWriter.WriteControlBoundary(space, tr, support);
                DetailWriter.WriteSupportAnnotations(space, tr, new[] { support }, "楼层梁");
                var before = space.Cast<ObjectId>().ToList();
                OffsetRebar.Write(db, tr, plan);
                var added = space.Cast<ObjectId>().Except(before).Select(id => tr.GetObject(id, OpenMode.ForRead)).ToList();
                Require(added.Count == plan.Bars.Count && added.All(obj => obj is Polyline p && p.ConstantWidth == 35 && p.Layer == "S-REIN"), "no helper or outline writes");
                File.WriteAllLines(Path.Combine(root, name + "-entities.txt"), space.Cast<ObjectId>().Select(id => (Entity)tr.GetObject(id, OpenMode.ForRead)).Select(e =>
                    e is Line line ? "L|" + line.StartPoint.X + "," + line.StartPoint.Y + "," + line.EndPoint.X + "," + line.EndPoint.Y :
                    e is Polyline poly ? (poly.Layer == "S-REIN" ? "B|" : "P|") + string.Join(",", Enumerable.Range(0, poly.NumberOfVertices).Concat(poly.Closed ? new[] { 0 } : Array.Empty<int>()).SelectMany(i => new[] { poly.GetPoint2dAt(i).X, poly.GetPoint2dAt(i).Y })) : ""));
                tr.Commit();
            }
            db.SaveAs(Path.Combine(root, name + ".dwg"), DwgVersion.Current);
        }
        finally { HostApplicationServices.WorkingDatabase = previous; }
    }

    private static OffsetRebar.Plan Plan(IEnumerable<List<Point2d>> material, IEnumerable<List<Point2d>> supports)
    {
        var entities = material.SelectMany(ContourGraph.Edges).Select(edge => (Entity)new Line(new Point3d(edge.Start.X, edge.Start.Y, 0), new Point3d(edge.End.X, edge.End.Y, 0)) { Layer = Standards.OtherThinLayer }).ToList();
        entities.AddRange(supports.Select(Poly));
        try { return OffsetRebar.Create(entities); }
        finally { foreach (var entity in entities) { entity.Dispose(); } }
    }
    private static Polyline Poly(List<Point2d> points)
    {
        var poly = new Polyline { Closed = true, Layer = Standards.OtherThinLayer };
        for (var i = 0; i < points.Count; i++) { poly.AddVertexAt(i, points[i], 0, 0, 0); }
        return poly;
    }
    private static IEnumerable<ContourGraph.Edge> WorldEdges(OffsetRebar.Plan plan) => plan.Bars.SelectMany(path =>
        Enumerable.Range(0, path.Points.Count - (path.IsClosed ? 0 : 1)).Select(i => new ContourGraph.Edge(World(plan, path.Points[i]), World(plan, path.Points[(i + 1) % path.Points.Count]))));
    private static RebarAnchorage.EndResult End(OffsetRebar.Plan plan, RebarAnchorage.AnchorKind kind) => plan.AnchorEnds.Single(end => end.Kind == kind);
    private static Point2d World(OffsetRebar.Plan plan, Point2d point) => plan.Origin + new Vector2d(point.X, point.Y);
    private static List<Point2d> Beam() => Rect(0, -2000, 960, 4000);
    private static List<Point2d> Rect(double x, double y, double w, double h) => Points(x, y, x + w, y, x + w, y + h, x, y + h);
    private static List<Point2d> Points(params double[] values)
    {
        var result = new List<Point2d>(); for (var i = 0; i < values.Length; i += 2) { result.Add(new Point2d(values[i], values[i + 1])); } return result;
    }
    private static void Point(Point2d point, double x, double y) => Require(point.GetDistanceTo(new Point2d(x, y)) < .001, "point " + point);
    private static void Require(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
}
