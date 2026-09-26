using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Box = AutoCADPlugin.SupportLabelLayout.Box;
using Edge = AutoCADPlugin.ContourGraph.Edge;

namespace AutoCADPlugin;

// Real native glyph/line extents, rather than a dimension's large rectangular
// bounding box which would incorrectly block the entire drawing underneath it.
internal sealed class AnnotationInk
{
    public List<Box> Text { get; } = new List<Box>();
    public List<Edge> Lines { get; } = new List<Edge>();
    public List<Box> Solids { get; } = new List<Box>();

    public void Add(Entity entity, int depth = 0)
    {
        if (!entity.Visible) { return; }
        // Exploding a block returns variable attribute definitions, not the
        // displayed instance values. Their empty extents are not visible ink.
        if (entity is AttributeDefinition definition && (!definition.Constant || definition.Invisible)) { return; }
        if (entity is AttributeReference attribute && attribute.Invisible) { return; }
        if (entity is Line line)
        { Lines.Add(new Edge(Point(line.StartPoint), Point(line.EndPoint))); return; }
        if (entity is DBText || entity is MText)
        { Text.Add(Bounds(entity)); return; }
        if (entity is Dimension || entity is BlockReference)
        {
            if (depth > 8) { throw new InvalidOperationException("标注块嵌套过深，无法完整检查避让。"); }
            using var exploded = new DBObjectCollection();
            try
            {
                entity.Explode(exploded);
                foreach (DBObject item in exploded) { if (item is Entity nested) { Add(nested, depth + 1); } }
                if (entity is BlockReference block && block.Database != null)
                {
                    var transaction = block.Database.TransactionManager.TopTransaction;
                    if (transaction == null) { throw new InvalidOperationException("块属性避让检查需要活动事务。"); }
                    foreach (ObjectId id in block.AttributeCollection)
                    { Add((AttributeReference)transaction.GetObject(id, OpenMode.ForRead), depth + 1); }
                }
            }
            finally { foreach (DBObject item in exploded) { item.Dispose(); } }
            return;
        }
        if (entity is Polyline poly && poly.NumberOfVertices >= 2)
        {
            for (var i = 0; i < poly.NumberOfVertices - (poly.Closed ? 0 : 1); i++)
            {
                if (Math.Abs(poly.GetBulgeAt(i)) > 1e-8) { Solids.Add(Bounds(poly)); return; }
                Lines.Add(new Edge(poly.GetPoint2dAt(i), poly.GetPoint2dAt((i + 1) % poly.NumberOfVertices)));
            }
            return;
        }
        Solids.Add(Bounds(entity));
    }

    public static Box Bounds(Entity entity)
    {
        var e = entity.GeometricExtents;
        return new Box(e.MinPoint.X, e.MinPoint.Y, e.MaxPoint.X, e.MaxPoint.Y);
    }
    private static Point2d Point(Point3d p) => new Point2d(p.X, p.Y);
}
