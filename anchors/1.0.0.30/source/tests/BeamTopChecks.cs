using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AutoCADPlugin;

internal static class BeamTopChecks
{
    public static void Run(Action<string, Action> check)
    {
        check("beam top: standalone upright becomes one inverted U, no remaining ordinary region", () =>
        {
            var plan = Plan(new[] { Rect(0, 0, 960, 800) }, new[] { Beam() });
            Require(plan.BeamTops.Bars.Count == 1 && plan.Bars.Count == 1 && plan.Boundaries.Count == 0, "one forced U only");
            VerifyU(plan.Bars[0], new Point2d(50, -1200), new Point2d(50, 750), new Point2d(910, 750), new Point2d(910, -1200));
            Require(plan.CornerBreaks.CornerCount == 0 && plan.AnchorEnds.Count == 0, "U not processed twice");
            Require(Math.Abs(plan.Supports[0].Max(p => p.Y) - 800) < .001, "virtual top");
            Require(plan.OriginalSupports[0].Max(p => p.Y) == 0, "original support unchanged");
        });
        check("beam top: unequal sides level at higher top before inset, short leg extended upwards", () =>
        {
            var ring = Points(0, 0, 960, 0, 960, 600, 0, 1000);
            var plan = Plan(new[] { ring }, new[] { Beam() });
            VerifyU(plan.Bars[0], new Point2d(50, -1200), new Point2d(50, 950), new Point2d(910, 950), new Point2d(910, -1200));
            Require(plan.BeamTops.Regions[0].LeftTop == 1000 && plan.BeamTops.Regions[0].RightTop == 600, "actual side heights");
            Require(plan.Supports[0].Max(p => p.Y) == 1000, "short side virtually raised to 1000");
        });
        check("beam top: opposite higher side, reflection and vertex order do not slope U", () =>
        {
            var ring = Points(0, 0, 960, 0, 960, 1000, 0, 600); ring.Reverse();
            var support = Beam(); support.Reverse();
            var plan = Plan(new[] { ring }, new[] { support });
            var world = World(plan, plan.BeamTops.Bars.Single());
            VerifyU(world, new Point2d(50, -1200), new Point2d(50, 950), new Point2d(910, 950), new Point2d(910, -1200));
        });
        check("beam top: narrower offset upright merges actual polygons, not bounding boxes", () =>
        {
            var plan = Plan(new[] { Rect(400, 0, 600, 900) }, new[] { Rect(0, -2400, 1600, 2400) });
            var world = World(plan, plan.BeamTops.Bars.Single());
            VerifyU(world, new Point2d(450, -1200), new Point2d(450, 850), new Point2d(950, 850), new Point2d(950, -1200));
            Require(!ContourGraph.Contains(plan.Supports[0], Local(plan, new Point2d(100, 500))), "empty shoulder not enlarged");
            Require(ContourGraph.Contains(plan.Supports[0], Local(plan, new Point2d(500, 500))), "actual upright enlarged");
        });
        check("beam top: horizontal floor slab never triggers U", () =>
        {
            var result = BeamTopRebar.Create(new[] { Rect(0, 0, 960, 800) }, new[] { Rect(0, -400, 3000, 400) }, 50);
            Require(result.Bars.Count == 0, "slab excluded");
            Require(SupportClassification.Label(3000, 400, "楼层梁") == "楼层板", "same classification as labels");
        });
        check("beam top: detached overhead region and point-only contact do not trigger U", () =>
        {
            foreach (var ring in new[] { Rect(0, .2, 960, 800), Rect(960, 0, 960, 800), Rect(0, -3000, 960, 500) })
            {
                var result = BeamTopRebar.Create(new[] { ring }, new[] { Beam() }, 50);
                Require(result.Bars.Count == 0, "not connected above beam");
            }
        });
        check("beam top: exact 1200 fits without 400 reserve, 1199.99 aborts instead of bending", () =>
        {
            var ring = Rect(0, 0, 960, 800);
            var exact = BeamTopRebar.Create(new[] { ring }, new[] { Rect(0, -1200, 960, 1200) }, 50);
            Require(exact.Bars.Count == 1 && exact.Bars[0].Points[0].Y == -1200, "force straight, no reserve test");
            Reject(() => BeamTopRebar.Create(new[] { ring }, new[] { Rect(0, -1199.99, 960, 1199.99) }, 50), "1200");
        });
        check("beam top: detached taller contour on same X cannot raise connected stem", () =>
        {
            var plan = Plan(new[] { Rect(0, 0, 960, 800), Rect(0, 2000, 300, 1000) }, new[] { Beam() });
            Require(plan.BeamTops.Regions.Count == 1 && Math.Abs(plan.BeamTops.Regions[0].Top - 800) < .001, "detached high edge ignored");
            VerifyU(World(plan, plan.BeamTops.Bars[0]), new Point2d(50, -1200), new Point2d(50, 750), new Point2d(910, 750), new Point2d(910, -1200));
        });
        check("beam top: inclined sides are not WCS vertical stems", () =>
        {
            var ring = Points(0, 0, 960, 0, 1160, 800, 200, 800);
            Require(BeamTopRebar.Create(new[] { ring }, new[] { Beam() }, 50).Bars.Count == 0, "no vertical side pair");
        });
        check("beam top: fixed U stops ordinary corner extension without being split", () =>
        {
            var material = Points(0, 0, 1400, 0, 1400, 600, 600, 600, 600, 1400, 0, 1400);
            var u = new RebarPath(Points(400, 0, 400, 900, 1000, 900, 1000, 0), false);
            var result = RebarCornerBreak.Apply(new[] { new RebarPath(material, true) }, new[] { material }, 0, new[] { u });
            Require(result.CornerCount == 1 && result.Bars.Count == 1, "fixed U is only obstacle");
            Require(result.Ends.Any(end => end.Hit && end.Tip.GetDistanceTo(new Point2d(400, 600)) < .001), "extension stops at U leg");
            Require(u.Points.Count == 4 && u.Points[1] == new Point2d(400, 900), "U untouched");
        });
        check("beam top: concave beam void blocks straight anchorage even when tip is inside", () =>
        {
            var beam = Points(0, 0, 960, 0, 960, -2000, 0, -2000, 0, -1000, 900, -1000, 900, -500, 0, -500);
            Reject(() => BeamTopRebar.Create(new[] { Rect(0, 0, 960, 800) }, new[] { beam }, 50), "1200");
        });
        check("beam top: width100 or height40 cannot contain inset U", () =>
        {
            Reject(() => BeamTopRebar.Create(new[] { Rect(0, 0, 100, 800) }, new[] { Rect(0, -2000, 100, 2000) }, 50), "过窄或过矮");
            Reject(() => BeamTopRebar.Create(new[] { Rect(0, 0, 960, 40) }, new[] { Beam() }, 50), "过窄或过矮");
        });
        check("beam top: side branch anchors inside merged upper area and bends across former beam top", () =>
        {
            var plan = Plan(new[] { Rect(0, 0, 960, 1000), Rect(-2000, 300, 2000, 300) }, new[] { Beam() });
            Require(plan.BeamTops.Bars.Count == 1, "one U despite side branch");
            Require(plan.AnchorEnds.Count == 2 && plan.AnchorEnds.All(end => end.Kind == RebarAnchorage.AnchorKind.Bent), "branch bends in virtual support");
            var entries = plan.AnchorEnds.Select(end => ToWorld(plan, end.Extension[0])).ToList();
            Require(entries.All(p => Math.Abs(p.X) < .001 && p.Y > 0), "branch enters above old beam");
            Require(plan.AnchorEnds.Any(end => ToWorld(plan, end.Extension.Last()).Y < 0), "continuous bend crosses merged seam");
            Require(plan.Bars.Count == 2, "forced U plus ordinary branch bar");
        });
        check("beam top: roof extends sideways and uneven stem still yields horizontal U", () =>
        {
            var roof = Points(0, 0, 960, 0, 960, 600, 2260, 1200, 2100, 1550, 0, 1000);
            var plan = Plan(new[] { roof }, new[] { Beam() });
            VerifyU(World(plan, plan.BeamTops.Bars.Single()), new Point2d(50, -1200), new Point2d(50, 950), new Point2d(910, 950), new Point2d(910, -1200));
            Require(plan.Boundaries.Count > 0 && plan.Bars.Count > 1, "roof continues through ordinary rules");
        });
        check("beam top: two beams and two narrow stems on one beam stay distinct", () =>
        {
            var plan = Plan(new[] { Rect(0, 0, 960, 800), Rect(3000, 0, 960, 1200) },
                new[] { Beam(), Rect(3000, -2400, 960, 2400) });
            Require(plan.BeamTops.Bars.Count == 2 && plan.Bars.Count == 2, "two separate beams");
            plan = Plan(new[] { Rect(0, 0, 600, 800), Rect(1400, 0, 600, 900) }, new[] { Rect(0, -3000, 2000, 3000) });
            Require(plan.BeamTops.Bars.Count == 2 && plan.Supports.Count == 1, "two prongs on one beam");
            Require(!ContourGraph.Contains(plan.Supports[0], Local(plan, new Point2d(1000, 400))), "gap between prongs preserved");
        });
        check("beam top: translated large coordinates, split vertical lines and micro drift", () =>
        {
            var ring = Points(0, 0, 960, 0, 960, 400, 960.001, 800, 0, 800, 0, 400);
            Point2d Move(Point2d p) => new Point2d(p.X + 18000000, p.Y - 1100000);
            var plan = Plan(new[] { ring.Select(Move).ToList() }, new[] { Beam().Select(Move).ToList() });
            Require(plan.BeamTops.Bars.Count == 1, "noise and large coordinates");
            var u = World(plan, plan.BeamTops.Bars[0]);
            Require(u.Points[1].GetDistanceTo(Move(new Point2d(50, 750))) < .01, "top location");
        });
        check("beam top: generated entities are only native magenta width35 polylines, repeat rejected", () =>
        {
            var plan = Plan(new[] { Rect(0, 0, 960, 800) }, new[] { Beam() });
            using var output = new Database(true, true);
            var previous = HostApplicationServices.WorkingDatabase; HostApplicationServices.WorkingDatabase = output;
            try
            {
                using var transaction = output.TransactionManager.StartTransaction();
                var count = OffsetRebar.Write(output, transaction, plan);
                var space = (BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(output), OpenMode.ForRead);
                var entities = space.Cast<ObjectId>().Select(id => transaction.GetObject(id, OpenMode.ForRead)).ToList();
                Require(count == 1 && entities.Count == 1 && entities[0] is Polyline, "no virtual boundaries or cross marks written");
                var polyline = (Polyline)entities[0];
                Require(!polyline.Closed && polyline.NumberOfVertices == 4 && polyline.ConstantWidth == 35 && polyline.Layer == "S-REIN", "U entity properties");
                var layer = (LayerTableRecord)transaction.GetObject(polyline.LayerId, OpenMode.ForRead);
                Require(layer.Color.ColorIndex == 6, "magenta layer");
                Reject(() => OffsetRebar.Write(output, transaction, plan), "未重复生成");
                transaction.Commit();
            }
            finally { HostApplicationServices.WorkingDatabase = previous; }
        });
    }

    public static void VerifyExample(OffsetRebar.Plan plan, double beamTop)
    {
        Require(plan.BeamTops.Bars.Count == 1, "example must have one forced U");
        VerifyU(World(plan, plan.BeamTops.Bars[0]), new Point2d(50, beamTop - 1200), new Point2d(50, -50),
            new Point2d(910, -50), new Point2d(910, beamTop - 1200));
        Require(Math.Abs(ToWorld(plan, plan.Supports[0].OrderByDescending(p => p.Y).First()).Y) < .01, "merged beam top is raised to zero");
        Require(plan.CornerBreaks.CornerCount == 1, "only large eave concave corner split");
    }

    private static OffsetRebar.Plan Plan(IEnumerable<List<Point2d>> material, IEnumerable<List<Point2d>> supports)
    {
        var entities = material.SelectMany(ContourGraph.Edges).Select(edge => (Entity)new Line(
            new Point3d(edge.Start.X, edge.Start.Y, 0), new Point3d(edge.End.X, edge.End.Y, 0)) { Layer = Standards.OtherThinLayer }).ToList();
        foreach (var ring in supports)
        {
            var support = new Polyline { Closed = true, Layer = Standards.RegionLayer };
            for (var i = 0; i < ring.Count; i++) { support.AddVertexAt(i, ring[i], 0, 0, 0); }
            entities.Add(support);
        }
        try { return OffsetRebar.Create(entities); }
        finally { foreach (var entity in entities) { entity.Dispose(); } }
    }
    private static void VerifyU(RebarPath path, params Point2d[] expected)
    {
        Require(!path.IsClosed && path.Points.Count == 4, "four-vertex open U");
        for (var i = 0; i < 4; i++) { Require(path.Points[i].GetDistanceTo(expected[i]) < .01, "U vertex " + i + ": " + path.Points[i]); }
    }
    private static RebarPath World(OffsetRebar.Plan plan, RebarPath path) => new RebarPath(path.Points.Select(p => ToWorld(plan, p)), path.IsClosed);
    private static Point2d ToWorld(OffsetRebar.Plan plan, Point2d p) => plan.Origin + new Vector2d(p.X, p.Y);
    private static Point2d Local(OffsetRebar.Plan plan, Point2d p) => new Point2d(p.X - plan.Origin.X, p.Y - plan.Origin.Y);
    private static List<Point2d> Beam() => Rect(0, -2000, 960, 2000);
    private static List<Point2d> Rect(double x, double y, double width, double height) => Points(x, y, x + width, y, x + width, y + height, x, y + height);
    private static List<Point2d> Points(params double[] coordinates)
    {
        var result = new List<Point2d>();
        for (var i = 0; i < coordinates.Length; i += 2) { result.Add(new Point2d(coordinates[i], coordinates[i + 1])); }
        return result;
    }
    private static void Reject(Action action, string message)
    {
        try { action(); } catch (InvalidOperationException error) { Require(error.Message.Contains(message), error.Message); return; }
        throw new InvalidOperationException("expected failure: " + message);
    }
    private static void Require(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
}
