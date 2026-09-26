using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AutoCADPlugin;

public sealed class SupportDisplay40Checks
{
    internal static void Run(Action<string, Action> check)
    {
        check("support40: beam has two dashed diagonals; slab no visible or hidden diagonals", () => Fixture(960, 2440, 1, 2));
        check("support40: slab keeps closed Support boundary and its label", () => Fixture(3200, 480, 1000, 0));
        check("support40: exact width=2*height retains existing fallback-beam classification", () => Fixture(960, 480, 1000, 2));
        check("support40: dash appearance independent of initial global linetype scale", () => Fixture(960, 2440, 25, 2));
    }

    private static void Fixture(double width, double height, double globalScale, int expectedCrosses)
    {
        using var db = new Database(true, true); db.Ltscale = globalScale;
        var previous = HostApplicationServices.WorkingDatabase;
        HostApplicationServices.WorkingDatabase = db;
        try { CheckFixture(db, width, height, globalScale, expectedCrosses); }
        finally { HostApplicationServices.WorkingDatabase = previous; }
    }

    private static void CheckFixture(Database db, double width, double height, double globalScale, int expectedCrosses)
    {
        using var tr = db.TransactionManager.StartTransaction(); Standards.Ensure(db, tr);
        var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
        using var support = new Polyline();
        support.AddVertexAt(0, new Point2d(0, 0), 0, 0, 0); support.AddVertexAt(1, new Point2d(width, 0), 0, 0, 0);
        support.AddVertexAt(2, new Point2d(width, height), 0, 0, 0); support.AddVertexAt(3, new Point2d(0, height), 0, 0, 0);
        support.Closed = true; DetailWriter.WriteControlBoundary(space, tr, support);
        DetailWriter.WriteSupportAnnotation(space, tr, support, "楼层梁");
        var entities = space.Cast<ObjectId>().Select(id => (Entity)tr.GetObject(id, OpenMode.ForRead)).ToList();
        var crosses = entities.OfType<Line>().Where(l => IsDiagonal(l, support)).ToList();
        Require(crosses.Count == expectedCrosses, "diagonal entity count includes invisible entities");
        Require(entities.OfType<Polyline>().Count() == 1 && support.Closed && OutlineRole.Get(support) == "Support", "boundary retained");
        Require(entities.OfType<DBText>().Single().TextString == (expectedCrosses == 0 ? "楼层板" : "楼层梁"), "cross follows classification label");
        foreach (var cross in crosses) { CheckDash(db, tr, cross); }
        Require(Math.Abs(db.Ltscale - globalScale) < 1e-8, "global LTSCALE unchanged");
        Require(entities.OfType<Line>().Where(l => !IsDiagonal(l, support)).All(l => l.Linetype != SupportCrossStyle.LinetypeName), "leaders are not dashed");
        tr.Commit();
    }

    [CommandMethod("SD_VERIFY40_SUPPORT")]
    public void VerifyCurrent()
    {
        var root = Environment.GetEnvironmentVariable("SD_OFFSET_TEST_ROOT");
        var log = new List<string>();
        try
        {
            var db = Application.DocumentManager.MdiActiveDocument.Database;
            using var tr = db.TransactionManager.StartTransaction();
            var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
            var local = space.Cast<ObjectId>().Select(id => (Entity)tr.GetObject(id, OpenMode.ForRead)).Where(IsGeneratedDetail).ToList();
            var supports = local.OfType<Polyline>().Where(p => OutlineRole.Get(p) == "Support").ToList();
            Require(supports.Count == 2, "both support boundaries retained in actual architectural result");
            var crossCount = 0;
            foreach (var support in supports)
            {
                GeometryTools.GetExtents(support, out var minX, out var minY, out var maxX, out var maxY);
                var slab = SupportClassification.IsFloorSlab(maxX - minX, maxY - minY);
                var crosses = local.OfType<Line>().Where(l => IsDiagonal(l, support)).ToList();
                Require(crosses.Count == (slab ? 0 : 2), "actual beam/slab diagonal counts");
                foreach (var cross in crosses) { CheckDash(db, tr, cross); }
                crossCount += crosses.Count;
            }
            Require(crossCount == 2, "only two native beam diagonal entities, not four");
            var plan = OffsetRebar.Create(local);
            Require(plan.OriginalSupports.Count == 2, "downstream rebar still recognizes slab support without its diagonals");
            var points = DistributedPointRebar.Create(plan, plan.Bars, CornerPointRebar.Create(plan, plan.Bars));
            var temporary = new List<Line>();
            try
            {
                // Reconstruct old solid beam/slab crosses, then independently compare
                // the complete downstream rebar and point results to the new display.
                var oldEntities = local.Where(e => e is not Line line || !supports.Any(s => IsDiagonal(line, s))).ToList();
                foreach (var support in supports)
                {
                    GeometryTools.GetExtents(support, out var x0, out var y0, out var x1, out var y1);
                    temporary.Add(new Line(new Point3d(x0, y0, 0), new Point3d(x1, y1, 0)) { LayerId = support.LayerId });
                    temporary.Add(new Line(new Point3d(x0, y1, 0), new Point3d(x1, y0, 0)) { LayerId = support.LayerId });
                }
                oldEntities.AddRange(temporary);
                var previous = OffsetRebar.Create(oldEntities);
                var oldPoints = DistributedPointRebar.Create(previous, previous.Bars, CornerPointRebar.Create(previous, previous.Bars));
                Require(Paths(plan.Bars).SequenceEqual(Paths(previous.Bars)), "all longitudinal rebar paths unchanged by display");
                Require(points.Centers.Count == oldPoints.Centers.Count && points.Centers.All(p => oldPoints.Centers.Any(q => p.GetDistanceTo(q) < .01)), "all point centers unchanged by display");
            }
            finally { foreach (var line in temporary) { line.Dispose(); } }
            using var output = db.Wblock(new ObjectIdCollection(local.Select(e => e.ObjectId).ToArray()), Point3d.Origin);
            output.SaveAs(Path.Combine(root, "outline40-only.dwg"), DwgVersion.Current);
            log.Add($"PASS native SD_AUTO_DETAIL: two Support boundaries; beam dashed lines=2; slab cross entities=0; unchanged rebar paths={plan.Bars.Count}; unchanged dot centers={points.Centers.Count}");
        }
        catch (System.Exception e) { log.Add("FAIL " + e); }
        File.WriteAllLines(Path.Combine(root, "support40-native-validation.txt"), log);
    }

    private static bool IsGeneratedDetail(Entity entity) => entity switch
    {
        Line line => Math.Abs(line.StartPoint.X) < 1000000,
        Polyline p => p.NumberOfVertices > 0 && Math.Abs(p.GetPoint2dAt(0).X) < 1000000,
        DBText text => Math.Abs(text.Position.X) < 1000000,
        _ => false
    };
    private static List<string> Paths(List<RebarPath> paths) => paths.Select(p => p.IsClosed + ":" +
        string.Join(";", p.Points.Select(q => q.X.ToString("F4", CultureInfo.InvariantCulture) + "," + q.Y.ToString("F4", CultureInfo.InvariantCulture))))
        .OrderBy(s => s, StringComparer.Ordinal).ToList();
    private static bool IsDiagonal(Line line, Polyline support) =>
        Enumerable.Range(0, support.NumberOfVertices).Any(i => support.GetPoint3dAt(i).DistanceTo(line.StartPoint) < .01) &&
        Enumerable.Range(0, support.NumberOfVertices).Any(i => support.GetPoint3dAt(i).DistanceTo(line.EndPoint) < .01) &&
        Math.Abs(line.StartPoint.X - line.EndPoint.X) > .01 && Math.Abs(line.StartPoint.Y - line.EndPoint.Y) > .01;
    private static void CheckDash(Database db, Transaction tr, Line line)
    {
        var type = (LinetypeTableRecord)tr.GetObject(line.LinetypeId, OpenMode.ForRead);
        Require(type.NumDashes == 2 && type.DashLengthAt(0) > 0 && type.DashLengthAt(1) < 0, "real native dashed linetype");
        Require(Math.Abs(type.DashLengthAt(0) * line.LinetypeScale * db.Ltscale - 250) < .01 &&
            Math.Abs(type.DashLengthAt(1) * line.LinetypeScale * db.Ltscale + 100) < .01, "reference 250-on / 100-off appearance");
        Require(line.Visible && line.Layer == Standards.OtherThinLayer && line.ColorIndex == 7 && OutlineRole.Get(line) == "", "beam cross retains style and stays out of contour roles");
    }
    private static void Require(bool value, string message) { if (!value) { throw new InvalidOperationException(message); } }
}
