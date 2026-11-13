using System.Collections.Generic;
using DrivenRides.Core.Routing;

namespace DrivenRides.Core.Ride
{
    public sealed class ExpandedPath
    {
        public List<int> Path { get; } = new List<int>();
        public HashSet<int> SlowPath { get; } = new HashSet<int>();
        public HashSet<int> UTurnPath { get; } = new HashSet<int>();
        public Dictionary<(int, int), (int Via, int Aim)> Aims { get; } = new Dictionary<(int, int), (int, int)>();
        public List<(int Entry, int Exit)> Arcs { get; } = new List<(int, int)>();
    }

    public static class PathExpander
    {
        private const float MinAimMetres = 10f;

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
                var uTurn = graph.IsUTurn(w[k], w[k + 1]);
                if (vias.Count > 0 && arc != null && arc(w[k], w[k + 1]))
                {
                    for (var back = 2; back >= 0; back--) if (result.Count - 1 - back >= 0) slowPath.Add(result.Count - 1 - back);
                    slowPath.Add(result.Count);
                    if (uTurn) { expanded.UTurnPath.Add(result.Count - 1); expanded.UTurnPath.Add(result.Count); }
                    expanded.Arcs.Add((w[k], w[k + 1]));
                    continue;
                }
                if (vias.Count > 0 && !uTurn)
                {
                    for (var back = 2; back >= 0; back--) if (result.Count - 1 - back >= 0) slowPath.Add(result.Count - 1 - back);
                    foreach (var via in vias) { slowPath.Add(result.Count); result.Add(via); }
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
