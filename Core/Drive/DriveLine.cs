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
        public static DriveLine Build(IReadOnlyList<Vec3> points, IReadOnlyList<(int Entry, Polyline Arc)> arcs)
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
            for (var k = 0; k < points.Count; k++)
            {
                Add(points[k]);
                along[k] = sum;
                known[k] = true;
                if (!byEntry.TryGetValue(k, out var turn) || turn.Points.Count < 2) continue;
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
