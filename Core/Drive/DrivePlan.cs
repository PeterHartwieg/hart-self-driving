using System;
using System.Collections.Generic;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;

namespace DrivenRides.Core.Drive
{
    public sealed class DrivePlan
    {
        public ExpandedPath Expanded { get; }
        public List<int> Path => Expanded.Path;
        public List<(int Entry, Polyline Arc)> Arcs { get; } = new List<(int, Polyline)>();
        public List<(int Position, float SpeedKmh)> Slow { get; } = new List<(int, float)>();
        public float TopKmh { get; }
        public List<(int Position, float From, float To, float Offset)> Shifts { get; } = new List<(int, float, float, float)>();
        public HashSet<int> Fillets { get; } = new HashSet<int>();

        private DrivePlan(ExpandedPath expanded, float topKmh) { Expanded = expanded; TopKmh = topKmh; }

        public static DrivePlan Build(LaneGraph graph, IReadOnlyList<int> route, CarSpec car, Func<int, int, Polyline?> arc,
            Func<int, int, int, bool>? clear = null, Func<int, float>? limitKmh = null, Func<int, int, (float From, float To, float Offset)?>? shift = null)
        {
            var expanded = PathExpander.Expand(graph, route, clear, (e, x) => arc(e, x) != null);
            var plan = new DrivePlan(expanded, car.TopKmh);
            var path = expanded.Path;
            for (var k = 1; k + 1 < path.Count; k++)
                if (Has(graph.Successors(path[k - 1]), path[k]) && Has(graph.Successors(path[k]), path[k + 1])
                    && DriveLine.Fillet(graph.Position(path[k - 1]), graph.Position(path[k]), graph.Position(path[k + 1])) != null)
                    plan.Fillets.Add(k);
            if (shift != null)
                for (var k = 0; k + 1 < path.Count; k++)
                    if (shift(path[k], path[k + 1]) is var (from, to, offset)) plan.Shifts.Add((k, from, to, offset));
            foreach (var (entry, exit) in expanded.Arcs)
            {
                var at = -1;
                for (var k = 0; k + 1 < path.Count; k++) if (path[k] == entry && path[k + 1] == exit) { at = k; break; }
                var line = at >= 0 ? arc(entry, exit) : null;
                if (line != null) plan.Arcs.Add((at, line));
            }
            foreach (var (at, line) in plan.Arcs)
            {
                if (line.Radius <= 0f) continue;
                var fastest = 0f;
                for (var v = 1f; v <= car.TopKmh / 3.6f; v += 0.25f)
                    if (car.RadiusAt(v) * TurnArc.RadiusSlack <= line.Radius) fastest = v;
                    else break;
                if (fastest > 0f && fastest * 3.6f < car.TopKmh) plan.Slow.Add((at, fastest * 3.6f));
            }
            if (limitKmh != null)
                for (var k = 0; k < path.Count; k++)
                {
                    var limit = limitKmh(path[k]) * LimitFactor;
                    if (limit > 0f && limit < car.TopKmh) plan.Slow.Add((k, limit));
                }
            return plan;
        }

        public const float LimitFactor = 1.2f;

        public static DrivePlan Fixed(List<int> path, IEnumerable<int>? slow, float topKmh)
        {
            var expanded = new ExpandedPath();
            expanded.Path.AddRange(path);
            var plan = new DrivePlan(expanded, topKmh);
            if (slow != null) foreach (var k in slow) plan.Slow.Add((k, 25f));
            return plan;
        }

        public DriveLine Line(LaneGraph graph)
        {
            var points = new List<Vec3>(Path.Count);
            foreach (var i in Path) points.Add(graph.Position(i));
            return DriveLine.Build(points, Arcs, Fillets).Shifted(Shifts);
        }

        private static bool Has(IReadOnlyList<int> list, int value)
        {
            for (var k = 0; k < list.Count; k++) if (list[k] == value) return true;
            return false;
        }

        public PursuitDriver Driver(DriveLine line, float wheelbase, float frontAhead, float trackWidth)
        {
            var driver = new PursuitDriver(line, wheelbase, frontAhead, TopKmh / 3.6f, trackWidth);
            foreach (var (position, kmh) in Slow)
            {
                if (position < 0 || position >= Path.Count) continue;
                var from = line.AlongOf[position];
                var to = position + 1 < Path.Count ? line.AlongOf[position + 1] : from + 5f;
                driver.Slow(from, Math.Max(to, from + 1f), kmh / 3.6f);
            }
            return driver;
        }
    }
}
