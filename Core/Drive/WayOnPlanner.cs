using System;
using System.Collections.Generic;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;

namespace DrivenRides.Core.Drive
{
    // Why a step is blocked, which says for how long (review round 1: every block held for the session): a manoeuvre
    // the car can't make, for the session (the world and the car don't change); something standing in the lane (a
    // parked car, a prop), for a while, then swept again; found blocked on the way (no motion, held by something, a
    // traffic car that won't move), for this ride only.
    public enum BlockCause { Manoeuvre, InLane, OnTheWay }

    // A route planned for the car and its checked plan (both null: none), with a short summary.
    public sealed class PlannedRoute
    {
        public Route? Route;
        public DrivePlan? Plan;
        public string Summary = "";
    }

    // What the planning of a way on asks the game (NwhPlanner): the route search with the steps found blocked, the
    // lanes swept for what stands in them, the sideways moves past parked cars, the blocks.
    public interface IWayOnWorld
    {
        LaneGraph Graph { get; }
        Feasibility Check { get; }
        // A route from `start` to the goals this car can drive, its line checked as driven; into.Route and into.Plan
        // null if there is none. Yields as it goes; with a job, its route search stops at the job's budget too.
        IEnumerable<object?> PlanRoute(int start, IReadOnlyList<Goal> goals, int vehicleType, WayOnJob? job, PlannedRoute into);
        // The lane steps of a path from a position on for `metres`, swept for what stands in them: each step swept is
        // added to `swept` as soon as it is, with what blocks it (null: passable, perhaps on a sideways move, ShiftFor)
        // and the metres along it where that begins. A step known clear from an earlier sweep may be left out.
        // near: where the car is (the game parks cars only near it).
        IEnumerable<object?> LanesAhead(IReadOnlyList<int> path, int position, float metres, Vec3 near, List<(int From, int To, string? What, float At)> swept);
        // A lane step's sideways move past a car parked partly in it, as last swept, or null.
        (float From, float To, float Offset)? ShiftFor(int from, int to);
        void Block(int from, int to, BlockCause cause);
        // Blocked for this car or for every car.
        bool IsBlocked(int from, int to);
        void Note(string text);
    }

    // The way on while a ride drives (docs/implementation-plan.md; review rounds 1 to 3): round a step found blocked on
    // the way (RoundBlock), and the look ahead for what has changed since the plan was made (LookAhead). Both are
    // jobs (WayOnJob) that a driver steps a little each physics step.
    public sealed class WayOnPlanner
    {
        // How far ahead the look-ahead sweeps lanes, and within how far of the car the parked cars are loaded (the game
        // clears them beyond 80 m of the player, ParkingLaneGenerator.CleanupParkedVehicles).
        public const float AheadMetres = 150f;
        public const float LoadedMetres = 70f;
        // A change found ahead is planned round at most this often (review round 3: a change with no way on was
        // planned again every few seconds for the rest of the ride).
        public const int MaxTries = 2;

        private readonly IWayOnWorld world;
        // The jobs' clock (tests give their own); null: a stopwatch.
        public Func<double>? ClockMs { get; set; }

        public WayOnPlanner(IWayOnWorld world) { this.world = world; }

        // What a ride's look-ahead knows about the plan the car drives now: each manoeuvre's verdict when the plan was
        // made (its baseline) and those checked again near; the changes tried for a way on (MaxTries each), the
        // changes this plan was checked against and passes (with the values it passed with), and what the car must
        // not drive into (each with the change it came from).
        public sealed class Watch
        {
            public DrivePlan? Plan { get; private set; }
            public readonly HashSet<(int, int)> Rechecked = new HashSet<(int, int)>();
            public readonly Dictionary<(int, int), Verdict> Baseline = new Dictionary<(int, int), Verdict>();
            public readonly Dictionary<string, int> Tries = new Dictionary<string, int>();
            public readonly Dictionary<string, Change> Passed = new Dictionary<string, Change>();
            public readonly Dictionary<string, Change> Holds = new Dictionary<string, Change>();

            // A plan taken up (the ride's first, or a way on): what was known about the old one goes.
            public void Installed(DrivePlan plan)
            {
                Plan = plan;
                Baseline.Clear();
                Tries.Clear();
                Passed.Clear();
                Holds.Clear();
            }
        }

        // A change found ahead: a step blocked, a manoeuvre of another shape, a lane passed on another sideways move.
        // Keyed by its kind and step (review round 4: keys with rounded values made a new key of each bit of noise);
        // its values are compared with tolerances (SameValue). Offset: metres from the step's start to where the car
        // must stop short of (the obstacle; a sideways move's ramp, before the step: negative).
        public sealed class Change
        {
            public string Kind = "", What = "";
            public int From, To;
            public float Offset;
            public (float From, float To, float Offset)? Shift;
            public Verdict? Shape;
            public bool Blocked => Kind == "blocked";
            public string Key => $"{Kind} {From}->{To}";

            public bool SameValue(Change other) =>
                Kind == other.Kind && (Kind == "shift" ? SameShift(Shift, other.Shift)
                    : Kind == "shape" ? Shape != null && other.Shape != null && Shape.SameShape(other.Shape) : true);
        }

        // The way on round a step the car found blocked on its way: the step is blocked for this ride, and the rest of
        // the way planned from where the car stands (PlanFrom).
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

        // The rest of a ride planned from where the car stands: from the start of the route step it is on, from the
        // waypoints of the lanes beside that step and from the next waypoints of the route. A way is kept only if the
        // car joins its line (DriveLine.Joins: within 1.5 m of it, not only beside it) and the model drives onto it
        // clean from the car's pose, speed and wheel (Feasibility.Join; review round 2).
        // The car may drive on while the join is checked (review round 4: it was checked from where the car had been
        // and taken up wherever the car was by then). So a moving car's join is checked from where it will be a little
        // later on the line it drives (`current`, Feasibility.Predict); the job then waits for the car to get there
        // and takes the way up only if the car is there as foreseen (JoinEnvelope), in the same physics step. If it
        // isn't, the check is made again from further ahead, up to JoinAttempts times.
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
                    // To where the join was checked from: nothing more to work out meanwhile.
                    for (var waited = 0; moving && waited < MaxWaitSteps && Ahead(pose(), target) > 0.05f; waited++)
                        yield return WayOnJob.NextStep;
                    var there = pose();
                    if (JoinEnvelope(there, target)) joined = target;
                    else if (attempt + 1 == JoinAttempts) refused = $"the car wasn't where foreseen ({Apart(there, target)})";
                }
                if (joined == null) { tried.Add($"{start}: {refused ?? "the car wasn't where foreseen"}"); continue; }
                // Taken up in this physics step: no yield between the pose check above and the end of the job.
                job.JoinedFrom = joined;
                job.Found = (found.Plan, new List<int>(found.Route.Waypoints), $"{why}; from {start}: {found.Summary}");
                yield break;
            }
            job.Note = $"{why}; no way on joins the car where it stands ({string.Join("; ", tried)})";
            world.Note(job.Note);
        }

        // A join is checked this far ahead of a moving car first, doubled each time the car wasn't there as foreseen.
        public const float JoinLeadSeconds = 0.5f;
        public const int JoinAttempts = 4;
        // Physics steps the job waits at most for the car to reach where the join was checked from.
        public const int MaxWaitSteps = 250;
        public const float StandingMs = 0.3f;

        // How close the car must be to the pose its join was checked from: where it is, its heading, speed and wheel.
        public const float JoinMetres = 0.35f, JoinDegrees = 3f, JoinSpeedMs = 1.5f, JoinWheelDegrees = 5f;

        public static bool JoinEnvelope(CarPose actual, CarPose checkedFrom) =>
            Math.Sqrt((actual.X - checkedFrom.X) * (actual.X - checkedFrom.X) + (actual.Z - checkedFrom.Z) * (actual.Z - checkedFrom.Z)) <= JoinMetres
            && Math.Abs(DriveLine.Wrap(actual.Heading - checkedFrom.Heading)) <= JoinDegrees * Math.PI / 180
            && Math.Abs(actual.Speed - checkedFrom.Speed) <= JoinSpeedMs
            && Math.Abs(actual.Wheel - checkedFrom.Wheel) <= JoinWheelDegrees;

        // Metres from a pose forward to a point (along the pose's heading).
        private static float Ahead(CarPose from, CarPose to) =>
            (to.X - from.X) * (float)Math.Sin(from.Heading) + (to.Z - from.Z) * (float)Math.Cos(from.Heading);

        private static string Apart(CarPose a, CarPose b) => FormattableString.Invariant(
            $"{Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z)):0.00} m, {Math.Abs(DriveLine.Wrap(a.Heading - b.Heading)) * 180 / Math.PI:0.0} degrees, {Math.Abs(a.Speed - b.Speed):0.0} m/s, wheel {Math.Abs(a.Wheel - b.Wheel):0.0} degrees");

        // The ride's look-ahead while it drives, as a job (review rounds 1 to 4):
        // - what the plan must not drive into is held short of at once;
        // - the lanes ahead are swept for what has loaded since, and each result is compared as it comes: a step
        //   blocked is held short of its obstacle, a lane needing another sideways move than the plan's is held short
        //   of the move's ramp; each manoeuvre ahead is checked again once the car is near enough for the world round
        //   it to be there, against the shape the plan has; a step of the route blocked now counts too;
        // - each change publishes its hold the moment it is found (HoldPosition, HoldOffset);
        // - a way on is planned for a change at most MaxTries times; with none, the plan the car drives is checked
        //   against each change (a blocked step fails it; another shape or sideways move: its line swept there), and
        //   the car drives on where it passes and holds short where it fails, until a new plan is installed or the
        //   change goes (a hold whose change is no longer found is dropped).
        // The job's HoldPosition and HoldOffset at its end are where the plan must stop short of, -1 for nowhere;
        // Changed says something changed; Found is a way on, if one was planned.
        public WayOnJob LookAhead(IReadOnlyList<int> route, IReadOnlyList<int> path, int position, Func<CarPose> pose,
            IReadOnlyList<Goal> goals, int vehicleType, Watch watch)
        {
            var routeNow = new List<int>(route);
            var pathNow = new List<int>(path);
            return new WayOnJob("ahead", position, job => LookAheadWork(job, routeNow, pathNow, position, pose, goals, vehicleType, watch), ClockMs);
        }

        // An obstacle in the lane is held short of by this much more than its start.
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
            var holdMetres = float.MaxValue;

            // Metres along the path from the car's position to a waypoint's position, plus an offset.
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
                if (!c.Blocked && watch.Passed.TryGetValue(c.Key, out var passed) && passed.SameValue(c)) return;
                changes.Add(c);
                Hold(c.From, c.Offset, c.What);
            }

            // A lane step's sideways move against the plan's, by where it begins and ends and how far (review round 3).
            void ShiftCheck(int from, int to)
            {
                if (planned == null || !Contains(graph.Successors(from), to) || world.IsBlocked(from, to)) return;
                var now = world.ShiftFor(from, to);
                (float From, float To, float Offset)? had = installed.TryGetValue((from, to), out var h) ? h : ((float, float, float)?)null;
                if (SameShift(now, had)) return;
                var ramp = Math.Min(now?.From ?? float.MaxValue, had?.From ?? float.MaxValue) - DriveLine.ShiftRamp;
                var what = now is var (f, t, o)
                    ? FormattableString.Invariant($"step {from} -> {to} passed {o:+0.00;-0.00} m sideways from {f:0.0} to {t:0.0} m")
                    : $"step {from} -> {to} needs no sideways move now";
                Found(new Change { Kind = "shift", From = from, To = to, Offset = ramp, Shift = now, What = what });
            }

            void LaneResult((int From, int To, string? What, float At) lane)
            {
                if (lane.What == null) { ShiftCheck(lane.From, lane.To); return; }
                world.Block(lane.From, lane.To, BlockCause.InLane);
                Found(new Change { Kind = "blocked", From = lane.From, To = lane.To, Offset = Math.Max(0f, lane.At - ObstacleClear), What = $"step {lane.From} -> {lane.To}: {lane.What}" });
            }

            // What the plan must not drive into: held short of at once.
            foreach (var held in watch.Holds.Values) Hold(held.From, held.Offset, held.What);

            // The lanes ahead, each result taken as it comes (review round 4: the moves were compared after the sweep).
            var lanes = new List<(int From, int To, string? What, float At)>();
            var seen = 0;
            foreach (var y in world.LanesAhead(path, position, AheadMetres, rear, lanes))
            {
                for (; seen < lanes.Count; seen++) LaneResult(lanes[seen]);
                yield return y;
            }
            for (; seen < lanes.Count; seen++) LaneResult(lanes[seen]);
            // And the lanes the sweep knew clear already.
            var gone = 0f;
            for (var k = Math.Max(0, position); k + 1 < path.Count && gone < AheadMetres; k++)
            {
                gone += Vec3.HorizontalDistance(graph.Position(path[k]), graph.Position(path[k + 1]));
                ShiftCheck(path[k], path[k + 1]);
            }

            // The steps of the route ahead blocked now, by this look or an earlier one, or for every car: held short of
            // where they were found blocked.
            var r0 = RouteStepAt(route, path, position);
            for (var q = Math.Max(0, r0); q + 1 < route.Count; q++)
            {
                int from = route[q], to = route[q + 1];
                if (!world.IsBlocked(from, to)) continue;
                var key = $"blocked {from}->{to}";
                var offset = watch.Holds.TryGetValue(key, out var was) ? was.Offset : 0f;
                Found(new Change { Kind = "blocked", From = from, To = to, Offset = offset, What = was?.What ?? $"{StepKind(graph, from, to)} {from} -> {to} blocked" });
            }

            // Each manoeuvre within LoadedMetres: checked again once while it stays that near (the parked cars round it
            // are loaded then), and whenever its verdict was forgotten since; against the plan's shape for it each look.
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

            // A hold whose change has gone (the step no longer blocked, the move or the shape back as planned) goes too
            // (review round 4).
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

                // No way on: the plan the car drives, checked against each change.
                DriveLine? line = null;
                foreach (var c in changes)
                {
                    if (c.Blocked) { watch.Holds[c.Key] = c; continue; }
                    var at = PathPosition(path, position, c.From);
                    var end = at >= 0 ? PathPosition(path, at + 1, c.To) : -1;
                    if (planned == null || at < 0 || end < 0)
                    {
                        watch.Holds[c.Key] = new Change { Kind = c.Kind, From = c.From, To = c.To, Offset = c.Offset, Shift = c.Shift, Shape = c.Shape, What = c.What + "; no plan to check it on" };
                        continue;
                    }
                    line ??= planned.Line(graph);
                    var swept = new Feasibility.SweepResult();
                    foreach (var y in check.SweepWork(planned, line, line.AlongOf[at] + Math.Min(0f, c.Offset) - 10f, line.AlongOf[end] + 10f, swept)) yield return y;
                    if (swept.What == null)
                    {
                        // Passes: kept with its values, and a hold it left from an earlier look goes (review round 4).
                        watch.Passed[c.Key] = c;
                        watch.Holds.Remove(c.Key);
                        world.Note($"ahead: {c.What}; no way on, and the line driven now passes there");
                    }
                    else
                    {
                        watch.Holds[c.Key] = new Change { Kind = c.Kind, From = c.From, To = c.To, Offset = c.Offset, Shift = c.Shift, Shape = c.Shape, What = $"{c.What}: {swept.What}" };
                        world.Note($"ahead: {c.What}; no way on, and the line driven now fails there ({swept.What}); holding short");
                    }
                }
            }
            // The hold at the end: where the plan must stop short of first, or nowhere.
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

        // The cause a step found blocked gets, from what failed it (review round 2): a lane step or a lane change is
        // something in the lane; a manoeuvre that met a car (Feasibility.Movable) is too, so it is checked again
        // later; one that met the built world (a prop, a kerb, the pavement, a building) or that the car can't steer
        // is a manoeuvre this car can't make.
        public static BlockCause CauseOf(LaneGraph graph, int from, int to, string what) =>
            !IsManoeuvre(graph, from, to) || what.Contains(Feasibility.Movable) ? BlockCause.InLane : BlockCause.Manoeuvre;

        public static bool IsManoeuvre(LaneGraph graph, int from, int to) => graph.IsUTurn(from, to) || Contains(graph.MissingTurns(from), to);

        public static string StepKind(LaneGraph graph, int from, int to) =>
            graph.IsWideUTurn(from, to) ? "wide U-turn" : graph.IsUTurn(from, to) ? "U-turn" : Contains(graph.MissingTurns(from), to) ? "turn"
            : Contains(graph.Successors(from), to) ? "lane" : "lane change";

        // The step of a route that a path position lies on: the last route waypoint at or before it in the path. The
        // index of the step's start in the route, or -1.
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

        // The first position from `position` on where the path is at a waypoint, or -1.
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
