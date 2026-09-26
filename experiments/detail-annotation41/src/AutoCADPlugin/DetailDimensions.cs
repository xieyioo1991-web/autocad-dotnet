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
        var fixedInk = new AnnotationInk();
        foreach (var entity in obstacles) { fixedInk.Add(entity); }
        var ink = Combine(fixedInk, Array.Empty<RotatedDimension>());
        var output = new List<RotatedDimension>();
        foreach (var item in plan.Items.OrderBy(i => i.Local).ThenBy(i => i.Datum).ThenByDescending(i => i.Horizontal).ThenBy(i => i.Length))
        {
            var dimension = (RotatedDimension)reference.Dimension.Clone();
            try { space.AppendEntity(dimension); tr.AddNewlyCreatedDBObject(dimension, true); }
            catch { dimension.Dispose(); throw; }
            dimension.XLine1Point = Point(item.First); dimension.XLine2Point = Point(item.Second);
            dimension.Rotation = item.Horizontal ? 0 : Math.PI / 2;
            dimension.DimensionText = "<>";
            AnnotationInk? accepted = null;
            for (var pass = 0; pass < 3 && accepted == null; pass++)
            {
                foreach (var position in Candidates(item, plan.Bounds))
                {
                    dimension.DimLinePoint = Point(position);
                    dimension.UsingDefaultTextPosition = true;
                    dimension.RecomputeDimensionBlock(true);
                    var originalText = dimension.TextPosition;
                    foreach (var shift in pass < 2 ? new[] { 0.0 } : new[] { 350.0, -350.0, 700.0, -700.0 })
                    {
                        if (shift != 0)
                        {
                            dimension.TextPosition = originalText + new Vector3d(Math.Cos(dimension.Rotation) * shift, Math.Sin(dimension.Rotation) * shift, 0);
                            dimension.RecomputeDimensionBlock(true);
                        }
                        var candidate = new AnnotationInk(); candidate.Add(dimension);
                        if (Conflicts(candidate, ink) && (pass == 0 || !TryMoveEarlierText(output, fixedInk, candidate))) { continue; }
                        ink = Combine(fixedInk, output); accepted = candidate; break;
                    }
                    if (accepted != null) { break; }
                }
            }
            if (accepted == null)
            { throw new InvalidOperationException($"尺寸{item.Length / 4:F1}（{(item.Horizontal ? "水平" : "竖向")}）附近无法找到不压文字的位置，已回滚本次标注。"); }
            DetailAnnotationIdentity.Set(dimension, tr, source, item.Local ? "LocalDimension" : item.Datum ? "DatumDimension" : "MainDimension");
            Trace?.Invoke($"ACCEPT {item.Length / 4:F1} H={item.Horizontal} local={item.Local} datum={item.Datum} at {dimension.DimLinePoint}");
            ink.Text.AddRange(accepted.Text); ink.Lines.AddRange(accepted.Lines); ink.Solids.AddRange(accepted.Solids);
            output.Add(dimension);
        }
        return output;
    }

    private static AnnotationInk Combine(AnnotationInk fixedInk, IEnumerable<RotatedDimension> dimensions)
    {
        var result = new AnnotationInk(); result.Text.AddRange(fixedInk.Text); result.Lines.AddRange(fixedInk.Lines); result.Solids.AddRange(fixedInk.Solids);
        foreach (var dimension in dimensions) { result.Add(dimension); }
        return result;
    }

    private static bool TryMoveEarlierText(List<RotatedDimension> dimensions, AnnotationInk fixedInk, AnnotationInk next)
    {
        if (Conflicts(next, fixedInk)) { return false; }
        var moved = new List<Tuple<RotatedDimension, Point3d, bool>>();
        foreach (var dimension in dimensions)
        {
            var own = new AnnotationInk(); own.Add(dimension);
            if (!Conflicts(next, own)) { continue; }
            var initial = dimension.TextPosition; var automatic = dimension.UsingDefaultTextPosition;
            moved.Add(Tuple.Create(dimension, initial, automatic));
            var other = Combine(fixedInk, dimensions.Where(d => d != dimension));
            other.Text.AddRange(next.Text); other.Lines.AddRange(next.Lines); other.Solids.AddRange(next.Solids);
            var success = false;
            foreach (var delta in new[] { 250.0, -250.0, 500.0, -500.0, 800.0, -800.0, 1200.0, -1200.0 })
            {
                dimension.TextPosition = initial + new Vector3d(Math.Cos(dimension.Rotation) * delta, Math.Sin(dimension.Rotation) * delta, 0);
                dimension.RecomputeDimensionBlock(true);
                var trial = new AnnotationInk(); trial.Add(dimension);
                if (Conflicts(trial, other)) { continue; }
                success = true; break;
            }
            if (!success) { Restore(moved); return false; }
        }
        if (Conflicts(next, Combine(fixedInk, dimensions))) { Restore(moved); return false; }
        return true;
    }

    private static void Restore(List<Tuple<RotatedDimension, Point3d, bool>> moved)
    {
        foreach (var item in moved)
        { item.Item1.TextPosition = item.Item2; item.Item1.UsingDefaultTextPosition = item.Item3; item.Item1.RecomputeDimensionBlock(true); }
    }

    private static bool Conflicts(AnnotationInk candidate, AnnotationInk scene) =>
        candidate.Text.Any(t => scene.Text.Any(other => t.Expand(70).Overlaps(other.Expand(40))) ||
            scene.Solids.Any(t.Expand(40).Overlaps) || scene.Lines.Any(e => SupportLabelLayout.Hits(e, t.Expand(35)))) ||
        candidate.Lines.Any(e => scene.Text.Any(t => SupportLabelLayout.Hits(e, t.Expand(20))));

    private static IEnumerable<Point2d> Candidates(DetailDimensionPlan.Item item, Box bounds)
    {
        var center = new Point2d((item.First.X + item.Second.X) / 2, (item.First.Y + item.Second.Y) / 2);
        if (item.Local)
        {
            foreach (var distance in new[] { 500.0, 900.0, 1400.0, 2000.0, 2800.0 })
            foreach (var side in new[] { -1, 1 })
            { yield return item.Horizontal ? new Point2d(center.X, center.Y + side * distance) : new Point2d(center.X + side * distance, center.Y); }
            yield break;
        }
        for (var lane = item.Datum ? 2 : 0; lane < 12; lane++)
        { yield return item.Horizontal ? new Point2d(center.X, bounds.Top + 800 + 650 * lane) : new Point2d(bounds.Left - 800 - 650 * lane, center.Y); }
    }
    private static Point3d Point(Point2d p) => new Point3d(p.X, p.Y, 0);
}
