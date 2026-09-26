using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Geometry;

namespace AutoCADPlugin;

internal static class PointBarContact
{
    // A finite transverse bar at the actual longitudinal endpoint supplies a
    // tangent row end. A nearby crossing or a projected infinite line does not.
    public static bool TryClearance(PointRowPath row, bool fromStart,
        IReadOnlyList<ContourGraph.Edge> network, out double clearance)
        => TryClearance(row, fromStart, network, out clearance, out _);

    public static bool TryClearance(PointRowPath row, bool fromStart,
        IReadOnlyList<ContourGraph.Edge> network, out double clearance, out bool horizontalReceiver)
    {
        clearance = double.PositiveInfinity;
        horizontalReceiver = false;
        var endpoint = fromStart ? row.FreeStart : row.FreeEnd;
        if (!endpoint.HasValue || row.Points.Count < 2) { return false; }
        var origin = fromStart ? row.Points[0] : row.Points.Last();
        var next = fromStart ? row.Points[1] : row.Points[row.Points.Count - 2];
        var direction = (next - origin).GetNormal();
        var length = origin.GetDistanceTo(next);
        foreach (var edge in network)
        {
            if (ContourGraph.Distance(endpoint.Value, edge) > ContourGraph.Tolerance) { continue; }
            var axis = (edge.End - edge.Start).GetNormal();
            var normal = new Vector2d(-axis.Y, axis.X);
            var rate = direction.DotProduct(normal);
            if (Math.Abs(rate) < 1e-6) { continue; }
            foreach (var sign in new[] { -1, 1 })
            {
                var distance = (sign * CornerPointRebar.TangentDistance - (origin - edge.Start).DotProduct(normal)) / rate;
                if (distance < -ContourGraph.Tolerance || distance > length + ContourGraph.Tolerance) { continue; }
                distance = Math.Max(0, Math.Min(length, distance));
                var point = origin + direction * distance;
                if (Math.Abs(ContourGraph.Distance(point, edge) - CornerPointRebar.TangentDistance) > .01) { continue; }
                if (distance < clearance - .01)
                {
                    clearance = distance;
                    horizontalReceiver = Math.Abs(direction.X) < 1e-6 && Math.Abs(axis.Y) < 1e-6;
                }
            }
        }
        return !double.IsPositiveInfinity(clearance);
    }
}
