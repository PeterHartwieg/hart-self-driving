using System;
using System.Collections.Generic;

namespace DrivenRides.Core.Routing
{
    public readonly struct Goal
    {
        public readonly int Waypoint;
        public readonly float WalkSeconds;
        public Goal(int waypoint, float walkSeconds) { Waypoint = waypoint; WalkSeconds = walkSeconds; }
    }

    public sealed class RouteRequest
    {
        public int Start;
        public IReadOnlyList<Goal> Goals = Array.Empty<Goal>();
        public int VehicleType;
        public Func<int, bool>? IsDisabled;
        public float IntersectionPenaltySeconds = 4f;
        // Turning into lanes not allowed for the vehicle type costs this much instead of being impossible: Gley's
        // own AI falls back to such lanes when no allowed one leads on. PositiveInfinity makes the filter hard.
        public float DisallowedPenaltySeconds = 20f;
        // A second of walking counts this much against a second of driving: a passenger would rather be driven on
        // (spike 21: the car stopped 100 m from a garage its private road leads into).
        public float WalkWeight = 2f;
        // A lane change costs this on top of its travel time, so a route changes lanes when that saves time (to be in
        // the turning lane at a junction) but doesn't weave. PositiveInfinity turns lane changes off.
        public float LaneChangePenaltySeconds = 5f;
        // A turn the game's data doesn't have (LaneGraph.MissingTurns) costs this on top, so the data's own turns
        // come first. PositiveInfinity turns them off.
        public float MissingTurnPenaltySeconds = 8f;
        // A U-turn at a junction (LaneGraph.UTurn) costs this on top. PositiveInfinity turns them off.
        public float UTurnPenaltySeconds = 12f;
        // The wider U-turns too (LaneGraph.UTurns: every approach lane to every far lane), for a car driven by its own
        // physics, each costing this much more per metre it is narrower than the widest, so the widest the car can
        // make wins: two lanes wider (8 m) is worth more than a lane change (5 s) before the junction. The Gley path
        // keeps the inner one only.
        public bool WideUTurns;
        public float UTurnNarrowerSecondsPerMetre = 1.5f;
        // Steps the game's data doesn't have (lane changes, added turns, U-turns) that the caller found blocked in the
        // world, by their start and end waypoints. Asked only for those steps.
        public Func<int, int, bool>? IsBlockedStep;
    }

    public sealed class Route
    {
        public IReadOnlyList<int> Waypoints { get; }
        public int GoalIndex { get; }
        public float DriveSeconds { get; }
        public float WalkSeconds { get; }
        public float LengthMetres { get; }

        public static Route Fixed(LaneGraph graph, IReadOnlyList<int> waypoints)
        {
            if (graph == null) throw new ArgumentNullException(nameof(graph));
            if (waypoints == null) throw new ArgumentNullException(nameof(waypoints));
            if (waypoints.Count == 0) throw new ArgumentException("A route needs a waypoint.", nameof(waypoints));
            var copy = new int[waypoints.Count];
            double length = 0, drive = 0;
            for (int i = 0; i < copy.Length; i++)
            {
                int w = waypoints[i];
                if (w < 0 || w >= graph.Count) throw new ArgumentOutOfRangeException(nameof(waypoints));
                copy[i] = w;
                if (i == 0) continue;
                float segment = Vec3.HorizontalDistance(graph.Position(copy[i - 1]), graph.Position(w));
                length += segment;
                drive += segment / (((double)graph.SpeedMs(copy[i - 1]) + graph.SpeedMs(w)) / 2);
            }
            return new Route(copy, copy[copy.Length - 1], drive, 0, length);
        }

        internal Route(int[] waypoints, int goalIndex, double drive, float walk, double length)
        {
            Waypoints = Array.AsReadOnly(waypoints);
            GoalIndex = goalIndex;
            DriveSeconds = (float)drive;
            WalkSeconds = walk;
            LengthMetres = (float)length;
        }
    }

    public enum PlanStatus { Running, Found, NoPath, InvalidRequest }

    public static class RoutePlanner
    {
        public static PlanJob Begin(LaneGraph graph, RouteRequest request) => new PlanJob(graph, request);
        public static PlanJob Plan(LaneGraph graph, RouteRequest request, int maxExpansions)
        {
            var job = Begin(graph, request);
            job.Step(maxExpansions);
            return job;
        }
    }

    public sealed class PlanJob
    {
        private readonly LaneGraph graph;
        private readonly int start, vehicleType;
        private readonly float penalty;
        private readonly float disallowedPenalty;
        private readonly float laneChangePenalty;
        private readonly float missingTurnPenalty;
        private readonly float uTurnPenalty;
        private readonly bool wideUTurns;
        private readonly float narrowerSeconds;
        private readonly float walkWeight;
        private readonly Func<int, int, bool>? blockedStep;
        private readonly Func<int, bool>? disabled;
        private readonly Dictionary<int, (Goal Goal, int Index)> goals = new Dictionary<int, (Goal, int)>();
        private readonly List<Vec3> goalPositions = new List<Vec3>();
        private readonly double[] costs;
        private readonly int[] parents;
        private readonly MinHeap open = new MinHeap();
        private double terminalCost = double.PositiveInfinity;
        private int terminalWaypoint = -1;

        internal PlanJob(LaneGraph graph, RouteRequest request)
        {
            this.graph = graph ?? throw new ArgumentNullException(nameof(graph));
            if (request == null) throw new ArgumentNullException(nameof(request));
            start = request.Start;
            vehicleType = request.VehicleType;
            penalty = request.IntersectionPenaltySeconds;
            disallowedPenalty = request.DisallowedPenaltySeconds;
            laneChangePenalty = request.LaneChangePenaltySeconds;
            missingTurnPenalty = request.MissingTurnPenaltySeconds;
            uTurnPenalty = request.UTurnPenaltySeconds;
            wideUTurns = request.WideUTurns;
            narrowerSeconds = request.UTurnNarrowerSecondsPerMetre;
            walkWeight = request.WalkWeight;
            blockedStep = request.IsBlockedStep;
            disabled = request.IsDisabled;
            costs = new double[graph.Count];
            parents = new int[graph.Count];
            Status = PlanStatus.InvalidRequest;
            if (start < 0 || start >= graph.Count || request.Goals == null || vehicleType < 0 || vehicleType >= 32 ||
                !FiniteNonnegative(penalty) || float.IsNaN(disallowedPenalty) || disallowedPenalty < 0 ||
                float.IsNaN(laneChangePenalty) || laneChangePenalty < 0 || float.IsNaN(missingTurnPenalty) || missingTurnPenalty < 0 ||
                float.IsNaN(uTurnPenalty) || uTurnPenalty < 0) return;
            for (int i = 0; i < request.Goals.Count; i++)
            {
                Goal goal = request.Goals[i];
                if (goal.Waypoint < 0 || goal.Waypoint >= graph.Count) continue;
                if (!FiniteNonnegative(goal.WalkSeconds)) return;
                if (!goals.TryGetValue(goal.Waypoint, out var old) || goal.WalkSeconds < old.Goal.WalkSeconds)
                    goals[goal.Waypoint] = (goal, i);
            }
            if (goals.Count == 0) return;
            foreach (int waypoint in goals.Keys) goalPositions.Add(graph.Position(waypoint));
            for (int i = 0; i < costs.Length; i++) { costs[i] = double.PositiveInfinity; parents[i] = -1; }
            costs[start] = 0;
            open.Push(new Entry(start, 0, Heuristic(start)));
            Status = PlanStatus.Running;
        }

        public PlanStatus Status { get; private set; }
        public int Expanded { get; private set; }
        public Route? Result { get; private set; }

        public PlanStatus Step(int maxExpansions) => Step(maxExpansions, null);

        // shouldStop is asked every 64 expansions, so a caller with a time budget can end a step inside the search.
        public PlanStatus Step(int maxExpansions, Func<bool>? shouldStop)
        {
            if (maxExpansions < 0) throw new ArgumentOutOfRangeException(nameof(maxExpansions));
            if (Status != PlanStatus.Running) return Status;
            int used = 0;
            while (open.Count > 0)
            {
                Entry entry = open.Peek();
                if (entry.Node == -1)
                {
                    open.Pop();
                    if (entry.Cost != terminalCost) continue;
                    Finish();
                    return Status;
                }
                if (entry.Cost != costs[entry.Node]) { open.Pop(); continue; }
                if (used == maxExpansions) return Status;
                if (shouldStop != null && (used & 63) == 0 && used > 0 && shouldStop()) return Status;
                open.Pop();
                used++;
                Expanded++;
                int i = entry.Node;
                if (goals.TryGetValue(i, out var goal))
                {
                    double total = entry.Cost + goal.Goal.WalkSeconds * walkWeight;
                    if (total < terminalCost)
                    {
                        terminalCost = total;
                        terminalWaypoint = i;
                        open.Push(new Entry(-1, total, total));
                    }
                }
                IReadOnlyList<int> successors = graph.Successors(i);
                for (int k = 0; k < successors.Count; k++) Relax(i, successors[k], entry.Cost, 0);
                if (!float.IsPositiveInfinity(laneChangePenalty) && !graph.IsIntersection(i) && !graph.IsJunctionExitWaypoint(i))
                {
                    // A lane change goes diagonally to the second waypoint after the one beside i, so the car moves
                    // over gently while driving on. Never inside a junction, and never from the first waypoint after
                    // one, where the car may still be coming out of a turn (spike 9: into a wall).
                    IReadOnlyList<int> sideways = graph.Lateral(i);
                    for (int k = 0; k < sideways.Count; k++)
                    {
                        int beside = sideways[k];
                        if (graph.IsIntersection(beside)) continue;
                        IReadOnlyList<int> ahead = graph.Successors(beside);
                        for (int m = 0; m < ahead.Count; m++)
                        {
                            int first = ahead[m];
                            if (first == i || graph.IsIntersection(first)) continue;
                            IReadOnlyList<int> further = graph.Successors(first);
                            // Nor may it end on a junction entry: the car should be straight in its lane before it turns.
                            // The quicker change to the first waypoint costs a little more, for short blocks only.
                            if (!graph.IsJunctionEntryWaypoint(first)) RelaxAdded(i, first, entry.Cost, laneChangePenalty + SharpChangeSeconds);
                            for (int f = 0; f < further.Count; f++)
                                if (further[f] != i && !graph.IsIntersection(further[f]) && !graph.IsJunctionEntryWaypoint(further[f]))
                                    RelaxAdded(i, further[f], entry.Cost, laneChangePenalty);
                        }
                    }
                }
                if (!float.IsPositiveInfinity(missingTurnPenalty))
                {
                    // Through a junction along a curve, about a fifth longer than the straight line, plus the penalty.
                    IReadOnlyList<int> added = graph.MissingTurns(i);
                    for (int k = 0; k < added.Count; k++)
                        RelaxAdded(i, added[k], entry.Cost, missingTurnPenalty + 0.2 * Vec3.Distance(graph.Position(i), graph.Position(added[k])) / graph.SpeedMs(i));
                }
                if (!float.IsPositiveInfinity(uTurnPenalty))
                {
                    if (wideUTurns)
                    {
                        IReadOnlyList<(int Target, float Width)> uTurns = graph.UTurns(i);
                        for (int k = 0; k < uTurns.Count; k++)
                        {
                            int x = uTurns[k].Target;
                            RelaxAdded(i, x, entry.Cost, uTurnPenalty + 0.6 * Vec3.Distance(graph.Position(i), graph.Position(x)) / graph.SpeedMs(i)
                                + narrowerSeconds * graph.UTurnNarrower(i, x));
                        }
                    }
                    else
                    {
                        int uTurn = graph.UTurn(i);
                        if (uTurn >= 0) RelaxAdded(i, uTurn, entry.Cost, uTurnPenalty + 0.6 * Vec3.Distance(graph.Position(i), graph.Position(uTurn)) / graph.SpeedMs(i));
                    }
                }
            }
            Status = PlanStatus.NoPath;
            return Status;
        }

        private const double SharpChangeSeconds = 2;

        private void RelaxAdded(int i, int j, double from, double extra)
        {
            if (blockedStep != null && blockedStep(i, j)) return;
            Relax(i, j, from, extra);
        }

        private void Relax(int i, int j, double from, double extra)
        {
            if (j != start && disabled?.Invoke(j) == true) return;
            bool disallowed = j != start && !graph.Allows(j, vehicleType);
            if (disallowed && float.IsPositiveInfinity(disallowedPenalty)) return;
            double cost = from + extra + Vec3.Distance(graph.Position(i), graph.Position(j)) /
                (((double)graph.SpeedMs(i) + graph.SpeedMs(j)) / 2);
            // Once, on turning into such a lane, not per waypoint: a Hamptons driveway allows no vehicle type, and
            // 60 s for each of its waypoints kept the car on the street 100 m from the garage it leads to (spike 21).
            if (disallowed && (i == start || graph.Allows(i, vehicleType))) cost += disallowedPenalty;
            if (!graph.IsIntersection(i) && graph.IsIntersection(j)) cost += penalty;
            if (cost >= costs[j]) return;
            costs[j] = cost;
            parents[j] = i;
            open.Push(new Entry(j, cost, cost + Heuristic(j)));
        }

        private double Heuristic(int node)
        {
            double best = double.PositiveInfinity;
            Vec3 p = graph.Position(node);
            for (int i = 0; i < goalPositions.Count; i++)
            {
                double distance = Vec3.HorizontalDistance(p, goalPositions[i]);
                if (distance < best) best = distance;
            }
            return best / graph.TopSpeedMs;
        }

        private void Finish()
        {
            var path = new List<int>();
            for (int i = terminalWaypoint; i != -1; i = parents[i]) path.Add(i);
            path.Reverse();
            double length = 0;
            for (int i = 1; i < path.Count; i++) length += Vec3.Distance(graph.Position(path[i - 1]), graph.Position(path[i]));
            var goal = goals[terminalWaypoint];
            Result = new Route(path.ToArray(), goal.Index, costs[terminalWaypoint], goal.Goal.WalkSeconds, length);
            Status = PlanStatus.Found;
        }

        private static bool FiniteNonnegative(float value) => value >= 0 && !float.IsInfinity(value);

        private readonly struct Entry
        {
            public readonly int Node;
            public readonly double Cost, Priority;
            public Entry(int node, double cost, double priority) { Node = node; Cost = cost; Priority = priority; }
        }

        private sealed class MinHeap
        {
            private readonly List<Entry> entries = new List<Entry>();
            public int Count => entries.Count;
            public Entry Peek() => entries[0];
            private static bool Before(Entry a, Entry b) => a.Priority < b.Priority ||
                (a.Priority == b.Priority && (a.Cost > b.Cost || (a.Cost == b.Cost && a.Node < b.Node)));
            public void Push(Entry entry)
            {
                int i = entries.Count;
                entries.Add(entry);
                while (i > 0)
                {
                    int parent = (i - 1) / 2;
                    if (!Before(entry, entries[parent])) break;
                    entries[i] = entries[parent];
                    i = parent;
                }
                entries[i] = entry;
            }
            public void Pop()
            {
                Entry last = entries[entries.Count - 1];
                entries.RemoveAt(entries.Count - 1);
                if (entries.Count == 0) return;
                int i = 0;
                while (i * 2 + 1 < entries.Count)
                {
                    int child = i * 2 + 1;
                    if (child + 1 < entries.Count && Before(entries[child + 1], entries[child])) child++;
                    if (!Before(entries[child], last)) break;
                    entries[i] = entries[child];
                    i = child;
                }
                entries[i] = last;
            }
        }
    }
}
