using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Geometry;

namespace AutoCADPlugin;

// Remove the inset segments corresponding to downward-facing material edges.
// Keep the upper bar and free-end returns; new cut ends intentionally do not anchor.
internal static class LowerRebarRemoval
{
    public static List<RebarPath> Apply(RebarPath path, IReadOnlyList<ContourGraph.Edge> lowerEdges,
        double inset, out bool removed)
    {
        var count = path.Points.Count - (path.IsClosed ? 0 : 1);
        var cuts = new bool[count];
        for (var i = 0; i < count; i++)
        {
            var start = path.Points[i]; var end = path.Points[(i + 1) % path.Points.Count];
            cuts[i] = lowerEdges.Any(edge => Matches(start, end, edge, inset));
        }
        removed = cuts.Any(cut => cut);
        if (!removed) { return new List<RebarPath> { path }; }
        var result = new List<RebarPath>();
        var points = new List<Point2d>();
        var first = path.IsClosed ? (Array.IndexOf(cuts, true) + 1) % count : 0;
        var firstVertex = -1; var lastVertex = -1;
        for (var step = 0; step < count; step++)
        {
            var index = (first + step) % count;
            if (cuts[index]) { Flush(); continue; }
            if (points.Count == 0) { firstVertex = index; points.Add(path.Points[index]); }
            lastVertex = (index + 1) % path.Points.Count;
            points.Add(path.Points[lastVertex]);
        }
        Flush();
        return result;

        void Flush()
        {
            if (points.Count < 2) { points.Clear(); return; }
            var originalStart = !path.IsClosed && firstVertex == 0;
            var originalEnd = !path.IsClosed && lastVertex == path.Points.Count - 1;
            result.Add(new RebarPath(points, false, originalStart ? path.StartContacts : null, originalEnd ? path.EndContacts : null,
                path.RegionIndex, originalStart ? path.StartRule : RebarEndRule.Free, originalEnd ? path.EndRule : RebarEndRule.Free));
            points.Clear();
        }
    }

    private static bool Matches(Point2d start, Point2d end, ContourGraph.Edge boundary, double inset)
    {
        var vector = boundary.End - boundary.Start;
        var direction = vector.GetNormal();
        var normal = new Vector2d(-direction.Y, direction.X);
        var origin = boundary.Start + normal * inset;
        if (Math.Abs((start - origin).DotProduct(normal)) > ContourGraph.Tolerance ||
            Math.Abs((end - origin).DotProduct(normal)) > ContourGraph.Tolerance) { return false; }
        var a = (start - origin).DotProduct(direction); var b = (end - origin).DotProduct(direction);
        return Math.Min(vector.Length, Math.Max(a, b)) - Math.Max(0, Math.Min(a, b)) > ContourGraph.Tolerance;
    }
}
