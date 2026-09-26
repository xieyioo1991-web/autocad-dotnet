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
                MessageForAdding = "\n点筋试验43：框选完整四倍轮廓、支撑及已有纵筋；接筋端相切收尾，支撑/自由端400，间距不超过800："
            });
            if (selection.Status != PromptStatus.OK) { return; }
            using var transaction = document.Database.TransactionManager.StartTransaction();
            var entities = new List<Entity>();
            foreach (var id in selection.Value.GetObjectIds())
            {
                if (transaction.GetObject(id, OpenMode.ForRead) is Entity entity) { entities.Add(entity); }
            }
            var generated = PointRebarGeneration.Write(document.Database, transaction, entities);
            var result = generated.Layout;
            var written = generated.Written;
            transaction.Commit();
            Application.SetSystemVariable("FILLMODE", 1);
            editor.Regen();
            editor.WriteMessage($"\n点筋试验43：固定角点{result.FixedCenters.Count}个，内部交点参与均布{result.RedistributedCorners}个，完成{result.Rows.Count}个排布区间；共{generated.Total}个点筋，新增{written}个，已有{generated.Total - written}个。接筋端按相切位置收尾，横排接管竖排交接尾点；支撑边缘/真实自由端仍为圆心400，中间等分间距不超过800。点筋实际外径100、圆心距纵筋中心线67.5，打叉区域禁止生成。\n");
            foreach (var warning in result.Warnings) { editor.WriteMessage("\n排布提示：" + warning); }
            if (result.Warnings.Count > 0) { editor.WriteMessage("\n以上冲突区间未补齐，请按提示检查；固定角点未移动。\n"); }
        }
        catch (System.Exception error)
        {
            editor.WriteMessage($"\nSD_POINT_REBAR：{error.Message}\n");
        }
    }
}





