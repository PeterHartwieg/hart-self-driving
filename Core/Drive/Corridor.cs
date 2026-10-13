using System;
using System.Collections.Generic;
using DrivenRides.Core.Routing;

namespace DrivenRides.Core.Drive
{
    // Layer 2's picture of one moving traffic car: the ground its body will cover, from its rear now along the lane to
    // its target waypoint and on, and when (docs/implementation-plan.md, "The four layers"). Where it hasn't chosen
    // yet (a waypoint with several successors) every way is in it. It ends where the car will stop: a waypoint with
    // `stop` set that it can still stop for, or the give-way line of another road at a junction the ride car holds.
    // Distances `u` are metres along the corridor from the car's front now (its rear is at -Length).
    public sealed class Corridor
    {
        // A traffic car's way of speeding up and of slowing down. Gley's cars take about 10 s to their top speed (1.4 m/s²
        // at 50 km/h); 2 m/s² leaves room for the quicker ones (T0's cars speed up at 2).
        public const float Accel = 2f;
        public const float Ease = 1.5f;
        // A traffic car stops for a `stop` waypoint it can stop for at this braking or less.
        public const float StopDecel = 4.5f;
        // Below this it is still pulling away.
        public const float StartingMs = 5f;
        public const float SideMargin = 0.2f;
        private const int MaxSegments = 160;

        public readonly struct Segment
        {
            public readonly Box Box;
            public readonly float U0;
            public Segment(Box box, float u0) { Box = box; U0 = u0; }
        }

        public readonly List<Segment> Segments = new List<Segment>();
        public float Speed, TopSpeed, Length;
        public bool SpeedingUp;
        public float MinX, MinZ, MaxX, MaxZ;

        // Builds the corridor of `car` as far as it can get in `seconds` and `beyond` metres more, from the lane graph
        // and Gley's live flags. The extra stretch is the ride car's sweep: a car coming the other way down the same
        // lane must be seen there before the ride car goes in, not only where it can be within the time. roadHeld: the
        // road the ride car holds at a junction, or -1 (JunctionRules.RoadHeld).
        // speedingUp: the car has been gaining speed (TrafficDriver watches each car's speed over time): it goes on up
        // to the road's limit, as a car that is pulling away does, rather than keeping its pace.
        public void Build(LaneGraph graph, in OtherCar car, ILiveFlags flags, float seconds, float beyond, Func<int, int> roadHeld, bool speedingUp = false)
        {
            Segments.Clear();
            Speed = car.Speed;
            Length = car.Length;
            var target = car.Target >= 0 && car.Target < graph.Count ? car.Target : -1;
            // A car pulling away goes up to the road's limit; one already moving keeps its pace (Gley gives each
            // car its own top speed below the limit, VehicleComponent.maxSpeed), give or take a little.
            var limit = (target >= 0 ? graph.SpeedMs(target) : LaneGraph.DefaultSpeedMs) * 1.1f;
            SpeedingUp = speedingUp;
            TopSpeed = Speed < StartingMs || speedingUp ? Math.Max(Speed, limit) : Math.Max(Speed, Math.Min(limit, Speed + 1f));
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
                // No target known: straight on along its heading.
                Add(Box.Along(fx, fz, fx + hx * reach, fz + hz * reach, half), 0f);
                return;
            }
            // A target the front has already reached is passed: Gley moves on from it.
            var tp = graph.Position(target);
            if ((tp.X - fx) * hx + (tp.Z - fz) * hz < 0.5f)
            {
                foreach (var next in graph.Successors(target)) Walk(graph, flags, roadHeld, fx, fz, 0f, next, reach, half, 0);
                if (graph.Successors(target).Count == 0) Add(Box.Along(fx, fz, fx + hx * 3f, fz + hz * 3f, half), 0f);
            }
            else Walk(graph, flags, roadHeld, fx, fz, 0f, target, reach, half, 0);
            // A car about to overtake: also the lanes beside its target (Gley's otherLanes), as it would move over.
            if (car.MayChangeLane)
                foreach (var side in graph.Lateral(target)) Walk(graph, flags, roadHeld, fx, fz, 0f, side, reach, half, 0);
        }

        private void Walk(LaneGraph graph, ILiveFlags flags, Func<int, int> roadHeld, float x, float z, float u, int w, float reach, float half, int depth)
        {
            while (Segments.Count < MaxSegments && depth < 60)
            {
                var p = graph.Position(w);
                var step = (float)Math.Sqrt((p.X - x) * (p.X - x) + (p.Z - z) * (p.Z - z));
                var end = u + step;
                // Where it will stop: it stops with its front at the line, if it can at StopDecel.
                var stops = false;
                if (flags.Stop(w)) stops = true;
                else if (flags.GiveWayEntry(w, out var junction, out var road) && road >= 0)
                {
                    var held = roadHeld(junction);
                    stops = held >= 0 && held != road;
                }
                if (stops && Speed * Speed / (2f * Math.Max(0.1f, end)) <= StopDecel)
                {
                    if (step > 0.05f) Add(Box.Along(x, z, p.X, p.Z, half), u);
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

        // How far the front gets in t seconds at the most (speeding up to TopSpeed) and at the least (easing off).
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

        // The earliest time the front reaches u.
        public float Reach(float u)
        {
            if (u <= 0f) return 0f;
            var speedUp = Math.Max(0f, (TopSpeed - Speed) / Accel);
            var during = Speed * speedUp + Accel * speedUp * speedUp / 2f;
            if (u <= during) return (float)((-Speed + Math.Sqrt(Speed * Speed + 2 * Accel * u)) / Accel);
            return speedUp + (u - during) / Math.Max(0.1f, TopSpeed);
        }

        // Drops the segments outside an area (the ride car's sweep), so each pose tests only the ones near it.
        public void Clip(float minX, float minZ, float maxX, float maxZ)
        {
            Segments.RemoveAll(seg => seg.Box.X + seg.Box.ExtentX < minX || seg.Box.X - seg.Box.ExtentX > maxX || seg.Box.Z + seg.Box.ExtentZ < minZ || seg.Box.Z - seg.Box.ExtentZ > maxZ);
        }

        public bool BoundsTouch(in Box b) =>
            b.X + b.ExtentX >= MinX && b.X - b.ExtentX <= MaxX && b.Z + b.ExtentZ >= MinZ && b.Z - b.ExtentZ <= MaxZ;

        // Where along the corridor a box lies on it: the lowest and highest u it covers, and the heading of the first
        // segment it touches. False if it is clear of the corridor.
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
