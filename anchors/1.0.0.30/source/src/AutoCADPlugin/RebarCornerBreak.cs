using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Geometry;

namespace AutoCADPlugin;

// Measure corners on the closed material boundary before the 50 inset.
// Map qualifying corners to their inset joints, never classify anchor elbows.
internal static class RebarCornerBreak
{
    public const double ExtensionLength = 400;
    public const double MinimumOutlineSideLength = 600;
    private const double NumericTolerance = 1e-6;

    internal sealed class Result
    {
        public List<RebarPath> Bars { get; } = new List<RebarPath>();
        public List<RebarExtensionCollision.Extension> Ends { get; } = new List<RebarExtensionCollision.Extension>();
        public int CornerCount { get; set; }
    }

    private sealed class Corner
    {
        public Corner(Point2d point, Vector2d incoming, Vector2d outgoing)
        { Point = point; Incoming = incoming; Outgoing = outgoing; }
        public Point2d Point { get; }
        public Vector2d Incoming { get; }
        public Vector2d Outgoing { get; }
    }

    private sealed class Piece
    {
        public Piece(RebarPath path, RebarExtensionCollision.Extension? start, RebarExtensionCollision.Extension? end)
        { Path = path; Start = start; End = end; }
        public RebarPath Path { get; }
        public RebarExtensionCollision.Extension? Start { get; }
        public RebarExtensionCollision.Extension? End { get; }
    }

    public static Result Apply(IReadOnlyList<RebarPath> anchoredBars,
        IReadOnlyList<List<Point2d>> materialBoundaries, double offsetDistance, IReadOnlyList<RebarPath>? fixedBars = null)
    {
        var result = new Result();
        var corners = FindCorners(materialBoundaries, offsetDistance);
        var pieces = new List<Piece>();
        for (var pathIndex = 0; pathIndex < anchoredBars.Count; pathIndex++)
        {
            var path = anchoredBars[pathIndex];
            var cuts = new List<int>();
            for (var i = path.IsClosed ? 0 : 1; i < path.Points.Count - (path.IsClosed ? 0 : 1); i++)
            {
                if (corners.Any(corner => Matches(path, i, corner))) { cuts.Add(i); }
            }
            result.CornerCount += cuts.Count;
            Split(path, pathIndex, cuts, pieces, result.Ends);
        }
        // Forced beam-top U bars can stop an extension, but never participate
        // in concave-corner splitting themselves.
        var obstacles = fixedBars is null ? anchoredBars : anchoredBars.Concat(fixedBars).ToList();
        RebarExtensionCollision.Trim(result.Ends, obstacles);
        foreach (var piece in pieces)
        {
            if (piece.Start is null && piece.End is null) { result.Bars.Add(piece.Path); continue; }
            var points = new List<Point2d>();
            if (piece.Start is not null && piece.Start.Length > NumericTolerance) { points.Add(piece.Start.Tip); }
            points.AddRange(piece.Path.Points);
            if (piece.End is not null && piece.End.Length > NumericTolerance) { points.Add(piece.End.Tip); }
            result.Bars.Add(new RebarPath(points, false));
        }
        return result;
    }

    private static List<Corner> FindCorners(IReadOnlyList<List<Point2d>> loops, double offsetDistance)
    {
        var result = new List<Corner>();
        foreach (var loop in loops)
        {
            var winding = Math.Sign(ContourGraph.Area(loop));
            for (var i = 0; i < loop.Count; i++)
            {
                var incoming = loop[i] - loop[(i + loop.Count - 1) % loop.Count];
                var outgoing = loop[(i + 1) % loop.Count] - loop[i];
                if (incoming.Length <= ContourGraph.Tolerance || outgoing.Length <= ContourGraph.Tolerance) { continue; }
                var a = incoming.GetNormal();
                var b = outgoing.GetNormal();
                // Right angles accept only drawing noise within the existing
                // 0.1 distance tolerance, capped at 0.001 radians for short edges.
                var angularTolerance = Math.Min(.001, ContourGraph.Tolerance / Math.Min(incoming.Length, outgoing.Length));
                if (Math.Abs(a.DotProduct(b)) > angularTolerance || Cross(a, b) * winding >= 0) { continue; }
                var longestSide = Math.Max(StraightSideLength(loop, i, -1), StraightSideLength(loop, i, 1));
                // Strictly greater than 600; only floating-point noise is
                // excluded here, not the contour graph's 0.1 drawing tolerance.
                if (longestSide <= MinimumOutlineSideLength + NumericTolerance) { continue; }
                var first = loop[i] + new Vector2d(-a.Y, a.X) * (winding * offsetDistance);
                var second = loop[i] + new Vector2d(-b.Y, b.X) * (winding * offsetDistance);
                var joint = first + a * (Cross(second - first, b) / Cross(a, b));
                result.Add(new Corner(joint, a, b));
            }
        }
        return result;
    }

    private static double StraightSideLength(IReadOnlyList<Point2d> loop, int corner, int step)
    {
        var origin = loop[corner];
        var index = (corner + step + loop.Count) % loop.Count;
        var vector = loop[index] - origin;
        var direction = vector.GetNormal();
        var length = vector.Length;
        // Split points on one straight boundary do not shorten that side.
        for (var count = 2; count < loop.Count; count++)
        {
            index = (index + step + loop.Count) % loop.Count;
            var candidate = loop[index] - origin;
            var projection = candidate.DotProduct(direction);
            if (projection <= length || Math.Abs(Cross(candidate, direction)) > ContourGraph.Tolerance) { break; }
            length = projection;
        }
        return length;
    }

    private static bool Matches(RebarPath path, int index, Corner corner)
    {
        var point = path.Points[index];
        if (point.GetDistanceTo(corner.Point) > ContourGraph.Tolerance) { return false; }
        var a = point - path.Points[(index + path.Points.Count - 1) % path.Points.Count];
        var b = path.Points[(index + 1) % path.Points.Count] - point;
        if (a.Length <= NumericTolerance || b.Length <= NumericTolerance) { return false; }
        a = a.GetNormal(); b = b.GetNormal();
        // Support opening / native offsets can reverse the traversal direction.
        return (a.DotProduct(corner.Incoming) > .999999 && b.DotProduct(corner.Outgoing) > .999999) ||
            (a.DotProduct(-corner.Outgoing) > .999999 && b.DotProduct(-corner.Incoming) > .999999);
    }

    private static void Split(RebarPath path, int pathIndex, List<int> cuts,
        List<Piece> pieces, List<RebarExtensionCollision.Extension> ends)
    {
        if (cuts.Count == 0) { pieces.Add(new Piece(path, null, null)); return; }
        var boundaries = path.IsClosed ? cuts : new[] { 0 }.Concat(cuts).Concat(new[] { path.Points.Count - 1 }).ToList();
        var pieceCount = path.IsClosed ? boundaries.Count : boundaries.Count - 1;
        for (var i = 0; i < pieceCount; i++)
        {
            var startIndex = boundaries[i];
            var endIndex = boundaries[(i + 1) % boundaries.Count];
            var points = new List<Point2d> { path.Points[startIndex] };
            var current = startIndex;
            do
            {
                current = (current + 1) % path.Points.Count;
                points.Add(path.Points[current]);
            } while (current != endIndex);
            var start = path.IsClosed || i > 0 ? MakeEnd(startIndex, points[1]) : null;
            var end = path.IsClosed || i < pieceCount - 1 ? MakeEnd(endIndex, points[points.Count - 2]) : null;
            pieces.Add(new Piece(new RebarPath(points, false), start, end));
        }

        RebarExtensionCollision.Extension MakeEnd(int vertex, Point2d neighbor)
        {
            var point = path.Points[vertex];
            var end = new RebarExtensionCollision.Extension(point, (point - neighbor).GetNormal(),
                ExtensionLength, pathIndex, vertex);
            ends.Add(end);
            return end;
        }
    }

    private static double Cross(Vector2d a, Vector2d b) => a.X * b.Y - a.Y * b.X;
}
