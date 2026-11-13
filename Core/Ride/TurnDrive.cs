using System;
using System.Collections.Generic;
using DrivenRides.Core.Routing;

namespace DrivenRides.Core.Ride
{
    public readonly struct CarShape
    {
        public readonly float WheelDistance, MaxSteerDegrees, FrontAxle, CentreHeight, CentreAhead;

        public CarShape(float wheelDistance, float maxSteerDegrees, float frontAxle, float centreHeight = 0f, float centreAhead = 0f)
        {
            WheelDistance = Math.Max(0.5f, wheelDistance);
            MaxSteerDegrees = Math.Min(60f, Math.Max(5f, maxSteerDegrees));
            FrontAxle = frontAxle;
            CentreHeight = Math.Min(4.5f, Math.Max(0f, centreHeight));
            CentreAhead = centreAhead;
        }
    }

    public readonly struct Pose
    {
        public readonly float X, Y, Z, Heading;

        public Pose(float x, float y, float z, float heading) { X = x; Y = y; Z = z; Heading = heading; }
    }

    public static class TurnDrive
    {
        private const float StepSeconds = 0.02f;
        private const float FrontReach = 1.5f;
        private const float CentreReach = 5f;

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

        public static List<Vec3> LaneAfter(LaneGraph graph, int from, float metres)
        {
            var points = new List<Vec3>();
            var at = from;
            var gone = 0f;
            while (gone < metres)
            {
                var next = -1;
                var best = -2f;
                var p = graph.Position(at);
                graph.PlainHeading(at, out var hx, out var hz);
                foreach (var n in graph.Successors(at))
                {
                    var q = graph.Position(n);
                    var length = Vec3.HorizontalDistance(p, q);
                    if (length < 0.01f) continue;
                    var dot = ((q.X - p.X) * hx + (q.Z - p.Z) * hz) / length;
                    if (dot > best) { best = dot; next = n; }
                }
                if (next < 0) break;
                gone += Vec3.HorizontalDistance(p, graph.Position(next));
                points.Add(graph.Position(next));
                at = next;
            }
            if (points.Count == 0 && graph.PlainHeading(from, out var dx, out var dz))
                points.Add(graph.Position(from) + new Vec3(dx, 0f, dz) * metres);
            return points;
        }

        private static double Wrap(double radians)
        {
            while (radians > Math.PI) radians -= 2 * Math.PI;
            while (radians < -Math.PI) radians += 2 * Math.PI;
            return radians;
        }
    }
}
