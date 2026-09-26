using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Geometry;

namespace AutoCADPlugin;

internal static class PointFixedCorners
{
    internal sealed class Segment
    {
        public ContourGraph.Edge Edge { get; }
        public RebarPath Path { get; }
        public int Index { get; }
        public int Count { get; }

        public Segment(ContourGraph.Edge edge, RebarPath path, int index, int count)
        { Edge = edge; Path = path; Index = index; Count = count; }
    }

    // Preserve source path adjacency through anchorage subtraction. An internal
    // crossing alone does not create a fixed corner, including the old red dot.
    public static bool IsFixed(Segment first, Segment second, Point2d vertex,
        IReadOnlyList<ContourGraph.Edge> completeEdges)
    {
        if (!AtEnd(first.Edge, vertex) || !AtEnd(second.Edge, vertex)) { return false; }
        if (ReferenceEquals(first.Path, second.Path))
        {
            var difference = Math.Abs(first.Index - second.Index);
            return difference == 1 || first.Path.IsClosed && difference == first.Count - 1;
        }

        // Separate bars can meet at an L corner. Three or more incident
        // directions identify a T/X junction instead.
        var directions = new List<Vector2d>();
        // Anchors cannot supply corners, but remain real incident directions.
        // Subtracting their masks here would turn an actual T back into an L.
        foreach (var edge in completeEdges)
        {
            if (ContourGraph.Distance(vertex, edge) > ContourGraph.Tolerance) { continue; }
            foreach (var end in PointBarJunction.Rays(edge, vertex))
            {
                var direction = (end - vertex).GetNormal();
                if (!directions.Any(d => d.DotProduct(direction) > 1 - 1e-8)) { directions.Add(direction); }
            }
        }
        return directions.Count == 2;
    }

    private static bool AtEnd(ContourGraph.Edge edge, Point2d vertex) =>
        vertex.GetDistanceTo(edge.Start) <= ContourGraph.Tolerance ||
        vertex.GetDistanceTo(edge.End) <= ContourGraph.Tolerance;
}
