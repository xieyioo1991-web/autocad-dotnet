using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.Geometry;

namespace AutoCADPlugin;

internal static class PointBarJunction
{
    // Only actual finite-segment intersections participate. No extension of
    // disconnected bars and no architectural vertices are used as corners.
    public static bool Intersect(ContourGraph.Edge a, ContourGraph.Edge b, out Point2d point)
    {
        point = Point2d.Origin;
        var r = a.End - a.Start;
        var s = b.End - b.Start;
        var divisor = Cross(r, s);
        if (Math.Abs(divisor) <= 1e-10 * Math.Max(1, r.Length * s.Length)) { return false; }
        var t = Cross(b.Start - a.Start, s) / divisor;
        var u = Cross(b.Start - a.Start, r) / divisor;
        var toleranceA = ContourGraph.Tolerance / r.Length;
        var toleranceB = ContourGraph.Tolerance / s.Length;
        if (t < -toleranceA || t > 1 + toleranceA || u < -toleranceB || u > 1 + toleranceB) { return false; }
        point = a.Start + r * Math.Max(0, Math.Min(1, t));
        return true;
    }

    public static IEnumerable<Point2d> Rays(ContourGraph.Edge edge, Point2d point)
    {
        if (point.GetDistanceTo(edge.Start) > ContourGraph.Tolerance) { yield return edge.Start; }
        if (point.GetDistanceTo(edge.End) > ContourGraph.Tolerance) { yield return edge.End; }
    }

    private static double Cross(Vector2d a, Vector2d b) => a.X * b.Y - a.Y * b.X;
}
