using System;
using Autodesk.AutoCAD.DatabaseServices;

namespace AutoCADPlugin;

internal static class DetailAnnotationIdentity
{
    private const string App = "SD_DETAIL_ANNOTATION";
    public static string Get(Entity entity)
    {
        using var data = entity.GetXDataForApplication(App);
        var values = data?.AsArray();
        return values != null && values.Length >= 2 ? (string)values[1].Value : string.Empty;
    }

    public static void Set(Entity entity, Transaction tr, string source, string role)
    {
        var apps = (RegAppTable)tr.GetObject(entity.Database.RegAppTableId, OpenMode.ForRead);
        if (!apps.Has(App))
        {
            apps.UpgradeOpen(); using var record = new RegAppTableRecord { Name = App };
            apps.Add(record); tr.AddNewlyCreatedDBObject(record, true);
        }
        using var data = new ResultBuffer(new TypedValue(1001, App), new TypedValue(1005, source), new TypedValue(1000, role));
        entity.XData = data;
    }

    public static string Role(Entity entity)
    {
        using var data = entity.GetXDataForApplication(App);
        var values = data?.AsArray();
        return values != null && values.Length > 2 ? Convert.ToString(values[2].Value) ?? "" : "";
    }
}
