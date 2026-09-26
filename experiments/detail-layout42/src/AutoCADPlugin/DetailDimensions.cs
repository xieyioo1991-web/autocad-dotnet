using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Box = AutoCADPlugin.SupportLabelLayout.Box;

namespace AutoCADPlugin;

internal static class DetailDimensions
{
    internal static Action<string>? Trace { get; set; }

    public static List<RotatedDimension> Write(Database db, Transaction tr, BlockTableRecord space,
        DetailReference reference, DetailDimensionPlan.Plan plan, IReadOnlyList<Entity> obstacles, string source)
    {
        var output = new List<RotatedDimension>();
        // Establish every dimension line before moving any text. All main
        // horizontal dimensions share Y; all main vertical dimensions share X.
        foreach (var item in plan.Items.OrderBy(i => i.Local).ThenByDescending(i => i.Horizontal).ThenBy(i => i.Length))
        {
            var dimension = (RotatedDimension)reference.Dimension.Clone();
            try { space.AppendEntity(dimension); tr.AddNewlyCreatedDBObject(dimension, true); }
            catch { dimension.Dispose(); throw; }
            dimension.XLine1Point = Point(item.First); dimension.XLine2Point = Point(item.Second);
            dimension.Rotation = item.Horizontal ? 0 : Math.PI / 2;
            dimension.DimensionText = "<>";
            dimension.Dimtmove = 2; // Text relocation must not move the dimension line.
            dimension.DimLinePoint = Point(LinePosition(item, plan.Bounds));
            dimension.UsingDefaultTextPosition = true;
            dimension.RecomputeDimensionBlock(true);
            DetailAnnotationIdentity.Set(dimension, tr, source, item.Local ? "LocalDimension" : "MainDimension");
            output.Add(dimension);
        }
        ArrangeText(output, obstacles);
        return output;
    }

    private static Point2d LinePosition(DetailDimensionPlan.Item item, Box bounds)
    {
        var center = new Point2d((item.First.X + item.Second.X) / 2, (item.First.Y + item.Second.Y) / 2);
        if (!item.Local)
        { return item.Horizontal ? new Point2d(center.X, bounds.Top + 800) : new Point2d(bounds.Left - 800, center.Y); }
        // Small steps retain their own nearby chain, independent of crowding
        // among the dimension text or reinforcement leaders.
        return item.Horizontal ? new Point2d(center.X, Math.Min(item.First.Y, item.Second.Y) - 500)
            : new Point2d(Math.Min(item.First.X, item.Second.X) - 500, center.Y);
    }

    private static void ArrangeText(IReadOnlyList<RotatedDimension> dimensions, IReadOnlyList<Entity> obstacles)
    {
        var fixedInk = new AnnotationInk();
        foreach (var entity in obstacles) { fixedInk.Add(entity); }
        var placedText = new List<Box>();
        foreach (var dimension in dimensions)
        {
            var linePosition = dimension.DimLinePoint;
            var initial = dimension.TextPosition;
            var otherLines = new AnnotationInk();
            foreach (var other in dimensions.Where(d => d != dimension)) { otherLines.Add(other); }
            var accepted = false;
            foreach (var delta in TextOffsets(dimension.Rotation))
            {
                dimension.TextPosition = initial + delta;
                dimension.RecomputeDimensionBlock(true);
                if (dimension.DimLinePoint.DistanceTo(linePosition) > .1)
                { throw new InvalidOperationException("CAD移动尺寸文字时改变了尺寸线，已回滚；不能拆开主尺寸链。"); }
                var ink = new AnnotationInk(); ink.Add(dimension);
                if (ink.Text.Any(box => TextConflict(box, fixedInk, otherLines, placedText) ||
                    ink.Lines.Any(e => SupportLabelLayout.Hits(e, box.Expand(20))))) { continue; }
                placedText.AddRange(ink.Text); accepted = true; break;
            }
            if (!accepted)
            { throw new InvalidOperationException($"尺寸{dimension.Measurement:F1}附近文字无法避让；尺寸线保持统一，已回滚本次标注。"); }
            Trace?.Invoke($"DIM {DetailAnnotationIdentity.Role(dimension)} rotation={dimension.Rotation:F3} line={dimension.DimLinePoint} text={dimension.TextPosition}");
        }
    }

    private static bool TextConflict(Box box, AnnotationInk fixedInk, AnnotationInk dimensions, List<Box> placed) =>
        fixedInk.Text.Concat(placed).Any(t => box.Expand(55).Overlaps(t.Expand(35))) ||
        fixedInk.Solids.Any(box.Expand(25).Overlaps) ||
        fixedInk.Lines.Concat(dimensions.Lines).Any(e => SupportLabelLayout.Hits(e, box.Expand(20)));

    private static IEnumerable<Vector3d> TextOffsets(double rotation)
    {
        var along = new Vector3d(Math.Cos(rotation), Math.Sin(rotation), 0);
        var outward = new Vector3d(-Math.Sin(rotation), Math.Cos(rotation), 0);
        foreach (var distance in new[] { 0.0, 350.0, 700.0, 1050.0, 1500.0, 2100.0, 2800.0 })
        foreach (var shift in new[] { 0.0, 250.0, -250.0, 500.0, -500.0, 800.0, -800.0, 1200.0, -1200.0 })
        { yield return outward * distance + along * shift; }
    }
    private static Point3d Point(Point2d p) => new Point3d(p.X, p.Y, 0);
}
