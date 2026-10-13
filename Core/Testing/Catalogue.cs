using System;
using System.Collections.Generic;
using DrivenRides.Core.Routing;

namespace DrivenRides.Core.Testing
{
    public static class Catalogue
    {

        public static List<Fixture> Build(LaneGraph graph) => Build(graph, new CatalogueOptions());

        public static List<Fixture> Build(LaneGraph graph, CatalogueOptions options)
        {
            var cases = new List<Fixture>();
            int[] lanes = Lanes(graph);
            var junctions = new List<int>();
            for (int i = 0; i < graph.Count; i++)
                if (graph.IsJunctionEntryWaypoint(i) || graph.IsJunctionExitWaypoint(i)) junctions.Add(i);
            var changes = new Dictionary<(int, int), (Fixture Fixture, float Distance, bool Wide)>();
            for (int i = 0; i < graph.Count; i++)
            {
                if (graph.IsJunctionEntryWaypoint(i))
                    foreach (int x in graph.MissingTurns(i))
                        Add(graph, options, cases, Make(graph, options, CaseKind.Turn, new List<int> { i, x }));
                if (UTurnCase(graph, options, i) is Fixture uTurn) Add(graph, options, cases, uTurn);
                if (graph.IsIntersection(i) || graph.IsJunctionExitWaypoint(i) || graph.Lateral(i).Count == 0) continue;
                float distance = JunctionDistance(graph, i, junctions);
                foreach (int beside in graph.Lateral(i))
                {
                    if (graph.IsIntersection(beside) || (Math.Abs(graph.SideOf(i, beside)) <= 4.5f && distance > 40f)) continue;
                    foreach (int first in graph.Successors(beside))
                    {
                        if (first == i || graph.IsIntersection(first)) continue;
                        foreach (int f in graph.Successors(first))
                        {
                            if (f == i || graph.IsIntersection(f) || graph.IsJunctionEntryWaypoint(f)) continue;
                            Fixture fixture = Make(graph, options, CaseKind.LaneChange, new List<int> { i, f });
                            if (NearBridge(graph, options, fixture.Route)) continue;
                            var key = (lanes[i], lanes[beside]);
                            if (!changes.TryGetValue(key, out var old) || distance < old.Distance)
                                changes[key] = (fixture, distance, Math.Abs(graph.SideOf(i, beside)) > 4.5f);
                        }
                    }
                }
            }
            // Every lane change across a wide gap (slip lanes, ramps: the one lane change that crashed); of the plain
            // ones, a fixed sample, since none has failed.
            foreach (var item in changes.Values)
                if (item.Wide || Sampled(item.Fixture.Id, options.LaneChangeSample)) cases.Add(item.Fixture);
            Ramps(graph, options, cases);
            cases.Sort((a, b) => { int order = b.SharpnessDegrees.CompareTo(a.SharpnessDegrees); return order != 0 ? order : string.CompareOrdinal(a.Id, b.Id); });
            return cases;
        }

        // The case for one added step of a route (a missing turn or a U-turn from entry to exit), as the catalogue
        // builds it: run-up, the manoeuvre, run-out. The feasibility check drives this case, so the planner asks the
        // question the rig asks. Null for any other step, or a case the bridge rules out.
        public static Fixture? ForStep(LaneGraph graph, int entry, int exit, CatalogueOptions options)
        {
            Fixture? fixture = null;
            if (graph.UTurn(entry) == exit) fixture = UTurnCase(graph, options, entry);
            else
                foreach (int x in graph.MissingTurns(entry))
                    if (x == exit) { fixture = Make(graph, options, CaseKind.Turn, new List<int> { entry, exit }); break; }
            return fixture == null || NearBridge(graph, options, fixture.Route) ? null : fixture;
        }

        // A U-turn's case: from the entry to its target, on through the junction to the first plain waypoint of the far
        // carriageway.
        private static Fixture? UTurnCase(LaneGraph graph, CatalogueOptions options, int i)
        {
            int target = graph.UTurn(i);
            if (target < 0) return null;
            var middle = new List<int> { i, target };
            var seen = new HashSet<int>(middle);
            Geometry.Heading(graph, middle, 1, out float dx, out float dz);
            int current = target;
            while (graph.IsIntersection(current))
            {
                int next = Geometry.Next(graph, current, false, dx, dz);
                if (next < 0 || !seen.Add(next)) break;
                Geometry.Direction(graph.Position(current), graph.Position(next), out dx, out dz);
                middle.Add(next);
                current = next;
            }
            if (graph.IsIntersection(current)) return null;
            Fixture fixture = Make(graph, options, CaseKind.UTurn, middle);
            fixture.TurnGateWaypoint = graph.UTurnStart(i);
            return fixture;
        }

        public static List<Fixture> FromRide(LaneGraph graph, IReadOnlyList<int> route, int line, CatalogueOptions options)
        {
            var result = new List<Fixture>();
            var piece = new List<int>();
            int part = 0;
            void Finish()
            {
                float length = 0;
                for (int k = 1; k < piece.Count; k++) length += Vec3.HorizontalDistance(graph.Position(piece[k - 1]), graph.Position(piece[k]));
                if (length >= 40f)
                {
                    var fixture = new Fixture { Id = "ride-" + line + "-" + part, Kind = CaseKind.Ride, Manoeuvre = CaseKind.Ride,
                        Route = new List<int>(piece), ExitIndex = piece.Count - 1, RideLine = line };
                    fixture.SharpnessDegrees = Sharpness(graph, fixture);
                    result.Add(fixture);
                }
                piece.Clear();
                part++;
            }
            foreach (int w in route)
            {
                if (options.NearBridge?.Invoke(graph.Position(w)) == true) Finish();
                else piece.Add(w);
            }
            Finish();
            return result;
        }

        private static Fixture Make(LaneGraph graph, CatalogueOptions options, CaseKind kind, List<int> middle)
        {
            var up = Walk(graph, middle, true, options.RunUpMetres);
            up.Reverse();
            var fixture = new Fixture { Id = kind.ToString().ToLowerInvariant() + "-" + middle[0] + "-" + middle[middle.Count - 1],
                Kind = kind, Manoeuvre = kind, Route = up, EntryIndex = up.Count };
            fixture.Route.AddRange(middle);
            fixture.ExitIndex = fixture.Route.Count - 1;
            fixture.Route.AddRange(Walk(graph, middle, false, options.RunOutMetres));
            fixture.SharpnessDegrees = Sharpness(graph, fixture);
            return fixture;
        }

        private static List<int> Walk(LaneGraph graph, List<int> middle, bool backwards, float limit)
        {
            var result = new List<int>();
            int index = backwards ? 0 : middle.Count - 1;
            int current = middle[index];
            // Run-up follows the approach lane, even when the next route step is an added turn.
            Geometry.Heading(graph, middle, index, out float dx, out float dz);
            var seen = new HashSet<int>(middle);
            float length = 0;
            while (true)
            {
                int next = Geometry.Next(graph, current, backwards, dx, dz);
                if (next < 0 || !seen.Add(next)) break;
                // Never round a bend of more than 60 degrees: at a dead end the only successor is the lane back
                // (turn test 0.1.33: a Hamptons case's run-out turned round and the car got stuck there).
                Geometry.Direction(graph.Position(backwards ? next : current), graph.Position(backwards ? current : next), out float nx, out float nz);
                if (nx * dx + nz * dz < 0.5f) break;
                float segment = Vec3.HorizontalDistance(graph.Position(current), graph.Position(next));
                if (length + segment > limit) break;
                length += segment;
                Geometry.Direction(graph.Position(backwards ? next : current), graph.Position(backwards ? current : next), out dx, out dz);
                result.Add(next);
                current = next;
            }
            return result;
        }

        private static float Sharpness(LaneGraph graph, Fixture fixture)
        {
            Geometry.Heading(graph, fixture.Route, fixture.EntryIndex, out float dx, out float dz);
            Geometry.Heading(graph, fixture.Route, fixture.ExitIndex, out float ex, out float ez);
            if (fixture.Kind != CaseKind.Ramp && fixture.Kind != CaseKind.LaneChange) return Geometry.Angle(dx, dz, ex, ez);
            float largest = 0;
            var headings = new List<(float X, float Z)>();
            // Include diagonal route segments, whose lane headings alone hide a lane change.
            for (int k = 0; k < fixture.Route.Count; k++)
            {
                Geometry.Heading(graph, fixture.Route, k, out ex, out ez);
                headings.Add((ex, ez));
                if (k + 1 < fixture.Route.Count && Geometry.Direction(graph.Position(fixture.Route[k]), graph.Position(fixture.Route[k + 1]), out ex, out ez))
                    headings.Add((ex, ez));
            }
            for (int a = 0; a < headings.Count; a++)
                for (int b = a + 1; b < headings.Count; b++)
                    largest = Math.Max(largest, Geometry.Angle(headings[a].X, headings[a].Z, headings[b].X, headings[b].Z));
            return largest;
        }

        // A stable pick of one in n by id (string.GetHashCode differs between runs).
        private static bool Sampled(string id, int n)
        {
            if (n <= 1) return true;
            uint hash = 2166136261;
            foreach (char c in id) hash = (hash ^ c) * 16777619;
            return hash % (uint)n == 0;
        }

        private static float JunctionDistance(LaneGraph graph, int i, List<int> junctions)
        {
            float distance = float.PositiveInfinity;
            foreach (int w in junctions)
                distance = Math.Min(distance, Vec3.HorizontalDistance(graph.Position(i), graph.Position(w)));
            return distance;
        }

        private static int[] Lanes(LaneGraph graph)
        {
            var parent = new int[graph.Count];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;
            int Root(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
            for (int i = 0; i < graph.Count; i++)
            {
                if (graph.IsIntersection(i) || graph.Successors(i).Count != 1) continue;
                int j = graph.Successors(i)[0];
                if (!graph.IsIntersection(j) && graph.Predecessors(j).Count == 1) parent[Root(j)] = Root(i);
            }
            for (int i = 0; i < parent.Length; i++) parent[i] = Root(i);
            return parent;
        }

        private static void Ramps(LaneGraph graph, CatalogueOptions options, List<Fixture> cases)
        {
            var seen = new HashSet<(int, int)>();
            void Chain(int start, int next)
            {
                var chain = new List<int> { start };
                int current = start;
                while (seen.Add((current, next)))
                {
                    chain.Add(next);
                    current = next;
                    if (graph.Successors(current).Count != 1 || graph.Predecessors(current).Count != 1) break;
                    next = graph.Successors(current)[0];
                }
                int begin = -1, end = -1;
                void Finish()
                {
                    if (begin >= 0) Add(graph, options, cases, Make(graph, options, CaseKind.Ramp, chain.GetRange(begin, end - begin + 1)));
                    begin = end = -1;
                }
                for (int a = 0; a < chain.Count; a++)
                {
                    float length = 0;
                    for (int b = a + 1; b < chain.Count; b++)
                    {
                        length += Vec3.HorizontalDistance(graph.Position(chain[b - 1]), graph.Position(chain[b]));
                        if (length > 60f) break;
                        if (Math.Abs(graph.Position(chain[b]).Y - graph.Position(chain[a]).Y) <= 3f) continue;
                        if (begin >= 0 && a > end) Finish();
                        if (begin < 0) begin = a;
                        end = Math.Max(end, b);
                    }
                }
                Finish();
            }
            for (int i = 0; i < graph.Count; i++)
                if (graph.Predecessors(i).Count != 1 || graph.Successors(i).Count != 1)
                    foreach (int next in graph.Successors(i)) if (!seen.Contains((i, next))) Chain(i, next);
            // Closed successor chains have no endpoint.
            for (int i = 0; i < graph.Count; i++)
                foreach (int next in graph.Successors(i)) if (!seen.Contains((i, next))) Chain(i, next);
        }

        private static bool NearBridge(LaneGraph graph, CatalogueOptions options, List<int> route)
        {
            if (options.NearBridge == null) return false;
            foreach (int w in route) if (options.NearBridge(graph.Position(w))) return true;
            return false;
        }

        private static void Add(LaneGraph graph, CatalogueOptions options, List<Fixture> cases, Fixture fixture)
        {
            if (!NearBridge(graph, options, fixture.Route)) cases.Add(fixture);
        }
    }
}
