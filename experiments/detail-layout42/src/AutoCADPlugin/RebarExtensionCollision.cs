using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.Geometry;

namespace AutoCADPlugin;

// Centerline intersections. Existing anchored paths are immutable obstacles.
// New ends grow at equal speed so crossing extensions do not depend on entity
// order or stop against a proposed segment that was itself trimmed away.
internal static class RebarExtensionCollision
{
    private const double Tolerance = 1e-6;

    internal sealed class Extension
    {
        public Extension(Point2d origin, Vector2d direction, double length, int pathIndex, int vertexIndex)
        { Origin = origin; Direction = direction; Length = length; PathIndex = pathIndex; VertexIndex = vertexIndex; }
        public Point2d Origin { get; }
        public Vector2d Direction { get; }
        public double Length { get; private set; }
        public int PathIndex { get; }
        public int VertexIndex { get; }
        public bool Hit { get; private set; }
        public Point2d Tip => Origin + Direction * Length;
        public void Stop(double distance)
        {
            Length = Math.Max(0, Math.Min(Length, distance));
            Hit = true;
        }
    }

    private sealed class Meeting
    {
        public Meeting(Extension a, Extension b, double distanceA, double distanceB)
        { A = a; B = b; DistanceA = distanceA; DistanceB = distanceB; }
        public Extension A { get; }
        public Extension B { get; }
        public double DistanceA { get; }
        public double DistanceB { get; }
        public double Time => Math.Max(DistanceA, DistanceB);
    }

    public static void Trim(IReadOnlyList<Extension> ends, IReadOnlyList<RebarPath> bars)
    {
        foreach (var end in ends) { TrimAgainstPaths(end, bars); }
        var meetings = new List<Meeting>();
        for (var i = 0; i < ends.Count; i++)
        {
            for (var j = i + 1; j < ends.Count; j++)
            {
                var meeting = FindMeeting(ends[i], ends[j]);
                if (meeting is not null) { meetings.Add(meeting); }
            }
        }
        while (meetings.Count > 0)
        {
            meetings.Sort((a, b) => a.Time.CompareTo(b.Time));
            var next = meetings[0];
            meetings.RemoveAt(0);
            var actual = FindMeeting(next.A, next.B);
            if (actual is null) { continue; }
            // For collinear ends one may have stopped before meeting. The
            // other end can meet its stationary tip later, never its old tip.
            if (actual.Time > next.Time + Tolerance) { meetings.Add(actual); continue; }
            if (actual.DistanceA >= actual.DistanceB - Tolerance) { actual.A.Stop(actual.DistanceA); }
            if (actual.DistanceB >= actual.DistanceA - Tolerance) { actual.B.Stop(actual.DistanceB); }
        }
    }

    private static void TrimAgainstPaths(Extension end, IReadOnlyList<RebarPath> paths)
    {
        for (var pathIndex = 0; pathIndex < paths.Count; pathIndex++)
        {
            var path = paths[pathIndex];
            var count = path.Points.Count;
            for (var i = 0; i < count - (path.IsClosed ? 0 : 1); i++)
            {
                // Both sides of its own cut meet at distance zero; neither is
                // an obstacle to the two straight continuations of that cut.
                if (pathIndex == end.PathIndex && (i == end.VertexIndex || (i + 1) % count == end.VertexIndex)) { continue; }
                var distance = FirstIntersection(end, path.Points[i], path.Points[(i + 1) % count]);
                if (distance.HasValue) { end.Stop(distance.Value); }
            }
        }
    }

    private static double? FirstIntersection(Extension end, Point2d start, Point2d finish)
    {
        var vector = finish - start;
        if (vector.Length <= Tolerance) { return null; }
        var direction = vector.GetNormal();
        var divisor = Cross(end.Direction, direction);
        var delta = start - end.Origin;
        if (Math.Abs(divisor) <= 1e-10)
        {
            if (Math.Abs(Cross(delta, end.Direction)) > Tolerance) { return null; }
            var a = delta.DotProduct(end.Direction);
            var b = (finish - end.Origin).DotProduct(end.Direction);
            if (Math.Max(a, b) < -Tolerance) { return null; }
            var distance = Math.Max(0, Math.Min(a, b));
            return distance <= end.Length + Tolerance ? Math.Min(distance, end.Length) : (double?)null;
        }
        var along = Cross(delta, direction) / divisor;
        var across = Cross(delta, end.Direction) / divisor;
        if (along < -Tolerance || along > end.Length + Tolerance || across < -Tolerance || across > vector.Length + Tolerance) { return null; }
        return Math.Max(0, Math.Min(end.Length, along));
    }

    private static Meeting? FindMeeting(Extension a, Extension b)
    {
        // The two continuations of one cut share a start but grow apart.
        if (a.PathIndex == b.PathIndex && a.VertexIndex == b.VertexIndex) { return null; }
        var delta = b.Origin - a.Origin;
        var divisor = Cross(a.Direction, b.Direction);
        if (Math.Abs(divisor) > 1e-10)
        {
            var x = Cross(delta, b.Direction) / divisor;
            var y = Cross(delta, a.Direction) / divisor;
            if (x < -Tolerance || y < -Tolerance || x > a.Length + Tolerance || y > b.Length + Tolerance) { return null; }
            return new Meeting(a, b, Math.Max(0, Math.Min(a.Length, x)), Math.Max(0, Math.Min(b.Length, y)));
        }
        if (Math.Abs(Cross(delta, a.Direction)) > Tolerance) { return null; }
        var separation = delta.DotProduct(a.Direction);
        if (a.Direction.DotProduct(b.Direction) > 0)
        {
            if (separation >= 0 && separation <= a.Length + Tolerance) { return new Meeting(a, b, Math.Min(a.Length, separation), 0); }
            if (separation < 0 && -separation <= b.Length + Tolerance) { return new Meeting(a, b, 0, Math.Min(b.Length, -separation)); }
            return null;
        }
        if (separation < -Tolerance || separation > a.Length + b.Length + Tolerance) { return null; }
        var time = Math.Max(separation / 2, Math.Max(separation - a.Length, separation - b.Length));
        var distanceA = Math.Min(a.Length, time);
        return new Meeting(a, b, Math.Max(0, distanceA), Math.Max(0, Math.Min(b.Length, separation - distanceA)));
    }

    private static double Cross(Vector2d a, Vector2d b) => a.X * b.Y - a.Y * b.X;
}
