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
        // Metres along the line where the front gives way to traffic (TrafficDriver), or null.
        public float? YieldAlong;
        // The wheel's angle now (degrees, positive to the right).
        public float WheelDegrees;
        // Metres along the line where the car's front must stop (a red light), or null.
        public float? StopAlong;
        // Metres from the car's front to something in its way (a car ahead), or null, and how fast that moves on
        // along the car's way (m/s; 0 for anything standing, people and parked cars).
        public float? GapAhead;
        public float GapSpeed;
        // A speed cap from the traffic rules (creeping past a car that waits for this one), m/s, or null.
        public float? MaxSpeed;
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
        private readonly float wheelbase, frontAhead, top, trackWidth;
        private readonly float[] cornerSpeeds;
        private readonly List<(float From, float To, float Speed)> slow = new List<(float, float, float)>();
        private float along;
        private float integral;
        // The pedals' integral term: a replay starting mid-drive restores it with the rest (review round 2, U3).
        public float Integral { get => integral; set => integral = value; }

        // Comfortable braking and the limit past which a red is driven through (amber), m/s².
        public const float Decel = TrafficMargins.Decel;
        public const float HardDecel = 5f;
        public const float StopGap = TrafficMargins.StopGap;
        // Sideways acceleration in a corner: 4 m/s² takes a 6.5 m arc at 18 km/h and a 20 m one at 32 (Peter, NWH
        // spike: larger turns faster; at 2.5 every corner felt slow; 14 October: faster turns, as a confident player
        // drives). The rig's traces hold the line to 0.3 m at the median and 0.9 m at the 95th percentile up to 4 m/s²;
        // past it the error grows (3.6 m at the 95th percentile at 4 to 4.5).
        public const float Lateral = 4f;
        public const float MinCornerMs = 12f / 3.6f;
        public const float LagSeconds = 0.25f;

        // frontAhead: the car's front bumper, metres ahead of the rear axle. trackWidth: the front track, for NWH's
        // Ackermann (CarModel.TrackWidth): the limo turns 0.8 as sharply as a bicycle with the same wheel angle, and a
        // driver steering for a bicycle ran 0.5 m wide on every arc (tools/calibrate, rig session 20261013-083259).
        public PursuitDriver(DriveLine line, float wheelbase, float frontAhead, float topMs, float trackWidth = 0f)
        {
            this.line = line;
            this.wheelbase = Math.Max(1f, wheelbase);
            this.trackWidth = Math.Max(0f, trackWidth);
            this.frontAhead = frontAhead;
            top = topMs;
            cornerSpeeds = line.CornerSpeeds(topMs, Lateral, 10f);
            // A U-turn's vias make sharp kinks in the line that read as a 2 to 3 m radius; no driver goes round a
            // corner at walking pace (Peter, NWH spike: the first U-turn was incredibly slow).
            for (var k = 0; k < cornerSpeeds.Length; k++) cornerSpeeds[k] = Math.Max(Math.Min(MinCornerMs, topMs), cornerSpeeds[k]);
        }

        public float Along => along;
        // Steering for a fitted curve's own curvature (on since 0.1.65); tools/calibrate turns it off to replay runs
        // driven before.
        public bool FeedForward { get; set; } = true;
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
            var yawRate = i.SpeedMs * CarModel.Curvature(i.WheelDegrees, wheelbase, trackWidth);
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
            // Feed-forward: the line's own curvature where the car will be once the wheel gets there, with pursuit only
            // for the error. Pursuit alone unwinds a lookahead early where a curve ends and runs wide where it starts;
            // out of a U-turn that was an overshoot and a swing back (Peter, T2). Pure pursuit from a car that is
            // exactly on the line commands ChordCurvature; what it commands beyond that is the correction.
            var reference = along + ahead;
            if (FeedForward && Smooth(reference, ld))
                curvature = LineCurvature(reference, ld) + (curvature - ChordCurvature(reference, ld));
            var steer = (float)(Math.Atan(wheelbase * curvature / CarModel.YawGain(wheelbase, trackWidth)) * 180 / Math.PI);

            var desired = PlannedSpeed(along, i.SpeedMs);
            var holding = false;
            // The braking a stop ahead needs, m/s²: past Decel the brake goes on in proportion, not only by the speed
            // error (T3 20261013-200946-0017: a red seen 6 m off at 7 m/s took 4.5 m/s²; the error alone gave 3.7).
            var need = 0f;
            if (i.StopAlong is float stop)
            {
                var room = stop - (along + frontAhead);
                desired = Math.Min(desired, Approach(0f, room));
                holding = room < 3f;
                need = Math.Max(need, Need(i.SpeedMs, room));
            }
            if (i.YieldAlong is float give)
            {
                var room = give - (along + frontAhead);
                desired = Math.Min(desired, Approach(0f, room));
                holding |= room < 3f;
                need = Math.Max(need, Need(i.SpeedMs, room));
            }
            if (i.GapAhead is float gap)
            {
                desired = Math.Min(desired, Follow(gap, i.GapSpeed));
                holding |= gap < StopGap + 3f && i.GapSpeed < 0.5f;
            }
            if (i.MaxSpeed is float cap) desired = Math.Min(desired, cap);
            if (along + frontAhead >= Length - 1f) desired = 0f;
            // No faster than the corner the wheel is turning now, nor the one it is asked for: the car accelerated
            // out of corners with the wheel still at lock and ran wide (Peter, NWH spike: overshooting the corner).
            var wheel = Math.Max(Math.Abs(i.WheelDegrees), Math.Abs(steer));
            if (wheel > 3f) desired = Math.Min(desired, Math.Max(MinCornerMs, (float)Math.Sqrt(Lateral / CarModel.Curvature(wheel, wheelbase, trackWidth))));

            var o = new DriveOutput { SteerDegrees = steer, Along = along, Lateral = lateral, Desired = desired, Lookahead = ld, Holding = holding };
            Pedals(ref o, i.SpeedMs, desired, i.Dt);
            if (need > Decel && o.Handbrake < 0.5f) { o.Throttle = 0f; o.Brake = Math.Max(o.Brake, Math.Min(1f, need / BrakePerInput)); }
            return o;
        }

        // A driver like this one, on the same line with the same plan, for foreseeing where the car goes
        // (TrafficDriver's near field); it keeps its own place on the line.
        public PursuitDriver Copy()
        {
            var copy = new PursuitDriver(line, wheelbase, frontAhead, top, trackWidth) { FeedForward = FeedForward };
            copy.slow.AddRange(slow);
            return copy;
        }

        // Driving on from a known place on the line (metres along it, the rear axle's).
        public void Resume(float alongNow) => along = alongNow;

        // A car that starts part way along its line (a dumped traffic scenario): where its rear axle is.
        public void StartAt(float x, float z) => along = line.Locate(x, z, 0f, line.Line.Length).Along;

        // After backing off: where the rear axle is now, searched up to 12 m back from the last position.
        public void BackTo(float x, float z) => along = line.Locate(x, z, Math.Max(0f, along - 12f), 13f).Along;

        // The line's curvature at a distance along it (right positive), over the lookahead: a fitted curve's own, and
        // a kink (a via's corner) spread over the stretch pursuit would round it in.
        private double LineCurvature(float at, float window)
        {
            var half = Math.Max(1f, window / 2f);
            Vec3 a = line.Line.At(at - half), b = line.Line.At(at), c = line.Line.At(at + half);
            var h1 = Math.Atan2(b.X - a.X, b.Z - a.Z);
            var h2 = Math.Atan2(c.X - b.X, c.Z - b.Z);
            var length = Vec3.HorizontalDistance(a, b) + Vec3.HorizontalDistance(b, c);
            return length > 0.5f ? DriveLine.Wrap(h2 - h1) / (length / 2) : 0.0;
        }

        // Whether the stretch from half a lookahead behind the reference to a lookahead past it lies on a fitted arc or
        // U-turn curve (DriveLine.Curves), whose curvature is the line's design. Elsewhere (Gley's junction curves,
        // a via's corner) pursuit rounds the line on its own, as before.
        private bool Smooth(float at, float lookahead)
        {
            foreach (var (from, to) in line.Curves)
                if (at - lookahead / 2f >= from && at + lookahead <= to) return true;
            return false;
        }

        // What pure pursuit commands from the line itself at a distance along it, aiming lookahead further on.
        private double ChordCurvature(float at, float lookahead)
        {
            Vec3 p = line.Line.At(at), q = line.Line.At(at + 0.5f), t = line.Line.At(at + lookahead);
            var heading = Math.Atan2(q.X - p.X, q.Z - p.Z);
            float tx = t.X - p.X, tz = t.Z - p.Z;
            var alpha = DriveLine.Wrap(Math.Atan2(tx, tz) - heading);
            return 2 * Math.Sin(alpha) / Math.Max(1f, Math.Sqrt(tx * tx + tz * tz));
        }

        // The speed the plan allows at `at` metres along: no faster than lets the car slow to each limit ahead at
        // Decel. It keeps no state, so the traffic sweep can ask it about any point ahead.
        public float PlannedSpeed(float at, float speedMs)
        {
            var desired = top;
            var pts = line.Line.Points;
            var horizon = at + 10f + speedMs * speedMs / (2 * Decel);
            for (var k = line.FirstPointFrom(at - 1f); k < pts.Count; k++)
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

        // The plan's speed at `at` with every limit ahead counted, however far: at most PlannedSpeed's, and the same
        // wherever that is below what the car reaches from speedMs within the next metre (PlannedSpeed looks only
        // 10 m past its stopping distance, and a limit further on asks for more than that). For the traffic sweep, which
        // asks it at every pose: the nearest limit ahead is the one with the least speed² + 2 Decel × distance, found
        // once per line (review round 10: the sweep's look ahead was most of its time).
        public float PlannedSpeedOnward(float at)
        {
            var desired = top;
            var pts = line.Line.Points;
            if (onward == null)
            {
                onward = new int[pts.Count];
                var best = -1;
                var bestKey = double.MaxValue;
                for (var j = pts.Count - 1; j >= 0; j--)
                {
                    var key = (double)cornerSpeeds[j] * cornerSpeeds[j] + 2.0 * Decel * line.Along(j);
                    if (key <= bestKey) { bestKey = key; best = j; }
                    onward[j] = best;
                }
            }
            var k = line.FirstPointFrom(at - 1f);
            for (; k < pts.Count; k++)
            {
                var s = line.Along(k);
                if (s < at - 1f) continue;
                if (s - at > 0f) break;
                desired = Math.Min(desired, cornerSpeeds[k]);
            }
            if (k < pts.Count)
            {
                var j = onward[k];
                desired = Math.Min(desired, Approach(cornerSpeeds[j], line.Along(j) - at));
            }
            foreach (var (from, to, speed) in slow)
            {
                if (to < at) continue;
                desired = Math.Min(desired, from <= at ? speed : Approach(speed, from - at));
            }
            return desired;
        }

        private int[]? onward;

        // Following: the highest speed from which the car, FollowReaction seconds after the car ahead starts braking
        // at LeaderBrake, stops StopGap behind where that car stops, braking at FollowBrake. A car that stands is
        // the plain stop at Decel as before. Treating every car ahead as standing kept the ride car 2.3 to 2.6 s
        // behind normal traffic and crawling (T3 20261013-191420: "very slow, even on straights"); this settles at
        // about 1.3 s behind a car at 10 m/s.
        public const float FollowReaction = TrafficMargins.FollowReaction;
        public const float FollowBrake = TrafficMargins.FollowBrake;
        public const float LeaderBrake = TrafficMargins.LeaderBrake;

        public static float Follow(float gap, float leaderMs)
        {
            if (leaderMs < 0.5f) return Approach(0f, gap - StopGap);
            var room = gap - StopGap + leaderMs * leaderMs / (2f * LeaderBrake);
            if (room <= 0f) return 0f;
            var t = FollowReaction;
            return (float)(FollowBrake * (-t + Math.Sqrt(t * t + 2 * room / FollowBrake)));
        }

        // Braking (m/s²) per unit of NWH brake input, roughly: 0.2 slowed the limo at about 3.7 m/s² (T3 trace
        // 20261013-200946-0017); lower than that, so a needed stop brakes harder rather than softer.
        public const float BrakePerInput = 12f;

        private static float Need(float speed, float room) => speed <= 0.3f ? 0f : room <= 0.05f ? HardDecel * 2f : speed * speed / (2f * room);

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
