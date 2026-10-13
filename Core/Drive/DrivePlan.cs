using System;
using System.Collections.Generic;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;

namespace DrivenRides.Core.Drive
{
    // How a car driven by its own physics drives a route (the rig's driver=nwh, and the feasibility check): the
    // planner's expansion with each added turn on its driver's arc or through its vias, and the stretches driven
    // slowly. One place, so the check simulates what the car will drive.
    public sealed class DrivePlan
    {
        public ExpandedPath Expanded { get; }
        public List<int> Path => Expanded.Path;
        // The arc turns: path position of the entry (the exit is the next position) and the arc.
        public List<(int Entry, Polyline Arc)> Arcs { get; } = new List<(int, Polyline)>();
        // Path positions driven at no more than a speed: the roads' limits.
        public List<(int Position, float SpeedKmh)> Slow { get; } = new List<(int, float)>();
        public float TopKmh { get; }
        // Lane steps passed with the line moved sideways (DriveLine.Shifted): path position, metres along the step
        // where the obstacle begins and ends, the offset (right positive).
        public List<(int Position, float From, float To, float Offset)> Shifts { get; } = new List<(int, float, float, float)>();
        // Path positions of plain lane corners, rounded off on the line (DriveLine.Fillet): both steps are lane steps.
        public HashSet<int> Fillets { get; } = new HashSet<int>();

        private DrivePlan(ExpandedPath expanded, float topKmh) { Expanded = expanded; TopKmh = topKmh; }

        // arc(entry, exit): the arc an added turn is driven along, or null for its vias. clear(entry, exit, aim): as
        // PathExpander's. limitKmh(waypoint): the road's limit there, 0 for none.
        // shift(from, to): a lane step's sideways move past a car parked partly in it, or null.
        public static DrivePlan Build(LaneGraph graph, IReadOnlyList<int> route, CarSpec car, Func<int, int, Polyline?> arc,
            Func<int, int, int, bool>? clear = null, Func<int, float>? limitKmh = null, Func<int, int, (float From, float To, float Offset)?>? shift = null)
        {
            var expanded = PathExpander.Expand(graph, route, clear, (e, x) => arc(e, x) != null);
            var plan = new DrivePlan(expanded, car.TopKmh);
            var path = expanded.Path;
            for (var k = 1; k + 1 < path.Count; k++)
                if (Has(graph.Successors(path[k - 1]), path[k]) && Has(graph.Successors(path[k]), path[k + 1])) plan.Fillets.Add(k);
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
            // Each arc no faster than the car still has a sixth of its lock to spare on it (the lock narrows with speed):
            // on a U-turn curve at its smallest radius the wheel sat at lock and the car ran wide out of it.
            foreach (var (at, line) in plan.Arcs)
            {
                if (line.Radius <= 0f) continue;
                var fastest = 0f;
                for (var v = 1f; v <= car.TopKmh / 3.6f; v += 0.25f)
                    if (car.RadiusAt(v) * TurnArc.RadiusSlack <= line.Radius) fastest = v;
                    else break;
                if (fastest > 0f && fastest * 3.6f < car.TopKmh) plan.Slow.Add((at, fastest * 3.6f));
            }
            // No flat speed for added turns or U-turns: the corner speed comes from the line's curvature (never under
            // PursuitDriver.MinCornerMs), so a wide turn is driven faster than a tight one (Peter, NWH spike). The
            // Gley-era 15 km/h for a long car's U-turn covered the whole straight way into the junction too, and held
            // the car at a crawl before the turn began (T3 20261013-185723, ride 8: "taking a turn super slow").
            // Gley caps every car at the road's limit; so does this driver.
            if (limitKmh != null)
                for (var k = 0; k < path.Count; k++)
                {
                    var limit = limitKmh(path[k]);
                    if (limit > 0f && limit < car.TopKmh) plan.Slow.Add((k, limit));
                }
            return plan;
        }

        // The path as given (the rig's controls): no arcs, the slow positions at the rig's 25 km/h.
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

        // The driver for this plan's line: a slow position covers the stretch from it to the next position.
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
