using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;

[assembly: CommandClass(typeof(AutoCADPlugin.RebarAnnotationCommands))]

namespace AutoCADPlugin;

public sealed class RebarAnnotationCommands
{
    [CommandMethod("SD_REBAR_LABEL")]
    public void LabelRebars()
    {
        var document = Application.DocumentManager.MdiActiveDocument;
        var editor = document.Editor;
        try
        {
            var selection = editor.GetSelection(new PromptSelectionOptions
            { MessageForAdding = "\n标注试验41：框选完整结构大样（纵筋、点筋、支撑及原梁板文字指引），每条纵筋标注%%1328@150：" });
            if (selection.Status != PromptStatus.OK) { return; }
            using var transaction = document.Database.TransactionManager.StartTransaction();
            var entities = new List<Entity>();
            foreach (var id in selection.Value.GetObjectIds())
            { if (transaction.GetObject(id, OpenMode.ForRead) is Entity entity) { entities.Add(entity); } }
            var result = RebarAnnotation.Write(document.Database, transaction, entities);
            transaction.Commit(); editor.Regen();
            editor.WriteMessage($"\n40版：完成{result.Bars}根纵筋指引，文字统一%%1328@150；共同排版{result.Supports}处梁板标注，替换旧标注实体{result.ReplacedEntities}个。纵筋及点筋位置未改动。可再次执行本命令重新排版。\n");
        }
        catch (System.Exception error)
        { editor.WriteMessage($"\nSD_REBAR_LABEL失败，未提交图形修改：{error.Message}\n"); }
    }
}

