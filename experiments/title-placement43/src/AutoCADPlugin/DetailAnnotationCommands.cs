using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using System.Linq;

[assembly: CommandClass(typeof(AutoCADPlugin.DetailAnnotationCommands))]
namespace AutoCADPlugin;

public sealed class DetailAnnotationCommands
{
    [CommandMethod("SD_DETAIL_LABEL")]
    public void Run()
    {
        var doc = Application.DocumentManager.MdiActiveDocument; var editor = doc.Editor;
        try
        {
            var selected = editor.GetSelection(new PromptSelectionOptions { MessageForAdding = "\n43试验：框选完整四倍结构大样，生成尺寸、图名并共同避让文字：" });
            if (selected.Status != PromptStatus.OK) { return; }
            using var tr = doc.Database.TransactionManager.StartTransaction();
            var entities = selected.Value.GetObjectIds().Select(id => (Entity)tr.GetObject(id, OpenMode.ForRead)).ToList();
            var result = DetailAnnotation.Write(doc.Database, tr, entities);
            tr.Commit(); editor.Regen();
            editor.WriteMessage($"\n43试验：尺寸{result.Dimensions}条（局部{result.LocalDimensions}条），图名及圆圈1已复制；重新布置纵筋指引{result.RebarLabels}条、梁板指引{result.SupportLabels}条。水平轴线{result.HorizontalAxes}、竖向轴线{result.VerticalAxes}；未生成轴号。\n");
            if (result.HorizontalAxes + result.VerticalAxes == 0)
            { editor.WriteMessage("\n未找到轴线，按支撑边缘定位。要继承建筑轴线、标高，请用43的SD_AUTO_DETAIL重新生成轮廓。\n"); }
        }
        catch (System.Exception error) { editor.WriteMessage("\nSD_DETAIL_LABEL失败，未提交本次图形修改：" + error.Message + "\n"); }
    }
}


