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
        // Metres along the line short of which the car gives way (YieldAlong), or null.
        public float? YieldAlong;
        // The wheel's angle now (degrees, positive to the right).
        public float WheelDegrees;
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
        public const float MinCornerMs = 12f / 3.6f;
        public const float LagSeconds = 0.25f;

        // frontAhead: the car's front bumper, metres ahead of the rear axle.
        public PursuitDriver(DriveLine line, float wheelbase, float frontAhead, float topMs)
        {
            this.line = line;
            this.wheelbase = Math.Max(1f, wheelbase);
            this.frontAhead = frontAhead;
            top = topMs;
            cornerSpeeds = line.CornerSpeeds(topMs, Lateral, 10f);
            // A U-turn's vias make sharp kinks in the line that read as a 2 to 3 m radius; no driver goes round a
            // corner at walking pace (Peter, NWH spike: the first U-turn was incredibly slow).
            for (var k = 0; k < cornerSpeeds.Length; k++) cornerSpeeds[k] = Math.Max(Math.Min(MinCornerMs, topMs), cornerSpeeds[k]);
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

            // The wheel reaches a new angle only after a moment (NWH smooths it and turns it at a limited rate), so the
            // driver steers for where the car will be by then: on along its present curve for LagSeconds.
            var yawRate = i.SpeedMs * Math.Tan(i.WheelDegrees * Math.PI / 180) / wheelbase;
            var turned = yawRate * LagSeconds;
            var ahead = i.SpeedMs * LagSeconds;
            var px = i.RearX + ahead * (float)Math.Sin(i.Heading + turned / 2);
            var pz = i.RearZ + ahead * (float)Math.Cos(i.Heading + turned / 2);
            var ph = i.Heading + turned;
            var ld = LookaheadAt(i.SpeedMs);
            var target = line.Line.At(along + ahead + ld);
            float tx = target.X - px, tz = target.Z - pz;
            var alpha = DriveLine.Wrap(Math.Atan2(tx, tz) - ph);
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
            if (i.YieldAlong is float give)
            {
                var room = give - (along + frontAhead);
                desired = Math.Min(desired, Approach(0f, room));
                holding |= room < 3f;
            }
            if (i.GapAhead is float gap)
            {
                desired = Math.Min(desired, Approach(0f, gap - StopGap));
                holding |= gap < StopGap + 3f;
            }
            if (along + frontAhead >= Length - 1f) desired = 0f;
            // No faster than the corner the wheel is turning now, nor the one it is asked for: the car accelerated
            // out of corners with the wheel still at lock and ran wide (Peter, NWH spike: overshooting the corner).
            var wheel = Math.Max(Math.Abs(i.WheelDegrees), Math.Abs(steer));
            if (wheel > 3f) desired = Math.Min(desired, Math.Max(MinCornerMs, (float)Math.Sqrt(Lateral * wheelbase / Math.Tan(wheel * Math.PI / 180))));

            var o = new DriveOutput { SteerDegrees = steer, Along = along, Lateral = lateral, Desired = desired, Lookahead = ld, Holding = holding };
            Pedals(ref o, i.SpeedMs, desired, i.Dt);
            return o;
        }

        // Giving way (Peter, NWH spike: the car pulled out across a traffic car onto Broadway). Another car moving
        // at over 1.5 m/s that will cross the next 25 m of the line within 3 s, before this car has gone past that
        // point, is given way to: the result is metres along the line where the front stops, 2 m short of where they
        // would meet. A car on the line behind this one is following it, not crossing.
        public float? YieldAlong(IEnumerable<(float X, float Z, float Vx, float Vz)> others, float speedMs)
        {
            var front = along + frontAhead;
            float? first = null;
            foreach (var (x, z, vx, vz) in others)
            {
                var speed = (float)Math.Sqrt(vx * vx + vz * vz);
                if (speed < 1.5f) continue;
                var (onAlong, onSide) = line.Locate(x, z, Math.Max(0f, along - 15f), 45f);
                if (Math.Abs(onSide) < 2.2f && onAlong < front + 1f) continue;
                for (var t = 0.25f; t <= 3f; t += 0.25f)
                {
                    float px = x + vx * t, pz = z + vz * t;
                    var (hitAlong, hitSide) = line.Locate(px, pz, front - 2f, 27f);
                    if (Math.Abs(hitSide) > 2.2f || hitAlong < front - 1f || hitAlong > front + 25f) continue;
                    // A meeting point far enough ahead that the other car is through it 1.5 s before this one (at no
                    // less than 3 m/s) is no conflict.
                    var ours = (hitAlong - front) / Math.Max(3f, speedMs);
                    if (ours > t + 1.5f) continue;
                    // Wait short of the turn that leads there, not of the meeting point: in a tight turn the body swings
                    // into the other lanes well before the line crosses them (Peter, NWH spike: a U-turn into an
                    // oncoming car). A car already in the turn finishes it unless the meeting point is right ahead.
                    var turnStart = TurnStart(front, hitAlong);
                    float stopAt;
                    if (turnStart > front + 0.5f) stopAt = Math.Min(hitAlong - 2f, turnStart);
                    else if (hitAlong - front < 4f) stopAt = front;
                    else break;
                    if (first == null || stopAt < first) first = stopAt;
                    break;
                }
            }
            return first;
        }

        // Where the line first bends 15 degrees away from its heading at `from`, before `to`; `to` if it doesn't.
        private float TurnStart(float from, float to)
        {
            var a = line.Line.At(from);
            var b = line.Line.At(from + 1f);
            var h0 = Math.Atan2(b.X - a.X, b.Z - a.Z);
            for (var s = from + 1f; s < to; s += 0.5f)
            {
                var p = line.Line.At(s);
                var q = line.Line.At(s + 1f);
                if (Math.Abs(DriveLine.Wrap(Math.Atan2(q.X - p.X, q.Z - p.Z) - h0)) > 15 * Math.PI / 180) return Math.Max(from, s - 1f);
            }
            return to;
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
