using System.Collections.Generic;
using System.Linq;
using System;
using Autodesk.AutoCAD.Geometry;

namespace AutoCADPlugin;

internal sealed class RebarPath
{
    public RebarPath(IEnumerable<Point2d> points, bool isClosed,
        IEnumerable<SupportContact>? startContacts = null, IEnumerable<SupportContact>? endContacts = null)
    {
        Points = points.ToList().AsReadOnly();
        IsClosed = isClosed;
        StartContacts = (startContacts ?? Array.Empty<SupportContact>()).ToList().AsReadOnly();
        EndContacts = (endContacts ?? Array.Empty<SupportContact>()).ToList().AsReadOnly();
    }

    public IReadOnlyList<Point2d> Points { get; }
    public bool IsClosed { get; }
    public IReadOnlyList<SupportContact> StartContacts { get; }
    public IReadOnlyList<SupportContact> EndContacts { get; }
}
