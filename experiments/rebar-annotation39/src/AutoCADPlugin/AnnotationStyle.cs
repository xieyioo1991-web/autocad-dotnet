using System;
using System.IO;
using System.Reflection;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;

namespace AutoCADPlugin;

// Recorded from 实例1：结构大样(最终完成目标).dwg, steel labels 153/155/159
// and their leaders 151/152/154/156/157/158. See reference-annotation-style.txt.
internal static class AnnotationStyle
{
    public const double WidthFactor = .7;
    public const string StyleName = "TSSD_Label";

    public static ObjectId Ensure(Database database, Transaction transaction)
    {
        var directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
        var font = FontPath(directory, "tssdeng.shx");
        var bigFont = FontPath(directory, "hztxt.shx");
        Standards.Ensure(database, transaction);
        SetLayer(database, transaction, Standards.TextLayer, 7);
        SetLayer(database, transaction, Standards.OtherThinLayer, 3);
        var table = (TextStyleTable)transaction.GetObject(database.TextStyleTableId, OpenMode.ForRead);
        var name = StyleName;
        if (table.Has(name) && !Compatible((TextStyleTableRecord)transaction.GetObject(table[name], OpenMode.ForRead)))
        { name = StyleName + "_SD39"; }
        TextStyleTableRecord record;
        if (table.Has(name))
        {
            record = (TextStyleTableRecord)transaction.GetObject(table[name], OpenMode.ForRead);
            if (!Compatible(record)) { throw new InvalidOperationException("同名标注样式与实例1冲突，请先重命名 TSSD_Label_SD39 后重试。"); }
            record.UpgradeOpen();
        }
        else
        {
            table.UpgradeOpen();
            // After AddNewlyCreatedDBObject, ownership belongs to the caller's transaction.
            record = new TextStyleTableRecord { Name = name };
            table.Add(record); transaction.AddNewlyCreatedDBObject(record, true);
        }
        record.FileName = font; record.BigFontFileName = bigFont;
        record.TextSize = 0; record.XScale = WidthFactor; record.ObliquingAngle = 0;
        record.IsVertical = false; record.FlagBits = 0;
        return record.ObjectId;
    }

    private static bool Compatible(TextStyleTableRecord record) =>
        string.Equals(Path.GetFileName(record.FileName), "tssdeng.shx", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Path.GetFileName(record.BigFontFileName), "hztxt.shx", StringComparison.OrdinalIgnoreCase) &&
        Math.Abs(record.XScale - WidthFactor) < 1e-6 && Math.Abs(record.ObliquingAngle) < 1e-6 && !record.IsVertical;

    private static string FontPath(string directory, string fileName)
    {
        var path = Path.Combine(directory, "Fonts", fileName);
        if (!File.Exists(path))
        { throw new InvalidOperationException($"缺少实例1标注字体 {fileName}。请保留39版DLL同目录的Fonts文件夹，不能只复制DLL。"); }
        return path;
    }

    private static void SetLayer(Database database, Transaction transaction, string name, short color)
    {
        var layers = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
        var layer = (LayerTableRecord)transaction.GetObject(layers[name], OpenMode.ForWrite);
        var types = (LinetypeTable)transaction.GetObject(database.LinetypeTableId, OpenMode.ForRead);
        layer.Color = Color.FromColorIndex(ColorMethod.ByAci, color);
        layer.LinetypeObjectId = types["Continuous"];
        layer.LineWeight = LineWeight.ByLineWeightDefault; layer.IsPlottable = true;
    }

    public static void ApplyText(DBText text, Database database, ObjectId style, string content)
    {
        text.SetDatabaseDefaults(database); text.TextStyleId = style;
        text.Layer = Standards.TextLayer; text.ColorIndex = 256; text.Linetype = "ByLayer";
        text.LineWeight = LineWeight.ByLayer; text.LinetypeScale = 1;
        text.TextString = content; text.Height = AnnotationLayout.TextHeight;
        text.WidthFactor = WidthFactor; text.Oblique = 0; text.Rotation = 0;
    }
}
