using System;
using System.Collections.Generic;
using System.Linq;
using AutoCADPlugin;
using Autodesk.AutoCAD.Geometry;

internal static class PointJunctionChecks
{
    private static Point2d P(double x,double y)=>new Point2d(x,y);
    private static List<Point2d> Box()=>new List<Point2d>{P(0,0),P(1200,0),P(1200,1200),P(0,1200)};
    private static OffsetRebar.Plan Context()=>new OffsetRebar.Plan{Boundaries=new List<List<Point2d>>{Box()}};
    private static RebarPath Path(params Point2d[] points)=>new RebarPath(points,false);
    private static void Require(bool ok,string message){if(!ok){throw new InvalidOperationException(message);}}

    public static void Run(Action<string,Action> test)
    {
        test("points35 corner follows moved longitudinal vertex, not architectural corners",()=>
        {
            var r=CornerPointRebar.Create(Context(),new[]{Path(P(300,300),P(800,300),P(800,800))});
            Require(r.Centers.Count==1 && r.Centers[0].GetDistanceTo(P(732.5,367.5))<1e-6,"bar-relative location");
        });
        test("points35 closed longitudinal L rejects its own concavity inside rectangular architecture",()=>
        {
            var l=new[]{P(100,100),P(1100,100),P(1100,500),P(500,500),P(500,1100),P(100,1100)};
            var r=CornerPointRebar.Create(Context(),new[]{new RebarPath(l,true)});
            Require(r.Centers.Count==5,"five longitudinal convex corners only");
            Require(!r.Centers.Any(p=>p.GetDistanceTo(P(567.5,567.5))<1),"bar concavity excluded despite surrounding concrete");
        });
        test("points35 actual crossing without vertices supplies four finite corner sectors",()=>
        {
            var r=CornerPointRebar.Create(Context(),new[]{Path(P(100,500),P(900,500)),Path(P(500,100),P(500,900))});
            Require(r.Centers.Count==4,"four crossing sectors");
            foreach(var x in new[]{432.5,567.5})foreach(var y in new[]{432.5,567.5})
                Require(r.Centers.Any(p=>p.GetDistanceTo(P(x,y))<1e-6),"crossing exact center");
        });
        test("points35 T junction and unconnected supporting lines differ",()=>
        {
            var horizontal=Path(P(100,500),P(900,500));
            Require(CornerPointRebar.Create(Context(),new[]{horizontal,Path(P(500,500),P(500,900))}).Centers.Count==2,"T has two corners");
            Require(CornerPointRebar.Create(Context(),new[]{horizontal,Path(P(500,501),P(500,900))}).Centers.Count==0,"one unit gap not extended");
        });
        test("points35 inclined crossing with no shared endpoint",()=>
        {
            var r=CornerPointRebar.Create(Context(),new[]{Path(P(100,600),P(1100,600)),Path(P(200,200),P(1000,1000))});
            Require(r.Centers.Count==4,"inclined crossing sectors");
            foreach(var center in r.Centers)
            {
                Require(Math.Abs(Math.Abs(center.Y-600)-67.5)<1e-6,"horizontal tangency");
                Require(Math.Abs(Math.Abs(center.Y-center.X)/Math.Sqrt(2)-67.5)<1e-6,"diagonal tangency");
            }
        });
        test("points35 ordinary bend excluded even outside crossed support; body intersections remain",()=>
        {
            var context=Context();
            context.AnchorEnds.Add(new RebarAnchorage.EndResult(0,false,P(300,500),RebarAnchorage.AnchorKind.Bent,
                new[]{P(350,500),P(700,500),P(700,100)},0));
            var bar=Path(P(100,500),P(350,500),P(700,500),P(700,100));
            Require(CornerPointRebar.Create(context,new[]{bar}).Centers.Count==0,"bend excluded without using material exclusion");
            var r=CornerPointRebar.Create(context,new[]{bar,Path(P(200,100),P(200,900))});
            Require(r.Centers.Count==4 && r.ExcludedAnchorCount==1,"nonanchor body preserved");
        });
        test("points35 vertical bent anchorage contributes neither elbow nor crossing corners",()=>
        {
            var context=Context();
            context.AnchorEnds.Add(new RebarAnchorage.EndResult(0,false,P(200,800),RebarAnchorage.AnchorKind.VerticalBent,
                new[]{P(300,700),P(500,500),P(500,100)},0));
            var bar=Path(P(100,900),P(200,800),P(300,700),P(500,500),P(500,100));
            var crossing=Path(P(100,300),P(1000,300));
            var r=CornerPointRebar.Create(context,new[]{bar,crossing});
            Require(r.Centers.Count==0 && r.ExcludedAnchorCount==1,"no elbow or crossing from anchor");
            var reversed=Path(bar.Points.Reverse().ToArray());
            Require(CornerPointRebar.Create(context,new[]{reversed,crossing}).Centers.Count==0,"reversed path still excluded");
        });
        test("points35 straight anchorage does not erase ordinary longitudinal corners",()=>
        {
            var context=Context();
            context.AnchorEnds.Add(new RebarAnchorage.EndResult(0,false,P(300,500),RebarAnchorage.AnchorKind.Straight,
                new[]{P(350,500),P(1000,500)},0));
            var r=CornerPointRebar.Create(context,new[]{Path(P(100,500),P(1000,500)),Path(P(500,100),P(500,900))});
            Require(r.Centers.Count==4 && r.ExcludedAnchorCount==0,"only bent types excluded");
        });
    }
}
