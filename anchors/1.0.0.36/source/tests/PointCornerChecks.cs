using System;
using System.Collections.Generic;
using System.Linq;
using AutoCADPlugin;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

internal static class PointCornerChecks
{
    private static Point2d P(double x, double y) => new Point2d(x, y);
    private static List<Point2d> Rect(double x, double y, double w, double h) =>
        new List<Point2d> { P(x,y), P(x+w,y), P(x+w,y+h), P(x,y+h) };
    private static void Require(bool ok, string message) { if (!ok) { throw new InvalidOperationException(message); } }
    private static OffsetRebar.Plan Context(List<Point2d> boundary) =>
        new OffsetRebar.Plan { Boundaries = new List<List<Point2d>> { boundary } };
    private static CornerPointRebar.Result Make(OffsetRebar.Plan context, params RebarPath[] bars) => CornerPointRebar.Create(context, bars);

    public static void Run(Action<string, Action> test)
    {
        test("dots native writer matches reference polyline and layer, repeat adds zero", () =>
        {
            using(var db=new Database(true,true))
            {
                var previous=HostApplicationServices.WorkingDatabase;
                HostApplicationServices.WorkingDatabase=db;
                try
                {
                    using(var tr=db.TransactionManager.StartTransaction())
                    {
                        Require(PointRebarWriter.Write(db,tr,new[]{P(117.5,117.5)})==1,"first write");
                        Require(PointRebarWriter.Write(db,tr,new[]{P(117.5,117.5)})==0,"repeat");
                        var ms=(BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db),OpenMode.ForRead);
                        var dots=ms.Cast<ObjectId>().Select(id=>tr.GetObject(id,OpenMode.ForRead)).OfType<Polyline>().ToList();
                        Require(dots.Count==1,"native entity count");
                        var dot=dots[0];
                        Require(dot.Layer=="S-REIN-POINT" && dot.Closed && dot.NumberOfVertices==2,"native type");
                        Require(dot.ColorIndex==256 && dot.Linetype=="ByLayer" && dot.LineWeight==LineWeight.ByLayer,"entity attributes");
                        Require(dot.GetBulgeAt(0)==1 && dot.GetBulgeAt(1)==1 && dot.ConstantWidth==50,"two semicircles solid width");
                        Require(Math.Abs(dot.GetPoint2dAt(0).GetDistanceTo(dot.GetPoint2dAt(1))+dot.ConstantWidth-100)<1e-6,"outer diameter100");
                        var layer=(LayerTableRecord)tr.GetObject(dot.LayerId,OpenMode.ForRead);
                        Require(layer.Color.ColorIndex==30 && layer.LinetypeObjectId==db.ContinuousLinetype && layer.LineWeight==LineWeight.ByLineWeightDefault,"reference layer");
                        tr.Commit();
                    }
                }
                finally {HostApplicationServices.WorkingDatabase=previous;}
            }
        });
        test("dots rectangle: four circles touch inner width35 edges, exact centers117.5", () =>
        {
            var result = Make(Context(Rect(0,0,1000,600)), new RebarPath(Rect(50,50,900,500), true));
            Require(result.Centers.Count == 4, "four corners");
            foreach (var x in new[] {117.5,882.5}) foreach (var y in new[] {117.5,482.5})
                Require(result.Centers.Any(c => c.GetDistanceTo(P(x,y)) < 1e-6), "independent expected coordinate");
        });
        test("dots reversed winding preserves the inside", () =>
        {
            var ring=Rect(50,50,900,500);ring.Reverse();
            Require(Make(Context(Rect(0,0,1000,600)),new RebarPath(ring,true)).Centers.Count==4,"reverse");
        });
        test("dots acute and obtuse corners tangent to finite segments", () =>
        {
            foreach(var angle in new[]{Math.PI/3,Math.PI*2/3})
            {
                var a=P(1000,0);var b=P(1000*Math.Cos(angle),1000*Math.Sin(angle));
                Require(CornerPointRebar.TryTangent(P(0,0),a,b,out var center),"candidate");
                Require(Math.Abs(center.Y-67.5)<1e-6,"horizontal tangency");
                Require(Math.Abs(Math.Abs(center.X*Math.Sin(angle)-center.Y*Math.Cos(angle))-67.5)<1e-6,"inclined tangency");
            }
        });
        test("dots concave L excludes the reentrant corner", () =>
        {
            var material=new List<Point2d>{P(0,0),P(1000,0),P(1000,400),P(400,400),P(400,1000),P(0,1000)};
            var bar=new List<Point2d>{P(50,50),P(950,50),P(950,350),P(350,350),P(350,950),P(50,950)};
            var r=Make(Context(material),new RebarPath(bar,true));
            Require(r.Centers.Count==5,"only five convex corners");
            Require(!r.Centers.Any(c=>c.GetDistanceTo(P(417.5,417.5))<1),"no concave dot");
        });
        test("dots short legs do not use imaginary extended tangency", () =>
        {
            Require(!CornerPointRebar.TryTangent(P(0,0),P(50,0),P(0,500),out _),"short edge rejected");
        });
        test("dots collinear subdivisions do not lose corner", () =>
        {
            var path=new RebarPath(new[]{P(950,50),P(90,50),P(50,50),P(50,80),P(50,550)},false);
            var r=Make(Context(Rect(0,0,1000,600)),path);
            Require(r.Centers.Count==1 && r.Centers[0].GetDistanceTo(P(117.5,117.5))<1e-6,"merged collinear");
        });
        test("dots separate bars with common endpoint form a corner", () =>
        {
            var r=Make(Context(Rect(0,0,1000,600)),new RebarPath(new[]{P(950,50),P(50,50)},false),
                new RebarPath(new[]{P(50,50),P(50,550)},false));
            Require(r.Centers.Count==1,"shared endpoint");
        });
        test("dots support exclusion tests disc edge, not just center", () =>
        {
            var support=Rect(150,0,200,400);
            Require(CornerPointRebar.IntersectsSupport(P(117.5,117.5),support),"edge crosses even though center outside");
            Require(!CornerPointRebar.IntersectsSupport(P(100,117.5),support),"external tangency allowed");
            Require(CornerPointRebar.IntersectsSupport(P(200,200),support),"center inside rejected");
            var ctx=Context(Rect(0,0,1000,600));ctx.OriginalSupports.Add(support);
            var r=Make(ctx,new RebarPath(Rect(50,50,900,500),true));
            Require(r.Centers.Count==3,"one corner excluded by support edge");
        });
        test("dots third longitudinal bar blocks old points and supplies actual junction corners", () =>
        {
            var r=Make(Context(Rect(0,0,1000,600)),new RebarPath(Rect(50,50,900,500),true),
                new RebarPath(new[]{P(160,50),P(160,550)},false));
            Require(r.Centers.Count==4,"two right corners and two new T junction corners");
            Require(!r.Centers.Any(p=>Math.Abs(p.X-117.5)<1e-6),"colliding original left corners remain blocked");
            Require(r.Centers.Count(p=>Math.Abs(p.X-227.5)<1e-6)==2,"new T corners on longitudinal geometry");
        });
        test("dots close candidate discs do not overlap", () =>
        {
            var r=Make(Context(Rect(0,0,300,300)),new RebarPath(Rect(50,50,200,200),true));
            for(var i=0;i<r.Centers.Count;i++)for(var j=i+1;j<r.Centers.Count;j++)
                Require(r.Centers[i].GetDistanceTo(r.Centers[j])>=100-1e-6,"dot overlap");
        });
        test("dots beam extension U top allowed, actual beam forbidden", () =>
        {
            var material=new List<List<Point2d>>{Rect(0,0,800,600)};
            var supports=new List<List<Point2d>>{Rect(0,-2000,800,2000)};
            var tops=BeamTopRebar.Create(material,supports,50);
            Require(tops.Regions.Count==1,"U detected");
            var ctx=new OffsetRebar.Plan{BeamTops=tops,OriginalSupports=supports,Supports=tops.EffectiveSupports};
            var r=CornerPointRebar.Create(ctx,tops.Bars);
            Require(r.Centers.Count==2 && r.ExtensionCount==2,"U two points");
            Require(r.Centers.Any(c=>c.GetDistanceTo(P(117.5,482.5))<1e-6),"U top exact point");
            Require(r.Centers.All(c=>c.Y>=50),"disc above actual beam");
            var crowded=tops.Bars.Concat(new[]{new RebarPath(new[]{P(90,580),P(90,-500)},false),
                new RebarPath(new[]{P(710,580),P(710,-500)},false)}).ToList();
            var overlaps=CornerPointRebar.Create(ctx,crowded);
            Require(overlaps.Centers.Count==2 && overlaps.ExtensionCount==2,"U top points retained despite inner anchorage bars");
            Require(overlaps.Centers.Any(c=>c.GetDistanceTo(P(117.5,482.5))<1e-6),"U dot not moved to inner bar");
        });
    }
}
