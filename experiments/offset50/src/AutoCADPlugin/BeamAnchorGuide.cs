using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Geometry;

namespace AutoCADPlugin;

// References are calculation-only; no helper entities are written to the DWG.
internal sealed class BeamAnchorGuide
{
    internal sealed class VerticalLine
    {
        public VerticalLine(double x, double bottom = double.NegativeInfinity, double top = double.PositiveInfinity)
        { X = x; Bottom = bottom; Top = top; }
        public double X { get; }
        public double Bottom { get; }
        public double Top { get; }
    }

    public BeamAnchorGuide(int supportIndex, IEnumerable<VerticalLine> lines)
    { SupportIndex = supportIndex; Lines = lines.ToList().AsReadOnly(); }

    public int SupportIndex { get; }
    public IReadOnlyList<VerticalLine> Lines { get; }

    public static List<BeamAnchorGuide> Create(IReadOnlyList<List<Point2d>> originalSupports,
        BeamTopRebar.Result beamTops, double inset)
    {
        var result = new List<BeamAnchorGuide>();
        for (var supportIndex = 0; supportIndex < originalSupports.Count; supportIndex++)
        {
            var support = originalSupports[supportIndex];
            if (SupportClassification.IsFloorSlab(support.Max(p => p.X) - support.Min(p => p.X),
                support.Max(p => p.Y) - support.Min(p => p.Y))) { continue; }
            var lines = new List<VerticalLine>();
            var hasU = false;
            for (var i = 0; i < beamTops.Regions.Count; i++)
            {
                if (beamTops.Regions[i].SupportIndex != supportIndex) { continue; }
                hasU = true;
                var bar = beamTops.Bars[i];
                var left = bar.Points[0].X + RebarAnchorage.GuideClearance;
                var right = bar.Points[3].X - RebarAnchorage.GuideClearance;
                // An inward guide must not cross to the other half of a narrow U.
                if (left > right + 1e-6) { continue; }
                lines.Add(new VerticalLine(left));
                lines.Add(new VerticalLine(right));
            }
            if (!hasU)
            {
                var orientation = Math.Sign(ContourGraph.Area(support));
                foreach (var edge in ContourGraph.Edges(support))
                {
                    if (Math.Abs(edge.End.X - edge.Start.X) > ContourGraph.Tolerance ||
                        Math.Abs(edge.End.Y - edge.Start.Y) <= ContourGraph.Tolerance) { continue; }
                    // CCW interior is left of the directed edge. Without a U,
                    // the user requires only 50 from the actual beam side.
                    var x = (edge.Start.X + edge.End.X) / 2 - Math.Sign(edge.End.Y - edge.Start.Y) * orientation * inset;
                    lines.Add(new VerticalLine(x, Math.Min(edge.Start.Y, edge.End.Y), Math.Max(edge.Start.Y, edge.End.Y)));
                }
            }
            result.Add(new BeamAnchorGuide(supportIndex, lines));
        }
        return result;
    }
}
