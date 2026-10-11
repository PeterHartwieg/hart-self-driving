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
    }

    public static class GoalPicker
    {
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
                    g.IsIntersection(i) || !g.Allows(i, vehicleType) || !HasStretch(g, i, options.MinStraightMetres)) continue;
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
