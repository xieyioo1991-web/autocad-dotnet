using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Box = AutoCADPlugin.SupportLabelLayout.Box;

namespace AutoCADPlugin;

// A closed outline is strokes, not a filled rectangle. Keep full text/solid
// extents, but test lines, dimensions and frame borders against actual strokes.
internal sealed class TitleObstacles
{
    internal const double Clearance = 100;
    private sealed class Obstacle
    {
        public AnnotationInk Ink { get; } = new AnnotationInk();
        public string Description { get; set; } = "";
        public double HalfWidth { get; set; }
        public bool Intersects(Box box)
        {
            var padded = box.Expand(Clearance + HalfWidth);
            return Ink.Text.Concat(Ink.Solids).Any(padded.Overlaps) ||
                Ink.Lines.Any(edge => SupportLabelLayout.Hits(edge, padded));
        }
    }

    private readonly List<Obstacle> obstacles = new List<Obstacle>();
    public List<Box> Guides { get; } = new List<Box>();

    public TitleObstacles(Transaction tr, IReadOnlyList<Entity> entities)
    {
        foreach (var entity in entities)
        {
            if (!entity.Visible) { continue; }
            var layer = (LayerTableRecord)tr.GetObject(entity.LayerId, OpenMode.ForRead);
            if (layer.IsOff || layer.IsFrozen) { continue; }
            var obstacle = new Obstacle { Description = $"{entity.Handle}({entity.Layer})" };
            if (entity is Polyline polyline)
            {
                for (var i = 0; i < polyline.NumberOfVertices; i++)
                { obstacle.HalfWidth = Math.Max(obstacle.HalfWidth, Math.Max(polyline.GetStartWidthAt(i), polyline.GetEndWidthAt(i)) / 2); }
            }
            obstacle.Ink.Add(entity);
            obstacles.Add(obstacle);
            Guides.AddRange(obstacle.Ink.Text);
            Guides.AddRange(obstacle.Ink.Solids);
            Guides.AddRange(obstacle.Ink.Lines.Select(e => new Box(Math.Min(e.Start.X, e.End.X), Math.Min(e.Start.Y, e.End.Y),
                Math.Max(e.Start.X, e.End.X), Math.Max(e.Start.Y, e.End.Y)).Expand(obstacle.HalfWidth)));
        }
    }

    public bool Intersects(Box box) => obstacles.Any(o => o.Intersects(box));
    public string Describe(Box box) => string.Join("、", obstacles.Where(o => o.Intersects(box)).Take(5).Select(o => o.Description));
}
