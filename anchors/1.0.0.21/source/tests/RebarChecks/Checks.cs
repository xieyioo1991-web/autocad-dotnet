using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.ApplicationServices;
using AutoCADPlugin;
public class Checks {
 const string Root=@"C:\Users\Administrator\Desktop\00C#.NET二次开发\tests\";
 [CommandMethod("SD_CHECK")]
 public void Run(){
 try {
 var db=Application.DocumentManager.MdiActiveDocument.Database;
 using(var tr=db.TransactionManager.StartTransaction()){
 var ms=(BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db),OpenMode.ForRead);
 var list=ArchitecturalOutlineExtractor.Extract(tr,SelectionSet.FromObjectIds(ms.Cast<ObjectId>().ToArray()));
 var origin=new Point2d(18259826.137068637,-1150051.2048687339);
 var edges=list.Select(s=>new OutlineSegment(Point(s.Start,origin,4),Point(s.End,origin,4),s.TargetLayer)).ToList();
 var placement=Example1Rebar.Match(edges);
 Assert(placement.Anchor.GetDistanceTo(Point2d.Origin)<.01,"source placement anchor");
 Assert(Math.Abs(placement.RoofEnd-4060.550925)<.1,"roof endpoint adapts to architectural outline");
 MustReject(list.Select(s=>new OutlineSegment(Point(s.Start,origin,1),Point(s.End,origin,1),s.TargetLayer)).ToList(),"reject 1:100 geometry");
 MustReject(new List<OutlineSegment>(),"reject empty selection");
 var two=new List<OutlineSegment>(edges);two.AddRange(edges.Select(s=>new OutlineSegment(s.Start+new Vector2d(20000,0),s.End+new Vector2d(20000,0),s.TargetLayer)));MustReject(two,"reject multiple details");
 using(var output=new Database(true,true)){
var previous=HostApplicationServices.WorkingDatabase; HostApplicationServices.WorkingDatabase=output; try {
 using(var ot=output.TransactionManager.StartTransaction()){
 Standards.Ensure(output,ot);
 var space=(BlockTableRecord)ot.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(output),OpenMode.ForWrite);
 foreach(var e in edges) DetailWriter.WriteOutlineSegment(space,ot,e.Start,e.End,e.TargetLayer);
 Assert(Example1Rebar.Write(output,ot,placement)==30,"30 entities");
 var bars=space.Cast<ObjectId>().Select(id=>ot.GetObject(id,OpenMode.ForRead)).OfType<Polyline>().Where(p=>p.Layer=="S-REIN").ToList();
 var dots=space.Cast<ObjectId>().Select(id=>ot.GetObject(id,OpenMode.ForRead)).OfType<Polyline>().Where(p=>p.Layer=="S-REIN-POINT").ToList();
 Assert(bars.Count==7 && dots.Count==23,"7 bars + 23 dots");
 Assert(bars.All(p=>Math.Abs(p.ConstantWidth-35)<1e-6),"bar width 35");
 Assert(dots.All(p=>p.Closed && p.NumberOfVertices==2 && p.GetBulgeAt(0)==1 && p.GetBulgeAt(1)==1 && Math.Abs(p.ConstantWidth-50)<1e-6),"native solid dot representation");
 File.WriteAllLines(Root+"dot-extents.txt", dots.Select(p=>$"{p.GeometricExtents.MinPoint} => {p.GeometricExtents.MaxPoint}")); Assert(dots.All(p=>Math.Abs(p.GetPoint2dAt(0).GetDistanceTo(p.GetPoint2dAt(1))+p.ConstantWidth-100)<.001),"dot outer diameter 100");
 for(int k=0;k<bars.Count;k++)for(int j=0;j<bars[k].NumberOfVertices;j++)Assert(bars[k].GetPoint2dAt(j).GetDistanceTo(Example1Rebar.Locate(Example1RebarData.Bars[k][j],placement,k>=5))<.001,"vertex geometry");
 bool duplicate=false;try{Example1Rebar.Write(output,ot,placement);}catch(InvalidOperationException){duplicate=true;} Assert(duplicate,"duplicate guard");
 File.WriteAllLines(Root+"render-entities.txt",space.Cast<ObjectId>().Select(id=>ot.GetObject(id,OpenMode.ForRead)).OfType<Entity>().Select(e=> e is Line l ? "L|"+l.StartPoint.X+","+l.StartPoint.Y+","+l.EndPoint.X+","+l.EndPoint.Y : e is Polyline p ? (p.Layer=="S-REIN" ? "B|" : "D|")+string.Join(",",Enumerable.Range(0,p.NumberOfVertices).SelectMany(i=>new[]{p.GetPoint2dAt(i).X,p.GetPoint2dAt(i).Y})) : ""));
ot.Commit();
 }
 output.SaveAs(Root+"example1-rebar-check.dwg",DwgVersion.Current); } finally {HostApplicationServices.WorkingDatabase=previous;}
 }
 // Reference placement must reproduce stored vertices without stretching.
 var exact=new Example1Rebar.Placement{Anchor=new Point2d(674114.2384208026,204485.17805524194),RoofEnd=Example1RebarData.RoofEndX,RoofSlope=.424474816};
 foreach(var p in Example1RebarData.Points)Assert((Example1Rebar.Locate(p,exact,true)-exact.Anchor-new Vector2d(p.X,p.Y)).Length<1e-6,"exact reference coordinates");
 File.WriteAllText(Root+"rebar-results.txt","PASS: source DWG extraction; 4x contour matching; roof adaptation; wrong scale/empty/multiple rejection; native 7 bars/23 solid dots; width 35; outer diameter 100; vertex placement; duplicate guard; exact reference coordinates.\nOutput: example1-rebar-check.dwg");
 }
 }catch(System.Exception e){File.WriteAllText(Root+"rebar-results.txt",e.ToString());}
 }
 [CommandMethod("SD_REFERENCE")]
 public void Reference(){try{
 var db=Application.DocumentManager.MdiActiveDocument.Database;
 using(var tr=db.TransactionManager.StartTransaction()){
 var ms=(BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db),OpenMode.ForRead);
 var all=ms.Cast<ObjectId>().Select(id=>tr.GetObject(id,OpenMode.ForRead)).OfType<Entity>().ToList();
 var old=all.OfType<Polyline>().Where(p=>p.Layer=="S-REIN"||p.Layer=="S-REIN-POINT").ToList();
 var before=old.Select(p=>Enumerable.Range(0,p.NumberOfVertices).Select(i=>p.GetPoint2dAt(i)).ToArray()).ToList();
 var match=Example1Rebar.Match(Example1Rebar.ReadEdges(all));
 foreach(var e in old){e.UpgradeOpen();e.Erase();}
 Example1Rebar.Write(db,tr,match);
 var after=ms.Cast<ObjectId>().Where(id=>!id.IsErased).Select(id=>tr.GetObject(id,OpenMode.ForRead)).OfType<Polyline>().Where(p=>p.Layer=="S-REIN"||p.Layer=="S-REIN-POINT").ToList();
 foreach(var poly in before)Assert(after.Any(p=>p.NumberOfVertices==poly.Length && Enumerable.Range(0,poly.Length).All(i=>p.GetPoint2dAt(i).GetDistanceTo(poly[i])<.01)),"reference native vertices");
 File.WriteAllText(Root+"reference-results.txt","PASS: all 30 target rebar polylines regenerated; every original vertex reproduced within 0.01 drawing unit. Original DWG not saved.");
 }
 }catch(System.Exception e){File.WriteAllText(Root+"reference-results.txt",e.ToString());}}
 static Point2d Point(Point2d p,Point2d origin,double s)=>new Point2d((p.X-origin.X)*s,(p.Y-origin.Y)*s);
 static void Assert(bool ok,string what){if(!ok)throw new InvalidOperationException("FAIL: "+what);}
 static void MustReject(List<OutlineSegment> edges,string what){bool rejected=false;try{Example1Rebar.Match(edges);}catch(InvalidOperationException){rejected=true;}Assert(rejected,what);}
}





