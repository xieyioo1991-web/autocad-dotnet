using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Geometry;
using AutoCADPlugin;

[assembly: CommandClass(typeof(Detail41Ownership))]
public sealed class Detail41Ownership
{
    [CommandMethod("SD_CHECK41_OWNERSHIP")]
    public void Run()
    {
        var root = Environment.GetEnvironmentVariable("SD_CHECK41_ROOT")!;
        var lines = new List<string>();
        try
        {
            var db = Application.DocumentManager.MdiActiveDocument.Database;
            string signature;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var all = Entities(db,tr); signature = Signature(all);
                DetailAnnotation.Write(db,tr,all);
                tr.Abort();
            }
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var original = Entities(db,tr);
                if (Signature(original) != signature) { throw new InvalidOperationException("full-update rollback modified drawing"); }
                lines.Add("PASS full annotation update rollback retains previous annotations and geometry");
                using var ids = new ObjectIdCollection(original.Select(e=>e.ObjectId).ToArray());
                using var mapping = new IdMapping(); db.DeepCloneObjects(ids,db.CurrentSpaceId,mapping,false);
                var copied = original.Select(e=>(Entity)tr.GetObject(mapping[e.ObjectId].Value,OpenMode.ForWrite)).ToList();
                foreach (var entity in copied) { entity.TransformBy(Matrix3d.Displacement(new Vector3d(100000,0,0))); }
                var supportIds = new HashSet<string>(copied.Where(e=>OutlineRole.Get(e)=="Support").Select(e=>e.Handle.ToString()));
                if (copied.Where(e=>DetailAnnotationIdentity.Get(e)!="").Any(e=>!supportIds.Contains(DetailAnnotationIdentity.Get(e))))
                { throw new InvalidOperationException("copied detail source handles not remapped"); }
                DetailAnnotation.Write(db,tr,copied);
                if (Signature(original) != signature) { throw new InvalidOperationException("updating copy changed original"); }
                lines.Add("PASS copied annotation owners remapped; updating copy leaves original unchanged");
                tr.Abort();
            }
        }
        catch (System.Exception e) { lines.Add("FAIL " + e); }
        File.WriteAllLines(Path.Combine(root,"ownership-checks.txt"),lines);
    }
    private static List<Entity> Entities(Database db,Transaction tr) => ((BlockTableRecord)tr.GetObject(db.CurrentSpaceId,OpenMode.ForRead)).Cast<ObjectId>().Select(id=>(Entity)tr.GetObject(id,OpenMode.ForRead)).ToList();
    private static string Signature(List<Entity> entities) => string.Join("|",entities.OrderBy(e=>e.Handle.Value).Select(e=>e.Handle+":"+e.GeometricExtents));
}
