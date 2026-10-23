using System;
using System.Collections.Generic;
using DrivenRides.Core.Drive;
using DrivenRides.Core.Routing;

namespace DrivenRides.Core.Parking
{
    // A temporary patch of street, independent of road routing. Encloses the whole body sweep, not just axles.
    public sealed class TrafficReservation
    {
        public Box Area { get; }
        public float Height { get; }
        public TrafficReservation(Box area, float height) { Area = area; Height = height; }

        public static TrafficReservation Preparing(CarSpec car, CarPose start, CarPose goal, float height, bool parking)
        {
            var poses = new List<CarPose> { start, goal };
            if (parking)
            {
                var s = (float)Math.Sin(goal.Heading); var c = (float)Math.Cos(goal.Heading);
                var ahead = (start.X - goal.X) * s + (start.Z - goal.Z) * c;
                var advance = Math.Max(0, car.FrontAhead + 3 - ahead);
                poses.Add(new CarPose(start.X + s * advance, start.Z + c * advance, goal.Heading));
            }
            return Enclose(car, poses, goal.Heading, height);
        }

        public static TrafficReservation ForPlan(CarSpec car, ManoeuvrePlan plan, float height)
        {
            var poses = new List<CarPose>();
            foreach (var p in plan.Points) poses.Add(p.Pose);
            poses.Add(plan.Goal);
            return Enclose(car, poses, plan.Goal.Heading, height);
        }

        private static TrafficReservation Enclose(CarSpec car, IEnumerable<CarPose> poses, float heading, float height)
        {
            var s = (float)Math.Sin(heading); var c = (float)Math.Cos(heading);
            float left = float.PositiveInfinity, right = float.NegativeInfinity, back = left, front = right;
            foreach (var pose in poses)
            {
                // Includes the controller's body-check margin and the distance between search samples.
                var body = ManoeuvrePlan.Body(car, pose, 0.5f);
                var x = body.X * c - body.Z * s; var z = body.X * s + body.Z * c;
                var w = body.RadiusOn(c, -s); var l = body.RadiusOn(s, c);
                left = Math.Min(left, x - w); right = Math.Max(right, x + w);
                back = Math.Min(back, z - l); front = Math.Max(front, z + l);
            }
            var mx = (left + right) / 2; var mz = (back + front) / 2;
            return new TrafficReservation(new Box(mx * c + mz * s, -mx * s + mz * c, heading,
                (right - left) / 2, (front - back) / 2), height);
        }

        public bool Touches(Box body, float height) => Math.Abs(height - Height) < 2f && Area.Overlaps(body);
        public static Box Body(OtherCar car) => new Box(car.X, car.Z, car.Heading, car.Width / 2, car.Length / 2);

        // Infinity means ordinary Gley movement. Cars already inside must be allowed to clear the reservation.
        // There is no retained car ID/state: pooled vehicles are assessed afresh wherever they reappear.
        public float StopDistance(OtherCar car, float height, Vec3? target = null)
        {
            if (Math.Abs(height - Height) >= 2 || Area.Overlaps(Body(car))) return float.PositiveInfinity;
            var speed = car.Speed;
            var distance = Entry(car, (float)Math.Sin(car.Heading), (float)Math.Cos(car.Heading));
            if (speed > 0.1f) distance = Math.Min(distance, Entry(car, car.Vx / speed, car.Vz / speed));
            if (target.HasValue && Math.Abs(target.Value.Y - Height) < 2)
            {
                var dx = target.Value.X - car.X; var dz = target.Value.Z - car.Z;
                var length = (float)Math.Sqrt(dx * dx + dz * dz);
                if (length > 0.1f)
                {
                    var turnEntry = Entry(car, dx / length, dz / length);
                    if (turnEntry <= length) distance = Math.Min(distance, turnEntry);
                }
            }
            // Start braking before the boundary, including one physics-step/reaction allowance.
            var horizon = speed * speed / 8f + Math.Max(3f, speed * 0.25f + 1f);
            return distance <= horizon ? Math.Max(0, distance - 0.5f) : float.PositiveInfinity;
        }

        public static float BrakedSpeed(float speed, float distance, float dt)
        {
            if (float.IsPositiveInfinity(distance) || dt <= 0) return speed;
            var deceleration = Math.Max(4f, speed * speed / (2f * Math.Max(0.01f, distance)));
            // Also bounds this very step when acquired late, rather than crossing the boundary before braking.
            return Math.Max(0, Math.Min(speed - deceleration * dt, distance / dt));
        }

        private float Entry(OtherCar car, float dx, float dz)
        {
            var body = Body(car);
            var px = (car.X - Area.X) * Area.Cos - (car.Z - Area.Z) * Area.Sin;
            var pz = (car.X - Area.X) * Area.Sin + (car.Z - Area.Z) * Area.Cos;
            var vx = dx * Area.Cos - dz * Area.Sin; var vz = dx * Area.Sin + dz * Area.Cos;
            var w = Area.HalfWidth + body.RadiusOn(Area.Cos, -Area.Sin);
            var l = Area.HalfLength + body.RadiusOn(Area.Sin, Area.Cos);
            float enter = 0, exit = float.PositiveInfinity;
            return Slab(px, vx, w, ref enter, ref exit) && Slab(pz, vz, l, ref enter, ref exit)
                ? enter : float.PositiveInfinity;
        }

        private static bool Slab(float p, float v, float extent, ref float enter, ref float exit)
        {
            if (Math.Abs(v) < 0.0001f) return Math.Abs(p) <= extent;
            var a = (-extent - p) / v; var b = (extent - p) / v;
            enter = Math.Max(enter, Math.Min(a, b)); exit = Math.Min(exit, Math.Max(a, b));
            return enter <= exit;
        }
    }
}
