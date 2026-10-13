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
        // The lane steps of a path from a position on for `metres`, swept for what stands in them: each one found
        // blocked is added to `found` as it is found. near: where the car is (the game parks cars only near it).
        IEnumerable<object?> LanesAhead(IReadOnlyList<int> path, int position, float metres, Vec3 near, List<(int From, int To, string What)> found);
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

        // What a ride's look-ahead knows about the plan the car drives now: the manoeuvres checked again near, the
        // changes tried for a way on (MaxTries each), the changes this plan was checked against and passes, and the
        // steps of it the car must not enter (a blocked one is dropped when its block has gone).
        public sealed class Watch
        {
            public DrivePlan? Plan { get; private set; }
            public readonly HashSet<(int, int)> Rechecked = new HashSet<(int, int)>();
            public readonly Dictionary<string, int> Tries = new Dictionary<string, int>();
            public readonly HashSet<string> Passed = new HashSet<string>();
            public readonly Dictionary<(int, int), (string Why, bool Blocked)> Holds = new Dictionary<(int, int), (string, bool)>();

            // A plan taken up (the ride's first, or a way on): what was known about the old one goes.
            public void Installed(DrivePlan plan)
            {
                Plan = plan;
                Passed.Clear();
                Holds.Clear();
            }
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
            foreach (var y in PlanFromWork(job, route, path, position, pose, goals, vehicleType, $"step {route[r]} -> {route[r + 1]} blocked by {what}"))
                yield return y;
        }

        // The rest of a ride planned from where the car stands: from the start of the route step it is on, from the
        // waypoints of the lanes beside that step and from the next waypoints of the route. A way is kept only if the
        // car joins its line where it stands (DriveLine.Joins: within 1.5 m of the line, not only beside it) and the
        // model, started at the car's pose, speed and wheel, drives onto it clean (Feasibility.Join; review round 2).
        // The pose is read when each way is tried, as the car may drive on meanwhile.
        private IEnumerable<object?> PlanFromWork(WayOnJob job, List<int> route, List<int> path, int position,
            Func<CarPose> pose, IReadOnlyList<Goal> goals, int vehicleType, string why)
        {
            var graph = world.Graph;
            var r = RouteStepAt(route, path, position);
            if (r < 0) { job.Note = why + "; the car is on no step of its route"; yield break; }
            int from = route[r], to = route[r + 1];
            var starts = new List<int> { from };
            foreach (var w in new[] { from, to })
                foreach (var beside in graph.Lateral(w)) if (!starts.Contains(beside)) starts.Add(beside);
            for (var k = r + 1; k < route.Count && k <= r + 2; k++) if (!starts.Contains(route[k])) starts.Add(route[k]);
            var tried = new List<string>();
            foreach (var start in starts)
            {
                var found = new PlannedRoute();
                foreach (var y in world.PlanRoute(start, goals, vehicleType, job, found)) yield return y;
                if (found.Route == null || found.Plan == null) { tried.Add($"{start}: no route"); continue; }
                var line = found.Plan.Line(graph);
                var now = pose();
                if (!line.Joins(now.X, now.Z, now.Heading, out _)) { tried.Add($"{start}: not where the car stands"); continue; }
                var joining = new Feasibility.SweepResult();
                foreach (var y in world.Check.JoinWork(found.Plan, line, now, joining)) yield return y;
                if (joining.What != null) { tried.Add($"{start}: {joining.What}"); continue; }
                job.Found = (found.Plan, new List<int>(found.Route.Waypoints), $"{why}; from {start}: {found.Summary}");
                yield break;
            }
            job.Note = $"{why}; no way on joins the car where it stands ({string.Join("; ", tried)})";
            world.Note(job.Note);
        }

        // The ride's look-ahead while it drives, as a job (review rounds 1 to 3):
        // - the steps of the plan already known bad are held short of at once;
        // - the lanes ahead are swept for what has loaded since (a step blocked, or passed on another sideways move
        //   than the plan's), each manoeuvre ahead is checked again once the car is near enough for the world round it
        //   to be there, and a step of the route blocked now counts too;
        // - each change publishes a hold before its step the moment it is found (HoldPosition), so the car brakes
        //   while the rest is looked at and a way on is planned;
        // - a way on is planned for a change at most MaxTries times; with none, the plan the car drives is checked
        //   against each change (a blocked step fails it; another shape or sideways move: its line swept there), and
        //   the car drives on where it passes and holds short where it fails, until a new plan is installed.
        // The job's HoldPosition at its end is the first step of the plan the car must not enter, -1 for none; Changed
        // says something changed; Found is a way on, if one was planned.
        public WayOnJob LookAhead(IReadOnlyList<int> route, IReadOnlyList<int> path, int position, Func<CarPose> pose,
            IReadOnlyList<Goal> goals, int vehicleType, Watch watch)
        {
            var routeNow = new List<int>(route);
            var pathNow = new List<int>(path);
            return new WayOnJob("ahead", position, job => LookAheadWork(job, routeNow, pathNow, position, pose, goals, vehicleType, watch), ClockMs);
        }

        private sealed class Change
        {
            public string Key = "", What = "";
            public int From, To;
            public bool Blocked;
        }

        private IEnumerable<object?> LookAheadWork(WayOnJob job, List<int> route, List<int> path, int position, Func<CarPose> pose,
            IReadOnlyList<Goal> goals, int vehicleType, Watch watch)
        {
            var graph = world.Graph;
            var check = world.Check;
            var here = pose();
            var rear = new Vec3(here.X, 0f, here.Z);
            var changes = new List<Change>();
            var keys = new HashSet<string>();

            void Hold(int from, string why)
            {
                var at = PathPosition(path, position, from);
                if (at < 0 || (job.HoldPosition >= 0 && job.HoldPosition <= at)) return;
                job.HoldPosition = at;
                job.HoldWhat = why;
            }

            void Found(string key, int from, int to, string what, bool blocked)
            {
                if (!keys.Add(key) || (!blocked && watch.Passed.Contains(key))) return;
                changes.Add(new Change { Key = key, From = from, To = to, What = what, Blocked = blocked });
                Hold(from, what);
            }

            // What is known bad about the plan: held short of at once (a blocked step whose block has gone is dropped).
            foreach (var step in new List<(int, int)>(watch.Holds.Keys))
                if (watch.Holds[step].Blocked && !world.IsBlocked(step.Item1, step.Item2)) watch.Holds.Remove(step);
            foreach (var pair in watch.Holds) Hold(pair.Key.Item1, pair.Value.Why);

            // The lanes ahead, each blocked one held short of as it is found.
            var lanes = new List<(int From, int To, string What)>();
            var seen = 0;
            foreach (var y in world.LanesAhead(path, position, AheadMetres, rear, lanes))
            {
                for (; seen < lanes.Count; seen++) LaneFound(lanes[seen]);
                yield return y;
            }
            for (; seen < lanes.Count; seen++) LaneFound(lanes[seen]);

            void LaneFound((int From, int To, string What) lane)
            {
                world.Block(lane.From, lane.To, BlockCause.InLane);
                Found($"blocked {lane.From}->{lane.To}", lane.From, lane.To, $"step {lane.From} -> {lane.To}: {lane.What}", true);
            }

            // The steps of the route ahead blocked now, by this look or an earlier one, or for every car.
            var r0 = RouteStepAt(route, path, position);
            for (var q = Math.Max(0, r0); q + 1 < route.Count; q++)
                if (world.IsBlocked(route[q], route[q + 1]))
                    Found($"blocked {route[q]}->{route[q + 1]}", route[q], route[q + 1], $"{StepKind(graph, route[q], route[q + 1])} {route[q]} -> {route[q + 1]} blocked", true);

            // Each manoeuvre within LoadedMetres checked again once while it stays that near (the parked cars round it
            // are loaded then), and again whenever its verdict was forgotten since; compared by shape (review round 2).
            watch.Rechecked.RemoveWhere(step => Vec3.HorizontalDistance(rear, graph.Position(step.Item1)) > LoadedMetres);
            for (var q = Math.Max(0, r0); q + 1 < route.Count; q++)
            {
                int from = route[q], to = route[q + 1];
                if (Vec3.HorizontalDistance(rear, graph.Position(from)) > LoadedMetres) break;
                if (!IsManoeuvre(graph, from, to)) continue;
                if (!watch.Rechecked.Add((from, to)) && check.Knows(from, to)) continue;
                foreach (var y in check.StepWork(from, to)) yield return y;
                var before = check.Step(from, to);
                check.Forget(from, to);
                foreach (var y in check.StepWork(from, to)) yield return y;
                var now = check.Step(from, to);
                if (!now.Feasible)
                {
                    world.Block(from, to, CauseOf(graph, from, to, now.ToString()));
                    Found($"blocked {from}->{to}", from, to, $"{StepKind(graph, from, to)} {from} -> {to} now {now}", true);
                }
                else if (!now.SameShape(before))
                    Found(FormattableString.Invariant($"shape {from}->{to} {now.Form} {now.Arc?.Length ?? 0f:0.0}"), from, to, $"{StepKind(graph, from, to)} {from} -> {to} now {now.Form}", false);
            }

            // The sideways moves of the lanes ahead against the plan's, by where they begin and end and how far
            // (review round 3: by the offset alone, a move needed earlier or later along the step went unseen).
            if (watch.Plan != null && SamePath(watch.Plan.Path, path))
            {
                var installed = new Dictionary<(int, int), (float From, float To, float Offset)>();
                foreach (var shift in watch.Plan.Shifts) installed[(path[shift.Position], path[shift.Position + 1])] = (shift.From, shift.To, shift.Offset);
                var gone = 0f;
                for (var k = Math.Max(0, position); k + 1 < path.Count && gone < AheadMetres; k++)
                {
                    int from = path[k], to = path[k + 1];
                    gone += Vec3.HorizontalDistance(graph.Position(from), graph.Position(to));
                    if (!Contains(graph.Successors(from), to) || world.IsBlocked(from, to)) continue;
                    var now = world.ShiftFor(from, to);
                    (float From, float To, float Offset)? had = installed.TryGetValue((from, to), out var h) ? h : ((float, float, float)?)null;
                    if (SameShift(now, had)) continue;
                    var what = now is var (f, t, o)
                        ? FormattableString.Invariant($"step {from} -> {to} passed {o:+0.00;-0.00} m sideways from {f:0.0} to {t:0.0} m")
                        : $"step {from} -> {to} needs no sideways move now";
                    Found(FormattableString.Invariant($"shift {from}->{to} {now?.Offset ?? 0f:0.00} {now?.From ?? 0f:0.0} {now?.To ?? 0f:0.0}"), from, to, what, false);
                }
            }

            if (changes.Count == 0) yield break;
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
                foreach (var y in PlanFromWork(job, route, path, position, pose, goals, vehicleType, "ahead: " + string.Join("; ", notes))) yield return y;
                if (job.Found != null) yield break;
            }

            // No way on: the plan the car drives, checked against each change.
            DriveLine? line = null;
            foreach (var c in changes)
            {
                if (c.Blocked) { watch.Holds[(c.From, c.To)] = (c.What, true); continue; }
                var at = PathPosition(path, position, c.From);
                var end = at >= 0 ? PathPosition(path, at + 1, c.To) : -1;
                if (watch.Plan == null || !SamePath(watch.Plan.Path, path) || at < 0 || end < 0)
                {
                    watch.Holds[(c.From, c.To)] = (c.What + "; no plan to check it on", false);
                    continue;
                }
                line ??= watch.Plan.Line(graph);
                var swept = new Feasibility.SweepResult();
                foreach (var y in check.SweepWork(watch.Plan, line, line.AlongOf[at] - 10f, line.AlongOf[end] + 10f, swept)) yield return y;
                if (swept.What == null)
                {
                    watch.Passed.Add(c.Key);
                    world.Note($"ahead: {c.What}; no way on, and the line driven now passes there");
                }
                else
                {
                    watch.Holds[(c.From, c.To)] = ($"{c.What}: {swept.What}", false);
                    world.Note($"ahead: {c.What}; no way on, and the line driven now fails there ({swept.What}); holding short");
                }
            }
            // The hold at the end: the first step the plan must not enter, or none.
            job.HoldPosition = -1;
            job.HoldWhat = "";
            foreach (var pair in watch.Holds) Hold(pair.Key.Item1, pair.Value.Why);
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
