using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Geometry;

namespace AutoCADPlugin;

internal static class RebarAnchorage
{
    public const double StraightLength = 1200;
    public const double MinimumRemaining = 400;
    public const double BendLead = 800;
    public const double BendLeg = 400;
    private const double NumericTolerance = 1e-6;

    internal enum AnchorKind { Straight, Bent, Failed }

    internal sealed class EndResult
    {
        public EndResult(int pathIndex, bool atStart, Point2d original, AnchorKind kind,
            IEnumerable<Point2d> extension, int supportIndex, string reason = "")
        {
            PathIndex = pathIndex;
            AtStart = atStart;
            Original = original;
            Kind = kind;
            Extension = extension.ToList().AsReadOnly();
            SupportIndex = supportIndex;
            Reason = reason;
        }

        public int PathIndex { get; }
        public bool AtStart { get; }
        public Point2d Original { get; }
        public AnchorKind Kind { get; }
        // Ordered outwards: support entry, then straight tip OR elbow and tip.
        public IReadOnlyList<Point2d> Extension { get; }
        public int SupportIndex { get; }
        public string Reason { get; }
    }

    internal sealed class Result
    {
        public List<RebarPath> Bars { get; } = new List<RebarPath>();
        public List<EndResult> Ends { get; } = new List<EndResult>();
    }

    private sealed class Entry
    {
        public Entry(int supportIndex, Point2d point, double distance, double depth)
        { SupportIndex = supportIndex; Point = point; Distance = distance; Depth = depth; }
        public int SupportIndex { get; }
        public Point2d Point { get; }
        public double Distance { get; }
        public double Depth { get; }
    }

    public static Result Apply(IReadOnlyList<RebarPath> paths, IReadOnlyList<List<Point2d>> supports)
    {
        var result = new Result();
        for (var i = 0; i < paths.Count; i++)
        {
            var path = paths[i];
            if (path.IsClosed) { result.Bars.Add(path); continue; }
            if (path.Points.Count < 2) { throw new InvalidOperationException("纵筋路径退化，无法确定锚固方向。"); }
            var start = Extend(i, true, path, supports);
            var end = Extend(i, false, path, supports);
            result.Ends.Add(start);
            result.Ends.Add(end);
            var points = new List<Point2d>();
            foreach (var point in start.Extension.Reverse().Concat(path.Points).Concat(end.Extension))
            {
                if (points.Count == 0 || points[points.Count - 1].GetDistanceTo(point) > NumericTolerance) { points.Add(point); }
            }
            result.Bars.Add(new RebarPath(points, false));
        }
        return result;
    }

    private static EndResult Extend(int pathIndex, bool atStart, RebarPath path, IReadOnlyList<List<Point2d>> supports)
    {
        var original = path.Points[atStart ? 0 : path.Points.Count - 1];
        var neighbor = path.Points[atStart ? 1 : path.Points.Count - 2];
        var contacts = atStart ? path.StartContacts : path.EndContacts;
        var vector = original - neighbor;
        if (vector.Length <= NumericTolerance) { return Fail("断口相邻顶点重合，无法确定延伸方向。"); }
        var direction = vector.GetNormal();
        var entries = FindEntries(original, direction, contacts, supports);
        if (entries.Count == 0) { return Fail("沿钢筋原方向不能从该接口进入对应支撑，保留原断口。"); }
        var entry = entries.OrderBy(candidate => candidate.Distance).First();
        if (entries.Any(candidate => candidate.SupportIndex != entry.SupportIndex && Math.Abs(candidate.Distance - entry.Distance) <= ContourGraph.Tolerance))
        { return Fail("断口同时进入多个重叠支撑，无法唯一确定锚固区域。"); }
        if (entry.Depth - StraightLength > MinimumRemaining + NumericTolerance)
        {
            return new EndResult(pathIndex, atStart, original, AnchorKind.Straight,
                new[] { entry.Point, entry.Point + direction * StraightLength }, entry.SupportIndex);
        }
        if (entry.Depth < BendLead - NumericTolerance)
        { return Fail($"支撑内沿原方向仅有{entry.Depth:F1}，放不下800直段，保留原断口。"); }

        var elbow = entry.Point + direction * BendLead;
        var left = new Vector2d(-direction.Y, direction.X);
        var polygon = supports[entry.SupportIndex];
        var leftSpace = PolygonRay.AvailableFrom(elbow, left, polygon);
        var rightSpace = PolygonRay.AvailableFrom(elbow, -left, polygon);
        var available = Math.Max(leftSpace, rightSpace);
        if (available < BendLeg - NumericTolerance)
        { return Fail($"800直段之后两侧最多只有{available:F1}，放不下400弯段，保留原断口。"); }
        // Equal room has no preferred side in the user's rule; choose left
        // (counter-clockwise) deterministically. Never cross a concave gap.
        var bendDirection = leftSpace >= rightSpace ? left : -left;
        return new EndResult(pathIndex, atStart, original, AnchorKind.Bent,
            new[] { entry.Point, elbow, elbow + bendDirection * BendLeg }, entry.SupportIndex);

        EndResult Fail(string reason) => new EndResult(pathIndex, atStart, original,
            AnchorKind.Failed, Array.Empty<Point2d>(), -1, reason);
    }

    private static List<Entry> FindEntries(Point2d origin, Vector2d direction,
        IReadOnlyList<SupportContact> contacts, IReadOnlyList<List<Point2d>> supports)
    {
        var result = new List<Entry>();
        foreach (var group in contacts.GroupBy(contact => contact.SupportIndex))
        {
            if (group.Key < 0 || group.Key >= supports.Count) { throw new InvalidOperationException("断口的支撑关联已失效。"); }
            foreach (var interval in PolygonRay.InsideIntervals(origin, direction, supports[group.Key]))
            {
                var entry = origin + direction * interval.Start;
                if (!group.Any(contact => ContourGraph.Distance(entry, contact.Boundary) <= ContourGraph.Tolerance)) { continue; }
                result.Add(new Entry(group.Key, entry, interval.Start, interval.End - interval.Start));
                break;
            }
        }
        return result;
    }
}
