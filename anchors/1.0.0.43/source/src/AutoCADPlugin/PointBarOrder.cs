using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Geometry;

namespace AutoCADPlugin;

// Calculation copies only: normalize traversal so collision priority does not
// depend on selection order, open-path direction, or a closed path's start.
internal static class PointBarOrder
{
    public static IEnumerable<RebarPath> Sort(IReadOnlyList<RebarPath> bars) => bars.Select(Normalize)
        .OrderBy(b => b.Points.Min(p => p.X)).ThenBy(b => b.Points.Min(p => p.Y))
        .ThenBy(b => b.Points.Max(p => p.X)).ThenBy(b => b.Points.Max(p => p.Y))
        .ThenBy(b => b.Points, PointListComparer.Instance).ThenBy(b => b.IsClosed);

    private static RebarPath Normalize(RebarPath bar)
    {
        IReadOnlyList<Point2d> best = bar.Points;
        var count = bar.Points.Count;
        for (var start = 0; start < (bar.IsClosed ? count : 1); start++)
        {
            foreach (var direction in new[] { 1, -1 })
            {
                var candidate = Enumerable.Range(0, count).Select(i => bar.Points[bar.IsClosed
                    ? (start + direction * i + count) % count : direction == 1 ? i : count - 1 - i]).ToList();
                if (PointListComparer.Instance.Compare(candidate, best) < 0) { best = candidate; }
            }
        }
        // Distribution needs geometry only; it never writes these copies back.
        return new RebarPath(best, bar.IsClosed);
    }

    private sealed class PointListComparer : IComparer<IReadOnlyList<Point2d>>
    {
        public static readonly PointListComparer Instance = new PointListComparer();
        public int Compare(IReadOnlyList<Point2d>? first, IReadOnlyList<Point2d>? second)
        {
            if (ReferenceEquals(first, second)) { return 0; }
            if (first == null) { return -1; }
            if (second == null) { return 1; }
            for (var i = 0; i < System.Math.Min(first.Count, second.Count); i++)
            {
                var comparison = first[i].X.CompareTo(second[i].X);
                if (comparison == 0) { comparison = first[i].Y.CompareTo(second[i].Y); }
                if (comparison != 0) { return comparison; }
            }
            return first.Count.CompareTo(second.Count);
        }
    }
}
