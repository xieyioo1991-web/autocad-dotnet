using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Geometry;

namespace AutoCADPlugin;

internal static class ShortBeamTopAnchorage
{
    public static RebarAnchorage.EndResult? TryCreate(int pathIndex, bool atStart, RebarPath path,
        IReadOnlyList<List<Point2d>> supports, IReadOnlyList<BeamTopRegion>? regions)
    {
        if (regions == null || regions.Count == 0) { return null; }
        var original = path.Points[atStart ? 0 : path.Points.Count - 1];
        var neighbor = path.Points[atStart ? 1 : path.Points.Count - 2];
        var direction = original - neighbor;
        if (Math.Abs(direction.X) > 1e-6 || direction.Y >= 0) { return null; }
        var contacts = atStart ? path.StartContacts : path.EndContacts;
        var region = regions.FirstOrDefault(r => contacts.Any(c => c.SupportIndex == r.SupportIndex) &&
            (Math.Abs(original.X - r.Left - OffsetRebar.OffsetDistance) <= ContourGraph.Tolerance ||
             Math.Abs(original.X - r.Right + OffsetRebar.OffsetDistance) <= ContourGraph.Tolerance));
        if (region == null) { return null; }

        // The contour inset already gives the intersection of both offset50
        // lines. Find that fold, walking past any collinear subdivisions.
        var step = atStart ? 1 : -1;
        var index = atStart ? 0 : path.Points.Count - 1;
        while (index + step >= 0 && index + step < path.Points.Count &&
            Math.Abs(path.Points[index + step].X - original.X) <= ContourGraph.Tolerance) { index += step; }
        var fold = path.Points[index];
        if (index + step < 0 || index + step >= path.Points.Count ||
            Math.Abs(path.Points[index + step].Y - fold.Y) <= ContourGraph.Tolerance) { return null; }
        var entry = new Point2d(original.X, region.Bottom);
        var depth = PolygonRay.AvailableFrom(entry, new Vector2d(0, -1), supports[region.SupportIndex]);
        if (!DrawingPrecision.Fits(depth, RebarAnchorage.StraightLength))
        {
            return new RebarAnchorage.EndResult(pathIndex, atStart, original, RebarAnchorage.AnchorKind.Failed,
                Array.Empty<Point2d>(), region.SupportIndex,
                $"短竖段斜筋从原梁顶向下仅有{depth:F1}，放不下1000直锚；保留原断口。");
        }
        return new RebarAnchorage.EndResult(pathIndex, atStart, fold, RebarAnchorage.AnchorKind.BeamTopStraight,
            new[] { entry, entry + new Vector2d(0, -Math.Min(RebarAnchorage.StraightLength, depth)) }, region.SupportIndex);
    }
}
