using System;
using System.Collections.Generic;
using DrivenRides.Core.Routing;

namespace DrivenRides.Core.Drive
{
    public sealed class Corridor
    {
        public const float Accel = 2f;
        public const float Ease = 1.5f;
        public const float StopDecel = 4.5f;
        public const float GiveWayOvershootDecel = 4f;
        public const float HeldStopDecel = 8f;
        public const float StartingMs = 5f;
        public const float SideMargin = 0.2f;
        private const int MaxSegments = 160;

        public readonly struct Segment
        {
            public readonly Box Box;
            public readonly float U0;
            public Segment(Box box, float u0) { Box = box; U0 = u0; }
        }

        // At its full size from the start: corridors are pooled and reused for any car (TrafficDriver), so a list growing
        // would allocate in the middle of a ride.
        public readonly List<Segment> Segments = new List<Segment>(MaxSegments + 1);
        public float Speed, TopSpeed, Length;
        public bool SpeedingUp;
        public float Limit;
        public float MinX, MinZ, MaxX, MaxZ;

        public void Build(LaneGraph graph, in OtherCar car, ILiveFlags flags, float seconds, float beyond, Func<int, int> roadHeld, bool speedingUp = false, float leaderMs = float.MaxValue, bool cruising = false)
        {
            Segments.Clear();
            Speed = car.Speed;
            Length = car.Length;
            var target = car.Target >= 0 && car.Target < graph.Count ? car.Target : -1;
            var limit = (target >= 0 ? graph.SpeedMs(target) : LaneGraph.DefaultSpeedMs) * 1.1f;
            SpeedingUp = speedingUp;
            Limit = limit;
            TopSpeed = Speed < StartingMs && !cruising || speedingUp ? Math.Max(Speed, limit) : Math.Max(Speed, Math.Min(limit, Speed + 1f));
            if (leaderMs < TopSpeed) TopSpeed = Math.Max(Speed, leaderMs + 1f);
            var reach = Math.Min(160f, Far(seconds) + 2f + beyond);
            float hx = (float)Math.Sin(car.Heading), hz = (float)Math.Cos(car.Heading);
            var half = car.Width / 2f + SideMargin;
            float fx = car.X + hx * car.Length / 2f, fz = car.Z + hz * car.Length / 2f;
            float rx = car.X - hx * car.Length / 2f, rz = car.Z - hz * car.Length / 2f;
            MinX = MinZ = float.MaxValue;
            MaxX = MaxZ = float.MinValue;
            Add(Box.Along(rx, rz, fx, fz, half), -car.Length);
            if (target < 0)
            {
                Add(Box.Along(fx, fz, fx + hx * reach, fz + hz * reach, half), 0f);
                return;
            }
            var tp = graph.Position(target);
            var toTarget = (tp.X - fx) * hx + (tp.Z - fz) * hz;
            if (toTarget < 0.5f && Speed * Speed / (2f * HeldStopDecel) <= toTarget + 0.5f && HeldAt(target, flags, roadHeld)) return;
            if ((tp.X - fx) * hx + (tp.Z - fz) * hz < 0.5f)
            {
                // Indexed, not foreach: an interface's enumerator is boxed, garbage every physics step.
                var next = graph.Successors(target);
                for (var k = 0; k < next.Count; k++) Walk(graph, flags, roadHeld, fx, fz, 0f, next[k], reach, half, 0);
                if (next.Count == 0) Add(Box.Along(fx, fz, fx + hx * 3f, fz + hz * 3f, half), 0f);
            }
            else Walk(graph, flags, roadHeld, fx, fz, 0f, target, reach, half, 0);
            if (car.MayChangeLane)
            {
                var sides = graph.Lateral(target);
                for (var k = 0; k < sides.Count; k++) Walk(graph, flags, roadHeld, fx, fz, 0f, sides[k], reach, half, 0);
            }
        }

        private static bool HeldAt(int w, ILiveFlags flags, Func<int, int> roadHeld)
        {
            if (flags.Stop(w)) return true;
            if (!flags.GiveWayEntry(w, out var junction, out var road) || road < 0) return false;
            var held = roadHeld(junction);
            return held >= 0 && held != road;
        }

        private void Walk(LaneGraph graph, ILiveFlags flags, Func<int, int> roadHeld, float x, float z, float u, int w, float reach, float half, int depth)
        {
            while (Segments.Count < MaxSegments && depth < 60)
            {
                var p = graph.Position(w);
                var step = (float)Math.Sqrt((p.X - x) * (p.X - x) + (p.Z - z) * (p.Z - z));
                var end = u + step;
                var stops = false;
                var givesWay = false;
                if (flags.Stop(w)) stops = true;
                else if (flags.GiveWayEntry(w, out var junction, out var road) && road >= 0)
                {
                    var held = roadHeld(junction);
                    stops = givesWay = held >= 0 && held != road;
                }
                if (stops && Speed * Speed / (2f * Math.Max(0.1f, end)) <= StopDecel)
                {
                    if (step > 0.05f) Add(Box.Along(x, z, p.X, p.Z, half), u);
                    return;
                }
                if (givesWay)
                {
                    var past = Speed * Speed / (2f * GiveWayOvershootDecel) - end;
                    if (step > 0.05f) Add(Box.Along(x, z, p.X, p.Z, half), u);
                    if (past > 0f)
                    {
                        var on = graph.Successors(w);
                        for (var k = 0; k < on.Count; k++) Walk(graph, flags, roadHeld, p.X, p.Z, end, on[k], Math.Min(reach, end + past), half, depth + 1);
                    }
                    return;
                }
                if (end >= reach)
                {
                    var f = step > 0f ? (reach - u) / step : 0f;
                    Add(Box.Along(x, z, x + (p.X - x) * f, z + (p.Z - z) * f, half), u);
                    return;
                }
                if (step > 0.05f) Add(Box.Along(x, z, p.X, p.Z, half), u);
                var next = graph.Successors(w);
                if (next.Count == 0) return;
                for (var k = 1; k < next.Count; k++) Walk(graph, flags, roadHeld, p.X, p.Z, end, next[k], reach, half, depth + 1);
                x = p.X;
                z = p.Z;
                u = end;
                w = next[0];
                depth++;
            }
        }

        private void Add(Box box, float u0)
        {
            Segments.Add(new Segment(box, u0));
            MinX = Math.Min(MinX, box.X - box.ExtentX);
            MaxX = Math.Max(MaxX, box.X + box.ExtentX);
            MinZ = Math.Min(MinZ, box.Z - box.ExtentZ);
            MaxZ = Math.Max(MaxZ, box.Z + box.ExtentZ);
        }

        public float Far(float t)
        {
            if (t <= 0f) return 0f;
            var speedUp = Math.Max(0f, (TopSpeed - Speed) / Accel);
            if (t <= speedUp) return Speed * t + Accel * t * t / 2f;
            return Speed * speedUp + Accel * speedUp * speedUp / 2f + TopSpeed * (t - speedUp);
        }

        public float Near(float t)
        {
            if (t <= 0f) return 0f;
            var stop = Speed / Ease;
            if (t >= stop) return Speed * Speed / (2f * Ease);
            return Speed * t - Ease * t * t / 2f;
        }

        public float Reach(float u)
        {
            if (u <= 0f) return 0f;
            var speedUp = Math.Max(0f, (TopSpeed - Speed) / Accel);
            var during = Speed * speedUp + Accel * speedUp * speedUp / 2f;
            if (u <= during) return (float)((-Speed + Math.Sqrt(Speed * Speed + 2 * Accel * u)) / Accel);
            return speedUp + (u - during) / Math.Max(0.1f, TopSpeed);
        }

        public void Clip(float minX, float minZ, float maxX, float maxZ)
        {
            // In place, without a predicate: Decide runs every physics step and must make no garbage.
            var kept = 0;
            for (var i = 0; i < Segments.Count; i++)
            {
                var seg = Segments[i];
                if (seg.Box.X + seg.Box.ExtentX < minX || seg.Box.X - seg.Box.ExtentX > maxX || seg.Box.Z + seg.Box.ExtentZ < minZ || seg.Box.Z - seg.Box.ExtentZ > maxZ) continue;
                Segments[kept++] = seg;
            }
            Segments.RemoveRange(kept, Segments.Count - kept);
        }

        public bool BoundsTouch(in Box b) =>
            b.X + b.ExtentX >= MinX && b.X - b.ExtentX <= MaxX && b.Z + b.ExtentZ >= MinZ && b.Z - b.ExtentZ <= MaxZ;

        public bool Covers(in Box b, out float from, out float to, out float sin, out float cos)
        {
            from = float.MaxValue;
            to = float.MinValue;
            sin = 0f;
            cos = 1f;
            if (!BoundsTouch(b)) return false;
            var hit = false;
            foreach (var s in Segments)
            {
                if (!s.Box.Overlaps(b)) continue;
                var (a, z) = s.Box.SpanOf(b);
                if (!hit) { sin = s.Box.Sin; cos = s.Box.Cos; }
                hit = true;
                from = Math.Min(from, s.U0 + a);
                to = Math.Max(to, s.U0 + z);
            }
            return hit;
        }
    }
}
