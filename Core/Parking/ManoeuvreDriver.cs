using System;
using System.Collections.Generic;
using DrivenRides.Core.Drive;

namespace DrivenRides.Core.Parking
{
    // Tracks one signed leg at a time. Road pursuit assumes forward travel and is intentionally not used here.
    public sealed class ManoeuvreDriver
    {
        private readonly CarSpec car;
        private readonly ManoeuvrePlan plan;
        private int cursor, end;
        private float integral;
        public bool Done { get; private set; }
        public string Failure { get; private set; } = "";
        public int Direction => plan.Points[Math.Min(cursor + 1, plan.Points.Count - 1)].Direction;
        public int Cursor => cursor;
        public ManoeuvreDriver(CarSpec car, ManoeuvrePlan plan) { this.car = car; this.plan = plan; SetEnd(); }
        // Check the closed-loop trajectory, including stops and steering changes, before moving a real car.
        // Empty means the model completed the manoeuvre inside the supplied clearance envelope.
        public static string Validate(CarSpec car, ManoeuvrePlan plan, Func<CarPose, bool> clear, Func<bool>? cancelled = null)
        {
            var start = plan.Points[0].Pose;
            var model = car.Model(start.X, start.Z, start.Heading);
            model.SetWheel(start.Wheel);
            var driver = new ManoeuvreDriver(car, plan);
            for (var i = 0; i < 15000; i++)
            {
                if (i % 32 == 0 && cancelled?.Invoke() == true) return "tracking check cancelled";
                var pose = new CarPose((float)model.X, (float)model.Z, (float)model.Heading, (float)model.Speed, (float)model.Steer);
                if (!clear(pose)) return "tracked body leaves the clear surface";
                var output = driver.Step(pose, false);
                if (driver.Failure.Length > 0) return driver.Failure;
                if (driver.Done) return "";
                model.Step(output, .02f);
            }
            return "tracking check timed out";
        }
        private void SetEnd()
        {
            end = Math.Min(cursor + 1, plan.Points.Count - 1);
            while (end + 1 < plan.Points.Count && plan.Points[end + 1].Direction == Direction) end++;
        }
        public bool Blocked(CarPose pose, IReadOnlyList<OtherCar> traffic, Func<CarPose, bool> clear)
        {
            if (!clear(pose)) return true;
            var distance = 0f; var previous = pose;
            for (var i = cursor + 1; i < plan.Points.Count && distance < 4f; i += 3)
            {
                var p = plan.Points[i].Pose;
                distance += ManoeuvrePlan.Distance(previous, p); previous = p;
                if (!clear(p)) return true;
                var body = ManoeuvrePlan.Body(car, p, 0.25f);
                foreach (var other in traffic)
                {
                    var seconds = distance / 0.8f;
                    var corridor = other.Speed < 0.1f
                        ? new Box(other.X, other.Z, other.Heading, other.Width / 2 + 0.3f, other.Length / 2 + 0.3f)
                        : Box.Along(other.X, other.Z, other.X + other.Vx * seconds, other.Z + other.Vz * seconds,
                            other.Width / 2 + 0.3f).Grown(0f, other.Length / 2 + 0.3f);
                    if (body.Overlaps(corridor)) return true;
                }
            }
            return false;
        }
        public DriveOutput Step(CarPose pose, bool blocked, float dt = 0.02f)
        {
            var hold = new DriveOutput { Handbrake = 1f, Holding = true, SteerDegrees = pose.Wheel };
            if (Done || Failure.Length > 0 || blocked) { integral = 0; return hold; }
            var best = cursor; var distance = ManoeuvrePlan.Distance(pose, plan.Points[cursor].Pose);
            for (var i = cursor + 1; i <= Math.Min(end, cursor + 25); i++)
            {
                var d = ManoeuvrePlan.Distance(pose, plan.Points[i].Pose);
                if (d < distance) { best = i; distance = d; }
            }
            cursor = best;
            if (distance > 0.8f) { Failure = "left the manoeuvre path"; return hold; }
            var toEnd = ManoeuvrePlan.Distance(pose, plan.Points[end].Pose);
            // A road handoff has a measured join tolerance. Stop within it instead of crawling forever
            // after a final point a few centimetres beside the real trajectory; parking keeps its precision.
            var joined = plan.RoadEntry && end == plan.Points.Count - 1 && toEnd < .3f
                && ManoeuvrePlan.Distance(pose, plan.Goal) <= .65f
                && Math.Abs(ManoeuvrePlan.Angle(pose.Heading - plan.Goal.Heading)) <= .18f;
            if (toEnd < 0.08f || cursor == end || joined)
            {
                integral = 0;
                if (Math.Abs(pose.Speed) > 0.06f) return hold;
                if (end == plan.Points.Count - 1)
                {
                    hold.SteerDegrees = 0;
                    if (Math.Abs(pose.Wheel) > 3f) return hold;
                    // A road handoff is checked again by JoinWork against the actual road line. It need not
                    // meet the precise pose of a parked car, but remains well inside the road join limits.
                    if (ManoeuvrePlan.Distance(pose, plan.Goal) > (plan.RoadEntry ? 0.65f : 0.4f)
                        || Math.Abs(ManoeuvrePlan.Angle(pose.Heading - plan.Goal.Heading)) > (plan.RoadEntry ? 0.18f : 0.1f))
                        Failure = "final parking pose is outside tolerance";
                    else Done = true;
                    return hold;
                }
                cursor = end; SetEnd(); return hold;
            }
            var direction = Direction;
            if (pose.Speed * direction < -0.04f) { integral = 0; return hold; }
            var look = cursor;
            var ahead = 0f;
            while (look < end && ahead < 1.2f) { ahead += ManoeuvrePlan.Distance(plan.Points[look].Pose, plan.Points[look + 1].Pose); look++; }
            var target = plan.Points[look].Pose;
            var dx = target.X - pose.X; var dz = target.Z - pose.Z;
            var curvature = 2f * (dx * (float)Math.Cos(pose.Heading) - dz * (float)Math.Sin(pose.Heading)) / Math.Max(0.25f, dx * dx + dz * dz);
            var reference = plan.Points[cursor].Pose;
            var rx = target.X - reference.X; var rz = target.Z - reference.Z;
            var chord = 2f * (rx * (float)Math.Cos(reference.Heading) - rz * (float)Math.Sin(reference.Heading)) / Math.Max(0.25f, rx * rx + rz * rz);
            if (ahead > 0.15f)
                curvature += ManoeuvrePlan.Angle(target.Heading - reference.Heading) / (direction * ahead) - chord;
            // Invert the same calibrated Ackermann model the search used, in both directions.
            var lo = -car.LockAt(0); var hi = car.LockAt(0);
            for (var i = 0; i < 16; i++)
            {
                var mid = (lo + hi) / 2;
                if (car.YawFactor * CarModel.Curvature(mid, car.Wheelbase, car.TrackWidth) < curvature) lo = mid; else hi = mid;
            }
            var steer = (lo + hi) / 2;
            // Steering gets into position before the first movement or a direction change.
            if (Math.Abs(pose.Speed) < 0.06f && Math.Abs(steer - pose.Wheel) > 8f) { integral = 0; hold.SteerDegrees = steer; return hold; }
            var desired = Math.Min(0.8f, Math.Max(0.12f, toEnd * 0.7f));
            // At parking speed proportional control alone cannot overcome the real tyres' rolling resistance.
            // A bounded integral supplies that torque, reset whenever we stop or wait for an obstacle.
            var error = desired - pose.Speed * direction;
            integral = Math.Max(0f, Math.Min(0.25f, integral + error * 0.2f * Math.Max(0f, Math.Min(dt, 0.1f))));
            var braking = error < -0.12f;
            if (braking) integral = Math.Max(0, integral - dt * 0.5f);
            return new DriveOutput { Desired = direction * desired, SteerDegrees = steer,
                Throttle = braking ? 0f : Math.Max(0f, Math.Min(0.35f, error * 0.45f + integral + 0.06f)),
                Handbrake = braking ? 0.35f : 0f };
        }
    }
}
