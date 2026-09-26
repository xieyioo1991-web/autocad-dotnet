using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using AutoCADPlugin;

[assembly: CommandClass(typeof(Detail41Variants))]
[assembly: CommandClass(typeof(OffsetChecks))]
[assembly: CommandClass(typeof(AnnotationChecks))]
public sealed class Detail41Variants
{
    [CommandMethod("SD_CHECK41_VARIANTS")]
    public void Run()
    {
        var root = Environment.GetEnvironmentVariable("SD_CHECK41_ROOT")!;
        var log = new List<string>();
        foreach (var name in new[] { "contact38-points", "layout37-u-and-crossings-points37", "tiny-side-beam" })
        {
            var previous = HostApplicationServices.WorkingDatabase;
            try
            {
                using var db = new Database(false, true);
                db.ReadDwgFile(Path.Combine(root,"variants", name + ".dwg"), FileOpenMode.OpenForReadAndAllShare, true, ""); db.CloseInput(true);
                HostApplicationServices.WorkingDatabase = db;
                using var tr = db.TransactionManager.StartTransaction();
                var entities = ((BlockTableRecord)tr.GetObject(db.CurrentSpaceId,OpenMode.ForRead)).Cast<ObjectId>().Select(id => (Entity)tr.GetObject(id, OpenMode.ForRead)).ToList();
                var result = DetailAnnotation.Write(db, tr, entities);
                if (result.Dimensions == 0 || result.VerticalAxes != 0 || result.HorizontalAxes != 0) { throw new InvalidOperationException("fallback dimension scenario"); }
                log.Add($"PASS {name}: {result.Dimensions} dimensions; {result.LocalDimensions} local; no fabricated axes or axis numbers");
                tr.Commit(); db.SaveAs(Path.Combine(root,"variants", name + "-41.dwg"),DwgVersion.Current);
            }
            catch (System.Exception e) { log.Add("FAIL " + name + ": " + e); }
            finally { HostApplicationServices.WorkingDatabase = previous; }
        }
        File.WriteAllLines(Path.Combine(root,"variant-checks.txt"),log);
    }
}
