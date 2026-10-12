using System;
using System.Collections.Generic;
using DrivenRides.Core.Routing;

namespace DrivenRides.Core.Ride
{
    // What a turn's drive needs of a car. The pivot is the car's origin, which Gley turns the body about.
    public readonly struct CarShape
    {
        public readonly float WheelDistance, MaxSteerDegrees, FrontAxle, CentreHeight, CentreAhead;

        // frontAxle: metres ahead of the pivot, where Gley steers from (VehicleComponent.GetFrontAxle). centreHeight
        // and centreAhead: the centre of mass above the road and ahead of the pivot (Gley puts it between the
        // wheels), from which Gley measures its 5 m reach.
        public CarShape(float wheelDistance, float maxSteerDegrees, float frontAxle, float centreHeight = 0f, float centreAhead = 0f)
        {
            WheelDistance = Math.Max(0.5f, wheelDistance);
            MaxSteerDegrees = Math.Min(60f, Math.Max(5f, maxSteerDegrees));
            FrontAxle = frontAxle;
            CentreHeight = Math.Min(4.5f, Math.Max(0f, centreHeight));
            CentreAhead = centreAhead;
        }
    }

    // The car's pivot and heading (radians, as atan2(dx, dz)).
    public readonly struct Pose
    {
        public readonly float X, Y, Z, Heading;

        public Pose(float x, float y, float z, float heading) { X = x; Y = y; Z = z; Heading = heading; }
    }

    // How Gley drives a car through an added turn, for the world check. DriveJob steers the front axle at the target
    // waypoint; the wheel turns at most maxSteer x the ride's steering factor a second, and the body turns about its
    // pivot by speed x tan(steer) / wheel distance. The target moves on once the centre of mass is within 5 m of it
    // (TrafficManager checks the preset path every physics step, DrivingAI.HasReachedOrPassedWaypoint) or the front
    // axle within 1.5 m (DriveJob). In 17 of 20 recorded turns (turn test 0.1.39) the car started its turn with the
    // centre 4.4 to 4.9 m from the last via. Steering from the pivot instead swung a 6.7 m car 1.8 m wide (0.1.38).
    public static class TurnDrive
    {
        private const float StepSeconds = 0.02f;
        private const float FrontReach = 1.5f;
        private const float CentreReach = 5f;

        // From the front axle on the entry waypoint, heading (dx, dz) with the wheel straight, through the targets
        // (the turn's vias, then the aim) until Gley would move on from the last. A pose every `every` metres, and
        // the last one. Null if the car never gets there (it circles a target it can't reach).
        public static List<Pose>? Drive(Vec3 entry, float dx, float dz, IReadOnlyList<Vec3> targets, CarShape car,
            float speedMs, float steeringFactor, float every = 0.5f)
        {
            var poses = new List<Pose>();
            var centreReach = Math.Sqrt(CentreReach * CentreReach - car.CentreHeight * car.CentreHeight);
            if (targets.Count == 0) return poses;
            var heading = Math.Atan2(dx, dz);
            double x = entry.X - car.FrontAxle * Math.Sin(heading), z = entry.Z - car.FrontAxle * Math.Cos(heading);
            double y = entry.Y;
            var step = Math.Max(0.1f, speedMs) * StepSeconds;
            var steerStep = car.MaxSteerDegrees * steeringFactor * StepSeconds;
            var budget = 40.0;
            var previous = entry;
            foreach (var t in targets) { budget += 2 * Vec3.HorizontalDistance(previous, t); previous = t; }
            var steer = 0.0;
            var target = 0;
            var since = 0.0;
            var arrived = false;
            poses.Add(new Pose((float)x, (float)y, (float)z, (float)heading));
            for (var travelled = 0.0; travelled < budget; travelled += step)
            {
                var fx = x + car.FrontAxle * Math.Sin(heading);
                var fz = z + car.FrontAxle * Math.Cos(heading);
                var tx = targets[target].X - fx;
                var tz = targets[target].Z - fz;
                var distance = Math.Sqrt(tx * tx + tz * tz);
                var cx = targets[target].X - (x + car.CentreAhead * Math.Sin(heading));
                var cz = targets[target].Z - (z + car.CentreAhead * Math.Cos(heading));
                if (distance < FrontReach || Math.Sqrt(cx * cx + cz * cz) < centreReach)
                {
                    var last = target == targets.Count - 1;
                    if (last) { arrived = true; break; }
                    target++;
                    continue;
                }
                steer = Steer(steer, Wrap(Math.Atan2(tx, tz) - heading) * 180 / Math.PI, distance / step, steerStep, car.MaxSteerDegrees);
                // Gley pushes the body along its heading, then turns it.
                x += step * Math.Sin(heading);
                z += step * Math.Cos(heading);
                heading += step * Math.Tan(steer * Math.PI / 180) / car.WheelDistance;
                y += (targets[target].Y - y) * Math.Min(1.0, step / Math.Max(step, distance));
                since += step;
                if (since >= every) { poses.Add(new Pose((float)x, (float)y, (float)z, (float)heading)); since = 0; }
            }
            poses.Add(new Pose((float)x, (float)y, (float)z, (float)heading));
            if (!arrived) return null;
            return poses;
        }

        // From the front axle on the line's start, heading along it with the wheel straight, steering the front axle at
        // the point `lookahead` metres further along the line than the car's pivot (the ride moves Gley's target there
        // every physics step), until that point reaches the line's end. Measured from the pivot, the middle of the car
        // keeps to the line and its front swings a little wide, as a driver takes a corner; measured from the front
        // axle, the car's rear cut inside it (Peter, turn test 0.1.43). Null if the car never gets there.
        public static List<Pose>? Pursue(Polyline line, CarShape car, float speedMs, float steeringFactor, float lookahead, float every = 0.5f, bool fromFront = false)
        {
            var poses = new List<Pose>();
            if (line.Length < 1f) return poses;
            var start = line.At(0f);
            var next = line.At(0.5f);
            var heading = Math.Atan2(next.X - start.X, next.Z - start.Z);
            double x = start.X - car.FrontAxle * Math.Sin(heading), z = start.Z - car.FrontAxle * Math.Cos(heading);
            var step = Math.Max(0.1f, speedMs) * StepSeconds;
            var steerStep = car.MaxSteerDegrees * steeringFactor * StepSeconds;
            var steer = 0.0;
            var since = 0.0;
            var along = 0f;
            poses.Add(new Pose((float)x, start.Y, (float)z, (float)heading));
            for (var travelled = 0.0; travelled < 2 * line.Length + 20; travelled += step)
            {
                var fx = x + car.FrontAxle * Math.Sin(heading);
                var fz = z + car.FrontAxle * Math.Cos(heading);
                along = fromFront ? line.Project((float)fx, (float)fz, along) : line.Project((float)x, (float)z, along);
                if (along + lookahead >= line.Length)
                {
                    poses.Add(new Pose((float)x, line.At(along).Y, (float)z, (float)heading));
                    return poses;
                }
                var carrot = line.At(along + lookahead);
                double tx = carrot.X - fx, tz = carrot.Z - fz;
                steer = Steer(steer, Wrap(Math.Atan2(tx, tz) - heading) * 180 / Math.PI, Math.Sqrt(tx * tx + tz * tz) / step, steerStep, car.MaxSteerDegrees);
                x += step * Math.Sin(heading);
                z += step * Math.Cos(heading);
                heading += step * Math.Tan(steer * Math.PI / 180) / car.WheelDistance;
                since += step;
                if (since >= every) { poses.Add(new Pose((float)x, line.At(along).Y, (float)z, (float)heading)); since = 0; }
            }
            return null;
        }

        // One physics step of DriveJob.ComputeSteerAngle: the wheel moves towards the wanted angle by its step, quicker
        // when it has to cross the middle (from a straight run it is at exactly 0, so it turns in within five steps)
        // or would not reach the angle before the target (`frames` steps away); then it is held to the lock.
        private static double Steer(double steer, double wanted, double frames, double steerStep, double maxSteer)
        {
            var quick = steerStep;
            if (frames > 5)
            {
                var need = Math.Abs(wanted - steer) / steerStep;
                if (Math.Sign(steer) != Math.Sign(wanted) && Math.Abs(wanted - steer) > 5) quick *= need / 5;
                else if (need > frames + 5) quick *= need / frames;
            }
            steer += Math.Max(-quick, Math.Min(quick, wanted - steer));
            return Math.Max(-maxSteer, Math.Min(maxSteer, steer));
        }

        private static double Wrap(double radians)
        {
            while (radians > Math.PI) radians -= 2 * Math.PI;
            while (radians < -Math.PI) radians += 2 * Math.PI;
            return radians;
        }
    }
}
