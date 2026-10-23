using System;
using System.Collections.Generic;
using DrivenRides.Core.Drive;

namespace DrivenRides.Core.Routing
{
    // Persistent street geometry, not a reservation or a generated free parking space.
    public readonly struct ParkingStrip
    {
        public readonly Vec3 Centre;
        public readonly float Heading, HalfWidth, HalfLength;
        public ParkingStrip(Vec3 centre, float heading, float halfWidth, float halfLength)
        { Centre = centre; Heading = heading; HalfWidth = halfWidth; HalfLength = halfLength; }
    }

    // Chooses road endpoints beside parallel parking. No manoeuvre search, steering or DrivePlan mutations here.
    public static class ParkingGoalPicker
    {
        public static List<Goal> Pick(LaneGraph graph, IReadOnlyList<ParkingStrip> strips, Vec3 door, CarSpec car, int vehicleType,
            float radius = 150f)
        {
            var costs = new Dictionary<int, float>();
            foreach (var strip in strips)
            {
                if (strip.HalfLength < car.Length / 2 + 0.5f || strip.HalfWidth < car.BodyHalf.X + 0.1f
                    || Math.Abs(strip.Centre.Y - door.Y) > 3f) continue;
                var s = (float)Math.Sin(strip.Heading); var c = (float)Math.Cos(strip.Heading);
                // A road endpoint is ahead of a possible parked body centre, ready for the local reverse entry.
                var staging = car.BodyHalf.Z + 3f;
                foreach (var w in graph.Near(door, radius + staging + 5f))
                {
                    var p = graph.Position(w);
                    var along = (p.X - strip.Centre.X) * s + (p.Z - strip.Centre.Z) * c - staging;
                    if (Math.Abs(along) > strip.HalfLength - car.Length / 2 - 0.5f) continue;
                    if (!graph.Allows(w, vehicleType) || !graph.PlainHeading(w, out var hx, out var hz)
                        || hx * s + hz * c < 0.98f || !Adjacent(graph, strip, p)) continue;
                    var parked = new Vec3(strip.Centre.X + s * along, strip.Centre.Y, strip.Centre.Z + c * along);
                    var walk = Vec3.HorizontalDistance(parked, door);
                    if (walk > radius || !StraightRoom(graph, w, s, c, staging + car.BodyHalf.Z + 1, car.FrontAhead + 2)) continue;
                    Keep(costs, w, walk / 1.4f);
                }
            }
            return Goals(costs);
        }

        // Once occupancy is known, use the same lane-side and direction checks for the actual space.
        public static List<Goal> ForSpace(LaneGraph graph, Vec3 rear, float heading, CarSpec car, int vehicleType)
        {
            var s = (float)Math.Sin(heading); var c = (float)Math.Cos(heading);
            var strip = new ParkingStrip(rear, heading, car.BodyHalf.X + 0.2f, car.Length / 2 + 1);
            var costs = new Dictionary<int, float>();
            var staging = car.FrontAhead + 3f;
            var wanted = new Vec3(rear.X + s * staging, rear.Y, rear.Z + c * staging);
            foreach (var w in graph.Near(wanted, 10))
            {
                var p = graph.Position(w);
                var ahead = (p.X - rear.X) * s + (p.Z - rear.Z) * c;
                if (!graph.Allows(w, vehicleType) || !graph.PlainHeading(w, out var hx, out var hz)
                    || hx * s + hz * c < 0.98f || ahead < staging || ahead > staging + 6f
                    || !Adjacent(graph, strip, p) || !StraightRoom(graph, w, s, c, ahead + 2, car.FrontAhead + 2)) continue;
                Keep(costs, w, (ahead - staging) / 0.8f);
            }
            return Goals(costs);
        }

        // The parking-facing lane must be the closest parallel lane to the strip, on this level. Both left and
        // right parking strips are valid; an inner lane cannot reach across another traffic lane to park.
        public static bool Adjacent(LaneGraph graph, ParkingStrip strip, Vec3 lane)
        {
            if (Math.Abs(lane.Y - strip.Centre.Y) > 1.5f) return false;
            var s = (float)Math.Sin(strip.Heading); var c = (float)Math.Cos(strip.Heading);
            var side = (lane.X - strip.Centre.X) * c - (lane.Z - strip.Centre.Z) * s;
            if (Math.Abs(side) < strip.HalfWidth + 0.5f || Math.Abs(side) > strip.HalfWidth + 3.2f) return false;
            foreach (var w in graph.Near(lane, 12))
            {
                if (!graph.PlainHeading(w, out var hx, out var hz) || Math.Abs(hx * s + hz * c) < 0.95f) continue;
                var a = graph.Position(w); var b = graph.Position(graph.Successors(w)[0]);
                if (Math.Abs(a.Y - lane.Y) > 1.5f || Math.Abs(b.Y - lane.Y) > 1.5f) continue;
                var length = Vec3.HorizontalDistance(a, b);
                var along = (lane.X - a.X) * hx + (lane.Z - a.Z) * hz;
                if (along < -0.5f || along > length + 0.5f) continue;
                var other = (a.X + hx * along - strip.Centre.X) * c - (a.Z + hz * along - strip.Centre.Z) * s;
                if (other * side > 0 && Math.Abs(other) > strip.HalfWidth && Math.Abs(other) < Math.Abs(side) - 1f) return false;
            }
            return true;
        }

        private static bool StraightRoom(LaneGraph graph, int w, float sx, float sz, float behind, float ahead)
        {
            for (var direction = 0; direction < 2; direction++)
            {
                var remaining = direction == 0 ? behind : ahead; var at = w;
                var seen = new HashSet<int> { at };
                while (remaining > 0)
                {
                    var next = direction == 0 ? graph.Predecessors(at) : graph.Successors(at);
                    if (next.Count != 1 || !seen.Add(next[0])) return false;
                    var to = next[0];
                    var a = graph.Position(direction == 0 ? to : at); var b = graph.Position(direction == 0 ? at : to);
                    var distance = Vec3.HorizontalDistance(a, b);
                    if (distance < 0.1f || ((b.X - a.X) * sx + (b.Z - a.Z) * sz) / distance < 0.98f) return false;
                    remaining -= distance;
                    // Leave the local motion entirely outside the junction, including the front overhang.
                    if (graph.IsIntersection(to)) { if (remaining >= -1f) return false; break; }
                    at = to;
                }
            }
            return true;
        }
        private static void Keep(Dictionary<int, float> costs, int w, float cost)
        { if (!costs.TryGetValue(w, out var old) || cost < old) costs[w] = cost; }
        private static List<Goal> Goals(Dictionary<int, float> costs)
        {
            var goals = new List<Goal>();
            foreach (var pair in costs) goals.Add(new Goal(pair.Key, pair.Value));
            goals.Sort((a, b) => { var order = a.WalkSeconds.CompareTo(b.WalkSeconds); return order != 0 ? order : a.Waypoint.CompareTo(b.Waypoint); });
            return goals;
        }
    }
}
