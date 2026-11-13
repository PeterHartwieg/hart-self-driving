using System;
using System.Collections.Generic;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;

namespace DrivenRides.Core.Drive
{
    public struct DriveInput
    {
        public float RearX, RearZ, Heading, SpeedMs, Dt;
        public float? YieldAlong;
        public float WheelDegrees;
        public float? StopAlong;
        public float? GapAhead;
        public float GapSpeed;
        public float? MaxSpeed;
    }

    public struct DriveOutput
    {
        public float SteerDegrees, Throttle, Brake, Handbrake;
        public float Along, Lateral, Desired, Lookahead;
        public bool Holding;
    }

    public sealed class PursuitDriver
    {
        private readonly DriveLine line;
        private readonly float wheelbase, frontAhead, top, trackWidth;
        private readonly float[] cornerSpeeds;
        private readonly List<(float From, float To, float Speed)> slow = new List<(float, float, float)>();
        private float along;
        private float integral;
        public float Integral { get => integral; set => integral = value; }

        public const float Decel = TrafficMargins.Decel;
        public const float HardDecel = 5f;
        public const float StopGap = TrafficMargins.StopGap;
        public const float Lateral = 6f;
        public const float MinCornerMs = 12f / 3.6f;
        public const float LagSeconds = 0.25f;

        public PursuitDriver(DriveLine line, float wheelbase, float frontAhead, float topMs, float trackWidth = 0f)
        {
            this.line = line;
            this.wheelbase = Math.Max(1f, wheelbase);
            this.trackWidth = Math.Max(0f, trackWidth);
            this.frontAhead = frontAhead;
            top = topMs;
            cornerSpeeds = line.CornerSpeeds(topMs, Lateral, 10f);
            for (var k = 0; k < cornerSpeeds.Length; k++) cornerSpeeds[k] = Math.Max(Math.Min(MinCornerMs, topMs), cornerSpeeds[k]);
        }

        public float Along => along;
        public bool FeedForward { get; set; } = true;
        public float Length => line.Line.Length;
        public float FrontAhead => frontAhead;

        public void Slow(float from, float to, float speedMs) => slow.Add((from, to, speedMs));

        public float LookaheadAt(float speedMs) => Math.Max(wheelbase + 1.5f, 1.5f + 0.45f * speedMs);

        public DriveOutput Step(DriveInput i)
        {
            along = line.Line.Project(i.RearX, i.RearZ, along);
            var here = line.Line.At(along);
            float hx = (float)Math.Sin(i.Heading), hz = (float)Math.Cos(i.Heading);
            var lateral = (i.RearX - here.X) * hz - (i.RearZ - here.Z) * hx;

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
            var reference = along + ahead;
            if (FeedForward && Smooth(reference, ld))
                curvature = LineCurvature(reference, ld) + (curvature - ChordCurvature(reference, ld));
            var steer = (float)(Math.Atan(wheelbase * curvature / CarModel.YawGain(wheelbase, trackWidth)) * 180 / Math.PI);

            var desired = PlannedSpeed(along, i.SpeedMs);
            var holding = false;
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
                holding |= gap < StopGap + 3f && i.GapSpeed < LeaderOffMs;
                if (i.GapSpeed < LeaderOffMs) need = Math.Max(need, Need(i.SpeedMs, gap - StopGap));
            }
            if (i.MaxSpeed is float cap) desired = Math.Min(desired, cap);
            if (along + frontAhead >= Length - 1f) desired = 0f;
            var wheel = Math.Max(Math.Abs(i.WheelDegrees), Math.Abs(steer));
            if (wheel > 3f) desired = Math.Min(desired, Math.Max(MinCornerMs, (float)Math.Sqrt(Lateral / CarModel.Curvature(wheel, wheelbase, trackWidth))));

            var o = new DriveOutput { SteerDegrees = steer, Along = along, Lateral = lateral, Desired = desired, Lookahead = ld, Holding = holding };
            Pedals(ref o, i.SpeedMs, desired, i.Dt);
            if (need > Decel && o.Handbrake < 0.5f) { o.Throttle = 0f; o.Brake = Math.Max(o.Brake, Math.Min(1f, need / BrakePerInput)); }
            return o;
        }

        public PursuitDriver Copy()
        {
            var copy = new PursuitDriver(line, wheelbase, frontAhead, top, trackWidth) { FeedForward = FeedForward };
            copy.slow.AddRange(slow);
            return copy;
        }

        public void Resume(float alongNow) => along = alongNow;

        public void StartAt(float x, float z) => along = line.Locate(x, z, 0f, line.Line.Length).Along;

        public void BackTo(float x, float z) => along = line.Locate(x, z, Math.Max(0f, along - 12f), 13f).Along;

        private double LineCurvature(float at, float window)
        {
            var half = Math.Max(1f, window / 2f);
            Vec3 a = line.Line.At(at - half), b = line.Line.At(at), c = line.Line.At(at + half);
            var h1 = Math.Atan2(b.X - a.X, b.Z - a.Z);
            var h2 = Math.Atan2(c.X - b.X, c.Z - b.Z);
            var length = Vec3.HorizontalDistance(a, b) + Vec3.HorizontalDistance(b, c);
            return length > 0.5f ? DriveLine.Wrap(h2 - h1) / (length / 2) : 0.0;
        }

        private bool Smooth(float at, float lookahead)
        {
            foreach (var (from, to) in line.Curves)
                if (at - lookahead / 2f >= from && at + lookahead <= to) return true;
            return false;
        }

        private double ChordCurvature(float at, float lookahead)
        {
            Vec3 p = line.Line.At(at), q = line.Line.At(at + 0.5f), t = line.Line.At(at + lookahead);
            var heading = Math.Atan2(q.X - p.X, q.Z - p.Z);
            float tx = t.X - p.X, tz = t.Z - p.Z;
            var alpha = DriveLine.Wrap(Math.Atan2(tx, tz) - heading);
            return 2 * Math.Sin(alpha) / Math.Max(1f, Math.Sqrt(tx * tx + tz * tz));
        }

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

        public const float FollowReaction = TrafficMargins.FollowReaction;
        public const float FollowBrake = TrafficMargins.FollowBrake;
        public const float LeaderBrake = TrafficMargins.LeaderBrake;

        public static float Follow(float gap, float leaderMs)
        {
            if (leaderMs < LeaderOffMs) return Approach(0f, gap - StopGap);
            var room = gap - StopGap + leaderMs * leaderMs / (2f * LeaderBrake);
            var t = FollowReaction;
            var safe = room <= 0f ? 0f : (float)(FollowBrake * (-t + Math.Sqrt(t * t + 2 * room / FollowBrake)));
            var start = Math.Min(leaderMs + 0.5f, Math.Min(StartMs, (gap - StopGap) / StartHeadway));
            return Math.Max(safe, start);
        }

        public const float LeaderOffMs = 0.3f, StartMs = 5f, StartHeadway = 0.6f;

        // Braking (m/s²) per unit of NWH brake input, roughly: 0.2 slowed the limo at about 3.7 m/s² (T3 trace
        // 20261013-200946-0017); lower than that, so a needed stop brakes harder rather than softer.
        public const float BrakePerInput = 12f;

        private static float Need(float speed, float room) => speed <= 0.3f ? 0f : room <= 0.05f ? HardDecel * 2f : speed * speed / (2f * room);

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
