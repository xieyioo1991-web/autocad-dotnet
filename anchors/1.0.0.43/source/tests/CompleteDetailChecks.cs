using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AutoCADPlugin;

[assembly: CommandClass(typeof(CompleteDetailChecks))]
public sealed class CompleteDetailChecks
{
    [CommandMethod("SD_CHECK42_TRANSACTION")]
    public void Run()
    {
        var log = new List<string>();
        var root = Environment.GetEnvironmentVariable("SD_CHECK42_ROOT")!;
        var db = Application.DocumentManager.MdiActiveDocument.Database;
        try
        {
            string before;
            using (var tr = db.TransactionManager.StartTransaction()) { before = Detail42Checks.Signature(Detail42Checks.Entities(db, tr)); }
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var input = Input(db, tr);
                var full = CompleteDetail.Write(db, tr, input);
                Detail42Checks.Require(full.Bars == 4 && full.Points == 26, "complete pipeline inside one uncommitted transaction", log);
                // Dispose without commit after every stage has run.
            }
            using (var tr = db.TransactionManager.StartTransaction())
            {
                Detail42Checks.Require(before == Detail42Checks.Signature(Detail42Checks.Entities(db, tr)), "full pipeline abort leaves original drawing byte-geometry intact", log);
            }
            var failed = false;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                // Deliberately open the contour: architectural output happens,
                // then the real rebar stage must reject it without a partial commit.
                var input = Input(db, tr);
                input.Outline = input.Outline.Take(1).ToList();
                try { CompleteDetail.Write(db, tr, input); }
                catch (InvalidOperationException) { failed = true; }
            }
            using (var tr = db.TransactionManager.StartTransaction())
            {
                Detail42Checks.Require(failed && before == Detail42Checks.Signature(Detail42Checks.Entities(db, tr)), "downstream rebar failure rolls back architectural output and guides", log);
            }
            log.Add("PASS");
        }
        catch (System.Exception e) { log.Add("FAIL " + e); }
        File.WriteAllLines(Path.Combine(root, "transaction-checks.txt"), log);
    }

    private static ArchitecturalDetail.Input Input(Database db, Transaction tr)
    {
        var source = Detail42Checks.Entities(db, tr);
        var support = source.OfType<Polyline>().Where(p => p.Layer == Standards.RegionLayer && p.Closed).ToList();
        using var selection = SelectionSet.FromObjectIds(source.Select(e => e.ObjectId).ToArray());
        return new ArchitecturalDetail.Input { Source = source, Supports = support,
            Outline = ArchitecturalOutlineExtractor.Extract(tr, selection), Output = Point3d.Origin };
    }

    [CommandMethod("SD_CHECK42_ELEVATION_TEXT")]
    public void ElevationText()
    {
        var root = Environment.GetEnvironmentVariable("SD_CHECK42_ROOT")!;
        var log = new List<string>();
        try
        {
            var db = Application.DocumentManager.MdiActiveDocument.Database;
            using var tr = db.TransactionManager.StartTransaction();
            var all = Detail42Checks.Local(db, tr);
            var text = all.OfType<DBText>().Single(t => DetailAnnotationIdentity.Role(t) == "Elevation");
            var otherElevations = all.Where(e => DetailAnnotationIdentity.Role(e) == "Elevation" && e != text).ToList();
            var before = Detail42Checks.Signature(otherElevations);
            var box = AnnotationInk.Bounds(text);
            // Force a fixed horizontal dimension line through the text.
            using var dimension = (RotatedDimension)all.OfType<RotatedDimension>().First().Clone();
            dimension.Rotation = 0;
            dimension.XLine1Point = new Point3d(box.Left,box.Bottom-1000,0);
            dimension.XLine2Point = new Point3d(box.Right,box.Bottom-1000,0);
            dimension.DimLinePoint = new Point3d(box.Left,(box.Bottom+box.Top)/2,0);
            dimension.UsingDefaultTextPosition = true;
            var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId,OpenMode.ForWrite);
            space.AppendEntity(dimension); tr.AddNewlyCreatedDBObject(dimension,true); dimension.RecomputeDimensionBlock(true);
            var point = dimension.DimLinePoint;
            foreach (var entity in all)
            {
                try { var probe = new AnnotationInk(); probe.Add(entity); }
                catch (System.Exception e) { throw new InvalidOperationException("Obstacle " + entity.GetType().Name + " " + entity.Handle + " " + entity.Layer,e); }
            }
            DetailTextLayout.Elevations(db,tr,all,all,new[] { dimension });
            Detail42Checks.Require(text.TextString == "10.200" && !box.Overlaps(AnnotationInk.Bounds(text)), "elevation text moves clear without changing value",log);
            Detail42Checks.Require(before == Detail42Checks.Signature(otherElevations) && point == dimension.DimLinePoint, "elevation symbol and fixed dimension line remain at original coordinates",log);
            log.Add("PASS");
        }
        catch (System.Exception e) { log.Add("FAIL " + e); }
        File.WriteAllLines(Path.Combine(root,"elevation-text-checks.txt"),log);
    }
}
