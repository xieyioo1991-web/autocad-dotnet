using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AutoCADPlugin;

// The reference file is a packaged COPY. Never open the user's source DWG here.
// Native cloning retains the title block's attributes and the real dim style.
internal sealed class DetailReference
{
    private const string BlockName = "SD_REFERENCE_41";
    private readonly List<Entity> prototypes;
    private DetailReference(List<Entity> entities) { prototypes = entities; }
    public RotatedDimension Dimension => prototypes.OfType<RotatedDimension>().Single();

    public static DetailReference Load(Database db, Transaction tr)
    {
        var blocks = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        ObjectId blockId;
        if (blocks.Has(BlockName)) { blockId = blocks[BlockName]; }
        else
        {
            var directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
            var path = Path.Combine(directory, "ReferenceStyle.dwg");
            if (!File.Exists(path)) { throw new InvalidOperationException("请保留DLL旁的ReferenceStyle.dwg样式文件及Fonts目录。"); }
            using var source = new Database(false, true);
            source.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, "");
            source.CloseInput(true);
            using var sourceTr = source.TransactionManager.StartTransaction();
            // Isolate conflicting text/dimension/block definitions, without changing
            // existing drawing objects which may already use the canonical names.
            RenameConflicts(source, sourceTr, db, tr);
            var space = (BlockTableRecord)sourceTr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(source), OpenMode.ForRead);
            var entities = space.Cast<ObjectId>().Select(id => (Entity)sourceTr.GetObject(id, OpenMode.ForRead)).ToList();
            var dimension = entities.OfType<RotatedDimension>().First();
            var selected = entities.Where(e => e.Layer == "S-TEXT-2" || e.Layer == "S-TEXT-3" ||
                e.Layer == Standards.ElevationLayer || e.Layer == Standards.AxisLayer ||
                e is BlockReference b && b.Name.StartsWith("ts_idx1", StringComparison.Ordinal)).Select(e => e.ObjectId).ToList();
            selected.Add(dimension.ObjectId);
            blocks.UpgradeOpen();
            using var block = new BlockTableRecord { Name = BlockName };
            blockId = blocks.Add(block); tr.AddNewlyCreatedDBObject(block, true);
            using var ids = new ObjectIdCollection(selected.ToArray());
            using var map = new IdMapping();
            source.WblockCloneObjects(ids, blockId, map, DuplicateRecordCloning.Ignore, false);
        }
        var definition = (BlockTableRecord)tr.GetObject(blockId, OpenMode.ForRead);
        var result = new DetailReference(definition.Cast<ObjectId>().Select(id => (Entity)tr.GetObject(id, OpenMode.ForRead)).ToList());
        ResolveFonts(result.prototypes, tr);
        return result;
    }

    private static void RenameConflicts(Database source, Transaction sourceTr, Database target, Transaction targetTr)
    {
        foreach (var pair in new[] { Tuple.Create(source.TextStyleTableId, target.TextStyleTableId),
            Tuple.Create(source.DimStyleTableId, target.DimStyleTableId), Tuple.Create(source.BlockTableId, target.BlockTableId) })
        {
            var table = (SymbolTable)sourceTr.GetObject(pair.Item1, OpenMode.ForRead);
            var targetTable = (SymbolTable)targetTr.GetObject(pair.Item2, OpenMode.ForRead);
            foreach (ObjectId id in table)
            {
                var record = (SymbolTableRecord)sourceTr.GetObject(id, OpenMode.ForRead);
                if (!targetTable.Has(record.Name) || record.Name.StartsWith("*", StringComparison.Ordinal) ||
                    record.Name.Equals("Standard", StringComparison.OrdinalIgnoreCase)) { continue; }
                var stem = record.Name + "_SD41";
                var name = stem;
                var suffix = 1;
                while (targetTable.Has(name) || table.Has(name)) { name = stem + "_" + suffix++; }
                record.UpgradeOpen(); record.Name = name;
            }
        }
    }

    private static void ResolveFonts(IEnumerable<Entity> prototypes, Transaction tr)
    {
        var root = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "", "Fonts");
        var styles = new HashSet<ObjectId>();
        foreach (var entity in prototypes)
        {
            if (entity is DBText text) { styles.Add(text.TextStyleId); }
            if (entity is Dimension dimension)
            { using var effective = dimension.GetDimstyleData(); styles.Add(effective.Dimtxsty); }
            if (entity is BlockReference block)
            {
                foreach (ObjectId id in block.AttributeCollection)
                { styles.Add(((AttributeReference)tr.GetObject(id, OpenMode.ForRead)).TextStyleId); }
            }
        }
        foreach (var id in styles)
        {
            var style = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
            var font = Path.GetFileName(style.FileName); var big = Path.GetFileName(style.BigFontFileName);
            if (font.Equals("tssdeng.shx", StringComparison.OrdinalIgnoreCase))
            {
                var path = Path.Combine(root, font);
                if (!File.Exists(path)) { throw new InvalidOperationException("缺少字体：" + path); }
                style.UpgradeOpen(); style.FileName = path;
                if (!string.IsNullOrEmpty(big))
                {
                    var bigPath = Path.Combine(root, big);
                    if (!File.Exists(bigPath)) { throw new InvalidOperationException("缺少字体：" + bigPath); }
                    style.BigFontFileName = bigPath;
                }
            }
        }
    }

    public List<Entity> CloneTitle(Database db, Transaction tr, ObjectId owner)
    {
        var title = prototypes.Where(e => e.Layer == "S-TEXT-2" || e.Layer == "S-TEXT-3" || e is BlockReference).ToList();
        using var ids = new ObjectIdCollection(title.Select(e => e.ObjectId).ToArray());
        using var mapping = new IdMapping();
        db.DeepCloneObjects(ids, owner, mapping, false);
        return title.Select(e => (Entity)tr.GetObject(mapping[e.ObjectId].Value, OpenMode.ForWrite)).ToList();
    }

    public List<Entity> Elevation => prototypes.Where(e => e.Layer == Standards.ElevationLayer).ToList();
    public Line Axis => prototypes.OfType<Line>().Single(e => e.Layer == Standards.AxisLayer);
}
