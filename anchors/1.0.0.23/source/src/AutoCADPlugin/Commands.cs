using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Colors;
using System;
using System.Collections.Generic;
using System.Reflection;

[assembly: CommandClass(typeof(AutoCADPlugin.Commands))]

namespace AutoCADPlugin;

public sealed class Commands
{
    [CommandMethod("SD_REBAR")]
    public void Rebar()
    {
        var document = Application.DocumentManager.MdiActiveDocument;
        var editor = document.Editor;
        try
        {
            var options = new PromptSelectionOptions { MessageForAdding = "\n试验23：框选已放大四倍的完整结构轮廓（含支撑），向内偏移50：" };
            var selected = editor.GetSelection(options);
            if (selected.Status != PromptStatus.OK) return;
            using var tr = document.Database.TransactionManager.StartTransaction();
            var entities = new List<Entity>();
            foreach (ObjectId id in selected.Value.GetObjectIds())
                if (tr.GetObject(id, OpenMode.ForRead) is Entity entity) entities.Add(entity);
            var plan = OffsetRebar.Create(entities);
            var count = OffsetRebar.Write(document.Database, tr, plan);
            tr.Commit();
            Application.SetSystemVariable("FILLMODE", 1);
            editor.Regen();
            editor.WriteMessage($"\n试验23完成：{plan.Boundaries.Count}个配筋区域，{plan.SupportCount}个支撑区域已扣除，生成{count}条闭合纵筋；向内偏移50，洋红，宽35。本轮未生成点筋。\n");
        }
        catch (System.Exception error)
        {
            editor.WriteMessage($"\nSD_REBAR：{error.Message}\n");
        }
    }

    [CommandMethod("HELLOCAD")]
    public void HelloCad()
    {
        Document document = Application.DocumentManager.MdiActiveDocument;
        Editor editor = document.Editor;
        editor.WriteMessage("\nAutoCAD 2020 .NET 插件环境已就绪。\n");
    }

    [CommandMethod("SD_VERSION")]
    public void Version()
    {
        var document = Application.DocumentManager.MdiActiveDocument;
        var assembly = Assembly.GetExecutingAssembly();
        document.Editor.WriteMessage($"\n结构大样插件版本：{assembly.GetName().Version}；加载路径：{assembly.Location}\n");
    }

    [CommandMethod("SD_SETUP")]
    public void SetupStandards()
    {
        var document = Application.DocumentManager.MdiActiveDocument;
        try
        {
            using var transaction = document.Database.TransactionManager.StartTransaction();
            Standards.Ensure(document.Database, transaction);
            transaction.Commit();
            document.Editor.WriteMessage("\n结构大样标准图层已准备完成。楼层梁、楼层板等支撑区域请使用图层 SD-REGION 圈定。\n");
        }
        catch (System.Exception exception)
        {
            document.Editor.WriteMessage($"\nSD_SETUP 失败：{exception.GetType().FullName}：{exception.Message}\n{exception.StackTrace}\n");
        }
    }

    [CommandMethod("SD_AUTO_DETAIL")]
    public void AutoDetail()
    {
        var editor = Application.DocumentManager.MdiActiveDocument.Editor;
        try
        {
            AutoDetailCore();
        }
        catch (System.Exception exception)
        {
            editor.WriteMessage($"\nSD_AUTO_DETAIL 失败：{exception.GetType().FullName}：{exception.Message}\n{exception.StackTrace}\n");
        }
    }

    private void AutoDetailCore()
    {
        var document = Application.DocumentManager.MdiActiveDocument;
        var editor = document.Editor;

        var first = editor.GetPoint("\n请选择建筑大样区域的第一个角点：");
        if (first.Status != PromptStatus.OK) return;
        var secondOptions = new PromptCornerOptions("请选择建筑大样区域的对角点：", first.Value)
        {
            UseDashedLine = true
        };
        var second = editor.GetCorner(secondOptions);
        if (second.Status != PromptStatus.OK) return;

        var output = editor.GetPoint("指定结构大样插入点（比例 1:100 → 1:25）：");
        if (output.Status != PromptStatus.OK) return;

        var regionFilter = new SelectionFilter(new[]
        {
            new TypedValue((int)DxfCode.Start, "LWPOLYLINE"),
            new TypedValue((int)DxfCode.LayerName, Standards.RegionLayer)
        });
        var selection = editor.SelectWindow(first.Value, second.Value, regionFilter);
        if (selection.Status != PromptStatus.OK || selection.Value.Count == 0)
        {
            editor.WriteMessage("\n框选区域内没有找到 SD-REGION 图层的闭合多段线。请先用该图层圈定楼层梁、楼层板等支撑区域。\n");
            return;
        }

        using var transaction = document.Database.TransactionManager.StartTransaction();
        Standards.Ensure(document.Database, transaction);

        var sourcePolylines = new List<Polyline>();
        foreach (SelectedObject selected in selection.Value)
        {
            if (selected == null) continue;
            var polyline = transaction.GetObject(selected.ObjectId, OpenMode.ForRead) as Polyline;
            if (polyline == null || !polyline.Closed || polyline.NumberOfVertices < 3) continue;
            sourcePolylines.Add(polyline);
        }

        if (sourcePolylines.Count == 0)
        {
            editor.WriteMessage("\n没有找到有效的闭合 SD-REGION 多段线。\n");
            transaction.Abort();
            return;
        }

        var modelSpace = (BlockTableRecord)transaction.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForWrite);
        var sourceBase = sourcePolylines[0].GetPoint2dAt(0);
        // Crossing selection keeps structural edges that cross the picked window;
        // the control polygon below performs the precise region test.
        var sourceEntitySelection = editor.SelectCrossingWindow(first.Value, second.Value);
        var outlineSegments = ArchitecturalOutlineExtractor.Extract(
            transaction, sourceEntitySelection.Status == PromptStatus.OK ? sourceEntitySelection.Value : null);
        if (outlineSegments.Count == 0)
        {
            editor.WriteMessage("\n未找到 WALL 或 COLUMN 轮廓。本版本严格按这两个图层识别，不会把其他图层当作外轮廓。\n");
        }
        var transformedOutline = new List<OutlineSegment>();
        foreach (var segment in outlineSegments)
        {
            var start = GeometryTools.TransformPoint(segment.Start, sourceBase, output.Value, Standards.ScaleFactor);
            var end = GeometryTools.TransformPoint(segment.End, sourceBase, output.Value, Standards.ScaleFactor);
            transformedOutline.Add(new OutlineSegment(start, end, segment.TargetLayer));
        }

        var supportTargets = new List<Polyline>();
        foreach (var source in sourcePolylines)
        {
            var target = GeometryTools.TransformFromBase(source, sourceBase, output.Value, Standards.ScaleFactor);
            // SD-REGION is copied exactly as the user drew it.  No clipping,
            // inferred intersections, or generated replacement edges are
            // applied; only the X and label are added afterward.
            DetailWriter.WriteControlBoundary(modelSpace, transaction, target);
            supportTargets.Add(target);
        }
        DetailWriter.WriteSupportAnnotations(modelSpace, transaction, supportTargets, "楼层梁");

        foreach (var segment in transformedOutline)
        {
            DetailWriter.WriteOutlineSegment(modelSpace, transaction, segment.Start, segment.End, segment.TargetLayer);
        }

        transaction.Commit();
        editor.WriteMessage($"\n已识别 {outlineSegments.Count} 条轮廓线和 {sourcePolylines.Count} 个支撑区域。当前阶段不生成纵筋和点筋；已按 1:100 → 1:25 放大并输出结构标准轮廓。\n");
    }
}
