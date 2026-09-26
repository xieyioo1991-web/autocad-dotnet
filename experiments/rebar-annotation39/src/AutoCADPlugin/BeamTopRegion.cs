using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Geometry;

namespace AutoCADPlugin;

// The user-defined vertical direction is WCS +Y. Region detection uses closed
// material geometry; annotations, reinforcement, and support diagonals never
// supply the two vertical sides.
internal sealed class BeamTopRegion
{
    private const double Tolerance = ContourGraph.Tolerance;

    private BeamTopRegion(int supportIndex, double left, double right, double bottom, double leftTop, double rightTop)
    { SupportIndex = supportIndex; Left = left; Right = right; Bottom = bottom; LeftTop = leftTop; RightTop = rightTop; }
    public int SupportIndex { get; }
    public double Left { get; }
    public double Right { get; }
    public double Bottom { get; }
    public double LeftTop { get; }
    public double RightTop { get; }
    public double Top => Math.Max(LeftTop, RightTop);
    public double Height => Top - Bottom;
    public bool HasInclinedConnection { get; private set; }
    public bool IsExtension => DrawingPrecision.GreaterThan(Height, 600);
    public bool NeedsU => IsExtension || !HasInclinedConnection;
    public List<Point2d> Polygon => new List<Point2d>
    {
        new Point2d(Left, Bottom), new Point2d(Right, Bottom),
        new Point2d(Right, Top), new Point2d(Left, Top)
    };

    private sealed class Side
    {
        public Side(double x) { X = x; }
        public double X { get; }
        public List<double> Tops { get; } = new List<double>();
    }

    public static List<BeamTopRegion> Find(IReadOnlyList<List<Point2d>> material, IReadOnlyList<List<Point2d>> supports)
    {
        var result = new List<BeamTopRegion>();
        var edges = material.SelectMany(ContourGraph.Edges).ToList();
        for (var index = 0; index < supports.Count; index++)
        {
            var support = supports[index];
            var left = support.Min(p => p.X); var right = support.Max(p => p.X);
            var bottom = support.Min(p => p.Y); var top = support.Max(p => p.Y);
            if (SupportClassification.IsFloorSlab(right - left, top - bottom)) { continue; }
            var faces = TopFaces(support, top);
            foreach (var face in faces)
            {
                FindOnFace(index, face.Start, face.End, top, edges, material, result);
            }
        }
        foreach (var region in result)
        {
            // Only an inclined boundary that leaves this upright is a branch.
            // A sloping cap contained between its two sides is not a branch.
            region.HasInclinedConnection = edges.Any(edge =>
                Math.Abs(edge.End.X - edge.Start.X) > Tolerance &&
                Math.Abs(edge.End.Y - edge.Start.Y) > Tolerance &&
                (Leaves(edge.Start, edge.End) || Leaves(edge.End, edge.Start)));

            bool Leaves(Point2d contact, Point2d other) =>
                contact.Y > region.Bottom + Tolerance && contact.Y <= region.Top + Tolerance &&
                (Math.Abs(contact.X - region.Left) <= Tolerance || Math.Abs(contact.X - region.Right) <= Tolerance) &&
                (other.X < region.Left - Tolerance || other.X > region.Right + Tolerance);
        }
        return result;
    }

    private static void FindOnFace(int supportIndex, double left, double right, double top,
        IReadOnlyList<ContourGraph.Edge> edges, IReadOnlyList<List<Point2d>> material, List<BeamTopRegion> result)
    {
        var sides = new List<Side>();
        foreach (var edge in edges)
        {
            if (Math.Abs(edge.Start.X - edge.End.X) > Tolerance || Math.Abs(edge.Start.Y - edge.End.Y) <= Tolerance) { continue; }
            var x = (edge.Start.X + edge.End.X) / 2;
            var height = Math.Max(edge.Start.Y, edge.End.Y);
            if (x < left - Tolerance || x > right + Tolerance || height <= top + Tolerance) { continue; }
            // Snap only within the existing contour tolerance at beam sides.
            if (Math.Abs(x - left) <= Tolerance) { x = left; }
            if (Math.Abs(x - right) <= Tolerance) { x = right; }
            var side = sides.FirstOrDefault(item => Math.Abs(item.X - x) <= Tolerance);
            if (side is null) { side = new Side(x); sides.Add(side); }
            if (!side.Tops.Any(value => Math.Abs(value - height) <= Tolerance)) { side.Tops.Add(height); }
        }
        sides.Sort((a, b) => a.X.CompareTo(b.X));
        if (sides.Count < 2) { return; }
        // Prefer the continuation of the two beam sides. Higher, narrower
        // branches inside that stem must not create extra overlapping hoops.
        if (Math.Abs(sides[0].X - left) <= Tolerance && Math.Abs(sides[sides.Count - 1].X - right) <= Tolerance)
        {
            var full = Candidate(sides[0], sides[sides.Count - 1]);
            if (full is not null) { result.Add(full); return; }
        }
        for (var i = 1; i < sides.Count; i++)
        {
            var region = Candidate(sides[i - 1], sides[i]);
            if (region is not null) { result.Add(region); }
        }

        BeamTopRegion? Candidate(Side a, Side b)
        {
            if (b.X - a.X <= Tolerance) { return null; }
            foreach (var pair in a.Tops.SelectMany(y1 => b.Tops.Select(y2 => (Left: y1, Right: y2)))
                .OrderByDescending(pair => Math.Min(pair.Left, pair.Right)).ThenByDescending(pair => Math.Max(pair.Left, pair.Right)))
            {
                var lowerTop = Math.Min(pair.Left, pair.Right);
                // Each side must itself stay connected to this beam. A tall,
                // detached contour sharing the same X must not raise the U.
                var strip = Math.Min((b.X - a.X) / 4, Tolerance / 4);
                if (Filled(a.X, b.X, top, lowerTop, material) &&
                    Filled(a.X, a.X + strip, top, pair.Left, material) &&
                    Filled(b.X - strip, b.X, top, pair.Right, material))
                { return new BeamTopRegion(supportIndex, a.X, b.X, top, pair.Left, pair.Right); }
            }
            return null;
        }
    }

    private static List<PolygonRay.Interval> TopFaces(IReadOnlyList<Point2d> polygon, double top)
    {
        var spans = ContourGraph.Edges(polygon)
            .Where(edge => Math.Abs(edge.Start.Y - top) <= Tolerance && Math.Abs(edge.End.Y - top) <= Tolerance)
            .Select(edge => new PolygonRay.Interval(Math.Min(edge.Start.X, edge.End.X), Math.Max(edge.Start.X, edge.End.X)))
            .OrderBy(span => span.Start).ToList();
        var merged = new List<PolygonRay.Interval>();
        foreach (var span in spans)
        {
            if (span.End - span.Start <= Tolerance) { continue; }
            if (merged.Count > 0 && span.Start <= merged[merged.Count - 1].End + Tolerance)
            { merged[merged.Count - 1] = new PolygonRay.Interval(merged[merged.Count - 1].Start, Math.Max(merged[merged.Count - 1].End, span.End)); }
            else { merged.Add(span); }
        }
        return merged;
    }

    private static bool Filled(double left, double right, double bottom, double top, IReadOnlyList<List<Point2d>> material)
    {
        if (top - bottom <= Tolerance) { return false; }
        // Horizontal intersections vary linearly between polygon vertex levels.
        // Check both sides of each level and its middle, so gaps, detached
        // overhead contours and mid-height side branches cannot be confused.
        var levels = new List<double> { bottom };
        foreach (var y in material.SelectMany(ring => ring).Select(p => p.Y)
            .Where(y => y > bottom + Tolerance && y < top - Tolerance).OrderBy(y => y))
        {
            if (y - levels[levels.Count - 1] > Tolerance) { levels.Add(y); }
        }
        levels.Add(top);
        for (var i = 1; i < levels.Count; i++)
        {
            var height = levels[i] - levels[i - 1];
            if (height <= 1e-7) { continue; }
            var epsilon = Math.Min(Tolerance / 2, height / 4);
            foreach (var y in new[] { levels[i - 1] + epsilon, (levels[i - 1] + levels[i]) / 2, levels[i] - epsilon })
            {
                if (!CoversWidth(y)) { return false; }
            }
        }
        return true;

        bool CoversWidth(double y)
        {
            var spans = new List<PolygonRay.Interval>();
            foreach (var ring in material)
            {
                var cuts = new List<double>();
                foreach (var edge in ContourGraph.Edges(ring))
                {
                    if ((edge.Start.Y > y) == (edge.End.Y > y)) { continue; }
                    cuts.Add(edge.Start.X + (y - edge.Start.Y) * (edge.End.X - edge.Start.X) / (edge.End.Y - edge.Start.Y));
                }
                cuts.Sort();
                for (var j = 1; j < cuts.Count; j += 2) { spans.Add(new PolygonRay.Interval(cuts[j - 1], cuts[j])); }
            }
            var covered = left;
            foreach (var span in spans.OrderBy(span => span.Start))
            {
                if (span.End < covered) { continue; }
                if (span.Start > covered + Tolerance) { return false; }
                covered = Math.Max(covered, span.End);
                if (covered >= right - Tolerance) { return true; }
            }
            return false;
        }
    }
}
