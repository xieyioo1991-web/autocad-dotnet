using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Geometry;
using AutoCADPlugin;

internal static class PointDistributionChecks
{
    private static Point2d P(double x, double y) => new Point2d(x,y);
    private static List<Point2d> Rect(double x,double y,double w,double h) => new List<Point2d>{P(x,y),P(x+w,y),P(x+w,y+h),P(x,y+h)};
    private static void Require(bool value,string message) { if(!value)throw new InvalidOperationException(message); }
    private static void Near(double value,double expected,string message) => Require(Math.Abs(value-expected)<.001,message+": "+value);
    private static DistributedPointRebar.Result Make(OffsetRebar.Plan context,params RebarPath[] bars) =>
        DistributedPointRebar.Create(context,bars,CornerPointRebar.Create(context,bars));

    public static void Run(Action<string,Action> check)
    {
        check("rows36: continuous change of direction is not a free endpoint",()=>
        {
            var source=new RebarPath(new[]{P(0,0),P(1000,0),P(1800,600)},false);
            var rows=PointRowPath.Offset(CornerPointRebar.Segments(source).ToList(),67.5,source);
            Require(rows.Count==1 && rows[0].Points.Count==3,"one continuous guide");
            Near(rows[0].Points[1].X,977.5,"offset intersection X");
            Near(rows[0].Points[1].Y,67.5,"offset intersection Y");
            Near(rows[0].Length,1955,"length across bend");
            Near(rows[0].At(rows[0].EndClearance(true)).GetDistanceTo(source.Points[0]),400,"true endpoint");
        });
        check("rows36: equal division is minimal count under800, anchors never move",()=>
        {
            foreach(var pair in new[]{(Length:1600d,Count:3),(Length:1600.1,Count:4),(Length:2100d,Count:4),(Length:600d,Count:2)})
            {
                var row=PointRowGeometry.Divide(100,100+pair.Length).Select(t=>P(t,200)).ToList();
                Require(row.Count==pair.Count,"minimal intervals");
                Near(row[0].X,100,"head");Near(row.Last().X,100+pair.Length,"tail");
                Require(row.Zip(row.Skip(1),(a,b)=>a.GetDistanceTo(b)).All(d=>d<=800.000001),"maximum800");
            }
        });
        check("rows36: closed3000x1000 retains four exact corners, adds six points",()=>
        {
            var ctx=new OffsetRebar.Plan{Boundaries=new List<List<Point2d>>{Rect(0,0,3000,1000)}};
            var result=Make(ctx,new RebarPath(Rect(50,50,2900,900),true));
            Require(result.FixedCenters.Count==4 && result.Centers.Count==10,"count "+result.Centers.Count);
            Require(result.Warnings.Count==0,string.Join(";",result.Warnings));
            foreach(var y in new[]{117.5,882.5})foreach(var x in new[]{117.5,808.75,1500,2191.25,2882.5})
                Require(result.Centers.Any(p=>p.GetDistanceTo(P(x,y))<.001),"independent coordinate "+P(x,y));
        });
        check("rows36: free endpoint distance is actual400, not axial400",()=>
        {
            var ctx=new OffsetRebar.Plan{Boundaries=new List<List<Point2d>>{Rect(0,0,3000,600)}};
            var result=Make(ctx,new RebarPath(new[]{P(50,50),P(2950,50)},false));
            Require(result.Centers.Count==4,"four free row points");
            var points=result.Centers.OrderBy(p=>p.X).ToList();
            Near(points[0].GetDistanceTo(P(50,50)),400,"start distance");
            Near(points.Last().GetDistanceTo(P(2950,50)),400,"end distance");
            Require(points.All(p=>Math.Abs(p.Y-117.5)<.001),"inside tangency");
        });
        check("rows36: fixed head plus support400 gives equal gaps and no support dots",()=>
        {
            var support=Rect(3000,0,1200,2000);
            var ctx=new OffsetRebar.Plan{Boundaries=new List<List<Point2d>>{Rect(0,0,3000,1000)},
                Supports=new List<List<Point2d>>{support},OriginalSupports=new List<List<Point2d>>{support}};
            var corners=new CornerPointRebar.Result();corners.Centers.Add(P(117.5,117.5));
            var result=DistributedPointRebar.Create(ctx,new[]{new RebarPath(new[]{P(50,50),P(3600,50)},false)},corners);
            Require(result.Centers.Count==5,"five points "+result.Centers.Count);
            Near(result.Centers.Max(p=>p.X),2600,"support400 from circle center");
            Require(result.Rows.Count==1 && result.Rows[0][0]==P(117.5,117.5),"fixed head is actual row start");
            Require(result.Centers.Contains(P(117.5,117.5)),"fixed head unchanged");
            Require(result.Centers.All(p=>!CornerPointRebar.IntersectsSupport(p,support)),"actual support forbidden");
        });
        check("rows36: inclined row tail is400 perpendicular to vertical support edge",()=>
        {
            var support=Rect(3000,0,1500,4000);
            var ctx=new OffsetRebar.Plan{Boundaries=new List<List<Point2d>>{Rect(0,0,3000,4000)},
                Supports=new List<List<Point2d>>{support},OriginalSupports=new List<List<Point2d>>{support}};
            var result=Make(ctx,new RebarPath(new[]{P(200,500),P(3400,2900)},false));
            Require(result.Rows.Count==2,"two sides of internal bar");
            foreach(var row in result.Rows)Near(row.Last().X,2600,"perpendicular400");
        });
        check("rows36: internal reentrant crossing reserves red sector only",()=>
        {
            var ctx=new OffsetRebar.Plan();
            ctx.CornerBreaks.Ends.Add(new RebarExtensionCollision.Extension(P(0,0),new Vector2d(1,0),400,0,0));
            ctx.CornerBreaks.Ends.Add(new RebarExtensionCollision.Extension(P(0,0),new Vector2d(0,1),400,1,0));
            var result=PointFixedCorners.Select(ctx,new[]{P(-67.5,67.5),P(67.5,67.5),P(67.5,-67.5),P(-900,300)});
            Require(result.Count==2 && result.Contains(P(67.5,-67.5)) && result.Contains(P(-900,300)),"red and external only");
        });
        check("rows36: virtual extension is a400 boundary for incoming bars",()=>
        {
            var beam=Rect(3000,-2000,960,2000);
            var top=BeamTopRebar.Create(new[]{Rect(3000,0,960,1000)},new[]{beam},50);
            var ctx=new OffsetRebar.Plan{Boundaries=new List<List<Point2d>>{Rect(0,0,3000,600)},BeamTops=top,
                Supports=top.EffectiveSupports,OriginalSupports=new List<List<Point2d>>{beam}};
            var result=Make(ctx,new RebarPath(new[]{P(50,50),P(3600,50)},false));
            Near(result.Centers.Max(p=>p.X),2600,"virtual edge400");
            Require(result.Centers.All(p=>p.X<=2600.001),"no incoming row continues into extension");
        });
        check("rows36: U top fixed, own legs may distribute above actual beam",()=>
        {
            var beam=Rect(0,-2000,960,2000);
            var top=BeamTopRebar.Create(new[]{Rect(0,0,960,1800)},new[]{beam},50);
            var ctx=new OffsetRebar.Plan{BeamTops=top,Supports=top.EffectiveSupports,OriginalSupports=new List<List<Point2d>>{beam}};
            var result=Make(ctx,top.Bars.ToArray());
            Require(result.FixedCenters.Count==2,"two U tops");
            Require(result.Centers.Any(p=>Math.Abs(p.Y-400)<.001),"actual beam400");
            Require(result.Centers.All(p=>p.Y>=400-.001),"outside crossed support");
        });
        check("rows36: fixed short curb corners preserved even below400, conflict explicit",()=>
        {
            var beam=Rect(0,-2000,960,2000);
            var top=BeamTopRebar.Create(new[]{Rect(0,0,960,400)},new[]{beam},50);
            var ctx=new OffsetRebar.Plan{BeamTops=top,Supports=top.EffectiveSupports,OriginalSupports=new List<List<Point2d>>{beam}};
            var result=Make(ctx,top.Bars.ToArray());
            Require(result.FixedCenters.Count==2 && result.Centers.Count==2,"fixed only");
            Require(result.Warnings.Any(s=>s.Contains("不足400")),"explicit fixed conflict");
        });
        check("rows36: collision does not silently leave an over800 hole",()=>
        {
            var ctx=new OffsetRebar.Plan{Boundaries=new List<List<Point2d>>{Rect(0,0,3000,1000)}};
            var corners=new CornerPointRebar.Result();corners.Centers.Add(P(117.5,117.5));
            var bars=new[]{new RebarPath(new[]{P(50,50),P(2950,50)},false),new RebarPath(new[]{P(2555.736,0),P(2555.736,500)},false)};
            var result=DistributedPointRebar.Create(ctx,bars,corners);
            Require(result.Warnings.Count>0,"collision warning");
            Require(result.Centers.Contains(P(117.5,117.5)),"fixed retained");
            Require(result.Rows.All(row=>row.Zip(row.Skip(1),(a,b)=>a.GetDistanceTo(b)).All(d=>d<=800.00001)),"only valid rows recorded");
        });
        check("rows36: selection order and polyline reversal preserve point set",()=>
        {
            var ctx=new OffsetRebar.Plan{Boundaries=new List<List<Point2d>>{Rect(0,0,3000,1000)}};
            var ring=Rect(50,50,2900,900);
            var first=Make(ctx,new RebarPath(ring,true));ring.Reverse();
            var reverse=Make(ctx,new RebarPath(ring,true));
            Require(first.Centers.Count==reverse.Centers.Count && first.Centers.All(p=>reverse.Centers.Any(q=>p.GetDistanceTo(q)<.001)),"same geometry");
        });
    }
}
