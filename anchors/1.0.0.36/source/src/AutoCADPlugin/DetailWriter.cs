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

    public static void WriteSupportAnnotations(BlockTableRecord modelSpace, Transaction transaction, IReadOnlyList<Polyline> boundaries, string fallbackLabel)
    {
        var usedLabelBoxes = new List<LabelBox>();
        foreach (var boundary in boundaries)
        {
            WriteSupportCross(modelSpace, transaction, boundary);
            GeometryTools.GetExtents(boundary, out var minX, out var minY, out var maxX, out var maxY);
            // Classify each support independently using drawing X/Y extents.
            // Uniform four-times scaling does not change this aspect ratio.
            var width = maxX - minX;
            var height = maxY - minY;
            var label = SupportClassification.Label(width, height, fallbackLabel);
            var placement = FindLabelPlacement(minX, minY, maxX, maxY, label, boundaries, usedLabelBoxes);
            var white = Color.FromColorIndex(ColorMethod.ByAci, 7);
            var leader = new Line(placement.LeaderStart, placement.LeaderEnd)
            {
                Layer = Standards.OtherThinLayer,
                Color = white
            };
            modelSpace.AppendEntity(leader);
            transaction.AddNewlyCreatedDBObject(leader, true);

            var text = new DBText
            {
                Layer = Standards.TextLayer,
                Color = white,
                TextString = label,
                Height = LabelHeight,
                // DBText defaults to a valid left/baseline anchor. Setting
                // AlignmentPoint without an applicable justification throws
                // eNotApplicable in AutoCAD 2020.
                Position = placement.TextPosition
            };
            modelSpace.AppendEntity(text);
            transaction.AddNewlyCreatedDBObject(text, true);
            usedLabelBoxes.Add(placement.Box);
        }
    }

    private const double LabelHeight = 250.0;
    private const double LabelGap = 300.0;

    private readonly struct LabelBox
    {
        public LabelBox(double minX, double minY, double maxX, double maxY)
        {
            MinX = minX;
            MinY = minY;
            MaxX = maxX;
            MaxY = maxY;
        }

        public double MinX { get; }
        public double MinY { get; }
        public double MaxX { get; }
        public double MaxY { get; }
    }

    private readonly struct LabelPlacement
    {
        public LabelPlacement(LabelBox box, Point3d textPosition, Point3d leaderStart, Point3d leaderEnd)
        {
            Box = box;
            TextPosition = textPosition;
            LeaderStart = leaderStart;
            LeaderEnd = leaderEnd;
        }

        public LabelBox Box { get; }
        public Point3d TextPosition { get; }
        public Point3d LeaderStart { get; }
        public Point3d LeaderEnd { get; }
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

    private static LabelPlacement FindLabelPlacement(double minX, double minY, double maxX, double maxY, string label, IReadOnlyList<Polyline> boundaries, List<LabelBox> used)
    {
        var width = Math.Max(LabelHeight * 2.5, label.Length * LabelHeight);
        var height = LabelHeight;
        var centerX = (minX + maxX) / 2.0;
        var centerY = (minY + maxY) / 2.0;
        var isHorizontal = (maxX - minX) >= (maxY - minY) * 1.5;
        var candidates = isHorizontal ? new[]
        {
            // Prefer the clear area above the support, which matches the
            // manually corrected structural detail.
            MakeCandidate(centerX - width / 2.0, maxY + LabelGap, new Point3d(centerX, maxY, 0), width, height),
            MakeCandidate(maxX + LabelGap, maxY + LabelGap, new Point3d(maxX, maxY, 0), width, height),
            MakeCandidate(maxX + LabelGap, centerY - height / 2.0, new Point3d(maxX, centerY, 0), width, height),
            MakeCandidate(minX - LabelGap - width, maxY + LabelGap, new Point3d(minX, maxY, 0), width, height),
            MakeCandidate(minX - LabelGap - width, centerY - height / 2.0, new Point3d(minX, centerY, 0), width, height),
            MakeCandidate(centerX - width / 2.0, minY - LabelGap - height, new Point3d(centerX, minY, 0), width, height)
        } : new[]
        {
            // A tall support is annotated from its right upper side first;
            // this keeps the text away from the upper architectural outline.
            MakeCandidate(maxX + LabelGap, maxY + LabelGap, new Point3d(maxX, maxY, 0), width, height),
            MakeCandidate(maxX + LabelGap, centerY - height / 2.0, new Point3d(maxX, centerY, 0), width, height),
            MakeCandidate(centerX - width / 2.0, maxY + LabelGap, new Point3d(centerX, maxY, 0), width, height),
            MakeCandidate(minX - LabelGap - width, maxY + LabelGap, new Point3d(minX, maxY, 0), width, height),
            MakeCandidate(minX - LabelGap - width, centerY - height / 2.0, new Point3d(minX, centerY, 0), width, height),
            MakeCandidate(centerX - width / 2.0, minY - LabelGap - height, new Point3d(centerX, minY, 0), width, height)
        };

        foreach (var candidate in candidates)
        {
            if (OverlapsBoundary(candidate.Box, boundaries, minX, minY, maxX, maxY)) continue;
            if (OverlapsAny(candidate.Box, used, LabelGap * 0.25)) continue;
            return candidate;
        }

        // Dense details can exhaust the preferred positions. Keep the label
        // readable by stacking it above the current support with a deterministic
        // offset; the leader still points back to the support edge.
        var fallback = MakeCandidate(maxX + LabelGap, maxY + LabelGap + used.Count * (height + LabelGap), new Point3d(maxX, maxY, 0), width, height);
        return fallback;
    }

    private static LabelPlacement MakeCandidate(double x, double y, Point3d leaderStart, double width, double height)
    {
        var box = new LabelBox(x, y, x + width, y + height);
        var textPosition = new Point3d(x, y, 0);
        var leaderEnd = new Point3d(x, y + height / 2.0, 0);
        return new LabelPlacement(box, textPosition, leaderStart, leaderEnd);
    }

    private static bool OverlapsBoundary(LabelBox box, IReadOnlyList<Polyline> boundaries, double ownMinX, double ownMinY, double ownMaxX, double ownMaxY)
    {
        foreach (var boundary in boundaries)
        {
            GeometryTools.GetExtents(boundary, out var minX, out var minY, out var maxX, out var maxY);
            if (Math.Abs(minX - ownMinX) < 1.0 && Math.Abs(minY - ownMinY) < 1.0 &&
                Math.Abs(maxX - ownMaxX) < 1.0 && Math.Abs(maxY - ownMaxY) < 1.0)
            {
                continue;
            }
            if (Intersects(box, new LabelBox(minX - LabelGap, minY - LabelGap, maxX + LabelGap, maxY + LabelGap))) return true;
        }
        return false;
    }

    private static bool OverlapsAny(LabelBox candidate, List<LabelBox> used, double margin)
    {
        foreach (var box in used)
        {
            if (Intersects(candidate, new LabelBox(box.MinX - margin, box.MinY - margin, box.MaxX + margin, box.MaxY + margin))) return true;
        }
        return false;
    }

    private static bool Intersects(LabelBox a, LabelBox b)
    {
        return a.MinX < b.MaxX && a.MaxX > b.MinX && a.MinY < b.MaxY && a.MaxY > b.MinY;
    }

}

