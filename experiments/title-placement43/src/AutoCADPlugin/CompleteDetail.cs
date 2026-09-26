using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AutoCADPlugin;

internal static class CompleteDetail
{
    internal sealed class Result
    {
        public ArchitecturalDetail.Result Architecture { get; set; } = null!;
        public DetailAnnotation.Result Annotation { get; set; } = null!;
        public int Bars { get; set; }
        public int Points { get; set; }
        public List<string> Warnings { get; } = new List<string>();
    }

    // No queued commands or nested commits: a failed stage rolls back the whole
    // new detail when the caller disposes its transaction without committing.
    public static Result Write(Database db, Transaction tr, ArchitecturalDetail.Input input)
    {
        var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
        var before = new HashSet<ObjectId>(space.Cast<ObjectId>());
        List<Entity> Created() => space.Cast<ObjectId>().Where(id => !before.Contains(id))
            .Select(id => (Entity)tr.GetObject(id, OpenMode.ForRead)).ToList();
        var result = new Result { Architecture = ArchitecturalDetail.Write(db, tr, input, false) };
        result.Warnings.AddRange(result.Architecture.Warnings);
        var plan = OffsetRebar.Create(result.Architecture.Entities);
        result.Bars = OffsetRebar.Write(db, tr, plan);
        result.Warnings.AddRange(plan.BeamSides.Notices);
        foreach (var end in plan.AnchorEnds.Where(e => e.Kind == RebarAnchorage.AnchorKind.Failed))
        {
            var point = plan.Origin + new Vector2d(end.Original.X, end.Original.Y);
            result.Warnings.Add($"未锚固断口({point.X:F1},{point.Y:F1})：{end.Reason}");
        }
        var points = PointRebarGeneration.Write(db, tr, Created());
        result.Points = points.Total;
        result.Warnings.AddRange(points.Layout.Warnings);
        // Dimensions and title precede all bar/support leaders, exactly once.
        result.Annotation = DetailAnnotation.Write(db, tr, Created());
        return result;
    }
}
