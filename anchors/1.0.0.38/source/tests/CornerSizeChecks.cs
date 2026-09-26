using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AutoCADPlugin;

internal static class CornerSizeChecks
{
    public static void Run(Action<string, Action> check)
    {
        check("corner size: 120/120 and 599.99/600 remain bent", () =>
        {
            foreach (var sides in new[] { (120.0, 120.0), (599.99, 600.0), (600.0, 600.0), (600.01, 120.0) })
            {
                var plan = Plan(L(sides.Item1, sides.Item2));
                Require(plan.CornerBreaks.CornerCount == 0 && plan.Bars.Single().IsClosed, "both sides <=600");
                Require(plan.Bars[0].Points.SequenceEqual(plan.UnbrokenBars[0].Points), "retained bend vertices");
            }
        });
        check("corner size: either side >600 qualifies at one decimal precision", () =>
        {
            foreach (var sides in new[] { (600.1, 120.0), (120.0, 600.1), (600.1, 600.1) })
            {
                var plan = Plan(L(sides.Item1, sides.Item2));
                Require(plan.CornerBreaks.CornerCount == 1 && plan.CornerBreaks.Ends.Count == 2, "either side qualifies");
                Require(plan.CornerBreaks.Ends.All(end => Math.Abs(end.Length - 200) < .001), "300 material thickness minus two 50 offsets gives 200 to opposite bar");
            }
        });
        check("corner size: exact 600 and threshold remain correct after rotate/mirror/reverse/translate", () =>
        {
            for (var variant = 0; variant < 8; variant++)
            {
                foreach (var length in new[] { 600.0, 600.1 })
                {
                    var angle = variant * .37;
                    var ring = L(length, 120).Select(p =>
                    {
                        var x = variant % 2 == 0 ? p.X : -p.X;
                        return new Point2d(18000000 + x * Math.Cos(angle) - p.Y * Math.Sin(angle),
                            -1100000 + x * Math.Sin(angle) + p.Y * Math.Cos(angle));
                    }).ToList();
                    if (variant >= 4) { ring.Reverse(); }
                    var plan = Plan(ring);
                    Require(plan.CornerBreaks.CornerCount == (length > 600 ? 1 : 0), "transformed threshold " + length);
                }
            }
        });
        check("corner size: 600 source side grows to 700 after inset, still do not cut", () =>
        {
            var ring = Points(0, 0, 1800, 0, 1800, 800, 1200, 800, 1200, 400, 600, 400, 600, 800, 0, 800);
            var plan = Plan(ring);
            Require(plan.OffsetLoops.SelectMany(ContourGraph.Edges).Any(edge => Math.Abs(edge.Start.GetDistanceTo(edge.End) - 700) < .001), "inset side actually 700");
            Require(plan.CornerBreaks.CornerCount == 0 && plan.Bars.Single().IsClosed, "measure source, not inset");
        });
        check("corner size: qualifying 610 source side shortens below 600 after inset, still cut", () =>
        {
            var ring = Points(0, 0, 800, 0, 1010, 400, 400, 400, 400, 1000, 0, 1000);
            var plan = Plan(ring);
            Require(plan.CornerBreaks.CornerCount == 1, "610 source side qualifies");
            var joint = plan.CornerBreaks.Ends[0].Origin;
            var incident = plan.OffsetLoops.SelectMany(ContourGraph.Edges).Where(edge =>
                edge.Start.GetDistanceTo(joint) < .001 || edge.End.GetDistanceTo(joint) < .001).ToList();
            Require(incident.Count == 2 && incident.All(edge => edge.Start.GetDistanceTo(edge.End) <= 600.000001), "both inset sides <=600");
        });
        check("corner size: collinear subdivisions count as one 700 side", () =>
        {
            var ring = L(700, 120);
            ring.Insert(3, new Point2d(650, 300));
            var plan = Plan(ring);
            Require(plan.CornerBreaks.CornerCount == 1, "350+350 is a 700 outline side");
        });
        check("corner size: real turn ends side, unrelated long edge does not qualify", () =>
        {
            var ring = Points(0, 0, 2000, 0, 2000, 300, 800, 300, 700, 400, 300, 400, 300, 800, 0, 800);
            var plan = Plan(ring);
            Require(plan.CornerBreaks.CornerCount == 0, "400/400 corner must not borrow distant 1200 edge");
        });
    }

    private static OffsetRebar.Plan Plan(List<Point2d> ring)
    {
        var entities = ContourGraph.Edges(ring).Select(edge => (Entity)new Line(
            new Point3d(edge.Start.X, edge.Start.Y, 0), new Point3d(edge.End.X, edge.End.Y, 0)) { Layer = Standards.OtherThinLayer }).ToList();
        try { return OffsetRebar.Create(entities); }
        finally { foreach (var entity in entities) { entity.Dispose(); } }
    }
    private static List<Point2d> L(double sideA, double sideB) =>
        Points(0, 0, 300 + sideA, 0, 300 + sideA, 300, 300, 300, 300, 300 + sideB, 0, 300 + sideB);
    private static List<Point2d> Points(params double[] coordinates)
    {
        var result = new List<Point2d>();
        for (var i = 0; i < coordinates.Length; i += 2) { result.Add(new Point2d(coordinates[i], coordinates[i + 1])); }
        return result;
    }
    private static void Require(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
}
