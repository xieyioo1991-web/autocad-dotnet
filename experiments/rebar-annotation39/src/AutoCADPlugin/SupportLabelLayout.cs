using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Geometry;

namespace AutoCADPlugin;

// Layout in the already enlarged structural drawing. Candidate leaders point
// into their own support and end beside/below text, never at its mid-height.
internal static class SupportLabelLayout
{
    public const double TextHeight = 250;
    private const double Gap = 300;
    private const double TextLift = 75;

    internal readonly struct Box
    {
        public Box(double left, double bottom, double right, double top)
        { Left = left; Bottom = bottom; Right = right; Top = top; }
        public double Left { get; }
        public double Bottom { get; }
        public double Right { get; }
        public double Top { get; }
        public Box Expand(double margin) => new Box(Left - margin, Bottom - margin, Right + margin, Top + margin);
        public bool Overlaps(Box other) => Left < other.Right && Right > other.Left && Bottom < other.Top && Top > other.Bottom;
        public bool Contains(Point2d p) => p.X >= Left && p.X <= Right && p.Y >= Bottom && p.Y <= Top;
        public static Box Around(IReadOnlyList<Point2d> points) => new Box(points.Min(p => p.X), points.Min(p => p.Y),
            points.Max(p => p.X), points.Max(p => p.Y));
    }

    internal sealed class Placement
    {
        public Placement(string label, Point2d text, List<Point2d> leader, string form)
        {
            Label = label; Text = text; Leader = leader; Form = form;
            Bounds = new Box(text.X, text.Y, text.X + Width(label), text.Y + TextHeight);
        }
        public string Label { get; }
        public Point2d Text { get; }
        public List<Point2d> Leader { get; }
        public string Form { get; }
        public Box Bounds { get; }
        public int Conflicts { get; set; }
    }

    public static List<Placement> Create(IReadOnlyList<List<Point2d>> supports,
        IReadOnlyList<ContourGraph.Edge> outline, string fallbackLabel)
    {
        var result = new List<Placement>();
        var boxes = supports.Select(Box.Around).ToList();
        foreach (var index in Enumerable.Range(0, supports.Count).OrderBy(i => boxes[i].Left).ThenBy(i => boxes[i].Bottom))
        {
            var own = boxes[index];
            var label = SupportClassification.Label(own.Right - own.Left, own.Top - own.Bottom, fallbackLabel);
            Placement? best = null;
            foreach (var gap in new[] { Gap, Gap * 2, Gap * 4 })
            {
                foreach (var candidate in Candidates(own, label, gap))
                {
                    var conflicts = boxes.Count(b => candidate.Bounds.Expand(50).Overlaps(b));
                    if (!ContourGraph.Contains(supports[index], candidate.Leader[0])) { conflicts += 100; }
                    conflicts += outline.Count(e => Hits(e, candidate.Bounds.Expand(50)));
                    foreach (var edge in Edges(candidate.Leader))
                    {
                        conflicts += boxes.Where((_, i) => i != index).Count(b => Hits(edge, b.Expand(50)));
                        conflicts += outline.Count(e => CrossesOutside(edge, e, own));
                        conflicts += result.Count(p => Hits(edge, p.Bounds.Expand(50)));
                        conflicts += result.Sum(p => Edges(p.Leader).Count(e => CrossesOutside(edge, e, own)));
                    }
                    conflicts += result.Count(p => candidate.Bounds.Expand(75).Overlaps(p.Bounds) ||
                        Edges(p.Leader).Any(e => Hits(e, candidate.Bounds.Expand(50))));
                    candidate.Conflicts = conflicts;
                    if (best == null || conflicts < best.Conflicts) { best = candidate; }
                    if (conflicts == 0) { break; }
                }
                if (best != null && best.Conflicts == 0) { break; }
            }
            result.Add(best ?? throw new InvalidOperationException("无法创建支撑指引候选。"));
        }
        return result;
    }

    private static IEnumerable<Placement> Candidates(Box box, string label, double gap)
    {
        var width = box.Right - box.Left;
        var height = box.Top - box.Bottom;
        var cx = (box.Left + box.Right) / 2;
        var cy = (box.Bottom + box.Top) / 2;
        var insetX = Math.Min(100, width / 4);
        var insetY = Math.Min(100, height / 4);
        if (width >= height * 1.5)
        {
            foreach (var side in new[] { -1, 1 })
            {
                var bottom = side < 0;
                var endY = bottom ? box.Bottom - gap - TextHeight : box.Top + gap;
                yield return new Placement(label, new Point2d(cx + TextLift, bottom ? endY + TextLift : endY),
                    new List<Point2d> { new Point2d(cx, cy), new Point2d(cx, endY + (bottom ? 0 : TextHeight + TextLift)) }, "vertical");
            }
            foreach (var above in new[] { true, false })
            foreach (var right in new[] { true, false })
            {
                var bend = new Point2d(cx + (right ? gap : -gap), above ? box.Top + gap : box.Bottom - gap - TextHeight);
                yield return Landing(label, new Point2d(cx, cy), bend, right, true);
            }
        }
        else
        {
            foreach (var fraction in new[] { .25, .5, .75 })
            foreach (var right in new[] { true, false })
            {
                var y = box.Bottom + height * fraction;
                var anchor = new Point2d(right ? box.Right - insetX : box.Left + insetX, y);
                var bend = new Point2d(right ? box.Right + gap : box.Left - gap, y);
                yield return Landing(label, anchor, bend, right, false);
            }
            foreach (var below in new[] { true, false })
            foreach (var right in new[] { true, false })
            {
                var anchor = new Point2d(right ? box.Right - insetX : box.Left + insetX,
                    below ? box.Bottom + Math.Min(300, height / 4) : box.Top - insetY);
                var bend = new Point2d(right ? box.Right + gap : box.Left - gap,
                    below ? box.Bottom - gap - TextHeight : box.Top + gap);
                yield return Landing(label, anchor, bend, right, true);
            }
        }
    }

    private static Placement Landing(string label, Point2d anchor, Point2d bend, bool right, bool elbow)
    {
        var length = Width(label) + 100;
        var end = bend + new Vector2d(right ? length : -length, 0);
        var text = new Point2d(Math.Min(bend.X, end.X) + 50, bend.Y + TextLift);
        return new Placement(label, text, elbow ? new List<Point2d> { anchor, bend, end } : new List<Point2d> { anchor, end },
            elbow ? "elbow" : "horizontal");
    }

    private static double Width(string label) => Math.Max(TextHeight * 2.5, label.Length * TextHeight);
    internal static IEnumerable<ContourGraph.Edge> Edges(IReadOnlyList<Point2d> points)
    {
        for (var i = 1; i < points.Count; i++) { yield return new ContourGraph.Edge(points[i - 1], points[i]); }
    }

    // Slab/beam layout uses finite segments and conservative text rectangles.
    internal static bool Hits(ContourGraph.Edge edge, Box box)
    {
        var low = 0.0; var high = 1.0;
        var d = edge.End - edge.Start;
        return Clip(-d.X, edge.Start.X - box.Left) && Clip(d.X, box.Right - edge.Start.X) &&
            Clip(-d.Y, edge.Start.Y - box.Bottom) && Clip(d.Y, box.Top - edge.Start.Y);
        bool Clip(double p, double q)
        {
            if (Math.Abs(p) < 1e-9) { return q >= 0; }
            var t = q / p;
            if (p < 0) { low = Math.Max(low, t); } else { high = Math.Min(high, t); }
            return low <= high;
        }
    }

    private static bool CrossesOutside(ContourGraph.Edge a, ContourGraph.Edge b, Box own)
    {
        var r = a.End - a.Start; var s = b.End - b.Start;
        var cross = r.X * s.Y - r.Y * s.X;
        if (Math.Abs(cross) < 1e-8)
        {
            // Ignore own-support overlap, but not an external collinear line.
            return !own.Expand(1).Contains(b.Start) && !own.Expand(1).Contains(b.End) &&
                (ContourGraph.Distance(a.Start, b) < 1 || ContourGraph.Distance(a.End, b) < 1);
        }
        var delta = b.Start - a.Start;
        var t = (delta.X * s.Y - delta.Y * s.X) / cross;
        var u = (delta.X * r.Y - delta.Y * r.X) / cross;
        return t >= 0 && t <= 1 && u >= 0 && u <= 1 && !own.Expand(1).Contains(a.Start + r * t);
    }
}
