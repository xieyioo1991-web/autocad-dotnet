using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Geometry;

namespace AutoCADPlugin;

// Geometry is in the same local coordinates as OffsetRebar.Plan. Only the
// smaller wedge of two connected straight bars can supply a convex corner.
internal static class CornerPointRebar
{
    public const double Radius = 50;
    public const double TangentDistance = Radius + Standards.LongitudinalWidth / 2;
    private const double Epsilon = 1e-6;

    internal sealed class Result
    {
        public List<Point2d> Centers { get; } = new List<Point2d>();
        public int ExtensionCount { get; set; }
        public int RejectedCount { get; set; }
    }

    public static Result Create(OffsetRebar.Plan context, IReadOnlyList<RebarPath> bars)
    {
        var material = context.Boundaries.Concat(context.BeamTops.Regions.Select(r => r.Polygon)).ToList();
        var union = ContourGraph.Build(material.SelectMany(ContourGraph.Edges).ToList(),
            new List<List<Point2d>>(), true);
        var edges = bars.SelectMany(Segments).ToList();
        var uEdges = context.BeamTops.Bars.SelectMany(Segments).ToList();
        var uCorners = new List<Point2d>();
        foreach (var u in context.BeamTops.Bars)
        {
            for (var i = 1; i + 1 < u.Points.Count; i++)
            {
                if (TryTangent(u.Points[i], u.Points[i - 1], u.Points[i + 1], out var corner)) { uCorners.Add(corner); }
            }
        }
        var candidates = new List<Point2d>();
        var result = new Result();
        for (var i = 0; i < edges.Count; i++)
        {
            for (var j = i + 1; j < edges.Count; j++)
            {
                if (!SharedEnd(edges[i], edges[j], out var vertex, out var a, out var b)) { continue; }
                if (!TryTangent(vertex, a, b, out var center)) { continue; }
                if (candidates.Any(p => p.GetDistanceTo(center) < ContourGraph.Tolerance)) { continue; }
                candidates.Add(center);
            }
        }
        // Reserve the beam-top U corners first. Real crossed supports remain
        // exclusions; EffectiveSupports must never replace OriginalSupports here.
        foreach (var center in candidates.OrderByDescending(InExtension).ThenBy(p => p.X).ThenBy(p => p.Y))
        {
            // User-confirmed exception: the two U-top dots keep tangency to
            // the U itself even when other anchorage bars pass through them.
            var collisionEdges = uCorners.Any(p => p.GetDistanceTo(center) < ContourGraph.Tolerance) ? uEdges : edges;
            if (!FitsMaterial(center, union) || context.OriginalSupports.Any(s => IntersectsSupport(center, s)) ||
                collisionEdges.Any(e => ContourGraph.Distance(center, e) < TangentDistance - Epsilon) ||
                result.Centers.Any(p => p.GetDistanceTo(center) < Radius * 2 - Epsilon))
            {
                result.RejectedCount++;
                continue;
            }
            result.Centers.Add(center);
            if (InExtension(center)) { result.ExtensionCount++; }
        }
        return result;

        bool InExtension(Point2d point) => context.BeamTops.Regions.Any(r => ContourGraph.Contains(r.Polygon, point));
    }

    internal static bool TryTangent(Point2d vertex, Point2d endA, Point2d endB, out Point2d center)
    {
        center = vertex;
        var a = endA - vertex;
        var b = endB - vertex;
        if (a.Length <= Epsilon || b.Length <= Epsilon) { return false; }
        var u = a.GetNormal();
        var v = b.GetNormal();
        var dot = Math.Max(-1, Math.Min(1, u.DotProduct(v)));
        if (Math.Abs(dot) > 1 - 1e-10) { return false; }
        var sineHalf = Math.Sqrt((1 - dot) / 2);
        var direction = (u + v).GetNormal();
        center = vertex + direction * (TangentDistance / sineHalf);
        // Tangency must lie on both finite segments, not on imaginary extensions.
        var along = (center - vertex).DotProduct(u);
        return along <= a.Length + Epsilon && along <= b.Length + Epsilon;
    }

    internal static bool IntersectsSupport(Point2d center, IReadOnlyList<Point2d> support) =>
        ContourGraph.Contains(support, center) ||
        ContourGraph.Edges(support).Any(e => ContourGraph.Distance(center, e) < Radius - Epsilon);

    private static bool FitsMaterial(Point2d center, IReadOnlyList<List<Point2d>> rings) =>
        rings.Count(r => ContourGraph.Contains(r, center)) % 2 == 1 &&
        rings.SelectMany(ContourGraph.Edges).All(e => ContourGraph.Distance(center, e) >= Radius - Epsilon);

    private static IEnumerable<ContourGraph.Edge> Segments(RebarPath path)
    {
        var points = path.Points.ToList();
        // Collinear subdivisions must not turn a long side into an artificially
        // short segment for the finite tangency check.
        var changed = true;
        while (changed && points.Count > 2)
        {
            changed = false;
            for (var i = path.IsClosed ? 0 : 1; i < points.Count - (path.IsClosed ? 0 : 1); i++)
            {
                var previous = points[(i + points.Count - 1) % points.Count];
                var next = points[(i + 1) % points.Count];
                if (ContourGraph.Distance(points[i], new ContourGraph.Edge(previous, next)) > Epsilon) { continue; }
                points.RemoveAt(i);
                changed = true;
                break;
            }
        }
        for (var i = 0; i < points.Count - (path.IsClosed ? 0 : 1); i++)
        {
            var edge = new ContourGraph.Edge(points[i], points[(i + 1) % points.Count]);
            if (edge.Start.GetDistanceTo(edge.End) > Epsilon) { yield return edge; }
        }
    }

    private static bool SharedEnd(ContourGraph.Edge first, ContourGraph.Edge second,
        out Point2d vertex, out Point2d a, out Point2d b)
    {
        foreach (var reverseA in new[] { false, true })
        {
            foreach (var reverseB in new[] { false, true })
            {
                vertex = reverseA ? first.End : first.Start;
                var other = reverseB ? second.End : second.Start;
                if (vertex.GetDistanceTo(other) > Epsilon) { continue; }
                a = reverseA ? first.Start : first.End;
                b = reverseB ? second.Start : second.End;
                return true;
            }
        }
        vertex = a = b = Point2d.Origin;
        return false;
    }
}
