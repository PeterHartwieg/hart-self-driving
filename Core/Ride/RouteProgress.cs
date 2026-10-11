using System;
using System.Collections.Generic;
using DrivenRides.Core.Routing;

namespace DrivenRides.Core.Ride
{
    public sealed class RouteProgress
    {
        private readonly Vec3[] points;
        private readonly double[] lengths;
        private readonly double maxAdvance;

        // maxAdvanceMetres bounds how far one update may move the car along the route. A car covers a metre or two
        // per frame, so a projection further ahead is a different part of the route passing nearby (a loop round a
        // block), not progress.
        public RouteProgress(IReadOnlyList<Vec3> points, double maxAdvanceMetres = 25)
        {
            maxAdvance = maxAdvanceMetres;
            if (points == null) throw new ArgumentNullException(nameof(points));
            if (points.Count == 0) throw new ArgumentException("A route needs at least one point.", nameof(points));
            this.points = new Vec3[points.Count];
            lengths = new double[points.Count];
            for (int i = 0; i < points.Count; i++)
            {
                Validate(points[i]);
                this.points[i] = points[i];
                if (i > 0) lengths[i] = lengths[i - 1] + Distance(points[i - 1], points[i]);
            }
            LengthMetres = lengths[lengths.Length - 1];
        }

        public double LengthMetres { get; }
        public double AlongMetres { get; private set; }
        public double LeftMetres => LengthMetres - AlongMetres;
        public int NextIndex { get; private set; }
        public int PointsLeft => points.Length - NextIndex;

        public void Update(Vec3 car)
        {
            Validate(car);
            if (NextIndex == points.Length) return;
            if (points.Length == 1)
            {
                if (Distance(car, points[0]) <= 5) NextIndex = 1;
                return;
            }
            int first = Math.Max(0, NextIndex - 1), last = Math.Min(points.Length - 2, NextIndex + 6);
            double bestDistance = double.PositiveInfinity, bestAlong = AlongMetres;
            int bestNext = NextIndex;
            double reach = AlongMetres + maxAdvance;
            for (int i = first; i <= last; i++)
            {
                if (lengths[i] > reach) break;
                Vec3 a = points[i], b = points[i + 1];
                double dx = (double)b.X - a.X, dy = (double)b.Y - a.Y, dz = (double)b.Z - a.Z;
                double squared = dx * dx + dy * dy + dz * dz;
                double raw = squared == 0 ? 1 : (((double)car.X - a.X) * dx + ((double)car.Y - a.Y) * dy + ((double)car.Z - a.Z) * dz) / squared;
                double t = Math.Max(0, Math.Min(1, raw));
                double segment = lengths[i + 1] - lengths[i];
                // A projection past the reach is clamped to it, not dropped: after a hitch the car may really be
                // further on, and clamping lets progress catch up by the limit each update.
                if (segment > 0 && lengths[i] + t * segment > reach)
                {
                    t = (reach - lengths[i]) / segment;
                    raw = Math.Min(raw, t);
                }
                double x = car.X - (a.X + t * dx), y = car.Y - (a.Y + t * dy), z = car.Z - (a.Z + t * dz);
                double distance = x * x + y * y + z * z;
                double along = lengths[i] + t * segment;
                // On a tie the nearer part of the route wins, so a crossing never pulls progress forward.
                if (distance > bestDistance || (distance == bestDistance && along >= bestAlong)) continue;
                bestDistance = distance;
                bestAlong = along;
                bestNext = i + (raw >= 1 ? 2 : raw >= 0 ? 1 : 0);
            }
            AlongMetres = Math.Max(AlongMetres, bestAlong);
            NextIndex = Math.Max(NextIndex, bestNext);
        }

        private static double Distance(Vec3 a, Vec3 b)
        {
            double x = (double)a.X - b.X, y = (double)a.Y - b.Y, z = (double)a.Z - b.Z;
            return Math.Sqrt(x * x + y * y + z * z);
        }
        private static void Validate(Vec3 p)
        {
            if (float.IsNaN(p.X) || float.IsInfinity(p.X) || float.IsNaN(p.Y) || float.IsInfinity(p.Y) || float.IsNaN(p.Z) || float.IsInfinity(p.Z))
                throw new ArgumentException("Positions must be finite.");
        }
    }
}
