using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using System;

namespace AutoCADPlugin;

internal static class GeometryTools
{
    public static Polyline ToLocalPolyline(Polyline source)
    {
        var result = new Polyline();
        if (source.NumberOfVertices == 0) return result;
        var origin = source.GetPoint2dAt(0);
        for (var i = 0; i < source.NumberOfVertices; i++)
        {
            var point = source.GetPoint2dAt(i);
            result.AddVertexAt(i, new Point2d(point.X - origin.X, point.Y - origin.Y), source.GetBulgeAt(i), source.GetStartWidthAt(i), source.GetEndWidthAt(i));
        }
        result.Closed = source.Closed;
        return result;
    }

    public static Polyline Transform(Polyline local, Point3d insertionPoint, double scale)
    {
        var result = new Polyline();
        for (var i = 0; i < local.NumberOfVertices; i++)
        {
            var point = local.GetPoint2dAt(i);
            var transformed = new Point2d(insertionPoint.X + point.X * scale, insertionPoint.Y + point.Y * scale);
            result.AddVertexAt(i, transformed, local.GetBulgeAt(i), 0, 0);
        }
        result.Closed = local.Closed;
        return result;
    }

    public static Polyline TransformFromBase(Polyline source, Point2d sourceBase, Point3d insertionPoint, double scale)
    {
        var result = new Polyline();
        for (var i = 0; i < source.NumberOfVertices; i++)
        {
            var point = source.GetPoint2dAt(i);
            var transformed = new Point2d(
                insertionPoint.X + (point.X - sourceBase.X) * scale,
                insertionPoint.Y + (point.Y - sourceBase.Y) * scale);
            result.AddVertexAt(i, transformed, source.GetBulgeAt(i), 0, 0);
        }
        result.Closed = source.Closed;
        return result;
    }

    public static Point2d MidPoint(Point2d a, Point2d b) =>
        new Point2d((a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0);

    public static double Distance(Point2d a, Point2d b) => a.GetDistanceTo(b);

    public static Point2d TransformPoint(Point2d point, Polyline sourceRegion, Point3d insertionPoint, double scale)
    {
        var origin = sourceRegion.GetPoint2dAt(0);
        return new Point2d(
            insertionPoint.X + (point.X - origin.X) * scale,
            insertionPoint.Y + (point.Y - origin.Y) * scale);
    }

    public static Point2d TransformPoint(Point2d point, Point2d sourceBase, Point3d insertionPoint, double scale)
    {
        return new Point2d(
            insertionPoint.X + (point.X - sourceBase.X) * scale,
            insertionPoint.Y + (point.Y - sourceBase.Y) * scale);
    }

    public static bool IsPointInsideOrOn(Polyline polygon, Point2d point)
    {
        var inside = false;
        for (var i = 0; i < polygon.NumberOfVertices; i++)
        {
            var next = (i + 1) % polygon.NumberOfVertices;
            var a = polygon.GetPoint2dAt(i);
            var b = polygon.GetPoint2dAt(next);
            if (PointToSegmentDistance(point, a, b) < 1.0) return true;
            var intersects = ((a.Y > point.Y) != (b.Y > point.Y)) &&
                point.X < (b.X - a.X) * (point.Y - a.Y) / ((b.Y - a.Y) == 0 ? double.Epsilon : b.Y - a.Y) + a.X;
            if (intersects) inside = !inside;
        }
        return inside;
    }

    public static void GetExtents(Polyline polyline, out double minX, out double minY, out double maxX, out double maxY)
    {
        minX = double.MaxValue;
        minY = double.MaxValue;
        maxX = double.MinValue;
        maxY = double.MinValue;
        for (var i = 0; i < polyline.NumberOfVertices; i++)
        {
            var p = polyline.GetPoint2dAt(i);
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X);
            maxY = Math.Max(maxY, p.Y);
        }
    }

    private static double PointToSegmentDistance(Point2d point, Point2d a, Point2d b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        if (Math.Abs(dx) < double.Epsilon && Math.Abs(dy) < double.Epsilon) return point.GetDistanceTo(a);
        var t = ((point.X - a.X) * dx + (point.Y - a.Y) * dy) / (dx * dx + dy * dy);
        t = Math.Max(0, Math.Min(1, t));
        return point.GetDistanceTo(new Point2d(a.X + t * dx, a.Y + t * dy));
    }
}
