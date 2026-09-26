using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Geometry;
using Box = AutoCADPlugin.SupportLabelLayout.Box;
using Edge = AutoCADPlugin.ContourGraph.Edge;

namespace AutoCADPlugin;

// Pure layout: one target is one whole rebar polyline or one support label.
// All dimensions are in the already enlarged drawing, independent of drawing origin.
internal static class AnnotationLayout
{
    public const string RebarText = "%%1328@150";
    public const double TextHeight = 250;
    private const double Clearance = 65;

    internal sealed class Target
    {
        public Target(string key, string text, List<Edge> segments, Box textOffsets, List<Point2d>? support = null)
        { Key = key; Text = text; Segments = segments; TextOffsets = textOffsets; Support = support; }
        public string Key { get; }
        public string Text { get; }
        public List<Edge> Segments { get; }
        public Box TextOffsets { get; }
        public List<Point2d>? Support { get; }
        public bool IsRebar => Support == null;
        public Box GeometryBounds => Box.Around(Segments.SelectMany(e => new[] { e.Start, e.End }).ToList());
    }

    internal sealed class Scene
    {
        public List<Target> Targets { get; } = new List<Target>();
        public List<Edge> Ink { get; } = new List<Edge>();
        public List<Box> Blocks { get; } = new List<Box>();
    }

    internal sealed class Placement
    {
        public Placement(Target target, Point2d textPosition, List<Point2d> leader)
        {
            Target = target; TextPosition = textPosition; Leader = leader;
            var b = target.TextOffsets;
            Bounds = new Box(textPosition.X + b.Left, textPosition.Y + b.Bottom,
                textPosition.X + b.Right, textPosition.Y + b.Top);
            Edges = SupportLabelLayout.Edges(leader).ToList();
        }
        public Target Target { get; }
        public Point2d TextPosition { get; }
        public List<Point2d> Leader { get; }
        public List<Edge> Edges { get; }
        public Box Bounds { get; }
        public double BaseCost { get; set; }
        public int HardConflicts { get; set; }
    }

    public static List<Placement> Create(Scene scene)
    {
        if (scene.Targets.Count == 0) { return new List<Placement>(); }
        var targets = scene.Targets.OrderBy(t => t.GeometryBounds.Left).ThenBy(t => t.GeometryBounds.Bottom)
            .ThenBy(t => t.Key, StringComparer.Ordinal).ToList();
        var pools = targets.ToDictionary(t => t.Key, t => Candidates(t).Select(p => ScoreStatic(p, scene))
            .OrderBy(p => p.HardConflicts).ThenBy(p => p.BaseCost).Take(180).ToList());
        var orders = new[] { targets, targets.AsEnumerable().Reverse().ToList(),
            targets.OrderBy(t => pools[t.Key].Count(p => p.HardConflicts == 0)).ToList(),
            targets.OrderBy(t => t.IsRebar).ToList(), targets.OrderByDescending(t => t.IsRebar).ToList() };
        List<Placement>? best = null;
        var bestCost = double.MaxValue;
        foreach (var order in orders)
        {
            var placements = new List<Placement>();
            foreach (var target in order) { placements.Add(Choose(pools[target.Key], placements)); }
            // Joint improvement also moves beam/slab text after bars have been placed.
            for (var pass = 0; pass < 3; pass++)
            {
                for (var i = 0; i < placements.Count; i++)
                {
                    var others = placements.Where((_, index) => index != i).ToList();
                    placements[i] = Choose(pools[placements[i].Target.Key], others);
                }
            }
            var cost = TotalCost(placements);
            if (cost < bestCost) { bestCost = cost; best = placements; }
        }
        var result = best ?? throw new InvalidOperationException("没有可用的标注布局。");
        if (HardConflicts(result) > 0)
        { throw new InvalidOperationException("本次框选内没有找到所有文字均可避让的位置，未改动图纸。请将相邻文字和完整大样一并框选，或适当腾出空间后重试。"); }
        return result.OrderBy(p => p.Target.Key, StringComparer.Ordinal).ToList();
    }

    private static Placement Choose(List<Placement> pool, List<Placement> placed) =>
        pool.OrderBy(p => p.BaseCost + p.HardConflicts * 100000000.0 + placed.Sum(other => PairCost(p, other))).First();

    private static double TotalCost(List<Placement> placements)
    {
        var total = placements.Sum(p => p.BaseCost + p.HardConflicts * 100000000.0);
        for (var i = 0; i < placements.Count; i++)
        for (var j = i + 1; j < placements.Count; j++) { total += PairCost(placements[i], placements[j]); }
        return total;
    }

    internal static int HardConflicts(IReadOnlyList<Placement> placements)
    {
        var count = placements.Sum(p => p.HardConflicts);
        for (var i = 0; i < placements.Count; i++)
        for (var j = i + 1; j < placements.Count; j++) { count += PairHard(placements[i], placements[j]); }
        return count;
    }

    private static int PairHard(Placement a, Placement b) =>
        (a.Bounds.Expand(Clearance).Overlaps(b.Bounds.Expand(Clearance)) ? 1 : 0) +
        a.Edges.Count(e => SupportLabelLayout.Hits(e, b.Bounds.Expand(Clearance))) +
        b.Edges.Count(e => SupportLabelLayout.Hits(e, a.Bounds.Expand(Clearance)));

    private static double PairCost(Placement a, Placement b) => PairHard(a, b) * 100000000.0 +
        a.Edges.Sum(e => b.Edges.Count(f => Crosses(e, f))) * 10000;

    private static Placement ScoreStatic(Placement p, Scene scene)
    {
        var box = p.Bounds.Expand(Clearance);
        p.HardConflicts = scene.Ink.Count(e => SupportLabelLayout.Hits(e, box)) + scene.Blocks.Count(box.Overlaps);
        p.HardConflicts += scene.Targets.Where(t => t.Support != null).Count(t => box.Overlaps(t.GeometryBounds));
        foreach (var edge in p.Edges)
        { p.HardConflicts += scene.Blocks.Count(b => SupportLabelLayout.Hits(edge, b.Expand(30))); }
        var length = p.Edges.Sum(e => e.Start.GetDistanceTo(e.End));
        var barCrossings = 0;
        var contourCrossings = 0;
        foreach (var edge in p.Edges)
        {
            foreach (var target in scene.Targets.Where(t => t.IsRebar))
            foreach (var bar in target.Segments)
            {
                if (Crosses(edge, bar) && !(target == p.Target &&
                    edge.Start.GetDistanceTo(p.Leader[0]) < .1 && ContourGraph.Distance(edge.Start, bar) < .1))
                { barCrossings++; }
            }
            contourCrossings += scene.Ink.Count(e => Crosses(edge, e));
        }
        p.BaseCost = length + barCrossings * 15000 + contourCrossings * 100;
        return p;
    }

    private static IEnumerable<Placement> Candidates(Target target)
    {
        var anchors = new List<Point2d>();
        if (target.Support != null)
        {
            var b = target.GeometryBounds;
            foreach (var fx in new[] { .15, .5, .85 })
            foreach (var fy in new[] { .2, .5, .8 })
            {
                var p = new Point2d(b.Left + (b.Right - b.Left) * fx, b.Bottom + (b.Top - b.Bottom) * fy);
                if (ContourGraph.Contains(target.Support, p)) { anchors.Add(p); }
            }
        }
        else
        {
            foreach (var edge in target.Segments.OrderByDescending(e => e.Start.GetDistanceTo(e.End)).Take(12))
            foreach (var fraction in new[] { .5, .25, .75 })
            { anchors.Add(edge.Start + (edge.End - edge.Start) * fraction); }
        }
        foreach (var anchor in anchors)
        foreach (var distance in new[] { 450.0, 800, 1250, 1900, 2800, 4200, 6500 })
        foreach (var direction in new[] { new Vector2d(1, 0), new Vector2d(-1, 0), new Vector2d(1, 1),
            new Vector2d(-1, 1), new Vector2d(1, -1), new Vector2d(-1, -1), new Vector2d(0, 1), new Vector2d(0, -1) })
        foreach (var right in new[] { true, false })
        {
            var bend = anchor + direction.GetNormal() * distance;
            var width = target.TextOffsets.Right - target.TextOffsets.Left;
            var end = bend + new Vector2d((right ? 1 : -1) * (width + 100), 0);
            // The native font's measured extents, not the encoded string length, set the landing.
            var text = new Point2d(Math.Min(bend.X, end.X) + 50 - target.TextOffsets.Left,
                bend.Y + 85 - target.TextOffsets.Bottom);
            if (Math.Abs(direction.Y) < .01 && ((right && direction.X < 0) || (!right && direction.X > 0))) { continue; }
            yield return new Placement(target, text, new List<Point2d> { anchor, bend, end });
        }
    }

    internal static bool Crosses(Edge a, Edge b)
    {
        var r = a.End - a.Start; var s = b.End - b.Start;
        var cross = r.X * s.Y - r.Y * s.X;
        if (Math.Abs(cross) < 1e-8)
        {
            return ContourGraph.Distance(a.Start, b) < .1 || ContourGraph.Distance(a.End, b) < .1 ||
                ContourGraph.Distance(b.Start, a) < .1 || ContourGraph.Distance(b.End, a) < .1;
        }
        var d = b.Start - a.Start;
        var t = (d.X * s.Y - d.Y * s.X) / cross; var u = (d.X * r.Y - d.Y * r.X) / cross;
        return t >= -1e-8 && t <= 1 + 1e-8 && u >= -1e-8 && u <= 1 + 1e-8;
    }
}
