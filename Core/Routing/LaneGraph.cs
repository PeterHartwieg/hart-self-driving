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
            for (int i = 0; i < n; i++) turns[i] = Array.Empty<int>();
            if (inferTurns) InferTurns();
        }

        private readonly IReadOnlyList<int>[] turns;

        // Turns the game's traffic data leaves out at a junction (spike 7: four lanes going straight on only at
        // three junctions in a row). From a junction entry i, the exits of the same junction it can't reach.
        public IReadOnlyList<int> MissingTurns(int i) => turns[i];
        public int InferredTurns { get; private set; }

        // A right turn goes from the rightmost lane into the rightmost lane of the cross street, a left turn from the
        // leftmost lane into the leftmost one. The exit must be ahead, at roughly a right angle, on the turn's side,
        // and within 25 m (right) or 45 m (left) of the entry.
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
                    float turn = ex * dz - ez * dx; // > 0: the exit heads to the entry's right
                    if (ahead < 2f || Math.Abs(ex * dx + ez * dz) > 0.4f) continue;
                    bool right = turn > 0;
                    if (right != side > 0) continue;
                    float distance = (float)Math.Sqrt(ox * ox + oz * oz);
                    if (distance > (right ? 25f : 45f)) continue;
                    // Outermost lanes on the turn's side, at both ends.
                    if (HasParallel(i, right) || HasParallel(x, right)) continue;
                    (found ??= new List<int>()).Add(x);
                }
                if (found == null) continue;
                turns[i] = found.AsReadOnly();
                InferredTurns++;
            }
        }

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

        // The plain waypoints reachable from a junction entry through the junction's own waypoints.
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

        // A lane in the same direction beside i on the given side (2 to 4.5 m), from the waypoint layout itself.
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
                    if (Math.Abs(along) > 3f || side < 2f || side > 4.5f) continue;
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

        // The unit heading of a plain waypoint (not in a junction, one successor) on the ground plane.
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

        // Which side of i's lane j is on: positive to the right of the heading (Unity's x right, z forward).
        public float SideOf(int i, int j)
        {
            if (!Heading(i, out float dx, out float dz)) return 0;
            float ox = positions[j].X - positions[i].X, oz = positions[j].Z - positions[i].Z;
            return ox * dz - oz * dx;
        }

        // The same for a point: positive when p is to the right of i's lane.
        public float SideOf(int i, Vec3 p)
        {
            if (!Heading(i, out float dx, out float dz)) return 0;
            float ox = p.X - positions[i].X, oz = p.Z - positions[i].Z;
            return ox * dz - oz * dx;
        }

        public int GivenLaneChanges { get; }
        public int InferredLaneChanges { get; private set; }

        // Gley's otherLanes: the waypoints a car can change lanes to from i. Its AI also uses them to leave a lane
        // that leads nowhere.
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
            // A very wide query is cheaper as a scan than as millions of empty cell lookups.
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
