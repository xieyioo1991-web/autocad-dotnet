using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Geometry;

namespace AutoCADPlugin;

// Keep the original longitudinal paths unchanged. Trim only the temporary
// segments that are allowed to supply point-bar corners.
internal static class PointAnchorExclusion
{
    public static List<ContourGraph.Edge> Read(OffsetRebar.Plan context)
    {
        var result = new List<ContourGraph.Edge>();
        foreach (var end in context.AnchorEnds)
        {
            if (end.Kind != RebarAnchorage.AnchorKind.Bent && end.Kind != RebarAnchorage.AnchorKind.VerticalBent) { continue; }
            var points = new[] { end.Original }.Concat(end.Extension).ToList();
            for (var i = 1; i < points.Count; i++)
            {
                if (points[i - 1].GetDistanceTo(points[i]) > ContourGraph.Tolerance)
                { result.Add(new ContourGraph.Edge(points[i - 1], points[i])); }
            }
        }
        return result;
    }

    public static IEnumerable<ContourGraph.Edge> Subtract(ContourGraph.Edge edge, IReadOnlyList<ContourGraph.Edge> masks)
    {
        var direction = edge.End - edge.Start;
        var length = direction.Length;
        if (length <= ContourGraph.Tolerance) { yield break; }
        var unit = direction / length;
        var intervals = new List<(double Start, double End)>();
        foreach (var mask in masks)
        {
            if (Math.Abs(Cross(mask.Start - edge.Start, unit)) > ContourGraph.Tolerance ||
                Math.Abs(Cross(mask.End - edge.Start, unit)) > ContourGraph.Tolerance) { continue; }
            var a = (mask.Start - edge.Start).DotProduct(unit);
            var b = (mask.End - edge.Start).DotProduct(unit);
            var low = Math.Max(0, Math.Min(a, b));
            var high = Math.Min(length, Math.Max(a, b));
            if (high > low) { intervals.Add((low, high)); }
        }
        double cursor = 0;
        foreach (var interval in intervals.OrderBy(p => p.Start))
        {
            if (interval.Start > cursor + ContourGraph.Tolerance)
            { yield return new ContourGraph.Edge(edge.Start + unit * cursor, edge.Start + unit * interval.Start); }
            cursor = Math.Max(cursor, interval.End);
        }
        if (length > cursor + ContourGraph.Tolerance)
        { yield return new ContourGraph.Edge(edge.Start + unit * cursor, edge.End); }
    }

    public static bool IsUPath(RebarPath path, IReadOnlyList<RebarPath> uPaths) =>
        uPaths.Any(u => SamePoints(path, u, false) || SamePoints(path, u, true));

    private static bool SamePoints(RebarPath first, RebarPath second, bool reverse)
    {
        if (first.Points.Count != second.Points.Count || first.IsClosed != second.IsClosed) { return false; }
        for (var i = 0; i < first.Points.Count; i++)
        {
            var j = reverse ? second.Points.Count - 1 - i : i;
            if (first.Points[i].GetDistanceTo(second.Points[j]) > ContourGraph.Tolerance) { return false; }
        }
        return true;
    }

    private static double Cross(Vector2d a, Vector2d b) => a.X * b.Y - a.Y * b.X;
}
