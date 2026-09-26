using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Geometry;

namespace AutoCADPlugin;

// Remove only the offset segments corresponding to a finite shared boundary
// with a support. Keep the other offset vertices; do not extend into supports.
internal static class SupportOpening
{
    private const double Tolerance = ContourGraph.Tolerance;

    private sealed class OffsetCap
    {
        public OffsetCap(ContourGraph.Edge edge, SupportContact contact) { Edge = edge; Contact = contact; }
        public ContourGraph.Edge Edge { get; }
        public SupportContact Contact { get; }
    }

    public static List<RebarPath> Create(IReadOnlyList<Point2d> boundary, List<Point2d> inset,
        IReadOnlyList<List<Point2d>> supports, double distance)
    {
        var caps = FindOffsetCaps(boundary, supports, distance);
        var parts = new List<(ContourGraph.Edge Edge, bool Removed)>();
        foreach (var edge in ContourGraph.Edges(inset))
        {
            SplitAtCaps(edge, caps, parts);
        }
        var gap = parts.FindIndex(part => part.Removed);
        if (gap < 0) { return new List<RebarPath> { new RebarPath(inset, true) }; }

        var result = new List<RebarPath>();
        var points = new List<Point2d>();
        // Start after a removed segment, so an open path can cross vertex zero
        // without being split or inadvertently reclosed there.
        for (var step = 1; step <= parts.Count; step++)
        {
            var part = parts[(gap + step) % parts.Count];
            if (part.Removed)
            {
                if (points.Count >= 2)
                {
                    var startContacts = caps.Where(cap => ContourGraph.Distance(points[0], cap.Edge) <= Tolerance).Select(cap => cap.Contact);
                    var endContacts = caps.Where(cap => ContourGraph.Distance(points[points.Count - 1], cap.Edge) <= Tolerance).Select(cap => cap.Contact);
                    result.Add(new RebarPath(points, false, startContacts, endContacts));
                }
                points.Clear();
                continue;
            }
            if (points.Count == 0) { points.Add(part.Edge.Start); }
            points.Add(part.Edge.End);
        }
        if (result.Count == 0)
        { throw new InvalidOperationException("该区域的偏移纵筋全部位于支撑接口，没有可保留的钢筋段，本次未生成。"); }
        return result;
    }

    private static List<OffsetCap> FindOffsetCaps(IReadOnlyList<Point2d> boundary,
        IReadOnlyList<List<Point2d>> supports, double distance)
    {
        var edges = ContourGraph.Edges(boundary).ToList();
        var supportEdges = supports.SelectMany((support, index) => ContourGraph.Edges(support).Select(edge => (Edge: edge, Index: index))).ToList();
        var result = new List<OffsetCap>();
        for (var i = 0; i < edges.Count; i++)
        {
            var edge = edges[i];
            var direction = (edge.End - edge.Start).GetNormal();
            var length = edge.Start.GetDistanceTo(edge.End);
            var normal = new Vector2d(-direction.Y, direction.X) * distance;
            var origin = edge.Start + normal; // Boundary is counter-clockwise.
            foreach (var support in supportEdges)
            {
                if (!Overlap(edge, support.Edge, out var start, out var end)) { continue; }
                var contact = new SupportContact(support.Index,
                    new ContourGraph.Edge(edge.Start + direction * start, edge.Start + direction * end));
                // A real corner uses the same miter as the closed offset. At a
                // collinear support transition the endpoint stays at its projection.
                if (start <= Tolerance)
                { start = JointParameter(edge, edges[(i + edges.Count - 1) % edges.Count], distance, 0); }
                if (end >= length - Tolerance)
                { end = JointParameter(edge, edges[(i + 1) % edges.Count], distance, length); }
                if (end - start > Tolerance)
                { result.Add(new OffsetCap(new ContourGraph.Edge(origin + direction * start, origin + direction * end), contact)); }
            }
        }
        return result;
    }

    private static double JointParameter(ContourGraph.Edge edge, ContourGraph.Edge neighbor, double distance, double fallback)
    {
        var direction = (edge.End - edge.Start).GetNormal();
        var otherDirection = (neighbor.End - neighbor.Start).GetNormal();
        var divisor = Cross(direction, otherDirection);
        if (Math.Abs(divisor) <= 1e-9) { return fallback; }
        var origin = edge.Start + new Vector2d(-direction.Y, direction.X) * distance;
        var otherOrigin = neighbor.Start + new Vector2d(-otherDirection.Y, otherDirection.X) * distance;
        return Cross(otherOrigin - origin, otherDirection) / divisor;
    }

    private static void SplitAtCaps(ContourGraph.Edge edge, List<OffsetCap> caps,
        List<(ContourGraph.Edge Edge, bool Removed)> parts)
    {
        var intervals = new List<(double Start, double End)>();
        foreach (var cap in caps)
        {
            if (Overlap(edge, cap.Edge, out var start, out var end)) { intervals.Add((start, end)); }
        }
        var length = edge.Start.GetDistanceTo(edge.End);
        var direction = (edge.End - edge.Start).GetNormal();
        var position = 0.0;
        foreach (var interval in intervals.OrderBy(interval => interval.Start))
        {
            if (interval.End <= position) { continue; }
            var start = interval.Start - position <= Tolerance ? position : interval.Start;
            var end = length - interval.End <= Tolerance ? length : interval.End;
            if (start > position) { AddPart(position, start, false); }
            AddPart(Math.Max(position, start), end, true);
            position = end;
        }
        AddPart(position, length, false);

        void AddPart(double start, double end, bool removed)
        {
            if (end - start <= 1e-8) { return; }
            parts.Add((new ContourGraph.Edge(edge.Start + direction * start, edge.Start + direction * end), removed));
        }
    }

    // Distances are along 'edge', so point-only contact has zero overlap.
    private static bool Overlap(ContourGraph.Edge edge, ContourGraph.Edge other, out double start, out double end)
    {
        start = 0;
        end = 0;
        var vector = edge.End - edge.Start;
        var otherVector = other.End - other.Start;
        if (vector.Length <= Tolerance || otherVector.Length <= Tolerance) { return false; }
        var direction = vector.GetNormal();
        var otherDirection = otherVector.GetNormal();
        // The contour graph already welds points within a drawing-distance
        // tolerance. A fixed 1e-8 angular cutoff was much stricter and could
        // reject those same shared edges after welding. Compare transverse
        // drift in drawing units instead, using the shorter finite edge.
        if (Math.Abs(Cross(direction, otherDirection)) * Math.Min(vector.Length, otherVector.Length) > Tolerance)
        { return false; }
        var a = (other.Start - edge.Start).DotProduct(direction);
        var b = (other.End - edge.Start).DotProduct(direction);
        start = Math.Max(0, Math.Min(a, b));
        end = Math.Min(vector.Length, Math.Max(a, b));
        if (end - start <= Tolerance) { return false; }
        // Measure at the shared span, not the remote ends of a long support.
        var startDistance = Math.Abs(Cross(otherDirection, edge.Start + direction * start - other.Start));
        var endDistance = Math.Abs(Cross(otherDirection, edge.Start + direction * end - other.Start));
        return startDistance <= Tolerance && endDistance <= Tolerance;
    }

    private static double Cross(Vector2d a, Vector2d b) => a.X * b.Y - a.Y * b.X;
}
