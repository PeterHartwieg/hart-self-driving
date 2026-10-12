using System.Collections.Generic;
using DrivenRides.Core.Routing;

namespace DrivenRides.Core.Ride
{
    public sealed class ExpandedPath
    {
        public List<int> Path { get; } = new List<int>();
        public HashSet<int> SlowPath { get; } = new HashSet<int>();
    }

    public static class PathExpander
    {
        // Puts each added turn's and U-turn's waypoints (straight into the junction, then the curve) between its
        // entry and exit, so the car drives them as a driver would.
        public static ExpandedPath Expand(LaneGraph? graph, IReadOnlyList<int> route)
        {
            var expanded = new ExpandedPath();
            var result = expanded.Path;
            var slowPath = expanded.SlowPath;
            var w = route;
            for (var k = 0; k < w.Count; k++)
            {
                result.Add(w[k]);
                if (graph == null || k + 1 >= w.Count) continue;
                var vias = graph.TurnVias(w[k], w[k + 1]);
                var uTurn = graph.UTurn(w[k]) == w[k + 1];
                if (vias.Count > 0 && !uTurn)
                {
                    // Spike 12's trace: an added left turn at 50 km/h came out 2.5 m wide, on the centre line.
                    for (var back = 2; back >= 0; back--) if (result.Count - 1 - back >= 0) slowPath.Add(result.Count - 1 - back);
                    foreach (var via in vias) { slowPath.Add(result.Count); result.Add(via); }
                    // The car aims down the exit's lane, past its first two waypoints, so it comes out of the arc in
                    // line with the lane instead of turning past 90 degrees towards the first one (spike 13).
                    if (k + 3 < w.Count && Contains(graph.Successors(w[k + 1]), w[k + 2]) && Contains(graph.Successors(w[k + 2]), w[k + 3]))
                    {
                        slowPath.Add(result.Count);
                        k += 2;
                        continue;
                    }
                    slowPath.Add(result.Count);
                    continue;
                }
                foreach (var via in vias) result.Add(via);
            }
            return expanded;
        }

        private static bool Contains(IReadOnlyList<int> list, int value)
        {
            for (int k = 0; k < list.Count; k++) if (list[k] == value) return true;
            return false;
        }
    }
}
