using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Colors;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;

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
            var options = new PromptSelectionOptions { MessageForAdding = "\n试验42：框选已放大四倍的完整结构轮廓（含支撑）；梁顶按较高竖边600判断倒U及延伸区：" };
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
            var straight = plan.AnchorEnds.Count(end => end.Kind == RebarAnchorage.AnchorKind.Straight);
            var bent = plan.AnchorEnds.Count(end => end.Kind == RebarAnchorage.AnchorKind.Bent);
            var vertical = plan.AnchorEnds.Count(end => end.Kind == RebarAnchorage.AnchorKind.VerticalBent);
            var lowerStraight = plan.AnchorEnds.Count(end => end.Kind == RebarAnchorage.AnchorKind.LowerStraight);
            var smallStraight = plan.AnchorEnds.Count(end => end.Kind == RebarAnchorage.AnchorKind.SmallStraight);
            var failed = plan.AnchorEnds.Where(end => end.Kind == RebarAnchorage.AnchorKind.Failed).ToList();
            var hits = plan.CornerBreaks.Ends.Count(end => end.Hit);
            var shortTop = plan.AnchorEnds.Count(end => end.Kind == RebarAnchorage.AnchorKind.BeamTopStraight);
            var curbs = plan.BeamTops.Regions.Count(region => !region.IsExtension);
            editor.WriteMessage($"\n试验42：生成{count}条纵筋，含倒U型{plan.BeamTops.Bars.Count}条（小反坎{curbs}条不作延伸区，两腿从原梁顶直锚1000）；短竖段斜筋从原梁顶直锚1000共{shortTop}端；普通直锚1000共{straight}端、弯锚{bent}端、下筋直锚600共{lowerStraight}端、小区域上筋直锚600共{smallStraight}端、斜筋转竖直800共{vertical}端、未锚固{failed.Count}端。识别水平上下筋{plan.BeamSides.PairCount}对，取消下筋的小区域{plan.BeamSides.SuppressedRegionCount}个，外侧自由竖段缩短50共{plan.BeamSides.ShortenedReturnCount}处。阴角断开{plan.CornerBreaks.CornerCount}处、延伸{plan.CornerBreaks.Ends.Count}端，其中遇筋截停{hits}端。尺寸按一位小数判断。洋红、宽35，点筋请另用SD_POINT_REBAR。\n");
            foreach (var notice in plan.BeamSides.Notices) { editor.WriteMessage($"\n提示：{notice}\n"); }
            foreach (var end in failed)
            {
                var point = plan.Origin + new Vector2d(end.Original.X, end.Original.Y);
                editor.WriteMessage($"\n未锚固断口（{point.X:F1}, {point.Y:F1}）：{end.Reason}\n");
            }
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

    [CommandMethod("SD_GENERATE_ALL")]
    public void GenerateAll()
    {
        var editor = Application.DocumentManager.MdiActiveDocument.Editor;
        try { AutoDetailCore(true); }
        catch (System.Exception error)
        { editor.WriteMessage("\nSD_GENERATE_ALL失败，未提交本次大样：" + error.Message + "\n"); }
    }
    private void AutoDetailCore(bool complete = false)
    {
        var document = Application.DocumentManager.MdiActiveDocument;
        var editor = document.Editor;

        var first = editor.GetPoint("\n请选择完整建筑大样（包含轴线和标高）的第一个角点：");
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

        var sourceSelection = editor.SelectCrossingWindow(first.Value, second.Value);
        if (sourceSelection.Status != PromptStatus.OK) { return; }
        var input = new ArchitecturalDetail.Input
        {
            Supports = sourcePolylines,
            Source = sourceSelection.Value.GetObjectIds().Select(id => (Entity)transaction.GetObject(id, OpenMode.ForRead)).ToList(),
            Outline = ArchitecturalOutlineExtractor.Extract(transaction, sourceSelection.Value),
            Output = output.Value
        };
        ArchitecturalDetail.Result architecture;
        if (complete)
        {
            var result = CompleteDetail.Write(document.Database, transaction, input);
            architecture = result.Architecture;
            transaction.Commit();
            editor.WriteMessage($"\n42一键生成：纵筋{result.Bars}条、点筋{result.Points}个、尺寸{result.Annotation.Dimensions}条、纵筋指引{result.Annotation.RebarLabels}条、梁板指引{result.Annotation.SupportLabels}条；图名及圈内1已生成。主尺寸链同线，指引最后避让。\n");
            foreach (var warning in result.Warnings) { editor.WriteMessage("\n提示：" + warning); }
            if (result.Warnings.Count > 0) { editor.WriteMessage("\n以上提示需检查，不能视为所有区间均已补齐。\n"); }
        }
        else
        {
            architecture = ArchitecturalDetail.Write(document.Database, transaction, input);
            transaction.Commit();
            editor.WriteMessage($"\n42轮廓阶段：{input.Outline.Count}条轮廓、{sourcePolylines.Count}个支撑，已放大4倍；轴线、标高在本阶段读取并输出。后续可执行SD_REBAR、SD_POINT_REBAR、SD_DETAIL_LABEL。\n");
            foreach (var warning in architecture.Warnings) { editor.WriteMessage("\n提示：" + warning); }
        }
        Application.SetSystemVariable("FILLMODE", 1);
        editor.Regen();
        editor.WriteMessage($"\n建筑数据读取：轴线{architecture.Axes}条、标高{architecture.Elevations}处；位置和数值来自本次框选，样式来自结构实例。未生成轴号。\n");
    }
}
