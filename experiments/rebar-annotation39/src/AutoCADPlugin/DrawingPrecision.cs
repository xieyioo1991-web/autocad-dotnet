using System;

namespace AutoCADPlugin;

// Round dimensions for rule decisions only; keep coordinates unrounded.
internal static class DrawingPrecision
{
    public static double Dimension(double value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);
    public static bool Fits(double available, double required) => Dimension(available) >= Dimension(required);
    public static bool LessThan(double value, double limit) => Dimension(value) < Dimension(limit);
    public static bool GreaterThan(double value, double limit) => Dimension(value) > Dimension(limit);
}
