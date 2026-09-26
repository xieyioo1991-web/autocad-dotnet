using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.PlottingServices;
using AutoCADPlugin;

public sealed class AnnotationChecks
{
    private static string Root => Environment.GetEnvironmentVariable("SD_ANNOTATION_TEST_ROOT") ?? throw new InvalidOperationException("missing root");
    private static string Baseline => Environment.GetEnvironmentVariable("SD_ANNOTATION_BASELINE") ?? throw new InvalidOperationException("missing baseline");

    [CommandMethod("SD_ANNOTATION_CHECK")]
    public void Run()
    {
        var log = new List<string>();
        foreach (var file in Directory.GetFiles(Baseline, "*-points38-final.dwg").OrderBy(f => f))
        {
            var name = Path.GetFileNameWithoutExtension(file).Replace("-points38-final", "");
            try { CheckDrawing(file, name, log); }
            catch (System.Exception e) { log.Add("FAIL|" + name + "|" + e); }
            File.WriteAllLines(Path.Combine(Root, "annotation39-results.txt"), log);
        }
        try { CheckRollback(log); } catch (System.Exception e) { log.Add("FAIL|rollback|" + e); }
        try { CheckCopyAndObstacle(log); } catch (System.Exception e) { log.Add("FAIL|copy-obstacle|" + e); }
        File.WriteAllLines(Path.Combine(Root, "annotation39-results.txt"), log);
        ReadReference();
    }

    private static void CheckDrawing(string file, string name, List<string> log)
    {
        using var db = new Database(false, true);
        db.ReadDwgFile(file, FileOpenMode.OpenForReadAndAllShare, false, null); db.CloseInput(true);
        var previous = HostApplicationServices.WorkingDatabase; HostApplicationServices.WorkingDatabase = db;
        try
        {
            List<string> before; List<string> first;
            int bars; int supports; int replaced;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var entities = Entities(db, tr);
                bars = entities.Count(e => e.Layer == Standards.ReinforcementLayer);
                if (bars == 0) { log.Add("PASS|" + name + "|no bars: no annotation requested"); return; }
                before = GeometrySnapshot(entities);
                var result = RebarAnnotation.Write(db, tr, entities);
                supports = result.Supports; replaced = result.ReplacedEntities;
                var after = Entities(db, tr);
                Require(before.SequenceEqual(GeometrySnapshot(after)), "rebar/dot/contour/support/cross unchanged");
                Verify(db, tr);
                first = AnnotationSnapshot(after);
                Export(name, after);
                tr.Commit();
            }
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var repeated = RebarAnnotation.Write(db, tr, Entities(db, tr).AsEnumerable().Reverse().ToList());
                Require(repeated.ReplacedEntities == (bars + supports) * 3, "replace each original text + 2 lines");
                Verify(db, tr);
                Require(before.SequenceEqual(GeometrySnapshot(Entities(db, tr))), "repeat preserves geometry");
                Require(first.SequenceEqual(AnnotationSnapshot(Entities(db, tr))), "repeat/reversed selection deterministic");
                tr.Commit();
            }
            db.SaveAs(Path.Combine(Root, name + "-labels39.dwg"), DwgVersion.Current);
            log.Add($"PASS|{name}|bars={bars}|supports={supports}|legacyEntitiesReplaced={replaced}|repeatStable=true|geometryUnchanged=true");
        }
        finally { HostApplicationServices.WorkingDatabase = previous; }
    }

    [CommandMethod("SD_VERIFY_ANNOTATION39")]
    public void VerifyCurrent()
    {
        var db = Application.DocumentManager.MdiActiveDocument.Database;
        try
        {
            using var tr = db.TransactionManager.StartTransaction(); Verify(db, tr);
            Export("production39", Entities(db, tr));
            File.WriteAllText(Path.Combine(Root, "production39-validation.txt"), "PASS native command: one leader/text per bar + support, finite source contacts, measured text/leader avoidance");
        }
        catch (System.Exception e) { File.WriteAllText(Path.Combine(Root, "production39-validation.txt"), "FAIL " + e); }
    }

    [CommandMethod("SD_PLOT_ANNOTATION39")]
    public void PlotCurrent()
    {
        var db = Application.DocumentManager.MdiActiveDocument.Database;
        try
        {
            using var tr = db.TransactionManager.StartTransaction();
            var layout = (Layout)tr.GetObject(LayoutManager.Current.GetLayoutId(LayoutManager.Current.CurrentLayout), OpenMode.ForRead);
            using var settings = new PlotSettings(layout.ModelType); settings.CopyFrom(layout);
            var validator = PlotSettingsValidator.Current;
            validator.SetPlotConfigurationName(settings, "DWG To PDF.pc3", "ISO_full_bleed_A3_(420.00_x_297.00_MM)");
            validator.RefreshLists(settings);
            var extents = Entities(db, tr).Select(e => e.GeometricExtents).ToList();
            var minX = extents.Min(e => e.MinPoint.X) - 200; var maxX = extents.Max(e => e.MaxPoint.X) + 200;
            var minY = extents.Min(e => e.MinPoint.Y) - 200; var maxY = extents.Max(e => e.MaxPoint.Y) + 200;
            validator.SetPlotWindowArea(settings, new Extents2d(minX, minY, maxX, maxY));
            validator.SetPlotType(settings, Autodesk.AutoCAD.DatabaseServices.PlotType.Window);
            validator.SetUseStandardScale(settings, true); validator.SetStdScaleType(settings, StdScaleType.ScaleToFit);
            validator.SetPlotCentered(settings, true); validator.SetPlotRotation(settings, PlotRotation.Degrees000);
            settings.PlotPlotStyles = false;
            using var info = new PlotInfo { Layout = layout.ObjectId, OverrideSettings = settings };
            using var infoValidator = new PlotInfoValidator { MediaMatchingPolicy = MatchingPolicy.MatchEnabled }; infoValidator.Validate(info);
            using var engine = PlotFactory.CreatePublishEngine();
            engine.BeginPlot(null, null);
            engine.BeginDocument(info, db.Filename, null, 1, true, Environment.GetEnvironmentVariable("SD_ANNOTATION_PDF"));
            using var page = new PlotPageInfo();
            engine.BeginPage(page, info, true, null); engine.BeginGenerateGraphics(null); engine.EndGenerateGraphics(null);
            engine.EndPage(null); engine.EndDocument(null); engine.EndPlot(null);
        }
        catch (System.Exception e) { File.WriteAllText(Path.Combine(Root, "plot39-error.txt"), e.ToString()); }
    }

    private static void Verify(Database db, Transaction tr)
    {
        var entities = Entities(db, tr);
        var targets = entities.Where(e => e.Layer == Standards.ReinforcementLayer || OutlineRole.Get(e) == "Support").ToList();
        var tagged = entities.Where(e => AnnotationIdentity.Get(e) != "").ToList();
        Require(tagged.Count == targets.Count * 3, "exactly three entities per target");
        var textEntities = tagged.OfType<DBText>().ToList();
        foreach (var target in targets)
        {
            var members = tagged.Where(e => AnnotationIdentity.Get(e) == target.Handle.ToString()).ToList();
            var text = members.OfType<DBText>().Single(); var leaders = members.OfType<Line>().ToList();
            var style = (TextStyleTableRecord)tr.GetObject(text.TextStyleId, OpenMode.ForRead);
            Require(leaders.Count == 2 && text.Layer == Standards.TextLayer && text.ColorIndex == 256 && text.Height == 250 && Math.Abs(text.WidthFactor - .7) < 1e-8, "native style and coverage");
            Require(Path.GetFileName(style.FileName) == "tssdeng.shx" && Path.GetFileName(style.BigFontFileName) == "hztxt.shx", "reference SHX fonts");
            if (target.Layer == Standards.ReinforcementLayer)
            {
                Require(text.TextString == "%%1328@150", "exact designer placeholder");
                Require(leaders.All(l => l.Layer == "S-OTHER-THIN" && l.ColorIndex == 256 && l.Linetype.ToUpperInvariant() == "BYLAYER" && l.LineWeight == LineWeight.ByLayer), "reference steel leader properties");
                var poly = (Polyline)target;
                Require(leaders.Any(l => poly.GetClosestPointTo(l.StartPoint, false).DistanceTo(l.StartPoint) < .1), "leader starts on finite assigned bar");
            }
            Require(members.All(e => OutlineRole.Get(e) == ""), "annotation does not impersonate contour/support");
        }
        foreach (var text in textEntities)
        {
            var a = text.GeometricExtents;
            foreach (var other in textEntities.Where(t => t.ObjectId != text.ObjectId))
            {
                var b = other.GeometricExtents;
                Require(!(a.MinPoint.X < b.MaxPoint.X && a.MaxPoint.X > b.MinPoint.X && a.MinPoint.Y < b.MaxPoint.Y && a.MaxPoint.Y > b.MinPoint.Y), "native text boxes do not overlap");
            }
            // Independent CAD intersection against measured text rectangle, including other leaders.
            using var rectangle = new Polyline();
            rectangle.AddVertexAt(0, new Point2d(a.MinPoint.X - 20, a.MinPoint.Y - 20), 0, 0, 0);
            rectangle.AddVertexAt(1, new Point2d(a.MaxPoint.X + 20, a.MinPoint.Y - 20), 0, 0, 0);
            rectangle.AddVertexAt(2, new Point2d(a.MaxPoint.X + 20, a.MaxPoint.Y + 20), 0, 0, 0);
            rectangle.AddVertexAt(3, new Point2d(a.MinPoint.X - 20, a.MaxPoint.Y + 20), 0, 0, 0); rectangle.Closed = true;
            foreach (var line in tagged.OfType<Line>())
            {
                using var hits = new Point3dCollection(); rectangle.IntersectWith(line, Intersect.OnBothOperands, hits, IntPtr.Zero, IntPtr.Zero);
                Require(hits.Count == 0, "native leaders avoid all text rectangles");
            }
        }
        Require(entities.OfType<DBText>().Count(t => t.TextString == "楼层梁" || t.TextString == "楼层板") == targets.Count(e => OutlineRole.Get(e) == "Support"), "no old beam/slab text left behind");
    }

    private static void CheckRollback(List<string> log)
    {
        using var db = new Database(false, true);
        db.ReadDwgFile(Path.Combine(Baseline, "contact38-points38-final.dwg"), FileOpenMode.OpenForReadAndAllShare, false, null); db.CloseInput(true);
        db.DisableUndoRecording(false);
        List<string> before;
        using (var tr = db.TransactionManager.StartTransaction()) { before = GeometrySnapshot(Entities(db, tr)); }
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var entities = Entities(db, tr); var bar = entities.OfType<Polyline>().First(p => p.Layer == Standards.ReinforcementLayer);
            bar.UpgradeOpen(); bar.SetBulgeAt(0, .5);
            var failed = false;
            try { RebarAnnotation.Write(db, tr, entities); } catch (InvalidOperationException) { failed = true; }
            Require(failed, "unsupported rebar reports failure"); // no Commit: test CAD rollback
            tr.Abort();
        }
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var after = GeometrySnapshot(Entities(db, tr));
            File.WriteAllLines(Path.Combine(Root, "rollback-difference.txt"), before.Except(after).Select(s => "BEFORE " + s).Concat(after.Except(before).Select(s => "AFTER " + s)));
            Require(before.SequenceEqual(after), "rollback geometry restored");
            Require(!Entities(db, tr).Any(e => AnnotationIdentity.Get(e) != ""), "no partial annotation");
        }
        log.Add("PASS|rollback|unsupported arc rejected, all transaction changes rolled back");
    }

    private static void CheckCopyAndObstacle(List<string> log)
    {
        using var db = new Database(false, true);
        db.ReadDwgFile(Path.Combine(Baseline, "contact38-points38-final.dwg"), FileOpenMode.OpenForReadAndAllShare, false, null); db.CloseInput(true);
        var previous = HostApplicationServices.WorkingDatabase; HostApplicationServices.WorkingDatabase = db;
        try
        {
            List<string> original;
            using (var tr = db.TransactionManager.StartTransaction())
            { RebarAnnotation.Write(db, tr, Entities(db, tr)); original = AnnotationSnapshot(Entities(db, tr)); tr.Commit(); }
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var input = new ObjectIdCollection(Entities(db, tr).Select(e => e.ObjectId).ToArray());
                using var map = new IdMapping(); db.DeepCloneObjects(input, db.CurrentSpaceId, map, false);
                var cloned = map.Cast<IdPair>().Where(p => p.IsCloned && p.IsPrimary).Select(p => (Entity)tr.GetObject(p.Value, OpenMode.ForWrite)).ToList();
                var cloneKeys = new HashSet<string>(cloned.Where(e => e.Layer == Standards.ReinforcementLayer || OutlineRole.Get(e) == "Support").Select(e => e.Handle.ToString()));
                Require(cloned.Where(e => AnnotationIdentity.Get(e) != "").All(e => cloneKeys.Contains(AnnotationIdentity.Get(e))), "native deep clone remaps XData 1005 source handles");
                var shift = Matrix3d.Displacement(new Vector3d(18259826, -1150051, 0));
                foreach (var entity in cloned) { entity.TransformBy(shift); }
                var copied = RebarAnnotation.Write(db, tr, cloned);
                Require(copied.ReplacedEntities == 12, "copy replaces only its own annotations");
                Require(original.All(s => AnnotationSnapshot(Entities(db, tr)).Contains(s)), "original detail labels preserved");
                Verify(db, tr); tr.Commit();
            }
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var entities = Entities(db, tr);
                var label = entities.OfType<DBText>().First(t => AnnotationIdentity.Get(t) != "");
                var position = label.Position;
                using var obstacle = new DBText(); obstacle.SetDatabaseDefaults(db); obstacle.TextString = "KEEP THIS NOTE";
                obstacle.Position = position; obstacle.Height = 300;
                var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                space.AppendEntity(obstacle); tr.AddNewlyCreatedDBObject(obstacle, true);
                RebarAnnotation.Write(db, tr, entities); // obstacle is deliberately outside the selection
                Require(obstacle.Position == position && obstacle.TextString == "KEEP THIS NOTE", "unrelated annotation remains intact");
                var extent = obstacle.GeometricExtents;
                foreach (var text in Entities(db, tr).OfType<DBText>().Where(t => AnnotationIdentity.Get(t) != ""))
                {
                    var b = text.GeometricExtents;
                    Require(!(extent.MinPoint.X < b.MaxPoint.X && extent.MaxPoint.X > b.MinPoint.X && extent.MinPoint.Y < b.MaxPoint.Y && extent.MaxPoint.Y > b.MinPoint.Y), "unselected neighboring note avoided");
                }
                Verify(db, tr); tr.Commit();
            }
            log.Add("PASS|copy-obstacle|large-coordinate copy owns its labels; original unchanged; unselected note avoided");
        }
        finally { HostApplicationServices.WorkingDatabase = previous; }
    }

    private static List<Entity> Entities(Database db, Transaction tr) => ((BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead))
        .Cast<ObjectId>().Where(id => !id.IsErased).Select(id => (Entity)tr.GetObject(id, OpenMode.ForRead)).ToList();
    private static string N(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
    private static string P(Point3d p) => N(p.X) + "," + N(p.Y);
    private static string P(Point2d p) => N(p.X) + "," + N(p.Y);
    private static List<string> AnnotationSnapshot(List<Entity> entities) => entities.Where(e => AnnotationIdentity.Get(e) != "")
        .Select(e => AnnotationIdentity.Get(e) + "|" + (e is DBText t ? "T" + P(t.Position) + t.TextString : e is Line l ? "L" + P(l.StartPoint) + P(l.EndPoint) : "?"))
        .OrderBy(s => s, StringComparer.Ordinal).ToList();
    private static List<string> GeometrySnapshot(List<Entity> entities) => entities.Where(e =>
        e is Polyline || OutlineRole.Get(e) == "Contour" || (e is Line l && IsCross(l, entities)))
        .Select(e => e.Handle + "|" + e.Layer + "|" + e.ColorIndex + "|" + OutlineRole.Get(e) + "|" +
            (e is Polyline p ? N(p.ConstantWidth) + p.Closed + "|" + string.Join(";", Enumerable.Range(0, p.NumberOfVertices).Select(i => P(p.GetPoint2dAt(i)) + "," + N(p.GetBulgeAt(i)))) :
            e is Line l ? P(l.StartPoint) + P(l.EndPoint) : "?" )).OrderBy(s => s, StringComparer.Ordinal).ToList();
    private static bool IsCross(Line l, List<Entity> entities) => entities.OfType<Polyline>().Where(p => OutlineRole.Get(p) == "Support")
        .Any(p => Enumerable.Range(0, p.NumberOfVertices).Any(i => p.GetPoint3dAt(i).DistanceTo(l.StartPoint) < .1) &&
            Enumerable.Range(0, p.NumberOfVertices).Any(i => p.GetPoint3dAt(i).DistanceTo(l.EndPoint) < .1));

    private static void Export(string name, List<Entity> entities)
    {
        var lines = new List<string>();
        foreach (var entity in entities)
        {
            if (entity is Line line) { lines.Add("L|" + P(line.StartPoint) + "," + P(line.EndPoint)); }
            else if (entity is Polyline p)
            {
                if (p.Layer == Standards.PointReinforcementLayer) { lines.Add("D|" + P(GeometryTools.MidPoint(p.GetPoint2dAt(0), p.GetPoint2dAt(1)))); continue; }
                var points = Enumerable.Range(0, p.NumberOfVertices).Select(p.GetPoint2dAt).ToList(); if (p.Closed) { points.Add(points[0]); }
                lines.Add((p.Layer == Standards.ReinforcementLayer ? "B|" : "P|") + string.Join(",", points.Select(P)));
            }
            else if (entity is DBText text) { lines.Add("T|" + P(text.Position) + "," + N(text.Height) + "|" + text.TextString); }
        }
        File.WriteAllLines(Path.Combine(Root, name + "-render.txt"), lines);
    }

    private static void ReadReference()
    {
        using var db = new Database(false, true);
        db.ReadDwgFile(Path.Combine(Path.GetDirectoryName(typeof(AnnotationChecks).Assembly.Location), "ReferenceStyle.dwg"), FileOpenMode.OpenForReadAndAllShare, false, null); db.CloseInput(true);
        using var tr = db.TransactionManager.StartTransaction();
        var lines = new List<string>();
        foreach (var t in Entities(db, tr).OfType<DBText>())
        {
            var style = (TextStyleTableRecord)tr.GetObject(t.TextStyleId, OpenMode.ForRead);
            lines.Add(t.TextString + "|" + t.Layer + "|height=" + t.Height + "|" + style.Name + "|" + style.FileName + "|" + style.BigFontFileName + "|width=" + t.WidthFactor + "|color=" + t.ColorIndex + "|weight=" + t.LineWeight + "|ltype=" + t.Linetype);
        }
        foreach (var layerName in new[] { "S-TEXT", "S-OTHER-THIN" })
        {
            var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            var layer = (LayerTableRecord)tr.GetObject(layers[layerName], OpenMode.ForRead);
            var type = (LinetypeTableRecord)tr.GetObject(layer.LinetypeObjectId, OpenMode.ForRead);
            lines.Add("LAYER|" + layerName + "|" + layer.Color.ColorIndex + "|" + layer.LineWeight + "|" + type.Name + "|plot=" + layer.IsPlottable);
        }
        File.WriteAllLines(Path.Combine(Root, "reference-annotation-style.txt"), lines);
    }
    private static void Require(bool value, string message) { if (!value) { throw new InvalidOperationException(message); } }
}
