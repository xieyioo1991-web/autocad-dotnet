using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Geometry;

namespace AutoCADPlugin;

// Geometry is in the same local coordinates as OffsetRebar.Plan. Only the
// smaller wedge of two intersecting longitudinal segments can supply a corner.
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
        public int ExcludedAnchorCount { get; set; }
        public int InternalJunctionCount { get; set; }
    }

    public static Result Create(OffsetRebar.Plan context, IReadOnlyList<RebarPath> bars)
    {
        var material = context.Boundaries.Concat(context.BeamTops.Regions.Select(r => r.Polygon)).ToList();
        var union = ContourGraph.Build(material.SelectMany(ContourGraph.Edges).ToList(),
            new List<List<Point2d>>(), true);
        var anchorMasks = PointAnchorExclusion.Read(context);
        var cornerEdges = new List<PointFixedCorners.Segment>();
        var completeEdges = new List<ContourGraph.Edge>();
        foreach (var bar in bars)
        {
            var masks = PointAnchorExclusion.IsUPath(bar, context.BeamTops.Bars)
                ? new List<ContourGraph.Edge>() : anchorMasks;
            var sourceEdges = Segments(bar).ToList();
            completeEdges.AddRange(sourceEdges);
            for (var index = 0; index < sourceEdges.Count; index++)
            {
                foreach (var remaining in PointAnchorExclusion.Subtract(sourceEdges[index], masks))
                { cornerEdges.Add(new PointFixedCorners.Segment(remaining, bar, index, sourceEdges.Count)); }
            }
        }
        var candidates = new List<(Point2d Center, RebarPath First, RebarPath Second)>();
        var internalJunctions = new List<Point2d>();
        var result = new Result { ExcludedAnchorCount = context.AnchorEnds.Count(end =>
            end.Kind == RebarAnchorage.AnchorKind.Bent || end.Kind == RebarAnchorage.AnchorKind.VerticalBent ||
            end.Kind == RebarAnchorage.AnchorKind.BeamTopStraight) };
        for (var i = 0; i < cornerEdges.Count; i++)
        {
            for (var j = i + 1; j < cornerEdges.Count; j++)
            {
                var first = cornerEdges[i];
                var second = cornerEdges[j];
                if (!PointBarJunction.Intersect(first.Edge, second.Edge, out var vertex)) { continue; }
                if (!PointFixedCorners.IsFixed(first, second, vertex, completeEdges))
                {
                    if (!internalJunctions.Any(p => p.GetDistanceTo(vertex) <= ContourGraph.Tolerance))
                    { internalJunctions.Add(vertex); }
                    continue;
                }
                foreach (var a in PointBarJunction.Rays(first.Edge, vertex))
                {
                    foreach (var b in PointBarJunction.Rays(second.Edge, vertex))
                    {
                        if (!TryTangent(vertex, a, b, out var center)) { continue; }
                        // Closed longitudinal loops determine their own inside,
                        // even if the architectural outline is a larger box.
                        if (!InsideBar(first.Path, center) || !InsideBar(second.Path, center)) { continue; }
                        candidates.Add((center, first.Path, second.Path));
                    }
                }
            }
        }
        // Reserve the beam-top U corners first. Real crossed supports remain
        // exclusions; EffectiveSupports must never replace OriginalSupports here.
        foreach (var candidate in candidates.OrderByDescending(c => InExtension(c.Center))
            .ThenBy(c => c.Center.X).ThenBy(c => c.Center.Y))
        {
            var center = candidate.Center;
            if (result.Centers.Any(p => p.GetDistanceTo(center) <= ContourGraph.Tolerance)) { continue; }
            // The corner stays tangent to its own bars. Other crossing or
            // anchorage bars may overlap it graphically.
            var collisionEdges = Segments(candidate.First).Concat(Segments(candidate.Second));
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
        result.InternalJunctionCount = internalJunctions.Count;
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

    internal static IEnumerable<ContourGraph.Edge> Segments(RebarPath path)
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

    private static bool InsideBar(RebarPath path, Point2d center) =>
        !path.IsClosed || ContourGraph.Contains(path.Points, center);
}
