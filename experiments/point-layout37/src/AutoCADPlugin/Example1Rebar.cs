using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Colors;

namespace AutoCADPlugin;

// A calibrated drafting recipe for example 1, not a structural design calculator.
// All dimensions below are OUTPUT drawing units (already enlarged four times).
internal static class Example1Rebar
{
    internal sealed class Placement
    {
        public Point2d Anchor;
        public double RoofEnd;
        public double RoofSlope;
    }

    internal static List<OutlineSegment> ReadEdges(IEnumerable<Entity> entities)
    {
        var edges = new List<OutlineSegment>();
        foreach (var entity in entities)
        {
            if (entity.Layer != Standards.OtherThinLayer && entity.Layer != Standards.OtherConstructionLayer) continue;
            if (entity is Line line) Add(line.StartPoint, line.EndPoint);
            if (entity is Polyline pl)
                for (int i = 0; i < pl.NumberOfVertices - (pl.Closed ? 0 : 1); i++)
                    if (pl.GetBulgeAt(i) == 0) Add(pl.GetPoint3dAt(i), pl.GetPoint3dAt((i + 1) % pl.NumberOfVertices));
        }
        return edges;
        void Add(Point3d a, Point3d b)
        {
            if (Math.Abs(a.Z - b.Z) > .01) return;
            if (a.DistanceTo(b) > 1) edges.Add(new OutlineSegment(new Point2d(a.X, a.Y), new Point2d(b.X, b.Y), Standards.OtherThinLayer));
        }
    }

    // Left-hand stepped concrete contour from the reference, relative to the
    // upper roof/vertical corner. Support labels and X strokes are not anchors.
    private static readonly Point2d[] Signature = {
        new Point2d(-1080,-840), new Point2d(-2520,0), new Point2d(-3000,0),
        new Point2d(-3000,-880), new Point2d(-2880,-880), new Point2d(-2880,-1000),
        new Point2d(-1760,-1000), new Point2d(-1760,-1320), new Point2d(0,-1320)
    };

    internal static Placement Match(List<OutlineSegment> edges)
    {
        var matches = new List<Placement>();
        foreach (var edge in edges)
        {
            var a = edge.Start.X < edge.End.X ? edge.Start : edge.End;
            var b = edge.Start.X < edge.End.X ? edge.End : edge.Start;
            if (Math.Abs(b.X - a.X - 1440) > 4 || Math.Abs(b.Y - a.Y + 840) > 4) continue;
            var anchor = new Point2d(a.X + 2520, a.Y);
            if (matches.Any(m => m.Anchor.GetDistanceTo(anchor) < 4)) continue;
            if (!SignatureMatches(edges, anchor)) continue;
            // Upper/lower inclined roof must both exist, with the sample's slope
            // and 960-wide stem; varying roof clipping length is allowed.
            const double slope = 0.424474816;
            var upperEnd = RoofExtent(edges, anchor, slope, 0);
            var lowerEnd = RoofExtent(edges, anchor, slope, -521.45333);
            if (upperEnd < 3000 || lowerEnd < 3000) continue;
            if (!Covered(edges, anchor + new Vector2d(0,-500)) ||
                !Covered(edges, anchor + new Vector2d(960,-500))) continue;
            // Anchored tails need the reference's support depth; don't blindly
            // plant long bars in a shorter or missing supporting member.
            if (!Covered(edges, anchor + new Vector2d(0,-2800)) ||
                !Covered(edges, anchor + new Vector2d(960,-2800))) continue;
            matches.Add(new Placement { Anchor = anchor, RoofEnd = Math.Min(upperEnd, lowerEnd), RoofSlope = slope });
        }
        if (matches.Count != 1)
            throw new InvalidOperationException(matches.Count == 0
                ? "轮廓未匹配实例1：请框选完整的四倍结构轮廓（含檐口、斜屋面和支撑）。本版不支持不同檐口尺寸、旋转或镜像，未生成钢筋。"
                : "选中了多个实例1轮廓，请一次只选一个大样，未生成钢筋。");
        return matches[0];
    }

    private static bool SignatureMatches(List<OutlineSegment> edges, Point2d anchor)
    {
        for (int i = 0; i < Signature.Length - 1; i++)
            for (int j = 0; j <= 8; j++)
            {
                var local = Signature[i] + (Signature[i + 1] - Signature[i]) * (j / 8.0);
                if (!Covered(edges, anchor + new Vector2d(local.X, local.Y))) return false;
            }
        return true;
    }

    private static double RoofExtent(List<OutlineSegment> edges, Point2d anchor, double slope, double intercept)
    {
        double end = 0;
        foreach (var edge in edges)
        {
            var a = edge.Start - anchor;
            var b = edge.End - anchor;
            if (Math.Abs(a.Y - slope * a.X - intercept) > 4 || Math.Abs(b.Y - slope * b.X - intercept) > 4) continue;
            if (Math.Abs(b.X - a.X) < 100) continue;
            end = Math.Max(end, Math.Max(a.X, b.X));
        }
        return end;
    }

    private static bool Covered(List<OutlineSegment> edges, Point2d p)
    {
        foreach (var edge in edges)
        {
            var v = edge.End - edge.Start;
            var t = Math.Max(0, Math.Min(1, (p - edge.Start).DotProduct(v) / v.DotProduct(v)));
            if (p.GetDistanceTo(edge.Start + v * t) <= 4) return true;
        }
        return false;
    }

    internal static Point2d Locate(Point2d local, Placement placement, bool roof)
    {
        // Keep the anchorage turns fixed. Redistribute only the sloping roof
        // portion, preserving its normal distance to the concrete faces.
        if (roof && local.X > 960)
        {
            var x = 960 + (local.X - 960) * (placement.RoofEnd - 960) / (Example1RebarData.RoofEndX - 960);
            local = new Point2d(x, local.Y + (x - local.X) * placement.RoofSlope);
        }
        return placement.Anchor + new Vector2d(local.X, local.Y);
    }

    internal static int Write(Database db, Transaction tr, Placement placement)
    {
        var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
        // Duplicate guard checks the drawing, not only the user's selection.
        foreach (ObjectId id in ms)
        {
            var e = tr.GetObject(id, OpenMode.ForRead) as Entity;
            if (e == null || (e.Layer != Standards.ReinforcementLayer && e.Layer != Standards.PointReinforcementLayer)) continue;
            var ext = e.GeometricExtents;
            if (ext.MaxPoint.X >= placement.Anchor.X - 3000 && ext.MinPoint.X <= placement.Anchor.X + placement.RoofEnd &&
                ext.MaxPoint.Y >= placement.Anchor.Y - 3280 && ext.MinPoint.Y <= placement.Anchor.Y + placement.RoofEnd * placement.RoofSlope)
                throw new InvalidOperationException("该大样内已有钢筋。请先撤销或删除旧钢筋后重试，避免重复叠加。");
        }
        Standards.Ensure(db, tr);
        SetLayer(Standards.ReinforcementLayer, 6);
        SetLayer(Standards.PointReinforcementLayer, 30);
        for (int index = 0; index < Example1RebarData.Bars.Length; index++)
        {
            var bar = new Polyline();
            var vertices = Example1RebarData.Bars[index];
            for (int i = 0; i < vertices.Length; i++)
                bar.AddVertexAt(i, Locate(vertices[i], placement, index >= 5), 0, 0, 0);
            bar.ConstantWidth = 35;
            Add(bar, Standards.ReinforcementLayer);
        }
        for (int i = 0; i < Example1RebarData.Points.Length; i++)
        {
            var center = Locate(Example1RebarData.Points[i], placement, i >= 15);
            // Same native entity as target: two semicircles, centerline diameter
            // 50 and width 50 => solid disc with OUTER diameter 100.
            var dot = new Polyline();
            dot.AddVertexAt(0, center + new Vector2d(25,0), 1, 50, 50);
            dot.AddVertexAt(1, center + new Vector2d(-25,0), 1, 50, 50);
            dot.Closed = true;
            dot.ConstantWidth = 50;
            Add(dot, Standards.PointReinforcementLayer);
        }
        return Example1RebarData.Bars.Length + Example1RebarData.Points.Length;

        void Add(Polyline pl, string layer)
        {
            pl.Layer = layer;
            pl.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
            pl.Linetype = "ByLayer";
            pl.LineWeight = LineWeight.ByLayer;
            ms.AppendEntity(pl);
            tr.AddNewlyCreatedDBObject(pl, true);
        }
        void SetLayer(string name, short color)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            var layer = (LayerTableRecord)tr.GetObject(lt[name], OpenMode.ForWrite);
            layer.Color = Color.FromColorIndex(ColorMethod.ByAci, color);
            layer.LinetypeObjectId = db.ContinuousLinetype;
            layer.LineWeight = LineWeight.ByLineWeightDefault;
        }
    }
}
