using System;
using System.Collections.Generic;

namespace DrivenRides.Core.Routing
{
    public sealed class LaneGraph
    {
        public const float DefaultSpeedMs = 13.9f;
        private readonly Vec3[] positions;
        private readonly IReadOnlyList<int>[] successors, predecessors, lateral;
        private readonly int[] masks;
        private readonly bool[] intersections;
        private readonly float[] speeds;
        private readonly float cellSize;
        private readonly Dictionary<(int, int), List<int>> grid = new Dictionary<(int, int), List<int>>();

        public LaneGraph(Vec3[] positions, int[][] neighbors, int[] allowedMask, bool[] intersection,
            float[] maxSpeedKmh, float cellSize = 50f, int[][]? otherLanes = null, bool inferLanes = false, bool inferTurns = false)
        {
            if (positions == null || neighbors == null || allowedMask == null || intersection == null || maxSpeedKmh == null)
                throw new ArgumentNullException("Graph arrays cannot be null.");
            int n = positions.Length;
            if (neighbors.Length != n || allowedMask.Length != n || intersection.Length != n || maxSpeedKmh.Length != n)
                throw new ArgumentException("Graph arrays must have equal lengths.");
            if (!Finite(cellSize) || cellSize <= 0) throw new ArgumentOutOfRangeException(nameof(cellSize));
            this.cellSize = cellSize;
            this.positions = (Vec3[])positions.Clone();
            masks = (int[])allowedMask.Clone();
            intersections = (bool[])intersection.Clone();
            speeds = new float[n];
            successors = new IReadOnlyList<int>[n];
            predecessors = new IReadOnlyList<int>[n];
            lateral = new IReadOnlyList<int>[n];
            if (otherLanes != null && otherLanes.Length != n) throw new ArgumentException("Graph arrays must have equal lengths.");
            var incoming = new List<int>[n];
            for (int i = 0; i < n; i++) incoming[i] = new List<int>();
            float top = DefaultSpeedMs;
            for (int i = 0; i < n; i++)
            {
                Vec3 p = positions[i];
                if (!Finite(p.X) || !Finite(p.Y) || !Finite(p.Z)) throw new ArgumentException("Positions must be finite.");
                if (!Finite(maxSpeedKmh[i])) throw new ArgumentException("Speeds must be finite.");
                speeds[i] = maxSpeedKmh[i] <= 0 ? DefaultSpeedMs : maxSpeedKmh[i] / 3.6f;
                if (i == 0 || speeds[i] > top) top = speeds[i];
                var cell = (Cell(p.X), Cell(p.Z));
                if (!grid.TryGetValue(cell, out var bucket)) grid[cell] = bucket = new List<int>();
                bucket.Add(i);
                var outgoing = new List<int>();
                var seen = new HashSet<int>();
                if (neighbors[i] == null) throw new ArgumentException("Neighbor lists cannot be null.");
                foreach (int j in neighbors[i])
                {
                    if (j < 0 || j >= n) { DroppedEdges++; continue; }
                    if (j == i || !seen.Add(j)) continue;
                    outgoing.Add(j);
                    incoming[j].Add(i);
                }
                successors[i] = outgoing.AsReadOnly();
                var sideways = new List<int>();
                if (otherLanes?[i] != null)
                    foreach (int j in otherLanes[i])
                    {
                        if (j < 0 || j >= n) { DroppedEdges++; continue; }
                        if (j != i && !seen.Contains(j) && !sideways.Contains(j)) sideways.Add(j);
                    }
                if (sideways.Count > 0) GivenLaneChanges++;
                lateral[i] = sideways.AsReadOnly();
            }
            for (int i = 0; i < n; i++) predecessors[i] = incoming[i].AsReadOnly();
            TopSpeedMs = top;
            if (inferLanes) InferLanes();
            turns = new IReadOnlyList<int>[n];
            uTurns = new int[n];
            uTurnStarts = new int[n];
            for (int i = 0; i < n; i++) { turns[i] = Array.Empty<int>(); uTurns[i] = -1; uTurnStarts[i] = -1; }
            if (inferTurns) { InferTurns(); InferUTurns(); InferWideUTurns(); }
        }

        private readonly IReadOnlyList<int>[] turns;
        private readonly int[] uTurns, uTurnStarts;

        // A U-turn at a junction into the other carriageway of a divided street (Peter: a player cuts across there,
        // waiting for a gap), or -1. The target is a waypoint inside the junction on the far carriageway, ahead of
        // the car: Gley skips a target behind the car as passed, and the far carriageway's exit is behind the entry.
        public int UTurn(int i) => uTurns[i];
        public int UTurnStart(int i) => uTurnStarts[i];

        public IReadOnlyList<(int Target, float Width)> UTurns(int i) =>
            uTurnPairs.TryGetValue(i, out var pairs) ? pairs : uTurns[i] >= 0 ? new[] { (uTurns[i], uTurnWidths[i]) } : Array.Empty<(int, float)>();
        public bool IsUTurn(int i, int x)
        {
            if (uTurns[i] == x) return true;
            if (!uTurnPairs.TryGetValue(i, out var pairs)) return false;
            foreach (var (target, _) in pairs) if (target == x) return true;
            return false;
        }
        public bool IsWideUTurn(int i, int x) => uTurns[i] != x && IsUTurn(i, x);
        public int UTurnStart(int i, int x) => uTurns[i] == x ? uTurnStarts[i] : uTurnPairStarts.TryGetValue((i, x), out var s) ? s : -1;
        public float UTurnNarrower(int i, int x) => uTurnNarrower.TryGetValue((i, x), out var n) ? n : 0f;
        public int UTurnGroupOf(int i, int x) => uTurnGroupOf.TryGetValue((i, x), out var m) ? m : uTurns[i] == x ? i : -1;
        public float UTurnWidth(int i, int x)
        {
            var m = UTurnGroupOf(i, x);
            if (m < 0) return 0f;
            foreach (var p in UTurnGroup(m)) if (p.Entry == i && p.Target == x) return p.Width;
            return 0f;
        }
        private readonly Dictionary<(int, int), int> uTurnGroupOf = new Dictionary<(int, int), int>();
        public int InferredWideUTurns { get; private set; }

        public JunctionBoxes Junctions => junctionBoxes ??= new JunctionBoxes(this);
        private JunctionBoxes? junctionBoxes;
        public IReadOnlyList<(int Entry, int Target, float Width)> UTurnGroup(int m) =>
            uTurnGroups.TryGetValue(m, out var group) ? group : uTurns[m] >= 0 ? new[] { (m, uTurns[m], uTurnWidths.Length > m ? uTurnWidths[m] : 0f) } : Array.Empty<(int, int, float)>();
        private readonly Dictionary<int, List<(int, int, float)>> uTurnGroups = new Dictionary<int, List<(int, int, float)>>();

        private float[] uTurnWidths = Array.Empty<float>();
        private readonly Dictionary<int, List<(int, float)>> uTurnPairs = new Dictionary<int, List<(int, float)>>();
        private readonly Dictionary<(int, int), int> uTurnPairStarts = new Dictionary<(int, int), int>();
        private readonly Dictionary<(int, int), float> uTurnNarrower = new Dictionary<(int, int), float>();

        private void InferWideUTurns()
        {
            uTurnWidths = new float[Count];
            for (int m = 0; m < Count; m++)
            {
                int t = uTurns[m];
                if (t < 0 || !Heading(m, out float dx, out float dz) || !AnyHeading(t, out float tx, out float tz)) continue;
                uTurnWidths[m] = Math.Abs(Side(m, dx, dz, positions[t]));
                var entries = new List<int> { m };
                foreach (int e in Near(positions[m], 20f))
                {
                    if (e == m || !IsJunctionEntry(e) || !Heading(e, out float ex, out float ez) || ex * dx + ez * dz < 0.95f) continue;
                    if (Math.Abs(positions[e].Y - positions[m].Y) > 1f) continue;
                    float along = Along(m, dx, dz, positions[e]), side = Side(m, dx, dz, positions[e]);
                    if (Math.Abs(along) <= 8f && side >= 2f && side <= 16f) entries.Add(e);
                }
                var lanes = new List<(int Waypoint, float Side)> { (t, 0f) };
                foreach (int j in Near(positions[t], 20f))
                {
                    if (j == t || !intersections[j] || !AnyHeading(j, out float jx, out float jz) || jx * tx + jz * tz < 0.95f) continue;
                    if (Math.Abs(positions[j].Y - positions[t].Y) > 1f) continue;
                    float along = Along(t, tx, tz, positions[j]), side = Side(t, tx, tz, positions[j]);
                    if (Math.Abs(along) > 4f || side < 2f || side > 16f) continue;
                    int same = lanes.FindIndex(l => Math.Abs(l.Side - side) < 1.5f);
                    if (same < 0) lanes.Add((j, side));
                    else if (same > 0 && Math.Abs(along) < Math.Abs(Along(t, tx, tz, positions[lanes[same].Waypoint]))) lanes[same] = (j, side);
                }
                if (entries.Count == 1 && lanes.Count == 1) continue;
                var pairs = new List<(int E, int X, float Width)>();
                foreach (int e in entries)
                {
                    Heading(e, out float ex, out float ez);
                    foreach (var (x, _) in lanes)
                    {
                        float width = Math.Abs(Side(e, ex, ez, positions[x]));
                        if (e == m && x == t) { pairs.Add((e, x, width)); continue; }
                        var chain = new List<int>();
                        int straight = StraightInto(e, ex, ez, 14f, out float startAhead, chain);
                        if (straight < 0) continue;
                        float ahead = Along(e, ex, ez, positions[x]);
                        if (ahead < startAhead - 2f || ahead > startAhead + 24f || width > 36f) continue;
                        if (ArcVia(e, ex, ez, x, startAhead, straight) is int via) chain.Add(via);
                        turnVia[(e, x)] = chain.ToArray();
                        uTurnPairStarts[(e, x)] = straight;
                        pairs.Add((e, x, width));
                        InferredWideUTurns++;
                    }
                }
                float widest = 0f;
                foreach (var p in pairs) widest = Math.Max(widest, p.Width);
                pairs.Sort((a, b) => b.Width.CompareTo(a.Width));
                uTurnGroups[m] = pairs.ConvertAll(p => (p.E, p.X, p.Width));
                foreach (var (e, x, width) in pairs)
                {
                    if (!uTurnPairs.TryGetValue(e, out var list))
                    {
                        uTurnPairs[e] = list = new List<(int, float)>();
                        if (uTurns[e] >= 0 && e != m) list.Add((uTurns[e], Math.Abs(Side(e, dx, dz, positions[uTurns[e]]))));
                    }
                    if (!list.Exists(p => p.Item1 == x)) list.Add((x, width));
                    uTurnNarrower[(e, x)] = widest - width;
                    uTurnGroupOf[(e, x)] = m;
                }
            }
        }

        private float Along(int i, float dx, float dz, Vec3 p) => (p.X - positions[i].X) * dx + (p.Z - positions[i].Z) * dz;
        private float Side(int i, float dx, float dz, Vec3 p) => (p.X - positions[i].X) * dz - (p.Z - positions[i].Z) * dx;

        private int? ArcVia(int i, float dx, float dz, int target, float startAhead, int straight)
        {
            float bx = positions[target].X - positions[i].X, bz = positions[target].Z - positions[i].Z;
            float bAhead = bx * dx + bz * dz, bSide = bx * dz - bz * dx;
            float lx = -dz, lz = dx;
            float half = -bSide / 2f;
            float apexAhead = Math.Max(bAhead, startAhead) + half;
            var apex = new Vec3(positions[i].X + dx * apexAhead + lx * half, positions[i].Y, positions[i].Z + dz * apexAhead + lz * half);
            int via = -1;
            float viaDistance = 5f;
            foreach (int j in Near(apex, 5f))
            {
                if (!intersections[j] || j == target || j == straight || Math.Abs(positions[j].Y - positions[i].Y) > 2f) continue;
                if (!AnyHeading(j, out float hx, out float hz) || hx * lx + hz * lz < 0.3f) continue;
                float d = Vec3.HorizontalDistance(apex, positions[j]);
                if (d < viaDistance) { viaDistance = d; via = j; }
            }
            return via >= 0 ? via : (int?)null;
        }
        public bool PlainHeading(int i, out float dx, out float dz) => Heading(i, out dx, out dz);
        public int InferredUTurns { get; private set; }

        private void InferUTurns()
        {
            for (int i = 0; i < Count; i++)
            {
                if (!IsJunctionEntry(i) || !Heading(i, out float dx, out float dz) || HasParallel(i, false)) continue;
                var chain = new List<int>();
                int straight = StraightInto(i, dx, dz, 14f, out float startAhead, chain);
                if (straight < 0) continue;
                int best = -1;
                float bestScore = float.MaxValue;
                foreach (int j in Near(positions[i], 50f))
                {
                    if (!intersections[j] || !AnyHeading(j, out float ex, out float ez) || ex * dx + ez * dz > -0.9f) continue;
                    if (Math.Abs(positions[j].Y - positions[i].Y) > 2f) continue;
                    float ox = positions[j].X - positions[i].X, oz = positions[j].Z - positions[i].Z;
                    float ahead = ox * dx + oz * dz, side = ox * dz - oz * dx;
                    if (ahead < startAhead - 2f || ahead > startAhead + 20f || side > -8f || side < -30f) continue;
                    float score = Math.Abs(-side - 15f) * 2f + Math.Abs(ahead - (startAhead + 4f));
                    if (score < bestScore) { bestScore = score; best = j; }
                }
                if (best < 0) continue;
                uTurns[i] = best;
                uTurnStarts[i] = straight;
                InferredUTurns++;
                float bx = positions[best].X - positions[i].X, bz = positions[best].Z - positions[i].Z;
                float bAhead = bx * dx + bz * dz, bSide = bx * dz - bz * dx;
                float lx = -dz, lz = dx;
                float half = -bSide / 2f;
                float apexAhead = Math.Max(bAhead, startAhead) + half;
                var apex = new Vec3(positions[i].X + dx * apexAhead + lx * half, positions[i].Y, positions[i].Z + dz * apexAhead + lz * half);
                int via = -1;
                float viaDistance = 5f;
                foreach (int j in Near(apex, 5f))
                {
                    if (!intersections[j] || j == best || j == straight || Math.Abs(positions[j].Y - positions[i].Y) > 2f) continue;
                    if (!AnyHeading(j, out float hx, out float hz) || hx * lx + hz * lz < 0.3f) continue;
                    float d = Vec3.HorizontalDistance(apex, positions[j]);
                    if (d < viaDistance) { viaDistance = d; via = j; }
                }
                if (via >= 0) chain.Add(via);
                turnVia[(i, best)] = chain.ToArray();
            }
        }

        private int StraightInto(int i, float dx, float dz, float metres, out float ahead) => StraightInto(i, dx, dz, metres, out ahead, null);

        private int StraightInto(int i, float dx, float dz, float metres, out float ahead, List<int>? chain)
        {
            ahead = 0;
            int current = i;
            chain?.Clear();
            for (int steps = 0; steps < 40; steps++)
            {
                int next = -1;
                float bestAlign = 0.95f;
                foreach (int j in successors[current])
                {
                    float x = positions[j].X - positions[current].X, z = positions[j].Z - positions[current].Z;
                    float length = (float)Math.Sqrt(x * x + z * z);
                    if (length < 0.1f) continue;
                    float align = (x * dx + z * dz) / length;
                    if (align > bestAlign) { bestAlign = align; next = j; }
                }
                if (next < 0 || !intersections[next]) return -1;
                current = next;
                chain?.Add(current);
                ahead = (positions[current].X - positions[i].X) * dx + (positions[current].Z - positions[i].Z) * dz;
                if (ahead >= metres) return current;
            }
            return -1;
        }

        private bool AnyHeading(int i, out float dx, out float dz)
        {
            dx = dz = 0;
            if (successors[i].Count != 1) return false;
            Vec3 a = positions[i], b = positions[successors[i][0]];
            float x = b.X - a.X, z = b.Z - a.Z, length = (float)Math.Sqrt(x * x + z * z);
            if (length < 0.5f) return false;
            dx = x / length;
            dz = z / length;
            return true;
        }

        // Turns the game's traffic data leaves out at a junction (spike 7: four lanes going straight on only at
        // three junctions in a row). From a junction entry i, the exits of the same junction it can't reach.
        public IReadOnlyList<int> MissingTurns(int i) => turns[i];
        public int InferredTurns { get; private set; }
        public int TrimmedChains { get; private set; }

        private void InferTurns()
        {
            for (int i = 0; i < Count; i++)
            {
                if (!IsJunctionEntry(i) || !Heading(i, out float dx, out float dz)) continue;
                var reached = JunctionExits(i);
                List<int>? found = null;
                foreach (int x in Near(positions[i], 45f))
                {
                    if (reached.Contains(x) || !IsJunctionExit(x) || !Heading(x, out float ex, out float ez)) continue;
                    if (Math.Abs(positions[x].Y - positions[i].Y) > 2f) continue;
                    float ox = positions[x].X - positions[i].X, oz = positions[x].Z - positions[i].Z;
                    float ahead = ox * dx + oz * dz, side = ox * dz - oz * dx;
                    float turn = ex * dz - ez * dx;
                    bool right = turn > 0;
                    if (ahead < 2f || Math.Abs(ex * dx + ez * dz) > (right ? 0.75f : 0.5f)) continue;
                    if (right != side > 0) continue;
                    float distance = (float)Math.Sqrt(ox * ox + oz * oz);
                    if (distance > (right ? 25f : 60f)) continue;
                    if (HasParallel(i, right) || HasParallel(x, right)) continue;
                    float radius = right ? Math.Min(Math.Abs(side), 9f) : 5f;
                    var chain = new List<int>();
                    int straight = ahead - radius >= 4f ? StraightInto(i, dx, dz, ahead - radius, out _, chain) : -1;
                    if (straight >= 0)
                    {
                        while (chain.Count > 0 && (positions[chain[chain.Count - 1]].X - positions[i].X) * dx + (positions[chain[chain.Count - 1]].Z - positions[i].Z) * dz > ahead - radius + 2f)
                            chain.RemoveAt(chain.Count - 1);
                        if (chain.Count == 0 || chain[chain.Count - 1] != straight) TrimmedChains++;
                        straight = chain.Count > 0 ? chain[chain.Count - 1] : -1;
                    }
                    var via = straight >= 0 ? straight : ArcVia(i, dx, dz, x, ex, ez, right);
                    if (via < 0 && (!right || distance > 15f)) continue;
                    (found ??= new List<int>()).Add(x);
                    if (straight >= 0) turnVia[(i, x)] = chain.ToArray();
                    else if (via >= 0) turnVia[(i, x)] = new[] { via };
                }
                if (found == null) continue;
                turns[i] = found.AsReadOnly();
                InferredTurns++;
            }
        }

        private readonly Dictionary<(int, int), int[]> turnVia = new Dictionary<(int, int), int[]>();

        public IReadOnlyList<int> TurnVias(int i, int x) => turnVia.TryGetValue((i, x), out var vias) ? vias : Array.Empty<int>();

        private int ArcVia(int i, float dx, float dz, int x, float ex, float ez, bool right = true)
        {
            float px = positions[i].X, pz = positions[i].Z, qx = positions[x].X, qz = positions[x].Z;
            float denominator = dx * ez - dz * ex;
            if (Math.Abs(denominator) < 0.2f) return -1;
            float t = ((qx - px) * ez - (qz - pz) * ex) / denominator;
            float cx = px + t * dx, cz = pz + t * dz;
            float mx = (px + qx) / 2, mz = (pz + qz) / 2;
            var arc = right
                ? new Vec3(cx + (mx - cx) * 0.3f, positions[i].Y, cz + (mz - cz) * 0.3f)
                : new Vec3(cx + ex * 5f, positions[i].Y, cz + ez * 5f);
            float bx = dx + ex, bz = dz + ez, bl = (float)Math.Sqrt(bx * bx + bz * bz);
            if (bl < 0.1f) return -1;
            bx /= bl;
            bz /= bl;
            int best = -1;
            float bestDistance = 6f;
            foreach (int j in Near(arc, 6f))
            {
                if (!intersections[j] || Math.Abs(positions[j].Y - positions[i].Y) > 2f) continue;
                if (!AnyHeading(j, out float hx, out float hz) || hx * bx + hz * bz < 0.3f) continue;
                float d = Vec3.HorizontalDistance(arc, positions[j]);
                if (d < bestDistance) { bestDistance = d; best = j; }
            }
            return best;
        }

        public bool IsJunctionExitWaypoint(int x) => IsJunctionExit(x);
        public bool IsJunctionEntryWaypoint(int i) => IsJunctionEntry(i);

        private bool IsJunctionEntry(int i)
        {
            if (intersections[i] || successors[i].Count == 0) return false;
            foreach (int j in successors[i]) if (!intersections[j]) return false;
            return true;
        }

        private bool IsJunctionExit(int x)
        {
            if (intersections[x] || successors[x].Count == 0) return false;
            foreach (int j in predecessors[x]) if (intersections[j]) return true;
            return false;
        }

        private HashSet<int> JunctionExits(int entry)
        {
            var exits = new HashSet<int>();
            var seen = new HashSet<int> { entry };
            var stack = new Stack<int>();
            stack.Push(entry);
            while (stack.Count > 0)
            {
                int u = stack.Pop();
                foreach (int v in successors[u])
                {
                    if (!seen.Add(v)) continue;
                    if (intersections[v]) { if (seen.Count < 400) stack.Push(v); }
                    else exits.Add(v);
                }
            }
            return exits;
        }

        private bool HasParallel(int i, bool right)
        {
            if (!Heading(i, out float dx, out float dz)) return false;
            foreach (int j in Near(positions[i], 6f))
            {
                if (j == i || !Heading(j, out float ex, out float ez) || dx * ex + dz * ez < 0.95f) continue;
                float ox = positions[j].X - positions[i].X, oz = positions[j].Z - positions[i].Z;
                float along = ox * dx + oz * dz, side = ox * dz - oz * dx;
                if (Math.Abs(along) <= 4f && Math.Abs(side) >= 2f && Math.Abs(side) <= 4.5f && (side > 0) == right) return true;
            }
            return false;
        }

        // Lane changes Gley's data leaves out: for each waypoint on a plain stretch without one, the waypoints of a
        // parallel lane beside it (same heading, 2 to 4.5 m to the side, level with it).
        private void InferLanes()
        {
            for (int i = 0; i < Count; i++)
            {
                if (lateral[i].Count > 0 || !Heading(i, out float dx, out float dz)) continue;
                List<int>? found = null;
                foreach (int j in Near(positions[i], 6f))
                {
                    if (j == i || Has(successors[i], j) || Has(predecessors[i], j) || !Heading(j, out float ex, out float ez)) continue;
                    if (dx * ex + dz * ez < 0.95f || Math.Abs(positions[j].Y - positions[i].Y) > 1f) continue;
                    float ox = positions[j].X - positions[i].X, oz = positions[j].Z - positions[i].Z;
                    float along = ox * dx + oz * dz, side = Math.Abs(ox * dz - oz * dx);
                    if (Math.Abs(along) > 3f || side < 2f || side > 7.5f) continue;
                    (found ??= new List<int>()).Add(j);
                }
                if (found == null) continue;
                lateral[i] = found.AsReadOnly();
                InferredLaneChanges++;
            }
        }

        private static bool Has(IReadOnlyList<int> list, int value)
        {
            for (int k = 0; k < list.Count; k++) if (list[k] == value) return true;
            return false;
        }

        private bool Heading(int i, out float dx, out float dz)
        {
            dx = dz = 0;
            if (intersections[i] || successors[i].Count != 1) return false;
            Vec3 a = positions[i], b = positions[successors[i][0]];
            float x = b.X - a.X, z = b.Z - a.Z, length = (float)Math.Sqrt(x * x + z * z);
            if (length < 0.5f) return false;
            dx = x / length;
            dz = z / length;
            return true;
        }

        public float SideOf(int i, int j)
        {
            if (!Heading(i, out float dx, out float dz)) return 0;
            float ox = positions[j].X - positions[i].X, oz = positions[j].Z - positions[i].Z;
            return ox * dz - oz * dx;
        }

        public float SideOf(int i, Vec3 p)
        {
            if (!Heading(i, out float dx, out float dz)) return 0;
            float ox = p.X - positions[i].X, oz = p.Z - positions[i].Z;
            return ox * dz - oz * dx;
        }

        public int GivenLaneChanges { get; }
        public int InferredLaneChanges { get; private set; }

        public IReadOnlyList<int> Lateral(int i) => lateral[i];

        public int Count => positions.Length;
        public int DroppedEdges { get; }
        public float TopSpeedMs { get; }
        public Vec3 Position(int i) => positions[i];
        public IReadOnlyList<int> Successors(int i) => successors[i];
        public IReadOnlyList<int> Predecessors(int i) => predecessors[i];
        public bool IsIntersection(int i) => intersections[i];
        public float SpeedMs(int i) => speeds[i];
        public bool Allows(int i, int vehicleType) => vehicleType >= 0 && vehicleType < 32 && (masks[i] & (1 << vehicleType)) != 0;

        public IEnumerable<int> Near(Vec3 p, float radius)
        {
            if (!Finite(radius) || radius < 0) throw new ArgumentOutOfRangeException(nameof(radius));
            if (!Finite(p.X) || !Finite(p.Y) || !Finite(p.Z)) throw new ArgumentException("Position must be finite.");
            double minX = Math.Floor(((double)p.X - radius) / cellSize), maxX = Math.Floor(((double)p.X + radius) / cellSize);
            double minZ = Math.Floor(((double)p.Z - radius) / cellSize), maxZ = Math.Floor(((double)p.Z + radius) / cellSize);
            if ((maxX - minX + 1) * (maxZ - minZ + 1) > Math.Max(1, grid.Count) * 4.0)
            {
                for (int i = 0; i < Count; i++) if (Vec3.HorizontalDistance(p, positions[i]) <= radius) yield return i;
                yield break;
            }
            if (minX < int.MinValue || maxX > int.MaxValue || minZ < int.MinValue || maxZ > int.MaxValue) yield break;
            for (long x = (long)minX; x <= maxX; x++)
                for (long z = (long)minZ; z <= maxZ; z++)
                    if (grid.TryGetValue(((int)x, (int)z), out var bucket))
                        foreach (int i in bucket) if (Vec3.HorizontalDistance(p, positions[i]) <= radius) yield return i;
        }

        public int Nearest(Vec3 p, float maxRadius, Func<int, bool>? filter = null)
        {
            int best = -1;
            float distance = float.PositiveInfinity;
            foreach (int i in Near(p, maxRadius))
            {
                if (filter != null && !filter(i)) continue;
                float d = Vec3.HorizontalDistance(p, positions[i]);
                if (d < distance || (d == distance && i < best)) { best = i; distance = d; }
            }
            return best;
        }

        private int Cell(float coordinate)
        {
            double cell = Math.Floor((double)coordinate / cellSize);
            if (cell < int.MinValue || cell > int.MaxValue) throw new ArgumentException("Position exceeds the grid range.");
            return (int)cell;
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
