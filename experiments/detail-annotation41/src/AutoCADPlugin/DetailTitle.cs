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
        BlockTableRecord space, Box geometry, IReadOnlyList<Box> obstacles, string source)
    {
        var title = reference.CloneTitle(db, tr, space.ObjectId);
        var template = Bounds(title);
        var cx = (geometry.Left + geometry.Right) / 2;
        var freeTop = geometry.Bottom - AxisReserve;
        var found = false;
        var offset = new Vector3d(0, 0, 0);
        for (var row = 0; row < 5 && !found; row++)
        foreach (var shift in new[] { 0.0, 700.0, -700.0, 1400.0, -1400.0 })
        {
            var dx = cx + shift - (template.Left + template.Right) / 2;
            var dy = freeTop - row * 500 - template.Top;
            var candidate = new Box(template.Left + dx, template.Bottom + dy, template.Right + dx, template.Top + dy);
            if (obstacles.Any(b => candidate.Expand(100).Overlaps(b))) { continue; }
            offset = new Vector3d(dx, dy, 0); found = true; break;
        }
        if (!found) { throw new InvalidOperationException("大样下方没有足够空间放置图名并预留轴号空间，本次标注已回滚。"); }
        foreach (var entity in title)
        {
            entity.TransformBy(Matrix3d.Displacement(offset));
            if (entity is DBText text) { text.AdjustAlignment(db); }
            DetailAnnotationIdentity.Set(entity, tr, source, "Title");
        }
        return title;
    }

    internal static Box Bounds(IEnumerable<Entity> entities)
    {
        var extents = entities.Select(e => e.GeometricExtents).ToList();
        if (extents.Count == 0) { throw new InvalidOperationException("缺少图形范围。"); }
        return new Box(extents.Min(e => e.MinPoint.X), extents.Min(e => e.MinPoint.Y),
            extents.Max(e => e.MaxPoint.X), extents.Max(e => e.MaxPoint.Y));
    }
}
