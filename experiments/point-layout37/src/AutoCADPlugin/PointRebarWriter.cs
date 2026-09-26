using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AutoCADPlugin;

internal static class PointRebarWriter
{
    public static int Write(Database database, Transaction transaction, IEnumerable<Point2d> centers)
    {
        Standards.Ensure(database, transaction);
        var layers = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
        var layer = (LayerTableRecord)transaction.GetObject(layers[Standards.PointReinforcementLayer], OpenMode.ForWrite);
        layer.Color = Color.FromColorIndex(ColorMethod.ByAci, 30);
        layer.LinetypeObjectId = database.ContinuousLinetype;
        layer.LineWeight = LineWeight.ByLineWeightDefault;
        var space = (BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForWrite);
        var existing = new List<Point2d>();
        foreach (ObjectId id in space)
        {
            if (transaction.GetObject(id, OpenMode.ForRead) is Polyline dot &&
                dot.Layer == Standards.PointReinforcementLayer && dot.Closed && dot.NumberOfVertices == 2 &&
                Math.Abs(dot.ConstantWidth - 50) < 1e-6 &&
                Math.Abs(dot.GetBulgeAt(0) - 1) < 1e-6 && Math.Abs(dot.GetBulgeAt(1) - 1) < 1e-6 &&
                Math.Abs(dot.GetPoint2dAt(0).GetDistanceTo(dot.GetPoint2dAt(1)) - 50) < 1e-6)
            {
                existing.Add(GeometryTools.MidPoint(dot.GetPoint2dAt(0), dot.GetPoint2dAt(1)));
            }
        }
        var count = 0;
        foreach (var center in centers)
        {
            if (existing.Any(p => p.GetDistanceTo(center) <= ContourGraph.Tolerance)) { continue; }
            if (existing.Any(p => p.GetDistanceTo(center) < 100 - 1e-6))
            { throw new InvalidOperationException("新排布与已有点筋重叠，请清除该大样旧点筋后重试；本次写入已回滚。"); }
            using var dot = new Polyline();
            dot.AddVertexAt(0, center + new Vector2d(25, 0), 1, 50, 50);
            dot.AddVertexAt(1, center + new Vector2d(-25, 0), 1, 50, 50);
            dot.Closed = true;
            dot.ConstantWidth = 50;
            dot.Layer = Standards.PointReinforcementLayer;
            dot.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
            dot.Linetype = "ByLayer";
            dot.LineWeight = LineWeight.ByLayer;
            space.AppendEntity(dot);
            transaction.AddNewlyCreatedDBObject(dot, true);
            existing.Add(center);
            count++;
        }
        return count;
    }
}
