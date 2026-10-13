using System;
using System.Collections.Generic;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;

namespace DrivenRides.Core.Drive
{
    // What the driver sees each physics step: the rear axle (the point a car turns about) and heading (radians, as
    // atan2(dx, dz)), the speed, and what is ahead.
    public struct DriveInput
    {
        public float RearX, RearZ, Heading, SpeedMs, Dt;
        // Metres along the line where the car's front must stop (a red light), or null.
        public float? StopAlong;
        // Metres from the car's front to something in its way (a car ahead), or null.
        public float? GapAhead;
    }

    public struct DriveOutput
    {
        // The wheel angle the driver wants (degrees, positive to the right) and the pedals, 0 to 1.
        public float SteerDegrees, Throttle, Brake, Handbrake;
        public float Along, Lateral, Desired, Lookahead;
        public bool Holding;
    }

    // A driver for a car with real physics: pure pursuit from the rear axle on the line (the ride's arc pursuit
    // targets a point ahead along the line in the same way), and pedals that hold a speed plan: the corner speed of
    // the line, the slow stretches of the ride's added turns, and braking to stop lines and cars ahead.
    public sealed class PursuitDriver
    {
        private readonly DriveLine line;
        private readonly float wheelbase, frontAhead, top;
        private readonly float[] cornerSpeeds;
        private readonly List<(float From, float To, float Speed)> slow = new List<(float, float, float)>();
        private float along;
        private float integral;
        private int firstPoint;

        // Comfortable braking and the limit past which a red is driven through (amber), m/s².
        public const float Decel = 2.5f;
        public const float HardDecel = 5f;
        public const float StopGap = 2.5f;
        // Sideways acceleration in a corner: 3.5 m/s² takes a 6.5 m arc at 17 km/h and a 20 m one at 30 (Peter, NWH
        // spike: larger turns faster; at 2.5 every corner felt slow).
        public const float Lateral = 3.5f;

        // frontAhead: the car's front bumper, metres ahead of the rear axle.
        public PursuitDriver(DriveLine line, float wheelbase, float frontAhead, float topMs)
        {
            this.line = line;
            this.wheelbase = Math.Max(1f, wheelbase);
            this.frontAhead = frontAhead;
            top = topMs;
            cornerSpeeds = line.CornerSpeeds(topMs, Lateral);
        }

        public float Along => along;
        public float Length => line.Line.Length;
        public float FrontAhead => frontAhead;

        // A stretch of the line (metres along it) driven at no more than speedMs.
        public void Slow(float from, float to, float speedMs) => slow.Add((from, to, speedMs));

        // How far ahead of the rear axle the driver looks: past the front axle, further at speed.
        public float LookaheadAt(float speedMs) => Math.Max(wheelbase + 1.5f, 1.5f + 0.45f * speedMs);

        public DriveOutput Step(DriveInput i)
        {
            along = line.Line.Project(i.RearX, i.RearZ, along);
            var here = line.Line.At(along);
            float hx = (float)Math.Sin(i.Heading), hz = (float)Math.Cos(i.Heading);
            // Left of the line is negative, as the steering is.
            var lateral = (i.RearX - here.X) * hz - (i.RearZ - here.Z) * hx;

            var ld = LookaheadAt(i.SpeedMs);
            var target = line.Line.At(along + ld);
            float tx = target.X - i.RearX, tz = target.Z - i.RearZ;
            var alpha = DriveLine.Wrap(Math.Atan2(tx, tz) - i.Heading);
            var distance = (float)Math.Sqrt(tx * tx + tz * tz);
            var curvature = 2 * Math.Sin(alpha) / Math.Max(1f, distance);
            var steer = (float)(Math.Atan(wheelbase * curvature) * 180 / Math.PI);

            var desired = PlannedSpeed(along, i.SpeedMs);
            var holding = false;
            if (i.StopAlong is float stop)
            {
                var room = stop - (along + frontAhead);
                desired = Math.Min(desired, Approach(0f, room));
                holding = room < 3f;
            }
            if (i.GapAhead is float gap)
            {
                desired = Math.Min(desired, Approach(0f, gap - StopGap));
                holding |= gap < StopGap + 3f;
            }
            if (along + frontAhead >= Length - 1f) desired = 0f;

            var o = new DriveOutput { SteerDegrees = steer, Along = along, Lateral = lateral, Desired = desired, Lookahead = ld, Holding = holding };
            Pedals(ref o, i.SpeedMs, desired, i.Dt);
            return o;
        }

        // The speed the plan allows here: no faster than lets the car slow to each limit ahead at Decel.
        public float PlannedSpeed(float at, float speedMs)
        {
            var desired = top;
            var pts = line.Line.Points;
            var horizon = at + 10f + speedMs * speedMs / (2 * Decel);
            while (firstPoint + 1 < pts.Count && line.Along(firstPoint) < at - 1f) firstPoint++;
            for (var k = firstPoint; k < pts.Count; k++)
            {
                var s = line.Along(k);
                if (s < at - 1f) continue;
                if (s > horizon) break;
                desired = Math.Min(desired, Approach(cornerSpeeds[k], s - at));
            }
            foreach (var (from, to, speed) in slow)
            {
                if (to < at) continue;
                desired = Math.Min(desired, from <= at ? speed : Approach(speed, from - at));
            }
            return desired;
        }

        // The highest speed from which the car slows to `speed` within `metres` at Decel.
        public static float Approach(float speed, float metres) =>
            metres <= 0f ? speed : (float)Math.Sqrt(speed * speed + 2 * Decel * metres);

        private void Pedals(ref DriveOutput o, float speed, float desired, float dt)
        {
            var error = desired - speed;
            if (desired < 0.3f && speed < 1f)
            {
                // Held on the handbrake: NWH's automatic gearbox shifts into reverse on the brake at a standstill.
                o.Handbrake = 1f;
                integral = 0f;
                return;
            }
            if (error >= 0f)
            {
                integral = Math.Min(0.5f, Math.Max(0f, integral + 0.08f * error * dt));
                o.Throttle = Math.Min(1f, Math.Max(0f, 0.25f * error + integral + 0.05f));
            }
            else if (error > -0.7f)
            {
                // Coasting a little too fast.
                integral = Math.Max(0f, integral - 0.1f * dt);
            }
            else
            {
                integral = 0f;
                o.Brake = Math.Min(1f, -0.12f * error);
            }
        }
    }
}
