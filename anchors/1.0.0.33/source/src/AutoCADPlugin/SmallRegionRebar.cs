using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Geometry;

namespace AutoCADPlugin;

// After lower-bar removal, shorten only the exposed downward vertical returns.
// The upper line and support entry keep their coordinates and direction.
internal static class SmallRegionRebar
{
    public const double ReturnReduction = 50;

    public static RebarPath? ShortenReturns(RebarPath path, out int shortened)
    {
        shortened = 0;
        var points = path.Points.ToList();
        if (path.StartRule == RebarEndRule.Free && ShortenStart(points)) { shortened++; }
        if (path.EndRule == RebarEndRule.Free)
        {
            points.Reverse();
            if (ShortenStart(points)) { shortened++; }
            points.Reverse();
        }
        if (points.Count < 2) { return null; }
        return new RebarPath(points, path.IsClosed, path.StartContacts, path.EndContacts,
            path.RegionIndex, path.StartRule, path.EndRule);
    }

    private static bool ShortenStart(List<Point2d> points)
    {
        if (points.Count < 2) { return false; }
        var tip = points[0];
        var last = 0;
        for (var i = 1; i < points.Count; i++)
        {
            if (Math.Abs(points[i].X - tip.X) > ContourGraph.Tolerance || points[i].Y <= points[i - 1].Y) { break; }
            last = i;
        }
        if (last == 0) { return false; }
        var height = points[last].Y - tip.Y;
        if (!DrawingPrecision.GreaterThan(height, ReturnReduction))
        {
            // A short return disappears at the upper junction; never trim the
            // adjacent horizontal bar or generate a reversed/zero-length segment.
            points.RemoveRange(0, last);
            return true;
        }
        var targetY = tip.Y + ReturnReduction;
        var next = 1;
        while (points[next].Y < targetY) { next++; }
        var a = points[next - 1]; var b = points[next];
        var target = a + (b - a) * ((targetY - a.Y) / (b.Y - a.Y));
        points.RemoveRange(0, next);
        if (target.GetDistanceTo(points[0]) > 1e-6) { points.Insert(0, target); }
        return true;
    }
}
