using System;
using System.Collections.Generic;
using System.Linq;
using AutoCADPlugin;
using Autodesk.AutoCAD.Geometry;

internal static class PointLayout37ReviewChecks
{
    private const double Tolerance = .01;

    public static void Run(Action<string, Action> check)
    {
        check("review37: masked anchor arm does not turn an actual T into a fixed L", () =>
        {
            var context = Context(1200, 1200);
            context.AnchorEnds.Add(new RebarAnchorage.EndResult(2, false, P(500, 500),
                RebarAnchorage.AnchorKind.Bent, new[] { P(900, 500), P(900, 100) }, 0));
            var bars = new[]
            {
                Path(P(100, 500), P(500, 500)),
                Path(P(500, 500), P(500, 900)),
                Path(P(500, 500), P(900, 500), P(900, 100))
            };
            var result = CornerPointRebar.Create(context, bars);
            Require(result.Centers.Count == 0,
                "the excluded anchor still contributes the actual third junction ray");
        });

        check("review37: a real same-path bend survives a masked crossing anchor", () =>
        {
            var context = Context(1200, 1200);
            context.AnchorEnds.Add(new RebarAnchorage.EndResult(1, false, P(500, 500),
                RebarAnchorage.AnchorKind.Bent, new[] { P(900, 500), P(900, 100) }, 0));
            var result = CornerPointRebar.Create(context, new[]
            {
                Path(P(100, 500), P(500, 500), P(500, 900)),
                Path(P(500, 500), P(900, 500), P(900, 100))
            });
            Require(result.Centers.Count == 1 && Has(result.Centers, P(432.5, 567.5)),
                "source adjacency retains genuine bend while anchor contributes no corner");
        });

        check("review37: equal-bounds crossing paths have selection-order independent centers", () =>
        {
            var context = Context(3000, 3000);
            var first = Path(P(200, 200), P(2700, 2700));
            var second = Path(P(200, 2700), P(2700, 200));
            var forward = Distribute(context, new[] { first, second });
            var swapped = Distribute(context, new[] { second, first });
            Require(forward.Centers.Count > 0, "nonempty crossing rows");
            SameCenters(forward.Centers, swapped.Centers,
                "bounds ties cannot choose different winning point rows");
            Require(forward.FixedCenters.Count == 0, "X has no fixed corners");
        });

        check("review37: reversing crossing paths retains centers despite point competition", () =>
        {
            var context = Context(3000, 3000);
            var first = Path(P(200, 200), P(2700, 2700));
            var second = Path(P(200, 2700), P(2700, 200));
            var original = Distribute(context, new[] { first, second });
            var reversed = Distribute(context, new[] { Reverse(second), Reverse(first) });
            SameCenters(original.Centers, reversed.Centers,
                "direction and selection reversal preserve geometric output");
        });

        check("review37: closed path cyclic start and winding leave fixed and distributed centers unchanged", () =>
        {
            var context = Context(3000, 2000);
            var ring = Rect(50, 50, 2900, 1900);
            var cross = Path(P(200, 200), P(2700, 1700));
            var original = Distribute(context, new[] { new RebarPath(ring, true), cross });
            Require(original.FixedCenters.Count == 4 && original.Centers.Count > 4,
                "closed fixture includes real corners and distributed points");
            for (var start = 0; start < ring.Count; start++)
            {
                var rotated = ring.Skip(start).Concat(ring.Take(start)).ToList();
                foreach (var candidate in new[] { rotated, rotated.AsEnumerable().Reverse().ToList() })
                {
                    var result = Distribute(context, new[] { Reverse(cross), new RebarPath(candidate, true) });
                    SameCenters(original.FixedCenters, result.FixedCenters, "closed cyclic fixed corners");
                    SameCenters(original.Centers, result.Centers, "closed cyclic distribution");
                }
            }
        });

        check("review37: U top and both leg tails survive reverse traversal and crossing path", () =>
        {
            var beam = Rect(0, -2000, 960, 2000);
            var tops = BeamTopRebar.Create(new[] { Rect(0, 0, 960, 1800) }, new[] { beam }, 50);
            var context = new OffsetRebar.Plan
            {
                BeamTops = tops,
                Supports = tops.EffectiveSupports,
                OriginalSupports = new List<List<Point2d>> { beam }
            };
            var bars = tops.Bars.Concat(new[] { Path(P(0, 900), P(960, 900)) }).ToList();
            var first = Distribute(context, bars);
            var reverse = Distribute(context, bars.AsEnumerable().Reverse().Select(Reverse).ToList());
            SameCenters(first.Centers, reverse.Centers, "U selection/reversal result");
            foreach (var x in new[] { 117.5, 842.5 })
            {
                Require(Has(first.FixedCenters, P(x, 1682.5)), "fixed U top remains unchanged");
                Require(Has(first.Centers, P(x, 400)), "both U legs terminate at real beam400");
            }
            Require(first.Centers.All(point => !CornerPointRebar.IntersectsSupport(point, beam)),
                "no dot disk overlaps crossed beam");
        });
    }

    private static DistributedPointRebar.Result Distribute(OffsetRebar.Plan context, IReadOnlyList<RebarPath> bars) =>
        DistributedPointRebar.Create(context, bars, CornerPointRebar.Create(context, bars));

    private static RebarPath Reverse(RebarPath path) => new RebarPath(path.Points.Reverse(), path.IsClosed);
    private static RebarPath Path(params Point2d[] points) => new RebarPath(points, false);
    private static Point2d P(double x, double y) => new Point2d(x, y);
    private static List<Point2d> Rect(double x, double y, double width, double height) =>
        new List<Point2d> { P(x, y), P(x + width, y), P(x + width, y + height), P(x, y + height) };
    private static OffsetRebar.Plan Context(double width, double height) =>
        new OffsetRebar.Plan { Boundaries = new List<List<Point2d>> { Rect(0, 0, width, height) } };
    private static bool Has(IEnumerable<Point2d> points, Point2d target) =>
        points.Any(point => point.GetDistanceTo(target) <= Tolerance);

    private static void SameCenters(IReadOnlyList<Point2d> first, IReadOnlyList<Point2d> second, string message) =>
        Require(first.Count == second.Count && first.All(point => Has(second, point)), message);

    private static void Require(bool value, string message)
    {
        if (!value) { throw new InvalidOperationException(message); }
    }
}
