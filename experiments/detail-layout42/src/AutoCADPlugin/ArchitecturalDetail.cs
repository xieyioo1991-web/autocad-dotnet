using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AutoCADPlugin;

internal static class ArchitecturalDetail
{
    internal sealed class Input
    {
        public IReadOnlyList<Polyline> Supports { get; set; } = Array.Empty<Polyline>();
        public IReadOnlyList<Entity> Source { get; set; } = Array.Empty<Entity>();
        public IReadOnlyList<OutlineSegment> Outline { get; set; } = Array.Empty<OutlineSegment>();
        public Point3d Output { get; set; }
    }

    internal sealed class Result
    {
        public List<Entity> Entities { get; } = new List<Entity>();
        public List<string> Warnings { get; } = new List<string>();
        public int Axes { get; set; }
        public int Elevations { get; set; }
    }

    // Both entry points use the same architectural selection and transaction.
    public static Result Write(Database db, Transaction tr, Input input, bool includeLabels = true)
    {
        if (input.Supports.Count == 0) { throw new InvalidOperationException("没有有效的闭合SD-REGION支撑区域。"); }
        if (db.CurrentSpaceId != SymbolUtilityServices.GetBlockModelSpaceId(db))
        { throw new InvalidOperationException("请在模型空间框选建筑大样。"); }
        Standards.Ensure(db, tr);
        var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
        var before = new HashSet<ObjectId>(space.Cast<ObjectId>());
        var origin = input.Supports[0].GetPoint2dAt(0);
        var result = new Result();
        var outlines = input.Outline.Select(s => new OutlineSegment(
            GeometryTools.TransformPoint(s.Start, origin, input.Output, Standards.ScaleFactor),
            GeometryTools.TransformPoint(s.End, origin, input.Output, Standards.ScaleFactor), s.TargetLayer)).ToList();
        if (outlines.Count == 0) { throw new InvalidOperationException("未找到WALL或COLUMN有效轮廓，请完整框选建筑大样。"); }
        var supports = new List<Polyline>();
        foreach (var source in input.Supports)
        {
            var target = GeometryTools.TransformFromBase(source, origin, input.Output, Standards.ScaleFactor);
            try { DetailWriter.WriteControlBoundary(space, tr, target); }
            catch { target.Dispose(); throw; }
            supports.Add(target);
        }
        result.Warnings.AddRange(DetailWriter.WriteSupportAnnotations(space, tr, supports, "楼层梁", outlines, includeLabels));
        foreach (var edge in outlines) { DetailWriter.WriteOutlineSegment(space, tr, edge.Start, edge.End, edge.TargetLayer); }
        result.Warnings.AddRange(ArchitecturalGuides.Write(db, tr, space, input.Source, origin, input.Output, supports[0].Handle.ToString()));
        result.Entities.AddRange(space.Cast<ObjectId>().Where(id => !before.Contains(id)).Select(id => (Entity)tr.GetObject(id, OpenMode.ForRead)));
        result.Axes = result.Entities.Count(e => DetailAnnotationIdentity.Role(e) == "Axis");
        result.Elevations = result.Entities.OfType<DBText>().Count(e => DetailAnnotationIdentity.Role(e) == "Elevation");
        return result;
    }
}
