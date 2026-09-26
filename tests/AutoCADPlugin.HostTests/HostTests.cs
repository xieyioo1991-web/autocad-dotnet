using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Application = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(AutoCADPlugin.HostTests.HostTests))]
namespace AutoCADPlugin.HostTests;

public sealed class HostTests
{
    private static void Log(string value)
    {
        Application.DocumentManager.MdiActiveDocument.Editor.WriteMessage("\n" + value + "\n");
        File.AppendAllText(Environment.GetEnvironmentVariable("SD_HOST_REPORT")!, value + Environment.NewLine);
    }

    private static void Run(string name, Action action)
    {
        try { action(); Log("PASS " + name); }
        catch (System.Exception ex) { Log("FAIL " + name + Environment.NewLine + ex); }
    }

    private static object Invoke(string method)
    {
        var assembly = Assembly.LoadFrom(Environment.GetEnvironmentVariable("SD_HOST_PLUGIN")!);
        Log("Loaded: " + assembly.FullName + " at " + assembly.Location);
        var type = assembly.GetType("AutoCADPlugin.Commands", true)!;
        return type.GetMethod(method)!.Invoke(Activator.CreateInstance(type), null)!;
    }

    [CommandMethod("SD_TEST_PROBES")]
    public void Probes()
    {
        Run("legacy LayerTableRecord.ByLayer", () => { using var layer = new LayerTableRecord(); layer.LineWeight = LineWeight.ByLayer; });
        Run("legacy DBText.AlignmentPoint", () => { using var text = new DBText(); text.AlignmentPoint = Point3d.Origin; });
        Run("PromptCornerOptions.UseDashedLine", () => { var options = new PromptCornerOptions("\nCorner: ", Point3d.Origin) { UseDashedLine = true }; });
    }

    [CommandMethod("SD_TEST_SETUP")]
    public void Setup() => Run("SetupStandards", () => Invoke("SetupStandards"));

    [CommandMethod("SD_TEST_PREPARE")]
    public void Prepare() => Run("Prepare support fixtures", () =>
    {
        var db = Application.DocumentManager.MdiActiveDocument.Database;
        using var tr = db.TransactionManager.StartTransaction();
        var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (!layers.Has("SD-REGION"))
        {
            layers.UpgradeOpen();
            var layer = new LayerTableRecord { Name = "SD-REGION" };
            layers.Add(layer); tr.AddNewlyCreatedDBObject(layer, true);
        }
        var space = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
        foreach (var id in space.Cast<ObjectId>().ToArray())
        {
            var entity = (Entity)tr.GetObject(id, OpenMode.ForRead);
            if (entity.Layer == "SD-REGION") { entity.UpgradeOpen(); entity.Erase(); }
        }
        AddRectangle(space, tr, 18259826.14, -1150871.2, 18260066.14, -1150321.2);
        AddRectangle(space, tr, 18260066.14, -1150441.2, 18260841.27, -1150321.2);
        tr.Commit();
    });

    [CommandMethod("SD_TEST_RENAME_ARCH_LAYERS")]
    public void RenameArchitecturalLayers() => Run("Rename architectural outline layers", () =>
    {
        var db = Application.DocumentManager.MdiActiveDocument.Database;
        using var tr = db.TransactionManager.StartTransaction();
        var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForWrite);
        RenameLayer(layers, tr, "WALL", "A-WALL");
        RenameLayer(layers, tr, "COLUMN", "A-COLUMN");
        tr.Commit();
    });

    private static void RenameLayer(LayerTable layers, Transaction tr, string source, string target)
    {
        if (!layers.Has(source)) return;
        if (layers.Has(target)) return;
        var layer = (LayerTableRecord)tr.GetObject(layers[source], OpenMode.ForWrite);
        layer.Name = target;
    }

    private static void AddRectangle(BlockTableRecord space, Transaction tr, double x1, double y1, double x2, double y2)
    {
        var poly = new Polyline();
        poly.AddVertexAt(0, new Point2d(x1, y1), 0, 0, 0);
        poly.AddVertexAt(1, new Point2d(x2, y1), 0, 0, 0);
        poly.AddVertexAt(2, new Point2d(x2, y2), 0, 0, 0);
        poly.AddVertexAt(3, new Point2d(x1, y2), 0, 0, 0);
        poly.Closed = true; poly.Layer = "SD-REGION";
        space.AppendEntity(poly); tr.AddNewlyCreatedDBObject(poly, true);
    }

    [CommandMethod("SD_TEST_DETAIL")]
    public void Detail() => Run("AutoDetail full command", () => Invoke("AutoDetail"));

    [CommandMethod("SD_TEST_INSPECT")]
    public void Inspect() => Run("Inspect output", () =>
    {
        var db = Application.DocumentManager.MdiActiveDocument.Database;
        using var tr = db.TransactionManager.StartTransaction();
        var space = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
        var entities = space.Cast<ObjectId>().Select(id => (Entity)tr.GetObject(id, OpenMode.ForRead)).ToArray();
        foreach (var group in entities.GroupBy(e => e.Layer)) Log(group.Key + ": " + group.Count());
        var supports = entities.OfType<Polyline>().Where(p => p.Layer == "S-OTHER-THIN" && p.Closed).ToArray();
        if (supports.Length != 2) throw new InvalidOperationException("Expected 2 copied SD-REGION boundaries; got " + supports.Length);
        foreach (var support in supports) Log("Support at " + support.GetPoint3dAt(0));
        if (entities.Any(e => e.Layer == "S-REIN" || e.Layer == "S-REIN-POINT")) throw new InvalidOperationException("Rebar was created during outline-only mode.");
        if (entities.OfType<DBText>().Count(t => t.TextString == "楼层梁") != 2) throw new InvalidOperationException("Missing support labels.");
    });

    [CommandMethod("SD_TEST_HATCH_INFO")]
    public void HatchInfo() => Run("Inspect hatch patterns", () =>
    {
        var db = Application.DocumentManager.MdiActiveDocument.Database;
        using var tr = db.TransactionManager.StartTransaction();
        var space = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
        foreach (var id in space.Cast<ObjectId>())
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not Hatch hatch) continue;
            var extents = hatch.GeometricExtents;
            var area = 0.0;
            try { area = hatch.Area; } catch { }
            Log($"HATCH {hatch.Handle} pattern={hatch.PatternName} area={area:0.##} extents=({extents.MinPoint.X:0.##},{extents.MinPoint.Y:0.##})-({extents.MaxPoint.X:0.##},{extents.MaxPoint.Y:0.##}) loops={hatch.NumberOfLoops}");
            for (var loopIndex = 0; loopIndex < hatch.NumberOfLoops; loopIndex++)
            {
                var loop = hatch.GetLoopAt(loopIndex);
                if (!loop.IsPolyline)
                {
                    Log("  loop non-polyline curves=" + loop.Curves.Count);
                    foreach (var curve in loop.Curves) Log("    curve=" + curve.GetType().FullName);
                    continue;
                }
                Log("  loop vertices=" + string.Join(";", loop.Polyline.Cast<BulgeVertex>().Select(v => $"{v.Vertex.X:0.##},{v.Vertex.Y:0.##}")));
            }
        }
        tr.Commit();
    });

}
