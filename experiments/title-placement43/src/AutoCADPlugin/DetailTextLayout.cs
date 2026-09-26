using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AutoCADPlugin;

internal static class DetailTextLayout
{
    // Relocate only generated elevation text; its value and triangle datum stay
    // unchanged. User-authored text remains an obstacle, never an editable target.
    public static void Elevations(Database db, Transaction tr, IReadOnlyList<Entity> selected,
        IReadOnlyList<Entity> obstacles, IReadOnlyList<RotatedDimension> dimensions)
    {
        var texts = selected.OfType<DBText>().Where(e => DetailAnnotationIdentity.Role(e) == "Elevation").ToList();
        var ids = new HashSet<ObjectId>(texts.Select(t => t.ObjectId));
        var ink = new AnnotationInk();
        foreach (var entity in obstacles.Where(e => !ids.Contains(e.ObjectId))) { ink.Add(entity); }
        foreach (var dimension in dimensions) { ink.Add(dimension); }
        foreach (var text in texts)
        {
            var original = text.Position;
            var accepted = false;
            foreach (var shift in Offsets())
            {
                if (!text.IsWriteEnabled) { text.UpgradeOpen(); }
                text.TransformBy(Matrix3d.Displacement(original + shift - text.Position));
                text.AdjustAlignment(db);
                var bounds = AnnotationInk.Bounds(text);
                if (ink.Text.Any(t => bounds.Expand(40).Overlaps(t.Expand(30))) ||
                    ink.Lines.Any(e => SupportLabelLayout.Hits(e, bounds.Expand(20))) ||
                    ink.Solids.Any(bounds.Expand(20).Overlaps)) { continue; }
                ink.Text.Add(bounds); accepted = true; break;
            }
            if (!accepted) { throw new InvalidOperationException($"标高文字{text.TextString}无法避让固定尺寸线，已回滚本次标注。"); }
        }
    }

    private static IEnumerable<Vector3d> Offsets()
    {
        foreach (var height in new[] { 0.0, 350.0, 700.0, 1050.0, 1400.0 })
        foreach (var horizontal in new[] { 0.0, -350.0, 350.0, -700.0, 700.0, -1400.0, 1400.0 })
        { yield return new Vector3d(horizontal, height, 0); }
    }
}
