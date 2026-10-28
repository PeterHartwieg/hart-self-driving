using System;
using System.Collections.Generic;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;

namespace DrivenRides.Core.Drive
{
    // The line a car driven by its own physics follows (docs/nwh-drive-spike.md): the ride's path through its
    // waypoints, with each added turn's driver's arc (TurnArc) in place of the corner. Each path position knows how
    // far along the line it is, for the speed plan and the stop lines.
    public sealed class DriveLine
    {
        public Polyline Line { get; }
        // Metres along Line of each path position; a waypoint the arc cuts past gets its nearest point on the line.
        public float[] AlongOf { get; }
        // The stretches (metres along) where the line is a fitted arc or U-turn curve, lead-in and run-out included:
        // there the driver may steer for the line's own curvature (PursuitDriver's feed-forward).
        public List<(float From, float To)> Curves { get; } = new List<(float, float)>();

        private DriveLine(Polyline line, float[] alongOf) { Line = line; AlongOf = alongOf; }

        // points: the path's waypoint positions. arcs: path position of each arc turn's entry (its exit is the next
        // position) and the arc, which starts TurnArc.LeadIn before the entry and runs out down the exit lane.
        // fillets: path positions of plain lane corners (both steps lane steps) to round off (Fillet).
        public static DriveLine Build(IReadOnlyList<Vec3> points, IReadOnlyList<(int Entry, Polyline Arc)> arcs, ICollection<int>? fillets = null)
        {
            var line = new List<Vec3>();
            var along = new float[points.Count];
            var known = new bool[points.Count];
            var sum = 0f;
            void Add(Vec3 p)
            {
                if (line.Count > 0)
                {
                    var step = Vec3.HorizontalDistance(line[line.Count - 1], p);
                    if (step < 0.05f) return;
                    sum += step;
                }
                line.Add(p);
            }
            var curves = new List<(float, float)>();
            var byEntry = new Dictionary<int, Polyline>();
            foreach (var (entry, arc) in arcs) if (entry >= 0 && entry + 1 < points.Count) byEntry[entry] = arc;
            var previousWaypoint = -1;
            for (var k = 0; k < points.Count; k++)
            {
                // A plain lane's corner, between two waypoints the line runs straight to and from, is rounded off.
                if (fillets != null && fillets.Contains(k) && previousWaypoint == k - 1 && k + 1 < points.Count && !byEntry.ContainsKey(k)
                    && Fillet(points[k - 1], points[k], points[k + 1]) is List<Vec3> round)
                {
                    var middle = round.Count / 2;
                    for (var i = 0; i < round.Count; i++)
                    {
                        Add(round[i]);
                        if (i == middle) along[k] = sum;
                    }
                }
                else
                {
                    Add(points[k]);
                    along[k] = sum;
                }
                known[k] = true;
                previousWaypoint = k;
                if (!byEntry.TryGetValue(k, out var turn) || turn.Points.Count < 2) continue;
                previousWaypoint = -1;
                // The arc from the entry waypoint on (its lead-in is the entry lane, already on the line).
                var from = 0;
                var walked = 0f;
                for (var i = 1; i < turn.Points.Count; i++)
                {
                    walked += Vec3.HorizontalDistance(turn.Points[i - 1], turn.Points[i]);
                    if (walked >= TurnArc.LeadIn - 0.01f) { from = i; break; }
                }
                var curveFrom = sum;
                for (var i = from; i < turn.Points.Count; i++) Add(turn.Points[i]);
                curves.Add((curveFrom, sum));
                // The waypoints the arc passes (its exit and the exit lane up to the run-out's end) are skipped.
                var end = turn.Points[turn.Points.Count - 1];
                var before = turn.Points[turn.Points.Count - 2];
                float ex = end.X - before.X, ez = end.Z - before.Z;
                var n = (float)Math.Sqrt(ex * ex + ez * ez);
                if (n > 0f) { ex /= n; ez /= n; }
                while (k + 1 < points.Count && (points[k + 1].X - end.X) * ex + (points[k + 1].Z - end.Z) * ez < 0.5f) k++;
            }
            var polyline = new Polyline(line);
            // The skipped waypoints: their nearest point on the line, searched from the last known one.
            var last = 0f;
            for (var k = 0; k < points.Count; k++)
            {
                if (known[k]) { last = along[k]; continue; }
                along[k] = NearestAlong(polyline, points[k], last);
                last = along[k];
            }
            var built = new DriveLine(polyline, along);
            built.Curves.AddRange(curves);
            return built;
        }

        // The line moved sideways over stretches (a car parked partly in the lane, passed within it; NwhPlanner): each
        // shift is the path position whose step it starts on, metres along that step where the obstacle begins and
        // ends, and the offset (right positive); it eases in and out over `ramp` metres either side.
        public const float ShiftRamp = 10f;

        public DriveLine Shifted(IReadOnlyList<(int Position, float From, float To, float Offset)> shifts, float ramp = ShiftRamp)
        {
            if (shifts.Count == 0) return this;
            var spans = new List<(float From, float To, float Offset)>();
            foreach (var (position, from, to, offset) in shifts)
                if (position >= 0 && position < AlongOf.Length) spans.Add((AlongOf[position] + from, AlongOf[position] + to, offset));
            float OffsetAt(float a)
            {
                var o = 0f;
                foreach (var (from, to, offset) in spans)
                {
                    float w;
                    if (a >= from && a <= to) w = 1f;
                    else if (a < from && a > from - ramp) w = Ease((a - (from - ramp)) / ramp);
                    else if (a > to && a < to + ramp) w = Ease(((to + ramp) - a) / ramp);
                    else w = 0f;
                    // Overlapping shifts (one obstacle over two steps) don't add up: the larger one holds.
                    if (Math.Abs(offset * w) > Math.Abs(o)) o = offset * w;
                }
                return o;
            }
            bool Near(float a)
            {
                foreach (var (from, to, _) in spans) if (a > from - ramp - 1f && a < to + ramp + 1f) return true;
                return false;
            }
            // The old line, a point a metre where it moves, with each point's distance along.
            var old = new List<(Vec3 P, float A)>();
            var pts = Line.Points;
            for (var i = 0; i < pts.Count; i++)
            {
                if (i > 0)
                {
                    float a0 = Along(i - 1), a1 = Along(i);
                    if (Near(a0) || Near(a1))
                        for (var a = a0 + 1f; a < a1 - 0.25f; a += 1f) old.Add((Line.At(a), a));
                }
                old.Add((pts[i], Along(i)));
            }
            var moved = new List<Vec3>(old.Count);
            var newAlong = new float[old.Count];
            var sum = 0f;
            for (var i = 0; i < old.Count; i++)
            {
                var (p, a) = old[i];
                var o = OffsetAt(a);
                var q = p;
                if (o != 0f)
                {
                    Vec3 b = old[Math.Max(0, i - 1)].P, c = old[Math.Min(old.Count - 1, i + 1)].P;
                    float dx = c.X - b.X, dz = c.Z - b.Z, n = (float)Math.Sqrt(dx * dx + dz * dz);
                    if (n > 1e-4f) q = new Vec3(p.X + dz / n * o, p.Y, p.Z - dx / n * o);
                }
                if (i > 0) sum += Vec3.HorizontalDistance(moved[i - 1], q);
                moved.Add(q);
                newAlong[i] = sum;
            }
            float Map(float a)
            {
                for (var i = 1; i < old.Count; i++)
                    if (old[i].A >= a)
                    {
                        var span = old[i].A - old[i - 1].A;
                        var f = span > 1e-4f ? (a - old[i - 1].A) / span : 0f;
                        return newAlong[i - 1] + (newAlong[i] - newAlong[i - 1]) * f;
                    }
                return sum;
            }
            var along = new float[AlongOf.Length];
            for (var k = 0; k < along.Length; k++) along[k] = Map(AlongOf[k]);
            var shifted = new DriveLine(new Polyline(moved), along);
            foreach (var (from, to) in Curves) shifted.Curves.Add((Map(from), Map(to)));
            return shifted;
        }

        private static float Ease(float t)
        {
            t = Math.Max(0f, Math.Min(1f, t));
            return t * t * (3f - 2f * t);
        }

        // A plain lane's corner at v rounded off as a driver takes it (Peter, T3: a Hamptons drive whose waypoints
        // kink 35 degrees round a gate pillar's island): a smooth curve from a point on the way in to one on the way
        // out, at most half way along either side, cutting the corner by at most 1 m. Null for a corner under 8
        // degrees (nothing to round) or over 100 (no lane corner).
        public static List<Vec3>? Fillet(Vec3 a, Vec3 v, Vec3 b)
        {
            float ax = v.X - a.X, az = v.Z - a.Z, bx = b.X - v.X, bz = b.Z - v.Z;
            float la = (float)Math.Sqrt(ax * ax + az * az), lb = (float)Math.Sqrt(bx * bx + bz * bz);
            if (la < 1f || lb < 1f) return null;
            ax /= la; az /= la; bx /= lb; bz /= lb;
            var turn = Math.Acos(Math.Max(-1f, Math.Min(1f, ax * bx + az * bz)));
            if (turn < 8 * Math.PI / 180 || turn > 100 * Math.PI / 180) return null;
            // A quadratic curve through the corner as its control point: it cuts the corner by t sin(turn / 2) / 2.
            var t = Math.Min(Math.Min(la, lb) / 2f, (float)(2.0 * FilletCut / Math.Sin(turn / 2)));
            var t1 = new Vec3(v.X - ax * t, a.Y + (v.Y - a.Y) * (1f - t / la), v.Z - az * t);
            var t2 = new Vec3(v.X + bx * t, v.Y + (b.Y - v.Y) * (t / lb), v.Z + bz * t);
            var steps = Math.Max(4, (int)Math.Ceiling(2 * t / 0.5f));
            var round = new List<Vec3>(steps + 1);
            for (var i = 0; i <= steps; i++)
            {
                var f = (float)i / steps;
                float w0 = (1 - f) * (1 - f), w1 = 2 * (1 - f) * f, w2 = f * f;
                round.Add(new Vec3(w0 * t1.X + w1 * v.X + w2 * t2.X, w0 * t1.Y + w1 * v.Y + w2 * t2.Y, w0 * t1.Z + w1 * v.Z + w2 * t2.Z));
            }
            return round;
        }

        // How far a rounded corner may cut inside the lane's waypoints (a lane is about 4 m wide).
        public const float FilletCut = 1f;

        // Whether a car at a pose (rear axle, heading as atan2(dx, dz)) can take up this line where it stands: within
        // 1.5 m of it, its heading within 30 degrees of the line's there, and the line going on at least 15 m (review
        // round 1: a replanned route was installed from a waypoint behind the car, whatever the car's pose). The
        // distance is to the nearest point of the line, not the sideways part of it: a line that starts 20 m ahead of
        // the car or ends behind it is that far off (review round 2: a start beyond the blocked step passed).
        public bool Joins(float x, float z, double heading, out float along) => JoinMiss(x, z, heading, out along) == null;

        // Which of Joins' conditions a car at a pose misses, in words for the log, or null when it joins the line.
        public string? JoinMiss(float x, float z, double heading, out float along)
        {
            var (at, _) = Locate(x, z, 0f, Line.Length);
            along = at;
            var nearest = Line.At(at);
            var distance = (float)Math.Sqrt((x - nearest.X) * (x - nearest.X) + (z - nearest.Z) * (z - nearest.Z));
            if (distance > JoinLateral)
                return FormattableString.Invariant($"{distance:0.0} m from the line, {at:0} m along it (at most {JoinLateral} m)");
            if (at > Line.Length - 15f)
                return FormattableString.Invariant($"{Line.Length - at:0} m of line left past the car (at least 15 m)");
            Vec3 a = Line.At(at), b = Line.At(at + 1f);
            var lineHeading = Math.Atan2(b.X - a.X, b.Z - a.Z);
            var off = Math.Abs(Wrap(lineHeading - heading)) * 180 / Math.PI;
            if (off > JoinHeading)
                return FormattableString.Invariant($"facing {off:0} degrees off the line, {at:0} m along it (at most {JoinHeading} degrees)");
            return null;
        }

        public const float JoinLateral = 1.5f;
        public const float JoinHeading = 30f;

        // Where a point is relative to the line, searching `range` metres on from `from`: metres along, and metres
        // to the side (right positive).
        public (float Along, float Lateral) Locate(float x, float z, float from, float range)
        {
            var best = (Along: from, Lateral: float.MaxValue);
            var bestDistance = float.MaxValue;
            var pts = Line.Points;
            for (var i = 1; i < pts.Count; i++)
            {
                var start = Along(i - 1);
                if (Along(i) < from) continue;
                if (start > from + range) break;
                Vec3 a = pts[i - 1], b = pts[i];
                float dx = b.X - a.X, dz = b.Z - a.Z;
                var span = (float)Math.Sqrt(dx * dx + dz * dz);
                if (span < 1e-4f) continue;
                var f = Math.Max(0f, Math.Min(1f, ((x - a.X) * dx + (z - a.Z) * dz) / (span * span)));
                float px = a.X + dx * f, pz = a.Z + dz * f;
                var distance = (x - px) * (x - px) + (z - pz) * (z - pz);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = (start + span * f, ((x - px) * dz - (z - pz) * dx) / span);
            }
            return best;
        }

        // Metres along the line of a car at (x, z) for a display that is not asked every frame (the route line
        // switched off for a while; review round 1 of the route line): searched on from `previous`, but never from
        // further back than one waypoint before the last one RouteProgress says the car has passed (`next` is its
        // NextIndex), and only up to its next waypoint, so a stale value cannot hold onto an earlier pass round a block.
        public float Follow(float x, float z, float previous, int next)
        {
            if (AlongOf.Length == 0) return previous;
            var last = AlongOf.Length - 1;
            var from = Math.Max(previous, AlongOf[Math.Max(0, Math.Min(next - 2, last))]);
            var to = AlongOf[Math.Max(0, Math.Min(next, last))] + 10f;
            return Math.Max(from, Locate(x, z, from, Math.Max(10f, to - from)).Along);
        }

        private static float NearestAlong(Polyline line, Vec3 p, float from)
        {
            var best = from;
            var bestDistance = float.MaxValue;
            var pts = line.Points;
            var at = 0f;
            for (var i = 1; i < pts.Count; i++)
            {
                var a = pts[i - 1];
                var b = pts[i];
                var span = Vec3.HorizontalDistance(a, b);
                if (at + span >= from - 1f && at <= from + 80f)
                {
                    float dx = b.X - a.X, dz = b.Z - a.Z;
                    var f = span > 0f ? Math.Max(0f, Math.Min(1f, ((p.X - a.X) * dx + (p.Z - a.Z) * dz) / (span * span))) : 0f;
                    float qx = a.X + dx * f - p.X, qz = a.Z + dz * f - p.Z;
                    var d = qx * qx + qz * qz;
                    if (d < bestDistance) { bestDistance = d; best = at + span * f; }
                }
                at += span;
            }
            return best;
        }

        // The highest speed (m/s) at each point of the line for a lateral acceleration of `lateral` m/s², from its
        // curvature over a window of `window` metres; `top` where it is straight.
        public float[] CornerSpeeds(float top, float lateral, float window = 6f)
        {
            var pts = Line.Points;
            var speeds = new float[pts.Count];
            for (var i = 0; i < pts.Count; i++)
            {
                var s = Along(i);
                var a = Line.At(s - window / 2f);
                var b = Line.At(s);
                var c = Line.At(s + window / 2f);
                var h1 = Math.Atan2(b.X - a.X, b.Z - a.Z);
                var h2 = Math.Atan2(c.X - b.X, c.Z - b.Z);
                var turn = Math.Abs(Wrap(h2 - h1));
                var length = Vec3.HorizontalDistance(a, b) + Vec3.HorizontalDistance(b, c);
                // The two chords' headings are half the window apart along the line.
                var curvature = length > 0.5f ? turn / (length / 2) : 0.0;
                speeds[i] = curvature > 1e-4 ? (float)Math.Min(top, Math.Sqrt(lateral / curvature)) : top;
            }
            return speeds;
        }

        private readonly List<float> alongCache = new List<float>();

        public float Along(int point)
        {
            if (alongCache.Count == 0)
            {
                var sum = 0f;
                var pts = Line.Points;
                for (var i = 0; i < pts.Count; i++)
                {
                    if (i > 0) sum += Vec3.HorizontalDistance(pts[i - 1], pts[i]);
                    alongCache.Add(sum);
                }
            }
            return alongCache[point];
        }

        // The first point of the line at or after `at` metres along it.
        public int FirstPointFrom(float at)
        {
            Along(0);
            int lo = 0, hi = alongCache.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (alongCache[mid] < at) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        public static double Wrap(double a)
        {
            while (a > Math.PI) a -= 2 * Math.PI;
            while (a < -Math.PI) a += 2 * Math.PI;
            return a;
        }
    }
}
