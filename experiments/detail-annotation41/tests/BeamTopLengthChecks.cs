using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AutoCADPlugin;

internal static class BeamTopLengthChecks
{
    private static Point2d P(double x,double y)=>new Point2d(x,y);
    private static List<Point2d> Rect(double x,double y,double w,double h)=>new List<Point2d>{P(x,y),P(x+w,y),P(x+w,y+h),P(x,y+h)};
    private static List<Point2d> Roof(double height)=>new List<Point2d>{P(0,0),P(960,0),P(960,height-200),P(2260,height+400),P(2100,height+750),P(0,height)};
    private static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}

    public static void Run(Action<string,Action> check, string root)
    {
        check("beam36: inclined stem threshold uses taller vertical side, strict600 with one decimal",()=>
        {
            foreach(var height in new[]{599.9,600,600.01,600.1,700})
            {
                var r=BeamTopRebar.Create(new[]{Roof(height)},new[]{Rect(0,-2000,960,2000)},50);
                var expected=height>=600.1;
                Require((r.Bars.Count==1)==expected,"U threshold "+height);
                Require(r.DirectAnchorRegions.Count==(expected?0:1),"short branch classification");
                Require((r.EffectiveSupports[0].Max(p=>p.Y)>0)==expected,"extension threshold");
            }
        });
        check("beam36: short nonsloping curb has exactly one U and no virtual support",()=>
        {
            var plan=Plan(Rect(0,0,960,400),2000);
            Require(plan.BeamTops.Bars.Count==1 && plan.Bars.Count==1,"no duplicate ordinary U");
            Require(plan.Boundaries.Count==0,"ordinary inset removed");
            Require(plan.Supports[0].Max(p=>p.Y)==0,"actual beam stays original");
            Require(plan.BeamTops.GeometrySupports[0].Max(p=>p.Y)==400,"geometric mask only");
            Require(!plan.BeamTops.Regions[0].IsExtension,"curb classification");
            var guides=BeamAnchorGuide.Create(plan.OriginalSupports,plan.BeamTops,50);
            Require(guides[0].Lines.All(l=>l.Top==0),"curb must not supply infinite U guides");
        });
        check("beam36: short roof folds at inset intersections then1000 from original beam top",()=>
        {
            var plan=Plan(Roof(600),2000);
            Require(plan.BeamTops.Bars.Count==0 && plan.BeamTops.DirectAnchorRegions.Count==1,"no U");
            Require(plan.AnchorEnds.Count==2 && plan.AnchorEnds.All(e=>e.Kind==RebarAnchorage.AnchorKind.BeamTopStraight),"two vertical anchors");
            foreach(var end in plan.AnchorEnds)
            {
                Require(Math.Abs(end.Extension[0].Y)<.001 && Math.Abs(end.Extension.Last().Y+1000)<.001,"1000 starts at beam top");
                Require(end.Original.Y>0,"fold remains above beam top");
                Require(Math.Abs(end.Original.X-50)<.001 || Math.Abs(end.Original.X-910)<.001,"side inward50");
                var slope= end.Original.X<100 ? new ContourGraph.Edge(P(0,600),P(2100,1350)) : new ContourGraph.Edge(P(960,400),P(2260,1000));
                // Lower slope starts at the original side; its offset intersection
                // lies on the infinite offset line before that finite segment.
                var axis=(slope.End-slope.Start).GetNormal();var d=end.Original-slope.Start;
                Require(Math.Abs(Math.Abs(d.X*axis.Y-d.Y*axis.X)-50)<.001,"inclined offset50");
            }
            Require(plan.Supports[0].Max(p=>p.Y)==0,"not an extension");
            Require(PointAnchorExclusion.Read(plan).Any(e=>e.Start.Y>0),"fold excluded from point corners");
        });
        check("beam36: short roof insufficient beam depth reports failure without bending",()=>
        {
            var plan=Plan(Roof(600),900);
            Require(plan.BeamTops.Bars.Count==0,"no U");
            Require(plan.AnchorEnds.Count==2 && plan.AnchorEnds.All(e=>e.Kind==RebarAnchorage.AnchorKind.Failed && e.Reason.Contains("1000")),"both failures explicit");
        });
        check("beam36: detached inclined contour does not disqualify short curb",()=>
        {
            var roof=Roof(500).Select(p=>p+new Vector2d(0,1500)).ToList();
            var result=BeamTopRebar.Create(new[]{Rect(0,0,960,400),roof},new[]{Rect(0,-2000,960,2000)},50);
            Require(result.Bars.Count==1 && !result.Regions[0].HasInclinedConnection,"detached ignored");
        });
        check("beam36: sloping cap inside curb width is not an attached inclined branch",()=>
        {
            var cap=new List<Point2d>{P(0,0),P(960,0),P(960,300),P(0,500)};
            var result=BeamTopRebar.Create(new[]{cap},new[]{Rect(0,-2000,960,2000)},50);
            Require(result.Bars.Count==1 && !result.Regions[0].IsExtension,"cap U only");
        });
        check("beam36: native fixtures for short roof, tall roof and small curb",()=>
        {
            foreach(var item in new[]{(Name:"short-roof36",Ring:Roof(600)),(Name:"tall-roof36",Ring:Roof(800)),(Name:"curb36",Ring:Rect(0,0,960,400))})
            {
                var plan=Plan(item.Ring,2000);
                using var output=new Database(true,true);
                var previous=HostApplicationServices.WorkingDatabase;
                HostApplicationServices.WorkingDatabase=output;
                try
                {
                    using(var tr=output.TransactionManager.StartTransaction())
                    {
                        Standards.Ensure(output,tr);
                        var space=(BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(output),OpenMode.ForWrite);
                        foreach(var edge in ContourGraph.Edges(item.Ring))DetailWriter.WriteOutlineSegment(space,tr,edge.Start,edge.End,Standards.OtherThinLayer);
                        using var support=new Polyline{Closed=true};var ring=Rect(0,-2000,960,2000);
                        for(var i=0;i<ring.Count;i++)support.AddVertexAt(i,ring[i],0,0,0);
                        DetailWriter.WriteControlBoundary(space,tr,support);
                        DetailWriter.WriteSupportAnnotations(space,tr,new[]{support},"楼层梁");
                        OffsetRebar.Write(output,tr,plan);
                        var entities=space.Cast<ObjectId>().Select(id=>(Entity)tr.GetObject(id,OpenMode.ForRead)).ToList();
                        File.WriteAllLines(Path.Combine(root,item.Name+"-entities.txt"),entities.Select(e=>e is Line l?
                            "L|"+l.StartPoint.X+","+l.StartPoint.Y+","+l.EndPoint.X+","+l.EndPoint.Y:e is Polyline p?
                            (p.Layer=="S-REIN"?"B|":"P|")+string.Join(",",Enumerable.Range(0,p.NumberOfVertices).Concat(p.Closed?new[]{0}:Array.Empty<int>()).SelectMany(i=>new[]{p.GetPoint2dAt(i).X,p.GetPoint2dAt(i).Y})):""));
                        tr.Commit();
                    }
                    output.SaveAs(Path.Combine(root,item.Name+".dwg"),DwgVersion.Current);
                }
                finally{HostApplicationServices.WorkingDatabase=previous;}
                var dots=DistributedPointRebar.Create(plan,plan.Bars,CornerPointRebar.Create(plan,plan.Bars));
                Require(dots.Centers.All(p=>!plan.OriginalSupports.Any(s=>CornerPointRebar.IntersectsSupport(p,s))),"no point in actual beam");
                File.WriteAllLines(Path.Combine(root,item.Name+"-distribution.txt"),new[]{"points="+dots.Centers.Count,"fixed="+dots.FixedCenters.Count,"rows="+dots.Rows.Count}.Concat(dots.Warnings));
                if(item.Name=="short-roof36")Require(dots.Centers.Any(p=>Math.Abs(p.Y-400)<.001),"no-U point row ends400 above actual beam");
            }
        });
    }
    private static OffsetRebar.Plan Plan(List<Point2d> material,double depth)
    {
        var entities=ContourGraph.Edges(material).Select(e=>(Entity)new Line(new Point3d(e.Start.X,e.Start.Y,0),new Point3d(e.End.X,e.End.Y,0)){Layer=Standards.OtherThinLayer}).ToList();
        var support=new Polyline{Closed=true,Layer=Standards.RegionLayer};var ring=Rect(0,-depth,960,depth);
        for(var i=0;i<ring.Count;i++)support.AddVertexAt(i,ring[i],0,0,0);entities.Add(support);
        try{return OffsetRebar.Create(entities);}finally{foreach(var entity in entities)entity.Dispose();}
    }
}
