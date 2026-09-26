using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AutoCADPlugin;

public sealed class NativePointChecks
{
    [CommandMethod("SD_POINT_VERIFY")]
    public void Verify()
    {
        var root=Environment.GetEnvironmentVariable("SD_OFFSET_TEST_ROOT");
        var results=new List<string>();
        foreach(var item in new[]{("precision-interface",23),("offset50-sample",23),("point-contact-tagged",7),
            ("paired-beam",6),("small-side-beam",1),("tiny-side-beam",0),("short-roof36",7),("tall-roof36",8),("curb36",2)})
        {
            try
            {
                using var db=new Database(false,true);
                db.ReadDwgFile(Path.Combine(root,"final-run",item.Item1+"-points36-final.dwg"),FileOpenMode.OpenForReadAndAllShare,false,"");
                db.CloseInput(true);
                using var tr=db.TransactionManager.StartTransaction();
                var space=(BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db),OpenMode.ForRead);
                var entities=space.Cast<ObjectId>().Select(id=>(Entity)tr.GetObject(id,OpenMode.ForRead)).ToList();
                var dots=entities.OfType<Polyline>().Where(p=>p.Layer==Standards.PointReinforcementLayer).ToList();
                Require(dots.Count==item.Item2,"expected native count");
                var context=OffsetRebar.Create(entities);
                var bars=entities.OfType<Polyline>().Where(p=>p.Layer==Standards.ReinforcementLayer).Select(p=>new RebarPath(
                    Enumerable.Range(0,p.NumberOfVertices).Select(i=>{var v=p.GetPoint2dAt(i)-context.Origin;return new Point2d(v.X,v.Y);}),p.Closed)).ToList();
                var barEdges=bars.SelectMany(CornerPointRebar.Segments).ToList();
                var exceptions=new List<Point2d>();
                foreach(var u in context.BeamTops.Bars)
                    for(var i=1;i+1<u.Points.Count;i++)
                        if(CornerPointRebar.TryTangent(u.Points[i],u.Points[i-1],u.Points[i+1],out var point))exceptions.Add(point);
                var centers=new List<Point2d>();
                foreach(var dot in dots)
                {
                    Require(dot.Closed && dot.NumberOfVertices==2 && dot.ConstantWidth==50 && dot.GetBulgeAt(0)==1 && dot.GetBulgeAt(1)==1,"solid arc polyline50+50");
                    Require(Math.Abs(dot.GetPoint2dAt(0).GetDistanceTo(dot.GetPoint2dAt(1))-50)<.001,"outer diameter100");
                    var layer=(LayerTableRecord)tr.GetObject(dot.LayerId,OpenMode.ForRead);
                    Require(layer.Color.ColorIndex==30 && layer.LinetypeObjectId==db.ContinuousLinetype && layer.LineWeight==LineWeight.ByLineWeightDefault,"reference layer attributes");
                    Require(dot.Color.IsByLayer && dot.LineWeight==LineWeight.ByLayer && dot.Linetype=="ByLayer","entity ByLayer");
                    var world=GeometryTools.MidPoint(dot.GetPoint2dAt(0),dot.GetPoint2dAt(1));
                    var p=new Point2d(world.X-context.Origin.X,world.Y-context.Origin.Y);centers.Add(p);
                    Require(context.OriginalSupports.All(s=>!CornerPointRebar.IntersectsSupport(p,s)),"entire dot outside crossed support");
                    Require(barEdges.Any(e=>Math.Abs(ContourGraph.Distance(p,e)-67.5)<.001),"actual finite bar tangency");
                    Require(exceptions.Any(q=>p.GetDistanceTo(q)<.001) || barEdges.All(e=>ContourGraph.Distance(p,e)>=67.5-.001),"no collision except authorized U top");
                }
                for(var i=0;i<centers.Count;i++)for(var j=i+1;j<centers.Count;j++)Require(centers[i].GetDistanceTo(centers[j])>=100-.001,"dots do not overlap");
                results.Add("PASS "+item.Item1+": "+dots.Count+" native points; style, tangency, support exclusion, collision");
            }
            catch(System.Exception error){results.Add("FAIL "+item.Item1+": "+error);}
        }
        File.WriteAllLines(Path.Combine(root,"native-validation36.txt"),results);
    }
    private static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}
