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
    internal static class NwhShape
    {
        private static readonly Dictionary<string, CarSpec?> known = new Dictionary<string, CarSpec?>();

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
                if (asset == null || vehicle == null) { Log.Trace($"NWH shape for {vehicleTypeName}: no drivable prefab."); return null; }
                var root = asset.transform;
                var wheels = new List<Vector3>();
                foreach (var wheel in asset.GetComponentsInChildren<NWH.Common.Vehicles.WheelUAPI>(true)) wheels.Add(root.InverseTransformPoint(wheel.transform.position));
                if (wheels.Count < 2) { Log.Trace($"NWH shape for {vehicleTypeName}: {wheels.Count} wheels."); return null; }
                float rear = float.MaxValue, front = float.MinValue;
                foreach (var w in wheels) { rear = Mathf.Min(rear, w.z); front = Mathf.Max(front, w.z); }
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
                Log.Trace($"NWH shape for {vehicleTypeName}: {spec}; steer rate {vehicle.steering.degreesPerSecondLimit:0}°/s, front group coefficient {coefficient:0.00}. As text: " +
                         string.Format(inv, "{0};{1:0.###};{2:0};{3:0.###},{4:0.###},{5:0.###};{6:0.###},{7:0.###},{8:0.###};0={9:0.##},10={10:0.##},25={11:0.##},50={12:0.##};{13:0.###};{14:0}",
                             spec.Name, spec.Wheelbase, topKmh, centre.X, centre.Y, centre.Z, half.X, half.Y, half.Z,
                             lockAt(0f), lockAt(10f / 3.6f), lockAt(25f / 3.6f), lockAt(50f / 3.6f), track, spec.WheelRate));
                return spec;
            }
            catch (Exception ex) { Log.Error($"NWH shape for {vehicleTypeName}: {ex.Message}"); return null; }
        }

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

    internal sealed class NwhPlanner : IWayOnWorld
    {
        private static readonly Dictionary<string, NwhPlanner> planners = new Dictionary<string, NwhPlanner>();
        private static object? plannersFor;

        public readonly CarSpec Car;
        public readonly Feasibility Check;
        public Transform? Ignore { get; set; }
        private readonly HashSet<(int, int)> laneClear = new HashSet<(int, int)>();
        private readonly Dictionary<(int, int), (BlockCause Cause, float At)> blocked = new Dictionary<(int, int), (BlockCause, float)>();
        private readonly Collider[] hits = new Collider[32];
        private readonly Vector3 laneHalf, bodyHalf;

        private const float RoadClearance = 0.35f;
        private static readonly int RaisedMask = LayerMask.GetMask("Roads", "Default");
        private static int parkedMask = -1;
        // The lane sweep's: parked cars and the player's cars at the width the driver stops for, and what truly
        // stands in the lane (props, buildings, the road's raised edges, median islands) at the car's own width, a
        // near-miss wider, from RoadClearance up. Kerb props along a lane's edge ('Curb1', a 'Divider' beside the
        // inner lane) are low or outside that: T3 (session 20261013-185723) refused every ride because the sweep took
        // them for cars in the lane; the manoeuvres' body sweep still counts every prop.
        private static int CarsMask => (1 << LayerHelper.ParkedVehiclesLayerIndex) | (1 << LayerHelper.PlayerVehiclesLayerIndex);
        private static int ParkedMask => parkedMask >= 0 ? parkedMask
            : parkedMask = (1 << LayerHelper.ParkedVehiclesLayerIndex) | (1 << LayerHelper.PlayerVehiclesLayerIndex) | TestCar.ObstacleMask;
        public const float PlannedAheadMetres = 200f;

        private readonly LaneGraph graph;

        private readonly Func<int, float>? limitKmh;

        private NwhPlanner(CarSpec car, bool speedLimits)
        {
            Car = car;
            graph = GleyGraph.Graph!;
            limitKmh = speedLimits ? LimitKmh : (Func<int, float>?)null;
            Check = new Feasibility(graph, car, WorldHit, Feasibility.DefaultMargin, limitKmh)
                { GroundAt = GroundAt, PavementAt = Sidewalk.Available ? Sidewalk.At : (Func<Vec3, bool>?)null };
            var h = car.BodyHalf;
            laneHalf = new Vector3(h.X + 0.4f, 0.6f, 0.5f);
            bodyHalf = new Vector3(h.X + Feasibility.NearMiss, h.Y, 0.5f);
        }

        public static NwhPlanner? For(string vehicleTypeName, float topKmh)
        {
            var graph = GleyGraph.Graph;
            if (graph == null) return null;
            if (!ReferenceEquals(plannersFor, graph)) { planners.Clear(); plannersFor = graph; }
            var speedLimits = !Options.IgnoreSpeedLimits;
            var key = vehicleTypeName + "@" + topKmh.ToString(CultureInfo.InvariantCulture) + (speedLimits ? "" : "@nolimits");
            if (planners.TryGetValue(key, out var planner)) return planner;
            var spec = NwhShape.For(vehicleTypeName, topKmh);
            if (spec == null) return null;
            planner = new NwhPlanner(spec, speedLimits);
            planners[key] = planner;
            return planner;
        }

        public static NwhPlanner? ForTrafficPrefab(GameObject trafficPrefab, float topKmh)
        {
            var type = DrivableSteering.TypeOf(trafficPrefab.name) ?? NwhDress.DonorFor(trafficPrefab, out _)?.TypeName;
            return type != null ? For(type, topKmh) : null;
        }

        public void Block(int from, int to, BlockCause cause = BlockCause.Manoeuvre) => blocked[(from, to)] = (cause, Time.realtimeSinceStartup);

        public bool IsBlocked(int from, int to) => blocked.ContainsKey((from, to)) || Gates.LockedStep(from, to);

        public void BeginRide()
        {
            var stale = new List<(int, int)>();
            foreach (var pair in blocked) if (pair.Value.Cause != BlockCause.Manoeuvre) stale.Add(pair.Key);
            foreach (var key in stale)
            {
                blocked.Remove(key);
                Check.Forget(key.Item1, key.Item2);
            }
            laneClear.Clear();
            Gates.Refresh(graph);
        }

        public static BlockCause CauseOf(LaneGraph graph, int from, int to, string what) => WayOnPlanner.CauseOf(graph, from, to, what);

        private static float LimitKmh(int waypoint)
        {
            var all = GleyGraph.Waypoints;
            return all != null && waypoint >= 0 && waypoint < all.Length && all[waypoint] != null ? all[waypoint].maxSpeed : 0f;
        }

        private string? WorldHit(BodyBox box)
        {
            var rotation = Quaternion.Euler(0f, box.Heading * Mathf.Rad2Deg, 0f);
            var centre = new Vector3(box.Centre.X, box.Centre.Y, box.Centre.Z);
            var half = new Vector3(box.Half.X, box.Half.Y, box.Half.Z);
            if (Overlap(centre, half, rotation, CarsMask) is string car) return Feasibility.Movable + car;
            if (Overlap(centre, half, rotation, ParkedMask & ~CarsMask) is string hit) return hit;
            return Raised(centre, half, rotation, centre.y - Car.BodyCentre.Y) is string raised ? "road edge " + raised : null;
        }

        private static int groundMask;
        private static int GroundMask => groundMask != 0 ? groundMask
            : groundMask = Physics.DefaultRaycastLayers & ~(ParkedMask | (1 << LayerHelper.VehiclesLayerIndex) | (NwhCar.GleyLayer >= 0 ? 1 << NwhCar.GleyLayer : 0)
                | LayerMask.GetMask("Human", "Ignore Raycast"));
        private static readonly RaycastHit[] groundHits = new RaycastHit[8];

        private const float MaxStep = Feasibility.KerbMax;

        private float? GroundAt(Vec3 p) => GroundBelow(p, Ignore);

        internal static float? GroundBelow(Vec3 p, Transform? ignore)
        {
            var n = Physics.RaycastNonAlloc(new Vector3(p.X, p.Y + 2f, p.Z), Vector3.down, groundHits, 4f, GroundMask, QueryTriggerInteraction.Ignore);
            float? highest = null;
            for (var h = 0; h < n; h++)
            {
                if (ignore != null && groundHits[h].transform.IsChildOf(ignore)) continue;
                if (groundHits[h].point.y > p.Y + MaxStep) continue;
                if (highest == null || groundHits[h].point.y > highest) highest = groundHits[h].point.y;
            }
            return highest;
        }

        // The road a car is set down on at a waypoint (the bridge landing): the surface nearest the waypoint's height
        // within 1.5 m, above or below. Lanes on a bridge's ramp run up to 0.6 m under the deck (waypoint 17753: lane
        // 5.86, road 6.44), which GroundBelow leaves out as a deck over the lane; a car set on the lane there started
        // inside the road and was not drawn until physics pushed it out.
        internal static float? RoadAt(Vector3 p, Transform? ignore)
        {
            var n = Physics.RaycastNonAlloc(p + Vector3.up * 1.5f, Vector3.down, groundHits, 3f, GroundMask, QueryTriggerInteraction.Ignore);
            float? best = null;
            for (var h = 0; h < n; h++)
            {
                if (ignore != null && groundHits[h].transform.IsChildOf(ignore)) continue;
                var y = groundHits[h].point.y;
                if (best == null || Mathf.Abs(y - p.y) < Mathf.Abs(best.Value - p.y)) best = y;
            }
            return best;
        }

        private string? Overlap(Vector3 centre, Vector3 half, Quaternion rotation, int mask)
        {
            var n = Physics.OverlapBoxNonAlloc(centre, half, hits, rotation, mask, QueryTriggerInteraction.Ignore);
            for (var h = 0; h < n; h++)
                if ((Ignore == null || !hits[h].transform.IsChildOf(Ignore)) && !Gates.IsOpenDoor(hits[h])) return Name(hits[h]);
            return null;
        }

        private string? Raised(Vector3 centre, Vector3 half, Quaternion rotation, float ground, int mask = 0)
        {
            var bottom = Mathf.Max(centre.y - half.y, ground + RoadClearance);
            var top = centre.y + half.y;
            if (top <= bottom) return null;
            var raisedHalf = new Vector3(half.x, Mathf.Max(0.05f, (top - bottom) / 2f), half.z);
            var lane = new Vector3(centre.x, ground, centre.z);
            var n = Physics.OverlapBoxNonAlloc(new Vector3(centre.x, (top + bottom) / 2f, centre.z), raisedHalf, hits, rotation, mask != 0 ? mask : RaisedMask, QueryTriggerInteraction.Ignore);
            for (var h = 0; h < n; h++)
                if ((Ignore == null || !hits[h].transform.IsChildOf(Ignore)) && !Gates.IsOpenDoor(hits[h]) && !IsRoadSurface(hits[h], lane)) return Name(hits[h]);
            return null;
        }

        private const float SurfaceReach = 0.6f;

        private static bool IsRoadSurface(Collider c, Vector3 onLine)
        {
            if (!c.Raycast(new Ray(onLine + Vector3.up * 2f, Vector3.down), out var down, 4f)) return false;
            return down.normal.y > 0.7f && Mathf.Abs(down.point.y - onLine.y) <= SurfaceReach;
        }

        private static string Name(Collider c) => $"'{c.name}' ({LayerMask.LayerToName(c.gameObject.layer)})";

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
                if (Bridges.Skips(from)) { gone += step; continue; }
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

        private IEnumerable<object?> BlockedAheadWork(LaneGraph graph, IReadOnlyList<int> path, int fromPosition, float metres, Vec3? near,
            List<(int From, int To, string? What, float At)> result)
        {
            if (near is Vec3 here) laneClear.RemoveWhere(step => Vec3.HorizontalDistance(here, graph.Position(step.Item2)) > LoadedMetres);
            var gone = 0f;
            for (var k = Math.Max(0, fromPosition); k + 1 < path.Count && gone < metres; k++)
            {
                int from = path[k], to = path[k + 1];
                gone += Vec3.HorizontalDistance(graph.Position(from), graph.Position(to));
                if (!Contains(graph.Successors(from), to) || laneClear.Contains((from, to)) || Bridges.Skips(from)) continue;
                var what = LaneBlocked(graph, from, to, out var at);
                result.Add((from, to, what, at));
                if (what == null && near is Vec3 n && Vec3.HorizontalDistance(n, graph.Position(to)) <= LoadedMetres) laneClear.Add((from, to));
                yield return null;
            }
        }

        public const float LoadedMetres = WayOnPlanner.LoadedMetres;

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
            var bodyBottom = Mathf.Max(0.05f, Car.BodyCentre.Y - Car.BodyHalf.Y);
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
                foreach (var (hit, clear, lowest) in new[] { (CarsMask, carClear, 0.3f), (TestCar.ObstacleMask, propClear, bodyBottom), (RaisedMask, propClear, RoadClearance) })
                {
                    var box = new Vector3(Pavement.LaneHalf + 0.5f, (1.5f - lowest) / 2f, 0.5f);
                    var n = Physics.OverlapBoxNonAlloc(centre + Vector3.up * ((1.5f + lowest) / 2f), box, hits, rotation, hit, QueryTriggerInteraction.Ignore);
                    for (var h = 0; h < n; h++)
                    {
                        var c = hits[h];
                        if (Ignore != null && c.transform.IsChildOf(Ignore)) continue;
                        if (Gates.IsOpenDoor(c)) continue;
                        // The road the wheels drive on, though it rises above RoadClearance somewhere in its bounds (0.1.90:
                        // a bridge's 25 x 24 m 'BridgeJointCollider', taken for something in the lane).
                        if (hit == RaisedMask && IsRoadSurface(c, centre)) continue;
                        // Its edge nearest the line at this point, sideways: a ray each way from the line at a height the
                        // obstacle has (a guardrail is 0.8 m tall). Not its bounds: a long curved guardrail's box
                        // (34 x 33 m in the Hamptons) holds the whole lane, and every step along it was blocked.
                        var height = Mathf.Clamp(c.bounds.max.y - ground - 0.15f, lowest + 0.05f, 0.9f);
                        var probe = centre + Vector3.up * height;
                        float? side = null;
                        if (c.Raycast(new Ray(probe, right), out var toRight, 3f)) side = toRight.distance;
                        if (c.Raycast(new Ray(probe, -right), out var toLeft, 3f) && (side == null || toLeft.distance < side.Value)) side = -toLeft.distance;
                        if (side == null && (c.Raycast(new Ray(probe - forward * 0.5f, forward), out _, 1f) || c.Raycast(new Ray(probe + forward * 0.5f, -forward), out _, 1f)))
                            side = 0f;
                        if (side == null)
                        {
                            if (c is MeshCollider mesh && !mesh.convex)
                            {
                                // A ray meets a mesh's faces only from their front: from inside a mesh, or from behind a
                                // one-sided one (a guardrail facing away from the road), the rays out miss it. Rays in
                                // from 3 m either side: met from both, the line runs through it (review round 1: such a
                                // collider was skipped); from one, its face is that far to that side (0.1.80 took any
                                // one as the line running through it).
                                var inFromRight = c.Raycast(new Ray(probe + right * 3f, -right), out var fromRight, 3f);
                                var inFromLeft = c.Raycast(new Ray(probe - right * 3f, right), out var fromLeft, 3f);
                                if (inFromRight && inFromLeft) side = 0f;
                                else if (inFromRight) side = 3f - fromRight.distance;
                                else if (inFromLeft) side = fromLeft.distance - 3f;
                                else continue;
                            }
                            else if ((c.ClosestPoint(probe) - probe).sqrMagnitude > 0.0025f) continue;
                            else side = 0f;
                        }
                        var need = half + clear;
                        if (Mathf.Abs(side.Value) >= need) continue;
                        first ??= f * length;
                        last = f * length;
                        what ??= (hit == CarsMask ? "parked in the lane: " : "in the lane: ") + Name(c) + $" (bounds {c.bounds.min} to {c.bounds.max})";
                        if (Mathf.Abs(side.Value) < 0.05f) { lo = 1f; hi = 0f; continue; }
                        if (side.Value > 0f) hi = Mathf.Min(hi, side.Value - need);
                        else lo = Mathf.Max(lo, side.Value + need);
                    }
                }
            }
            if (first == null) { shifts.Remove((from, to)); return null; }
            if (lo > hi) { shifts.Remove((from, to)); at = first.Value; return what + ", no room to pass within the lane"; }
            var offset = Mathf.Clamp(0f, lo, hi);
            if (Mathf.Abs(offset) < 0.1f) { shifts.Remove((from, to)); return null; }
            var rear = Car.BodyHalf.Z - Car.BodyCentre.Z;
            var move = (first.Value - Car.FrontAhead - 1f, last!.Value + rear + 1f, offset);
            shifts[(from, to)] = move;
            Log.Trace($"NWH planner: step {from} -> {to} passed {offset:+0.00;-0.00} m sideways ({what}).");
            return null;
        }

        private readonly Dictionary<(int, int), (float From, float To, float Offset)> shifts = new Dictionary<(int, int), (float, float, float)>();
        public (float From, float To, float Offset)? ShiftFor(int from, int to) => shifts.TryGetValue((from, to), out var s) ? s : ((float, float, float)?)null;

        private string? LaneChangeBlocked(LaneGraph graph, int from, int to)
        {
            Vec3 a = graph.Position(from), b = graph.Position(to);
            var radius = Car.BodyHalf.X + 0.1f;
            var lift = RoadClearance + radius;
            var n = Physics.OverlapCapsuleNonAlloc(new Vector3(a.X, a.Y + lift, a.Z), new Vector3(b.X, b.Y + lift, b.Z), radius, hits, ParkedMask, QueryTriggerInteraction.Ignore);
            for (var h = 0; h < n; h++)
                if (Ignore == null || !hits[h].transform.IsChildOf(Ignore)) return "lane change: " + Name(hits[h]);
            return null;
        }

        public (Route? Route, DrivePlan? Plan, string Summary) PlanRoute(LaneGraph graph, int start, IReadOnlyList<Goal> goals, int vehicleType, int rounds = PlanRounds)
        {
            var into = new Planned();
            foreach (var _ in PlanRouteWork(graph, start, goals, vehicleType, rounds, null, into)) { }
            return (into.Route, into.Plan, into.Summary);
        }

        public const int PlanRounds = 16;

        private sealed class Planned
        {
            public Route? Route;
            public DrivePlan? Plan;
            public string Summary = "";
        }

        private const int SearchStep = 2000;
        private const int MaxExpansions = 200000;

        private IEnumerable<object?> PlanRouteWork(LaneGraph graph, int start, IReadOnlyList<Goal> goals, int vehicleType, int rounds, WayOnJob? job, Planned into, bool atGate = false)
        {
            if (!Gates.Read) Gates.Refresh(graph);
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
                    foreach (var y in Check.PrepareWork(route.Waypoints)) yield return y;
                    var built = DrivePlan.Build(graph, route.Waypoints, Car, Check.Arc, Check.Clear, limitKmh, ShiftFor);
                    ThroughGates(built);
                    OffBridges(built);
                    yield return null;
                    var notes = new List<string>();
                    foreach (var y in Check.Validate(built, found, notes)) yield return y;
                    foreach (var note in notes) Log.Trace("NWH planner: " + note + ".");
                    if (found.Count == 0) { plan = built; break; }
                }
                foreach (var (from, to, what) in found)
                {
                    if (what.Contains(Feasibility.NoCase)) noCase++;
                    shifts.Remove((from, to));
                    Block(from, to, CauseOf(graph, from, to, what));
                    Log.Trace($"NWH planner: blocked {StepKind(graph, from, to)} {from} -> {to} for {Car.Name}: {what}.");
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
            if (noCase > 0) Log.Trace($"NWH planner: {noCase} steps blocked with no case to check them on.");
            into.Route = route;
            into.Plan = route != null ? plan : null;
            var earlier = blocked.Count - blockedNotes.Count;
            // No way round a gate that stays shut for the player, and the goal behind it: the ride ends before the
            // gate, on the lane, with room to stop clear of its doors and trigger, and the player walks the rest
            // (Peter, 14 October 2026; the game's taxi refuses such a ride outright, TaxiSystem.CanEnterPrivateFence).
            if (route == null && !atGate)
            {
                Route? through = null;
                var probe = RoutePlanner.Begin(graph, new RouteRequest
                {
                    Start = start, Goals = goals, VehicleType = vehicleType, IsDisabled = GleyGraph.IsDisabled,
                    IsBlockedStep = (a, b) => GleyGraph.IsBlockedStep(a, b) || blocked.ContainsKey((a, b)), WideUTurns = true,
                });
                Func<bool>? stop = job != null ? () => job.OverBudget : (Func<bool>?)null;
                while (probe.Status == PlanStatus.Running && probe.Expanded < MaxExpansions)
                {
                    probe.Step(job != null ? SearchStep : MaxExpansions, stop);
                    if (probe.Status == PlanStatus.Running) yield return null;
                }
                through = probe.Status == PlanStatus.Found ? probe.Result : null;
                if (through != null)
                {
                    var w = through.Waypoints;
                    for (var k = 0; k + 1 < w.Count; k++)
                    {
                        if (!(Gates.On(w[k], w[k + 1]) is Gates.Found gate) || !gate.Gate.Locked) continue;
                        var stopAt = GateCrossing.DropOff(graph, w, k, gate.Gate, GateClear);
                        if (stopAt <= 0) break;
                        Log.Trace($"NWH planner: the way to the goal goes through the gate '{gate.Gate.Name}', shut for the player; the ride ends at waypoint {w[stopAt]} before it.");
                        var atTheGate = new Planned();
                        foreach (var y in PlanRouteWork(graph, start, new[] { new Goal(w[stopAt], 0f) }, vehicleType, rounds, job, atTheGate, true)) yield return y;
                        into.Route = atTheGate.Route;
                        into.Plan = atTheGate.Plan;
                        into.Summary = $"ends at the gate '{gate.Gate.Name}' (shut for the player), at waypoint {w[stopAt]}: " + atTheGate.Summary;
                        yield break;
                    }
                }
            }
            into.Summary = (route != null ? $"{(driven.Count > 0 ? string.Join(", ", driven) : "no added turns")}, {changes} lane changes" : $"no route ({Math.Max(0, earlier)} manoeuvres blocked earlier this session)")
                + (blockedNotes.Count > 0 ? $"; blocked {blockedNotes.Count}: {string.Join("; ", blockedNotes.GetRange(0, Math.Min(4, blockedNotes.Count)))}{(blockedNotes.Count > 4 ? "; ..." : "")}" : "")
                + (noCase > 0 ? $"; {noCase} with no case to check" : "");
        }

        private static string StepKind(LaneGraph graph, int from, int to) => WayOnPlanner.StepKind(graph, from, to);

        // Nothing of a plan is checked where a bridge skip jumps the car (Bridges): no corner within two waypoints of a
        // skipped stretch (a corner's sweep reaches 10 m either side), no sideways move on one.
        private static void OffBridges(DrivePlan plan)
        {
            var path = plan.Path;
            bool Near(int k)
            {
                for (var q = Math.Max(0, k - 2); q <= k + 2 && q < path.Count; q++) if (Bridges.Skips(path[q])) return true;
                return false;
            }
            plan.Fillets.RemoveWhere(Near);
            plan.Shifts.RemoveAll(shift => Bridges.Skips(path[shift.Position]));
        }

        public const float GateClear = 3f;

        private static void ThroughGates(DrivePlan plan)
        {
            var path = plan.Path;
            for (var k = 0; k + 1 < path.Count; k++)
            {
                if (!(Gates.On(path[k], path[k + 1]) is Gates.Found gate) || gate.Gate.Locked) continue;
                if (k > 0) plan.Slow.Add((k - 1, Feasibility.SqueezeKmh));
                plan.Slow.Add((k, Feasibility.SqueezeKmh));
            }
        }

        public static int RouteStepAt(IReadOnlyList<int> route, IReadOnlyList<int> path, int position) => WayOnPlanner.RouteStepAt(route, path, position);

        public DrivePlan? Plan(LaneGraph graph, IReadOnlyList<int> route, out List<(int From, int To, string What)> failed)
        {
            var plan = DrivePlan.Build(graph, route, Car, Check.Arc, Check.Clear, limitKmh, ShiftFor);
            ThroughGates(plan);
            OffBridges(plan);
            var notes = new List<string>();
            failed = Check.ValidateNow(plan, notes);
            foreach (var note in notes) Log.Trace("NWH planner: " + note + ".");
            foreach (var (from, to, what) in failed)
            {
                shifts.Remove((from, to));
                Log.Trace($"NWH planner: step {from} -> {to} fails as driven ({what}).");
            }
            return failed.Count == 0 ? plan : null;
        }

        public WayOnPlanner WayOn => wayOn ??= new WayOnPlanner(this);
        private WayOnPlanner? wayOn;

        public WayOnJob PlanRoundBlock(IReadOnlyList<int> route, IReadOnlyList<int> path, int position, string what,
            Func<CarPose> pose, IReadOnlyList<Goal> goals, int vehicleType) => WayOn.RoundBlock(route, path, position, what, pose, goals, vehicleType);

        public WayOnJob LookAhead(IReadOnlyList<int> route, IReadOnlyList<int> path, int position, Func<CarPose> pose,
            IReadOnlyList<Goal> goals, int vehicleType, WayOnPlanner.Watch watch) => WayOn.LookAhead(route, path, position, pose, goals, vehicleType, watch);

        LaneGraph IWayOnWorld.Graph => graph;
        Feasibility IWayOnWorld.Check => Check;

        IEnumerable<object?> IWayOnWorld.PlanRoute(int start, IReadOnlyList<Goal> goals, int vehicleType, WayOnJob? job, PlannedRoute into)
        {
            var planned = new Planned();
            foreach (var y in PlanRouteWork(graph, start, goals, vehicleType, PlanRounds, job, planned)) yield return y;
            into.Route = planned.Route;
            into.Plan = planned.Plan;
            into.Summary = planned.Summary;
        }

        IEnumerable<object?> IWayOnWorld.LanesAhead(IReadOnlyList<int> path, int position, float metres, Vec3 near, List<(int From, int To, string? What, float At)> swept) =>
            BlockedAheadWork(graph, path, position, metres, near, swept);

        bool IWayOnWorld.IsBlocked(int from, int to) => GleyGraph.IsBlockedStep(from, to) || IsBlocked(from, to);

        void IWayOnWorld.Note(string text) => Log.Trace("NWH planner: " + text + ".");

        private static bool Contains(IReadOnlyList<int> list, int value)
        {
            for (var k = 0; k < list.Count; k++) if (list[k] == value) return true;
            return false;
        }
    }
}
