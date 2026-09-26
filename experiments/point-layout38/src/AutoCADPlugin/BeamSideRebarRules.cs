using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Geometry;

namespace AutoCADPlugin;

internal static class BeamSideRebarRules
{
    public const double SmallRegionSize = 600;
    private const double Tolerance = ContourGraph.Tolerance;

    internal sealed class Result
    {
        public List<RebarPath> Bars { get; } = new List<RebarPath>();
        public List<string> Notices { get; } = new List<string>();
        public int PairCount { get; set; }
        public int SuppressedRegionCount { get; set; }
        public int ShortenedReturnCount { get; set; }
    }

    private sealed class End
    {
        public End(int pathIndex, bool atStart, Point2d point, int direction, SupportContact contact)
        {
            PathIndex = pathIndex; AtStart = atStart; Point = point; Direction = direction;
            Support = contact.SupportIndex;
            FaceX = (contact.Boundary.Start.X + contact.Boundary.End.X) / 2;
            Bottom = Math.Min(contact.Boundary.Start.Y, contact.Boundary.End.Y);
            Top = Math.Max(contact.Boundary.Start.Y, contact.Boundary.End.Y);
        }
        public int PathIndex { get; }
        public bool AtStart { get; }
        public Point2d Point { get; }
        public int Direction { get; }
        public int Support { get; }
        public double FaceX { get; }
        public double Bottom { get; }
        public double Top { get; }
    }

    public static Result Apply(IReadOnlyList<RebarPath> paths, IReadOnlyList<List<Point2d>> boundaries,
        IReadOnlyList<List<Point2d>> supports, IReadOnlyList<BeamAnchorGuide> beamGuides, double inset)
    {
        var result = new Result();
        var beams = new HashSet<int>(beamGuides.Select(guide => guide.SupportIndex));
        var startRules = paths.Select(path => path.StartRule).ToArray();
        var endRules = paths.Select(path => path.EndRule).ToArray();
        var smallRegions = new HashSet<int>();
        foreach (var group in Enumerable.Range(0, paths.Count).GroupBy(index => paths[index].RegionIndex))
        {
            if (group.Key < 0 || group.Key >= boundaries.Count) { continue; }
            var indices = group.ToList();
            var boundary = boundaries[group.Key];
            MarkPairs(indices, paths, boundary, supports, beams, startRules, endRules, result);
            if (DrawingPrecision.LessThan(boundary.Max(p => p.X) - boundary.Min(p => p.X), SmallRegionSize) &&
                DrawingPrecision.LessThan(boundary.Max(p => p.Y) - boundary.Min(p => p.Y), SmallRegionSize) &&
                indices.Any(index => paths[index].StartContacts.Concat(paths[index].EndContacts).Any(contact => IsBeamSide(contact, beams))))
            { smallRegions.Add(group.Key); }
        }
        var removedRegions = new HashSet<int>();
        for (var i = 0; i < paths.Count; i++)
        {
            var path = paths[i];
            var marked = new RebarPath(path.Points, path.IsClosed, path.StartContacts, path.EndContacts,
                path.RegionIndex, startRules[i], endRules[i]);
            if (!smallRegions.Contains(path.RegionIndex)) { result.Bars.Add(marked); continue; }
            var lowerEdges = ContourGraph.Edges(boundaries[path.RegionIndex]).Where(edge => edge.End.X - edge.Start.X > Tolerance).ToList();
            var trimmed = LowerRebarRemoval.Apply(marked, lowerEdges, inset, out var removed);
            foreach (var piece in trimmed)
            {
                // The small-region rule overrides the ordinary upper-bend rule,
                // including unpaired inclined upper bars entering this beam side.
                var startRule = SmallRule(piece.StartRule, piece.StartContacts);
                var endRule = SmallRule(piece.EndRule, piece.EndContacts);
                var straight = new RebarPath(piece.Points, piece.IsClosed, piece.StartContacts, piece.EndContacts,
                    piece.RegionIndex, startRule, endRule);
                var shortenedPath = SmallRegionRebar.ShortenReturns(straight, out var shortened);
                result.ShortenedReturnCount += shortened;
                if (shortenedPath != null) { result.Bars.Add(shortenedPath); }
            }
            if (removed) { removedRegions.Add(path.RegionIndex); }
        }
        result.SuppressedRegionCount = removedRegions.Count;
        return result;

        RebarEndRule SmallRule(RebarEndRule rule, IReadOnlyList<SupportContact> contacts) =>
            rule != RebarEndRule.Free && contacts.Any(contact => IsBeamSide(contact, beams)) ? RebarEndRule.SmallStraight : rule;
    }

    private static void MarkPairs(List<int> indices, IReadOnlyList<RebarPath> paths, List<Point2d> boundary,
        IReadOnlyList<List<Point2d>> supports, HashSet<int> beams,
        RebarEndRule[] startRules, RebarEndRule[] endRules, Result result)
    {
        var pending = new List<End>();
        foreach (var index in indices)
        {
            var path = paths[index];
            if (path.IsClosed) { continue; }
            AddEnd(index, true, path, boundary, supports, beams, pending);
            AddEnd(index, false, path, boundary, supports, beams, pending);
        }
        while (pending.Count > 0)
        {
            var component = new List<End> { pending[0] };
            pending.RemoveAt(0);
            for (var i = 0; i < component.Count; i++)
            {
                for (var j = pending.Count - 1; j >= 0; j--)
                {
                    if (!SameInterface(component[i], pending[j])) { continue; }
                    component.Add(pending[j]); pending.RemoveAt(j);
                }
            }
            // Pair only a unique top/bottom combination on this finite contact.
            if (component.Count != 2)
            {
                if (component.Count > 2) { result.Notices.Add("同一区域的同一梁侧接口存在多于两个水平断口，未强行配对，继续普通锚固。"); }
                continue;
            }
            var ordered = component.OrderBy(end => end.Point.Y).ToList();
            if (ordered[1].Point.Y - ordered[0].Point.Y <= Tolerance) { continue; }
            SetRule(ordered[0], RebarEndRule.LowerStraight);
            SetRule(ordered[1], RebarEndRule.UpperBend);
            result.PairCount++;
        }

        void SetRule(End end, RebarEndRule rule)
        {
            if (end.AtStart) { startRules[end.PathIndex] = rule; }
            else { endRules[end.PathIndex] = rule; }
        }
    }

    private static void AddEnd(int index, bool atStart, RebarPath path, List<Point2d> boundary,
        IReadOnlyList<List<Point2d>> supports, HashSet<int> beams, List<End> ends)
    {
        var point = path.Points[atStart ? 0 : path.Points.Count - 1];
        var neighbor = path.Points[atStart ? 1 : path.Points.Count - 2];
        var vector = point - neighbor;
        if (Math.Abs(vector.X) <= Tolerance || Math.Abs(vector.Y) > Tolerance) { return; }
        var direction = Math.Sign(vector.X);
        var contacts = (atStart ? path.StartContacts : path.EndContacts).Where(contact => IsBeamSide(contact, beams)).ToList();
        var candidates = new List<End>();
        foreach (var contact in contacts)
        {
            var candidate = new End(index, atStart, point, direction, CompleteContact(contact, boundary, supports[contact.SupportIndex]));
            if (point.Y < candidate.Bottom - Tolerance || point.Y > candidate.Top + Tolerance ||
                (candidate.FaceX - point.X) * direction < -Tolerance) { continue; }
            var entry = new Point2d(candidate.FaceX, point.Y);
            if (PolygonRay.AvailableFrom(entry, new Vector2d(direction, 0), supports[candidate.Support]) <= Tolerance) { continue; }
            candidates.Add(candidate);
        }
        if (candidates.Count == 0) { return; }
        var nearest = candidates.OrderBy(end => Math.Abs(end.FaceX - point.X)).First();
        if (candidates.Any(end => end.Support != nearest.Support && Math.Abs(end.FaceX - nearest.FaceX) <= Tolerance)) { return; }
        ends.Add(nearest);
    }

    private static SupportContact CompleteContact(SupportContact contact, List<Point2d> boundary, List<Point2d> support)
    {
        var x = (contact.Boundary.Start.X + contact.Boundary.End.X) / 2;
        var low = Math.Min(contact.Boundary.Start.Y, contact.Boundary.End.Y);
        var high = Math.Max(contact.Boundary.Start.Y, contact.Boundary.End.Y);
        var intervals = new List<(double Low, double High)>();
        foreach (var edge in ContourGraph.Edges(boundary))
        {
            if (Math.Abs(edge.Start.X - x) > Tolerance || Math.Abs(edge.End.X - x) > Tolerance) { continue; }
            foreach (var side in ContourGraph.Edges(support))
            {
                if (Math.Abs(side.Start.X - x) > Tolerance || Math.Abs(side.End.X - x) > Tolerance) { continue; }
                var a = Math.Max(Math.Min(edge.Start.Y, edge.End.Y), Math.Min(side.Start.Y, side.End.Y));
                var b = Math.Min(Math.Max(edge.Start.Y, edge.End.Y), Math.Max(side.Start.Y, side.End.Y));
                if (b - a > Tolerance) { intervals.Add((a, b)); }
            }
        }
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var interval in intervals)
            {
                if (interval.Low > high + Tolerance || interval.High < low - Tolerance) { continue; }
                if (interval.Low < low) { low = interval.Low; changed = true; }
                if (interval.High > high) { high = interval.High; changed = true; }
            }
        }
        return new SupportContact(contact.SupportIndex, new ContourGraph.Edge(new Point2d(x, low), new Point2d(x, high)));
    }

    private static bool IsBeamSide(SupportContact contact, HashSet<int> beams) =>
        beams.Contains(contact.SupportIndex) && Math.Abs(contact.Boundary.Start.X - contact.Boundary.End.X) <= Tolerance &&
        Math.Abs(contact.Boundary.Start.Y - contact.Boundary.End.Y) > Tolerance;

    private static bool SameInterface(End a, End b) => a.Support == b.Support && a.Direction == b.Direction &&
        Math.Abs(a.FaceX - b.FaceX) <= Tolerance && Math.Max(a.Bottom, b.Bottom) <= Math.Min(a.Top, b.Top) + Tolerance;
}
