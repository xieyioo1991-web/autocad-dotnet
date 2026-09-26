using Autodesk.AutoCAD.DatabaseServices;

namespace AutoCADPlugin;

internal static class AnnotationIdentity
{
    private const string ApplicationName = "SD_ANNOTATION";
    public static string Get(Entity entity)
    {
        using var data = entity.GetXDataForApplication(ApplicationName);
        var values = data?.AsArray();
        return values != null && values.Length >= 2 ? (string)values[1].Value : string.Empty;
    }

    public static void Set(Entity entity, Transaction transaction, string sourceHandle)
    {
        var table = (RegAppTable)transaction.GetObject(entity.Database.RegAppTableId, OpenMode.ForRead);
        if (!table.Has(ApplicationName))
        {
            table.UpgradeOpen();
            using var record = new RegAppTableRecord { Name = ApplicationName };
            table.Add(record); transaction.AddNewlyCreatedDBObject(record, true);
        }
        // Native XData handle type lets AutoCAD remap ownership when the whole
        // detail (bars and labels together) is deep-cloned or inserted elsewhere.
        using var data = new ResultBuffer(new TypedValue(1001, ApplicationName), new TypedValue(1005, sourceHandle));
        entity.XData = data;
    }
}
