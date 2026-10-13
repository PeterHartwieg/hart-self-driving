using System;
using System.Collections.Generic;

namespace DrivenRides.Core.Routing
{
    // The junctions' boxes: for each junction (its waypoints), the convex hull of its waypoints
    // and of the stop lines and first waypoints of the roads into and out of it. The crossings lie inside, and the
    // game marks them as pedestrian ground: T2 (session 20261013-193524) failed turns as "pavement" with the car on
    // the zebra crossing in a junction's mouth, right on its line.
    public sealed class JunctionBoxes
    {
        private readonly List<(List<(float X, float Z)> Hull, float Y)> boxes = new List<(List<(float, float)>, float)>();
        private readonly Dictionary<(int, int), List<int>> grid = new Dictionary<(int, int), List<int>>();
        private const float Cell = 50f;

        public JunctionBoxes(LaneGraph graph)
        {
            var n = graph.Count;
            var parent = new int[n];
            for (var i = 0; i < n; i++) parent[i] = i;
            int Root(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
            // Joined by lane edges, and by being within 8 m of each other: Gley's lanes cross a junction on their own
            // waypoints without meeting, and a block (60 m and more) keeps the next junction apart.
            for (var i = 0; i < n; i++)
            {
                if (!graph.IsIntersection(i)) continue;
                foreach (var j in graph.Successors(i))
                    if (graph.IsIntersection(j)) parent[Root(j)] = Root(i);
                var p = graph.Position(i);
                foreach (var j in graph.Near(p, 8f))
                    if (j != i && graph.IsIntersection(j) && Math.Abs(graph.Position(j).Y - p.Y) < 2f && Vec3.HorizontalDistance(graph.Position(j), p) <= 8f)
                        parent[Root(j)] = Root(i);
            }
            var members = new Dictionary<int, List<int>>();
            for (var i = 0; i < n; i++)
            {
                if (!graph.IsIntersection(i)) continue;
                var r = Root(i);
                if (!members.TryGetValue(r, out var list)) members[r] = list = new List<int>();
                list.Add(i);
            }
            foreach (var group in members.Values)
            {
                var points = new List<(float X, float Z)>();
                var y = 0f;
                var seen = new HashSet<int>();
                void Take(int w)
                {
                    if (!seen.Add(w)) return;
                    var p = graph.Position(w);
                    points.Add((p.X, p.Z));
                    y += p.Y;
                }
                foreach (var w in group)
                {
                    Take(w);
                    foreach (var before in graph.Predecessors(w)) if (!graph.IsIntersection(before)) Take(before);
                    foreach (var after in graph.Successors(w)) if (!graph.IsIntersection(after)) Take(after);
                }
                if (points.Count < 3) continue;
                var hull = Hull(points);
                if (hull.Count < 3) continue;
                var index = boxes.Count;
                boxes.Add((hull, y / seen.Count));
                float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
                foreach (var (x, z) in hull) { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minZ = Math.Min(minZ, z); maxZ = Math.Max(maxZ, z); }
                for (var cx = (int)Math.Floor(minX / Cell); cx <= (int)Math.Floor(maxX / Cell); cx++)
                    for (var cz = (int)Math.Floor(minZ / Cell); cz <= (int)Math.Floor(maxZ / Cell); cz++)
                    {
                        if (!grid.TryGetValue((cx, cz), out var cell)) grid[(cx, cz)] = cell = new List<int>();
                        cell.Add(index);
                    }
            }
        }

        public int Count => boxes.Count;

        // Whether a point lies in a junction's box (and level with it).
        public bool Contains(Vec3 p)
        {
            if (!grid.TryGetValue(((int)Math.Floor(p.X / Cell), (int)Math.Floor(p.Z / Cell)), out var cell)) return false;
            foreach (var index in cell)
            {
                var (hull, y) = boxes[index];
                if (Math.Abs(p.Y - y) > 3f) continue;
                var inside = true;
                for (var k = 0; k < hull.Count && inside; k++)
                {
                    var (ax, az) = hull[k];
                    var (bx, bz) = hull[(k + 1) % hull.Count];
                    // Counter-clockwise hull: inside is on the left of every edge.
                    if ((bx - ax) * (p.Z - az) - (bz - az) * (p.X - ax) < 0f) inside = false;
                }
                if (inside) return true;
            }
            return false;
        }

        // Andrew's monotone chain, counter-clockwise.
        private static List<(float X, float Z)> Hull(List<(float X, float Z)> points)
        {
            points.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Z.CompareTo(b.Z));
            var hull = new List<(float X, float Z)>();
            float Cross((float X, float Z) o, (float X, float Z) a, (float X, float Z) b) => (a.X - o.X) * (b.Z - o.Z) - (a.Z - o.Z) * (b.X - o.X);
            foreach (var p in points)
            {
                while (hull.Count >= 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= 0) hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            var lower = hull.Count + 1;
            for (var i = points.Count - 2; i >= 0; i--)
            {
                var p = points[i];
                while (hull.Count >= lower && Cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= 0) hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            hull.RemoveAt(hull.Count - 1);
            return hull;
        }
    }
}
