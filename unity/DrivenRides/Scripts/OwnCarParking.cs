#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DrivenRides.Core.Drive;
using DrivenRides.Core.Parking;
using DrivenRides.Core.Routing;
using Helpers;
using UI;
using UnityEngine;

namespace DrivenRides
{
    internal static class OwnCarParking
    {
        public static IEnumerator Depart(NwhCar car, NwhPlanner planner, IReadOnlyList<DepartureEntry> entries,
            List<Goal> goals, int vehicleType, Func<bool> stop, ManoeuvreResult result, Action<Route, DrivePlan> install)
        {
            var graph = GleyGraph.Graph!;
            var start = NwhManoeuvre.Pose(car);
            var routes = new Dictionary<int, (Route? Route, DrivePlan? Plan)>();
            NwhManoeuvre.Hold(car);
            var world = new ManoeuvreWorld(car,planner.Car,start,departure:true);
            if (!world.Ready) { result.Reason="too many nearby obstacles"; yield break; }
            var surface = world.ReadSurface(start);
            while (surface.MoveNext()) { if (stop()) yield break; yield return surface.Current; }
            // Filter by the actual aisle connection before spending the pose-search budget. Euclidean proximity
            // otherwise fills the candidate limit with lanes behind the same curb or across an intersection.
            var reachable = entries.Select(e => (Entry:e, Pose:new CarPose(e.Position.X,e.Position.Z,e.Heading)))
                .Select(e => (e.Entry,e.Pose,Distance:world.Space.Estimate?.Invoke(e.Pose) ?? float.PositiveInfinity))
                .Where(e => !float.IsPositiveInfinity(e.Distance) && world.Space.Clear(planner.Car,e.Pose))
                .OrderBy(e => {
                    var before=new CarPose(e.Pose.X-Mathf.Sin(e.Pose.Heading)*4,e.Pose.Z-Mathf.Cos(e.Pose.Heading)*4,e.Pose.Heading);
                    var approach=world.Space.Estimate?.Invoke(before) ?? float.PositiveInfinity;
                    // Prefer a forward arrival from the driveway over backing along a lane to a closer point.
                    return e.Distance+Mathf.Max(0,approach-e.Distance)*4;
                }).Select(e => e.Entry).ToList();
            Log.Trace($"Parking departure: {reachable.Count} of {entries.Count} entries connected through the local surface.");
            if (reachable.Count == 0) world.Dump(start,false);
            var attempted = 0;
            var began = Time.realtimeSinceStartup;
            foreach (var entry in reachable)
            {
                if (stop()) yield break;
                if (attempted >= 32 || Time.realtimeSinceStartup - began > 90f) break;
                if (!routes.TryGetValue(entry.From, out var road))
                {
                    var planned = planner.PlanRoute(graph, entry.From, goals, vehicleType);
                    road = (planned.Route, planned.Plan); routes.Add(entry.From, road);
                    yield return null;
                }
                if (stop()) yield break;
                if (road.Route == null || road.Plan == null) continue;
                // The road may leave this segment in a different direction; never hand over to such a route.
                if (road.Plan.Line(graph).JoinMiss(entry.Position.X, entry.Position.Z, entry.Heading, out _) != null) continue;
                attempted++;
                Log.Trace($"Parking departure: entry {entry.From}->{entry.To}, {entry.Distance:0.0} m away at {entry.Position}.");
                var attempt = new ManoeuvreResult();
                var goal = new CarPose(entry.Position.X, entry.Position.Z, entry.Heading);
                var move = NwhManoeuvre.Run(car, planner.Car, goal, stop, attempt, departure: true, snapshot:world);
                while (move.MoveNext()) yield return move.Current;
                result.Reason = attempt.Reason;
                Log.Trace($"Parking departure: entry {entry.From}, {attempt.Reason}.");
                if (stop()) yield break;
                if (attempt.Success) { install(road.Route, road.Plan); result.Success = true; yield break; }
                // Geometry and origin cease to be valid after movement. Fail in place rather than trying an old plan.
                if (ManoeuvrePlan.Distance(start, NwhManoeuvre.Pose(car)) > 0.25f
                    || Mathf.Abs(ManoeuvrePlan.Angle(start.Heading - NwhManoeuvre.Pose(car).Heading)) > 0.05f) yield break;
            }
            if (result.Reason == "cancelled") result.Reason = "no reachable road entry from this parking area";
        }

        public static bool IsLegal(CarController car, bool parallelOnly = false)
        {
            foreach (var wheel in car.vehicleController.powertrain.wheels)
            {
                var t = wheel.wheelUAPI.transform;
                var hits = Physics.OverlapBox(t.position - t.up * 0.1f, new Vector3(0.1f, wheel.wheelUAPI.Radius * 2, 0.1f),
                    t.rotation, LayerHelper.parkingAreaLayerMask);
                var legal = false;
                foreach (var hit in hits)
                {
                    var lane = hit.GetComponentInParent<ParkingLaneGenerator>();
                    if (lane != null && !lane.isHandicapParking && (!parallelOnly || ParkingNavigation.OrdinaryParallel(lane))) legal = true;
                }
                if (!legal) return false;
            }
            return true;
        }
        public static IEnumerator Unpark(NwhCar car, CarSpec spec, DrivePlan road, Func<bool> stop, ManoeuvreResult result)
        {
            var graph = GleyGraph.Graph!;
            var line = road.Line(graph);
            var pose = NwhManoeuvre.Pose(car);
            var along = line.Line.Project(pose.X, pose.Z, 0);
            var limit = line.Line.Length - spec.FrontAhead - 2f;
            // The local manoeuvre ends before any junction. Intersections, gates and lane changes belong to the road driver.
            for (var i = 0; i < road.Path.Count; i++)
                if (graph.IsIntersection(road.Path[i]) && line.AlongOf[i] > along) { limit = Mathf.Min(limit, line.AlongOf[i] - spec.FrontAhead - 2); break; }
            for (var advance = 6f; advance <= 15f; advance += 3f)
            {
                if (stop()) yield break;
                if (along + advance > limit) break;
                var p = line.Line.At(along + advance); var next = line.Line.At(along + advance + 1);
                var heading = Mathf.Atan2(next.X - p.X, next.Z - p.Z);
                if (Mathf.Abs(ManoeuvrePlan.Angle(heading - pose.Heading)) > 0.25f) break;
                var goal = new CarPose(p.X, p.Z, heading);
                var attempt = new ManoeuvreResult();
                var move = NwhManoeuvre.Run(car, spec, goal, stop, attempt);
                while (move.MoveNext()) yield return move.Current;
                result.Reason = attempt.Reason;
                if (attempt.Success) { result.Success = true; yield break; }
                // Once a manoeuvre moved, returning control is safer than planning another departure from stale geometry.
                if (ManoeuvrePlan.Distance(pose, NwhManoeuvre.Pose(car)) > 0.25f) yield break;
            }
            if (result.Reason == "cancelled") result.Reason = "not enough clear straight road to pull out";
        }

        public static IEnumerator Park(CarController owner, NwhCar car, NwhPlanner planner, Vector3 position, Quaternion rotation,
            Func<bool> stop, ManoeuvreResult result)
        {
            var forward = rotation * Vector3.forward;
            var rear = position + rotation * new Vector3(0, 0, car.RearZ);
            var goal = new CarPose(rear.x, rear.z, Mathf.Atan2(forward.x, forward.z));
            var graph = GleyGraph.Graph;
            if (graph == null) { result.Reason = "traffic is not ready"; yield break; }
            var pose = NwhManoeuvre.Pose(car);
            var dx = pose.X - goal.X; var dz = pose.Z - goal.Z;
            var side = dx * Mathf.Cos(goal.Heading) - dz * Mathf.Sin(goal.Heading);
            var ahead = dx * forward.x + dz * forward.z;
            Log.Trace($"Parking pose: rear ({pose.X:0.00}, {pose.Z:0.00}), heading {pose.Heading:0.000}; goal ({goal.X:0.00}, {goal.Z:0.00}), heading {goal.Heading:0.000}; ahead {ahead:0.00}, side {side:0.00}.");
            // Already beside the space on the correct approach: the local planner can start here.
            var nearby = ParkingNavigation.BesideSpace(car, planner.Car, position, rotation);
            if (!nearby)
            {
                // Find a road waypoint beyond the space, facing the same way. The route must be directed: proximity
                // across a one-way street is not permission to cross it. The road driver handles the whole approach.
                var prefab = OwnCarTaxi.PlanPrefab(owner, out _);
                if (prefab == null) { result.Reason = "no vehicle type for the parking approach"; yield break; }
                var approaches = ParkingNavigation.SpaceGoals(car, planner.Car, position, rotation, (int)prefab.vehicleType);
                if (approaches.Count == 0) { result.Reason = "no adjacent driving approach to the parking strip"; yield break; }
                var start = OwnCarTaxi.StartUnder(graph, owner, -1, w => !graph.IsIntersection(w), out var offset);
                if (start < 0 || Mathf.Abs(offset) > 1.5f) { result.Reason = "the car is off its driving lane"; yield break; }
                var planned = planner.PlanRoute(graph, start, approaches, (int)prefab.vehicleType);
                var road = planned.Item2;
                if (road == null || OwnCarTaxi.StartRefusal(owner, planner, road, graph, offset) != null)
                { result.Reason = "no drivable route to the parking space"; yield break; }
                NwhDrive? drive = null;
                var ride = Ride.Active;
                try
                {
                    if (stop()) yield break;
                    drive = car.Root.AddComponent<NwhDrive>(); drive.SlowdownDumps = false;
                    drive.Begin(car, road, planner.Car, road.TopKmh > 0 ? road.TopKmh : 30f);
                    if (ride != null) ride.ParkingApproach = true;
                    var seconds = 0f;
                    while (!drive.Ended && !stop())
                    {
                        if (!InstanceBehavior<UIs>.Instance.gameSpeed.Paused) seconds += Time.unscaledDeltaTime;
                        if (seconds > 120f || OwnCarDrive.Trouble() != null) { drive.Stop("parking approach interrupted"); break; }
                        yield return null;
                    }
                    if (stop()) yield break;
                    if (!drive.Arrived) { result.Reason = "could not reach the parking approach"; yield break; }
                }
                finally
                {
                    if (ride != null) ride.ParkingApproach = false;
                    if (drive != null) { drive.Stop("parking approach ended"); UnityEngine.Object.Destroy(drive); }
                    NwhManoeuvre.Hold(car);
                }
                pose = NwhManoeuvre.Pose(car);
                if (ManoeuvrePlan.Distance(pose, goal) > 24f || Mathf.Abs(ManoeuvrePlan.Angle(pose.Heading - goal.Heading)) > 0.3f)
                { result.Reason = "parking approach ended too far from the space"; yield break; }
            }
            var manoeuvre = NwhManoeuvre.Run(car, planner.Car, goal, stop, result, parking: true);
            while (manoeuvre.MoveNext()) yield return manoeuvre.Current;
        }
    }
}
