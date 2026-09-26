using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using System;
using System.Collections.Generic;

namespace AutoCADPlugin;

internal readonly struct SteelSegment
{
    public SteelSegment(Point2d start, Point2d end)
    {
        Start = start;
        End = end;
    }

    public Point2d Start { get; }
    public Point2d End { get; }
}

internal static class StructuralPathExtractor
{
    private static readonly HashSet<string> StructuralLayers = new(StringComparer.OrdinalIgnoreCase)
    {
        "WALL", "COLUMN", "BEAM", "SLAB", "ROOF", "结构", "墙", "柱", "梁", "板"
    };

    private static readonly HashSet<string> IgnoredLayers = new(StringComparer.OrdinalIgnoreCase)
    {
        Standards.RegionLayer, "PUB_DIM", "DIM_ELEV", "DIM_LEAD", "PUB_HATCH",
        "J-ANNO-填充图案", "J-LM-立面线脚1-0.18"
    };

    public static List<SteelSegment> Extract(Transaction transaction, SelectionSet? selection, List<Polyline> regions)
    {
        var result = new List<SteelSegment>();
        if (selection == null || regions.Count == 0) return result;
        var structuralCandidates = new List<SteelSegment>();
        var fallbackCandidates = new List<SteelSegment>();

        foreach (SelectedObject selected in selection)
        {
            if (selected == null) continue;
            var entity = transaction.GetObject(selected.ObjectId, OpenMode.ForRead) as Entity;
            if (entity == null || IgnoredLayers.Contains(entity.Layer)) continue;

            if (entity is Line line)
            {
                AddCandidate(line.StartPoint, line.EndPoint, entity.Layer, regions, structuralCandidates, fallbackCandidates);
            }
            else if (entity is Polyline polyline && !string.Equals(polyline.Layer, Standards.RegionLayer, StringComparison.OrdinalIgnoreCase))
            {
                for (var i = 0; i < polyline.NumberOfVertices; i++)
                {
                    var next = i + 1;
                    if (next >= polyline.NumberOfVertices)
                    {
                        if (!polyline.Closed) break;
                        next = 0;
                    }
                    if (polyline.GetSegmentType(i) != SegmentType.Line) continue;
                    var start = polyline.GetPoint2dAt(i);
                    var end = polyline.GetPoint2dAt(next);
                    AddCandidate(
                        new Point3d(start.X, start.Y, 0),
                        new Point3d(end.X, end.Y, 0),
                        entity.Layer, regions, structuralCandidates, fallbackCandidates);
                }
            }
        }

        // Prefer known construction layers. Only fall back to other geometry when the
        // architectural drawing has no usable WALL/COLUMN/BEAM/SLAB outlines.
        result.AddRange(structuralCandidates.Count > 0 ? structuralCandidates : fallbackCandidates);
        return RemoveDuplicates(result);
    }

    private static void AddCandidate(
        Point3d start,
        Point3d end,
        string layer,
        List<Polyline> regions,
        List<SteelSegment> structural,
        List<SteelSegment> fallback)
    {
        var a = new Point2d(start.X, start.Y);
        var b = new Point2d(end.X, end.Y);
        if (a.GetDistanceTo(b) < 30.0) return;
        var midpoint = GeometryTools.MidPoint(a, b);
        // SD-REGION marks floor beams/slabs and other support geometry that must
        // remain visible but must not receive automatic reinforcement.
        foreach (var region in regions)
        {
            if (GeometryTools.IsPointInsideOrOn(region, midpoint)) return;
        }
        var segment = new SteelSegment(a, b);
        fallback.Add(segment);
        if (StructuralLayers.Contains(layer)) structural.Add(segment);
    }

    private static List<SteelSegment> RemoveDuplicates(List<SteelSegment> input)
    {
        var result = new List<SteelSegment>();
        foreach (var item in input)
        {
            var duplicate = false;
            foreach (var existing in result)
            {
                var sameDirection = item.Start.GetDistanceTo(existing.Start) < 1.0 && item.End.GetDistanceTo(existing.End) < 1.0;
                var reverseDirection = item.Start.GetDistanceTo(existing.End) < 1.0 && item.End.GetDistanceTo(existing.Start) < 1.0;
                if (sameDirection || reverseDirection)
                {
                    duplicate = true;
                    break;
                }
            }
            if (!duplicate) result.Add(item);
        }
        return result;
    }
}
