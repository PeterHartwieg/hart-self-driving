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
            var i = FirstAtOrPast(along);
            if (i >= points.Count) return points[points.Count - 1];
            var span = at[i] - at[i - 1];
            var f = span > 0f ? (along - at[i - 1]) / span : 0f;
            return points[i - 1] + (points[i] - points[i - 1]) * f;
        }

        // The first point after the first one at or past `along` metres, or the count if none: a binary search, since
        // the driver and its foresight ask this many times a step along lines of hundreds of points.
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

        // How far along the line the point nearest (x, z) is, searching from `from` up to 10 m on (the car only moves
        // forward along it).
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
        // The arc may end this far past the exit waypoint (on into the exit lane, settling there later rather than
        // hugging the corner; Peter, 14 October 2026), and the line runs RunOut on down the exit lane.
        private const float ExitSlack = 8f;
        // Without a junction box given, the arc that fits ends by this far past the exit waypoint.
        private const float BoxExitSlack = 2f;
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

        // The radii to try for a turn, largest first (Peter, 14 October 2026: "aim for larger curves which allow higher
        // speeds"; at 4 m/s² sideways the speed goes with the root of the radius). The largest is the one the two lanes
        // leave room for: from the entry waypoint (the stop line) to ExitSlack past the exit waypoint, an arc plus the
        // straights either side of it. Then smaller ones, each 0.8 of the one before, down to the junction box's arc
        // (Fit with no radius) and the car's tightest followable one. Which one the car takes is the feasibility
        // check's: the body against the world, the pavement, kerbs and the lanes, largest first.
        public static List<float> Radii(LaneGraph graph, int entry, int exit, float minRadius)
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
            return radii;
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
            float startFrom = 0f, float endBy = BoxExitSlack)
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

        // A U-turn as a driver takes it (Peter, T2: through Gley's vias the car made two tight 90-degree turns with a
        // straight between): on along the entry lane, one curve tangent to the entry lane and to the target's lane,
        // as wide as the two lanes are apart (a semicircle when they are level), then on down the target's lane.
        // It starts where the straight way into the junction ends (LaneGraph.UTurnStart: past the median's nose),
        // earlier if the curve would reach past the junction's far side. Null when the lanes are too close for the
        // car's circle (minRadius, with RadiusSlack): that car goes round the block.
        public static Polyline? FitUTurn(LaneGraph graph, int entry, int target, float minRadius) => FitUTurn(graph, entry, target, minRadius, out _);

        public static Polyline? FitUTurn(LaneGraph graph, int entry, int target, float minRadius, out string why)
        {
            why = "not a U-turn with a plain entry";
            if (!graph.IsUTurn(entry, target) || !graph.PlainHeading(entry, out var ux, out var uz)) return null;
            int startWaypoint = graph.UTurnStart(entry, target);
            // The lane the U-turn ends in: the target's lane where it leaves the junction (inside it, the target's
            // own heading may be skewed, and the curve would end bent against the lane).
            var lane = target;
            for (var guard = 0; guard < 20 && graph.IsIntersection(lane) && graph.Successors(lane).Count > 0; guard++) lane = graph.Successors(lane)[0];
            why = "no start or target heading";
            float vx, vz;
            if (startWaypoint < 0 || !(graph.PlainHeading(lane, out vx, out vz) || Heading(graph, target, out vx, out vz))) return null;
            Vec3 e = graph.Position(entry), x = graph.Position(graph.PlainHeading(lane, out _, out _) ? lane : target);
            float lx = -uz, lz = ux; // left of the entry heading
            float nx = -vz, nz = vx; // left of the target's heading
            float Along(Vec3 p) => (p.X - e.X) * ux + (p.Z - e.Z) * uz;
            // The far side of the junction along the entry's straight way.
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
            // The circle from S = e + u s, centre to the left, tangent to the target's lane on its left: its radius
            // grows or shrinks with s where the lanes aren't level. S is the straight way's end (past the median's
            // nose) unless another start gives the car a circle it can follow, or keeps the curve within the junction;
            // never before 6 m past the entry.
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
                // A followable circle first, then the start nearest the straight way's end.
                var score = (radius >= least ? 0f : 1000f + (least - radius) * 100f) + Math.Abs(along - preferred);
                if (score < bestScore) { bestScore = score; start = along; r = radius; }
            }
            why = "the target's lane is on the wrong side";
            if (r <= 0f) return null;
            why = FormattableString.Invariant($"a circle of {r:0.0} m, the car follows {minRadius * RadiusSlack:0.0} m and more");
            if (r < minRadius * RadiusSlack || r > MaxRadius) return null;
            var s = new Vec3(e.X + ux * start, e.Y, e.Z + uz * start);
            var c = new Vec3(s.X + lx * r, 0f, s.Z + lz * r);
            // Turning left: the heading (atan2(x, z)) falls from the entry's to the target's.
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

        // A waypoint's heading towards its successor, junction waypoints included.
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
