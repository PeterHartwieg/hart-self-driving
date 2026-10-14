using System;
using System.Collections.Generic;
using DrivenRides.Core.Routing;

namespace DrivenRides.Core.Drive
{
    // How far a wheel stands above the plane through the car's three other wheels: a slope or a ramp tilts all four
    // together, a kerb lifts one (turn test 0.1.59: the rear wheels 0.49 m above the front ones on a Hamptons ramp read
    // as a kerb against the lowest wheel). The rig's kerb check (TestCar) and the planner's (Feasibility) both use it.
    // Both wheels of one side up together isn't checked: the road's cross-fall it needed, read beside the nearest lane,
    // met islands and kerbs there (T3 on 0.1.81, ride 13: a 0.48 m "lean" for the limo standing flat on its line in a
    // median's crossing; the planner refused a U-turn for a 0.89 m one). A car run onto a kerb lifts one wheel first.
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
    }
}
