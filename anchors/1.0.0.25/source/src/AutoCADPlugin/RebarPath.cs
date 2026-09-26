using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Geometry;

namespace AutoCADPlugin;

internal sealed class RebarPath
{
    public RebarPath(IEnumerable<Point2d> points, bool isClosed)
    {
        Points = points.ToList().AsReadOnly();
        IsClosed = isClosed;
    }

    public IReadOnlyList<Point2d> Points { get; }
    public bool IsClosed { get; }
}
