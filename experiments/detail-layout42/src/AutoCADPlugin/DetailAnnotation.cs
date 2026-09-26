using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AutoCADPlugin;

internal static class DetailAnnotation
{
    internal sealed class Result
    {
        public int Dimensions { get; set; }
        public int LocalDimensions { get; set; }
        public int RebarLabels { get; set; }
        public int SupportLabels { get; set; }
        public int VerticalAxes { get; set; }
        public int HorizontalAxes { get; set; }
    }

    public static Result Write(Database db, Transaction tr, IReadOnlyList<Entity> selected)
    {
        var supports = selected.OfType<Polyline>().Where(e => OutlineRole.Get(e) == "Support").ToList();
        if (supports.Count == 0) { throw new InvalidOperationException("请框选完整结构轮廓、支撑、钢筋、轴线标高及已有文字。"); }
        var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
        if (selected.Any(e => e.OwnerId != space.ObjectId)) { throw new InvalidOperationException("只能处理当前空间中的原生完整大样。"); }
        var owners = new HashSet<string>(supports.Select(e => e.Handle.ToString()));
        var keys = new HashSet<string>(selected.Where(e => e.Layer == Standards.ReinforcementLayer).Select(e => e.Handle.ToString()).Concat(owners));
        var all = space.Cast<ObjectId>().Select(id => (Entity)tr.GetObject(id, OpenMode.ForRead)).ToList();
        var old = all.Where(e => owners.Contains(DetailAnnotationIdentity.Get(e)) &&
            DetailAnnotationIdentity.Role(e) != "Axis" && DetailAnnotationIdentity.Role(e) != "Elevation").ToList();
        var oldIds = new HashSet<ObjectId>(old.Select(e => e.ObjectId));
        var labels = all.Where(e => keys.Contains(AnnotationIdentity.Get(e))).ToList();
        var legacy = LegacySupportAnnotations.Find(selected, supports);
        foreach (var match in legacy) { labels.Add(match.Text); labels.AddRange(match.Leaders); }
        var movable = new HashSet<ObjectId>(labels.Select(e => e.ObjectId));
        var complete = selected.Where(e => !oldIds.Contains(e.ObjectId)).ToList();
        // Include guides by ownership even if their outermost ends/text fall just
        // outside the user's selection. Unrelated details never enter this group.
        foreach (var guide in all.Where(e => owners.Contains(DetailAnnotationIdentity.Get(e)) &&
            (DetailAnnotationIdentity.Role(e) == "Axis" || DetailAnnotationIdentity.Role(e) == "Elevation")))
        { if (!complete.Any(e => e.ObjectId == guide.ObjectId)) { complete.Add(guide); } }
        var plan = DetailDimensionPlan.Create(complete);
        var reference = DetailReference.Load(db, tr);
        var range = plan.Bounds.Expand(18000);
        var obstacles = all.Where(e => !oldIds.Contains(e.ObjectId) && !movable.Contains(e.ObjectId) &&
            range.Overlaps(AnnotationInk.Bounds(e).Expand(1))).ToList();
        foreach (var entity in old) { entity.UpgradeOpen(); entity.Erase(); }
        var source = supports.OrderBy(e => e.Handle.Value).First().Handle.ToString();
        var dimensions = DetailDimensions.Write(db, tr, space, reference, plan, obstacles, source);
        DetailTextLayout.Elevations(db, tr, complete, obstacles, dimensions);
        var titleObstacles = obstacles.Concat<Entity>(dimensions).Select(AnnotationInk.Bounds).ToList();
        var title = DetailTitle.Write(db, tr, reference, space, plan.Bounds, titleObstacles, source);
        complete.AddRange(dimensions); complete.AddRange(title);
        // Last stage: re-place BOTH support and bar labels around real dimension
        // strokes/glyphs and the title. A failure aborts the caller's transaction.
        var annotations = RebarAnnotation.Write(db, tr, complete, false);
        return new Result { Dimensions = dimensions.Count, LocalDimensions = plan.Items.Count(i => i.Local),
            RebarLabels = annotations.Bars, SupportLabels = annotations.Supports,
            HorizontalAxes = plan.HorizontalAxes, VerticalAxes = plan.VerticalAxes };
    }
}
