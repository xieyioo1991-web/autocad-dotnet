using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

[assembly: CommandClass(typeof(AutoCADPlugin.PointRebarCommands))]

namespace AutoCADPlugin;

public sealed class PointRebarCommands
{
    [CommandMethod("SD_POINT_REBAR")]
    public void PointRebar()
    {
        var document = Application.DocumentManager.MdiActiveDocument;
        var editor = document.Editor;
        try
        {
            var selection = editor.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = "\n点筋试验40：框选完整四倍轮廓、支撑及已有纵筋；接筋端相切收尾，支撑/自由端400，间距不超过800："
            });
            if (selection.Status != PromptStatus.OK) { return; }
            using var transaction = document.Database.TransactionManager.StartTransaction();
            var entities = new List<Entity>();
            foreach (var id in selection.Value.GetObjectIds())
            {
                if (transaction.GetObject(id, OpenMode.ForRead) is Entity entity) { entities.Add(entity); }
            }
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
            var written = PointRebarWriter.Write(document.Database, transaction, centers);
            transaction.Commit();
            Application.SetSystemVariable("FILLMODE", 1);
            editor.Regen();
            editor.WriteMessage($"\n点筋试验40：固定角点{result.FixedCenters.Count}个，内部交点参与均布{result.RedistributedCorners}个，完成{result.Rows.Count}个排布区间；共{centers.Count}个点筋，新增{written}个，已有{centers.Count - written}个。接筋端按相切位置收尾，横排接管竖排交接尾点；支撑边缘/真实自由端仍为圆心400，中间等分间距不超过800。点筋实际外径100、圆心距纵筋中心线67.5，打叉区域禁止生成。\n");
            foreach (var warning in result.Warnings) { editor.WriteMessage("\n排布提示：" + warning); }
            if (result.Warnings.Count > 0) { editor.WriteMessage("\n以上冲突区间未补齐，请按提示检查；固定角点未移动。\n"); }
        }
        catch (System.Exception error)
        {
            editor.WriteMessage($"\nSD_POINT_REBAR：{error.Message}\n");
        }
    }
}



