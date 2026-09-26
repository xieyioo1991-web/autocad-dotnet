using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Geometry;
using AutoCADPlugin;
using Box = AutoCADPlugin.SupportLabelLayout.Box;

[assembly: CommandClass(typeof(TitleChecks))]
public sealed class TitleChecks
{
    [CommandMethod("SD_CHECK41_TITLE")]
    public void Run()
    {
        var root = Environment.GetEnvironmentVariable("SD_CHECK41_ROOT")!;
        var lines = new List<string>();
        try
        {
            var db = Application.DocumentManager.MdiActiveDocument.Database;
            int count;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var space = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
                count = space.Cast<ObjectId>().Count();
                var reference = DetailReference.Load(db, tr);
                var title = DetailTitle.Write(db, tr, reference, space, new Box(0, 0, 10000, 6000), new List<Box>(), "0");
                Require(title.Count == 4, "title group contains four native entities", lines);
                Require(title.OfType<DBText>().Any(t => t.TextString == "檐口大样图" && Math.Abs(t.Height - 500) < .1), "literal title and height", lines);
                Require(title.OfType<Polyline>().Any(p => Math.Abs(p.ConstantWidth - 70) < .1), "underline width", lines);
                var block = title.OfType<BlockReference>().Single();
                var attr = (AttributeReference)tr.GetObject(block.AttributeCollection.Cast<ObjectId>().Single(), OpenMode.ForRead);
                Require(attr.TextString == "1" && Math.Abs(attr.Height - 840) < .1, "index 1 and actual height", lines);
                Require(attr.Position.DistanceTo(block.Position) < 1000, "attribute moves with title circle", lines);
                Require(DetailTitle.Bounds(title).Top <= -DetailTitle.AxisReserve + .1, "axis number space reserved", lines);
                Require(reference.Dimension.Dimlfac == .25 && reference.Dimension.Dimscale == 1, "reference dimension scale retained", lines);
                // Do not commit: verify all geometry and definitions roll back.
            }
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var space = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                Require(count == space.Cast<ObjectId>().Count(), "model space rollback", lines);
                var blocks = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                Require(!blocks.Has("SD_REFERENCE_41"), "native import rollback", lines);
            }
            lines.Add("PASS");
        }
        catch (System.Exception e) { lines.Add(e.ToString()); }
        File.WriteAllLines(Path.Combine(root, "title-checks.txt"), lines);
    }
    private static void Require(bool value, string name, List<string> output)
    { if (!value) { throw new InvalidOperationException(name); } output.Add("OK " + name); }
}
