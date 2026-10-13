using System;
using System.Collections.Generic;
using DrivenRides.Core.Routing;

namespace DrivenRides.Core.Testing
{
    public sealed class Judge
    {
        private readonly CaseGeometry geometry;
        private readonly Fixture fixture;
        private readonly List<LaneStretch> plain = new List<LaneStretch>();
        private readonly List<LaneStretch> exitRoad = new List<LaneStretch>();
        private readonly JudgeResult result = new JudgeResult();
        private readonly Pavement pavement;
        private readonly LaneGraph graph;

        // Whether a point is pedestrian ground (in game: the NavMesh under the pedestrians' filter, NwhMeasure.Ground:
        // road and pavement are one flat collider, only the pedestrians' NavMesh tells them apart). Set, it replaces
        // the geometric kerb estimate (Pavement), which stays for T1 offline.
        public Func<Vec3, bool>? PavementAt { get; set; }
        // Raised with the step and what, when the car is found on the pavement (the rig records it in its samples).
        public Action<int, string>? OnPavement { get; set; }
        // The footprint's corners are pulled this far inward before they are sampled, as the pedestrian ground the
        // game marks runs up to a lane's edge in places (a promenade by the pier, measured 0.5 m from a lane centre).
        public const float PavementInset = 0.3f;
        private Sample previous;
        private bool hasPrevious, turnPassed, entered;
        private int nextGate, segment;
        private Vec3 stuckPosition;
        private double stuckTime;

        public Judge(LaneGraph graph, CaseGeometry geometry, Fixture fixture)
        {
            this.geometry = geometry;
            this.fixture = fixture;
            this.graph = graph;
            // The pavement past the manoeuvre (the whole route for a ride or a ramp).
            pavement = new Pavement(graph, fixture.Route, fixture.Manoeuvre == CaseKind.Turn || fixture.Manoeuvre == CaseKind.UTurn ? fixture.EntryIndex + 1 : 0);
            // Lane stretches only: an added turn's or a lane change's step is a chord across lanes, not a lane.
            for (int k = 0; k + 1 < fixture.Route.Count; k++)
                if (!graph.IsIntersection(fixture.Route[k]) && !graph.IsIntersection(fixture.Route[k + 1]) && Has(graph.Successors(fixture.Route[k]), fixture.Route[k + 1]))
                    plain.Add(new LaneStretch(graph, geometry.RoutePolyline[k], geometry.RoutePolyline[k + 1], k) { Min = -1f, Max = Vec3.HorizontalDistance(geometry.RoutePolyline[k], geometry.RoutePolyline[k + 1]) + 1f });
            // The oncoming lanes of the road turned into, from its mouth on: inside the junction a correct left turn
            // sweeps across them, and a U-turn's own approach lanes are that road's oncoming lanes (the far-side check
            // covers U-turns).
            if (fixture.Manoeuvre != CaseKind.Turn) return;
            float length = 0;
            for (int k = fixture.ExitIndex; k + 1 < fixture.Route.Count && length < 25f; k++)
            {
                var stretch = new LaneStretch(graph, geometry.RoutePolyline[k], geometry.RoutePolyline[k + 1], k);
                stretch.Min = 0;
                stretch.Max = Math.Min(stretch.Length, 25f - length);
                exitRoad.Add(stretch);
                length += stretch.Length;
            }
            if (exitRoad.Count == 0)
            {
                Gate exit = geometry.Gates[1];
                var stretch = new LaneStretch(graph, exit.Point, exit.Point + new Vec3(exit.DirX * 25f, 0, exit.DirZ * 25f), fixture.ExitIndex);
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
            if (!Finished && s.Footprint != null && !result.Reasons.Contains("pavement")
                && (PavementAt != null ? OnPedestrianGround(s) : pavement.Past(s.Footprint)) is string onPavement)
                OnThePavement(s.Step, onPavement);
            if (entered && !Finished)
                foreach (LaneStretch stretch in exitRoad) Intrusion(s, stretch);
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
            foreach (LaneStretch stretch in plain)
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

        private void Intrusion(Sample s, LaneStretch stretch, bool limitLength = true)
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

        // A pavement finding recorded by the game (a samples file's P line), for rejudging.
        public void OnThePavement(int step, string what)
        {
            if (result.Reasons.Contains("pavement")) return;
            result.Incidents.Add(step + ": " + what);
            Fail(step, "pavement");
            OnPavement?.Invoke(step, what);
        }

        // A footprint corner, pulled PavementInset inward, on pedestrian ground and not within half a lane of any
        // lane (crosswalks are pedestrian ground too, and lie on the lanes).
        private string? OnPedestrianGround(Sample s)
        {
            for (int k = 0; k < 4 && k < s.Footprint.Length; k++)
            {
                Vec3 corner = s.Footprint[k];
                float dx = s.Centre.X - corner.X, dz = s.Centre.Z - corner.Z, length = (float)Math.Sqrt(dx * dx + dz * dz);
                if (length > PavementInset) corner = new Vec3(corner.X + dx / length * PavementInset, corner.Y, corner.Z + dz / length * PavementInset);
                if (!PavementAt!(corner) || Testing.Pavement.OnALane(graph, corner)) continue;
                return FormattableString.Invariant($"pavement: a corner on pedestrian ground at ({corner.X:0.0}, {corner.Z:0.0})");
            }
            return null;
        }

        public void Contact(int step, string what)
        {
            result.Incidents.Add(step + ": " + what);
            Fail(step, "contact");
        }
        public void Incident(int step, string what) => result.Incidents.Add(step + ": " + what);
        // A failure found outside the judge (the feasibility check's carriageway and lane rules).
        public void Reject(int step, string reason, string what)
        {
            result.Incidents.Add(step + ": " + what);
            Fail(step, reason);
        }
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
    }
}
