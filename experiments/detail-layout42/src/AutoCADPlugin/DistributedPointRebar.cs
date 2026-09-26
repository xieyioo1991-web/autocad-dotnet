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

    private sealed class DeferredEnd
    {
        public DeferredEnd(Point2d point, List<Point2d> row, bool first) { Point = point; Row = row; First = first; }
        public Point2d Point { get; }
        public List<Point2d> Row { get; }
        public bool First { get; }
    }

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
        var network = bars.SelectMany(CornerPointRebar.Segments).ToList();
        var deferred = new List<DeferredEnd>();
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
                    { AddRow(clipped, rowMaterial, supports, eligible, network, deferred, result); }
                }
            }
        }
        // A vertical contact tail is a spacing boundary, not an extra dot on
        // the receiving horizontal row. Wait until every row has been built.
        foreach (var end in deferred)
        {
            var covered = result.Rows.Any(row => Enumerable.Range(1, Math.Max(0, row.Count - 1)).Any(i =>
                Math.Abs(row[i].Y - row[i - 1].Y) <= Tolerance &&
                ContourGraph.Distance(end.Point, new ContourGraph.Edge(row[i - 1], row[i])) <= Tolerance));
            if (covered) { continue; }
            if (result.Centers.Any(p => p.GetDistanceTo(end.Point) > Tolerance && p.GetDistanceTo(end.Point) < 100 - Epsilon))
            { Warn(end.Point, "竖排交接处没有可接管的完整横排，且尾点与已有点筋冲突，请检查", result); continue; }
            if (!result.Centers.Any(p => p.GetDistanceTo(end.Point) <= Tolerance)) { result.Centers.Add(end.Point); }
            if (end.First) { end.Row.Insert(0, end.Point); } else { end.Row.Add(end.Point); }
        }
        return result;
    }

    private static void AddRow(PointRowPath route,
        IReadOnlyList<List<Point2d>> material, IReadOnlyList<List<Point2d>> supports,
        IReadOnlyList<ContourGraph.Edge> ownerEdges, IReadOnlyList<ContourGraph.Edge> network,
        List<DeferredEnd> deferred, Result result)
    {
        var length = route.Length;
        var delegatedEnds = new List<Point2d>();
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
                start = PointBarContact.TryClearance(route, true, network, out var contactStart, out var horizontalReceiver)
                    ? contactStart : route.EndClearance(true);
                if (fixedOnRow.Count > 0 && fixedOnRow[0] <= start + Tolerance) { start = fixedOnRow[0]; }
                if (horizontalReceiver) { delegatedEnds.Add(route.At(start)); }
            }
            else if (fixedOnRow.Count > 0) { start = fixedOnRow[0]; }
            else { Warn(route.Points[0], "路径被轮廓截断且没有固定起点，未按自由端处理", result); return; }
        }
        if (!fixedOnRow.Any(t => length - t <= Tolerance) && !AtSupport(length))
        {
            if (route.FreeEnd.HasValue)
            {
                end = length - (PointBarContact.TryClearance(route, false, network, out var contactEnd, out var horizontalReceiver)
                    ? contactEnd : route.EndClearance(false));
                if (fixedOnRow.Count > 0 && fixedOnRow[fixedOnRow.Count - 1] >= end - Tolerance)
                { end = fixedOnRow[fixedOnRow.Count - 1]; }
                if (horizontalReceiver) { delegatedEnds.Add(route.At(end)); }
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
            bool Delegated(Point2d p) => delegatedEnds.Any(q => p.GetDistanceTo(q) <= Tolerance) &&
                !result.FixedCenters.Any(q => p.GetDistanceTo(q) <= Tolerance);
            // A conflict invalidates this interval, not just one interior dot:
            // silently dropping that dot could leave a gap larger than800.
            foreach (var point in row)
            {
                if (result.FixedCenters.Any(p => p.GetDistanceTo(point) <= Tolerance)) { continue; }
                if (!PointRowGeometry.InsideMaterial(point, material) ||
                    ownerEdges.Any(e => ContourGraph.Distance(point, e) < CornerPointRebar.TangentDistance - Epsilon) ||
                    !ownerEdges.Any(e => Math.Abs(ContourGraph.Distance(point, e) - CornerPointRebar.TangentDistance) <= Epsilon) ||
                    !Delegated(point) && (result.Centers.Any(p => p.GetDistanceTo(point) > Tolerance && p.GetDistanceTo(point) < 100 - Epsilon) ||
                    row.Any(p => !Delegated(p) && p.GetDistanceTo(point) > Tolerance && p.GetDistanceTo(point) < 100 - Epsilon)))
                {
                    Warn(point, "400收尾或等分点与所属纵筋/点筋冲突，本区间保留固定点，未强行补点", result);
                    return;
                }
            }
            var actualRow = row.Where(p => !Delegated(p)).ToList();
            for (var i = 0; i < row.Count; i++)
            {
                var point = row[i];
                if (Delegated(point)) { deferred.Add(new DeferredEnd(point, actualRow, i == 0)); continue; }
                if (!result.Centers.Any(p => p.GetDistanceTo(point) <= Tolerance)) { result.Centers.Add(point); }
            }
            result.Rows.Add(actualRow);
        }

    }

    private static void Warn(Point2d point, string reason, Result result)
    {
        var message = $"坐标({point.X + result.Origin.X:F1},{point.Y + result.Origin.Y:F1})：{reason}。";
        if (!result.Warnings.Contains(message)) { result.Warnings.Add(message); }
    }
}
