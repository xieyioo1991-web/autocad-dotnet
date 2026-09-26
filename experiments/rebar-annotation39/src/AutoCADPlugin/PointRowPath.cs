using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Geometry;

namespace AutoCADPlugin;

// A row follows an entire longitudinal path, including its unreserved bends.
// Segment vertices are never mistaken for free ends.
internal sealed class PointRowPath
{
    public List<Point2d> Points { get; } = new List<Point2d>();
    public Point2d? FreeStart { get; set; }
    public Point2d? FreeEnd { get; set; }
    public bool Closed { get; set; }
    public double Length => Segments.Sum(e => e.Start.GetDistanceTo(e.End));
    private IEnumerable<ContourGraph.Edge> Segments => Enumerable.Range(1, Points.Count - 1)
        .Select(i => new ContourGraph.Edge(Points[i - 1], Points[i]));

    public Point2d At(double distance)
    {
        foreach (var edge in Segments)
        {
            var vector = edge.End - edge.Start;
            if (vector.Length < 1e-6) { continue; }
            if (distance <= vector.Length) { return edge.Start + vector.GetNormal() * Math.Max(0, distance); }
            distance -= vector.Length;
        }
        return Points[Points.Count - 1];
    }

    public bool Locate(Point2d point, out double distance)
    {
        distance = 0;
        foreach (var edge in Segments)
        {
            var vector = edge.End - edge.Start;
            if (vector.Length < 1e-6) { continue; }
            if (PointRowGeometry.OnRow(point, edge.Start, vector.GetNormal(), vector.Length, out var t))
            { distance += Math.Max(0, Math.Min(vector.Length, t)); return true; }
            distance += vector.Length;
        }
        return false;
    }

    public double EndClearance(bool fromStart)
    {
        var endpoint = fromStart ? FreeStart : FreeEnd;
        if (!endpoint.HasValue) { return 0; }
        var vertices = fromStart ? Points.ToList() : Points.AsEnumerable().Reverse().ToList();
        var total = 0.0;
        for (var i = 1; i < vertices.Count; i++)
        {
            var axis = vertices[i] - vertices[i - 1];
            if (axis.Length < 1e-6) { continue; }
            var unit = axis.GetNormal();
            var delta = vertices[i - 1] - endpoint.Value;
            var projection = delta.DotProduct(unit);
            var discriminant = projection * projection - delta.DotProduct(delta) + 400 * 400;
            if (discriminant >= 0)
            {
                var t = -projection + Math.Sqrt(discriminant);
                if (t >= 0 && t <= axis.Length) { return total + t; }
            }
            total += axis.Length;
        }
        return double.PositiveInfinity;
    }

    public static List<PointRowPath> Offset(IReadOnlyList<ContourGraph.Edge> edges, double offset, RebarPath source)
    {
        var result = new List<PointRowPath>();
        var group = new List<ContourGraph.Edge>();
        foreach (var edge in edges)
        {
            if (group.Count > 0 && group[group.Count - 1].End.GetDistanceTo(edge.Start) > .1) { Flush(); }
            group.Add(edge);
        }
        Flush();
        return result;

        void Flush()
        {
            if (group.Count == 0) { return; }
            var closed = group[0].Start.GetDistanceTo(group[group.Count - 1].End) <= .1;
            var shifted = group.Select(edge =>
            {
                var direction = (edge.End - edge.Start).GetNormal();
                var normal = new Vector2d(-direction.Y, direction.X) * offset;
                return new ContourGraph.Edge(edge.Start + normal, edge.End + normal);
            }).ToList();
            var path = new PointRowPath { Closed = closed };
            path.Points.Add(closed ? Joint(shifted[shifted.Count - 1], shifted[0]) : shifted[0].Start);
            for (var i = 1; i < shifted.Count; i++) { path.Points.Add(Joint(shifted[i - 1], shifted[i])); }
            path.Points.Add(closed ? path.Points[0] : shifted[shifted.Count - 1].End);
            if (!source.IsClosed && group[0].Start.GetDistanceTo(source.Points[0]) <= .1) { path.FreeStart = group[0].Start; }
            if (!source.IsClosed && group[group.Count - 1].End.GetDistanceTo(source.Points[source.Points.Count - 1]) <= .1)
            { path.FreeEnd = group[group.Count - 1].End; }
            result.Add(path);
            group.Clear();
        }
    }

    public List<PointRowPath> Clip(IReadOnlyList<List<Point2d>> material, IReadOnlyList<List<Point2d>> supports)
    {
        var result = new List<PointRowPath>();
        PointRowPath? current = null;
        foreach (var edge in Segments)
        {
            var vector = edge.End - edge.Start;
            if (vector.Length < 1e-6) { continue; }
            var direction = vector.GetNormal();
            foreach (var range in PointRowGeometry.Ranges(edge.Start, direction, vector.Length, material, supports))
            {
                var start = edge.Start + direction * range.Start;
                var end = edge.Start + direction * range.End;
                if (current == null || current.Points[current.Points.Count - 1].GetDistanceTo(start) > .1)
                { current = new PointRowPath(); current.Points.Add(start); result.Add(current); }
                current.Points.Add(end);
            }
        }
        if (Closed && result.Count > 1 && result[result.Count - 1].Points.Last().GetDistanceTo(result[0].Points[0]) <= .1)
        {
            var last = result[result.Count - 1];
            last.Points.AddRange(result[0].Points.Skip(1));
            result.RemoveAt(0);
        }
        foreach (var path in result)
        {
            path.Closed = path.Points[0].GetDistanceTo(path.Points.Last()) <= .1;
            if (path.Points[0].GetDistanceTo(Points[0]) <= .1) { path.FreeStart = FreeStart; }
            if (path.Points.Last().GetDistanceTo(Points.Last()) <= .1) { path.FreeEnd = FreeEnd; }
        }
        return result;
    }

    private static Point2d Joint(ContourGraph.Edge a, ContourGraph.Edge b)
    {
        var first = a.End - a.Start; var second = b.End - b.Start;
        var cross = first.X * second.Y - first.Y * second.X;
        if (Math.Abs(cross) < 1e-6) { return a.End; }
        var delta = b.Start - a.Start;
        return a.Start + first * ((delta.X * second.Y - delta.Y * second.X) / cross);
    }
}
