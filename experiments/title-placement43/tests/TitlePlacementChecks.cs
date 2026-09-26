using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AutoCADPlugin;

[assembly: CommandClass(typeof(TitlePlacementChecks))]
public sealed class TitlePlacementChecks
{
    [CommandMethod("SD_CHECK43_FRAME")]
    public void CheckFrame()
    {
        var root = Environment.GetEnvironmentVariable("SD_CHECK43_ROOT")!;
        var log = new List<string>();
        var db = Application.DocumentManager.MdiActiveDocument.Database;
        try
        {
            using var tr = db.TransactionManager.StartTransaction();
            var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId,OpenMode.ForWrite);
            var selected = space.Cast<ObjectId>().Select(id => (Entity)tr.GetObject(id,OpenMode.ForRead)).ToList();
            using var frame = new Polyline();
            foreach (var p in new[] { new Point2d(-15000,-15000),new Point2d(15000,-15000),new Point2d(15000,13000),new Point2d(-15000,13000) })
            { frame.AddVertexAt(frame.NumberOfVertices,p,0,0,0); }
            frame.Closed = true; frame.Layer = "0";
            space.AppendEntity(frame);tr.AddNewlyCreatedDBObject(frame,true);
            var before = frame.GeometricExtents.ToString();
            var result = DetailAnnotation.Write(db,tr,selected);
            if(result.Dimensions == 0 || frame.GeometricExtents.ToString() != before) { throw new InvalidOperationException("frame changed or missing dimensions"); }
            log.Add("PASS one complete annotation pipeline succeeds inside empty surrounding frame; frame unchanged");
            tr.Commit();
        }
        catch(System.Exception e) { log.Add("FAIL " + e.Message); }
        File.WriteAllLines(Path.Combine(root,"frame-checks.txt"),log);
    }
}
