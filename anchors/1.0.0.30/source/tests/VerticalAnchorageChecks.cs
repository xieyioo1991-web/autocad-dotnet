using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Geometry;
using AutoCADPlugin;

internal static class VerticalAnchorageChecks
{
    public static void Run(Action<string, Action> check)
    {
        check("vertical anchor: right entry bends at U inner40, vertical800 not perpendicular", () =>
        {
            var end = WithU(new Point2d(1010, 525), new Point2d(1210, 625));
            Require(end.Kind == RebarAnchorage.AnchorKind.VerticalBent, "vertical bend");
            Point(end.Extension[0], 960, 500); Point(end.Extension[1], 870, 455); Point(end.Extension[2], 870, -345);
            Require(Math.Abs((end.Extension[1] - end.Extension[0]).DotProduct(end.Extension[2] - end.Extension[1])) > 1, "not an ordinary90 bend");
        });
        check("vertical anchor: top entry crosses far leg guide, no800 lead prerequisite", () =>
        {
            var end = WithU(new Point2d(250, 850), new Point2d(450, 950));
            Require(end.Kind == RebarAnchorage.AnchorKind.VerticalBent, "short approach allowed");
            Point(end.Extension[0], 150, 800); Point(end.Extension[1], 90, 770); Point(end.Extension[2], 90, -30);
        });
        check("vertical anchor: left entry inward direction is right, not outline side", () =>
        {
            var end = WithU(new Point2d(-50, 525), new Point2d(-250, 625));
            Point(end.Extension[1], 90, 455); Point(end.Extension[2], 90, -345);
        });
        check("vertical anchor: special rule precedes even a spacious1200 straight anchor", () =>
        {
            var beam = Rect(0, -7000, 3000, 7000);
            var tops = BeamTopRebar.Create(new[] { Rect(0, 0, 3000, 800) }, new[] { beam }, 50);
            var end = First(new Point2d(3050, 525), new Point2d(3250, 625), tops.EffectiveSupports,
                BeamAnchorGuide.Create(new[] { beam }, tops, 50));
            Require(end.Kind == RebarAnchorage.AnchorKind.VerticalBent, "must not select1200 straight");
            Point(end.Extension[1], 2910, 455); Point(end.Extension[2], 2910, -345);
        });
        check("vertical anchor: horizontal90 and vertical straight remain ordinary", () =>
        {
            Require(WithU(new Point2d(1010, 500), new Point2d(1210, 500)).Kind == RebarAnchorage.AnchorKind.Bent, "horizontal ordinary");
            Require(WithU(new Point2d(500, 850), new Point2d(500, 1050)).Kind == RebarAnchorage.AnchorKind.Straight, "vertical ordinary");
        });
        check("vertical anchor: acute inclined entry is not captured by obtuse rule", () =>
        {
            Require(WithU(new Point2d(1010, 475), new Point2d(1210, 375)).Kind != RebarAnchorage.AnchorKind.VerticalBent, "acute excluded");
        });
        check("vertical anchor: exact800 fits,799.99 keeps original end", () =>
        {
            var exact = WithU(new Point2d(1010, -1130), new Point2d(1210, -1030));
            Require(exact.Kind == RebarAnchorage.AnchorKind.VerticalBent, "800 exact");
            Point(exact.Extension[2], 870, -2000);
            var shortEnd = WithU(new Point2d(1010, -1130.01), new Point2d(1210, -1030.01));
            Require(shortEnd.Kind == RebarAnchorage.AnchorKind.Failed && shortEnd.Extension.Count == 0 && shortEnd.Reason.Contains("800"), "no shortened or upward fallback");
        });
        check("vertical anchor: direct beam without U uses outline50, not90", () =>
        {
            var beam = Rect(0, -2000, 960, 2800);
            foreach (var reverse in new[] { false, true })
            {
                if (reverse) { beam.Reverse(); }
                var end = First(new Point2d(1010, 525), new Point2d(1210, 625), new[] { beam },
                    BeamAnchorGuide.Create(new[] { beam }, new BeamTopRebar.Result(), 50));
                Require(end.Kind == RebarAnchorage.AnchorKind.VerticalBent, "bare beam vertical");
                Point(end.Extension[1], 910, 475); Point(end.Extension[2], 910, -325);
            }
        });
        check("vertical anchor: floor slab keeps ordinary rules", () =>
        {
            var slab = Rect(0, -1000, 5000, 1500);
            var guides = BeamAnchorGuide.Create(new[] { slab }, new BeamTopRebar.Result(), 50);
            Require(guides.Count == 0, "not a beam");
            var end = First(new Point2d(-50, 25), new Point2d(-250, 125), new[] { slab }, guides);
            Require(end.Kind == RebarAnchorage.AnchorKind.Straight, "slab still1200");
        });
        check("vertical anchor: unrelated beam guide cannot supply a turn", () =>
        {
            var beam = Rect(0, -2000, 960, 2800);
            var guides = new[] { new BeamAnchorGuide(1, new[] { new BeamAnchorGuide.VerticalLine(870) }) };
            var end = First(new Point2d(1010, 525), new Point2d(1210, 625), new[] { beam }, guides);
            Require(end.Kind != RebarAnchorage.AnchorKind.VerticalBent, "guide belongs to another beam");
        });
        check("vertical anchor:800 cannot cross concave void even if tip is inside", () =>
        {
            var beam = Points(0, -2000, 960, -2000, 960, -450, 500, -450, 500, -300, 960, -300, 960, 800, 0, 800);
            var end = First(new Point2d(1010, 25), new Point2d(1210, 125), new[] { beam },
                BeamAnchorGuide.Create(new[] { beam }, new BeamTopRebar.Result(), 50));
            Require(end.Kind == RebarAnchorage.AnchorKind.Failed && end.Extension.Count == 0, "middle outside rejected");
        });
        check("vertical anchor: do not follow forward slope beyond first exit to a guide", () =>
        {
            var end = WithU(new Point2d(60, 850), new Point2d(260, 1050));
            Require(end.Kind == RebarAnchorage.AnchorKind.Failed && end.Extension.Count == 0, "both U guides behind entry");
        });
        check("vertical anchor: too narrow U cannot cross its40 guides", () =>
        {
            var beam = Rect(0, -2000, 160, 2000);
            var tops = BeamTopRebar.Create(new[] { Rect(0, 0, 160, 800) }, new[] { beam }, 50);
            var end = First(new Point2d(210, 525), new Point2d(410, 625), tops.EffectiveSupports,
                BeamAnchorGuide.Create(new[] { beam }, tops, 50));
            Require(end.Kind == RebarAnchorage.AnchorKind.Failed, "no swapped guides or ordinary fallback");
        });
        check("vertical anchor: reversed path and large translated coordinates", () =>
        {
            var shift = new Vector2d(18000000, -1100000);
            var beam = Rect(0, -2000, 960, 2000).Select(p => p + shift).ToList();
            var material = Rect(0, 0, 960, 800).Select(p => p + shift).ToList();
            var tops = BeamTopRebar.Create(new[] { material }, new[] { beam }, 50);
            var contact = ContourGraph.Edges(tops.EffectiveSupports[0]).Select(edge => new SupportContact(0, edge)).ToList();
            var path = new RebarPath(new[] { new Point2d(1210, 625) + shift, new Point2d(1010, 525) + shift }, false, null, contact);
            var result = RebarAnchorage.Apply(new[] { path }, tops.EffectiveSupports, BeamAnchorGuide.Create(new[] { beam }, tops, 50));
            var end = result.Ends.Single(item => !item.AtStart);
            Require(end.Kind == RebarAnchorage.AnchorKind.VerticalBent, "translated reversed");
            Require(end.Extension[2].GetDistanceTo(new Point2d(870, -345) + shift) < .001, "translated tip");
        });
    }

    public static void VerifyExample(OffsetRebar.Plan plan)
    {
        var ends = plan.AnchorEnds.Where(end => end.Kind == RebarAnchorage.AnchorKind.VerticalBent).ToList();
        Require(ends.Count == 2 && plan.AnchorEnds.All(end => end.Kind != RebarAnchorage.AnchorKind.Failed), "both roof ends must now anchor");
        var elbows = ends.Select(end => plan.Origin + new Vector2d(end.Extension[1].X, end.Extension[1].Y)).ToList();
        Require(elbows.Any(p => Math.Abs(p.X - 90) < .01) && elbows.Any(p => Math.Abs(p.X - 870) < .01), "sample U50/910, guides90/870");
        foreach (var end in ends)
        {
            var elbow = end.Extension[1]; var tip = end.Extension[2];
            Point(tip, elbow.X, elbow.Y - 800);
            var path = plan.UnanchoredBars[end.PathIndex];
            var neighbor = path.Points[end.AtStart ? 1 : path.Points.Count - 2];
            var before = (end.Original - neighbor).GetNormal();
            var turn = elbow - end.Original;
            Require(Math.Abs(before.X * turn.Y - before.Y * turn.X) < .001 && before.DotProduct(turn) > 0, "elbow is forward intersection on original slope");
        }
    }

    private static RebarAnchorage.EndResult WithU(Point2d endpoint, Point2d neighbor)
    {
        var beam = Rect(0, -2000, 960, 2000);
        var tops = BeamTopRebar.Create(new[] { Rect(0, 0, 960, 800) }, new[] { beam }, 50);
        return First(endpoint, neighbor, tops.EffectiveSupports, BeamAnchorGuide.Create(new[] { beam }, tops, 50));
    }

    private static RebarAnchorage.EndResult First(Point2d endpoint, Point2d neighbor,
        IReadOnlyList<List<Point2d>> supports, IReadOnlyList<BeamAnchorGuide> guides)
    {
        var contacts = ContourGraph.Edges(supports[0]).Select(edge => new SupportContact(0, edge)).ToList();
        var path = new RebarPath(new[] { endpoint, neighbor }, false, contacts);
        return RebarAnchorage.Apply(new[] { path }, supports, guides).Ends.Single(end => end.AtStart);
    }

    private static List<Point2d> Rect(double x, double y, double w, double h) => Points(x, y, x + w, y, x + w, y + h, x, y + h);
    private static List<Point2d> Points(params double[] values)
    {
        var result = new List<Point2d>();
        for (var i = 0; i < values.Length; i += 2) { result.Add(new Point2d(values[i], values[i + 1])); }
        return result;
    }
    private static void Point(Point2d point, double x, double y) => Require(point.GetDistanceTo(new Point2d(x, y)) < .001, "point " + point);
    private static void Require(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
}
