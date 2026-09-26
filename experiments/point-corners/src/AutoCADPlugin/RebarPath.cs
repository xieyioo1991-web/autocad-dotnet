using System.Collections.Generic;
using System.Linq;
using System;
using Autodesk.AutoCAD.Geometry;

namespace AutoCADPlugin;

internal enum RebarEndRule { Default, UpperBend, LowerStraight, SmallStraight, Free }

internal sealed class RebarPath
{
    public RebarPath(IEnumerable<Point2d> points, bool isClosed,
        IEnumerable<SupportContact>? startContacts = null, IEnumerable<SupportContact>? endContacts = null,
        int regionIndex = -1, RebarEndRule startRule = RebarEndRule.Default, RebarEndRule endRule = RebarEndRule.Default)
    {
        Points = points.ToList().AsReadOnly();
        IsClosed = isClosed;
        StartContacts = (startContacts ?? Array.Empty<SupportContact>()).ToList().AsReadOnly();
        EndContacts = (endContacts ?? Array.Empty<SupportContact>()).ToList().AsReadOnly();
        RegionIndex = regionIndex;
        StartRule = startRule;
        EndRule = endRule;
    }

    public IReadOnlyList<Point2d> Points { get; }
    public bool IsClosed { get; }
    public IReadOnlyList<SupportContact> StartContacts { get; }
    public IReadOnlyList<SupportContact> EndContacts { get; }
    public int RegionIndex { get; }
    public RebarEndRule StartRule { get; }
    public RebarEndRule EndRule { get; }
}
