using System.Collections.Generic;
using DrivenRides.Core.Routing;

namespace DrivenRides.Core.Ride
{
    public sealed class ExpandedPath
    {
        public List<int> Path { get; } = new List<int>();
        public HashSet<int> SlowPath { get; } = new HashSet<int>();
        // The U-turns' own positions (vias and target), driven slower still.
        public HashSet<int> UTurnPath { get; } = new HashSet<int>();
        // For each added turn (entry, exit): its last via and the exit-lane waypoint the car aims at from there, the
        // straight line the car actually drives out of the turn.
        public Dictionary<(int, int), (int Via, int Aim)> Aims { get; } = new Dictionary<(int, int), (int, int)>();
        // The added turns (entry, exit) driven along a driver's arc (TurnArc) instead of through their vias, in route
        // order.
        public List<(int Entry, int Exit)> Arcs { get; } = new List<(int, int)>();
    }

    public static class PathExpander
    {
        private const float MinAimMetres = 10f;

        // Puts each added turn's and U-turn's waypoints (straight into the junction, then the curve) between its
        // entry and exit, so the car drives them as a driver would.
        // clear(entry, exit, aim), if given, says whether the car can drive the turn through its vias to an aim
        // without touching anything (the game checks the world); the aim avoids a way out that cuts a kerb corner.
        // arc(entry, exit), if given, says whether the car can drive an added turn along a driver's arc (TurnArc), clear
        // of everything; such a turn gets no vias, the ride steers it along the arc.
        public static ExpandedPath Expand(LaneGraph? graph, IReadOnlyList<int> route, System.Func<int, int, int, bool>? clear = null,
            System.Func<int, int, bool>? arc = null)
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
                if (vias.Count > 0 && !uTurn && arc != null && arc(w[k], w[k + 1]))
                {
                    for (var back = 2; back >= 0; back--) if (result.Count - 1 - back >= 0) slowPath.Add(result.Count - 1 - back);
                    slowPath.Add(result.Count);
                    expanded.Arcs.Add((w[k], w[k + 1]));
                    continue;
                }
                if (vias.Count > 0 && !uTurn)
                {
                    // Spike 12's trace: an added left turn at 50 km/h came out 2.5 m wide, on the centre line.
                    for (var back = 2; back >= 0; back--) if (result.Count - 1 - back >= 0) slowPath.Add(result.Count - 1 - back);
                    foreach (var via in vias) { slowPath.Add(result.Count); result.Add(via); }
                    // The car aims down the exit's lane, at its first waypoint at least 10 m past the last via, so it
                    // comes out of the arc in line with the lane instead of turning past 90 degrees towards a near one
                    // (spike 13). Never further than the third: at an angled junction, where the exit lane starts
                    // far from the via, aiming 30 m down the lane drew the car through the oncoming lanes on a
                    // shallow diagonal (turn test 0.1.31, onto Broadway).
                    // The way to the aim must be clear too: one cut a kerb corner (turn test 0.1.34). The first
                    // waypoint at least 10 m on (at most the third) the car can drive the turn to without touching
                    // anything; failing that, the furthest clear one short of it; failing that, the 10 m one as
                    // without a world check.
                    var lastVia = vias[vias.Count - 1];
                    var candidates = 1;
                    while (candidates < 3 && k + 1 + candidates < w.Count && Contains(graph.Successors(w[k + candidates]), w[k + 1 + candidates])) candidates++;
                    var plain = 0;
                    if (graph.PlainHeading(w[k + 1], out var ex, out var ez))
                    {
                        var last = graph.Position(lastVia);
                        float Along(int i) => (graph.Position(i).X - last.X) * ex + (graph.Position(i).Z - last.Z) * ez;
                        while (plain + 1 < candidates && Along(w[k + 1 + plain]) < MinAimMetres) plain++;
                    }
                    bool Clear(int c) => clear == null || clear(w[k], w[k + 1], w[k + 1 + c]);
                    var skip = plain;
                    if (!Clear(plain))
                    {
                        var further = plain + 1;
                        while (further < candidates && !Clear(further)) further++;
                        var nearer = plain - 1;
                        while (nearer >= 0 && !Clear(nearer)) nearer--;
                        skip = further < candidates ? further : nearer >= 0 ? nearer : plain;
                    }
                    expanded.Aims[(w[k], w[k + 1])] = (lastVia, w[k + 1 + skip]);
                    slowPath.Add(result.Count);
                    k += skip;
                    continue;
                }
                // A U-turn is driven at turn speed too: a 6.7 m car entering one at 50 km/h swung wide into the far
                // kerb (turn test 0.1.30).
                if (uTurn)
                {
                    for (var back = 2; back >= 0; back--) if (result.Count - 1 - back >= 0) slowPath.Add(result.Count - 1 - back);
                    foreach (var via in vias) { slowPath.Add(result.Count); expanded.UTurnPath.Add(result.Count); result.Add(via); }
                    slowPath.Add(result.Count);
                    expanded.UTurnPath.Add(result.Count);
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
