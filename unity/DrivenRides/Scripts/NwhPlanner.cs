#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using DrivenRides.Core.Drive;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;
using GleyTrafficSystem;
using Helpers;
using UnityEngine;

namespace DrivenRides
{
    // The shape of a car driven by its own physics, read from its drivable prefab asset without spawning it (as
    // DrivableSteering reads the wheelbase): the axles, the lock and its speed curve, NWH's Ackermann (the front
    // track) and the body box, all from the rear axle. Core's feasibility check takes it (CarSpec).
    internal static class NwhShape
    {
        private static readonly Dictionary<string, CarSpec?> known = new Dictionary<string, CarSpec?>();

        // vehicleTypeName: the type whose Vehicles/PlayerVehicles/<id> prefab drives; null if it has none (the taxi).
        public static CarSpec? For(string vehicleTypeName, float topKmh)
        {
            var key = vehicleTypeName + "@" + topKmh.ToString(CultureInfo.InvariantCulture);
            if (known.TryGetValue(key, out var found)) return found;
            found = Load(vehicleTypeName, topKmh);
            known[key] = found;
            return found;
        }

        private static CarSpec? Load(string vehicleTypeName, float topKmh)
        {
            try
            {
                var asset = PrefabHelper.LoadPrefabAssetByName("Vehicles/PlayerVehicles/" + NwhCar.Id(vehicleTypeName));
                var vehicle = asset != null ? asset.GetComponentInChildren<NWH.VehiclePhysics2.VehicleController>(true) : null;
                if (asset == null || vehicle == null) { Log.Info($"NWH shape for {vehicleTypeName}: no drivable prefab."); return null; }
                var root = asset.transform;
                var wheels = new List<Vector3>();
                foreach (var wheel in asset.GetComponentsInChildren<NWH.Common.Vehicles.WheelUAPI>(true)) wheels.Add(root.InverseTransformPoint(wheel.transform.position));
                if (wheels.Count < 2) { Log.Info($"NWH shape for {vehicleTypeName}: {wheels.Count} wheels."); return null; }
                float rear = float.MaxValue, front = float.MinValue;
                foreach (var w in wheels) { rear = Mathf.Min(rear, w.z); front = Mathf.Max(front, w.z); }
                // The front track, as WheelGroup.FindBelongingWheels measures it: the two front wheels apart. NWH's
                // Ackermann only where the steering group adds it (WheelGroup.addAckerman, on by default).
                float left = float.MaxValue, right = float.MinValue;
                foreach (var w in wheels) if (front - w.z < 0.3f) { left = Mathf.Min(left, w.x); right = Mathf.Max(right, w.x); }
                var track = right > left ? right - left : 0f;
                var ackermann = true;
                var coefficient = 1f;
                foreach (var group in vehicle.powertrain.wheelGroups)
                    if (group != null && group.steerCoefficient != 0f) { ackermann = group.addAckerman; coefficient = group.steerCoefficient; break; }
                if (!ackermann) track = 0f;
                var maxSteer = vehicle.steering.maximumSteerAngle;
                var curve = vehicle.steering.speedSensitiveSteeringCurve;
                Func<float, float> lockAt = speedMs => maxSteer * (curve != null ? Mathf.Clamp01(curve.Evaluate(Mathf.Abs(speedMs) / 50f)) : 1f);
                MeasureBody(asset, rear, out var centre, out var half);
                var spec = new CarSpec(NwhCar.Id(vehicleTypeName), front - rear, lockAt, centre, half, topKmh, track, vehicle.steering.degreesPerSecondLimit);
                var inv = CultureInfo.InvariantCulture;
                // One line in CarSpec.Parse's form, for tools/rejudge and tools/calibrate.
                Log.Info($"NWH shape for {vehicleTypeName}: {spec}; steer rate {vehicle.steering.degreesPerSecondLimit:0}°/s, front group coefficient {coefficient:0.00}. As text: " +
                         string.Format(inv, "{0};{1:0.###};{2:0};{3:0.###},{4:0.###},{5:0.###};{6:0.###},{7:0.###},{8:0.###};0={9:0.##},10={10:0.##},25={11:0.##},50={12:0.##};{13:0.###};{14:0}",
                             spec.Name, spec.Wheelbase, topKmh, centre.X, centre.Y, centre.Z, half.X, half.Y, half.Z,
                             lockAt(0f), lockAt(10f / 3.6f), lockAt(25f / 3.6f), lockAt(50f / 3.6f), track, spec.WheelRate));
                return spec;
            }
            catch (Exception ex) { Log.Error($"NWH shape for {vehicleTypeName}: {ex.Message}"); return null; }
        }

        // The body box from the solid colliders, as NwhCar measures it on a spawned car, moved to the rear axle.
        private static void MeasureBody(GameObject asset, float rearZ, out Vec3 centre, out Vec3 half)
        {
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = -min;
            var found = false;
            var t = asset.transform;
            foreach (var c in asset.GetComponentsInChildren<Collider>(true))
            {
                if (c.isTrigger) continue;
                Bounds b;
                if (c is BoxCollider box) b = new Bounds(box.center, box.size);
                else if (c is MeshCollider mesh && mesh.sharedMesh != null) b = mesh.sharedMesh.bounds;
                else if (c is CapsuleCollider cap) b = new Bounds(cap.center, Vector3.one * cap.radius * 2f + (cap.direction == 2 ? Vector3.forward : cap.direction == 1 ? Vector3.up : Vector3.right) * (cap.height - cap.radius * 2f));
                else if (c is SphereCollider sphere) b = new Bounds(sphere.center, Vector3.one * sphere.radius * 2f);
                else continue;
                for (var i = 0; i < 8; i++)
                {
                    var corner = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var local = t.InverseTransformPoint(c.transform.TransformPoint(corner));
                    min = Vector3.Min(min, local);
                    max = Vector3.Max(max, local);
                    found = true;
                }
            }
            if (!found) { min = new Vector3(-0.9f, 0f, rearZ - 1f); max = new Vector3(0.9f, 1.5f, rearZ + 4f); }
            var c0 = (min + max) / 2f;
            var h = (max - min) / 2f;
            centre = new Vec3(c0.x, c0.y, c0.z - rearZ);
            half = new Vec3(h.x, h.y, h.z);
        }
    }

    // The planner's questions for a ride car driven by its own physics (docs/implementation-plan.md, "What the car can
    // drive" and "Parked cars in lanes"): which form each added turn takes and whether the car can make it (Core's
    // Feasibility, with the world swept here), and whether a lane is blocked by a parked car. One per car and session;
    // the Gley path (Ride.FindBlockedSteps, TurnDrive) stays for Gley ride cars.
    internal sealed class NwhPlanner
    {
        private static readonly Dictionary<string, NwhPlanner> planners = new Dictionary<string, NwhPlanner>();
        private static object? plannersFor;

        public readonly CarSpec Car;
        public readonly Feasibility Check;
        // The ride car itself (or the player's own car it stands for) is never in its own way.
        public Transform? Ignore { get; set; }
        private readonly HashSet<(int, int)> laneClear = new HashSet<(int, int)>();
        // Steps this car can't drive, for the session: what one car can't make another may (GleyGraph's set is for
        // every car).
        private readonly HashSet<(int, int)> blocked = new HashSet<(int, int)>();
        private readonly Collider[] hits = new Collider[8];
        private readonly Vector3 worldHalf, laneHalf, bodyHalf;

        // A turn's way is checked against the road's own colliders from this height above the lane up: a slope or a
        // crown stays under it, a divider built into the road does not (as Ride.BodyClear, turn test 0.1.41).
        private const float RoadClearance = 0.35f;
        // The road's own colliders and the Default layer's, from RoadClearance up: a divider built into the road, and
        // the median islands, whose colliders are on Default (T2: the limo stuck at full throttle against one
        // where its lane hugs a median's rounded nose, 'Collider' (Default), turn rig run 20261013-182118-0027).
        private static readonly int RaisedMask = LayerMask.GetMask("Roads", "Default");
        // Parked cars, the player's parked cars and what stands in a lane: a car driven by its own physics hits all of
        // them (Gley's cars pass through the parked ones' layer; the spike's turn-18207-14353 ran into one on Broadway).
        // They don't move within a session, so the manoeuvres' body sweep counts them too (T2: U-turns run wide into
        // the parking lane nosed into a parked van and an ambulance).
        private static int parkedMask = -1;
        private static int ParkedMask => parkedMask >= 0 ? parkedMask
            : parkedMask = (1 << LayerHelper.ParkedVehiclesLayerIndex) | (1 << LayerHelper.PlayerVehiclesLayerIndex) | TestCar.ObstacleMask;
        // The lanes are swept this far ahead when a ride is planned; the rest as the car drives (BlockedAhead), since
        // the game parks cars only near the player.
        public const float PlannedAheadMetres = 200f;

        private NwhPlanner(CarSpec car)
        {
            Car = car;
            Check = new Feasibility(GleyGraph.Graph!, car, WorldHit, Feasibility.DefaultMargin, LimitKmh) { GroundAt = GroundAt };
            var h = car.BodyHalf;
            worldHalf = new Vector3(h.X + Feasibility.DefaultMargin + Feasibility.NearMiss, h.Y, h.Z + Feasibility.DefaultMargin + Feasibility.NearMiss);
            // The driver stops for anything within its half width and 0.4 m of its line (NwhDrive.GapAhead).
            laneHalf = new Vector3(h.X + 0.4f, 0.6f, 0.5f);
            // The body itself, a near-miss larger, against what is built into the road: the car follows a lane to
            // within a few centimetres (T2's limo, 0.1 m on straights).
            bodyHalf = new Vector3(h.X + Feasibility.NearMiss, h.Y, 0.5f);
        }

        // The planner for a vehicle type driven by its own physics, kept for the lane graph's lifetime (a city load
        // starts afresh); null if the type has no drivable prefab or there is no graph.
        public static NwhPlanner? For(string vehicleTypeName, float topKmh)
        {
            var graph = GleyGraph.Graph;
            if (graph == null) return null;
            if (!ReferenceEquals(plannersFor, graph)) { planners.Clear(); plannersFor = graph; }
            var key = vehicleTypeName + "@" + topKmh.ToString(CultureInfo.InvariantCulture);
            if (planners.TryGetValue(key, out var planner)) return planner;
            var spec = NwhShape.For(vehicleTypeName, topKmh);
            if (spec == null) return null;
            planner = new NwhPlanner(spec);
            planners[key] = planner;
            return planner;
        }

        // For a ride car given as its traffic prefab (the Gley path's car): its drivable version, or for a car with
        // none (the taxi) the drivable car it is dressed on (NwhDress), whose physics it drives with.
        public static NwhPlanner? ForTrafficPrefab(GameObject trafficPrefab, float topKmh)
        {
            var type = DrivableSteering.TypeOf(trafficPrefab.name) ?? NwhDress.DonorFor(trafficPrefab, out _)?.TypeName;
            return type != null ? For(type, topKmh) : null;
        }

        public void Block(int from, int to) => blocked.Add((from, to));
        public bool IsBlocked(int from, int to) => blocked.Contains((from, to));

        private static float LimitKmh(int waypoint)
        {
            var all = GleyGraph.Waypoints;
            return all != null && waypoint >= 0 && waypoint < all.Length && all[waypoint] != null ? all[waypoint].maxSpeed : 0f;
        }

        // The body box at a simulated pose against street props and buildings, and against the road's own colliders
        // from RoadClearance up (the rig's contact and near-miss).
        private string? WorldHit(BodyBox box)
        {
            var rotation = Quaternion.Euler(0f, box.Heading * Mathf.Rad2Deg, 0f);
            var centre = new Vector3(box.Centre.X, box.Centre.Y, box.Centre.Z);
            if (Overlap(centre, worldHalf, rotation, ParkedMask) is string hit) return hit;
            return Raised(centre, worldHalf, rotation, centre.y - Car.BodyCentre.Y) is string raised ? "road edge " + raised : null;
        }

        // What a wheel stands on: the road, the pavement, a kerb; not cars, people, props or buildings (the body sweep
        // has those).
        private static int groundMask;
        private static int GroundMask => groundMask != 0 ? groundMask
            : groundMask = Physics.DefaultRaycastLayers & ~(ParkedMask | (1 << LayerHelper.VehiclesLayerIndex) | (NwhCar.GleyLayer >= 0 ? 1 << NwhCar.GleyLayer : 0)
                | LayerMask.GetMask("Human", "Ignore Raycast"));
        private readonly RaycastHit[] groundHits = new RaycastHit[8];

        // The ground's height under a point, from 2 m above it down to 2 m below.
        private float? GroundAt(Vec3 p)
        {
            var n = Physics.RaycastNonAlloc(new Vector3(p.X, p.Y + 2f, p.Z), Vector3.down, groundHits, 4f, GroundMask, QueryTriggerInteraction.Ignore);
            float? highest = null;
            for (var h = 0; h < n; h++)
            {
                if (Ignore != null && groundHits[h].transform.IsChildOf(Ignore)) continue;
                if (highest == null || groundHits[h].point.y > highest) highest = groundHits[h].point.y;
            }
            return highest;
        }

        // A box against the given layers, leaving out the ride car itself.
        private string? Overlap(Vector3 centre, Vector3 half, Quaternion rotation, int mask)
        {
            var n = Physics.OverlapBoxNonAlloc(centre, half, hits, rotation, mask, QueryTriggerInteraction.Ignore);
            for (var h = 0; h < n; h++)
                if (Ignore == null || !hits[h].transform.IsChildOf(Ignore)) return Name(hits[h]);
            return null;
        }

        // The part of a box from RoadClearance above the ground up, against the road's and the Default layer's
        // colliders.
        private string? Raised(Vector3 centre, Vector3 half, Quaternion rotation, float ground)
        {
            var bottom = Mathf.Max(centre.y - half.y, ground + RoadClearance);
            var top = centre.y + half.y;
            if (top <= bottom) return null;
            var raisedHalf = new Vector3(half.x, Mathf.Max(0.05f, (top - bottom) / 2f), half.z);
            return Overlap(new Vector3(centre.x, (top + bottom) / 2f, centre.z), raisedHalf, rotation, RaisedMask);
        }

        private static string Name(Collider c) => $"'{c.name}' ({LayerMask.LayerToName(c.gameObject.layer)})";

        // The steps of a route this car can't drive: each added turn and U-turn through the feasibility check, each
        // lane change swept as wide as the car, and the lanes of the first `laneMetres` swept for parked cars.
        public List<(int From, int To, string What)> BlockedSteps(LaneGraph graph, IReadOnlyList<int> w, float laneMetres = PlannedAheadMetres)
        {
            var result = new List<(int, int, string)>();
            var gone = 0f;
            for (var k = 0; k + 1 < w.Count; k++)
            {
                int from = w[k], to = w[k + 1];
                var step = Vec3.HorizontalDistance(graph.Position(from), graph.Position(to));
                if (Contains(graph.Successors(from), to))
                {
                    if (gone < laneMetres && LaneBlocked(graph, from, to) is string parked) result.Add((from, to, parked));
                }
                else if (graph.IsUTurn(from, to) || Contains(graph.MissingTurns(from), to))
                {
                    var verdict = Check.Step(from, to);
                    if (!verdict.Feasible) result.Add((from, to, verdict.ToString()));
                }
                else if (LaneChangeBlocked(graph, from, to) is string what) result.Add((from, to, what));
                gone += step;
            }
            return result;
        }

        // The lane steps of a path from `fromPosition` on for `metres`, swept for parked cars as the car comes near
        // them (the game parks cars only near the player); each step found clear is not swept again. For the ride's
        // driver to call as it goes (NwhDrive), with the result blocked (GleyGraph.BlockStep) and the route planned
        // again.
        public List<(int From, int To, string What)> BlockedAhead(LaneGraph graph, IReadOnlyList<int> path, int fromPosition, float metres)
        {
            var result = new List<(int, int, string)>();
            var gone = 0f;
            for (var k = Math.Max(0, fromPosition); k + 1 < path.Count && gone < metres; k++)
            {
                int from = path[k], to = path[k + 1];
                gone += Vec3.HorizontalDistance(graph.Position(from), graph.Position(to));
                if (!Contains(graph.Successors(from), to) || laneClear.Contains((from, to))) continue;
                if (LaneBlocked(graph, from, to) is string what) result.Add((from, to, what));
                else laneClear.Add((from, to));
            }
            return result;
        }

        // A plain lane step swept with the car's width for parked cars and anything standing in the lane, and with its
        // body for the road's raised edges (a median island's nose the lane hugs). Not in a junction: nothing parks
        // there, and the turn's own check covers it.
        private string? LaneBlocked(LaneGraph graph, int from, int to)
        {
            if (graph.IsIntersection(from) && graph.IsIntersection(to)) return null;
            Vec3 a = graph.Position(from), b = graph.Position(to);
            var length = Vec3.HorizontalDistance(a, b);
            if (length < 0.05f) return null;
            var rotation = Quaternion.LookRotation(new Vector3(b.X - a.X, 0f, b.Z - a.Z));
            var steps = Mathf.Max(1, Mathf.CeilToInt(length));
            for (var i = 0; i <= steps; i++)
            {
                var f = (float)i / steps;
                var at = new Vector3(a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f + 0.9f, a.Z + (b.Z - a.Z) * f);
                if (Overlap(at, laneHalf, rotation, ParkedMask) is string parked) return "parked in the lane: " + parked;
                var ground = a.Y + (b.Y - a.Y) * f;
                var body = new Vector3(at.x, ground + Car.BodyCentre.Y, at.z) + rotation * new Vector3(Car.BodyCentre.X, 0f, 0f);
                if (Raised(body, bodyHalf, rotation, ground) is string raised) return "road edge in the lane: " + raised;
            }
            return null;
        }

        // A lane change swept as wide as the car (a 2.3 m car changed lanes into a lamp post, turn test 0.1.33).
        private string? LaneChangeBlocked(LaneGraph graph, int from, int to)
        {
            Vec3 a = graph.Position(from), b = graph.Position(to);
            var radius = Car.BodyHalf.X + 0.1f;
            var n = Physics.OverlapCapsuleNonAlloc(new Vector3(a.X, a.Y + 1f, a.Z), new Vector3(b.X, b.Y + 1f, b.Z), radius, hits, ParkedMask, QueryTriggerInteraction.Ignore);
            for (var h = 0; h < n; h++)
                if (Ignore == null || !hits[h].transform.IsChildOf(Ignore)) return "lane change: " + Name(hits[h]);
            return null;
        }

        // The ride's expansion of a route for this car: added turns on an arc the car can drive cleanly, else through
        // vias it can; the slow stretches and the road limits (DrivePlan).
        public DrivePlan Plan(LaneGraph graph, IReadOnlyList<int> route) => DrivePlan.Build(graph, route, Car, Check.Arc, Check.Clear, LimitKmh);

        private static bool Contains(IReadOnlyList<int> list, int value)
        {
            for (var k = 0; k < list.Count; k++) if (list[k] == value) return true;
            return false;
        }
    }
}
