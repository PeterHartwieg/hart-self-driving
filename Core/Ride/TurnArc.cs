using System;
using System.Collections.Generic;
using DrivenRides.Core.Routing;

namespace DrivenRides.Core.Ride
{
    public sealed class Polyline
    {
        private readonly List<Vec3> points;
        private readonly List<float> at = new List<float>();

        public Polyline(List<Vec3> points)
        {
            this.points = points;
            var sum = 0f;
            for (var i = 0; i < points.Count; i++)
            {
                if (i > 0) sum += Vec3.HorizontalDistance(points[i - 1], points[i]);
                at.Add(sum);
            }
        }

        public IReadOnlyList<Vec3> Points => points;
        public float Radius { get; set; }
        public float Length => at.Count > 0 ? at[at.Count - 1] : 0f;

        public Vec3 At(float along)
        {
            if (points.Count == 0) return default;
            if (along <= 0f) return points[0];
            var i = FirstAtOrPast(along);
            if (i >= points.Count) return points[points.Count - 1];
            var span = at[i] - at[i - 1];
            var f = span > 0f ? (along - at[i - 1]) / span : 0f;
            return points[i - 1] + (points[i] - points[i - 1]) * f;
        }

        private int FirstAtOrPast(float along)
        {
            int lo = 1, hi = points.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (at[mid] < along) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        public float Project(float x, float z, float from)
        {
            var best = from;
            var bestDistance = float.MaxValue;
            for (var i = FirstAtOrPast(from - 1f); i < points.Count; i++)
            {
                if (at[i - 1] > from + 10f) break;
                Vec3 a = points[i - 1], b = points[i];
                float dx = b.X - a.X, dz = b.Z - a.Z;
                var span = dx * dx + dz * dz;
                var f = span > 0f ? Math.Max(0f, Math.Min(1f, ((x - a.X) * dx + (z - a.Z) * dz) / span)) : 0f;
                float px = a.X + dx * f - x, pz = a.Z + dz * f - z;
                var distance = px * px + pz * pz;
                if (distance < bestDistance) { bestDistance = distance; best = at[i - 1] + (at[i] - at[i - 1]) * f; }
            }
            return Math.Max(from, best);
        }
    }

    public static class TurnArc
    {
        private const double MinTurnDegrees = 30, MaxTurnDegrees = 150;
        public const float RadiusSlack = 1.15f;
        private const float MaxRadius = 30f;
        // No arc is tighter than this, whatever the car: the taxi's and the limo's turning circles in Gley (3.2 m and
        // 2 m) let them pivot round a corner on their back wheels (Peter, turn test 0.1.47). The 6.7 m car's
        // tightest followable arc, 6.6 m, looked right.
        public const float MinDriveRadius = 6.5f;
        private const float ExitSlack = 8f;
        private const float BoxExitSlack = 2f;
        private const float RunOut = 10f;
        // The line starts this far back along the entry lane: Gley lets go of the entry waypoint once the car's centre
        // is 5 m from it and then steered at the exit across the junction, into the oncoming lanes (turn test
        // 0.1.42); the ride holds the car on its lane from here.
        public const float LeadIn = 12f;
        private const float Spacing = 0.5f;

        private const float BoxMargin = 3.5f;

        public static Polyline? Fit(LaneGraph graph, int entry, int exit, float minRadius, float? radius = null)
        {
            if (!graph.PlainHeading(entry, out var ux, out var uz) || !graph.PlainHeading(exit, out var vx, out var vz)) return null;
            Vec3 e = graph.Position(entry), x = graph.Position(exit);
            var cross = ux * vz - uz * vx;
            if (Math.Abs(cross) < 0.2f) return null;
            float wx = x.X - e.X, wz = x.Z - e.Z;
            var a = (wx * vz - wz * vx) / cross;
            var b = (wx * uz - wz * ux) / cross;
            var meet = new Vec3(e.X + ux * a, e.Y, e.Z + uz * a);
            float first = a, last = b;
            foreach (var i in graph.Near(meet, 40f))
            {
                foreach (var n in graph.Successors(i))
                {
                    Vec3 p = graph.Position(i), q = graph.Position(n);
                    if (Crossing(e, ux, uz, p, q, out var s) && s >= 0f && s < first) first = s;
                    if (Crossing(x, vx, vz, p, q, out var t) && t <= 0f && t > b - 30f && t > last) last = t;
                }
            }
            return Fit(e, ux, uz, x, vx, vz, minRadius, radius, Math.Max(0f, first - BoxMargin), last + BoxMargin);
        }

        public const float PastMiddle = 4f;

        public static List<float> Radii(LaneGraph graph, int entry, int exit, float minRadius, float halfWidth = 1f)
        {
            var radii = new List<float>();
            if (!graph.PlainHeading(entry, out var ux, out var uz) || !graph.PlainHeading(exit, out var vx, out var vz)) return radii;
            Vec3 e = graph.Position(entry), x = graph.Position(exit);
            var cross = ux * vz - uz * vx;
            if (Math.Abs(cross) < 0.2f) return radii;
            var turn = Math.Atan2(Math.Abs(cross), ux * vx + uz * vz);
            float wx = x.X - e.X, wz = x.Z - e.Z;
            var a = (wx * vz - wz * vx) / cross;
            var b = (wx * uz - wz * ux) / cross;
            var room = Math.Min(a, ExitSlack - b);
            var least = Math.Max(minRadius * RadiusSlack, MinDriveRadius);
            if (room <= 0f) return radii;
            var largest = (float)Math.Min(MaxRadius, room / Math.Tan(turn / 2) - 0.01);
            var box = Fit(graph, entry, exit, minRadius)?.Radius;
            for (var r = largest; r > least; r *= 0.8f)
            {
                if (box is float bx && r <= bx + 0.5f) break;
                radii.Add(r);
            }
            if (box is float b0) radii.Add(b0);
            if (radii.Count == 0 || radii[radii.Count - 1] > least + 1f) radii.Add(least);
            if (cross > 0f)
            {
                var meet = new Vec3(e.X + ux * a, e.Y, e.Z + uz * a);
                var middle = RoadsMeet(graph, e, ux, uz, x, vx, vz)
                    ?? graph.Junctions.CentreOf(new Vec3((e.X + x.X) / 2f, e.Y, (e.Z + x.Z) / 2f)) ?? graph.Junctions.CentreOf(meet);
                if (middle is Vec3 c)
                {
                    float bxv = -ux + vx, bzv = -uz + vz;
                    var n = (float)Math.Sqrt(bxv * bxv + bzv * bzv);
                    if (n > 1e-4f)
                    {
                        var reach = ((c.X - meet.X) * bxv + (c.Z - meet.Z) * bzv) / n;
                        var apex = 1 / Math.Cos(turn / 2) - 1;
                        var keep = box ?? least;
                        radii.RemoveAll(r => r > keep + 0.01f && r * apex > reach + PastMiddle);
                    }
                }
            }
            return radii;
        }

        public static Vec3? RoadsMeet(LaneGraph graph, Vec3 e, float ux, float uz, Vec3 x, float vx, float vz)
        {
            if (!(CentreLine(graph, e, ux, uz, -30f, 2f) is float entryMid) || !(CentreLine(graph, x, vx, vz, -2f, 30f) is float exitMid)) return null;
            var p = new Vec3(e.X - uz * entryMid, e.Y, e.Z + ux * entryMid);
            var q = new Vec3(x.X - vz * exitMid, x.Y, x.Z + vx * exitMid);
            var cross = ux * vz - uz * vx;
            if (Math.Abs(cross) < 0.2f) return null;
            float wx = q.X - p.X, wz = q.Z - p.Z;
            var a = (wx * vz - wz * vx) / cross;
            return new Vec3(p.X + ux * a, p.Y, p.Z + uz * a);
        }

        private static float? CentreLine(LaneGraph graph, Vec3 at, float ux, float uz, float from, float to)
        {
            var owns = new List<float> { 0f };
            float? oncoming = null;
            foreach (var w in graph.Near(at, 40f))
            {
                if (graph.IsIntersection(w) || !graph.PlainHeading(w, out var hx, out var hz)) continue;
                var p = graph.Position(w);
                if (Math.Abs(p.Y - at.Y) > 2f) continue;
                float dx = p.X - at.X, dz = p.Z - at.Z;
                var along = dx * ux + dz * uz;
                if (along < from || along > to) continue;
                var left = -dx * uz + dz * ux;
                if (Math.Abs(left) > 25f) continue;
                var dot = hx * ux + hz * uz;
                if (dot > 0.9f) owns.Add(left);
                else if (dot < -0.9f && left > 0.5f && (oncoming == null || left < oncoming)) oncoming = left;
            }
            if (!(oncoming is float o)) return null;
            var own = 0f;
            foreach (var l in owns) if (l < o && l > own) own = l;
            return (own + o) / 2f;
        }

        private static bool Crossing(Vec3 from, float dx, float dz, Vec3 p, Vec3 q, out float along)
        {
            along = 0f;
            float sx = q.X - p.X, sz = q.Z - p.Z;
            var length = (float)Math.Sqrt(sx * sx + sz * sz);
            if (length < 0.01f) return false;
            var denominator = dx * sz - dz * sx;
            if (Math.Abs(denominator) / length < 0.7f) return false;
            float rx = p.X - from.X, rz = p.Z - from.Z;
            var f = (dx * rz - dz * rx) / denominator;
            if (f < 0f || f > 1f) return false;
            along = (rx * sz - rz * sx) / denominator;
            return true;
        }

        public static Polyline? Fit(Vec3 e, float ux, float uz, Vec3 x, float vx, float vz, float minRadius, float? radius = null,
            float startFrom = 0f, float endBy = BoxExitSlack)
        {
            var cross = ux * vz - uz * vx;
            var turn = Math.Atan2(Math.Abs(cross), ux * vx + uz * vz);
            if (turn * 180 / Math.PI < MinTurnDegrees || turn * 180 / Math.PI > MaxTurnDegrees) return null;
            float wx = x.X - e.X, wz = x.Z - e.Z;
            var a = (wx * vz - wz * vx) / cross;
            var b = (wx * uz - wz * ux) / cross;
            var room = Math.Min(a, ExitSlack - b);
            if (room <= 0f) return null;
            var half = Math.Tan(turn / 2);
            var box = Math.Max(0f, Math.Min(a - startFrom, endBy - b));
            var least = Math.Max(minRadius * RadiusSlack, MinDriveRadius);
            var r = radius ?? (float)Math.Min(MaxRadius, Math.Max(box / half, least));
            if (r < least || r * half > room + 0.01f) return null;
            var t = (float)(r * half);
            var t1 = new Vec3(e.X + ux * (a - t), e.Y, e.Z + uz * (a - t));
            var t2 = new Vec3(x.X + vx * (b + t), x.Y, x.Z + vz * (b + t));
            var side = cross > 0 ? -1f : 1f;
            var c = new Vec3(t1.X + side * uz * r, 0f, t1.Z - side * ux * r);
            var points = new List<Vec3> { new Vec3(e.X - ux * LeadIn, e.Y, e.Z - uz * LeadIn) };
            AddStraight(points, points[0], e);
            AddStraight(points, e, t1);
            double sx = t1.X - c.X, sz = t1.Z - c.Z;
            var sweep = cross > 0 ? -turn : turn;
            var steps = Math.Max(2, (int)Math.Ceiling(r * turn / Spacing));
            for (var k = 1; k <= steps; k++)
            {
                var phi = sweep * k / steps;
                double cos = Math.Cos(phi), sin = Math.Sin(phi);
                var f = (float)k / steps;
                points.Add(new Vec3((float)(c.X + sx * cos + sz * sin), e.Y + (x.Y - e.Y) * f, (float)(c.Z - sx * sin + sz * cos)));
            }
            var end = new Vec3(t2.X + vx * RunOut, x.Y, t2.Z + vz * RunOut);
            AddStraight(points, points[points.Count - 1], end);
            return new Polyline(points) { Radius = r };
        }

        public static Polyline? FitUTurn(LaneGraph graph, int entry, int target, float minRadius) => FitUTurn(graph, entry, target, minRadius, out _);

        public static Polyline? FitUTurn(LaneGraph graph, int entry, int target, float minRadius, out string why)
        {
            why = "not a U-turn with a plain entry";
            if (!graph.IsUTurn(entry, target) || !graph.PlainHeading(entry, out var ux, out var uz)) return null;
            int startWaypoint = graph.UTurnStart(entry, target);
            var lane = target;
            for (var guard = 0; guard < 20 && graph.IsIntersection(lane) && graph.Successors(lane).Count > 0; guard++) lane = graph.Successors(lane)[0];
            why = "no start or target heading";
            float vx, vz;
            if (startWaypoint < 0 || !(graph.PlainHeading(lane, out vx, out vz) || Heading(graph, target, out vx, out vz))) return null;
            Vec3 e = graph.Position(entry), x = graph.Position(graph.PlainHeading(lane, out _, out _) ? lane : target);
            float lx = -uz, lz = ux;
            float nx = -vz, nz = vx;
            float Along(Vec3 p) => (p.X - e.X) * ux + (p.Z - e.Z) * uz;
            var junctionEnd = Along(graph.Position(startWaypoint));
            var at = startWaypoint;
            for (var guard = 0; guard < 40; guard++)
            {
                var next = -1;
                foreach (var n in graph.Successors(at))
                {
                    Vec3 a = graph.Position(at), b = graph.Position(n);
                    var length = Vec3.HorizontalDistance(a, b);
                    if (length > 0.1f && ((b.X - a.X) * ux + (b.Z - a.Z) * uz) / length > 0.95f) { next = n; break; }
                }
                if (next < 0 || !graph.IsIntersection(next)) break;
                junctionEnd = Math.Max(junctionEnd, Along(graph.Position(next)));
                at = next;
            }
            var denominator = 1f - (lx * nx + lz * nz);
            why = "the target's lane isn't turned back towards the entry";
            if (denominator < 0.2f) return null;
            float RadiusAt(float along) => ((e.X + ux * along - x.X) * nx + (e.Z + uz * along - x.Z) * nz) / denominator;
            var least = minRadius * RadiusSlack;
            var preferred = Along(graph.Position(startWaypoint));
            float start = preferred, r = RadiusAt(preferred), bestScore = float.MaxValue;
            for (var along = 6f; along <= junctionEnd + 3f; along += 0.5f)
            {
                var radius = RadiusAt(along);
                if (radius <= 0f || along + radius > junctionEnd + 3f) continue;
                var score = (radius >= least ? 0f : 1000f + (least - radius) * 100f) + Math.Abs(along - preferred);
                if (score < bestScore) { bestScore = score; start = along; r = radius; }
            }
            why = "the target's lane is on the wrong side";
            if (r <= 0f) return null;
            why = FormattableString.Invariant($"a circle of {r:0.0} m, the car follows {minRadius * RadiusSlack:0.0} m and more");
            if (r < minRadius * RadiusSlack || r > MaxRadius) return null;
            var s = new Vec3(e.X + ux * start, e.Y, e.Z + uz * start);
            var c = new Vec3(s.X + lx * r, 0f, s.Z + lz * r);
            var turn = Math.Atan2(ux, uz) - Math.Atan2(vx, vz);
            while (turn <= 0) turn += 2 * Math.PI;
            while (turn > 2 * Math.PI) turn -= 2 * Math.PI;
            why = FormattableString.Invariant($"the lanes are {turn * 180 / Math.PI:0} degrees apart");
            if (turn < 150 * Math.PI / 180 || turn > 210 * Math.PI / 180) return null;
            why = "";
            var points = new List<Vec3> { new Vec3(e.X - ux * LeadIn, e.Y, e.Z - uz * LeadIn) };
            AddStraight(points, points[0], e);
            AddStraight(points, e, s);
            double ox = s.X - c.X, oz = s.Z - c.Z;
            var steps = Math.Max(4, (int)Math.Ceiling(r * turn / Spacing));
            for (var k = 1; k <= steps; k++)
            {
                var phi = -turn * k / steps;
                double cos = Math.Cos(phi), sin = Math.Sin(phi);
                var f = (float)k / steps;
                points.Add(new Vec3((float)(c.X + ox * cos + oz * sin), e.Y + (x.Y - e.Y) * f, (float)(c.Z - ox * sin + oz * cos)));
            }
            var end = points[points.Count - 1];
            AddStraight(points, end, new Vec3(end.X + vx * RunOut, x.Y, end.Z + vz * RunOut));
            return new Polyline(points) { Radius = r };
        }

        private static bool Heading(LaneGraph graph, int i, out float dx, out float dz)
        {
            dx = dz = 0f;
            var next = graph.Successors(i);
            if (next.Count == 0) return false;
            Vec3 a = graph.Position(i), b = graph.Position(next[0]);
            var length = Vec3.HorizontalDistance(a, b);
            if (length < 0.1f) return false;
            dx = (b.X - a.X) / length;
            dz = (b.Z - a.Z) / length;
            return true;
        }

        private static void AddStraight(List<Vec3> points, Vec3 from, Vec3 to)
        {
            var length = Vec3.HorizontalDistance(from, to);
            var steps = (int)Math.Ceiling(length / Spacing);
            for (var k = 1; k <= steps; k++) points.Add(from + (to - from) * ((float)k / steps));
        }
    }
}
