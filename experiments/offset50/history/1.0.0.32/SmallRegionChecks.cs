using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Geometry;
using AutoCADPlugin;

internal static class SmallRegionChecks
{
    public static void Run(Action<string, Action> check, string root)
    {
        check("small32:500x400 upper straight600 from beam face, return300 reduced to100", () =>
        {
            var plan = Plan(Rect(-500, 0, 500, 400));
            var end = plan.AnchorEnds.Single();
            Require(end.Kind == RebarAnchorage.AnchorKind.SmallStraight && end.Extension.Count == 2, "only600 straight, no elbow");
            Point(World(plan, end.Extension[0]), 0, 350); Point(World(plan, end.Extension.Last()), 600, 350);
            Require(plan.BeamSides.ShortenedReturnCount == 1 && plan.BeamSides.SuppressedRegionCount == 1, "one shortened small region");
            var path = WorldPath(plan);
            Require(path.Any(p => p.GetDistanceTo(new Point2d(-450, 250)) < .001), "free end raised200 from50 to250");
            Require(path.Any(p => p.GetDistanceTo(new Point2d(-450, 350)) < .001), "upper corner unchanged");
            Require(path.All(p => p.Y >= 250), "no bottom bar or anchorage hook");
        });
        check("small32: mirrored/reversed and large translated coordinates", () =>
        {
            var shift = new Vector2d(18000000, -1100000);
            var material = Rect(960, 0, 500, 400).Select(p => p + shift).Reverse().ToList();
            var beam = Beam().Select(p => p + shift).Reverse().ToList();
            var plan = BeamSideChecks.Plan(new[] { material }, new[] { beam });
            var end = plan.AnchorEnds.Single();
            Point(World(plan, end.Extension.Last()) - shift, 360, 350);
            Require(WorldPath(plan).Any(p => (p - shift).GetDistanceTo(new Point2d(1410, 250)) < .001), "opposite free end shortened upwards");
        });
        check("small32: upper enters beam extension with600 straight, U unchanged", () =>
        {
            var plan = BeamSideChecks.Plan(new[] { Rect(0, 0, 960, 800), Rect(-500, 300, 500, 400) }, new[] { Rect(0, -2000, 960, 2000) });
            Require(plan.BeamTops.Bars.Count == 1 && plan.Bars.Count == 2, "U plus small upper bar");
            var end = plan.AnchorEnds.Single();
            Require(end.Kind == RebarAnchorage.AnchorKind.SmallStraight, "small rule in virtual support");
            Point(World(plan, end.Extension.Last()), 600, 650);
            Point(World(plan, plan.BeamTops.Bars[0].Points[0]), 50, -1000);
            var ordinary = plan.Bars.Single(path => path != plan.BeamTops.Bars[0]);
            Require(ordinary.Points.Select(p => World(plan, p)).Any(p => p.GetDistanceTo(new Point2d(-450, 550)) < .001), "free leg shortens outside U");
        });
        check("small32: beam700 allows600 though800 bend would fail; spacious beam still600", () =>
        {
            foreach (var width in new[] { 700.0, 2500.0 })
            {
                var plan = BeamSideChecks.Plan(new[] { Rect(-500, 0, 500, 400) }, new[] { Rect(0, -3000, width, 6000) });
                var end = plan.AnchorEnds.Single();
                Require(end.Kind == RebarAnchorage.AnchorKind.SmallStraight, "no bent or1000 fallback");
                Point(World(plan, end.Extension.Last()), 600, 350);
            }
        });
        check("small32:600 does not fit, upper keeps original cut and reports, no bend", () =>
        {
            var plan = BeamSideChecks.Plan(new[] { Rect(-500, 0, 500, 400) }, new[] { Rect(0, -2000, 599.9, 4000) });
            var end = plan.AnchorEnds.Single();
            Require(end.Kind == RebarAnchorage.AnchorKind.Failed && end.Extension.Count == 0 && end.Reason.Contains("小区域上部纵筋"), "explicit small upper failure");
            Require(WorldPath(plan).Any(p => p.GetDistanceTo(new Point2d(-50, 350)) < .001), "original support endpoint retained");
        });
        check("small32: vertical return <=200 disappears without shortening horizontal bar", () =>
        {
            foreach (var height in new[] { 200.0, 300.0, 300.04 })
            {
                var plan = Plan(Rect(-500, 0, 500, height));
                var points = WorldPath(plan);
                Require(points.Count >= 2 && points.All(p => Math.Abs(p.Y - (height - 50)) < .001), "upper only, no negative return");
                Require(Math.Abs(points.Min(p => p.X) + 450) < .001 && Math.Abs(points.Max(p => p.X) - 600) < .001, "full upper and600 preserved");
                Require(plan.BeamSides.ShortenedReturnCount == 1, "return removed once");
            }
        });
        check("small32: return200.1 leaves0.1 at project precision", () =>
        {
            var plan = Plan(Rect(-500, 0, 500, 300.1));
            var points = WorldPath(plan);
            Require(Math.Abs(points.Max(p => p.Y) - points.Min(p => p.Y) - .1) < .001, "0.1 remaining");
        });
        check("small32: inclined upper follows original direction for straight600", () =>
        {
            var plan = Plan(Points(-500, 0, 0, 100, 0, 500, -500, 400));
            var end = plan.AnchorEnds.Single();
            Require(end.Kind == RebarAnchorage.AnchorKind.SmallStraight && end.Extension.Count == 2, "small overrides slope/ordinary bends");
            var vector = end.Extension[1] - end.Extension[0];
            Require(Math.Abs(vector.Length - 600) < .001 && Math.Abs(vector.Y / vector.X - .2) < .001, "unchanged slope600");
        });
        check("small32: vertical subdivisions trim by total200 in either traversal order", () =>
        {
            foreach (var reverse in new[] { false, true })
            {
                var points = Points(-450, 50, -450, 180, -450, 350, -50, 350);
                if (reverse) { points.Reverse(); }
                var path = new RebarPath(points, false, null, null, 0,
                    reverse ? RebarEndRule.SmallStraight : RebarEndRule.Free,
                    reverse ? RebarEndRule.Free : RebarEndRule.SmallStraight);
                var result = SmallRegionRebar.ShortenReturns(path, out var count);
                Require(result != null && count == 1, "one vertical run");
                Require(result!.Points.Count == 3 && result.Points.Any(p => p == new Point2d(-450, 250)), "200 from complete run, not each segment");
            }
        });
        check("small32: inclined free edge and support-facing vertical legs are untouched", () =>
        {
            var slope = new RebarPath(Points(-450, 50, -400, 350, -50, 350), false, null, null, 0, RebarEndRule.Free);
            var unchanged = SmallRegionRebar.ShortenReturns(slope, out var count);
            Require(count == 0 && unchanged != null && unchanged.Points.SequenceEqual(slope.Points), "only vertical returns");
            var attached = new RebarPath(Points(-450, 50, -450, 350, -50, 350), false);
            unchanged = SmallRegionRebar.ShortenReturns(attached, out count);
            Require(count == 0 && unchanged != null && unchanged.Points.SequenceEqual(attached.Points), "only free cut ends");
        });
        check("small32: two downward free legs shorten independently, no zero-length polyline", () =>
        {
            var path = new RebarPath(Points(0, 0, 0, 300, 400, 300, 400, 0), false, null, null, 0, RebarEndRule.Free, RebarEndRule.Free);
            var shortened = SmallRegionRebar.ShortenReturns(path, out var count);
            Require(shortened != null && count == 2, "two returns");
            Point(shortened!.Points[0], 0, 200); Point(shortened.Points.Last(), 400, 200);
            var verticalOnly = new RebarPath(Points(0, 0, 0, 150), false, null, null, 0, RebarEndRule.Free, RebarEndRule.Free);
            Require(SmallRegionRebar.ShortenReturns(verticalOnly, out count) == null && count == 1, "no degenerate output");
        });
        check("small32 native DWG: short return removed, straight upper retained", () =>
        {
            BeamSideChecks.Fixture(root, "tiny-side-beam", Rect(-500, 0, 500, 200));
        });
    }

    private static OffsetRebar.Plan Plan(List<Point2d> material) => BeamSideChecks.Plan(new[] { material }, new[] { Beam() });
    private static List<Point2d> WorldPath(OffsetRebar.Plan plan) => plan.Bars.Single().Points.Select(p => World(plan, p)).ToList();
    private static Point2d World(OffsetRebar.Plan plan, Point2d point) => plan.Origin + new Vector2d(point.X, point.Y);
    private static List<Point2d> Beam() => Rect(0, -2000, 960, 4000);
    private static List<Point2d> Rect(double x, double y, double width, double height) => Points(x, y, x + width, y, x + width, y + height, x, y + height);
    private static List<Point2d> Points(params double[] values)
    {
        var result = new List<Point2d>(); for (var i = 0; i < values.Length; i += 2) { result.Add(new Point2d(values[i], values[i + 1])); } return result;
    }
    private static void Point(Point2d point, double x, double y) => Require(point.GetDistanceTo(new Point2d(x, y)) < .001, "point " + point);
    private static void Require(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
}
