using System;
using System.Collections.Generic;
using DrivenRides.Core.Routing;

namespace DrivenRides.Core.Testing
{
    public sealed class Judge
    {
        private readonly CaseGeometry geometry;
        private readonly Fixture fixture;
        private readonly List<Stretch> plain = new List<Stretch>();
        private readonly List<Stretch> exitRoad = new List<Stretch>();
        private readonly JudgeResult result = new JudgeResult();
        private Sample previous;
        private bool hasPrevious, turnPassed, entered;
        private int nextGate, segment;
        private Vec3 stuckPosition;
        private double stuckTime;

        public Judge(LaneGraph graph, CaseGeometry geometry, Fixture fixture)
        {
            this.geometry = geometry;
            this.fixture = fixture;
            // Lane stretches only: an added turn's or a lane change's step is a chord across lanes, not a lane.
            for (int k = 0; k + 1 < fixture.Route.Count; k++)
                if (!graph.IsIntersection(fixture.Route[k]) && !graph.IsIntersection(fixture.Route[k + 1]) && Has(graph.Successors(fixture.Route[k]), fixture.Route[k + 1]))
                    plain.Add(new Stretch(graph, geometry.RoutePolyline[k], geometry.RoutePolyline[k + 1], k) { Min = -1f, Max = Vec3.HorizontalDistance(geometry.RoutePolyline[k], geometry.RoutePolyline[k + 1]) + 1f });
            // The oncoming lanes of the road turned into, from its mouth on: inside the junction a correct left turn
            // sweeps across them, and a U-turn's own approach lanes are that road's oncoming lanes (the far-side check
            // covers U-turns).
            if (fixture.Manoeuvre != CaseKind.Turn) return;
            float length = 0;
            for (int k = fixture.ExitIndex; k + 1 < fixture.Route.Count && length < 25f; k++)
            {
                var stretch = new Stretch(graph, geometry.RoutePolyline[k], geometry.RoutePolyline[k + 1], k);
                stretch.Min = 0;
                stretch.Max = Math.Min(stretch.Length, 25f - length);
                exitRoad.Add(stretch);
                length += stretch.Length;
            }
            if (exitRoad.Count == 0)
            {
                Gate exit = geometry.Gates[1];
                var stretch = new Stretch(graph, exit.Point, exit.Point + new Vec3(exit.DirX * 25f, 0, exit.DirZ * 25f), fixture.ExitIndex);
                stretch.Min = 0;
                exitRoad.Add(stretch);
            }
        }

        public bool Failed { get; private set; }
        public int FailedStep { get; private set; } = -1;
        public bool Finished { get; private set; }

        public void Add(Sample s)
        {
            bool newGate = false;
            // A car loaded on the entry waypoint (no run-up) starts with its front axle just past the gate.
            if (!hasPrevious && nextGate == 0)
            {
                Gate first = geometry.Gates[0];
                float along = first.Along(s.FrontAxle);
                if (fixture.EntryIndex == 0 || (along >= 0 && along <= 12f && Math.Abs(first.Lateral(s.FrontAxle)) <= 4f))
                {
                    nextGate = 1;
                    result.GatesPassed.Add(first.Name);
                    entered = true;
                    result.EntrySpeed = s.SpeedMs;
                    newGate = true;
                }
            }
            if (hasPrevious)
            {
                var crossings = new List<(int Index, float Fraction)>();
                for (int k = nextGate; k < geometry.Gates.Count; k++)
                    if (geometry.Gates[k].Crossing(previous.FrontAxle, s.FrontAxle, out float fraction)) crossings.Add((k, fraction));
                crossings.Sort((a, b) => { int order = a.Fraction.CompareTo(b.Fraction); return order != 0 ? order : a.Index.CompareTo(b.Index); });
                foreach (var crossing in crossings)
                {
                    if (crossing.Index != nextGate)
                    {
                        Fail(s.Step, "missed-gate:" + geometry.Gates[nextGate].Name);
                        continue;
                    }
                    Gate gate = geometry.Gates[nextGate++];
                    result.GatesPassed.Add(gate.Name);
                    newGate = true;
                    if (gate.Name == "entry") { entered = true; result.EntrySpeed = s.SpeedMs; }
                    if (gate.Name == "last")
                    {
                        Finished = true;
                        result.LastGateLateral = gate.Lateral(s.Centre);
                        float yaw = (float)(s.HeadingDegrees * Math.PI / 180);
                        result.LastGateHeadingError = Geometry.Angle((float)Math.Sin(yaw), (float)Math.Cos(yaw), gate.DirX, gate.DirZ);
                        if (Math.Abs(result.LastGateLateral) > 1f) Fail(s.Step, "exit-lane");
                        if (result.LastGateHeadingError > 10f) Fail(s.Step, "exit-heading");
                    }
                }
                if (geometry.TurnGate?.Crossed(previous.FrontAxle, s.FrontAxle) == true) turnPassed = true;
            }
            if (s.NearMiss > 0)
            {
                if (result.NearMiss == 0 || s.NearMiss < result.NearMiss) result.NearMiss = s.NearMiss;
                if (s.NearMiss <= 0.1f) Fail(s.Step, "near-miss");
            }
            Plain(s);
            if (entered && !Finished)
                foreach (Stretch stretch in exitRoad) Intrusion(s, stretch);
            if (fixture.Manoeuvre == CaseKind.UTurn && !turnPassed) FarSide(s);
            if (!hasPrevious || newGate || s.Waiting || previous.Waiting || Vec3.HorizontalDistance(stuckPosition, s.Centre) >= 2f)
            {
                stuckPosition = s.Centre;
                stuckTime = s.Time;
            }
            else if (!Finished && s.Time - stuckTime >= 20) Fail(s.Step, "stuck");
            previous = s;
            hasPrevious = true;
        }

        private void Plain(Sample s)
        {
            var route = geometry.RoutePolyline;
            int best = segment;
            float distance = float.PositiveInfinity;
            bool within = false;
            for (int k = segment; k + 1 < route.Count && k < segment + 3; k++)
            {
                float length = Vec3.HorizontalDistance(route[k], route[k + 1]);
                if (length == 0) continue;
                Geometry.Direction(route[k], route[k + 1], out float dx, out float dz);
                float t = ((s.Centre.X - route[k].X) * dx + (s.Centre.Z - route[k].Z) * dz) / length;
                Vec3 point = route[k] + (route[k + 1] - route[k]) * Math.Max(0, Math.Min(1, t));
                if (Math.Abs(s.Centre.Y - point.Y) > 2f) continue;
                float d = Vec3.HorizontalDistance(point, s.Centre);
                if (d < distance) { distance = d; best = k; within = t >= 0 && t <= 1; }
            }
            if (float.IsPositiveInfinity(distance)) return;
            segment = best;
            foreach (Stretch stretch in plain)
            {
                if (stretch.Index != best) continue;
                // Off either end (behind the route's start, past a stretch's end) the distance says nothing.
                if (within) result.PlainDeviationMax = Math.Max(result.PlainDeviationMax, distance);
                // During a turn only the receiving road is checked: crossing the approach road's oncoming lane is legal.
                if (!entered || Finished || stretch.Index >= fixture.ExitIndex || (fixture.Manoeuvre != CaseKind.Turn && fixture.Manoeuvre != CaseKind.UTurn))
                    Intrusion(s, stretch);
                break;
            }
        }

        private void Intrusion(Sample s, Stretch stretch, bool limitLength = true)
        {
            if (s.Footprint == null) return;
            foreach (Vec3 point in s.Footprint)
            {
                float along = stretch.Along(point);
                if ((limitLength && (along < stretch.Min || along > stretch.Max)) || Math.Abs(point.Y - stretch.Height(along)) > 2f) continue;
                float side = stretch.Side(point);
                float depth = Math.Max(0, Math.Max(stretch.Left - side, side - stretch.Right));
                result.MaxIntrusion = Math.Max(result.MaxIntrusion, depth);
                if (depth > 0.3f)
                {
                    result.IntrusionDetail ??= FormattableString.Invariant(
                        $"step {s.Step}, route segment {stretch.Index}, point ({point.X:0.0}, {point.Z:0.0}) {side:0.00} m across, boundaries {stretch.Left:0.00} / {stretch.Right:0.00}, {along:0.0} m along");
                    Fail(s.Step, "intrusion");
                }
            }
        }

        private void FarSide(Sample s)
        {
            Gate exit = geometry.Gates[1];
            if (s.Footprint == null) return;
            foreach (Vec3 point in s.Footprint)
            {
                float along = exit.Along(point);
                if (along >= -20f && along <= 25f && Math.Abs(exit.Lateral(point)) <= 1.75f && Math.Abs(point.Y - exit.Point.Y) <= 2f)
                    Fail(s.Step, "far-side-early");
            }
        }

        public void Contact(int step, string what)
        {
            result.Incidents.Add(step + ": " + what);
            Fail(step, "contact");
        }
        public void Incident(int step, string what) => result.Incidents.Add(step + ": " + what);
        public void Replanned(string why)
        {
            result.Incidents.Add("replanned: " + why);
            Fail(hasPrevious ? previous.Step : -1, "replanned");
        }
        private void Fail(int step, string reason)
        {
            if (!Failed) { Failed = true; FailedStep = step; }
            if (!result.Reasons.Contains(reason)) result.Reasons.Add(reason);
        }

        private static bool Has(IReadOnlyList<int> list, int value)
        {
            for (int k = 0; k < list.Count; k++) if (list[k] == value) return true;
            return false;
        }

        public JudgeResult Result(bool timedOut) => new JudgeResult
        {
            Verdict = Failed ? "fail" : timedOut || !Finished ? "incomplete" : "pass",
            Reasons = new List<string>(result.Reasons), GatesPassed = new List<string>(result.GatesPassed), Incidents = new List<string>(result.Incidents),
            EntrySpeed = result.EntrySpeed, MaxIntrusion = result.MaxIntrusion, NearMiss = result.NearMiss, PlainDeviationMax = result.PlainDeviationMax,
            LastGateLateral = result.LastGateLateral, LastGateHeadingError = result.LastGateHeadingError, IntrusionDetail = result.IntrusionDetail
        };

        private sealed class Stretch
        {
            private readonly Vec3 start, end;
            private readonly float dx, dz;
            public readonly int Index;
            public readonly float Length;
            public float Min, Max;
            public float Left = float.NegativeInfinity, Right = float.PositiveInfinity;

            public Stretch(LaneGraph graph, Vec3 start, Vec3 end, int index)
            {
                this.start = start;
                this.end = end;
                Index = index;
                Geometry.Direction(start, end, out dx, out dz);
                Length = Vec3.HorizontalDistance(start, end);
                Max = Length;
                float left = float.NegativeInfinity, right = float.PositiveInfinity;
                var same = new List<float> { 0 };
                foreach (int w in graph.Near(start, 12f))
                {
                    Vec3 p = graph.Position(w);
                    if (Math.Abs(p.Y - start.Y) > 1f || !graph.PlainHeading(w, out float x, out float z)) continue;
                    float dot = dx * x + dz * z, side = Side(p);
                    if (dot > 0.5f) same.Add(side);
                    // An oncoming waypoint less than 2.5 m to the side is no lane beside: the game reuses a lane both
                    // ways in places, and a Hamptons lane road is too narrow for any car to keep to its half.
                    else if (dot < -0.5f && Math.Abs(side) >= 2.5f)
                    {
                        if (side < 0) left = Math.Max(left, side);
                        else if (side > 0) right = Math.Min(right, side);
                    }
                }
                float ownLeft = 0, ownRight = 0;
                foreach (float side in same)
                {
                    if (side < ownLeft && side > left) ownLeft = side;
                    if (side > ownRight && side < right) ownRight = side;
                }
                if (!float.IsNegativeInfinity(left)) Left = (left + ownLeft) / 2;
                if (!float.IsPositiveInfinity(right)) Right = (right + ownRight) / 2;
            }
            public float Along(Vec3 p) => (p.X - start.X) * dx + (p.Z - start.Z) * dz;
            public float Side(Vec3 p) => (p.X - start.X) * dz - (p.Z - start.Z) * dx;
            public float Height(float along) => Length == 0 ? start.Y : start.Y + (end.Y - start.Y) * along / Length;
        }
    }
}
