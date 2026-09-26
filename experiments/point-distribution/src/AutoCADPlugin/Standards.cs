using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;

namespace AutoCADPlugin;

internal static class Standards
{
    public const string RegionLayer = "SD-REGION";
    public const string ReinforcementLayer = "S-REIN";
    public const string PointReinforcementLayer = "S-REIN-POINT";
    public const string OtherThinLayer = "S-OTHER-THIN";
    public const string OtherConstructionLayer = "S-OTHER-XTLKX";
    public const string TextLayer = "S-TEXT";
    public const string DimensionLayer = "S-DIM";
    public const string AxisLayer = "S-AXIS-COLU";
    public const string ElevationLayer = "DIM_ELEV";
    public const double ScaleFactor = 4.0;
    public const double LongitudinalWidth = 35.0;
    public const double PointDiameter = 50.0;
    public const double DefaultPointSpacing = 150.0;

    public static void Ensure(Database database, Transaction transaction)
    {
        EnsureLayer(database, transaction, RegionLayer, 2);
        EnsureLayer(database, transaction, ReinforcementLayer, 6);
        EnsureLayer(database, transaction, PointReinforcementLayer, 30);
        EnsureLayer(database, transaction, OtherThinLayer, 3);
        EnsureLayer(database, transaction, OtherConstructionLayer, 150);
        EnsureLayer(database, transaction, TextLayer, 7);
        EnsureLayer(database, transaction, DimensionLayer, 3);
        EnsureLayer(database, transaction, AxisLayer, 253);
        EnsureLayer(database, transaction, ElevationLayer, 3);
    }

    private static void EnsureLayer(Database database, Transaction transaction, string name, short colorIndex)
    {
        var table = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
        if (table.Has(name)) return;
        table.UpgradeOpen();
        var record = new LayerTableRecord
        {
            Name = name,
            Color = Color.FromColorIndex(ColorMethod.ByAci, colorIndex),
            // AutoCAD does not allow ByLayer as a LayerTableRecord lineweight;
            // ByLayer is the valid value for entities assigned to this layer.
            LineWeight = LineWeight.LineWeight000
        };
        table.Add(record);
        transaction.AddNewlyCreatedDBObject(record, true);
    }
}
