using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AutoCADPlugin;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

public sealed class Outline38Checks
{
    [CommandMethod("SD_VERIFY38_OUTLINE")]
    public void Verify()
    {
        var root=Environment.GetEnvironmentVariable("SD_OFFSET_TEST_ROOT");
        var lines=new List<string>();
        try
        {
            var db=Application.DocumentManager.MdiActiveDocument.Database;
            using var tr=db.TransactionManager.StartTransaction();
            var space=(BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db),OpenMode.ForRead);
            var entities=space.Cast<ObjectId>().Select(id=>(Entity)tr.GetObject(id,OpenMode.ForRead)).ToList();
            var supports=entities.OfType<Polyline>().Where(p=>OutlineRole.Get(p)=="Support").ToList();
            Require(supports.Count==2,"two output support roles");
            var beam=supports.OrderBy(p=>p.GetPoint2dAt(0).X).First();
            GeometryTools.GetExtents(beam,out var x0,out var y0,out var x1,out var y1);
            Require(Math.Abs(x1-x0-960)<.01 && Math.Abs(y1-y0-2440)<.01 && supports.Any(p=>p.GetPoint2dAt(0).GetDistanceTo(Point2d.Origin)<.01),"fourfold support and requested insertion");
            Require(supports.All(p=>p.Layer==Standards.OtherThinLayer && p.ColorIndex==7 && p.ConstantWidth==0),"support standard unchanged");
            var contours=entities.OfType<Line>().Where(e=>OutlineRole.Get(e)=="Contour").ToList();
            Require(contours.Count>8 && contours.All(e=>e.Layer==Standards.OtherThinLayer && e.ColorIndex==7),"concrete contour roles and style retained");
            var labels=entities.OfType<DBText>().Where(t=>t.Layer==Standards.TextLayer && Math.Abs(t.Position.X)<1000000).ToList();
            Require(labels.Count==2 && labels.Any(t=>t.TextString=="楼层梁") && labels.Any(t=>t.TextString=="楼层板"),"native labels correctly classified");
            Require(labels.All(t=>t.Height==250 && t.Rotation==0),"native text dimensions");
            Require(!entities.OfType<Polyline>().Any(p=>p.Layer==Standards.ReinforcementLayer || p.Layer==Standards.PointReinforcementLayer),"outline command generates no bars/dots");
            lines.Add("PASS SD_AUTO_DETAIL: source DWG plus two supplied SD-REGION boundaries, scale4, role/layer/width/text and no reinforcement");
            PointLayout38Checks.Export(root,"outline38-command",space,tr,true);
        }
        catch(System.Exception error){lines.Add("FAIL SD_AUTO_DETAIL: "+error);}
        File.WriteAllLines(Path.Combine(root,"outline-command38-validation.txt"),lines);
    }
    private static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}
