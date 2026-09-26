using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Box = AutoCADPlugin.SupportLabelLayout.Box;

namespace AutoCADPlugin;

internal static class DetailTitle
{
    // Reserved drafting space, not a fabricated axis or axis number. All units
    // here are enlarged model-space units, just like the cloned reference title.
    public const double AxisReserve = 1800;

    public static List<Entity> Write(Database db, Transaction tr, DetailReference reference,
        BlockTableRecord space, Box geometry, IReadOnlyList<Entity> obstacles, string source)
    {
        var title = reference.CloneTitle(db, tr, space.ObjectId);
        var template = Bounds(title);
        var scene = new TitleObstacles(tr, obstacles);
        var found = false;
        var offset = new Vector3d(0, 0, 0);
        var candidates = Candidates(geometry, template, scene.Guides).ToList();
        foreach (var candidate in candidates)
        {
            if (scene.Intersects(candidate)) { continue; }
            offset = new Vector3d(candidate.Left - template.Left, candidate.Top - template.Top, 0);
            found = true; break;
        }
        if (!found)
        {
            throw new InvalidOperationException($"图名附近{candidates.Count}个候选位置均被实际图形占用（首选位置障碍：{scene.Describe(candidates[0])}）。请将大样插入点移到较空的位置；本次大样已回滚。");
        }
        foreach (var entity in title)
        {
            entity.TransformBy(Matrix3d.Displacement(offset));
            if (entity is DBText text) { text.AdjustAlignment(db); }
            DetailAnnotationIdentity.Set(entity, tr, source, "Title");
        }
        return title;
    }

    private static IEnumerable<Box> Candidates(Box geometry, Box template, IReadOnlyList<Box> guides)
    {
        var width = template.Right - template.Left;
        var height = template.Top - template.Bottom;
        var center = (geometry.Left + geometry.Right) / 2;
        var top = geometry.Bottom - AxisReserve;
        // Bound the search to nearby drafting space. Never move a title to an
        // arbitrary distant part of the drawing simply to declare success.
        var sideLimit = Math.Max(1400, width / 2 + 200);
        const double downLimit = 6000;
        var x = new List<double> { center, center - 700, center + 700, center - 1400, center + 1400,
            center - sideLimit, center + sideLimit };
        var y = Enumerable.Range(0, 13).Select(i => top - 500 * i).ToList();
        var search = new Box(center - sideLimit - width / 2, top - downLimit - height, center + sideLimit + width / 2, top).Expand(200);
        foreach (var guide in guides.Where(g => search.Overlaps(g.Expand(1))))
        {
            // Edge-derived candidates fit gaps a fixed grid can miss.
            x.Add(guide.Left - width / 2 - TitleObstacles.Clearance - 1);
            x.Add(guide.Right + width / 2 + TitleObstacles.Clearance + 1);
            y.Add(guide.Bottom - TitleObstacles.Clearance - 1);
            y.Add(guide.Top + height + TitleObstacles.Clearance + 1);
        }
        var centers = x.Where(v => Math.Abs(v - center) <= sideLimit + .1).GroupBy(v => Math.Round(v - center, 1)).Select(g => g.First());
        var tops = y.Where(v => v <= top + .1 && v >= top - downLimit - .1).GroupBy(v => Math.Round(v - top, 1)).Select(g => g.First()).ToList();
        return centers.SelectMany(cx => tops.Select(cy => new Box(cx - width / 2, cy - height, cx + width / 2, cy)))
            .OrderBy(b => Math.Pow((b.Left + b.Right) / 2 - center, 2) + Math.Pow((b.Top - top) * 1.5, 2));
    }

    internal static Box Bounds(IEnumerable<Entity> entities)
    {
        var extents = entities.Select(e => e.GeometricExtents).ToList();
        if (extents.Count == 0) { throw new InvalidOperationException("缺少图形范围。"); }
        return new Box(extents.Min(e => e.MinPoint.X), extents.Min(e => e.MinPoint.Y),
            extents.Max(e => e.MaxPoint.X), extents.Max(e => e.MaxPoint.Y));
    }
}
