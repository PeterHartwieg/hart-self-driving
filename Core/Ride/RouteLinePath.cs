using System;
using System.Collections.Generic;
using DrivenRides.Core.Routing;

namespace DrivenRides.Core.Ride
{
    // Display samples only: retain every steering vertex and subdivide long straight spans for road-height probes.
    // Distances stay in the driver's horizontal metres, even after the adapter projects samples onto the road.
    public sealed class RouteLinePath
    {
        private readonly List<Vec3> points = new List<Vec3>();
        private readonly List<float> distances = new List<float>();
        public int Count => points.Count;
        public float Length => Count == 0 ? 0f : distances[Count - 1];
        public Vec3 Point(int index) => points[index];
        public float Distance(int index) => distances[index];

        // Physics rides keep their rear-axle plan point. Native Gley can run wide of its waypoint line
        // after a U-turn: anchor its head at the car without changing which suffix progress consumes.
        // Keep the planned height as the reference for the adapter's road probe in either case.
        public static Vec3 Head(Polyline line, float along, Vec3? carPosition = null)
        {
            var at = line.At(Math.Max(0f, along));
            return carPosition is Vec3 car ? new Vec3(car.X, at.Y, car.Z) : at;
        }

        public void Reset(Polyline line, float spacing = 2f)
        {
            if (line == null) throw new ArgumentNullException(nameof(line));
            if (!(spacing > 0f) || float.IsInfinity(spacing)) throw new ArgumentOutOfRangeException(nameof(spacing));
            points.Clear();
            distances.Clear();
            var source = line.Points;
            var along = 0f;
            for (var i = 0; i < source.Count; i++)
            {
                var b = source[i];
                if (!Finite(b.X) || !Finite(b.Y) || !Finite(b.Z)) throw new ArgumentException("Route points must be finite.", nameof(line));
                if (i == 0) { points.Add(b); distances.Add(0f); continue; }
                var a = source[i - 1];
                var length = Vec3.HorizontalDistance(a, b);
                if (length < 0.0001f) continue;
                var steps = (int)Math.Ceiling(length / spacing);
                for (var step = 1; step <= steps; step++)
                {
                    var fraction = (float)step / steps;
                    points.Add(a + (b - a) * fraction);
                    distances.Add(along + length * fraction);
                }
                along += length;
            }
        }

        // Strictly after the consumed distance: the adapter supplies the head as vertex zero.
        // Binary search avoids rescanning the consumed portion and chooses by progress, never spatial proximity
        // (a future loop/crossing can occupy the same position).
        public int FirstAfter(float along)
        {
            if (float.IsNaN(along)) throw new ArgumentOutOfRangeException(nameof(along));
            int lo = 0, hi = Count;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (distances[mid] <= along) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
