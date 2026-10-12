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
            if (inferTurns) { InferTurns(); InferUTurns(); }
        }

        private readonly IReadOnlyList<int>[] turns;
        private readonly int[] uTurns, uTurnStarts;

        // A U-turn at a junction into the other carriageway of a divided street (Peter: a player cuts across there,
        // waiting for a gap), or -1. The target is a waypoint inside the junction on the far carriageway, ahead of
        // the car: Gley skips a target behind the car as passed, and the far carriageway's exit is behind the entry.
        public int UTurn(int i) => uTurns[i];
        public int UTurnStart(int i) => uTurnStarts[i];
        public bool PlainHeading(int i, out float dx, out float dz) => Heading(i, out dx, out dz);
        public int InferredUTurns { get; private set; }

        private void InferUTurns()
        {
            for (int i = 0; i < Count; i++)
            {
                if (!IsJunctionEntry(i) || !Heading(i, out float dx, out float dz) || HasParallel(i, false)) continue;
                // The turn starts only well inside the junction, past the nose of the median (spike 11: turning at
                // the stop line hit the divider, and the median's nose stood 10 m past it): along the junction's own
                // straight-ahead waypoints, 14 m in at least.
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
                    // A car can't turn round tighter than about 5 m: best about 15 m across (a turning circle of about
                    // 7.5 m), and level with or a little beyond the turn's start.
                    if (ahead < startAhead - 2f || ahead > startAhead + 20f || side > -8f || side < -30f) continue;
                    float score = Math.Abs(-side - 15f) * 2f + Math.Abs(ahead - (startAhead + 4f));
                    if (score < bestScore) { bestScore = score; best = j; }
                }
                if (best < 0) continue;
                uTurns[i] = best;
                uTurnStarts[i] = straight;
                InferredUTurns++;
                // Then through a junction waypoint near the far end of the arc, so the car follows the curve.
                float bx = positions[best].X - positions[i].X, bz = positions[best].Z - positions[i].Z;
                float bAhead = bx * dx + bz * dz, bSide = bx * dz - bz * dx;
                float lx = -dz, lz = dx; // left of the heading
                float half = -bSide / 2f;
                float apexAhead = Math.Max(bAhead, startAhead) + half;
                var apex = new Vec3(positions[i].X + dx * apexAhead + lx * half, positions[i].Y, positions[i].Z + dz * apexAhead + lz * half);
                int via = -1;
                float viaDistance = 5f;
                foreach (int j in Near(apex, 5f))
                {
                    if (!intersections[j] || j == best || j == straight || Math.Abs(positions[j].Y - positions[i].Y) > 2f) continue;
                    // At the far end of the arc the car heads across, towards the turn's side; a lane heading the
                    // other way would put it on the wrong side.
                    if (!AnyHeading(j, out float hx, out float hz) || hx * lx + hz * lz < 0.3f) continue;
                    float d = Vec3.HorizontalDistance(apex, positions[j]);
                    if (d < viaDistance) { viaDistance = d; via = j; }
                }
                // The whole straight way in, so a red light's stop line on it holds the car (spike 18 ran one).
                if (via >= 0) chain.Add(via);
                turnVia[(i, best)] = chain.ToArray();
            }
        }

        // The first junction waypoint at least `metres` ahead of entry i along the junction's straight-ahead
        // waypoints, or -1 if the straight way leaves the junction first.
        private int StraightInto(int i, float dx, float dz, float metres, out float ahead) => StraightInto(i, dx, dz, metres, out ahead, null);

        // The same, collecting every junction waypoint on the way in `chain`, so the car's path passes the stop line.
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

        // The heading of any waypoint with one successor, junction waypoints included.
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

        // A right turn goes from the rightmost lane into the rightmost lane of the cross street, a left turn from the
        // leftmost lane into the leftmost one. The exit must be ahead, at roughly a right angle, on the turn's side,
        // and within 25 m (right) or 60 m (left, which may cross the other carriageway of a divided street) of the
        // entry.
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
                    bool right = turn > 0;
                    // Junctions aren't all square: a right turn may be from about 40 to 140 degrees (spike 16: a
                    // 130-degree right turn after the bridge was missed for a U-turn and a left), a left turn from
                    // about 60 to 120.
                    if (ahead < 2f || Math.Abs(ex * dx + ez * dz) > (right ? 0.75f : 0.5f)) continue;
                    if (right != side > 0) continue;
                    float distance = (float)Math.Sqrt(ox * ox + oz * oz);
                    if (distance > (right ? 25f : 60f)) continue;
                    // Outermost lanes on the turn's side, at both ends.
                    if (HasParallel(i, right) || HasParallel(x, right)) continue;
                    // Straight on into the junction until about a turning radius short of the exit's lane, then the
                    // turn, as a driver does. Turning at the stop line and cutting across drove a left turn through
                    // the oncoming lanes of the street it turned into (spike 11). A short turn, such as a right turn
                    // into the near lane, takes a waypoint on its curve instead.
                    // A left turn goes on until almost level with the exit's lane (3 m short), past the oncoming lanes
                    // of the street it turns into: a swing wide then carries the car into lanes going its own way, not
                    // into the oncoming ones (spike 12: it corrected at the end of a left turn, just short of them).
                    // 5 m, about the car's turning circle: from 3 m short it turned past 90 degrees and corrected
                    // back (spike 13). The ride also aims the car further down the exit's lane (Ride.Expand).
                    float radius = right ? Math.Min(Math.Abs(side), 9f) : 5f;
                    var chain = new List<int>();
                    int straight = ahead - radius >= 4f ? StraightInto(i, dx, dz, ahead - radius, out _, chain) : -1;
                    // Without a straight way in (a T-junction), a guide waypoint on the turn's line (ArcVia).
                    var via = straight >= 0 ? straight : ArcVia(i, dx, dz, x, ex, ez, right);
                    // Without a waypoint to steer through, a longer turn would cut the corner in a straight line, over
                    // the pavement at a T-junction (spike 16: into a pedestrian). Only a short turn goes without one.
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

        // A junction waypoint near the middle of the curve from i to its added-turn exit x, for the car to steer
        // through so it doesn't cut the inside corner on a straight line (spike 9); -1 if none is near.
        public IReadOnlyList<int> TurnVias(int i, int x) => turnVia.TryGetValue((i, x), out var vias) ? vias : Array.Empty<int>();

        // A guide waypoint for a turn without a straight way into the junction. Where the entry's line meets the exit
        // lane's line is the turn's corner. A right turn aims close to the corner, on the outer, squarer line (the
        // chord cut the inside corner, where poles and tree pits stand: spike 19); a left turn at a T-junction aims at
        // the exit lane 5 m past the corner, so the car crosses to it gently instead of overshooting into the kerb
        // at the top of the T.
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
            // Only a waypoint of a lane heading between the entry's and the exit's directions: one of the oncoming
            // lanes would steer the car onto the wrong side of the street (spike 11).
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
                    // Up to 7.5 m: a slip lane can stand that far from the next lane (spike 18: the lane for the right
                    // turn after the bridge); the ride's world check guards a change across a barrier.
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
