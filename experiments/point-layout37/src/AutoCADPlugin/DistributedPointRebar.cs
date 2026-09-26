using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Geometry;

namespace AutoCADPlugin;

// Rows are calculation geometry only. The writer receives point centers and
// never changes any selected longitudinal bar or fixed corner.
internal static class DistributedPointRebar
{
    private const double Epsilon = 1e-5;
    private const double Tolerance = ContourGraph.Tolerance;

    internal sealed class Result
    {
        public List<Point2d> Centers { get; } = new List<Point2d>();
        public List<Point2d> FixedCenters { get; } = new List<Point2d>();
        public List<List<Point2d>> Rows { get; } = new List<List<Point2d>>();
        public List<string> Warnings { get; } = new List<string>();
        public int RedistributedCorners { get; set; }
        public Point2d Origin { get; set; }
    }

    public static Result Create(OffsetRebar.Plan context, IReadOnlyList<RebarPath> bars, CornerPointRebar.Result corners)
    {
        var result = new Result { Origin = context.Origin };
        result.FixedCenters.AddRange(corners.Centers);
        result.Centers.AddRange(result.FixedCenters);
        result.RedistributedCorners = corners.InternalJunctionCount;
        var material = context.Boundaries.Concat(context.BeamTops.Regions.Select(region => region.Polygon)).ToList();
        var union = ContourGraph.Build(material.SelectMany(ContourGraph.Edges).ToList(), new List<List<Point2d>>(), true);
        foreach (var bar in PointBarOrder.Sort(bars))
        {
            var isU = PointAnchorExclusion.IsUPath(bar, context.BeamTops.Bars);
            var supports = isU ? context.OriginalSupports : context.Supports;
            var rowMaterial = union;
            if (isU)
            {
                var index = context.BeamTops.Bars.FindIndex(u => PointAnchorExclusion.IsUPath(bar, new[] { u }));
                rowMaterial = new List<List<Point2d>> { context.BeamTops.Regions[index].Polygon };
            }
            // Bend anchorage is excluded from FIXED CORNERS, not from the
            // continuous distribution guide. Real/virtual support clipping
            // removes its forbidden part; a short no-U upright can thus reach
            // the actual beam's400 boundary without inventing a free endpoint.
            var eligible = CornerPointRebar.Segments(bar).ToList();
            foreach (var sign in new[] { -1, 1 })
            {
                foreach (var route in PointRowPath.Offset(eligible, sign * CornerPointRebar.TangentDistance, bar))
                {
                    foreach (var point in result.FixedCenters)
                    {
                        if (route.Locate(point, out _) && supports.Any(s => PointRowGeometry.NearSupport(point, s, 400 - Epsilon)))
                        { Warn(point, "固定角点距支撑不足400，固定点保留，该侧无法同时满足400收尾", result); }
                    }
                    foreach (var clipped in route.Clip(rowMaterial, supports))
                    { AddRow(clipped, rowMaterial, supports, eligible, result); }
                }
            }
        }
        return result;
    }

    private static void AddRow(PointRowPath route,
        IReadOnlyList<List<Point2d>> material, IReadOnlyList<List<Point2d>> supports,
        IReadOnlyList<ContourGraph.Edge> ownerEdges, Result result)
    {
        var length = route.Length;
        if (length <= Tolerance) { return; }
        var fixedOnRow = new List<double>();
        foreach (var point in result.FixedCenters)
        {
            if (route.Locate(point, out var t)) { fixedOnRow.Add(t); }
        }
        fixedOnRow.Sort();
        if (route.Closed)
        {
            if (fixedOnRow.Count == 0) { Warn(route.Points[0], "闭合点筋路径没有可固定角点，未自行指定起点", result); return; }
            fixedOnRow.Add(fixedOnRow[0] + length);
            for (var i = 1; i < fixedOnRow.Count; i++) { Divide(fixedOnRow[i - 1], fixedOnRow[i]); }
            return;
        }
        var start = 0.0; var end = length;
        if (!fixedOnRow.Any(t => t <= Tolerance) && !AtSupport(0))
        {
            if (route.FreeStart.HasValue)
            {
                start = route.EndClearance(true);
                if (fixedOnRow.Count > 0 && fixedOnRow[0] <= start + Tolerance) { start = fixedOnRow[0]; }
            }
            else if (fixedOnRow.Count > 0) { start = fixedOnRow[0]; }
            else { Warn(route.Points[0], "路径被轮廓截断且没有固定起点，未按自由端处理", result); return; }
        }
        if (!fixedOnRow.Any(t => length - t <= Tolerance) && !AtSupport(length))
        {
            if (route.FreeEnd.HasValue)
            {
                end = length - route.EndClearance(false);
                if (fixedOnRow.Count > 0 && fixedOnRow[fixedOnRow.Count - 1] >= end - Tolerance)
                { end = fixedOnRow[fixedOnRow.Count - 1]; }
            }
            else if (fixedOnRow.Count > 0) { end = fixedOnRow[fixedOnRow.Count - 1]; }
            else { Warn(route.Points.Last(), "路径被轮廓截断且没有固定终点，未按自由端处理", result); return; }
        }
        if (end < start - Tolerance)
        { Warn(route.At(length / 2), "此段长度不足以在自由端保留圆心400，未增加分布点", result); return; }
        var stops = new List<double> { start };
        stops.AddRange(fixedOnRow.Where(t => t > start + Tolerance && t < end - Tolerance));
        if (end - start > Tolerance) { stops.Add(end); }
        if (stops.Count == 1) { TryAdd(new List<Point2d> { route.At(start) }); }
        for (var i = 1; i < stops.Count; i++) { Divide(stops[i - 1], stops[i]); }

        void Divide(double a, double b)
        {
            var row = PointRowGeometry.Divide(a, b).Select(t => route.At(route.Closed ? t % length : t)).ToList();
            TryAdd(row);
        }

        bool AtSupport(double t)
        {
            var point = route.At(t);
            return supports.Any(s => ContourGraph.Edges(s).Any(e => Math.Abs(ContourGraph.Distance(point, e) - 400) < .01));
        }

        void TryAdd(List<Point2d> row)
        {
            // A conflict invalidates this interval, not just one interior dot:
            // silently dropping that dot could leave a gap larger than800.
            foreach (var point in row)
            {
                if (result.FixedCenters.Any(p => p.GetDistanceTo(point) <= Tolerance)) { continue; }
                if (!PointRowGeometry.InsideMaterial(point, material) ||
                    ownerEdges.Any(e => ContourGraph.Distance(point, e) < CornerPointRebar.TangentDistance - Epsilon) ||
                    !ownerEdges.Any(e => Math.Abs(ContourGraph.Distance(point, e) - CornerPointRebar.TangentDistance) <= Epsilon) ||
                    result.Centers.Any(p => p.GetDistanceTo(point) > Tolerance && p.GetDistanceTo(point) < 100 - Epsilon) ||
                    row.Any(p => p.GetDistanceTo(point) > Tolerance && p.GetDistanceTo(point) < 100 - Epsilon))
                {
                    Warn(point, "400收尾或等分点与所属纵筋/点筋冲突，本区间保留固定点，未强行补点", result);
                    return;
                }
            }
            foreach (var point in row)
            {
                if (!result.Centers.Any(p => p.GetDistanceTo(point) <= Tolerance)) { result.Centers.Add(point); }
            }
            result.Rows.Add(row);
        }

    }

    private static void Warn(Point2d point, string reason, Result result)
    {
        var message = $"坐标({point.X + result.Origin.X:F1},{point.Y + result.Origin.Y:F1})：{reason}。";
        if (!result.Warnings.Contains(message)) { result.Warnings.Add(message); }
    }
}
