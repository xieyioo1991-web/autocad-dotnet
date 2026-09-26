using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
using Autodesk.AutoCAD.Runtime;
[assembly: CommandClass(typeof(DetailPlot))]
public sealed class DetailPlot {
private static string Root => Environment.GetEnvironmentVariable("SD_CHECK41_ROOT");
private static List<Entity> Entities(Database db, Transaction tr) => ((BlockTableRecord)tr.GetObject(db.CurrentSpaceId,OpenMode.ForRead)).Cast<ObjectId>().Select(id=>(Entity)tr.GetObject(id,OpenMode.ForRead)).Where(e=>Math.Abs(e.GeometricExtents.MinPoint.X)<40000).ToList();
[CommandMethod("SD_PLOT41")]
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

}
