using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Colors;
using System;
using System.Collections.Generic;

namespace AutoCADPlugin;

internal static class DetailWriter
{
    public static void WriteOutlineSegment(BlockTableRecord modelSpace, Transaction transaction, Point2d a, Point2d b, string layer)
    {
        var line = new Line(new Point3d(a.X, a.Y, 0), new Point3d(b.X, b.Y, 0))
        {
            Layer = layer,
            Color = Color.FromColorIndex(ColorMethod.ByAci, 7)
        };
        modelSpace.AppendEntity(line);
        transaction.AddNewlyCreatedDBObject(line, true);
        OutlineRole.Set(line, transaction, "Contour");
    }

    public static void WriteControlBoundary(BlockTableRecord modelSpace, Transaction transaction, Polyline boundary)
    {
        boundary.Layer = Standards.OtherThinLayer;
        boundary.Color = Color.FromColorIndex(ColorMethod.ByAci, 7);
        boundary.ConstantWidth = 0;
        modelSpace.AppendEntity(boundary);
        transaction.AddNewlyCreatedDBObject(boundary, true);
        OutlineRole.Set(boundary, transaction, "Support");
    }

    public static void WriteSupportAnnotation(BlockTableRecord modelSpace, Transaction transaction, Polyline boundary, string label)
    {
        WriteSupportAnnotations(modelSpace, transaction, new[] { boundary }, label);
    }

    public static IReadOnlyList<string> WriteSupportAnnotations(BlockTableRecord modelSpace, Transaction transaction,
        IReadOnlyList<Polyline> boundaries, string fallbackLabel, IReadOnlyList<OutlineSegment>? outlines = null)
    {
        var polygons = new List<List<Point2d>>();
        foreach (var boundary in boundaries)
        {
            WriteSupportCross(modelSpace, transaction, boundary);
            var polygon = new List<Point2d>();
            for (var i = 0; i < boundary.NumberOfVertices; i++) { polygon.Add(boundary.GetPoint2dAt(i)); }
            polygons.Add(polygon);
        }
        var obstacles = new List<ContourGraph.Edge>();
        if (outlines != null)
        {
            foreach (var segment in outlines) { obstacles.Add(new ContourGraph.Edge(segment.Start, segment.End)); }
        }
        var warnings = new List<string>();
        foreach (var placement in SupportLabelLayout.Create(polygons, obstacles, fallbackLabel))
        {
            foreach (var edge in SupportLabelLayout.Edges(placement.Leader))
            {
                using var leader = new Line(new Point3d(edge.Start.X, edge.Start.Y, 0), new Point3d(edge.End.X, edge.End.Y, 0))
                { Layer = Standards.OtherThinLayer, Color = Color.FromColorIndex(ColorMethod.ByAci, 7) };
                modelSpace.AppendEntity(leader);
                transaction.AddNewlyCreatedDBObject(leader, true);
            }
            using var text = new DBText
            {
                Layer = Standards.TextLayer,
                Color = Color.FromColorIndex(ColorMethod.ByAci, 7),
                TextString = placement.Label,
                Height = SupportLabelLayout.TextHeight,
                Position = new Point3d(placement.Text.X, placement.Text.Y, 0)
            };
            modelSpace.AppendEntity(text);
            transaction.AddNewlyCreatedDBObject(text, true);
            if (placement.Conflicts > 0)
            {
                warnings.Add($"{placement.Label}指引在({placement.Text.X:F1},{placement.Text.Y:F1})附近空间不足，请检查避让。");
            }
        }
        return warnings;
    }
    private static void WriteSupportCross(BlockTableRecord modelSpace, Transaction transaction, Polyline boundary)
    {
        GeometryTools.GetExtents(boundary, out var minX, out var minY, out var maxX, out var maxY);
        var white = Color.FromColorIndex(ColorMethod.ByAci, 7);
        var diagonalA = new Line(new Point3d(minX, minY, 0), new Point3d(maxX, maxY, 0)) { Layer = Standards.OtherThinLayer, Color = white };
        var diagonalB = new Line(new Point3d(minX, maxY, 0), new Point3d(maxX, minY, 0)) { Layer = Standards.OtherThinLayer, Color = white };
        modelSpace.AppendEntity(diagonalA);
        transaction.AddNewlyCreatedDBObject(diagonalA, true);
        modelSpace.AppendEntity(diagonalB);
        transaction.AddNewlyCreatedDBObject(diagonalB, true);
    }

}
