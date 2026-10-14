using System;
using System.Collections.Generic;

namespace DrivenRides.Core.Routing
{
    // A gate across the road (the game's FenceDoor; a gated community's is a HamptonsPrivateFenceDoor): where its doors
    // and the trigger that opens them stand, as one box on the ground, and whether it opens for the player.
    public sealed class RoadGate
    {
        public float MinX, MaxX, MinZ, MaxZ, Y;
        public bool Locked;
        public string Name = "";

        public bool Contains(Vec3 p, float grow = 0f) =>
            p.X >= MinX - grow && p.X <= MaxX + grow && p.Z >= MinZ - grow && p.Z <= MaxZ + grow && Math.Abs(p.Y - Y) < 3f;

        public Vec3 Centre => new Vec3((MinX + MaxX) / 2f, Y, (MinZ + MaxZ) / 2f);
    }

    // Which lane steps go through a gate, and where a ride that can't go through stops before it.
    public static class GateCrossing
    {
        // The graph's steps (waypoint to successor) whose segment passes through the gate's box.
        public static List<(int From, int To)> Steps(LaneGraph graph, RoadGate gate)
        {
            var found = new List<(int, int)>();
            var centre = gate.Centre;
            var reach = Math.Max(gate.MaxX - gate.MinX, gate.MaxZ - gate.MinZ) / 2f + 30f;
            foreach (var w in graph.Near(centre, reach))
            {
                var a = graph.Position(w);
                if (Math.Abs(a.Y - gate.Y) > 3f) continue;
                foreach (var n in graph.Successors(w))
                    if (Crosses(a, graph.Position(n), gate)) found.Add((w, n));
            }
            return found;
        }

        // Whether a segment passes through the gate's box (on the ground, slab test).
        public static bool Crosses(Vec3 a, Vec3 b, RoadGate gate)
        {
            float t0 = 0f, t1 = 1f;
            if (!Clip(a.X, b.X - a.X, gate.MinX, gate.MaxX, ref t0, ref t1)) return false;
            if (!Clip(a.Z, b.Z - a.Z, gate.MinZ, gate.MaxZ, ref t0, ref t1)) return false;
            return true;
        }

        private static bool Clip(float p, float d, float min, float max, ref float t0, ref float t1)
        {
            if (Math.Abs(d) < 1e-6f) return p >= min && p <= max;
            float u = (min - p) / d, v = (max - p) / d;
            if (u > v) { var s = u; u = v; v = s; }
            t0 = Math.Max(t0, u);
            t1 = Math.Min(t1, v);
            return t0 <= t1;
        }

        // Where a route that must go through a locked gate at its step `crossing` (route[crossing] -> route[crossing+1])
        // ends instead: the last waypoint before it that stands at least `clear` metres outside the gate's box, so the
        // car stops with its front clear of the doors and of the trigger that would show the game's "locked" message.
        // The index in the route, or -1 if there is no such waypoint.
        public static int DropOff(LaneGraph graph, IReadOnlyList<int> route, int crossing, RoadGate gate, float clear)
        {
            for (var k = Math.Min(crossing, route.Count - 1); k >= 0; k--)
                if (!gate.Contains(graph.Position(route[k]), clear)) return k;
            return -1;
        }
    }
}
