using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AutoCADPlugin;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

internal static class PointLayout37Checks
{
    private const double Tolerance = 0.01;
    private const string FixtureName = "layout37-u-and-crossings";

    private sealed class Fixture
    {
        public Fixture(OffsetRebar.Plan plan, DistributedPointRebar.Result points)
        {
            Plan = plan;
            Points = points;
            WorldCenters = points.Centers.Select(p => World(plan, p)).ToList();
            WorldFixed = points.FixedCenters.Select(p => World(plan, p)).ToList();
        }

        public OffsetRebar.Plan Plan { get; }
        public DistributedPointRebar.Result Points { get; }
        public List<Point2d> WorldCenters { get; }
        public List<Point2d> WorldFixed { get; }
    }

    public static void Run(Action<string, Action> check, string root)
    {
        Fixture? fixture = null;
        check("layout37: synthetic screenshot-shape fixture and diagnostics", () =>
        {
            fixture = CreateFixture(root);
            Require(fixture.Plan.BeamTops.Bars.Count == 1, "one beam-top U");
            Require(fixture.Plan.CornerBreaks.Ends.Count >= 2, "reentrant extensions are present");
        });
        check("layout37: both U legs distribute from fixed top to original beam400", () =>
        {
            var data = fixture ?? throw new InvalidOperationException("fixture generation must succeed");
            foreach (var x in new[] { 2917.5, 3642.5 })
            {
                var top = new Point2d(x, 982.5);
                var tail = new Point2d(x, -80);
                Require(Has(data.WorldFixed, top), "U top remains fixed: " + Format(top));
                Require(Has(data.WorldCenters, tail), "U tail is400 above beam top: " + Format(tail));
                var leg = data.WorldCenters.Where(p => Math.Abs(p.X - x) < Tolerance &&
                    p.Y >= -80 - Tolerance && p.Y <= 982.5 + Tolerance).OrderBy(p => p.Y).ToList();
                Require(leg.Count >= 3, "U leg needs an interior point at x=" + x);
                Require(leg.Zip(leg.Skip(1), (a, b) => b.Y - a.Y).All(gap => gap <= 800 + Tolerance),
                    "U leg maximum spacing800");
            }
        });
        check("layout37: true lower-left corner fixed and internal crossings unlocked", () =>
        {
            var data = fixture ?? throw new InvalidOperationException("fixture generation must succeed");
            Require(Has(data.WorldFixed, new Point2d(597.5, -362.5)), "true lower-left convex corner");
            foreach (var candidate in new[] { new Point2d(842.5, -362.5), new Point2d(977.5, -362.5),
                new Point2d(597.5, 17.5), new Point2d(842.5, 17.5) })
            {
                Require(!Has(data.WorldFixed, candidate), "through crossing is not a fixed corner: " + Format(candidate));
            }
        });
        check("layout37: no point circle intersects original crossed support", () =>
        {
            var data = fixture ?? throw new InvalidOperationException("fixture generation must succeed");
            Require(data.Points.Centers.All(point => data.Plan.OriginalSupports.All(support =>
                !CornerPointRebar.IntersectsSupport(point, support))), "point disks remain outside actual support");
        });
        check("layout37: lower row redistributes from true green corner to virtual support400", () =>
        {
            var data = fixture ?? throw new InvalidOperationException("fixture generation must succeed");
            VerifyStraightRow(data, new Point2d(597.5, -362.5), new Point2d(2400, -362.5), 4);
            foreach (var oldCandidate in new[] { new Point2d(842.5, -362.5), new Point2d(977.5, -362.5),
                new Point2d(597.5, 17.5), new Point2d(842.5, 17.5) })
            {
                Require(!Has(data.WorldCenters, oldCandidate), "old intersection candidate not appended after division: " + Format(oldCandidate));
            }
        });
        check("layout37: synthetic bar order and path direction preserve all dots", () =>
        {
            var data = fixture ?? throw new InvalidOperationException("fixture generation must succeed");
            VerifyReversal(data);
        });
    }

    // Called directly with the newly generated architectural plan, so this
    // regression never reads DWGs or result files left by an earlier test run.
    public static void RunArchitectural(Action<string, Action> check, string root, OffsetRebar.Plan plan)
    {
        var points = DistributedPointRebar.Create(plan, plan.Bars, CornerPointRebar.Create(plan, plan.Bars));
        var data = new Fixture(plan, points);
        WriteDiagnostics(root, data, "precision-interface", "actual architectural sample; generated structural contour with supplied supports");
        check("layout37: actual architectural lower-left row survives transverse vertical bar", () =>
        {
            var head = new Point2d(-2762.5, -882.5);
            var retreat = Math.Sqrt(400 * 400 - 67.5 * 67.5);
            var tail = new Point2d(-1310 - retreat, -882.5);
            Require(Has(data.WorldFixed, head), "original lower-left fixed corner stays");
            VerifyStraightRow(data, head, tail, 3);
            Require(Math.Abs(tail.GetDistanceTo(new Point2d(-1310, -950)) - 400) < Tolerance,
                "tail center uses actual endpoint distance400");
        });
        check("layout37: actual architectural U legs both reach original beam400", () =>
        {
            foreach (var x in new[] { 117.5, 842.5 })
            {
                var top = new Point2d(x, -117.5);
                var tail = new Point2d(x, -440);
                Require(Has(data.WorldFixed, top), "architectural U top remains fixed");
                VerifyStraightRow(data, top, tail, 2);
            }
        });
        check("layout37: actual architectural bar reversal preserves all dots", () => VerifyReversal(data));
    }

    private static void VerifyStraightRow(Fixture fixture, Point2d head, Point2d tail, int count)
    {
        var row = fixture.Points.Rows.Select(points => points.Select(p => World(fixture.Plan, p)).ToList())
            .FirstOrDefault(points => points.Count > 0 &&
                ((points[0].GetDistanceTo(head) < Tolerance && points.Last().GetDistanceTo(tail) < Tolerance) ||
                 (points[0].GetDistanceTo(tail) < Tolerance && points.Last().GetDistanceTo(head) < Tolerance)))
            ?? throw new InvalidOperationException("complete row from " + Format(head) + " to " + Format(tail));
        Require(row.Count == count, "minimal equal-interval row count: " + row.Count);
        var step = head.GetDistanceTo(tail) / (count - 1);
        Require(step <= 800 + Tolerance, "row gap no greater than800");
        for (var i = 0; i < count; i++)
        {
            var expected = head + (tail - head) * ((double)i / (count - 1));
            Require(Has(row, expected), "independent equal-division coordinate: " + Format(expected));
        }
    }

    private static void VerifyReversal(Fixture fixture)
    {
        var reversed = fixture.Plan.Bars.AsEnumerable().Reverse().Select(bar => new RebarPath(
            bar.Points.Reverse(), bar.IsClosed, bar.EndContacts, bar.StartContacts,
            bar.RegionIndex, bar.EndRule, bar.StartRule)).ToList();
        var corners = CornerPointRebar.Create(fixture.Plan, reversed);
        var result = DistributedPointRebar.Create(fixture.Plan, reversed, corners);
        var changed = fixture.Points.Centers.Where(p => !Has(result.Centers, p))
            .Concat(result.Centers.Where(p => !Has(fixture.Points.Centers, p))).ToList();
        Require(result.Centers.Count == fixture.Points.Centers.Count && changed.Count == 0,
            "reversed center set differs: " + string.Join(";", changed.Select(p => Format(World(fixture.Plan, p)))));
        Require(result.FixedCenters.Count == fixture.Points.FixedCenters.Count &&
            fixture.Points.FixedCenters.All(p => Has(result.FixedCenters, p)), "same fixed center set after reversal");
    }

    private static Fixture CreateFixture(string root)
    {
        // This is a synthetic analogue of screenshots2-4, not the user's DWG.
        var pieces = new List<List<Point2d>>
        {
            Rect(0, 0, 960, 1100), Rect(480, -480, 2320, 480),
            Rect(2800, -480, 960, 1580), Rect(2400, -2500, 400, 400)
        };
        var material = ContourGraph.Build(pieces.SelectMany(ContourGraph.Edges).ToList(), new List<List<Point2d>>());
        var beam = Rect(2800, -3600, 960, 3120);
        using var database = new Database(true, true);
        var previous = HostApplicationServices.WorkingDatabase;
        HostApplicationServices.WorkingDatabase = database;
        try
        {
            Fixture fixture;
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                Standards.Ensure(database, transaction);
                var space = (BlockTableRecord)transaction.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForWrite);
                foreach (var edge in material.SelectMany(ContourGraph.Edges))
                {
                    DetailWriter.WriteOutlineSegment(space, transaction, edge.Start, edge.End, Standards.OtherThinLayer);
                }
                using var support = MakePolyline(beam);
                DetailWriter.WriteControlBoundary(space, transaction, support);
                DetailWriter.WriteSupportAnnotation(space, transaction, support, "楼层梁");
                var selected = space.Cast<ObjectId>().Select(id => (Entity)transaction.GetObject(id, OpenMode.ForRead)).ToList();
                var plan = OffsetRebar.Create(selected);
                var points = DistributedPointRebar.Create(plan, plan.Bars, CornerPointRebar.Create(plan, plan.Bars));
                fixture = new Fixture(plan, points);
                OffsetRebar.Write(database, transaction, plan);
                WriteEntities(root, space, transaction);
                WriteDiagnostics(root, fixture);
                transaction.Commit();
            }
            database.SaveAs(Path.Combine(root, FixtureName + ".dwg"), DwgVersion.Current);
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                PointRebarWriter.Write(database, transaction, fixture.WorldCenters);
                transaction.Commit();
            }
            database.SaveAs(Path.Combine(root, FixtureName + "-points37.dwg"), DwgVersion.Current);
            return fixture;
        }
        finally
        {
            HostApplicationServices.WorkingDatabase = previous;
        }
    }

    private static void WriteEntities(string root, BlockTableRecord space, Transaction transaction)
    {
        var lines = new List<string>();
        foreach (ObjectId id in space)
        {
            var entity = (Entity)transaction.GetObject(id, OpenMode.ForRead);
            if (entity is Line line)
            {
                lines.Add("L|" + Join(new[] { line.StartPoint.X, line.StartPoint.Y, line.EndPoint.X, line.EndPoint.Y }));
            }
            else if (entity is Polyline polyline)
            {
                var indices = Enumerable.Range(0, polyline.NumberOfVertices).ToList();
                if (polyline.Closed) { indices.Add(0); }
                lines.Add((polyline.Layer == Standards.ReinforcementLayer ? "B|" : "P|") +
                    Join(indices.SelectMany(i => new[] { polyline.GetPoint2dAt(i).X, polyline.GetPoint2dAt(i).Y })));
            }
        }
        File.WriteAllLines(Path.Combine(root, FixtureName + "-entities.txt"), lines);
    }

    private static void WriteDiagnostics(string root, Fixture fixture, string name = FixtureName,
        string description = "synthetic analogue of screenshots2-4; not original user DWG")
    {
        var lines = new List<string>
        {
            description,
            "points=" + fixture.WorldCenters.Count, "fixed=" + fixture.WorldFixed.Count,
            "rows=" + fixture.Points.Rows.Count, "warnings=" + fixture.Points.Warnings.Count,
            "origin=" + Format(fixture.Plan.Origin)
        };
        lines.AddRange(fixture.WorldFixed.Select(point => "F|" + Format(point)));
        lines.AddRange(fixture.WorldCenters.Select(point => "D|" + Format(point)));
        lines.AddRange(fixture.Points.Rows.Select(row => "R|" + string.Join(";", row.Select(p => Format(World(fixture.Plan, p))))));
        lines.AddRange(fixture.Points.Warnings.Select(warning => "W|" + warning));
        File.WriteAllLines(Path.Combine(root, name + "-diagnostics37.txt"), lines);
    }

    private static Point2d World(OffsetRebar.Plan plan, Point2d local) => plan.Origin + new Vector2d(local.X, local.Y);
    private static bool Has(IEnumerable<Point2d> points, Point2d expected) => points.Any(p => p.GetDistanceTo(expected) < Tolerance);
    private static string Format(Point2d point) => Join(new[] { point.X, point.Y });
    private static string Join(IEnumerable<double> values) => string.Join(",", values.Select(value => value.ToString("0.###", CultureInfo.InvariantCulture)));
    private static void Require(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }

    private static List<Point2d> Rect(double x, double y, double width, double height) => new List<Point2d>
    {
        new Point2d(x, y), new Point2d(x + width, y), new Point2d(x + width, y + height), new Point2d(x, y + height)
    };

    private static Polyline MakePolyline(IReadOnlyList<Point2d> points)
    {
        var polyline = new Polyline();
        for (var i = 0; i < points.Count; i++) { polyline.AddVertexAt(i, points[i], 0, 0, 0); }
        polyline.Closed = true;
        return polyline;
    }
}
