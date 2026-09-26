using System;
using System.Collections.Generic;
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
                MessageForAdding = "\n点筋试验35：框选完整四倍轮廓、支撑及已有纵筋；按纵筋交角布点，排除弯锚："
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
            var result = CornerPointRebar.Create(context, bars);
            var centers = new List<Point2d>();
            foreach (var center in result.Centers) { centers.Add(context.Origin + new Vector2d(center.X, center.Y)); }
            var written = PointRebarWriter.Write(document.Database, transaction, centers);
            transaction.Commit();
            Application.SetSystemVariable("FILLMODE", 1);
            editor.Regen();
            editor.WriteMessage($"\n点筋试验35：按纵筋自身转折及实际交点识别，已排除{result.ExcludedAnchorCount}个弯锚端。合格阳角{centers.Count}处（梁上方延伸区域{result.ExtensionCount}处），新增{written}个，已有或重叠跳过{centers.Count - written}个；非内侧、越界或空间不足候选跳过{result.RejectedCount}处。点筋实际外径100，圆心距纵筋中心线67.5，打叉区域禁止生成；未布置沿边分布点筋。\n");
        }
        catch (System.Exception error)
        {
            editor.WriteMessage($"\nSD_POINT_REBAR：{error.Message}\n");
        }
    }
}

