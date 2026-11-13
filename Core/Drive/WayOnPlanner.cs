using System;
using System.Collections.Generic;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;

namespace DrivenRides.Core.Drive
{
    public enum BlockCause { Manoeuvre, InLane, OnTheWay }

    public sealed class PlannedRoute
    {
        public Route? Route;
        public DrivePlan? Plan;
        public string Summary = "";
    }

    public interface IWayOnWorld
    {
        LaneGraph Graph { get; }
        Feasibility Check { get; }
        IEnumerable<object?> PlanRoute(int start, IReadOnlyList<Goal> goals, int vehicleType, WayOnJob? job, PlannedRoute into);
        IEnumerable<object?> LanesAhead(IReadOnlyList<int> path, int position, float metres, Vec3 near, List<(int From, int To, string? What, float At)> swept);
        (float From, float To, float Offset)? ShiftFor(int from, int to);
        void Block(int from, int to, BlockCause cause);
        bool IsBlocked(int from, int to);
        void Note(string text);
    }

    public sealed class WayOnPlanner
    {
        public const float AheadMetres = 150f;
        public const float LoadedMetres = 70f;
        public const int MaxTries = 2;

        private readonly IWayOnWorld world;
        public Func<double>? ClockMs { get; set; }

        public WayOnPlanner(IWayOnWorld world) { this.world = world; }

        public sealed class Watch
        {
            public DrivePlan? Plan { get; private set; }
            public readonly HashSet<(int, int)> Rechecked = new HashSet<(int, int)>();
            public readonly Dictionary<(int, int), Verdict> Baseline = new Dictionary<(int, int), Verdict>();
            public readonly Dictionary<string, int> Tries = new Dictionary<string, int>();
            public readonly Dictionary<string, Change> Passed = new Dictionary<string, Change>();
            public readonly Dictionary<string, Change> Holds = new Dictionary<string, Change>();

            public void Installed(DrivePlan plan)
            {
                Plan = plan;
                Baseline.Clear();
                Tries.Clear();
                Passed.Clear();
                Holds.Clear();
            }
        }

        public sealed class Change
        {
            public string Kind = "", What = "";
            public int From, To;
            public float Offset;
            public (float From, float To, float Offset)? Shift;
            public Verdict? Shape;
            public bool? Passes;
            public string Failed = "";
            public bool Blocked => Kind == "blocked";
            public string Key => $"{Kind} {From}->{To}";

            public bool SameValue(Change other) =>
                Kind == other.Kind && (Kind == "shift" ? SameShift(Shift, other.Shift)
                    : Kind == "shape" ? Shape != null && other.Shape != null && Shape.SameShape(other.Shape) : true);
        }

        public WayOnJob RoundBlock(IReadOnlyList<int> route, IReadOnlyList<int> path, int position, string what, Func<CarPose> pose,
            IReadOnlyList<Goal> goals, int vehicleType)
        {
            var routeNow = new List<int>(route);
            var pathNow = new List<int>(path);
            return new WayOnJob("replanned", position, job => RoundBlockWork(job, routeNow, pathNow, position, what, pose, goals, vehicleType), ClockMs);
        }

        private IEnumerable<object?> RoundBlockWork(WayOnJob job, List<int> route, List<int> path, int position, string what,
            Func<CarPose> pose, IReadOnlyList<Goal> goals, int vehicleType)
        {
            var r = RouteStepAt(route, path, position);
            if (r < 0) { job.Note = $"blocked by {what}; the car is on no step of its route"; yield break; }
            world.Block(route[r], route[r + 1], BlockCause.OnTheWay);
            foreach (var y in PlanFromWork(job, route, path, position, pose, goals, vehicleType, $"step {route[r]} -> {route[r + 1]} blocked by {what}", null))
                yield return y;
        }

        private IEnumerable<object?> PlanFromWork(WayOnJob job, List<int> route, List<int> path, int position,
            Func<CarPose> pose, IReadOnlyList<Goal> goals, int vehicleType, string why, DrivePlan? current)
        {
            var graph = world.Graph;
            var check = world.Check;
            var r = RouteStepAt(route, path, position);
            if (r < 0) { job.Note = why + "; the car is on no step of its route"; yield break; }
            int from = route[r], to = route[r + 1];
            var starts = new List<int> { from };
            foreach (var w in new[] { from, to })
                foreach (var beside in graph.Lateral(w)) if (!starts.Contains(beside)) starts.Add(beside);
            for (var k = r + 1; k < route.Count && k <= r + 2; k++) if (!starts.Contains(route[k])) starts.Add(route[k]);
            var currentLine = current?.Line(graph);
            var tried = new List<string>();
            foreach (var start in starts)
            {
                var found = new PlannedRoute();
                foreach (var y in world.PlanRoute(start, goals, vehicleType, job, found)) yield return y;
                if (found.Route == null || found.Plan == null) { tried.Add($"{start}: no route"); continue; }
                var line = found.Plan.Line(graph);
                string? refused = null;
                CarPose? joined = null;
                var lead = JoinLeadSeconds;
                for (var attempt = 0; attempt < JoinAttempts && joined == null && refused == null; attempt++, lead *= 2f)
                {
                    var now = pose();
                    var moving = Math.Abs(now.Speed) > StandingMs;
                    var target = now;
                    if (moving)
                    {
                        if (current == null || currentLine == null) { refused = "the car moves, with no line to foresee it on"; break; }
                        target = check.Predict(current, currentLine, now, lead);
                    }
                    if (!line.Joins(target.X, target.Z, target.Heading, out _)) { refused = "not where the car stands"; break; }
                    var joining = new Feasibility.SweepResult();
                    foreach (var y in check.JoinWork(found.Plan, line, target, joining)) yield return y;
                    if (joining.What != null) { refused = joining.What; break; }
                    for (var waited = 0; moving && waited < MaxWaitSteps && Ahead(pose(), target) > 0.05f; waited++)
                        yield return WayOnJob.NextStep;
                    var there = pose();
                    if (JoinEnvelope(there, target)) joined = target;
                    else if (attempt + 1 == JoinAttempts) refused = $"the car wasn't where foreseen ({Apart(there, target)})";
                }
                if (joined == null) { tried.Add($"{start}: {refused ?? "the car wasn't where foreseen"}"); continue; }
                job.JoinedFrom = joined;
                job.Found = (found.Plan, new List<int>(found.Route.Waypoints), $"{why}; from {start}: {found.Summary}");
                yield break;
            }
            job.Note = $"{why}; no way on joins the car where it stands ({string.Join("; ", tried)})";
            world.Note(job.Note);
        }

        public const float JoinLeadSeconds = 0.5f;
        public const int JoinAttempts = 4;
        public const int MaxWaitSteps = 250;
        public const float StandingMs = 0.3f;

        public const float JoinMetres = 0.35f, JoinDegrees = 3f, JoinSpeedMs = 1.5f, JoinWheelDegrees = 5f;

        public static bool JoinEnvelope(CarPose actual, CarPose checkedFrom) =>
            Math.Sqrt((actual.X - checkedFrom.X) * (actual.X - checkedFrom.X) + (actual.Z - checkedFrom.Z) * (actual.Z - checkedFrom.Z)) <= JoinMetres
            && Math.Abs(DriveLine.Wrap(actual.Heading - checkedFrom.Heading)) <= JoinDegrees * Math.PI / 180
            && Math.Abs(actual.Speed - checkedFrom.Speed) <= JoinSpeedMs
            && Math.Abs(actual.Wheel - checkedFrom.Wheel) <= JoinWheelDegrees;

        private static float Ahead(CarPose from, CarPose to) =>
            (to.X - from.X) * (float)Math.Sin(from.Heading) + (to.Z - from.Z) * (float)Math.Cos(from.Heading);

        private static string Apart(CarPose a, CarPose b) => FormattableString.Invariant(
            $"{Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z)):0.00} m, {Math.Abs(DriveLine.Wrap(a.Heading - b.Heading)) * 180 / Math.PI:0.0} degrees, {Math.Abs(a.Speed - b.Speed):0.0} m/s, wheel {Math.Abs(a.Wheel - b.Wheel):0.0} degrees");

        public WayOnJob LookAhead(IReadOnlyList<int> route, IReadOnlyList<int> path, int position, Func<CarPose> pose,
            IReadOnlyList<Goal> goals, int vehicleType, Watch watch)
        {
            var routeNow = new List<int>(route);
            var pathNow = new List<int>(path);
            return new WayOnJob("ahead", position, job => LookAheadWork(job, routeNow, pathNow, position, pose, goals, vehicleType, watch), ClockMs);
        }

        public const float ObstacleClear = 0.5f;

        private IEnumerable<object?> LookAheadWork(WayOnJob job, List<int> route, List<int> path, int position, Func<CarPose> pose,
            IReadOnlyList<Goal> goals, int vehicleType, Watch watch)
        {
            var graph = world.Graph;
            var check = world.Check;
            var here = pose();
            var rear = new Vec3(here.X, 0f, here.Z);
            var planned = watch.Plan != null && SamePath(watch.Plan.Path, path) ? watch.Plan : null;
            var installed = new Dictionary<(int, int), (float From, float To, float Offset)>();
            if (planned != null)
                foreach (var shift in planned.Shifts) installed[(path[shift.Position], path[shift.Position + 1])] = (shift.From, shift.To, shift.Offset);
            var changes = new List<Change>();
            var found = new Dictionary<string, Change>();
            var unswept = new List<Change>();
            var holdMetres = float.MaxValue;
            DriveLine? line = null;

            float MetresTo(int at, float offset)
            {
                var m = offset;
                for (var k = Math.Max(0, position); k < at && k + 1 < path.Count; k++) m += Vec3.HorizontalDistance(graph.Position(path[k]), graph.Position(path[k + 1]));
                return m;
            }

            void Hold(int from, float offset, string why)
            {
                var at = PathPosition(path, position, from);
                if (at < 0) return;
                var metres = MetresTo(at, offset);
                if (job.HoldPosition >= 0 && holdMetres <= metres) return;
                holdMetres = metres;
                job.HoldPosition = at;
                job.HoldOffset = offset;
                job.HoldWhat = why;
            }

            void Found(Change c)
            {
                if (found.ContainsKey(c.Key)) return;
                found[c.Key] = c;
                if (!c.Blocked && watch.Passed.TryGetValue(c.Key, out var passed) && passed.SameValue(c) && !watch.Holds.ContainsKey(c.Key)) return;
                if (c.Kind == "shift") { unswept.Add(c); return; }
                changes.Add(c);
                Hold(c.From, c.Offset, c.What);
            }

            IEnumerable<object?> SweepOnLine(Change c)
            {
                var at = PathPosition(path, position, c.From);
                var end = at >= 0 ? PathPosition(path, at + 1, c.To) : -1;
                if (planned == null || at < 0 || end < 0) { c.Passes = false; c.Failed = "no plan to check it on"; yield break; }
                line ??= planned.Line(graph);
                var swept = new Feasibility.SweepResult();
                var from = line.AlongOf[at] - 10f - (c.Kind == "shift" ? DriveLine.ShiftRamp : 0f);
                foreach (var y in check.SweepWork(planned, line, from, line.AlongOf[end] + 10f, swept)) yield return y;
                c.Passes = swept.What == null;
                c.Failed = swept.What ?? "";
            }

            void Settle(Change c)
            {
                if (c.Passes == true)
                {
                    watch.Passed[c.Key] = c;
                    watch.Holds.Remove(c.Key);
                    return;
                }
                watch.Passed.Remove(c.Key);
                watch.Holds[c.Key] = new Change { Kind = c.Kind, From = c.From, To = c.To, Offset = c.Offset, Shift = c.Shift, Shape = c.Shape, What = $"{c.What}: {c.Failed}" };
                Hold(c.From, c.Offset, c.What);
            }

            IEnumerable<object?> SweepMoves()
            {
                while (unswept.Count > 0)
                {
                    var c = unswept[0];
                    unswept.RemoveAt(0);
                    foreach (var y in SweepOnLine(c)) yield return y;
                    Settle(c);
                    if (c.Passes == true) world.Note($"ahead: {c.What}; the line driven now passes there");
                    else changes.Add(c);
                }
            }

            void ShiftCheck(int from, int to)
            {
                if (planned == null || !Contains(graph.Successors(from), to) || world.IsBlocked(from, to)) return;
                var now = world.ShiftFor(from, to);
                (float From, float To, float Offset)? had = installed.TryGetValue((from, to), out var h) ? h : ((float, float, float)?)null;
                if (SameShift(now, had)) return;
                var limit = now is var (nf, _, _) ? nf - ObstacleClear : had!.Value.From - DriveLine.ShiftRamp;
                var what = now is var (f, t, o)
                    ? FormattableString.Invariant($"step {from} -> {to} passed {o:+0.00;-0.00} m sideways from {f:0.0} to {t:0.0} m")
                    : $"step {from} -> {to} needs no sideways move now";
                Found(new Change { Kind = "shift", From = from, To = to, Offset = limit, Shift = now, What = what });
            }

            void LaneResult((int From, int To, string? What, float At) lane)
            {
                if (lane.What == null) { ShiftCheck(lane.From, lane.To); return; }
                world.Block(lane.From, lane.To, BlockCause.InLane);
                Found(new Change { Kind = "blocked", From = lane.From, To = lane.To, Offset = Math.Max(0f, lane.At - ObstacleClear), What = $"step {lane.From} -> {lane.To}: {lane.What}" });
            }

            foreach (var held in watch.Holds.Values) Hold(held.From, held.Offset, held.What);

            var lanes = new List<(int From, int To, string? What, float At)>();
            var seen = 0;
            foreach (var y in world.LanesAhead(path, position, AheadMetres, rear, lanes))
            {
                for (; seen < lanes.Count; seen++) LaneResult(lanes[seen]);
                foreach (var z in SweepMoves()) yield return z;
                yield return y;
            }
            for (; seen < lanes.Count; seen++) LaneResult(lanes[seen]);
            var gone = 0f;
            for (var k = Math.Max(0, position); k + 1 < path.Count && gone < AheadMetres; k++)
            {
                gone += Vec3.HorizontalDistance(graph.Position(path[k]), graph.Position(path[k + 1]));
                ShiftCheck(path[k], path[k + 1]);
            }
            foreach (var z in SweepMoves()) yield return z;

            var r0 = RouteStepAt(route, path, position);
            for (var q = Math.Max(0, r0); q + 1 < route.Count; q++)
            {
                int from = route[q], to = route[q + 1];
                if (!world.IsBlocked(from, to)) continue;
                var key = $"blocked {from}->{to}";
                var offset = watch.Holds.TryGetValue(key, out var was) ? was.Offset : 0f;
                Found(new Change { Kind = "blocked", From = from, To = to, Offset = offset, What = was?.What ?? $"{StepKind(graph, from, to)} {from} -> {to} blocked" });
            }

            watch.Rechecked.RemoveWhere(step => Vec3.HorizontalDistance(rear, graph.Position(step.Item1)) > LoadedMetres);
            for (var q = Math.Max(0, r0); q + 1 < route.Count; q++)
            {
                int from = route[q], to = route[q + 1];
                if (Vec3.HorizontalDistance(rear, graph.Position(from)) > LoadedMetres) break;
                if (!IsManoeuvre(graph, from, to)) continue;
                if (!watch.Baseline.ContainsKey((from, to)))
                {
                    foreach (var y in check.StepWork(from, to)) yield return y;
                    watch.Baseline[(from, to)] = check.Step(from, to);
                }
                if (watch.Rechecked.Add((from, to)) || !check.Knows(from, to))
                {
                    check.Forget(from, to);
                    foreach (var y in check.StepWork(from, to)) yield return y;
                }
                var now = check.Step(from, to);
                if (!now.Feasible)
                {
                    world.Block(from, to, CauseOf(graph, from, to, now.ToString()));
                    Found(new Change { Kind = "blocked", From = from, To = to, What = $"{StepKind(graph, from, to)} {from} -> {to} now {now}" });
                }
                else if (!now.SameShape(watch.Baseline[(from, to)]))
                    Found(new Change { Kind = "shape", From = from, To = to, Shape = now, What = $"{StepKind(graph, from, to)} {from} -> {to} now {now.Form}" });
            }

            foreach (var key in new List<string>(watch.Holds.Keys))
                if (!found.ContainsKey(key)) watch.Holds.Remove(key);

            if (changes.Count > 0)
            {
                job.Changed = true;
                var fresh = new List<Change>();
                foreach (var c in changes)
                {
                    watch.Tries.TryGetValue(c.Key, out var tries);
                    if (tries >= MaxTries) continue;
                    watch.Tries[c.Key] = tries + 1;
                    fresh.Add(c);
                }
                if (fresh.Count > 0)
                {
                    var notes = new List<string>();
                    foreach (var c in fresh) notes.Add(c.What);
                    foreach (var y in PlanFromWork(job, route, path, position, pose, goals, vehicleType, "ahead: " + string.Join("; ", notes), planned)) yield return y;
                    if (job.Found != null) yield break;
                }

                foreach (var c in changes)
                {
                    if (c.Blocked) { watch.Holds[c.Key] = c; continue; }
                    if (c.Passes == null) foreach (var y in SweepOnLine(c)) yield return y;
                    Settle(c);
                    world.Note(c.Passes == true
                        ? $"ahead: {c.What}; no way on, and the line driven now passes there"
                        : $"ahead: {c.What}; no way on, and the line driven now fails there ({c.Failed}); holding short");
                }
            }
            job.HoldPosition = -1;
            job.HoldOffset = 0f;
            job.HoldWhat = "";
            holdMetres = float.MaxValue;
            foreach (var held in watch.Holds.Values) Hold(held.From, held.Offset, held.What);
        }

        private static bool SameShift((float From, float To, float Offset)? a, (float From, float To, float Offset)? b)
        {
            if (a == null || b == null) return a == null && b == null;
            return Math.Abs(a.Value.Offset - b.Value.Offset) <= ShiftOffsetTolerance
                && Math.Abs(a.Value.From - b.Value.From) <= ShiftAlongTolerance && Math.Abs(a.Value.To - b.Value.To) <= ShiftAlongTolerance;
        }

        public const float ShiftOffsetTolerance = 0.05f;
        public const float ShiftAlongTolerance = 0.5f;

        public static BlockCause CauseOf(LaneGraph graph, int from, int to, string what) =>
            !IsManoeuvre(graph, from, to) || what.Contains(Feasibility.Movable) ? BlockCause.InLane : BlockCause.Manoeuvre;

        public static bool IsManoeuvre(LaneGraph graph, int from, int to) => graph.IsUTurn(from, to) || Contains(graph.MissingTurns(from), to);

        public static string StepKind(LaneGraph graph, int from, int to) =>
            graph.IsWideUTurn(from, to) ? "wide U-turn" : graph.IsUTurn(from, to) ? "U-turn" : Contains(graph.MissingTurns(from), to) ? "turn"
            : Contains(graph.Successors(from), to) ? "lane" : "lane change";

        public static int RouteStepAt(IReadOnlyList<int> route, IReadOnlyList<int> path, int position)
        {
            int r = -1, k = 0;
            for (var q = 0; q < route.Count && k < path.Count; q++)
            {
                while (k < path.Count && path[k] != route[q]) k++;
                if (k >= path.Count || k > position) break;
                r = q;
            }
            return r >= 0 && r + 1 < route.Count ? r : -1;
        }

        public static int PathPosition(IReadOnlyList<int> path, int position, int waypoint)
        {
            for (var k = Math.Max(0, position); k < path.Count; k++) if (path[k] == waypoint) return k;
            return -1;
        }

        private static bool SamePath(IReadOnlyList<int> a, IReadOnlyList<int> b)
        {
            if (a.Count != b.Count) return false;
            for (var k = 0; k < a.Count; k++) if (a[k] != b[k]) return false;
            return true;
        }

        private static bool Contains(IReadOnlyList<int> list, int value)
        {
            for (var k = 0; k < list.Count; k++) if (list[k] == value) return true;
            return false;
        }
    }
}
