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
            float[] maxSpeedKmh, float cellSize = 50f, int[][]? otherLanes = null, bool inferLanes = false)
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
