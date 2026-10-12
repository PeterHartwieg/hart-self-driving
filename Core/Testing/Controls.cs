using System;
using System.Collections.Generic;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;

namespace DrivenRides.Core.Testing
{
    public static class Controls
    {
        public static List<Fixture> Build(LaneGraph graph, IReadOnlyList<Fixture> catalogue)
        {
            var ranked = new List<Fixture>(catalogue);
            ranked.Sort((a, b) => { int order = b.SharpnessDegrees.CompareTo(a.SharpnessDegrees); return order != 0 ? order : string.CompareOrdinal(a.Id, b.Id); });
            var result = new List<Fixture>();
            bool uTurn = false, left = false;
            foreach (Fixture fixture in ranked)
            {
                if (!uTurn && fixture.Kind == CaseKind.UTurn && fixture.TurnGateWaypoint >= 0)
                {
                    var expanded = PathExpander.Expand(graph, fixture.Route);
                    Pair(result, fixture, new List<int>(fixture.Route), expanded.SlowPath, "far-side-early");
                    uTurn = true;
                }
                if (left || fixture.Kind != CaseKind.Turn) continue;
                int entry = fixture.Route[fixture.EntryIndex], exit = fixture.Route[fixture.ExitIndex];
                if (!graph.PlainHeading(entry, out float dx, out float dz) || !graph.PlainHeading(exit, out float ex, out float ez) || ex * dz - ez * dx >= 0) continue;
                var vias = graph.TurnVias(entry, exit);
                if (vias.Count == 0) continue;
                // The bad half aims into the oncoming lane of the road it turns into, past its mouth, as the turns of
                // spikes 11 and 12 came out on the centre line: the waypoint after the vias (where the car aims) is
                // swapped for the nearest plain waypoint of a lane heading the other way.
                var correct = PathExpander.Expand(graph, fixture.Route);
                int aim = correct.Path.IndexOf(vias[vias.Count - 1]) + 1;
                if (aim <= 0 || aim >= correct.Path.Count) continue;
                int target = correct.Path[aim], nearest = -1;
                float distance = float.PositiveInfinity;
                foreach (int w in graph.Near(graph.Position(target), 12f))
                {
                    if (graph.IsIntersection(w) || !Direction(graph, w, out float wx, out float wz) || wx * ex + wz * ez >= -0.5f) continue;
                    float d = Vec3.HorizontalDistance(graph.Position(target), graph.Position(w));
                    if (d < distance || (d == distance && w < nearest)) { distance = d; nearest = w; }
                }
                if (nearest < 0) continue;
                var bad = new List<int>(correct.Path);
                bad[aim] = nearest;
                Pair(result, fixture, bad, correct.SlowPath, "intrusion");
                left = true;
            }
            return result;
        }

        private static bool Direction(LaneGraph graph, int w, out float dx, out float dz)
        {
            dx = dz = 0;
            return graph.Successors(w).Count == 1 && Geometry.Direction(graph.Position(w), graph.Position(graph.Successors(w)[0]), out dx, out dz);
        }

        private static void Pair(List<Fixture> result, Fixture source, List<int> path, HashSet<int> slowPath, string reason)
        {
            Fixture Copy(string suffix) => new Fixture { Id = source.Id + suffix, Kind = CaseKind.Control, Manoeuvre = source.Kind,
                Route = new List<int>(source.Route), EntryIndex = source.EntryIndex, ExitIndex = source.ExitIndex,
                TurnGateWaypoint = source.TurnGateWaypoint, SharpnessDegrees = source.SharpnessDegrees, PairId = source.Id, RideLine = source.RideLine };
            result.Add(Copy("-ok"));
            var bad = Copy("-bad");
            bad.Path = path;
            bad.SlowPath = new List<int>(slowPath);
            bad.SlowPath.Sort();
            bad.IsMutation = true;
            bad.ExpectedFailure = reason;
            result.Add(bad);
        }
    }
}
