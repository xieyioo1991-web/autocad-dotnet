using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AutoCADPlugin;

internal static class OffsetRebar
{
    public const double OffsetDistance = 50;

    internal sealed class Plan
    {
        public Point2d Origin;
        public List<List<Point2d>> Boundaries = new List<List<Point2d>>();
        public List<List<Point2d>> Bars = new List<List<Point2d>>();
        public int SupportCount;
    }

    public static Plan Create(IReadOnlyList<Entity> selected)
    {
        var hasRoles = selected.Any(e => OutlineRole.Get(e) == "Contour");
        var supports = new List<List<Point2d>>();
        var edges = new List<ContourGraph.Edge>();
        foreach (var entity in selected)
        {
            var role = OutlineRole.Get(entity);
            if (entity.Layer != Standards.OtherThinLayer && entity.Layer != Standards.OtherConstructionLayer && entity.Layer != Standards.RegionLayer) { continue; }
            var isSupport = role == "Support" || entity.Layer == Standards.RegionLayer ||
                (!hasRoles && entity.Layer == Standards.OtherThinLayer && entity is Polyline candidate && candidate.Closed);
            if (isSupport && entity is Polyline support)
            {
                var ring = ReadRing(support);
                if (!support.Closed || ring.Count < 3) { throw new InvalidOperationException("支撑多段线必须闭合且至少有3个顶点。"); }
                supports.Add(ring);
                continue;
            }
            if (hasRoles && role != "Contour") { continue; }
            if (entity is Line line)
            {
                CheckPlane(line.StartPoint);
                CheckPlane(line.EndPoint);
                edges.Add(new ContourGraph.Edge(To2d(line.StartPoint), To2d(line.EndPoint)));
            }
            else if (entity is Polyline polyline)
            {
                var ring = ReadRing(polyline);
                for (var i = 0; i < ring.Count - (polyline.Closed ? 0 : 1); i++)
                { edges.Add(new ContourGraph.Edge(ring[i], ring[(i + 1) % ring.Count])); }
            }
        }
        if (edges.Count == 0) { throw new InvalidOperationException("未选中配筋轮廓。请先用SD_AUTO_DETAIL生成四倍轮廓，再框选完整大样。"); }
        // Version21 stored support diagonals on the contour layer without tags.
        edges.RemoveAll(e => supports.Any(s => IsSupportDiagonal(e, s)));
        if (edges.Count == 0) { throw new InvalidOperationException("只有支撑区域，没有配筋轮廓。"); }
        var origin = edges[0].Start;
        Point2d Local(Point2d point) => new Point2d(point.X - origin.X, point.Y - origin.Y);
        edges = edges.Select(e => new ContourGraph.Edge(Local(e.Start), Local(e.End))).ToList();
        supports = supports.Select(s => s.Select(Local).ToList()).ToList();
        foreach (var support in supports) { edges.AddRange(ContourGraph.Edges(support)); }
        var plan = new Plan { Origin = origin, SupportCount = supports.Count, Boundaries = ContourGraph.Build(edges, supports) };
        // For this first experiment, fail explicitly on nested loops rather than
        // silently treating a void/support island as material.
        foreach (var boundary in plan.Boundaries)
        {
            // A shared corner can be reported as inside by a ray test on a
            // vertex. Use an interior sample so corner contact is not nesting.
            if (ContourGraph.Area(boundary) <= 0)
            { throw new InvalidOperationException("本轮试验暂不支持含内孔或嵌套边界的配筋区域，未生成钢筋。"); }
            var interior = ContourGraph.InteriorPoint(boundary);
            if (plan.Boundaries.Any(other => other != boundary && ContourGraph.Contains(other, interior)))
            { throw new InvalidOperationException("本轮试验暂不支持含内孔或嵌套边界的配筋区域，未生成钢筋。"); }
            if (supports.Any(s => s.All(p => ContourGraph.Contains(boundary, p))))
            { throw new InvalidOperationException("检测到完全位于轮廓内部的支撑岛，本轮暂不处理内孔偏移。"); }
            plan.Bars.AddRange(Inset(boundary));
        }
        return plan;
    }

    internal static List<List<Point2d>> Inset(List<Point2d> boundary)
    {
        using var source = MakePolyline(boundary);
        var bars = new List<List<Point2d>>();
        // Offset sign alone is not a reliable definition of 'inside'. Select
        // results by containment and minimum clearance instead of winding sign.
        foreach (var distance in new[] { -OffsetDistance, OffsetDistance })
        {
            DBObjectCollection? offsetObjects = null;
            try
            {
                offsetObjects = source.GetOffsetCurves(distance);
                foreach (DBObject item in offsetObjects)
                {
                    if (item is not Polyline polyline || !polyline.Closed) { continue; }
                    var ring = ReadRing(polyline);
                    if (ring.Count < 3 || Math.Abs(ContourGraph.Area(ring)) < 1) { continue; }
                    if (Math.Abs(ContourGraph.Area(ring)) >= Math.Abs(ContourGraph.Area(boundary))) { continue; }
                    if (!ValidInset(ring, boundary)) { continue; }
                    if (!bars.Any(existing => SameRing(existing, ring))) { bars.Add(ring); }
                }
            }
            catch (Autodesk.AutoCAD.Runtime.Exception error) when (error.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.InvalidInput)
            {
                // A collapsed offset is expected for regions narrower than100.
                // The final empty-result check reports it and aborts the command.
            }
            finally
            {
                if (offsetObjects != null)
                {
                    foreach (DBObject item in offsetObjects) { item.Dispose(); }
                    offsetObjects.Dispose();
                }
            }
        }
        if (bars.Count == 0) { throw new InvalidOperationException("有区域无法向内偏移50（过窄、偏移坍缩或几何无效），本次未生成钢筋。"); }
        return bars;
    }

    private static bool ValidInset(List<Point2d> ring, List<Point2d> boundary)
    {
        foreach (var edge in ContourGraph.Edges(ring))
        {
            for (var i = 0; i <= 16; i++)
            {
                var point = edge.Start + (edge.End - edge.Start) * (i / 16.0);
                if (!ContourGraph.Contains(boundary, point)) { return false; }
            }
            foreach (var outer in ContourGraph.Edges(boundary))
            {
                if (SegmentsCross(edge, outer)) { return false; }
                var clearance = Math.Min(Math.Min(ContourGraph.Distance(edge.Start, outer), ContourGraph.Distance(edge.End, outer)),
                    Math.Min(ContourGraph.Distance(outer.Start, edge), ContourGraph.Distance(outer.End, edge)));
                if (clearance < OffsetDistance - ContourGraph.Tolerance) { return false; }
            }
        }
        return true;
    }

    private static bool SegmentsCross(ContourGraph.Edge a, ContourGraph.Edge b)
    {
        double Cross(Vector2d x, Vector2d y) => x.X * y.Y - x.Y * y.X;
        var r = a.End - a.Start;
        var s = b.End - b.Start;
        var divisor = Cross(r, s);
        if (Math.Abs(divisor) < 1e-10) { return false; }
        var t = Cross(b.Start - a.Start, s) / divisor;
        var u = Cross(b.Start - a.Start, r) / divisor;
        return t >= 0 && t <= 1 && u >= 0 && u <= 1;
    }

    public static int Write(Database database, Transaction transaction, Plan plan)
    {
        var space = (BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForWrite);
        var worldBars = plan.Bars.Select(r => r.Select(p => plan.Origin + new Vector2d(p.X, p.Y)).ToList()).ToList();
        foreach (ObjectId id in space)
        {
            if (transaction.GetObject(id, OpenMode.ForRead) is Polyline existing && existing.Layer == Standards.ReinforcementLayer && existing.Closed)
            {
                var points = Enumerable.Range(0, existing.NumberOfVertices).Select(existing.GetPoint2dAt).ToList();
                if (worldBars.Any(r => SameRing(r, points))) { throw new InvalidOperationException("此轮廓已有相同偏移纵筋，未重复生成。"); }
            }
        }
        Standards.Ensure(database, transaction);
        var layers = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
        var layer = (LayerTableRecord)transaction.GetObject(layers[Standards.ReinforcementLayer], OpenMode.ForWrite);
        layer.Color = Color.FromColorIndex(ColorMethod.ByAci, 6);
        layer.LinetypeObjectId = database.ContinuousLinetype;
        layer.LineWeight = LineWeight.ByLineWeightDefault;
        foreach (var ring in worldBars)
        {
            using var polyline = MakePolyline(ring);
            polyline.Layer = Standards.ReinforcementLayer;
            polyline.ConstantWidth = Standards.LongitudinalWidth;
            polyline.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
            polyline.Linetype = "ByLayer";
            polyline.LineWeight = LineWeight.ByLayer;
            space.AppendEntity(polyline);
            transaction.AddNewlyCreatedDBObject(polyline, true);
        }
        return worldBars.Count;
    }

    private static Polyline MakePolyline(List<Point2d> points)
    {
        var polyline = new Polyline();
        for (var i = 0; i < points.Count; i++) { polyline.AddVertexAt(i, points[i], 0, 0, 0); }
        polyline.Closed = true;
        return polyline;
    }

    private static List<Point2d> ReadRing(Polyline polyline)
    {
        var points = new List<Point2d>();
        for (var i = 0; i < polyline.NumberOfVertices; i++)
        {
            CheckPlane(polyline.GetPoint3dAt(i));
            if (Math.Abs(polyline.GetBulgeAt(i)) > 1e-10) { throw new InvalidOperationException("本轮试验只处理直线轮廓，所选多段线含圆弧段。"); }
            points.Add(To2d(polyline.GetPoint3dAt(i)));
        }
        return points;
    }

    private static bool SameRing(List<Point2d> a, List<Point2d> b)
    {
        if (a.Count != b.Count) { return false; }
        for (var offset = 0; offset < b.Count; offset++)
        {
            if (Enumerable.Range(0, a.Count).All(i => a[i].GetDistanceTo(b[(offset + i) % b.Count]) <= ContourGraph.Tolerance) ||
                Enumerable.Range(0, a.Count).All(i => a[i].GetDistanceTo(b[(offset - i + b.Count) % b.Count]) <= ContourGraph.Tolerance)) { return true; }
        }
        return false;
    }

    private static bool IsSupportDiagonal(ContourGraph.Edge edge, List<Point2d> ring)
    {
        var minX = ring.Min(p => p.X); var minY = ring.Min(p => p.Y);
        var maxX = ring.Max(p => p.X); var maxY = ring.Max(p => p.Y);
        bool Same(Point2d a, Point2d b) =>
            (edge.Start.GetDistanceTo(a) < .1 && edge.End.GetDistanceTo(b) < .1) ||
            (edge.End.GetDistanceTo(a) < .1 && edge.Start.GetDistanceTo(b) < .1);
        return Same(new Point2d(minX, minY), new Point2d(maxX, maxY)) || Same(new Point2d(minX, maxY), new Point2d(maxX, minY));
    }

    private static void CheckPlane(Point3d point)
    {
        if (Math.Abs(point.Z) > .01) { throw new InvalidOperationException("本轮只处理WCS的Z=0平面轮廓。"); }
    }

    private static Point2d To2d(Point3d point) => new Point2d(point.X, point.Y);
}
