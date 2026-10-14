using System;
using System.Collections.Generic;
using DrivenRides.Core.Routing;

namespace DrivenRides.Core.Testing
{
    // Where the road past a manoeuvre ends: the kerb is half a lane past the outermost lane on the right (the lane
    // graph's lanes are mostly 4 m apart) and a parking lane's width more, since a parking lane isn't in the lane
    // graph. A footprint more than 0.3 m past it (as the judge's intrusion) is on the pavement. Judged by geometry,
    // not by height: Peter saw the limo end U-turns on the pavement in runs the rig passed, and neither the wheels'
    // heights (0.1.56) nor the body found anything in 189 runs, so there the pavement is flush with the road or the
    // NWH wheel rays don't see it. T2 (session 20261013-182118): U-turns the planner's check passed ran 5 m wide
    // through the parking lane into a parked van; the ones that passed in game swung up to 1.5 m past the lane's edge.
    // Only out of the junction (a street opens on every side there; a U-turn through its vias swings up to 6 m off
    // its line in turns that passed) and only on the right (a U-turn crosses the median's side at its opening).
    public sealed class Pavement
    {
        public const float LaneHalf = 2f;
        // A parking lane's width beyond the outermost lane; settable for tools/rejudge (--parking) to see how much
        // of it runs use.
        public static float ParkingLane = 2.5f;
        public static float KerbOffset => LaneHalf + ParkingLane;
        // The left kerb too (a median's or a one-way street's), half a lane past the leftmost lane where no oncoming
        // lane is; for tools/rejudge (--left) while it is being measured.
        public static bool Left;
        public const float Tolerance = 0.3f;

        private readonly LaneGraph graph;
        private readonly List<LaneStretch> stretches = new List<LaneStretch>();

        // The route's lane steps from `from` on that leave or join a junction or run outside one.
        internal Pavement(LaneGraph graph, IReadOnlyList<int> route, int from)
        {
            this.graph = graph;
            for (var k = Math.Max(0, from); k + 1 < route.Count; k++)
            {
                int a = route[k], b = route[k + 1];
                if (!Has(graph.Successors(a), b) || (graph.IsIntersection(a) && graph.IsIntersection(b))) continue;
                var stretch = new LaneStretch(graph, graph.Position(a), graph.Position(b), k, edges: true);
                if (stretch.Length < 0.05f) continue;
                stretch.Min = -1f;
                stretch.Max = stretch.Length + 1f;
                stretches.Add(stretch);
            }
        }

        // A footprint point past the kerb beside the stretch it stands on (the nearest one along whose length it
        // is), unless it stands on another lane (a cross street's mouth): what and where, or null.
        internal string? Past(Vec3[] footprint)
        {
            foreach (var p in footprint)
            {
                LaneStretch? nearest = null;
                var nearestSide = float.MaxValue;
                foreach (var s in stretches)
                {
                    var along = s.Along(p);
                    if (along < s.Min || along > s.Max || Math.Abs(p.Y - s.Height(along)) > 2f) continue;
                    var distance = Math.Abs(s.Side(p) - (s.OwnLeft + s.OwnRight) / 2f);
                    if (distance < nearestSide) { nearestSide = distance; nearest = s; }
                }
                if (nearest == null) continue;
                if (float.IsPositiveInfinity(nearest.Right))
                {
                    var past = nearest.Side(p) - nearest.OwnRight - KerbOffset;
                    if (past > Tolerance && !OnALane(graph, p))
                        return FormattableString.Invariant($"pavement: {past:0.00} m past the kerb right of route segment {nearest.Index}, at ({p.X:0.0}, {p.Z:0.0})");
                }
                if (Left && float.IsNegativeInfinity(nearest.Left))
                {
                    var past = nearest.OwnLeft - LaneHalf - nearest.Side(p);
                    if (past > Tolerance && !OnALane(graph, p))
                        return FormattableString.Invariant($"pavement: {past:0.00} m past the kerb left of route segment {nearest.Index}, at ({p.X:0.0}, {p.Z:0.0})");
                }
            }
            return null;
        }

        // A footprint corner, pulled Judge.PavementInset inward, on pedestrian ground (the game's pedestrians' NavMesh)
        // and not carriageway (OnALane): what and where, with how far it is from the nearest lane, or null. The rig's
        // judge and the planner's sweep share it. inBox: told of each corner on pedestrian ground that counts as
        // carriageway only for lying in a junction's box, and its distance from the nearest lane (the rig records the
        // farthest, so T3 shows whether a box holds real pavement).
        public static string? CornerOnPavement(LaneGraph graph, Vec3[] footprint, Vec3 centre, Func<Vec3, bool> pedestrianGround, Action<Vec3, float>? inBox = null)
        {
            for (int k = 0; k < 4 && k < footprint.Length; k++)
            {
                Vec3 corner = footprint[k];
                float dx = centre.X - corner.X, dz = centre.Z - corner.Z, length = (float)Math.Sqrt(dx * dx + dz * dz);
                if (length > Judge.PavementInset) corner = new Vec3(corner.X + dx / length * Judge.PavementInset, corner.Y, corner.Z + dz / length * Judge.PavementInset);
                if (!pedestrianGround(corner)) continue;
                var distance = LaneDistance(graph, corner);
                if (distance <= LaneHalf) continue;
                if (graph.Junctions.Contains(corner)) { inBox?.Invoke(corner, distance); continue; }
                return FormattableString.Invariant($"pavement: a corner on pedestrian ground at ({corner.X:0.0}, {corner.Z:0.0}), {distance:0.0} m from the nearest lane, outside any junction's box");
            }
            return null;
        }

        // Whether a point is carriageway though pedestrian ground: in a junction's box (JunctionBoxes: its crossings
        // and a median's opening), or within half a lane of any lane of the graph, whatever its direction (a promenade
        // beside a lane by the pier).
        public static bool OnALane(LaneGraph graph, Vec3 p) => graph.Junctions.Contains(p) || LaneDistance(graph, p) <= LaneHalf;

        // Whether a point outside every junction's box lies on a lane going another way than any of `ways` (unit
        // headings, x and z): within half a lane of a lane step, and none of the lane steps it is that near to
        // within 45 degrees of one of them. A turn's body may sweep its own direction's lanes and the junction, never
        // the oncoming lanes before or after it (0.1.100: a left turn's arc cut across the oncoming lanes' mouth).
        public static bool OnAnotherWaysLane(LaneGraph graph, Vec3 p, IReadOnlyList<(float X, float Z)> ways)
        {
            if (graph.Junctions.Contains(p)) return false;
            var other = false;
            foreach (var w in graph.Near(p, 30f))
            {
                var a = graph.Position(w);
                if (Math.Abs(a.Y - p.Y) > 2f) continue;
                foreach (var n in graph.Successors(w))
                {
                    var b = graph.Position(n);
                    float dx = b.X - a.X, dz = b.Z - a.Z, span = dx * dx + dz * dz;
                    if (span < 1e-4f) continue;
                    var f = Math.Max(0f, Math.Min(1f, ((p.X - a.X) * dx + (p.Z - a.Z) * dz) / span));
                    float qx = a.X + dx * f - p.X, qz = a.Z + dz * f - p.Z;
                    if (qx * qx + qz * qz > LaneHalf * LaneHalf) continue;
                    var length = (float)Math.Sqrt(span);
                    foreach (var (wx, wz) in ways)
                        if ((dx * wx + dz * wz) / length > 0.707f) return false;
                    other = true;
                }
            }
            return other;
        }

        // How far a point is from the nearest lane step of the graph level with it (within 30 m; infinity if none).
        public static float LaneDistance(LaneGraph graph, Vec3 p)
        {
            var best = float.PositiveInfinity;
            foreach (var w in graph.Near(p, 30f))
            {
                var a = graph.Position(w);
                if (Math.Abs(a.Y - p.Y) > 2f) continue;
                foreach (var n in graph.Successors(w))
                {
                    var b = graph.Position(n);
                    float dx = b.X - a.X, dz = b.Z - a.Z, span = dx * dx + dz * dz;
                    var f = span > 0f ? Math.Max(0f, Math.Min(1f, ((p.X - a.X) * dx + (p.Z - a.Z) * dz) / span)) : 0f;
                    float qx = a.X + dx * f - p.X, qz = a.Z + dz * f - p.Z;
                    best = Math.Min(best, (float)Math.Sqrt(qx * qx + qz * qz));
                }
            }
            return best;
        }

        private static bool Has(IReadOnlyList<int> list, int value)
        {
            for (var k = 0; k < list.Count; k++) if (list[k] == value) return true;
            return false;
        }
    }
}
