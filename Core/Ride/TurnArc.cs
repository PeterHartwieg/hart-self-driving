using System;
using System.Collections.Generic;
using DrivenRides.Core.Routing;

namespace DrivenRides.Core.Ride
{
    // A line through points, measured in metres along it (flat).
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
        // An arc turn's radius (TurnArc), 0 for any other line.
        public float Radius { get; set; }
        public float Length => at.Count > 0 ? at[at.Count - 1] : 0f;

        public Vec3 At(float along)
        {
            if (points.Count == 0) return default;
            if (along <= 0f) return points[0];
            for (var i = 1; i < points.Count; i++)
            {
                if (at[i] < along) continue;
                var span = at[i] - at[i - 1];
                var f = span > 0f ? (along - at[i - 1]) / span : 0f;
                return points[i - 1] + (points[i] - points[i - 1]) * f;
            }
            return points[points.Count - 1];
        }

        // How far along the line the point nearest (x, z) is, searching from `from` up to 10 m on (the car only moves
        // forward along it).
        public float Project(float x, float z, float from)
        {
            var best = from;
            var bestDistance = float.MaxValue;
            for (var i = 1; i < points.Count; i++)
            {
                if (at[i] < from - 1f) continue;
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

    // The line a driver takes through a junction: on along the entry lane, one circular arc as wide as the junction
    // allows, out in line with the exit lane at its mouth, then on down it (Peter, turn test 0.1.41: the car drove
    // straight in, turned at full lock, cut the corner and overshot the exit lane). Gley's car follows it by pursuit.
    public static class TurnArc
    {
        private const double MinTurnDegrees = 30, MaxTurnDegrees = 150;
        // The arc must be this much wider than the car's tightest circle, so pursuit has room to follow it.
        public const float RadiusSlack = 1.15f;
        private const float MaxRadius = 30f;
        // No arc is tighter than this, whatever the car: the taxi's and the limo's turning circles in Gley (3.2 m and
        // 2 m) let them pivot round a corner on their back wheels (Peter, turn test 0.1.47). The 6.7 m car's
        // tightest followable arc, 6.6 m, looked right.
        public const float MinDriveRadius = 6.5f;
        // The arc may end this far past the exit waypoint, and the line runs this far on down the exit lane.
        private const float ExitSlack = 2f;
        private const float RunOut = 10f;
        // The line starts this far back along the entry lane: Gley lets go of the entry waypoint once the car's centre
        // is 5 m from it and then steered at the exit across the junction, into the oncoming lanes (turn test
        // 0.1.42); the ride holds the car on its lane from here.
        public const float LeadIn = 12f;
        private const float Spacing = 0.5f;

        // How far outside the lanes that cross a turn's way the arc may start and end: half a lane to the kerb line,
        // and 1.5 m more, so a turn at a large junction is a little wider (Peter, turn test 0.1.44).
        private const float BoxMargin = 3.5f;

        // Null when the turn is too shallow or too sharp for one arc, or the junction has no room for one the car can
        // follow. The arc stays in the junction box: it starts no earlier than the first lane crossing the entry lane
        // and ends by the last lane the exit lane crosses (Peter, turn test 0.1.43: with an entry waypoint 20 m short
        // of a narrow cross street, the widest arc swept over the avenue's oncoming lanes before the junction).
        // radius: this arc instead of the widest one the junction has room for (it must still fit).
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
            // Along the entry lane from the entry, the first crossing lane; along the exit lane from the exit
            // waypoint, the last one before it.
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

        // Where the segment p-q crosses the line from `from` along (dx, dz), as metres along that line; only segments
        // at more than 45 degrees to it.
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

        // startFrom and endBy, the junction box: the arc should start no earlier than this far along the entry lane
        // from e and end no later than this far along the exit lane from x (negative: before it). Where that is too
        // tight for the car, the arc is the car's widest followable one and starts a little earlier, as a driver of a
        // long car does; it must still fit between the two waypoints.
        public static Polyline? Fit(Vec3 e, float ux, float uz, Vec3 x, float vx, float vz, float minRadius, float? radius = null,
            float startFrom = 0f, float endBy = ExitSlack)
        {
            var cross = ux * vz - uz * vx;
            var turn = Math.Atan2(Math.Abs(cross), ux * vx + uz * vz);
            if (turn * 180 / Math.PI < MinTurnDegrees || turn * 180 / Math.PI > MaxTurnDegrees) return null;
            // Where the entry and exit lanes cross: a metres on from the entry, b metres along the exit lane from the
            // exit waypoint (negative: before it).
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
            // The centre is r from T1, square to the entry lane on the side the turn goes.
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
                // Rotating (sx, sz) by phi the way a heading atan2(x, z) turns.
                points.Add(new Vec3((float)(c.X + sx * cos + sz * sin), e.Y + (x.Y - e.Y) * f, (float)(c.Z - sx * sin + sz * cos)));
            }
            var end = new Vec3(t2.X + vx * RunOut, x.Y, t2.Z + vz * RunOut);
            AddStraight(points, points[points.Count - 1], end);
            return new Polyline(points) { Radius = r };
        }

        private static void AddStraight(List<Vec3> points, Vec3 from, Vec3 to)
        {
            var length = Vec3.HorizontalDistance(from, to);
            var steps = (int)Math.Ceiling(length / Spacing);
            for (var k = 1; k <= steps; k++) points.Add(from + (to - from) * ((float)k / steps));
        }
    }
}
