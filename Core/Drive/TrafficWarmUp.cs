using System;
using System.Collections.Generic;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;

namespace DrivenRides.Core.Drive
{
    // The traffic decisions on a made-up road, a few seconds of them, once a game session before any ride: the first
    // calls of TrafficDriver's code are compiled then (the game's Mono compiles each method on its first call), not in
    // the first ride's physics steps. Every T3 run's first ride had one decision of 13 to 18 ms, later rides none over
    // 10 ms (review round 11).
    public static class TrafficWarmUp
    {
        // The decisions made; for the log and the test.
        public static int Run(CarModel? model = null)
        {
            // Three lanes, waypoints 4 m apart: east along z = 0, west along z = 4, north across them at x = 100.
            var points = new List<Vec3>();
            var next = new List<int[]>();
            void Lane(float x0, float z0, float dx, float dz, int count)
            {
                var first = points.Count;
                for (var i = 0; i < count; i++)
                {
                    points.Add(new Vec3(x0 + dx * i, 0f, z0 + dz * i));
                    next.Add(i + 1 < count ? new[] { first + i + 1 } : Array.Empty<int>());
                }
            }
            Lane(0f, 0f, 4f, 0f, 50);
            Lane(196f, 4f, -4f, 0f, 50);
            Lane(100f, -60f, 0f, 4f, 30);
            var n = points.Count;
            var mask = new int[n];
            var speeds = new float[n];
            for (var i = 0; i < n; i++) { mask[i] = -1; speeds[i] = 50f; }
            var graph = new LaneGraph(points.ToArray(), next.ToArray(), mask, new bool[n], speeds);

            var decisions = 0;
            // Straight on through the junction at x = 92 to 108, then standing; and left into the lane going north
            // there, joining it ahead of a car in it.
            var straight = new List<int>();
            for (var i = 0; i < 50; i++) straight.Add(i);
            var left = new List<int>();
            for (var i = 0; i < 25; i++) left.Add(i);
            for (var i = 116; i < 130; i++) left.Add(i);
            decisions += Drive(graph, points, straight, model, true);
            decisions += Drive(graph, points, left, model, false);
            return decisions;
        }

        private static int Drive(LaneGraph graph, List<Vec3> points, List<int> way, CarModel? model, bool stand)
        {
            var linePoints = new List<Vec3>();
            for (var i = 0; i < way.Count; i++) linePoints.Add(points[way[i]]);
            var line = DriveLine.Build(linePoints, Array.Empty<(int, Polyline)>());
            var driver = new PursuitDriver(line, 3f, 3.9f, 14f, 1.5f);
            var body = new RideBody { Wheelbase = 3f, FrontAhead = 3.9f, RearBehind = 1.1f, HalfWidth = 0.95f };
            var traffic = new TrafficDriver(graph, line, driver, body) { Model = model ?? new CarModel(3f, 35f, 0, 0, 0) { TrackWidth = 1.5f } };
            var flags = new NoFlags();
            var decision = new TrafficDecision();
            var cars = new List<OtherCar>();
            var marks = new List<PathMark>();
            var east = (float)(Math.PI / 2);
            var decisions = 0;
            // A car ahead, one coming the other way, one in the lane going north; standing, one waiting for the ride
            // car just ahead (the layer 3 rules: back-off, stand-off).
            float a = 70f, v = 10f;
            for (var step = 0; step < 300; step++)
            {
                var t = step * 0.02f;
                var standing = stand && step >= 120;
                if (standing) v = 0f;
                else a = Math.Min(a + v * 0.02f, line.Line.Length - 10f);
                var p = line.Line.At(a);
                var q = line.Line.At(a + 0.5f);
                var heading = (float)Math.Atan2(q.X - p.X, q.Z - p.Z);
                driver.StartAt(p.X, p.Z);
                // The junction's entry a stop line for the first 0.8 s, then a give-way.
                marks.Clear();
                for (var k = 0; k < way.Count; k++)
                {
                    var along = line.Along(k);
                    if (along < a - 12f || along > a + 84f) continue;
                    var m = new PathMark { Position = k, Waypoint = way[k], Along = along, Stop = k == 23 && step < 40, GiveWay = k == 23, Enter = k == 23, Exit = k == 27, Junction = k >= 23 && k <= 27 ? 1 : -1, Road = 0 };
                    if (traffic.Rules.ShouldAsk(m, a + body.FrontAhead)) m.Free = step > 80;
                    marks.Add(m);
                }
                cars.Clear();
                cars.Add(new OtherCar { Id = 1, X = 95f + t * 8f, Z = 0f, Heading = east, Vx = 8f, Length = 4.5f, Width = 1.9f, TriggerLength = 3f, Target = Math.Min(49, (int)((100f + t * 8f) / 4f)) });
                cars.Add(new OtherCar { Id = 2, X = 160f - t * 10f, Z = 4f, Heading = -east, Vx = -10f, Length = 4.5f, Width = 1.9f, TriggerLength = 3f, Target = Math.Min(99, 50 + (int)((200f - (156f - t * 10f)) / 4f)) });
                cars.Add(new OtherCar { Id = 3, X = 100f, Z = -30f + t * 9f, Heading = 0f, Vz = 9f, Length = 4.5f, Width = 1.9f, TriggerLength = 3f, Target = 100 + Math.Min(29, (int)((30f + t * 9f) / 4f) + 2) });
                if (standing) cars.Add(new OtherCar { Id = 4, X = p.X + 8f, Z = 1.5f, Heading = east + 2.5f, Length = 4.5f, Width = 1.9f, TriggerLength = 3f, Target = -1, StoppedForUs = true });
                traffic.Decide(new TrafficSnapshot
                {
                    Time = t, RearX = p.X, RearZ = p.Z, Heading = heading, Speed = v, Wheel = 0f, Marks = marks, Cars = cars, Flags = flags,
                    GapBehind = standing ? 20f : (float?)null,
                }, decision);
                decisions++;
            }
            return decisions;
        }

        private sealed class NoFlags : ILiveFlags
        {
            public bool Stop(int waypoint) => false;
            public bool GiveWayEntry(int waypoint, out int junction, out int road) { junction = -1; road = -1; return false; }
        }
    }
}
