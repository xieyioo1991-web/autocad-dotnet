using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Geometry;

namespace AutoCADPlugin;

internal static class PolygonRay
{
    internal readonly struct Interval
    {
        public Interval(double start, double end) { Start = start; End = end; }
        public double Start { get; }
        public double End { get; }
    }

    // Intersect the polygon analytically. Every interval lies continuously
    // inside; concave gaps cannot be skipped by looking only at a distant tip.
    public static List<Interval> InsideIntervals(Point2d origin, Vector2d direction, IReadOnlyList<Point2d> polygon)
    {
        var cuts = new List<double> { 0 };
        foreach (var edge in ContourGraph.Edges(polygon))
        {
            var vector = edge.End - edge.Start;
            var length = vector.Length;
            if (length <= ContourGraph.Tolerance) { continue; }
            var delta = edge.Start - origin;
            var denominator = Cross(direction, vector);
            if (Math.Abs(denominator) > 1e-10 * length)
            {
                var distance = Cross(delta, vector) / denominator;
                var fraction = Cross(delta, direction) / denominator;
                if (distance >= -ContourGraph.Tolerance && fraction >= -1e-10 && fraction <= 1 + 1e-10)
                { cuts.Add(Math.Max(0, distance)); }
            }
            else if (Math.Abs(Cross(direction, delta)) <= ContourGraph.Tolerance)
            {
                AddCollinear(edge.Start);
                AddCollinear(edge.End);
            }
        }
        var sorted = new List<double>();
        foreach (var value in cuts.OrderBy(value => value))
        {
            if (sorted.Count == 0 || value - sorted[sorted.Count - 1] > 1e-7) { sorted.Add(value); }
        }
        var result = new List<Interval>();
        for (var i = 1; i < sorted.Count; i++)
        {
            var start = sorted[i - 1];
            var end = sorted[i];
            if (end - start <= ContourGraph.Tolerance) { continue; }
            var sample = origin + direction * ((start + end) / 2);
            if (!ContourGraph.Contains(polygon, sample) || ContourGraph.Edges(polygon).Any(edge => ContourGraph.Distance(sample, edge) < 1e-7))
            { continue; }
            if (result.Count > 0 && Math.Abs(result[result.Count - 1].End - start) <= 1e-7)
            { result[result.Count - 1] = new Interval(result[result.Count - 1].Start, end); }
            else { result.Add(new Interval(start, end)); }
        }
        return result;

        void AddCollinear(Point2d point)
        {
            var distance = (point - origin).DotProduct(direction);
            if (distance >= -ContourGraph.Tolerance) { cuts.Add(Math.Max(0, distance)); }
        }
    }

    public static double AvailableFrom(Point2d origin, Vector2d direction, IReadOnlyList<Point2d> polygon)
    {
        var interval = InsideIntervals(origin, direction, polygon).FirstOrDefault();
        return interval.Start <= ContourGraph.Tolerance ? interval.End : 0;
    }

    private static double Cross(Vector2d a, Vector2d b) => a.X * b.Y - a.Y * b.X;
}
