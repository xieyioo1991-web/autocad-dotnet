using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using System;
using System.Collections.Generic;

namespace AutoCADPlugin;

internal readonly struct OutlineSegment
{
    public OutlineSegment(Point2d start, Point2d end, string targetLayer)
    {
        Start = start;
        End = end;
        TargetLayer = targetLayer;
    }

    public Point2d Start { get; }
    public Point2d End { get; }
    public string TargetLayer { get; }
}

internal static class ArchitecturalOutlineExtractor
{
    private sealed class FillRegion
    {
        public FillRegion(List<Point2d> boundary)
        {
            Boundary = boundary;
        }

        public List<Point2d> Boundary { get; }
    }

    private static readonly HashSet<string> OutlineLayers = new(StringComparer.OrdinalIgnoreCase)
    {
        "WALL",
        "COLUMN"
    };

    public static List<OutlineSegment> Extract(Transaction transaction, SelectionSet? selection)
    {
        var result = new List<OutlineSegment>();
        if (selection == null) return result;

        var nonConcreteBoundaries = new List<OutlineSegment>();
        var nonConcreteRegions = new List<FillRegion>();
        var concreteRegions = new List<FillRegion>();
        var sourceOutline = new List<OutlineSegment>();
        var concreteBoundaries = new List<OutlineSegment>();

        // First collect only material boundaries. Hatch pattern strokes are
        // never copied; only the actual boundary loop is considered.
        foreach (SelectedObject selected in selection)
        {
            if (selected == null) continue;
            if (transaction.GetObject(selected.ObjectId, OpenMode.ForRead) is not Hatch hatch) continue;

            if (IsReinforcedConcrete(hatch.PatternName))
            {
                try
                {
                    AddFillRegions(concreteRegions, hatch);
                    AddHatchBoundaries(concreteBoundaries, hatch, Standards.OtherThinLayer);
                }
                catch (System.Exception)
                {
                    // A malformed/custom hatch must not prevent WALL/COLUMN
                    // and SD-REGION processing from completing.
                }
                continue;
            }

            // Material hatches on PUB_HATCH identify regions whose outer
            // contour must not be retained when the material is not concrete.
            // Annotation hatches on other layers are ignored.
            if (!hatch.Layer.Equals("PUB_HATCH", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                AddFillRegions(nonConcreteRegions, hatch);
                AddHatchBoundaries(nonConcreteBoundaries, hatch, Standards.OtherThinLayer);
            }
            catch (System.Exception)
            {
                // Ignore an unsupported material hatch rather than inventing
                // a boundary or aborting the whole detail command.
            }
        }

        // WALL/COLUMN is copied directly, except where it is the boundary or
        // an internal stroke of a non-concrete material region.
        foreach (SelectedObject selected in selection)
        {
            if (selected == null) continue;
            var entity = transaction.GetObject(selected.ObjectId, OpenMode.ForRead) as Entity;
            if (entity == null || !OutlineLayers.Contains(entity.Layer)) continue;

            if (entity is Line line)
            {
                AddSource(sourceOutline, line.StartPoint, line.EndPoint, nonConcreteBoundaries, nonConcreteRegions);
                continue;
            }

            if (entity is not Polyline polyline) continue;
            for (var index = 0; index < polyline.NumberOfVertices; index++)
            {
                var next = index + 1;
                if (next >= polyline.NumberOfVertices)
                {
                    if (!polyline.Closed) break;
                    next = 0;
                }
                if (polyline.GetSegmentType(index) != SegmentType.Line) continue;
                var start = polyline.GetPoint2dAt(index);
                var end = polyline.GetPoint2dAt(next);
                AddSource(sourceOutline, new Point3d(start.X, start.Y, 0), new Point3d(end.X, end.Y, 0), nonConcreteBoundaries, nonConcreteRegions);
            }
        }

        result.AddRange(sourceOutline);
        // Concrete hatch contours are retained only when they are on the
        // outside of the union of concrete regions. Shared/internal edges
        // are removed before output; no replacement geometry is invented.
        result.AddRange(FilterConcreteBoundaries(concreteBoundaries, concreteRegions));
        return RemoveDuplicates(result);
    }

    private static bool IsReinforcedConcrete(string patternName)
    {
        return patternName.IndexOf("钢筋混凝土", StringComparison.OrdinalIgnoreCase) >= 0 ||
               patternName.Equals("CONCRETE", StringComparison.OrdinalIgnoreCase) ||
               patternName.Equals("AR-CONC", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddHatchBoundaries(List<OutlineSegment> result, Hatch hatch, string targetLayer)
    {
        for (var loopIndex = 0; loopIndex < hatch.NumberOfLoops; loopIndex++)
        {
            var loop = hatch.GetLoopAt(loopIndex);
            if (loop.IsPolyline && loop.Polyline != null && loop.Polyline.Count >= 2)
            {
                for (var vertexIndex = 0; vertexIndex < loop.Polyline.Count; vertexIndex++)
                {
                    var next = (vertexIndex + 1) % loop.Polyline.Count;
                    var a = loop.Polyline[vertexIndex].Vertex;
                    var b = loop.Polyline[next].Vertex;
                    Add(result, new Point3d(a.X, a.Y, 0), new Point3d(b.X, b.Y, 0), targetLayer);
                }
                continue;
            }

            foreach (var curve in loop.Curves)
            {
                if (curve is not LineSegment2d line) continue;
                Add(result,
                    new Point3d(line.StartPoint.X, line.StartPoint.Y, 0),
                    new Point3d(line.EndPoint.X, line.EndPoint.Y, 0),
                    targetLayer);
            }
        }
    }

    private static void AddFillRegions(List<FillRegion> regions, Hatch hatch)
    {
        for (var loopIndex = 0; loopIndex < hatch.NumberOfLoops; loopIndex++)
        {
            var loop = hatch.GetLoopAt(loopIndex);
            var boundary = new List<Point2d>();
            if (loop.IsPolyline && loop.Polyline != null)
            {
                for (var index = 0; index < loop.Polyline.Count; index++)
                {
                    var point = loop.Polyline[index].Vertex;
                    boundary.Add(new Point2d(point.X, point.Y));
                }
            }
            else
            {
                foreach (var curve in loop.Curves)
                {
                    if (curve is not LineSegment2d line)
                    {
                        boundary.Clear();
                        break;
                    }
                    boundary.Add(new Point2d(line.StartPoint.X, line.StartPoint.Y));
                }
            }

            if (boundary.Count >= 3) regions.Add(new FillRegion(boundary));
        }
    }

    private static void AddSource(
        List<OutlineSegment> result,
        Point3d start,
        Point3d end,
        List<OutlineSegment> nonConcreteBoundaries,
        List<FillRegion> nonConcreteRegions)
    {
        var a = new Point2d(start.X, start.Y);
        var b = new Point2d(end.X, end.Y);
        if (a.GetDistanceTo(b) < 1.0) return;
        if (IsExcludedByNonConcrete(a, b, nonConcreteBoundaries, nonConcreteRegions)) return;
        result.Add(new OutlineSegment(a, b, Standards.OtherThinLayer));
    }

    private static List<OutlineSegment> FilterConcreteBoundaries(
        List<OutlineSegment> input,
        List<FillRegion> concreteRegions)
    {
        if (input.Count == 0 || concreteRegions.Count == 0) return input;

        var split = SplitCollinearSegments(input);
        var result = new List<OutlineSegment>();
        foreach (var segment in split)
        {
            if (!IsInternalConcreteSegment(segment, concreteRegions))
            {
                result.Add(segment);
            }
        }
        return result;
    }

    private static bool IsInternalConcreteSegment(OutlineSegment segment, List<FillRegion> concreteRegions)
    {
        var dx = segment.End.X - segment.Start.X;
        var dy = segment.End.Y - segment.Start.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1.0) return false;

        // Sample more than the midpoint so a long edge that overlaps another
        // region only on one part is split and classified correctly.
        var normalX = -dy / length;
        var normalY = dx / length;
        var offset = Math.Max(1.0, Math.Min(5.0, length * 0.02));
        var samples = new[] { 0.2, 0.5, 0.8 };
        foreach (var ratio in samples)
        {
            var center = new Point2d(
                segment.Start.X + dx * ratio,
                segment.Start.Y + dy * ratio);
            var left = new Point2d(center.X + normalX * offset, center.Y + normalY * offset);
            var right = new Point2d(center.X - normalX * offset, center.Y - normalY * offset);

            // If either side is outside every concrete region, this is an
            // external concrete contour and must remain.
            if (!IsInsideAny(left, concreteRegions) || !IsInsideAny(right, concreteRegions))
            {
                return false;
            }
        }

        // Both sides are occupied by concrete regions at every sample: this
        // is a shared/internal hatch edge, not an outside contour.
        return true;
    }

    private static bool IsInsideAny(Point2d point, List<FillRegion> regions)
    {
        foreach (var region in regions)
        {
            if (IsPointOnBoundary(point, region.Boundary, 1.0)) return true;
            if (IsInside(point, region.Boundary)) return true;
        }
        return false;
    }

    private static List<OutlineSegment> SplitCollinearSegments(List<OutlineSegment> input)
    {
        var result = new List<OutlineSegment>();
        foreach (var source in input)
        {
            var parameters = new List<double> { 0.0, 1.0 };
            foreach (var other in input)
            {
                if (!AreCollinear(source.Start, source.End, other.Start, other.End)) continue;
                AddParameterIfOnSegment(parameters, other.Start, source.Start, source.End);
                AddParameterIfOnSegment(parameters, other.End, source.Start, source.End);
            }

            parameters.Sort();
            var unique = new List<double>();
            foreach (var value in parameters)
            {
                var clamped = Math.Max(0.0, Math.Min(1.0, value));
                if (unique.Count == 0 || Math.Abs(clamped - unique[unique.Count - 1]) > 1e-8)
                {
                    unique.Add(clamped);
                }
            }

            for (var index = 0; index + 1 < unique.Count; index++)
            {
                var start = PointAt(source.Start, source.End, unique[index]);
                var end = PointAt(source.Start, source.End, unique[index + 1]);
                if (start.GetDistanceTo(end) >= 1.0)
                {
                    result.Add(new OutlineSegment(start, end, source.TargetLayer));
                }
            }
        }
        return result;
    }

    private static bool AreCollinear(Point2d a, Point2d b, Point2d c, Point2d d)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1.0) return false;
        var crossC = dx * (c.Y - a.Y) - dy * (c.X - a.X);
        var crossD = dx * (d.Y - a.Y) - dy * (d.X - a.X);
        return Math.Abs(crossC) <= length && Math.Abs(crossD) <= length;
    }

    private static void AddParameterIfOnSegment(List<double> parameters, Point2d point, Point2d start, Point2d end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared < 1.0) return;
        var length = Math.Sqrt(lengthSquared);
        var cross = dx * (point.Y - start.Y) - dy * (point.X - start.X);
        if (Math.Abs(cross) > length) return;
        var parameter = ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared;
        if (parameter >= -1e-8 && parameter <= 1.0 + 1e-8)
        {
            parameters.Add(parameter);
        }
    }

    private static Point2d PointAt(Point2d start, Point2d end, double parameter)
    {
        return new Point2d(
            start.X + (end.X - start.X) * parameter,
            start.Y + (end.Y - start.Y) * parameter);
    }

    private static bool IsExcludedByNonConcrete(
        Point2d start,
        Point2d end,
        List<OutlineSegment> nonConcreteBoundaries,
        List<FillRegion> nonConcreteRegions)
    {
        foreach (var boundary in nonConcreteBoundaries)
        {
            if (AreSameSegment(start, end, boundary.Start, boundary.End)) return true;
        }

        var midpoint = new Point2d((start.X + end.X) * 0.5, (start.Y + end.Y) * 0.5);
        foreach (var region in nonConcreteRegions)
        {
            if (IsPointOnBoundary(midpoint, region.Boundary, 1.0)) continue;
            if (IsInside(midpoint, region.Boundary)) return true;
        }
        return false;
    }

    private static bool IsPointOnBoundary(Point2d point, List<Point2d> boundary, double tolerance)
    {
        for (var index = 0; index < boundary.Count; index++)
        {
            var a = boundary[index];
            var b = boundary[(index + 1) % boundary.Count];
            var segment = b - a;
            var lengthSquared = segment.DotProduct(segment);
            if (lengthSquared < 1e-12) continue;
            var t = Math.Max(0.0, Math.Min(1.0, (point - a).DotProduct(segment) / lengthSquared));
            var projection = a + segment * t;
            if (point.GetDistanceTo(projection) <= tolerance) return true;
        }
        return false;
    }

    private static bool IsInside(Point2d point, List<Point2d> boundary)
    {
        var inside = false;
        for (int i = 0, j = boundary.Count - 1; i < boundary.Count; j = i++)
        {
            var a = boundary[i];
            var b = boundary[j];
            var crosses = ((a.Y > point.Y) != (b.Y > point.Y)) &&
                          point.X < (b.X - a.X) * (point.Y - a.Y) /
                          ((b.Y - a.Y) == 0 ? 1e-12 : (b.Y - a.Y)) + a.X;
            if (crosses) inside = !inside;
        }
        return inside;
    }

    private static bool AreSameSegment(Point2d a, Point2d b, Point2d c, Point2d d)
    {
        return (a.GetDistanceTo(c) < 1.0 && b.GetDistanceTo(d) < 1.0) ||
               (a.GetDistanceTo(d) < 1.0 && b.GetDistanceTo(c) < 1.0);
    }

    private static void Add(List<OutlineSegment> result, Point3d start, Point3d end, string targetLayer)
    {
        var a = new Point2d(start.X, start.Y);
        var b = new Point2d(end.X, end.Y);
        if (a.GetDistanceTo(b) >= 1.0) result.Add(new OutlineSegment(a, b, targetLayer));
    }

    private static List<OutlineSegment> RemoveDuplicates(List<OutlineSegment> input)
    {
        var result = new List<OutlineSegment>();
        foreach (var item in input)
        {
            var duplicate = false;
            foreach (var existing in result)
            {
                var same = item.Start.GetDistanceTo(existing.Start) < 1.0 &&
                           item.End.GetDistanceTo(existing.End) < 1.0;
                var reverse = item.Start.GetDistanceTo(existing.End) < 1.0 &&
                              item.End.GetDistanceTo(existing.Start) < 1.0;
                if (same || reverse)
                {
                    duplicate = true;
                    break;
                }
            }
            if (!duplicate) result.Add(item);
        }
        return result;
    }
}
