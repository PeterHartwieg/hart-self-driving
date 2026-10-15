using System;
using System.Collections.Generic;

namespace DrivenRides.Core.Routing
{
    public sealed class GoalOptions
    {
        public float Radius = 60f;
        public float MaxHeightDiff = 3f;
        public float MinStraightMetres = 15f;
        public float WalkSpeedMs = 1.4f;
        // No limit by default: a cut by walking distance alone can drop every reachable goal (one-way lanes).
        public int MaxGoals = int.MaxValue;
        // Prefer the lane at the kerb (no lane to its right), so the car stops beside the pavement. Falls back to
        // any lane when none qualifies.
        public bool KerbLane = false;
        // Only lanes with the target on their right, so the passenger gets out on the destination's side of the
        // street instead of across it. No fallback here: the caller plans again without it if no route is found.
        public bool TargetOnRight = false;
        // Lanes not allowed for the vehicle type count too (the Hamptons' private roads allow none).
        public bool AnyVehicleType = false;
    }

    public static class GoalPicker
    {
        // A wide destination search must not turn into permission to leave the passenger at the road's entrance.
        // Try stops nearest the door first, then retain those while adding farther stops if none is reachable.
        // Each batch owns its list: a route or a later replan may keep it after enumeration moves on.
        public static IEnumerable<List<Goal>> PickNearbyFirst(LaneGraph g, Vec3 target, int vehicleType,
            GoalOptions options, float bandMetres = 20f)
        {
            if (!Nonnegative(bandMetres) || bandMetres == 0) throw new ArgumentException("Invalid goal band.", nameof(bandMetres));
            var goals = Pick(g, target, vehicleType, options);
            int count = 0;
            while (count < goals.Count)
            {
                float limit = goals[count].WalkSeconds + bandMetres / options.WalkSpeedMs;
                do { count++; } while (count < goals.Count && goals[count].WalkSeconds <= limit);
                yield return goals.GetRange(0, count);
            }
        }

        public static List<Goal> Pick(LaneGraph g, Vec3 target, int vehicleType, GoalOptions? options = null)
        {
            if (g == null) throw new ArgumentNullException(nameof(g));
            options ??= new GoalOptions();
            if (!Nonnegative(options.Radius) || !Nonnegative(options.MaxHeightDiff) ||
                !Nonnegative(options.MinStraightMetres) || !Nonnegative(options.WalkSpeedMs) ||
                options.WalkSpeedMs == 0 || options.MaxGoals < 0) throw new ArgumentException("Invalid goal options.", nameof(options));
            var result = new List<Goal>();
            if (options.MaxGoals == 0) return result;
            foreach (int i in g.Near(target, options.Radius))
            {
                if (Math.Abs((double)g.Position(i).Y - target.Y) > options.MaxHeightDiff ||
                    g.IsIntersection(i) || (!options.AnyVehicleType && !g.Allows(i, vehicleType)) || !HasStretch(g, i, options.MinStraightMetres)) continue;
                result.Add(new Goal(i, Vec3.HorizontalDistance(g.Position(i), target) / options.WalkSpeedMs));
            }
            if (options.TargetOnRight) result.RemoveAll(goal => g.SideOf(goal.Waypoint, target) <= 0);
            if (options.KerbLane)
            {
                var kerb = result.FindAll(goal => IsKerbLane(g, goal.Waypoint));
                if (kerb.Count > 0) result = kerb;
            }
            result.Sort((a, b) =>
            {
                int comparison = a.WalkSeconds.CompareTo(b.WalkSeconds);
                return comparison != 0 ? comparison : a.Waypoint.CompareTo(b.Waypoint);
            });
            if (result.Count > options.MaxGoals) result.RemoveRange(options.MaxGoals, result.Count - options.MaxGoals);
            return result;
        }

        private static bool IsKerbLane(LaneGraph g, int i)
        {
            foreach (int j in g.Lateral(i)) if (g.SideOf(i, j) > 1f) return false;
            return true;
        }

        private static bool HasStretch(LaneGraph g, int candidate, float minimum)
        {
            if (!Straight(g, candidate)) return false;
            var visited = new HashSet<int> { candidate };
            double length = 0;
            for (int direction = 0; direction < 2; direction++)
            {
                int current = candidate;
                while (length < minimum)
                {
                    int next = direction == 0 ? g.Predecessors(current)[0] : g.Successors(current)[0];
                    if (!Straight(g, next) || !visited.Add(next)) break;
                    length += Vec3.HorizontalDistance(g.Position(current), g.Position(next));
                    current = next;
                }
            }
            return length >= minimum;
        }

        private static bool Straight(LaneGraph g, int i) => !g.IsIntersection(i) &&
            g.Predecessors(i).Count == 1 && g.Successors(i).Count == 1;
        private static bool Nonnegative(float value) => value >= 0 && !float.IsInfinity(value);
    }
}
