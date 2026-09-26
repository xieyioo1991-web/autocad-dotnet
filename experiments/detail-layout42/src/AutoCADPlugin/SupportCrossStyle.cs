using System;
using Autodesk.AutoCAD.DatabaseServices;

namespace AutoCADPlugin;

internal static class SupportCrossStyle
{
    public const string LinetypeName = "SD-SUPPORT-DASH";

    public static ObjectId Ensure(Database database, Transaction transaction)
    {
        var table = (LinetypeTable)transaction.GetObject(database.LinetypeTableId, OpenMode.ForRead);
        if (table.Has(LinetypeName))
        {
            var existing = (LinetypeTableRecord)transaction.GetObject(table[LinetypeName], OpenMode.ForRead);
            if (existing.NumDashes != 2 || Math.Abs(existing.PatternLength - .35) > 1e-8 ||
                Math.Abs(existing.DashLengthAt(0) - .25) > 1e-8 || Math.Abs(existing.DashLengthAt(1) + .10) > 1e-8)
            { throw new InvalidOperationException("SD-SUPPORT-DASH线型与梁叉线定义冲突，请重命名该已有线型后重试。"); }
            return existing.ObjectId;
        }
        table.UpgradeOpen();
        using var record = new LinetypeTableRecord
        {
            Name = LinetypeName,
            AsciiDescription = "Beam support cross; reference __DASH pattern",
            PatternLength = .35,
            NumDashes = 2
        };
        record.SetDashLengthAt(0, .25);
        record.SetDashLengthAt(1, -.10);
        var id = table.Add(record);
        transaction.AddNewlyCreatedDBObject(record, true);
        return id;
    }

    // The reference drawing uses __DASH (.25,-.10) and LTSCALE=1000:
    // 250 drawing units on, 100 off. Compensate per entity instead of changing
    // the document-wide LTSCALE and unintentionally restyling other linework.
    public static double EntityScale(Database database) => 1000.0 / database.Ltscale;
}
