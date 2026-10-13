#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using DrivenRides.Core.Drive;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;
using DrivenRides.Core.Testing;
using GleyTrafficSystem;
using UnityEngine;

namespace DrivenRides
{
    // Keeps the last ten seconds of the traffic round the ride car (ten frames a second) and, when a traffic run fails
    // (contact with a Gley car, a long standstill, a red run), turns them into a T0 scenario (Core's
    // TrafficScenario, docs/implementation-plan.md "Tests"): the lane graph round the car with Gley's flags, the
    // intersections it meets, the ride car on its path, and each traffic car from where it was with the targets it
    // went through. Copied into tests/traffic-scenarios/, it runs offline in every `dotnet test`.
    internal sealed class TrafficRecorder
    {
        private const float Every = 0.1f;
        private const float Keep = 10f;
        private const float Around = 170f;

        private sealed class Frame
        {
            public ScenarioFrame State = new ScenarioFrame();
            public float Along;
            // Waypoints with `stop` set among those watched.
            public HashSet<int> Stops = new HashSet<int>();
        }

        private readonly LinkedList<Frame> frames = new LinkedList<Frame>();
        private readonly Dictionary<int, (float Length, float Width, float Trigger)> sizes = new Dictionary<int, (float, float, float)>();
        private readonly HashSet<int> watched = new HashSet<int>();
        private float next, watchedAt = float.MinValue;

        public void Record(float time, Vector3 rear, float heading, float speed, float along, IReadOnlyList<OtherCar> cars, IReadOnlyList<VehicleComponent> components)
        {
            if (time < next) return;
            next = time + Every;
            var all = GleyGraph.Waypoints;
            var graph = GleyGraph.Graph;
            if (all == null || graph == null) return;
            // The light waypoints round the car, looked up once a second.
            if (time - watchedAt > 1f)
            {
                watchedAt = time;
                watched.Clear();
                foreach (var w in graph.Near(GleyGraph.ToVec(rear), Around)) if (all[w] != null && all[w].enter) watched.Add(w);
            }
            var f = new Frame { Along = along };
            f.State.T = time;
            f.State.RideX = rear.x;
            f.State.RideZ = rear.z;
            f.State.RideHeading = heading;
            f.State.RideSpeed = speed;
            foreach (var w in watched) if (all[w].stop) f.Stops.Add(w);
            for (var i = 0; i < cars.Count; i++)
            {
                var c = cars[i];
                sizes[c.Id] = (c.Length, c.Width, c.TriggerLength);
                string action;
                try { action = components[i].GetCurrentAction().ToString(); }
                catch (Exception) { action = ""; }
                f.State.Cars.Add(new ScenarioCarState { Id = c.Id, X = c.X, Z = c.Z, Heading = c.Heading, Vx = c.Vx, Vz = c.Vz, Target = c.Target, ForUs = c.StoppedForUs, Action = action });
            }
            frames.AddLast(f);
            while (frames.Count > 0 && frames.First.Value.State.T < time - Keep) frames.RemoveFirst();
        }

        // The scenario from the oldest frame kept to now. path/line/arcs/slow: the ride's, as NwhDrive drives it.
        public TrafficScenario? Build(string name, string note, NwhCar car, float topMs, List<int> path, DriveLine line,
            IReadOnlyList<(int Entry, Polyline Arc)> arcs, IReadOnlyList<(float From, float To, float Ms)> slow, GleyTraffic traffic)
        {
            var all = GleyGraph.Waypoints;
            var graph = GleyGraph.Graph;
            if (all == null || graph == null || frames.Count == 0) return null;
            var first = frames.First.Value;
            var last = frames.Last.Value;
            var t0 = first.State.T;

            // The ride's path from where it was at the oldest frame.
            var start = 0;
            for (var k = 0; k < path.Count; k++) if (line.AlongOf[k] <= first.Along - 2f) start = k;
            var shift = line.AlongOf[start];

            // The waypoints round where the car was and is, and the path on from there.
            var chosen = new SortedSet<int>();
            foreach (var w in graph.Near(new Vec3(first.State.RideX, 0, first.State.RideZ), Around)) chosen.Add(w);
            foreach (var w in graph.Near(new Vec3(last.State.RideX, 0, last.State.RideZ), Around)) chosen.Add(w);
            var end = start;
            for (var k = start; k < path.Count && line.AlongOf[k] - shift < 400f; k++) { chosen.Add(path[k]); end = k; }
            var local = new Dictionary<int, int>();
            foreach (var w in chosen) local[w] = local.Count;

            var s = new TrafficScenario { Name = name, Note = note, Seconds = last.State.T - t0 + 30f };
            var junctions = new SortedSet<int>();
            foreach (var w in chosen)
            {
                var g = all[w];
                var (junction, road) = traffic.JunctionOf(w);
                if (junction >= 0) junctions.Add(junction);
                s.Waypoints.Add(new ScenarioWaypoint
                {
                    X = g.position.x, Y = g.position.y, Z = g.position.z, Next = graph.Successors(w).Where(local.ContainsKey).Select(n => local[n]).ToArray(),
                    InJunction = graph.IsIntersection(w), Kmh = g.maxSpeed, Stop = first.Stops.Contains(w) || (!watched.Contains(w) && g.stop),
                    GiveWay = g.giveWay, Enter = g.enter, Exit = g.exit, Junction = junction, Road = road, Source = w,
                });
            }
            foreach (var j in junctions)
            {
                var x = traffic.Intersection(j);
                var spec = new ScenarioJunction { Id = j, Kind = x is PriorityIntersection ? "priority" : "replay" };
                // Each road's entries (a light's roads too, so the simulation can go on cycling it after the replay),
                // and a light's own green and amber times.
                if (x is PriorityIntersection p)
                    foreach (var r in p.enterWaypoints) spec.Roads.Add(r.roadWaypoints.Where(local.ContainsKey).Select(w => local[w]).ToArray());
                if (x is TrafficLightsIntersection l)
                {
                    foreach (var r in l.stopWaypoints) spec.Roads.Add(r.roadWaypoints.Where(local.ContainsKey).Select(w => local[w]).ToArray());
                    spec.Green = l.greenLightTime;
                    spec.Yellow = l.yellowLightTime;
                }
                s.Junctions.Add(spec);
            }
            // Light changes over the window, replayed.
            foreach (var w in watched)
            {
                if (!local.ContainsKey(w)) continue;
                var timeline = new ScenarioTimeline { Waypoint = local[w] };
                var was = first.Stops.Contains(w);
                foreach (var f in frames)
                {
                    var now = f.Stops.Contains(w);
                    if (now == was) continue;
                    timeline.At.Add(f.State.T - t0);
                    timeline.Stop.Add(now);
                    was = now;
                }
                if (timeline.At.Count > 0) s.Timelines.Add(timeline);
            }

            var ride = s.Ride;
            ride.Path = path.Skip(start).Take(end - start + 1).TakeWhile(local.ContainsKey).Select(w => local[w]).ToArray();
            foreach (var (entry, arc) in arcs)
                if (entry >= start && entry - start + 1 < ride.Path.Length)
                    ride.Arcs.Add(new ScenarioArc { Entry = entry - start, Radius = arc.Radius, Points = arc.Points.ToList() });
            ride.Wheelbase = car.Wheelbase;
            ride.Lock = car.MaxSteer;
            ride.FrontAhead = car.FrontAhead;
            ride.RearBehind = car.RearZ - (car.BodyCentre.z - car.BodyHalf.z);
            ride.HalfWidth = car.BodyHalf.x;
            ride.TopMs = topMs;
            ride.X = first.State.RideX;
            ride.Z = first.State.RideZ;
            ride.Heading = first.State.RideHeading;
            ride.Speed = first.State.RideSpeed;
            foreach (var (from, to, ms) in slow) if (to > shift) ride.Slow.Add((from - shift, to - shift, ms));

            // Each traffic car from where it was first seen, through the targets it went for.
            var seen = new Dictionary<int, ScenarioCar>();
            var fastest = new Dictionary<int, float>();
            foreach (var f in frames)
                foreach (var c in f.State.Cars)
                {
                    var speed = Mathf.Sqrt(c.Vx * c.Vx + c.Vz * c.Vz);
                    fastest[c.Id] = Mathf.Max(fastest.TryGetValue(c.Id, out var v) ? v : 0f, speed);
                    if (!seen.TryGetValue(c.Id, out var spec))
                    {
                        var size = sizes.TryGetValue(c.Id, out var z) ? z : (4.5f, 1.9f, 3f);
                        spec = new ScenarioCar
                        {
                            Id = c.Id, Spawn = f.State.T - t0, HasPose = true, X = c.X, Z = c.Z, Heading = c.Heading, Speed = speed,
                            Length = size.Item1, Width = size.Item2, Trigger = size.Item3,
                        };
                        seen[c.Id] = spec;
                    }
                    if (c.Target >= 0 && local.TryGetValue(c.Target, out var target) && (spec.Route.Length == 0 || spec.Route[spec.Route.Length - 1] != target))
                        spec.Route = spec.Route.Append(target).ToArray();
                }
            foreach (var spec in seen.Values)
            {
                if (spec.Route.Length == 0) continue;
                var limit = Mathf.Max(1f, s.Waypoints[spec.Route[0]].Kmh / 3.6f);
                spec.Pace = Mathf.Clamp(fastest[spec.Id] / limit, 0.3f, 1.2f);
                s.Cars.Add(spec);
            }
            foreach (var f in frames)
            {
                var copy = new ScenarioFrame { T = f.State.T - t0, RideX = f.State.RideX, RideZ = f.State.RideZ, RideHeading = f.State.RideHeading, RideSpeed = f.State.RideSpeed };
                foreach (var c in f.State.Cars)
                {
                    var state = c;
                    state.Target = c.Target >= 0 && local.TryGetValue(c.Target, out var t) ? t : -1;
                    copy.Cars.Add(state);
                }
                s.History.Add(copy);
            }
            return s;
        }
    }
}
