using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Geometry;

namespace AutoCADPlugin;

internal static class BeamTopRebar
{
    public const double StraightLength = 1200;

    internal sealed class Result
    {
        public List<BeamTopRegion> Regions { get; } = new List<BeamTopRegion>();
        public List<RebarPath> Bars { get; } = new List<RebarPath>();
        public List<List<Point2d>> EffectiveSupports { get; } = new List<List<Point2d>>();
    }

    public static Result Create(IReadOnlyList<List<Point2d>> material, IReadOnlyList<List<Point2d>> supports, double inset)
    {
        var result = new Result();
        result.Regions.AddRange(BeamTopRegion.Find(material, supports));
        foreach (var region in result.Regions)
        {
            var left = region.Left + inset; var right = region.Right - inset;
            var top = region.Top - inset; // Level the short side first, then inset the horizontal top.
            if (right - left <= ContourGraph.Tolerance || top <= region.Bottom + ContourGraph.Tolerance)
            { throw new InvalidOperationException($"楼层梁{region.SupportIndex + 1}上方竖直区域过窄或过矮，无法按偏移{inset}生成倒U型纵筋，本次未生成。"); }
            CheckDepth(left); CheckDepth(right);
            var tipY = region.Bottom - StraightLength;
            result.Bars.Add(new RebarPath(new[] { new Point2d(left, tipY), new Point2d(left, top),
                new Point2d(right, top), new Point2d(right, tipY) }, false));

            void CheckDepth(double x)
            {
                var available = PolygonRay.AvailableFrom(new Point2d(x, region.Bottom), new Vector2d(0, -1), supports[region.SupportIndex]);
                if (available < StraightLength - 1e-6)
                { throw new InvalidOperationException($"楼层梁{region.SupportIndex + 1}倒U型纵筋在梁内仅有{available:F1}直锚空间，放不下1200；不改弯锚，本次未生成。"); }
            }
        }
        for (var i = 0; i < supports.Count; i++)
        {
            var regions = result.Regions.Where(region => region.SupportIndex == i).ToList();
            if (regions.Count == 0) { result.EffectiveSupports.Add(supports[i]); continue; }
            var unionEdges = ContourGraph.Edges(supports[i]).Concat(regions.SelectMany(region => ContourGraph.Edges(region.Polygon))).ToList();
            var union = ContourGraph.Build(unionEdges, new List<List<Point2d>>());
            if (union.Count != 1 || ContourGraph.Area(union[0]) <= 0)
            { throw new InvalidOperationException($"楼层梁{i + 1}与上方竖直区域无法合并为一个支撑，本次未生成。"); }
            result.EffectiveSupports.Add(union[0]);
        }
        return result;
    }
}
