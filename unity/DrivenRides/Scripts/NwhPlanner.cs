#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using DrivenRides.Core.Drive;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;
using DrivenRides.Core.Testing;
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
    internal sealed class NwhPlanner : IWayOnWorld
    {
        private static readonly Dictionary<string, NwhPlanner> planners = new Dictionary<string, NwhPlanner>();
        private static object? plannersFor;

        public readonly CarSpec Car;
        public readonly Feasibility Check;
        // The ride car itself (or the player's own car it stands for) is never in its own way.
        public Transform? Ignore { get; set; }
        private readonly HashSet<(int, int)> laneClear = new HashSet<(int, int)>();
        // Steps this car can't drive, with why (BlockCause): what one car can't make another may (GleyGraph's set is
        // for every car).
        private readonly Dictionary<(int, int), (BlockCause Cause, float At)> blocked = new Dictionary<(int, int), (BlockCause, float)>();
        private readonly Collider[] hits = new Collider[32];
        private readonly Vector3 laneHalf, bodyHalf;

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
        // The lane sweep's: parked cars and the player's cars at the width the driver stops for, and what truly
        // stands in the lane (props, buildings, the road's raised edges, median islands) at the car's own width, a
        // near-miss wider, from RoadClearance up. Kerb props along a lane's edge ('Curb1', a 'Divider' beside the
        // inner lane) are low or outside that: T3 (session 20261013-185723) refused every ride because the sweep took
        // them for cars in the lane; the manoeuvres' body sweep still counts every prop.
        private static int CarsMask => (1 << LayerHelper.ParkedVehiclesLayerIndex) | (1 << LayerHelper.PlayerVehiclesLayerIndex);
        private static int StandingMask => TestCar.ObstacleMask | RaisedMask;
        private static int ParkedMask => parkedMask >= 0 ? parkedMask
            : parkedMask = (1 << LayerHelper.ParkedVehiclesLayerIndex) | (1 << LayerHelper.PlayerVehiclesLayerIndex) | TestCar.ObstacleMask;
        // The lanes are swept this far ahead when a ride is planned; the rest as the car drives (BlockedAhead), since
        // the game parks cars only near the player.
        public const float PlannedAheadMetres = 200f;

        private readonly LaneGraph graph;

        private NwhPlanner(CarSpec car)
        {
            Car = car;
            graph = GleyGraph.Graph!;
            // The pavement from the pedestrians' NavMesh (Sidewalk): no manoeuvre that puts a corner of the body on it.
            Check = new Feasibility(graph, car, WorldHit, Feasibility.DefaultMargin, LimitKmh)
                { GroundAt = GroundAt, PavementAt = Sidewalk.Available ? Sidewalk.At : (Func<Vec3, bool>?)null };
            var h = car.BodyHalf;
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

        // Blocks of the InLane kind (BlockCause) hold this long, then the step is swept again.
        public const float InLaneSeconds = 180f;

        public void Block(int from, int to, BlockCause cause = BlockCause.Manoeuvre) => blocked[(from, to)] = (cause, Time.realtimeSinceStartup);

        public bool IsBlocked(int from, int to)
        {
            if (!blocked.TryGetValue((from, to), out var b)) return false;
            if (b.Cause == BlockCause.InLane && Time.realtimeSinceStartup - b.At > InLaneSeconds)
            {
                // Checked afresh: a manoeuvre's verdict was cached with what stood in its way then.
                blocked.Remove((from, to));
                Check.Forget(from, to);
                return false;
            }
            return true;
        }

        // A new ride: what blocked the last one on its way doesn't block this one, and no lane is taken as clear from
        // the last ride's sweep (the parked cars there may have been cleared and parked again since).
        public void BeginRide()
        {
            var stale = new List<(int, int)>();
            foreach (var pair in blocked) if (pair.Value.Cause == BlockCause.OnTheWay) stale.Add(pair.Key);
            foreach (var key in stale) blocked.Remove(key);
            laneClear.Clear();
        }

        // The cause a step found blocked gets, from what failed it (WayOnPlanner.CauseOf).
        public static BlockCause CauseOf(LaneGraph graph, int from, int to, string what) => WayOnPlanner.CauseOf(graph, from, to, what);

        private static float LimitKmh(int waypoint)
        {
            var all = GleyGraph.Waypoints;
            return all != null && waypoint >= 0 && waypoint < all.Length && all[waypoint] != null ? all[waypoint].maxSpeed : 0f;
        }

        // The body box at a simulated pose against street props and buildings, and against the road's own colliders
        // from RoadClearance up (the rig's contact and near-miss).
        // A car (parked, or the player's) is named as movable (Feasibility.Movable): it may be gone later (CauseOf).
        private string? WorldHit(BodyBox box)
        {
            var rotation = Quaternion.Euler(0f, box.Heading * Mathf.Rad2Deg, 0f);
            var centre = new Vector3(box.Centre.X, box.Centre.Y, box.Centre.Z);
            // The box as the check sized it: the margin while manoeuvring, the body itself when settled in its lane.
            var half = new Vector3(box.Half.X, box.Half.Y, box.Half.Z);
            if (Overlap(centre, half, rotation, CarsMask) is string car) return Feasibility.Movable + car;
            if (Overlap(centre, half, rotation, ParkedMask & ~CarsMask) is string hit) return hit;
            return Raised(centre, half, rotation, centre.y - Car.BodyCentre.Y) is string raised ? "road edge " + raised : null;
        }

        // What a wheel stands on: the road, the pavement, a kerb; not cars, people, props or buildings (the body sweep
        // has those).
        private static int groundMask;
        private static int GroundMask => groundMask != 0 ? groundMask
            : groundMask = Physics.DefaultRaycastLayers & ~(ParkedMask | (1 << LayerHelper.VehiclesLayerIndex) | (NwhCar.GleyLayer >= 0 ? 1 << NwhCar.GleyLayer : 0)
                | LayerMask.GetMask("Human", "Ignore Raycast"));
        private static readonly RaycastHit[] groundHits = new RaycastHit[8];

        // The ground's height under a point, from 2 m above it down to 2 m below.
        private float? GroundAt(Vec3 p) => GroundBelow(p, Ignore);

        // The same for anyone (the rig's TestCar), leaving out a car's own colliders.
        internal static float? GroundBelow(Vec3 p, Transform? ignore)
        {
            var n = Physics.RaycastNonAlloc(new Vector3(p.X, p.Y + 2f, p.Z), Vector3.down, groundHits, 4f, GroundMask, QueryTriggerInteraction.Ignore);
            float? highest = null;
            for (var h = 0; h < n; h++)
            {
                if (ignore != null && groundHits[h].transform.IsChildOf(ignore)) continue;
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
        private string? Raised(Vector3 centre, Vector3 half, Quaternion rotation, float ground, int mask = 0)
        {
            var bottom = Mathf.Max(centre.y - half.y, ground + RoadClearance);
            var top = centre.y + half.y;
            if (top <= bottom) return null;
            var raisedHalf = new Vector3(half.x, Mathf.Max(0.05f, (top - bottom) / 2f), half.z);
            return Overlap(new Vector3(centre.x, (top + bottom) / 2f, centre.z), raisedHalf, rotation, mask != 0 ? mask : RaisedMask);
        }

        private static string Name(Collider c) => $"'{c.name}' ({LayerMask.LayerToName(c.gameObject.layer)})";

        // The steps of a route this car can't drive: each added turn and U-turn through the feasibility check, each
        // lane change swept as wide as the car, and the lanes of the first `laneMetres` swept for parked cars.
        public List<(int From, int To, string What)> BlockedSteps(LaneGraph graph, IReadOnlyList<int> w, float laneMetres = PlannedAheadMetres)
        {
            var result = new List<(int From, int To, string What)>();
            foreach (var _ in BlockedStepsWork(graph, w, laneMetres, result)) { }
            return result;
        }

        private IEnumerable<object?> BlockedStepsWork(LaneGraph graph, IReadOnlyList<int> w, float laneMetres, List<(int From, int To, string What)> result)
        {
            var gone = 0f;
            for (var k = 0; k + 1 < w.Count; k++)
            {
                int from = w[k], to = w[k + 1];
                var step = Vec3.HorizontalDistance(graph.Position(from), graph.Position(to));
                if (Contains(graph.Successors(from), to))
                {
                    if (gone < laneMetres && LaneBlocked(graph, from, to, out _) is string parked) result.Add((from, to, parked));
                }
                else if (graph.IsUTurn(from, to) || Contains(graph.MissingTurns(from), to))
                {
                    foreach (var y in Check.StepWork(from, to)) yield return y;
                    var verdict = Check.Step(from, to);
                    if (!verdict.Feasible) result.Add((from, to, verdict.ToString()));
                }
                else if (LaneChangeBlocked(graph, from, to) is string what) result.Add((from, to, what));
                gone += step;
                yield return null;
            }
        }

        // The lane steps of a path from `fromPosition` on for `metres`, swept for parked cars as the car comes near
        // them (the game parks cars only near the player); each step found clear is not swept again. For the ride's
        // driver to call as it goes (NwhDrive), with the result blocked (GleyGraph.BlockStep) and the route planned
        // again.
        // near: where parked cars are loaded (the ride car; the game parks cars only near the player, and the camera
        // follows the ride car): a step farther than LoadedMetres from it is swept but never remembered as clear
        // (review round 1: a whole route swept before departure was cached clear before its cars loaded).
        // A step remembered as clear is forgotten once it is farther than LoadedMetres from `near` (review round 2: the
        // game clears parked cars beyond 80 m of the player and parks others when it comes back, so a step found clear
        // before is swept again when the car comes near it again).
        public List<(int From, int To, string What)> BlockedAhead(LaneGraph graph, IReadOnlyList<int> path, int fromPosition, float metres, Vec3? near = null)
        {
            var swept = new List<(int From, int To, string? What, float At)>();
            foreach (var _ in BlockedAheadWork(graph, path, fromPosition, metres, near, swept)) { }
            var result = new List<(int From, int To, string What)>();
            foreach (var (from, to, what, _) in swept) if (what != null) result.Add((from, to, what));
            return result;
        }

        // Each lane step swept goes in `result` as soon as it is: what blocks it (null: passable, perhaps on a sideways
        // move) and the metres along it where that begins (the look-ahead holds short of it; review round 4).
        private IEnumerable<object?> BlockedAheadWork(LaneGraph graph, IReadOnlyList<int> path, int fromPosition, float metres, Vec3? near,
            List<(int From, int To, string? What, float At)> result)
        {
            if (near is Vec3 here) laneClear.RemoveWhere(step => Vec3.HorizontalDistance(here, graph.Position(step.Item2)) > LoadedMetres);
            var gone = 0f;
            for (var k = Math.Max(0, fromPosition); k + 1 < path.Count && gone < metres; k++)
            {
                int from = path[k], to = path[k + 1];
                gone += Vec3.HorizontalDistance(graph.Position(from), graph.Position(to));
                if (!Contains(graph.Successors(from), to) || laneClear.Contains((from, to))) continue;
                var what = LaneBlocked(graph, from, to, out var at);
                result.Add((from, to, what, at));
                if (what == null && near is Vec3 n && Vec3.HorizontalDistance(n, graph.Position(to)) <= LoadedMetres) laneClear.Add((from, to));
                yield return null;
            }
        }

        // Within this of the ride car, the game's parked cars are there (they are cleared beyond 80 m of the player,
        // ParkingLaneGenerator.CleanupParkedVehicles).
        public const float LoadedMetres = WayOnPlanner.LoadedMetres;

        // A plain lane step swept for what stands in it: parked cars and the player's cars, and from RoadClearance up
        // props, buildings, the road's raised edges and median islands. A driver passes a car parked partly in the
        // lane by moving over within it: where the obstacles leave room on one side (the car's half width, a margin
        // the driver's gap check doesn't stop for, and no more than a little past the lane's edge), the step is
        // passed on a line moved sideways (ShiftFor, DriveLine.Shifted); otherwise it is blocked, and the route
        // changes lanes round it or goes another way (T3, session 20261013-200946: twelve rides refused for a car
        // parked partly in their lane). Not in a junction: nothing parks there, and the turn's own check covers it.
        // at: metres along the step where what blocks it begins (0 if nothing does).
        private string? LaneBlocked(LaneGraph graph, int from, int to, out float at)
        {
            at = 0f;
            if (graph.IsIntersection(from) && graph.IsIntersection(to)) return null;
            Vec3 a = graph.Position(from), b = graph.Position(to);
            var length = Vec3.HorizontalDistance(a, b);
            if (length < 0.05f) return null;
            var forward = new Vector3(b.X - a.X, 0f, b.Z - a.Z).normalized;
            var right = new Vector3(forward.z, 0f, -forward.x);
            var rotation = Quaternion.LookRotation(forward);
            var half = Car.BodyHalf.X;
            // Room needed beside each kind: the driver stops for a car within half width + 0.4 m of its line.
            const float carClear = 0.5f, propClear = Feasibility.NearMiss + 0.1f;
            var maxShift = Pavement.LaneHalf + 0.2f - half;
            float lo = -maxShift, hi = maxShift;
            float? first = null, last = null;
            string? what = null;
            var steps = Mathf.Max(1, Mathf.CeilToInt(length));
            for (var i = 0; i <= steps; i++)
            {
                var f = (float)i / steps;
                var ground = a.Y + (b.Y - a.Y) * f;
                var centre = new Vector3(a.X + (b.X - a.X) * f, ground, a.Z + (b.Z - a.Z) * f);
                foreach (var (hit, clear, lowest) in new[] { (CarsMask, carClear, 0.3f), (StandingMask, propClear, RoadClearance) })
                {
                    var box = new Vector3(Pavement.LaneHalf + 0.5f, (1.5f - lowest) / 2f, 0.5f);
                    var n = Physics.OverlapBoxNonAlloc(centre + Vector3.up * ((1.5f + lowest) / 2f), box, hits, rotation, hit, QueryTriggerInteraction.Ignore);
                    for (var h = 0; h < n; h++)
                    {
                        var c = hits[h];
                        if (Ignore != null && c.transform.IsChildOf(Ignore)) continue;
                        // Its edge nearest the line at this point, sideways: a ray each way from the line at a height the
                        // obstacle has (a guardrail is 0.8 m tall). Not its bounds: a long curved guardrail's box
                        // (34 x 33 m in the Hamptons) holds the whole lane, and every step along it was blocked.
                        var height = Mathf.Clamp(c.bounds.max.y - ground - 0.15f, lowest + 0.05f, 0.9f);
                        var probe = centre + Vector3.up * height;
                        float? side = null;
                        if (c.Raycast(new Ray(probe, right), out var toRight, 3f)) side = toRight.distance;
                        if (c.Raycast(new Ray(probe, -right), out var toLeft, 3f) && (side == null || toLeft.distance < side.Value)) side = -toLeft.distance;
                        // Neither way: the line runs through it (a convex one holds the point), or it is only ahead or
                        // behind within this metre's box.
                        if (side == null)
                        {
                            if (c is MeshCollider mesh && !mesh.convex)
                            {
                                // A ray from inside a mesh doesn't meet it: rays in from 3 m either side do if the line
                                // runs through it (review round 1: such a collider was skipped).
                                var inFromRight = c.Raycast(new Ray(probe + right * 3f, -right), out _, 3f);
                                var inFromLeft = c.Raycast(new Ray(probe - right * 3f, right), out _, 3f);
                                if (!inFromRight && !inFromLeft) continue;
                            }
                            else if ((c.ClosestPoint(probe) - probe).sqrMagnitude > 0.0025f) continue;
                            side = 0f;
                        }
                        var need = half + clear;
                        if (Mathf.Abs(side.Value) >= need) continue;
                        first ??= f * length;
                        last = f * length;
                        what ??= (hit == CarsMask ? "parked in the lane: " : "in the lane: ") + Name(c) + $" (bounds {c.bounds.min} to {c.bounds.max})";
                        // Standing on the line itself: no side to pass on.
                        if (Mathf.Abs(side.Value) < 0.05f) { lo = 1f; hi = 0f; continue; }
                        if (side.Value > 0f) hi = Mathf.Min(hi, side.Value - need);
                        else lo = Mathf.Max(lo, side.Value + need);
                    }
                }
            }
            if (first == null) { shifts.Remove((from, to)); return null; }
            if (lo > hi) { shifts.Remove((from, to)); at = first.Value; return what + ", no room to pass within the lane"; }
            var offset = Mathf.Clamp(0f, lo, hi);
            // A move of a few centimetres is within the margins: the line stays (T3 listed shifts of 0.06 m).
            if (Mathf.Abs(offset) < 0.1f) { shifts.Remove((from, to)); return null; }
            // The rear axle moves over before the front reaches the obstacle and back once the rear has passed it.
            var rear = Car.BodyHalf.Z - Car.BodyCentre.Z;
            var move = (first.Value - Car.FrontAhead - 1f, last!.Value + rear + 1f, offset);
            shifts[(from, to)] = move;
            Log.Info($"NWH planner: step {from} -> {to} passed {offset:+0.00;-0.00} m sideways ({what}).");
            return null;
        }

        // Lane steps passed on a line moved sideways (LaneBlocked): metres along the step where the move holds, and
        // the offset (right positive).
        private readonly Dictionary<(int, int), (float From, float To, float Offset)> shifts = new Dictionary<(int, int), (float, float, float)>();
        public (float From, float To, float Offset)? ShiftFor(int from, int to) => shifts.TryGetValue((from, to), out var s) ? s : ((float, float, float)?)null;

        // A lane change swept as wide as the car (a 2.3 m car changed lanes into a lamp post, turn test 0.1.33).
        private string? LaneChangeBlocked(LaneGraph graph, int from, int to)
        {
            Vec3 a = graph.Position(from), b = graph.Position(to);
            var radius = Car.BodyHalf.X + 0.1f;
            // From RoadClearance up, so the kerb along the lanes is no lane change's obstacle.
            var lift = RoadClearance + radius;
            var n = Physics.OverlapCapsuleNonAlloc(new Vector3(a.X, a.Y + lift, a.Z), new Vector3(b.X, b.Y + lift, b.Z), radius, hits, ParkedMask, QueryTriggerInteraction.Ignore);
            for (var h = 0; h < n; h++)
                if (Ignore == null || !hits[h].transform.IsChildOf(Ignore)) return "lane change: " + Name(hits[h]);
            return null;
        }

        // A route planned for this car, as an NWH ride is planned (Ride.TryCreateRide; the rig's ride cases): wider
        // U-turns allowed, up to eight rounds of planning, each finding the steps this car can't drive
        // (BlockedSteps) and the steps its line fails on as driven (Feasibility.Validate), and blocking them. The
        // route and its checked plan, or nulls, and a short summary of the added steps driven and the ones blocked.
        public (Route? Route, DrivePlan? Plan, string Summary) PlanRoute(LaneGraph graph, int start, IReadOnlyList<Goal> goals, int vehicleType, int rounds = 8)
        {
            var into = new Planned();
            foreach (var _ in PlanRouteWork(graph, start, goals, vehicleType, rounds, null, into)) { }
            return (into.Route, into.Plan, into.Summary);
        }

        private sealed class Planned
        {
            public Route? Route;
            public DrivePlan? Plan;
            public string Summary = "";
        }

        // The route search is stepped this many expansions at a time inside a job, up to MaxExpansions.
        private const int SearchStep = 2000;
        private const int MaxExpansions = 200000;

        private IEnumerable<object?> PlanRouteWork(LaneGraph graph, int start, IReadOnlyList<Goal> goals, int vehicleType, int rounds, WayOnJob? job, Planned into)
        {
            // The pedestrians' NavMesh may have come since the planner was made (Sidewalk).
            Check.PavementAt = Sidewalk.Available ? Sidewalk.At : (Func<Vec3, bool>?)null;
            var blockedNotes = new List<string>();
            var noCase = 0;
            Route? route = null;
            DrivePlan? plan = null;
            for (var attempt = 0; attempt < rounds; attempt++)
            {
                var search = RoutePlanner.Begin(graph, new RouteRequest
                {
                    Start = start, Goals = goals, VehicleType = vehicleType, IsDisabled = GleyGraph.IsDisabled,
                    IsBlockedStep = (a, b) => GleyGraph.IsBlockedStep(a, b) || IsBlocked(a, b), WideUTurns = true,
                });
                Func<bool>? stop = job != null ? () => job.OverBudget : (Func<bool>?)null;
                while (search.Status == PlanStatus.Running && search.Expanded < MaxExpansions)
                {
                    search.Step(job != null ? SearchStep : MaxExpansions, stop);
                    if (search.Status == PlanStatus.Running) yield return null;
                }
                route = search.Status == PlanStatus.Found ? search.Result : null;
                if (route == null) break;
                var found = new List<(int From, int To, string What)>();
                foreach (var y in BlockedStepsWork(graph, route.Waypoints, PlannedAheadMetres, found)) yield return y;
                if (found.Count == 0)
                {
                    // The line the car will drive, rounded corners and sideways moves included, checked as driven: a
                    // step it fails on is blocked like the others (review round 2: never an unchecked plan).
                    // Its turns' checks worked out first as yielding work, so Build finds them known (review round 3).
                    foreach (var y in Check.PrepareWork(route.Waypoints)) yield return y;
                    var built = DrivePlan.Build(graph, route.Waypoints, Car, Check.Arc, Check.Clear, LimitKmh, ShiftFor);
                    yield return null;
                    var notes = new List<string>();
                    foreach (var y in Check.Validate(built, found, notes)) yield return y;
                    foreach (var note in notes) Log.Info("NWH planner: " + note + ".");
                    if (found.Count == 0) { plan = built; break; }
                }
                foreach (var (from, to, what) in found)
                {
                    if (what.Contains(Feasibility.NoCase)) noCase++;
                    shifts.Remove((from, to));
                    Block(from, to, CauseOf(graph, from, to, what));
                    blockedNotes.Add($"{StepKind(graph, from, to)} {from}->{to} ({(what.Length > 60 ? what.Substring(0, 60) + "..." : what)})");
                }
                route = null;
            }
            var driven = new List<string>();
            var changes = 0;
            if (route != null)
            {
                var w = route.Waypoints;
                for (var k = 0; k + 1 < w.Count; k++)
                {
                    if (Contains(graph.Successors(w[k]), w[k + 1])) continue;
                    var kind = StepKind(graph, w[k], w[k + 1]);
                    if (kind == "lane change") { changes++; continue; }
                    var verdict = Check.Step(w[k], w[k + 1]);
                    driven.Add(kind == "wide U-turn" ? $"{kind} {graph.UTurnWidth(w[k], w[k + 1]):0} m ({verdict.Form})" : $"{kind} ({verdict.Form})");
                }
            }
            // Steps blocked for want of a case to check them on (Feasibility.NoCase) are counted, so a gap in the
            // catalogue shows in the log (review round 2).
            if (noCase > 0) Log.Info($"NWH planner: {noCase} steps blocked with no case to check them on.");
            into.Route = route;
            into.Plan = route != null ? plan : null;
            into.Summary = (route != null ? $"{(driven.Count > 0 ? string.Join(", ", driven) : "no added turns")}, {changes} lane changes" : "no route")
                + (blockedNotes.Count > 0 ? $"; blocked {blockedNotes.Count}: {string.Join("; ", blockedNotes.GetRange(0, Math.Min(4, blockedNotes.Count)))}{(blockedNotes.Count > 4 ? "; ..." : "")}" : "")
                + (noCase > 0 ? $"; {noCase} with no case to check" : "");
        }

        private static string StepKind(LaneGraph graph, int from, int to) => WayOnPlanner.StepKind(graph, from, to);

        public static int RouteStepAt(IReadOnlyList<int> route, IReadOnlyList<int> path, int position) => WayOnPlanner.RouteStepAt(route, path, position);

        // The ride's expansion of a route for this car: added turns on an arc the car can drive cleanly, else through
        // vias it can; the slow stretches and the road limits (DrivePlan). The line is checked as it will be driven
        // (Feasibility.Validate; review rounds 1 and 2): null, with the steps it fails on in `failed`, when any part
        // of it fails, so no caller drives an unchecked fallback.
        public DrivePlan? Plan(LaneGraph graph, IReadOnlyList<int> route, out List<(int From, int To, string What)> failed)
        {
            var plan = DrivePlan.Build(graph, route, Car, Check.Arc, Check.Clear, LimitKmh, ShiftFor);
            var notes = new List<string>();
            failed = Check.ValidateNow(plan, notes);
            foreach (var note in notes) Log.Info("NWH planner: " + note + ".");
            foreach (var (from, to, what) in failed)
            {
                shifts.Remove((from, to));
                Log.Info($"NWH planner: step {from} -> {to} fails as driven ({what}).");
            }
            return failed.Count == 0 ? plan : null;
        }

        // The way on round a step the car found blocked on its way, and the look-ahead while it drives: Core's
        // WayOnPlanner, with this planner as its world.
        public WayOnPlanner WayOn => wayOn ??= new WayOnPlanner(this);
        private WayOnPlanner? wayOn;

        public WayOnJob PlanRoundBlock(IReadOnlyList<int> route, IReadOnlyList<int> path, int position, string what,
            Func<CarPose> pose, IReadOnlyList<Goal> goals, int vehicleType) => WayOn.RoundBlock(route, path, position, what, pose, goals, vehicleType);

        public WayOnJob LookAhead(IReadOnlyList<int> route, IReadOnlyList<int> path, int position, Func<CarPose> pose,
            IReadOnlyList<Goal> goals, int vehicleType, WayOnPlanner.Watch watch) => WayOn.LookAhead(route, path, position, pose, goals, vehicleType, watch);

        // IWayOnWorld: this planner's graph, check, route search, lane sweep, moves and blocks.
        LaneGraph IWayOnWorld.Graph => graph;
        Feasibility IWayOnWorld.Check => Check;

        IEnumerable<object?> IWayOnWorld.PlanRoute(int start, IReadOnlyList<Goal> goals, int vehicleType, WayOnJob? job, PlannedRoute into)
        {
            var planned = new Planned();
            foreach (var y in PlanRouteWork(graph, start, goals, vehicleType, 8, job, planned)) yield return y;
            into.Route = planned.Route;
            into.Plan = planned.Plan;
            into.Summary = planned.Summary;
        }

        IEnumerable<object?> IWayOnWorld.LanesAhead(IReadOnlyList<int> path, int position, float metres, Vec3 near, List<(int From, int To, string? What, float At)> swept) =>
            BlockedAheadWork(graph, path, position, metres, near, swept);

        bool IWayOnWorld.IsBlocked(int from, int to) => GleyGraph.IsBlockedStep(from, to) || IsBlocked(from, to);

        void IWayOnWorld.Note(string text) => Log.Info("NWH planner: " + text + ".");

        private static bool Contains(IReadOnlyList<int> list, int value)
        {
            for (var k = 0; k < list.Count; k++) if (list[k] == value) return true;
            return false;
        }
    }
}
