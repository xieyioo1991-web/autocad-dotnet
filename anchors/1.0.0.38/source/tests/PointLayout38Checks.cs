using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AutoCADPlugin;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

internal static class PointLayout38Checks
{
    public static void Run(Action<string, Action> check, string root)
    {
        check("contact38: finite endpoint contact is tangent instead of400, nearby crossings are not endpoints", () =>
        {
            var path = new RebarPath(new[] { P(50,50), P(2000,50) }, false);
            var row = PointRowPath.Offset(CornerPointRebar.Segments(path).ToList(),67.5,path).Single();
            var receiver = new ContourGraph.Edge(P(50,-500),P(50,1000));
            Require(PointBarContact.TryClearance(row,true,new[]{receiver},out var t) && Math.Abs(t-67.5)<.01,"tangent start");
            Require(!PointBarContact.TryClearance(row,true,new[]{new ContourGraph.Edge(P(450,-500),P(450,1000))},out _),"mid-span crossing is not a touching endpoint");
            Require(!PointBarContact.TryClearance(row,true,new[]{new ContourGraph.Edge(P(50,500),P(50,1000))},out _),"infinite line is not a finite contact");
        });
        check("labels38: clear beam uses horizontal underline, clear slab vertical pointer", () =>
        {
            var beam=Rect(2800,-3000,960,4100); var slab=Rect(3760,-1500,3600,480);
            var placements=SupportLabelLayout.Create(new[]{beam,slab},Array.Empty<ContourGraph.Edge>(),"楼层梁");
            Require(placements.Count==2 && placements.All(p=>p.Conflicts==0),"clear annotations");
            Require(placements[0].Form=="horizontal" && placements[0].Label=="楼层梁","beam straight underline");
            Require(placements[1].Form=="vertical" && placements[1].Label=="楼层板","slab vertical pointer");
            Require(placements[0].Leader.All(p=>p.Y<placements[0].Text.Y),"leader does not join text middle");
            Require(placements[1].Leader[0].X<placements[1].Text.X,"vertical pointer beside text");
        });
        check("labels38: obstructed straight positions choose an elbow with a horizontal landing", () =>
        {
            var slab=Rect(0,0,3000,480);
            // Block both centered straight-label boxes while leaving the
            // upper-right landing area clear; supplied as actual contour lines.
            var obstacles=new[]{new ContourGraph.Edge(P(1520,-400),P(2450,-400)),
                new ContourGraph.Edge(P(1520,850),P(1650,850))};
            var placement=SupportLabelLayout.Create(new[]{slab},obstacles,"楼层梁").Single();
            Require(placement.Form=="elbow" && placement.Conflicts==0,"clear alternate elbow");
            Require(placement.Leader.Count==3 && Math.Abs(placement.Leader[1].Y-placement.Leader[2].Y)<.01,"horizontal landing");
            Require(placement.Text.Y>placement.Leader[1].Y,"text above landing");
        });
        check("labels38: translation and input order preserve annotation geometry", () =>
        {
            var supports=new[]{Rect(2800,-3000,960,4100),Rect(3760,-1500,3600,480)};
            var initial=SupportLabelLayout.Create(supports,Array.Empty<ContourGraph.Edge>(),"楼层梁");
            var shift=new Vector2d(18259826,-1150051);
            var changed=SupportLabelLayout.Create(supports.Reverse().Select(p=>p.Select(v=>v+shift).ToList()).ToList(),Array.Empty<ContourGraph.Edge>(),"楼层梁");
            Require(initial.Zip(changed,(a,b)=>a.Form==b.Form && (a.Text+shift).GetDistanceTo(b.Text)<.01).All(v=>v),"same translated placement");
        });
        check("contact38: screenshot L fixture and native annotation entities", () => Create(root));
    }

    private static void Create(string root)
    {
        var rings=ContourGraph.Build(new[]{Rect(0,-480,480,1580),Rect(480,-480,2320,480)}.SelectMany(ContourGraph.Edges).ToList(),new List<List<Point2d>>());
        var supports=new[]{Rect(2800,-3000,960,4100),Rect(3760,-1500,3600,480)};
        using var db=new Database(true,true);
        var previous=HostApplicationServices.WorkingDatabase;HostApplicationServices.WorkingDatabase=db;
        try
        {
            using(var tr=db.TransactionManager.StartTransaction())
            {
                Standards.Ensure(db,tr);
                var space=(BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db),OpenMode.ForWrite);
                var outlines=rings.SelectMany(ContourGraph.Edges).Select(e=>new OutlineSegment(e.Start,e.End,Standards.OtherThinLayer)).ToList();
                foreach(var edge in outlines) DetailWriter.WriteOutlineSegment(space,tr,edge.Start,edge.End,edge.TargetLayer);
                var boundaries=new List<Polyline>();
                foreach(var support in supports)
                {
                    var polyline=new Polyline();for(var i=0;i<support.Count;i++)polyline.AddVertexAt(i,support[i],0,0,0);
                    polyline.Closed=true;DetailWriter.WriteControlBoundary(space,tr,polyline);boundaries.Add(polyline);
                }
                var warnings=DetailWriter.WriteSupportAnnotations(space,tr,boundaries,"楼层梁",outlines);
                Require(warnings.Count==0,"leaders have space in screenshot fixture");
                var entities=space.Cast<ObjectId>().Select(id=>(Entity)tr.GetObject(id,OpenMode.ForRead)).ToList();
                Require(entities.OfType<DBText>().Count()==2,"beam and slab labels");
                Require(entities.OfType<DBText>().All(t=>t.Layer==Standards.TextLayer && t.Height==250),"original text standard retained");
                var plan=OffsetRebar.Create(entities);
                OffsetRebar.Write(db,tr,plan);
                var points=DistributedPointRebar.Create(plan,plan.Bars,CornerPointRebar.Create(plan,plan.Bars));
                var world=points.Centers.Select(p=>p+new Vector2d(plan.Origin.X,plan.Origin.Y)).ToList();
                bool Has(double x,double y)=>world.Any(p=>p.GetDistanceTo(P(x,y))<.01);
                Require(Has(117.5,310) && Has(362.5,310),"left and right vertical intermediate dots align");
                Require(Has(117.5,-117.5),"upper horizontal row reaches left-bar tangent contact");
                Require(!Has(362.5,-362.5),"vertical tail delegated to horizontal distribution, no crowded extra dot");
                Require(world.Count==12,"twelve dots in the screenshot analogue");
                var reversed=plan.Bars.AsEnumerable().Reverse().Select(b=>new RebarPath(b.Points.Reverse(),b.IsClosed)).ToList();
                var reversePoints=DistributedPointRebar.Create(plan,reversed,CornerPointRebar.Create(plan,reversed));
                Require(points.Centers.Count==reversePoints.Centers.Count && points.Centers.All(p=>reversePoints.Centers.Any(q=>p.GetDistanceTo(q)<.01)),"contact handoff independent of selection/traversal");
                var diagnostics=new List<string>{"synthetic screenshot1-2 analogue, not the user's original DWG"};
                diagnostics.AddRange(points.Rows.Select(row=>"R|"+string.Join(";",row.Select(p=>Format(p+new Vector2d(plan.Origin.X,plan.Origin.Y))))));
                diagnostics.AddRange(points.Warnings.Select(w=>"W|"+w));
                File.WriteAllLines(Path.Combine(root,"contact38-diagnostics.txt"),diagnostics);
                tr.Commit();
            }
            db.SaveAs(Path.Combine(root,"contact38.dwg"),DwgVersion.Current);
            using(var tr=db.TransactionManager.StartTransaction())
            {
                var space=(BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db),OpenMode.ForRead);
                var entities=space.Cast<ObjectId>().Select(id=>(Entity)tr.GetObject(id,OpenMode.ForRead)).ToList();
                var plan=OffsetRebar.Create(entities);
                var points=DistributedPointRebar.Create(plan,plan.Bars,CornerPointRebar.Create(plan,plan.Bars));
                PointRebarWriter.Write(db,tr,points.Centers.Select(p=>p+new Vector2d(plan.Origin.X,plan.Origin.Y)).ToList());
                Export(root,"contact38",space,tr);tr.Commit();
            }
            db.SaveAs(Path.Combine(root,"contact38-points.dwg"),DwgVersion.Current);
        }
        finally{HostApplicationServices.WorkingDatabase=previous;}
    }

    internal static void Export(string root,string name,BlockTableRecord space,Transaction tr,bool nearOrigin=false)
    {
        var lines=new List<string>();
        foreach(ObjectId id in space)
        {
            var entity=(Entity)tr.GetObject(id,OpenMode.ForRead);
            if(nearOrigin && ((entity is Line farLine && Math.Abs(farLine.StartPoint.X)>1000000) ||
                (entity is Polyline farPoly && Math.Abs(farPoly.GetPoint2dAt(0).X)>1000000) ||
                (entity is DBText farText && Math.Abs(farText.Position.X)>1000000))) continue;
            if(entity is Line line) lines.Add("L|"+Format(P(line.StartPoint.X,line.StartPoint.Y))+","+Format(P(line.EndPoint.X,line.EndPoint.Y)));
            else if(entity is Polyline poly)
            {
                if(poly.Layer==Standards.PointReinforcementLayer) {lines.Add("D|"+Format(GeometryTools.MidPoint(poly.GetPoint2dAt(0),poly.GetPoint2dAt(1))));continue;}
                var pts=Enumerable.Range(0,poly.NumberOfVertices).Select(poly.GetPoint2dAt).ToList();if(poly.Closed)pts.Add(pts[0]);
                lines.Add((poly.Layer==Standards.ReinforcementLayer?"B|":"P|")+string.Join(",",pts.Select(Format)));
            }
            else if(entity is DBText text) lines.Add("T|"+Format(P(text.Position.X,text.Position.Y))+","+text.Height.ToString(CultureInfo.InvariantCulture)+"|"+text.TextString);
        }
        File.WriteAllLines(Path.Combine(root,name+"-render.txt"),lines);
    }
    private static string Format(Point2d p)=>p.X.ToString("0.######",CultureInfo.InvariantCulture)+","+p.Y.ToString("0.######",CultureInfo.InvariantCulture);
    private static Point2d P(double x,double y)=>new Point2d(x,y);
    private static List<Point2d> Rect(double x,double y,double w,double h)=>new List<Point2d>{P(x,y),P(x+w,y),P(x+w,y+h),P(x,y+h)};
    private static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}
