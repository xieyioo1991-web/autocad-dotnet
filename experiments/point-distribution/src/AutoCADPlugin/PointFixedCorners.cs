using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Geometry;

namespace AutoCADPlugin;

internal static class PointFixedCorners
{
    public static List<Point2d> Select(OffsetRebar.Plan context, IReadOnlyList<Point2d> candidates)
    {
        var result = new List<Point2d>();
        foreach (var center in candidates)
        {
            var keep = true;
            foreach (var horizontal in context.CornerBreaks.Ends.Where(e => Math.Abs(e.Direction.Y) < 1e-6))
            {
                var vertical = context.CornerBreaks.Ends.FirstOrDefault(e =>
                    e.Origin.GetDistanceTo(horizontal.Origin) <= ContourGraph.Tolerance && Math.Abs(e.Direction.X) < 1e-6);
                if (vertical == null) { continue; }
                var delta = center - horizontal.Origin;
                if (Math.Abs(Math.Abs(delta.X) - CornerPointRebar.TangentDistance) > ContourGraph.Tolerance ||
                    Math.Abs(Math.Abs(delta.Y) - CornerPointRebar.TangentDistance) > ContourGraph.Tolerance) { continue; }
                // At a split reentrant corner reserve the dot between the
                // horizontal extension and the ORIGINAL vertical leg (red).
                // Dots beside the vertical extension (cyan) are redistributed.
                keep = delta.DotProduct(horizontal.Direction) > 0 && delta.DotProduct(vertical.Direction) < 0;
                break;
            }
            if (keep) { result.Add(center); }
        }
        return result;
    }
}
