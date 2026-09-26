namespace AutoCADPlugin;

internal static class SupportClassification
{
    public static bool IsFloorSlab(double width, double height) => width > 2 * height;

    public static string Label(double width, double height, string fallback)
    {
        if (height > 2 * width) { return "楼层梁"; }
        return IsFloorSlab(width, height) ? "楼层板" : fallback;
    }
}
