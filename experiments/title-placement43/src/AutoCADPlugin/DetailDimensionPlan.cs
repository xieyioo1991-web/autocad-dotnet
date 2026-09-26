using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Box = AutoCADPlugin.SupportLabelLayout.Box;
using Edge = AutoCADPlugin.ContourGraph.Edge;

namespace AutoCADPlugin;

internal static class DetailDimensionPlan
{
    internal sealed class Item
    {
        public Item(Point2d first, Point2d second, bool horizontal, bool local, bool datum = false)
        { First = first; Second = second; Horizontal = horizontal; Local = local; Datum = datum; }
        public Point2d First { get; }
        public Point2d Second { get; }
        public bool Horizontal { get; }
        public bool Local { get; }
        public bool Datum { get; }
        public double Length => Math.Abs(Coordinate(First, Horizontal) - Coordinate(Second, Horizontal));
    }

    internal sealed class Plan
    {
        public List<Item> Items { get; } = new List<Item>();
        public Box Bounds { get; set; }
        public int HorizontalAxes { get; set; }
        public int VerticalAxes { get; set; }
    }

    public static Plan Create(IReadOnlyList<Entity> entities)
    {
        // No reinforcement offsets: dimensions reference the actual contour.
        var outline = OffsetRebar.CreateOutline(entities);
        Point2d World(Point2d p) => new Point2d(p.X + outline.Origin.X, p.Y + outline.Origin.Y);
        var rings = outline.Boundaries.Select(r => r.Select(World).ToList()).ToList();
        var supports = outline.OriginalSupports.Select(r => r.Select(World).ToList()).ToList();
        var axes = entities.OfType<Line>().Where(e => DetailAnnotationIdentity.Role(e) == "Axis" || e.Layer == Standards.AxisLayer).ToList();
        var all = rings.SelectMany(r => r).Concat(supports.SelectMany(s => s)).ToList();
        var result = new Plan { Bounds = Box.Around(all), HorizontalAxes = axes.Count(a => Math.Abs(a.StartPoint.Y - a.EndPoint.Y) < .1),
            VerticalAxes = axes.Count(a => Math.Abs(a.StartPoint.X - a.EndPoint.X) < .1) };
        var horizontalMain = new List<Point2d>();
        var verticalMain = new List<Point2d>();
        foreach (var ring in rings)
        {
            var locals = LocalSteps(ring);
            foreach (var index in locals)
            {
                var a = ring[index]; var b = ring[(index + 1) % ring.Count]; var c = ring[(index + 2) % ring.Count];
                AddUnique(result.Items, new Item(a, b, Math.Abs(a.Y - b.Y) < .1, true));
                AddUnique(result.Items, new Item(b, c, Math.Abs(b.Y - c.Y) < .1, true));
            }
            // Keep major projections at the outside ends of a little step. The
            // tiny offsets themselves are dimensioned only by the local pair.
            var suppressed = new HashSet<int>(locals.Select(i => (i + 1) % ring.Count));
            var eligible = ring.Where((_, i) => !suppressed.Contains(i)).ToList();
            var orthogonalEdges = ContourGraph.Edges(ring).Where(e => Orthogonal(e.End - e.Start)).ToList();
            horizontalMain.AddRange(eligible.Where(p => orthogonalEdges.Any(e => p.GetDistanceTo(e.Start) < .1 || p.GetDistanceTo(e.End) < .1)));
            var horizontalEdges = orthogonalEdges.Where(e => Math.Abs(e.Start.Y - e.End.Y) < .1).ToList();
            verticalMain.AddRange(eligible.Where(p => horizontalEdges.Any(e => p.GetDistanceTo(e.Start) < .1 || p.GetDistanceTo(e.End) < .1)));
            // Sloping roof endpoints are not a set of horizontal component levels.
            // Keep the highest upright contact as a major level, not both roof faces.
            var uprights = orthogonalEdges.Where(e => Math.Abs(e.Start.X - e.End.X) < .1).SelectMany(e => new[] { e.Start, e.End }).ToList();
            if (uprights.Count > 0) { verticalMain.Add(uprights.OrderByDescending(p => p.Y).First()); }
        }
        // Support corners and axis coordinates are never discarded as "tiny".
        horizontalMain.AddRange(supports.SelectMany(s => s));
        verticalMain.AddRange(supports.SelectMany(s => s));
        foreach (var horizontal in new[] { true, false })
        {
            var candidates = new List<Point2d>(horizontal ? horizontalMain : verticalMain);
            foreach (var axis in axes)
            {
                if (horizontal && Math.Abs(axis.StartPoint.X - axis.EndPoint.X) < .1)
                { candidates.Add(new Point2d(axis.StartPoint.X, Math.Max(axis.StartPoint.Y, axis.EndPoint.Y))); }
                if (!horizontal && Math.Abs(axis.StartPoint.Y - axis.EndPoint.Y) < .1)
                { candidates.Add(new Point2d(Math.Min(axis.StartPoint.X, axis.EndPoint.X), axis.StartPoint.Y)); }
            }
            var points = DistinctStations(candidates, horizontal);
            for (var i = 1; i < points.Count; i++)
            {
                var item = new Item(points[i - 1], points[i], horizontal, false);
                if (result.Items.Any(local => local.Local && SameProjection(local, item))) { continue; }
                AddUnique(result.Items, item);
            }
            // Axis/support stations already locate the detail in this chain.
            // Do not add an overlapping second baseline for the same datum.
        }
        return result;
    }

    private static List<int> LocalSteps(List<Point2d> ring)
    {
        var result = new List<int>();
        if (ring.Count < 6) { return result; }
        for (var i = 0; i < ring.Count; i++)
        {
            var before = ring[i] - ring[(i + ring.Count - 1) % ring.Count];
            var a = ring[(i + 1) % ring.Count] - ring[i];
            var b = ring[(i + 2) % ring.Count] - ring[(i + 1) % ring.Count];
            var after = ring[(i + 3) % ring.Count] - ring[(i + 2) % ring.Count];
            if (!Orthogonal(a) || !Orthogonal(b) || a.Length < .1 || b.Length < .1) { continue; }
            if (Math.Abs(a.GetNormal().DotProduct(b.GetNormal())) > .001) { continue; }
            if (before.Length > 2 * b.Length && after.Length > 2 * a.Length &&
                Math.Abs(before.GetNormal().DotProduct(b.GetNormal())) > .999 && Math.Abs(after.GetNormal().DotProduct(a.GetNormal())) > .999)
            { result.Add(i); }
        }
        return result;
    }

    private static bool Orthogonal(Vector2d v) => Math.Abs(v.X) < .1 || Math.Abs(v.Y) < .1;
    private static double Coordinate(Point2d p, bool horizontal) => horizontal ? p.X : p.Y;
    private static List<Point2d> DistinctStations(List<Point2d> points, bool horizontal) => points
        .GroupBy(p => Math.Round(Coordinate(p, horizontal), 1, MidpointRounding.AwayFromZero))
        .OrderBy(g => g.Key).Select(g => horizontal ? g.OrderByDescending(p => p.Y).First() : g.OrderBy(p => p.X).First()).ToList();
    private static bool SameProjection(Item a, Item b) => a.Horizontal == b.Horizontal &&
        Math.Abs(Math.Min(Coordinate(a.First, a.Horizontal), Coordinate(a.Second, a.Horizontal)) - Math.Min(Coordinate(b.First, b.Horizontal), Coordinate(b.Second, b.Horizontal))) < .1 &&
        Math.Abs(Math.Max(Coordinate(a.First, a.Horizontal), Coordinate(a.Second, a.Horizontal)) - Math.Max(Coordinate(b.First, b.Horizontal), Coordinate(b.Second, b.Horizontal))) < .1;
    private static void AddUnique(List<Item> items, Item item)
    { if (item.Length > .1 && !items.Any(other => SameProjection(other, item))) { items.Add(item); } }
}
