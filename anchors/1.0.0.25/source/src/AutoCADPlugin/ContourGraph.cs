using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Geometry;

namespace AutoCADPlugin;

// Polygonize straight linework in a local XY plane. Split crossings and
// collinear overlaps before traversing half-edges; merge adjacent material faces.
internal static class ContourGraph
{
    public const double Tolerance = 0.1;

    internal readonly struct Edge
    {
        public Edge(Point2d start, Point2d end) { Start = start; End = end; }
        public Point2d Start { get; }
        public Point2d End { get; }
    }

    public static List<List<Point2d>> Build(List<Edge> input, List<List<Point2d>> supports)
    {
        if (input.Count > 2000) { throw new InvalidOperationException("试验版一次最多处理2000条轮廓边，请缩小选择范围。"); }
        var nodes = new List<Point2d>();
        var links = new HashSet<(int, int)>();
        foreach (var source in input)
        {
            if (source.Start.GetDistanceTo(source.End) <= Tolerance) { continue; }
            var cuts = new List<double> { 0, 1 };
            foreach (var other in input) { Split(source, other, cuts); }
            var sorted = cuts.OrderBy(t => t).ToList();
            for (var i = 1; i < sorted.Count; i++)
            {
                var a = At(source, sorted[i - 1]);
                var b = At(source, sorted[i]);
                if (a.GetDistanceTo(b) <= Tolerance) { continue; }
                var u = Node(a, nodes);
                var v = Node(b, nodes);
                if (u != v) { links.Add(u < v ? (u, v) : (v, u)); }
            }
        }
        var adjacent = nodes.Select(_ => new List<int>()).ToArray();
        foreach (var link in links)
        {
            adjacent[link.Item1].Add(link.Item2);
            adjacent[link.Item2].Add(link.Item1);
        }
        for (var i = 0; i < nodes.Count; i++)
        {
            var center = nodes[i];
            adjacent[i].Sort((a, b) => Angle(nodes[a] - center).CompareTo(Angle(nodes[b] - center)));
        }
        // Open annotation leaders have no bounded face. They are removed by
        // topology, not by assuming any fixed location, length, or slope.
        var removed = true;
        while (removed)
        {
            removed = false;
            for (var i = 0; i < nodes.Count; i++)
            {
                if (adjacent[i].Count != 1) { continue; }
                adjacent[adjacent[i][0]].Remove(i);
                adjacent[i].Clear();
                removed = true;
            }
        }
        var visited = new HashSet<(int, int)>();
        var materialEdges = new HashSet<(int, int)>();
        for (var u = 0; u < nodes.Count; u++)
        {
            foreach (var v in adjacent[u])
            {
                if (visited.Contains((u, v))) { continue; }
                var ring = Walk(u, v, adjacent, visited, links.Count * 2 + 1);
                var polygon = ring.Select(i => nodes[i]).ToList();
                if (Area(polygon) <= Tolerance) { continue; }
                var sample = InteriorPoint(polygon);
                if (supports.Any(s => Contains(s, sample))) { continue; }
                for (var i = 0; i < ring.Count; i++)
                {
                    var a = ring[i];
                    var b = ring[(i + 1) % ring.Count];
                    if (!materialEdges.Remove((b, a))) { materialEdges.Add((a, b)); }
                }
            }
        }
        // Material is on the left of each retained half-edge. At a shared
        // corner, follow the first clockwise outgoing edge from the incoming
        // edge's reverse direction. This keeps point-touching regions separate;
        // choosing by degree or by enumeration order cannot do that reliably.
        var successors = new Dictionary<(int, int), (int, int)>();
        var destinations = new HashSet<(int, int)>();
        foreach (var edge in materialEdges)
        {
            var neighbors = adjacent[edge.Item2];
            var incomingIndex = neighbors.IndexOf(edge.Item1);
            for (var step = 1; step <= neighbors.Count; step++)
            {
                var candidate = (edge.Item2, neighbors[(incomingIndex + neighbors.Count - step) % neighbors.Count]);
                if (!materialEdges.Contains(candidate)) { continue; }
                if (!destinations.Add(candidate))
                { throw new InvalidOperationException("轮廓交点无法分解为独立边界，请检查重叠或退化线段。"); }
                successors.Add(edge, candidate);
                break;
            }
            if (!successors.ContainsKey(edge))
            { throw new InvalidOperationException("配筋外边界存在断口，请检查轮廓闭合及支撑边界。"); }
        }
        var output = new List<List<Point2d>>();
        while (materialEdges.Count > 0)
        {
            var seed = materialEdges.First();
            var current = seed;
            var polygon = new List<Point2d>();
            var ringNodes = new HashSet<int>();
            do
            {
                if (!materialEdges.Remove(current) || !ringNodes.Add(current.Item1))
                { throw new InvalidOperationException("配筋边界存在自接触或退化环，本轮暂不支持此类内孔连接。"); }
                polygon.Add(nodes[current.Item1]);
                current = successors[current];
            } while (current != seed);
            if (polygon.Count < 3) { throw new InvalidOperationException("配筋边界退化，无法形成闭合区域。"); }
            output.Add(polygon);
        }
        if (output.Count == 0) { throw new InvalidOperationException("没有形成支撑区域以外的闭合配筋轮廓；请选全轮廓并检查缺口。"); }
        return output;
    }

    private static List<int> Walk(int u, int v, List<int>[] adjacent, HashSet<(int, int)> visited, int limit)
    {
        var start = (u, v);
        var result = new List<int>();
        do
        {
            if (!visited.Add((u, v))) { throw new InvalidOperationException("轮廓拓扑遍历失败。"); }
            result.Add(u);
            var next = adjacent[v];
            var index = next.IndexOf(u);
            var w = next[(index + next.Count - 1) % next.Count];
            u = v;
            v = w;
            if (result.Count > limit) { throw new InvalidOperationException("轮廓无法闭合。"); }
        } while ((u, v) != start);
        return result;
    }

    private static void Split(Edge a, Edge b, List<double> cuts)
    {
        var r = a.End - a.Start;
        var s = b.End - b.Start;
        var q = b.Start - a.Start;
        var cross = Cross(r, s);
        if (Math.Abs(cross) > 1e-9 * Math.Max(1, r.Length * s.Length))
        {
            var t = Cross(q, s) / cross;
            var u = Cross(q, r) / cross;
            if (t >= 0 && t <= 1 && u >= 0 && u <= 1) { cuts.Add(t); }
        }
        AddProjection(b.Start);
        AddProjection(b.End);
        void AddProjection(Point2d point)
        {
            var t = (point - a.Start).DotProduct(r) / r.DotProduct(r);
            if (t > 0 && t < 1 && point.GetDistanceTo(At(a, t)) <= Tolerance) { cuts.Add(t); }
        }
    }

    public static double Area(IReadOnlyList<Point2d> ring)
    {
        double area = 0;
        var origin = ring[0];
        for (var i = 0; i < ring.Count; i++)
        {
            area += Cross(ring[i] - origin, ring[(i + 1) % ring.Count] - origin);
        }
        return area / 2;
    }

    public static bool Contains(IReadOnlyList<Point2d> ring, Point2d point)
    {
        var inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var a = ring[i];
            var b = ring[j];
            if ((a.Y > point.Y) != (b.Y > point.Y) &&
                point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
            { inside = !inside; }
        }
        return inside;
    }

    public static double Distance(Point2d p, Edge edge)
    {
        var v = edge.End - edge.Start;
        if (v.Length <= 1e-10) { return p.GetDistanceTo(edge.Start); }
        var t = Math.Max(0, Math.Min(1, (p - edge.Start).DotProduct(v) / v.DotProduct(v)));
        return p.GetDistanceTo(At(edge, t));
    }

    public static IEnumerable<Edge> Edges(IReadOnlyList<Point2d> ring)
    {
        for (var i = 0; i < ring.Count; i++) { yield return new Edge(ring[i], ring[(i + 1) % ring.Count]); }
    }

    public static Point2d InteriorPoint(IReadOnlyList<Point2d> ring)
    {
        foreach (var edge in Edges(ring).OrderByDescending(e => e.Start.GetDistanceTo(e.End)))
        {
            var v = edge.End - edge.Start;
            var point = At(edge, .5) + new Vector2d(-v.Y, v.X).GetNormal() * .01;
            if (Contains(ring, point)) { return point; }
        }
        throw new InvalidOperationException("无法确定区域内部，可能存在自交或退化边。");
    }

    private static int Node(Point2d point, List<Point2d> nodes)
    {
        for (var i = 0; i < nodes.Count; i++) { if (nodes[i].GetDistanceTo(point) <= Tolerance) { return i; } }
        nodes.Add(point);
        return nodes.Count - 1;
    }

    private static Point2d At(Edge edge, double t) => edge.Start + (edge.End - edge.Start) * t;
    private static double Cross(Vector2d a, Vector2d b) => a.X * b.Y - a.Y * b.X;
    private static double Angle(Vector2d v) => Math.Atan2(v.Y, v.X);
}
