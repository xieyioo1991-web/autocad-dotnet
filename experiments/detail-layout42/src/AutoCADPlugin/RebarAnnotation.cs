using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Box = AutoCADPlugin.SupportLabelLayout.Box;
using Edge = AutoCADPlugin.ContourGraph.Edge;

namespace AutoCADPlugin;

internal static class RebarAnnotation
{
    internal sealed class Result
    {
        public int Bars { get; set; }
        public int Supports { get; set; }
        public int ReplacedEntities { get; set; }
        public List<AnnotationLayout.Placement> Placements { get; } = new List<AnnotationLayout.Placement>();
    }

    // Caller owns the transaction. Any failed measurement, migration or placement
    // aborts before commit; borrowed CAD entities are never disposed here.
    public static Result Write(Database database, Transaction transaction, IReadOnlyList<Entity> selected, bool requireBars = true)
    {
        var bars = selected.Where(e => e.Layer == Standards.ReinforcementLayer).ToList();
        if (bars.Count == 0 && requireBars) { throw new InvalidOperationException("请把已有纵筋、完整轮廓、支撑及梁板文字指引一起框选。"); }
        var supports = selected.OfType<Polyline>().Where(p => OutlineRole.Get(p) == "Support").ToList();
        foreach (var bar in bars) { ValidateBar(bar); }
        var space = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);
        if (selected.Any(e => e.OwnerId != space.ObjectId))
        { throw new InvalidOperationException("仅支持当前空间中的原生实体，请勿混选块内部或其他空间的对象。"); }
        var all = space.Cast<ObjectId>().Select(id => (Entity)transaction.GetObject(id, OpenMode.ForRead)).ToList();
        var keys = new HashSet<string>(bars.Concat<Entity>(supports).Select(e => e.Handle.ToString()), StringComparer.Ordinal);
        var replaced = all.Where(e => keys.Contains(AnnotationIdentity.Get(e))).ToList();
        var legacy = LegacySupportAnnotations.Find(selected, supports);
        foreach (var match in legacy) { replaced.Add(match.Text); replaced.AddRange(match.Leaders); }
        var replacedIds = new HashSet<ObjectId>(replaced.Select(e => e.ObjectId));
        var style = AnnotationStyle.Ensure(database, transaction);
        var scene = new AnnotationLayout.Scene();
        foreach (var bar in bars.Cast<Polyline>())
        { scene.Targets.Add(new AnnotationLayout.Target(bar.Handle.ToString(), AnnotationLayout.RebarText, Segments(bar), Measure(database, style, AnnotationLayout.RebarText))); }
        foreach (var support in supports)
        {
            var polygon = LegacySupportAnnotations.Vertices(support); var box = Box.Around(polygon);
            var label = SupportClassification.Label(box.Right - box.Left, box.Top - box.Bottom, "楼层梁");
            scene.Targets.Add(new AnnotationLayout.Target(support.Handle.ToString(), label, Segments(support), Measure(database, style, label), polygon));
        }
        var drawingBounds = Box.Around(scene.Targets.SelectMany(t => t.Segments).SelectMany(e => new[] { e.Start, e.End }).ToList()).Expand(10000);
        foreach (var entity in all.Where(e => !replacedIds.Contains(e.ObjectId))) { AddObstacle(scene, entity, drawingBounds); }
        var placements = AnnotationLayout.Create(scene);
        Standards.Ensure(database, transaction);
        foreach (var entity in replaced.Distinct()) { entity.UpgradeOpen(); entity.Erase(); }
        foreach (var placement in placements) { WritePlacement(database, space, transaction, style, placement); }
        var result = new Result { Bars = bars.Count, Supports = supports.Count, ReplacedEntities = replacedIds.Count };
        result.Placements.AddRange(placements);
        return result;
    }

    private static void ValidateBar(Entity entity)
    {
        if (entity is not Polyline p || p.NumberOfVertices < 2 || Math.Abs(p.ConstantWidth - 35) > .1 ||
            Math.Abs(p.Elevation) > .1 || (p.Normal - Vector3d.ZAxis).Length > 1e-6 ||
            Enumerable.Range(0, p.NumberOfVertices).Any(i => Math.Abs(p.GetBulgeAt(i)) > 1e-8))
        { throw new InvalidOperationException($"纵筋句柄{entity.Handle}不是本阶段的XY平面宽35直线多段线，未生成不完整标注。"); }
    }

    private static Box Measure(Database database, ObjectId style, string content)
    {
        using var text = new DBText();
        AnnotationStyle.ApplyText(text, database, style, content); text.Position = Point3d.Origin;
        var extents = text.GeometricExtents;
        var box = new Box(extents.MinPoint.X, extents.MinPoint.Y, extents.MaxPoint.X, extents.MaxPoint.Y);
        if (box.Right <= box.Left || box.Top <= box.Bottom)
        { throw new InvalidOperationException("当前文字样式无法取得有效字形范围，未生成标注。请检查该图文字字体。"); }
        return box;
    }

    private static void AddObstacle(AnnotationLayout.Scene scene, Entity entity, Box range)
    {
        if (entity is Dimension)
        {
            var ink = new AnnotationInk(); ink.Add(entity);
            scene.Ink.AddRange(ink.Lines); scene.Blocks.AddRange(ink.Text); scene.Blocks.AddRange(ink.Solids);
            return;
        }
        // Extents failures are intentionally propagated: an unmeasurable visible
        // object cannot silently disappear from collision checking.
        var extents = entity.GeometricExtents;
        var box = new Box(extents.MinPoint.X, extents.MinPoint.Y, extents.MaxPoint.X, extents.MaxPoint.Y);
        if (!range.Expand(1).Overlaps(box.Expand(1))) { return; }
        if (entity is Line line)
        { scene.Ink.Add(new Edge(LegacySupportAnnotations.Point(line.StartPoint), LegacySupportAnnotations.Point(line.EndPoint))); }
        else if (entity is Polyline p && p.Layer != Standards.PointReinforcementLayer &&
            Enumerable.Range(0, p.NumberOfVertices).All(i => Math.Abs(p.GetBulgeAt(i)) < 1e-8))
        { scene.Ink.AddRange(Segments(p)); }
        else { scene.Blocks.Add(box); }
    }

    internal static List<Edge> Segments(Polyline p)
    {
        var points = LegacySupportAnnotations.Vertices(p);
        if (p.Closed) { points.Add(points[0]); }
        return SupportLabelLayout.Edges(points).Where(e => e.Start.GetDistanceTo(e.End) > .1).ToList();
    }

    private static void WritePlacement(Database database, BlockTableRecord space, Transaction transaction, ObjectId style, AnnotationLayout.Placement placement)
    {
        foreach (var edge in placement.Edges)
        {
            using var line = new Line(new Point3d(edge.Start.X, edge.Start.Y, 0), new Point3d(edge.End.X, edge.End.Y, 0));
            line.SetDatabaseDefaults(database); line.Layer = Standards.OtherThinLayer;
            line.ColorIndex = placement.Target.IsRebar ? 256 : 7;
            line.Linetype = "ByLayer"; line.LineWeight = LineWeight.ByLayer; line.LinetypeScale = 1;
            space.AppendEntity(line); transaction.AddNewlyCreatedDBObject(line, true);
            AnnotationIdentity.Set(line, transaction, placement.Target.Key);
        }
        using var text = new DBText();
        AnnotationStyle.ApplyText(text, database, style, placement.Target.Text);
        text.Position = new Point3d(placement.TextPosition.X, placement.TextPosition.Y, 0);
        space.AppendEntity(text); transaction.AddNewlyCreatedDBObject(text, true);
        AnnotationIdentity.Set(text, transaction, placement.Target.Key);
    }
}
