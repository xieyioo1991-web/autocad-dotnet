using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using AutoCADPlugin;
public sealed class OffsetChecks
{
 private static readonly string Root=Environment.GetEnvironmentVariable("SD_OFFSET_TEST_ROOT");
 private static readonly List<string> Results=new List<string>();
 private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
 private static List<Point2d> Rect(double x,double y,double w,double h)=>new List<Point2d>{new Point2d(x,y),new Point2d(x+w,y),new Point2d(x+w,y+h),new Point2d(x,y+h)};
 private static void Case(string name,Action action){try{action();Results.Add("PASS "+name);}catch(System.Exception e){Results.Add("FAIL "+name+": "+e);}}
 [CommandMethod("SD_OFFSET_CHECK")]
 public void Run()
 {
 var db=Application.DocumentManager.MdiActiveDocument.Database;
 using(var tr=db.TransactionManager.StartTransaction()){Standards.Ensure(db,tr);tr.Commit();}
 Case("rectangle 1000x600 -> 900x500, 50 offset",()=>{
 var plan=Make(new[]{Rect(0,0,1000,600)});Require(plan.Bars.Count==1,"count");Require(Math.Abs(Math.Abs(ContourGraph.Area(plan.OffsetLoops[0]))-450000)<.01,"area");
 var world=plan.Bars[0].Points.Select(p=>plan.Origin+new Vector2d(p.X,p.Y)).ToList();Require(Math.Abs(world.Min(p=>p.X)-50)<.01&&Math.Abs(world.Max(p=>p.Y)-550)<.01,"offset coordinates");});
 Case("clockwise and large translated coordinates",()=>{
 var r=Rect(18259826,-1150051,1000,600);r.Reverse();var p=Make(new[]{r});Require(Math.Abs(Math.Abs(ContourGraph.Area(p.OffsetLoops[0]))-450000)<.01,"area");});
 Case("rotated rectangle, non-template dimensions",()=>{
 var r=Rect(0,0,1600,800).Select(p=>new Point2d(p.X*Math.Cos(.6)-p.Y*Math.Sin(.6),p.X*Math.Sin(.6)+p.Y*Math.Cos(.6))).ToList();var plan=Make(new[]{r});Require(Math.Abs(Math.Abs(ContourGraph.Area(plan.OffsetLoops[0]))-1050000)<.01,"area");});
 Case("concave L shape",()=>{
 var r=new List<Point2d>{new Point2d(0,0),new Point2d(1000,0),new Point2d(1000,400),new Point2d(400,400),new Point2d(400,1000),new Point2d(0,1000)};var p=Make(new[]{r});Require(Math.Abs(Math.Abs(ContourGraph.Area(p.OffsetLoops[0]))-450000)<.01,"area");});
 Case("shared edges merged before offset",()=>{var p=Make(new[]{Rect(0,0,600,500),Rect(600,0,600,500)});Require(p.Bars.Count==1,"shared edge created two bars");Require(Math.Abs(Math.Abs(ContourGraph.Area(p.OffsetLoops[0]))-440000)<.01,"area");});
 Case("support at end subtracted",()=>{var p=Make(new[]{Rect(0,0,1000,600)},new[]{Rect(800,0,200,600)});Require(p.Bars.Count==1,"count");Require(Math.Abs(Math.Abs(ContourGraph.Area(p.OffsetLoops[0]))-350000)<.01,"area");});
 Case("support strip splits two material regions",()=>{var p=Make(new[]{Rect(0,0,2000,600)},new[]{Rect(800,-100,400,800)});Require(p.Bars.Count==2,"count");Require(p.OffsetLoops.All(r=>Math.Abs(Math.Abs(ContourGraph.Area(r))-350000)<.01),"area");});
 Case("open contour rejected",()=>{var es=Lines(Rect(0,0,1000,600));es[0].Dispose();es.RemoveAt(0);try{Reject(()=>OffsetRebar.Create(es));}finally{foreach(var e in es)e.Dispose();}});
 Case("narrow region rejected",()=>Reject(()=>Make(new[]{Rect(0,0,80,600)})));
 Case("support island rejected",()=>Reject(()=>Make(new[]{Rect(0,0,1000,1000)},new[]{Rect(300,300,200,200)})));
 PointContactChecks.Run(Case, Root);
 SupportOpeningChecks.Run(Case, Root);
 AnchorageChecks.Run(Case);
 CornerBreakChecks.Run(Case);
 CornerSizeChecks.Run(Case);
 BeamTopChecks.Run(Case);
 VerticalAnchorageChecks.Run(Case);
 BeamSideChecks.Run(Case, Root);
 SmallRegionChecks.Run(Case, Root);
 PointCornerChecks.Run(Case);
 PointJunctionChecks.Run(Case);
 PointDistributionChecks.Run(Case);
 PointLayout37Checks.Run(Case, Root);
 PointLayout37ReviewChecks.Run(Case);
 BeamTopLengthChecks.Run(Case, Root);
 Case("actual architectural sample plus two supports",()=>Sample(db));
 Case("actual architectural coordinates, beam top aligned with eave, both interfaces open",()=>Sample(db, -840, "precision-interface"));
 File.WriteAllLines(Path.Combine(Root,"offset-results.txt"),Results);
 }
 private static void Reject(Action a){bool rejected=false;try{a();}catch(InvalidOperationException){rejected=true;}Require(rejected,"not rejected");}
 private static List<Entity> Lines(List<Point2d> ring){return ContourGraph.Edges(ring).Select(e=>(Entity)new Line(new Point3d(e.Start.X,e.Start.Y,0),new Point3d(e.End.X,e.End.Y,0)){Layer=Standards.OtherThinLayer}).ToList();}
 private static Polyline Poly(List<Point2d> r){var p=new Polyline();for(int i=0;i<r.Count;i++)p.AddVertexAt(i,r[i],0,0,0);p.Closed=true;p.Layer=Standards.OtherThinLayer;return p;}
 private static OffsetRebar.Plan Make(IEnumerable<List<Point2d>> rings,IEnumerable<List<Point2d>>? supports=null){var es=rings.SelectMany(Lines).ToList();if(supports!=null)es.AddRange(supports.Select(Poly));try{return OffsetRebar.CreateOutline(es);}finally{foreach(var e in es)e.Dispose();}}
 private static void Sample(Database db, double beamTop = -1080, string name = "offset50-sample")
 {
 List<OutlineSegment> outlines;
 using(var tr=db.TransactionManager.StartTransaction()){
 var ms=(BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db),OpenMode.ForRead);
 outlines=ArchitecturalOutlineExtractor.Extract(tr,SelectionSet.FromObjectIds(ms.Cast<ObjectId>().ToArray()));}
 var origin=new Point2d(18259826.137068637,-1150051.2048687339);
 var es=outlines.Select(e=>(Entity)new Line(new Point3d((e.Start.X-origin.X)*4,(e.Start.Y-origin.Y)*4,0),new Point3d((e.End.X-origin.X)*4,(e.End.Y-origin.Y)*4,0)){Layer=Standards.OtherThinLayer}).ToList();
 es.Add(Poly(Rect(0,-3280,960,3280 + beamTop)));es.Add(Poly(Rect(960,-1560,3100.550925,480)));
 var plan=OffsetRebar.Create(es);Results.Add("INFO sample boundaries="+plan.Boundaries.Count+" bars="+plan.Bars.Count+" U="+plan.BeamTops.Bars.Count);
 BeamTopChecks.VerifyExample(plan, beamTop);
 VerticalAnchorageChecks.VerifyExample(plan);
 BeamSideChecks.VerifyExample(plan);
 var pointPlan=DistributedPointRebar.Create(plan,plan.Bars,CornerPointRebar.Create(plan,plan.Bars));
 if(name=="precision-interface"){PointLayout37Checks.RunArchitectural(Case,Root,plan);}
 File.WriteAllLines(Path.Combine(Root,name+"-distribution36.txt"),new[]{"points="+pointPlan.Centers.Count,"fixed="+pointPlan.FixedCenters.Count,"rows="+pointPlan.Rows.Count,"redistributed="+pointPlan.RedistributedCorners}.Concat(pointPlan.Warnings));
 Require(pointPlan.Centers.All(p=>!plan.OriginalSupports.Any(s=>CornerPointRebar.IntersectsSupport(p,s))),"sample dots outside real support");
 Results.Add("INFO corners=" + plan.CornerBreaks.CornerCount + " ends=" + plan.CornerBreaks.Ends.Count + " hits=" + plan.CornerBreaks.Ends.Count(end => end.Hit));
 Results.Add("INFO anchors straight=" + plan.AnchorEnds.Count(end => end.Kind == RebarAnchorage.AnchorKind.Straight) + " bent=" + plan.AnchorEnds.Count(end => end.Kind == RebarAnchorage.AnchorKind.Bent) + " lower600=" + plan.AnchorEnds.Count(end => end.Kind == RebarAnchorage.AnchorKind.LowerStraight) + " vertical800=" + plan.AnchorEnds.Count(end => end.Kind == RebarAnchorage.AnchorKind.VerticalBent) + " failed=" + plan.AnchorEnds.Count(end => end.Kind == RebarAnchorage.AnchorKind.Failed));
 foreach (var end in plan.AnchorEnds.Where(end => end.Kind == RebarAnchorage.AnchorKind.Failed)) { Results.Add("INFO anchor failure: " + end.Reason); }
 using(var output=new Database(true,true)){
 var previous=HostApplicationServices.WorkingDatabase;HostApplicationServices.WorkingDatabase=output;
 try{
 using(var tr=output.TransactionManager.StartTransaction()){
 Standards.Ensure(output,tr);var ms=(BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(output),OpenMode.ForWrite);
 // recreate entities in destination database so source layer IDs never leak.
 var supports=new List<Polyline>();foreach(var e in es){if(e is Line l){DetailWriter.WriteOutlineSegment(ms,tr,new Point2d(l.StartPoint.X,l.StartPoint.Y),new Point2d(l.EndPoint.X,l.EndPoint.Y),Standards.OtherThinLayer);}else if(e is Polyline pl){var copy=Poly(Enumerable.Range(0,pl.NumberOfVertices).Select(pl.GetPoint2dAt).ToList());DetailWriter.WriteControlBoundary(ms,tr,copy);supports.Add(copy);}}
 DetailWriter.WriteSupportAnnotations(ms,tr,supports,"楼层梁");
 AnchorageChecks.VerifyOld25Conflict(output,tr,plan);
 CornerBreakChecks.VerifyOld26Conflict(output,tr,plan);
 var before=ms.Cast<ObjectId>().ToList();OffsetRebar.Write(output,tr,plan);var bars=ms.Cast<ObjectId>().Except(before).Select(id=>tr.GetObject(id,OpenMode.ForRead)).OfType<Polyline>().ToList();
 Require(bars.Count==plan.Bars.Count&&bars.All(p=>p.ConstantWidth==35&&p.Layer=="S-REIN"),"entities");Require(bars.Zip(plan.Bars,(entity,path)=>entity.Closed==path.IsClosed).All(match=>match),"closed flags");
 Require(!ms.Cast<ObjectId>().Select(id=>(Entity)tr.GetObject(id,OpenMode.ForRead)).Any(e=>e.Layer=="S-REIN-POINT"),"no point bars");
 Reject(()=>OffsetRebar.Write(output,tr,plan));
 File.WriteAllLines(Path.Combine(Root,name == "offset50-sample" ? "render-entities.txt" : name + "-entities.txt"),ms.Cast<ObjectId>().Select(id=>(Entity)tr.GetObject(id,OpenMode.ForRead)).Select(e=>e is Line l?"L|"+l.StartPoint.X+","+l.StartPoint.Y+","+l.EndPoint.X+","+l.EndPoint.Y:e is Polyline p?(p.Layer=="S-REIN"?"B|":"P|")+string.Join(",",Enumerable.Range(0,p.NumberOfVertices).Concat(p.Closed?new[]{0}:Array.Empty<int>()).SelectMany(i=>new[]{p.GetPoint2dAt(i).X,p.GetPoint2dAt(i).Y})):""));
 tr.Commit();}
 output.SaveAs(Path.Combine(Root,name + ".dwg"),DwgVersion.Current);
 }finally{HostApplicationServices.WorkingDatabase=previous;}}
 foreach(var e in es)e.Dispose();
 }
}


