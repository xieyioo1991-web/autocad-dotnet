using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
namespace AutoCADPlugin;
internal static class PointRebarGeneration
{
    internal sealed class Result
    {
        public DistributedPointRebar.Result Layout { get; set; } = null!;
        public int Total { get; set; }
        public int Written { get; set; }
    }
    public static Result Write(Database database, Transaction transaction, IReadOnlyList<Entity> entities)
    {
        var context = OffsetRebar.Create(entities);
        var bars = new List<RebarPath>();
        foreach (var entity in entities)
        {
            if (entity.Layer != Standards.ReinforcementLayer) { continue; }
            if (entity is not Polyline bar || Math.Abs(bar.ConstantWidth - 35) > 1e-6 ||
                Math.Abs(bar.Elevation) > 1e-6 || (bar.Normal - Vector3d.ZAxis).Length > 1e-6)
            { throw new InvalidOperationException("本轮点筋要求选择宽35、XY平面上的纵筋直线多段线。"); }
            var points = new List<Point2d>();
            for (var i = 0; i < bar.NumberOfVertices; i++)
            {
                if (Math.Abs(bar.GetBulgeAt(i)) > 1e-6)
                { throw new InvalidOperationException("本轮暂不处理圆弧纵筋的阳角点筋。"); }
                var point = bar.GetPoint2dAt(i) - context.Origin;
                points.Add(new Point2d(point.X, point.Y));
            }
            bars.Add(new RebarPath(points, bar.Closed));
        }
        if (bars.Count == 0) { throw new InvalidOperationException("请先执行SD_REBAR，并把已生成的洋红纵筋一起框选。"); }
        var corners = CornerPointRebar.Create(context, bars);
        var result = DistributedPointRebar.Create(context, bars, corners);
        var centers = new List<Point2d>();
        foreach (var center in result.Centers) { centers.Add(context.Origin + new Vector2d(center.X, center.Y)); }
        foreach (var dot in entities.OfType<Polyline>().Where(e => e.Layer == Standards.PointReinforcementLayer))
        {
            if (dot.NumberOfVertices != 2 || !dot.Closed)
            { throw new InvalidOperationException("框选中含已有点筋；请先删除本大样的旧点筋，再按新规则生成。"); }
            var center = GeometryTools.MidPoint(dot.GetPoint2dAt(0), dot.GetPoint2dAt(1));
            if (!centers.Any(p => p.GetDistanceTo(center) <= ContourGraph.Tolerance))
            { throw new InvalidOperationException("本大样已有与38版排布不一致的点筋；请先删除旧点筋，再执行SD_POINT_REBAR，避免旧青点残留。"); }
        }
        var written = PointRebarWriter.Write(database, transaction, centers);
        return new Result { Layout = result, Total = centers.Count, Written = written };
    }
}
