using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Box = AutoCADPlugin.SupportLabelLayout.Box;

namespace AutoCADPlugin;

// Migration of 38's untagged Line/DBText annotations. Contours, support boundaries
// and both cross diagonals are ineligible. Only a short connected route from an
// exact beam/slab label to its selected support can be replaced.
internal static class LegacySupportAnnotations
{
    internal sealed class Match
    {
        public Match(DBText text, Polyline support, List<Line> leaders)
        { Text = text; Support = support; Leaders = leaders; }
        public DBText Text { get; }
        public Polyline Support { get; }
        public List<Line> Leaders { get; }
    }

    public static List<Match> Find(IReadOnlyList<Entity> entities, IReadOnlyList<Polyline> supports)
    {
        var polygons = supports.ToDictionary(s => s.ObjectId, Vertices);
        var lines = entities.OfType<Line>().Where(l => l.Layer == Standards.OtherThinLayer &&
            OutlineRole.Get(l) == "" && AnnotationIdentity.Get(l) == "" &&
            !supports.Any(s => IsDiagonal(l, polygons[s.ObjectId]))).ToList();
        var result = new List<Match>();
        var used = new HashSet<ObjectId>();
        foreach (var text in entities.OfType<DBText>().Where(t => t.Layer == Standards.TextLayer &&
            (t.TextString == "楼层梁" || t.TextString == "楼层板") && AnnotationIdentity.Get(t) == ""))
        {
            var box = new Box(text.Position.X - 160, text.Position.Y - 160,
                text.Position.X + text.Height * text.TextString.Length + 160, text.Position.Y + text.Height + 160);
            var matches = new List<Match>();
            foreach (var first in lines.Where(l => !used.Contains(l.ObjectId)))
            foreach (var end in new[] { false, true })
            {
                var near = Point(end ? first.EndPoint : first.StartPoint);
                var far = Point(end ? first.StartPoint : first.EndPoint);
                if (!box.Contains(near)) { continue; }
                TryMatch(far, new List<Line> { first });
                foreach (var second in lines.Where(l => l.ObjectId != first.ObjectId && !used.Contains(l.ObjectId)))
                {
                    if (Point(second.StartPoint).GetDistanceTo(far) < .1) { TryMatch(Point(second.EndPoint), new List<Line> { first, second }); }
                    if (Point(second.EndPoint).GetDistanceTo(far) < .1) { TryMatch(Point(second.StartPoint), new List<Line> { first, second }); }
                }
            }
            var match = matches.OrderBy(m => m.Leaders.Sum(l => l.Length)).FirstOrDefault();
            if (match == null)
            { throw new InvalidOperationException($"未能可靠识别旧“{text.TextString}”指引（文字句柄{text.Handle}），未修改图纸。请连同完整指引和支撑框选，或删除该旧指引及文字后重试。"); }
            result.Add(match);
            foreach (var line in match.Leaders) { used.Add(line.ObjectId); }

            void TryMatch(Point2d anchor, List<Line> route)
            {
                foreach (var support in supports)
                {
                    var polygon = polygons[support.ObjectId]; var bounds = Box.Around(polygon);
                    if (SupportClassification.Label(bounds.Right - bounds.Left, bounds.Top - bounds.Bottom, "楼层梁") != text.TextString) { continue; }
                    if (ContourGraph.Contains(polygon, anchor) || ContourGraph.Edges(polygon).Any(e => ContourGraph.Distance(anchor, e) < .1))
                    { matches.Add(new Match(text, support, route)); }
                }
            }
        }
        return result;
    }

    private static bool IsDiagonal(Line line, List<Point2d> polygon)
    {
        var box = Box.Around(polygon);
        var a = Point(line.StartPoint); var b = Point(line.EndPoint);
        bool Corner(Point2d p) => (Math.Abs(p.X - box.Left) < .1 || Math.Abs(p.X - box.Right) < .1) &&
            (Math.Abs(p.Y - box.Bottom) < .1 || Math.Abs(p.Y - box.Top) < .1);
        return Corner(a) && Corner(b);
    }

    internal static List<Point2d> Vertices(Polyline polyline) =>
        Enumerable.Range(0, polyline.NumberOfVertices).Select(polyline.GetPoint2dAt).ToList();
    internal static Point2d Point(Point3d p) => new Point2d(p.X, p.Y);
}
