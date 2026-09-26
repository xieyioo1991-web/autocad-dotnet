using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AutoCADPlugin;
using Box = AutoCADPlugin.SupportLabelLayout.Box;

[assembly: CommandClass(typeof(TitleClearanceChecks))]
public sealed class TitleClearanceChecks
{
    [CommandMethod("SD_CHECK43_CLEARANCE")]
    public void Run()
    {
        var log = new List<string>();
        var db = Application.DocumentManager.MdiActiveDocument.Database;
        void Check(string name, Action test)
        { try { test(); log.Add("PASS " + name); } catch(System.Exception e) { log.Add("FAIL " + name + ": " + e); } }
        Check("sloping line bounding rectangle must not fill empty paper", () => Case(db, new Line(new Point3d(-20000,-15000,0),new Point3d(20000,15000,0)), false, false));
        Check("long vertical axis shifts title sideways without changing axis", () => Case(db, new Line(new Point3d(5000,-15000,0),new Point3d(5000,6000,0)), true, false));
        Check("hidden filled object does not block title", () => Case(db, Filled(false), false, false));
        Check("real filled obstacle still rejects placement and rolls back", () => Case(db, Filled(true), false, true));
        Check("large drawing coordinates retain title contents and clearance", () => LargeCoordinates(db));
        File.WriteAllLines(Path.Combine(Environment.GetEnvironmentVariable("SD_CHECK43_ROOT")!,"clearance-checks.txt"),log);
    }

    private static Solid Filled(bool visible) => new Solid(new Point3d(-20000,-20000,0),new Point3d(30000,-20000,0),
        new Point3d(-20000,0,0),new Point3d(30000,0,0)) { Visible=visible };

    private static void Case(Database db, Entity obstacle, bool shifted, bool reject)
    {
        using(obstacle)
        {
            int before;
            using(var tr=db.TransactionManager.StartTransaction())
            {
                var space=(BlockTableRecord)tr.GetObject(db.CurrentSpaceId,OpenMode.ForWrite);
                before=space.Cast<ObjectId>().Count();
                obstacle.SetDatabaseDefaults(db);space.AppendEntity(obstacle);tr.AddNewlyCreatedDBObject(obstacle,true);
                var geometry=obstacle.GeometricExtents.ToString();
                var reference=DetailReference.Load(db,tr);
                var threw=false;
                try
                {
                    var title=DetailTitle.Write(db,tr,reference,space,new Box(0,0,10000,6000),new[] {obstacle},"0");
                    var bounds=DetailTitle.Bounds(title);
                    Require(bounds.Top<=-1800+.1,"axis space reserved");
                    if(shifted) { Require(!bounds.Expand(100).Contains(new Point2d(5000,(bounds.Top+bounds.Bottom)/2)),"title still crosses vertical line"); }
                    else if(!reject) { Require(Math.Abs((bounds.Left+bounds.Right)/2-5000)<.1 && Math.Abs(bounds.Top+1800)<.1,"empty first choice moved unnecessarily"); }
                    Require(title.OfType<DBText>().Any(t=>t.TextString=="檐口大样图"&&t.Height==500),"title text or style changed");
                    Require(obstacle.GeometricExtents.ToString()==geometry,"existing obstacle moved");
                }
                catch(InvalidOperationException e)
                {
                    if(!reject) { throw; }
                    Require(e.Message.Contains("实际图形占用")&&e.Message.Contains(obstacle.Handle.ToString()),"missing blocking object diagnostic");
                    threw=true;
                }
                Require(threw==reject,"wrong rejection outcome");
            }
            using(var tr=db.TransactionManager.StartTransaction())
            { Require(((BlockTableRecord)tr.GetObject(db.CurrentSpaceId,OpenMode.ForRead)).Cast<ObjectId>().Count()==before,"failed title not rolled back"); }
        }
    }

    private static void LargeCoordinates(Database db)
    {
        using var tr=db.TransactionManager.StartTransaction();
        var space=(BlockTableRecord)tr.GetObject(db.CurrentSpaceId,OpenMode.ForWrite);
        var reference=DetailReference.Load(db,tr);
        var title=DetailTitle.Write(db,tr,reference,space,new Box(18259826,-1150051,18269826,-1144051),Array.Empty<Entity>(),"0");
        var bounds=DetailTitle.Bounds(title);
        Require(Math.Abs((bounds.Left+bounds.Right)/2-18264826)<.1 && Math.Abs(bounds.Top-(-1151851))<.1,"large coordinate placement");
        var index=title.OfType<BlockReference>().Single();
        var attribute=(AttributeReference)tr.GetObject(index.AttributeCollection.Cast<ObjectId>().Single(),OpenMode.ForRead);
        Require(attribute.TextString=="1"&&attribute.Position.DistanceTo(index.Position)<1000,"number 1 moved away from circle");
    }
    private static void Require(bool condition,string message) { if(!condition) { throw new InvalidOperationException(message); } }
}
