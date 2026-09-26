using Autodesk.AutoCAD.DatabaseServices;

namespace AutoCADPlugin;

internal static class OutlineRole
{
    private const string ApplicationName = "SD_OUTLINE_ROLE";

    public static void Set(Entity entity, Transaction transaction, string role)
    {
        var database = entity.Database;
        var table = (RegAppTable)transaction.GetObject(database.RegAppTableId, OpenMode.ForRead);
        if (!table.Has(ApplicationName))
        {
            table.UpgradeOpen();
            var record = new RegAppTableRecord { Name = ApplicationName };
            table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }
        using var data = new ResultBuffer(new TypedValue(1001, ApplicationName), new TypedValue(1000, role));
        entity.XData = data;
    }

    public static string Get(Entity entity)
    {
        using var data = entity.GetXDataForApplication(ApplicationName);
        return data?.AsArray().Length >= 2 ? (string)data.AsArray()[1].Value : string.Empty;
    }
}
