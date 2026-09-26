using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Geometry;

namespace AutoCADPlugin;

internal static class PointRowGeometry
{
    public const double EndClearance = 400;
    public const double MaximumSpacing = 800;
    private const double Epsilon = 1e-6;

    public static IEnumerable<double> Divide(double start, double end)
    {
        var length = end - start;
        if (length <= Epsilon) { return new[] { start }; }
        var intervals = Math.Max(1, (int)Math.Ceiling((length - Epsilon) / MaximumSpacing));
        return Enumerable.Range(0, intervals + 1).Select(i => start + length * ((double)i / intervals));
    }

    public static List<PolygonRay.Interval> Ranges(Point2d origin, Vector2d direction, double length,
        IReadOnlyList<List<Point2d>> material, IReadOnlyList<List<Point2d>> supports)
    {
        var cuts = new List<double> { 0, length };
        foreach (var ring in material)
        {
            foreach (var edge in ContourGraph.Edges(ring)) { AddCapsuleCuts(origin, direction, edge, CornerPointRebar.Radius, length, cuts); }
        }
        foreach (var support in supports)
        {
            foreach (var edge in ContourGraph.Edges(support)) { AddCapsuleCuts(origin, direction, edge, EndClearance, length, cuts); }
        }
        var ordered = cuts.OrderBy(t => t).ToList();
        var result = new List<PolygonRay.Interval>();
        for (var i = 1; i < ordered.Count; i++)
        {
            var a = ordered[i - 1]; var b = ordered[i];
            if (b - a <= Epsilon) { continue; }
            var middle = origin + direction * ((a + b) / 2);
            if (!InsideMaterial(middle, material) || supports.Any(s => NearSupport(middle, s, EndClearance - Epsilon))) { continue; }
            if (result.Count > 0 && Math.Abs(result[result.Count - 1].End - a) < Epsilon)
            { result[result.Count - 1] = new PolygonRay.Interval(result[result.Count - 1].Start, b); }
            else { result.Add(new PolygonRay.Interval(a, b)); }
        }
        return result;
    }

    public static bool InsideMaterial(Point2d point, IReadOnlyList<List<Point2d>> rings) =>
        rings.Count(r => ContourGraph.Contains(r, point)) % 2 == 1 &&
        rings.SelectMany(ContourGraph.Edges).All(e => ContourGraph.Distance(point, e) >= CornerPointRebar.Radius - Epsilon);

    public static bool NearSupport(Point2d point, IReadOnlyList<Point2d> polygon, double radius) =>
        ContourGraph.Contains(polygon, point) || ContourGraph.Edges(polygon).Any(e => ContourGraph.Distance(point, e) < radius);

    public static bool OnRow(Point2d point, Point2d origin, Vector2d direction, double length, out double parameter)
    {
        var delta = point - origin;
        parameter = delta.DotProduct(direction);
        return Math.Abs(Cross(delta, direction)) <= ContourGraph.Tolerance &&
            parameter >= -ContourGraph.Tolerance && parameter <= length + ContourGraph.Tolerance;
    }

    private static void AddCapsuleCuts(Point2d origin, Vector2d direction, ContourGraph.Edge edge,
        double radius, double length, List<double> cuts)
    {
        var vector = edge.End - edge.Start;
        if (vector.Length <= Epsilon) { return; }
        var axis = vector.GetNormal();
        var normal = new Vector2d(-axis.Y, axis.X);
        var denominator = direction.DotProduct(normal);
        if (Math.Abs(denominator) > Epsilon)
        {
            foreach (var sign in new[] { -1, 1 })
            { Add(((edge.Start - origin).DotProduct(normal) + sign * radius) / denominator); }
        }
        foreach (var vertex in new[] { edge.Start, edge.End })
        {
            var delta = origin - vertex;
            var projection = delta.DotProduct(direction);
            var discriminant = projection * projection - delta.DotProduct(delta) + radius * radius;
            if (discriminant < 0) { continue; }
            Add(-projection - Math.Sqrt(discriminant));
            Add(-projection + Math.Sqrt(discriminant));
        }

        void Add(double t)
        {
            if (t <= Epsilon || t >= length - Epsilon) { return; }
            if (Math.Abs(ContourGraph.Distance(origin + direction * t, edge) - radius) <= .001) { cuts.Add(t); }
        }
    }

    private static double Cross(Vector2d a, Vector2d b) => a.X * b.Y - a.Y * b.X;
}
