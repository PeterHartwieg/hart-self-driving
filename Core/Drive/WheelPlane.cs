using System;
using System.Collections.Generic;
using DrivenRides.Core.Routing;

namespace DrivenRides.Core.Drive
{
    // How far a wheel stands above the plane through the car's three other wheels: a slope or a ramp tilts all four
    // together, a kerb lifts one (turn test 0.1.59: the rear wheels 0.49 m above the front ones on a Hamptons ramp read
    // as a kerb against the lowest wheel). The rig's kerb check (TestCar) and the planner's (Feasibility) both use it.
    public static class WheelPlane
    {
        // wheels: four points (where each wheel meets the ground). The rise of wheel i, or null when the other three
        // don't make a plane.
        public static float? Rise(IReadOnlyList<Vec3> wheels, int i)
        {
            var others = new List<Vec3>(3);
            for (var j = 0; j < wheels.Count && others.Count < 3; j++) if (j != i) others.Add(wheels[j]);
            if (others.Count < 3) return null;
            Vec3 a = others[1] - others[0], b = others[2] - others[0];
            // Cross product a x b.
            double nx = a.Y * b.Z - a.Z * b.Y, ny = a.Z * b.X - a.X * b.Z, nz = a.X * b.Y - a.Y * b.X;
            var length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (length < 1e-6) return null;
            nx /= length; ny /= length; nz /= length;
            if (ny < 0) { nx = -nx; ny = -ny; nz = -nz; }
            var w = wheels[i] - others[0];
            return (float)(w.X * nx + w.Y * ny + w.Z * nz);
        }

        // How much higher the car's left wheels stand than its right ones, beyond the road's own cross-fall there:
        // both wheels of one side up on a kerb tilt the plane through any three wheels with them, so Rise can't see
        // it (review round 2). wheels: rear left, rear right, front left, front right. crossFall: how much higher the
        // road itself is on the left than on the right across the same width (CrossFall times the width).
        public static float Roll(IReadOnlyList<Vec3> wheels, float crossFall) =>
            (wheels[0].Y + wheels[2].Y - wheels[1].Y - wheels[3].Y) / 2f - crossFall;

        // The road's cross-fall under a car, metres per metre across it to the left: on the nearest lane of the
        // graph, level with the car, the ground SampleHalf to the car's left less the ground SampleHalf to its right,
        // over the distance between. Sampled near the lane's middle, so a kerb beside the lane isn't taken for the
        // road's fall. Null where no lane is within LaneReach or the ground isn't there.
        public static float? CrossFall(LaneGraph graph, Vec3 centre, double heading, Func<Vec3, float?> groundAt)
        {
            Vec3? nearest = null;
            var best = LaneReach * LaneReach;
            foreach (var w in graph.Near(centre, 30f))
            {
                var a = graph.Position(w);
                if (Math.Abs(a.Y - centre.Y) > 2f) continue;
                foreach (var n in graph.Successors(w))
                {
                    var b = graph.Position(n);
                    float dx = b.X - a.X, dz = b.Z - a.Z, span = dx * dx + dz * dz;
                    var f = span > 0f ? Math.Max(0f, Math.Min(1f, ((centre.X - a.X) * dx + (centre.Z - a.Z) * dz) / span)) : 0f;
                    var q = new Vec3(a.X + dx * f, a.Y + (b.Y - a.Y) * f, a.Z + dz * f);
                    var d = (q.X - centre.X) * (q.X - centre.X) + (q.Z - centre.Z) * (q.Z - centre.Z);
                    if (d < best) { best = d; nearest = q; }
                }
            }
            if (!(nearest is Vec3 p)) return null;
            float rx = (float)Math.Cos(heading) * SampleHalf, rz = -(float)Math.Sin(heading) * SampleHalf;
            if (!(groundAt(new Vec3(p.X - rx, p.Y, p.Z - rz)) is float left) || !(groundAt(new Vec3(p.X + rx, p.Y, p.Z + rz)) is float right)) return null;
            return (left - right) / (2f * SampleHalf);
        }

        public const float SampleHalf = 0.75f;
        public const float LaneReach = 6f;
    }
}
