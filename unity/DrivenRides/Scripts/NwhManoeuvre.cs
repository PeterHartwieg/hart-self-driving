#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DrivenRides.Core.Drive;
using DrivenRides.Core.Parking;
using DrivenRides.Core.Routing;
using GleyTrafficSystem;
using Helpers;
using UI;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace DrivenRides
{
    internal sealed class ManoeuvreResult
    {
        public bool Success;
        public string Reason = "cancelled";
    }

    // A short-lived controller for local manoeuvres. Never writes a road plan, waypoint or lane offset.
    internal sealed class NwhManoeuvre : MonoBehaviour
    {
        private NwhCar car = null!;
        private CarSpec spec = null!;
        private ManoeuvrePlan plan = null!;
        private ManoeuvreDriver driver = null!;
        private ManoeuvreWorld world = null!;
        private GleyTraffic traffic = null!;
        private ParkingTraffic? reservation;
        private CancellationTokenSource? searchCancellation;
        private readonly List<OtherCar> cars = new List<OtherCar>();
        private float elapsed, waiting, stalled, lastProgress, nextTrace;
        private Vector3 lastPosition;
        public bool Ended { get; private set; }
        public bool Arrived { get; private set; }
        public bool Waiting { get; private set; }
        public string Reason { get; private set; } = "";

        public static CarPose Pose(NwhCar car)
        {
            var rear = car.RearAxle;
            return new CarPose(rear.x, rear.z, car.Root.transform.eulerAngles.y * Mathf.Deg2Rad,
                Vector3.Dot(car.Body.velocity, car.Root.transform.forward), car.Vehicle.steering.angle);
        }

        public static IEnumerator Run(NwhCar car, CarSpec spec, CarPose target, Func<bool> stop, ManoeuvreResult result, bool parking = false, bool departure = false, ManoeuvreWorld? snapshot = null)
        {
            Hold(car);
            if (!ParkingTraffic.HooksReady) { result.Reason = "traffic reservation is unavailable"; yield break; }
            NwhManoeuvre? drive = null;
            CancellationTokenSource? cancelSearch = null;
            try
            {
                if (stop() || car.Root == null) yield break;
                // Own the reservation before the incremental search. Stop/disable/unload can now release it
                // even while the coroutine is still planning and Unity abandons its iterator.
                drive = car.Root.AddComponent<NwhManoeuvre>();
                drive.car = car; drive.spec = spec;
                drive.reservation = new ParkingTraffic(drive, car,
                    TrafficReservation.Preparing(spec, Pose(car), departure ? Pose(car) : target, car.Root.transform.position.y, parking));
                var world = snapshot ?? new ManoeuvreWorld(car, spec, target, departure);
                if (!world.Ready) { result.Reason = "too many nearby obstacles"; yield break; }
                if (departure)
                {
                    var surface = world.ReadSurface(target);
                    while (surface.MoveNext()) { if (stop() || car.Root == null || drive.Ended) yield break; yield return surface.Current; }
                    if (world.Space.Estimate?.Invoke(Pose(car)) == float.PositiveInfinity)
                    { result.Reason = "parking area has no clear ground connection to this lane"; world.Dump(target, parking); yield break; }
                }
                // Only the immutable Core snapshot goes to a worker. Sample Unity's steering curve here;
                // the bounded low-speed search must not call a game/Unity API off the main thread.
                var lockDegrees = spec.LockAt(.8f);
                var searchCar = departure ? new CarSpec(spec.Name,spec.Wheelbase,_=>lockDegrees,spec.BodyCentre,spec.BodyHalf,
                    spec.TopKmh,spec.TrackWidth,spec.WheelRate) {YawFactor=spec.YawFactor} : spec;
                // Both curbside and lot departures hand over to a road; only an arrival needs parking precision.
                var search = new ManoeuvrePlanner(searchCar, world.Space, Pose(car), target, stageForParking: parking, roadEntry: !parking);
                var total = Stopwatch.StartNew();
                if (departure)
                {
                    cancelSearch = new CancellationTokenSource(); drive.searchCancellation=cancelSearch; var token=cancelSearch.Token;
                    var trackingFailure="";
                    var worker=Task.Run(() => {
                        while(!search.Done && !token.IsCancellationRequested) search.Step(8);
                        if(search.Result != null && !token.IsCancellationRequested)
                            trackingFailure=ManoeuvreDriver.Validate(searchCar,search.Result,
                                p=>world.Space.ClearBody(searchCar,p,.15f) && world.SurfaceClear(p,0),()=>token.IsCancellationRequested);
                    });
                    var expired=false; var aborted=false;
                    while(!worker.IsCompleted)
                    {
                        if (stop() || car.Root == null || drive.Ended) { aborted=true; cancelSearch.Cancel(); }
                        if(total.Elapsed.TotalSeconds>12) { expired=true; cancelSearch.Cancel(); }
                        yield return null;
                    }
                    worker.GetAwaiter().GetResult();
                    if(aborted) yield break;
                    if(expired) { result.Reason="manoeuvre planning timed out"; world.Dump(target,parking); yield break; }
                    if(trackingFailure.Length>0) { result.Reason="manoeuvre tracking: "+trackingFailure; world.Dump(target,parking); yield break; }
                }
                else while (!search.Done)
                {
                    if (stop() || car.Root == null || drive.Ended) yield break;
                    var frame=Stopwatch.StartNew();
                    do { search.Step(1); } while(!search.Done && frame.Elapsed.TotalMilliseconds<3);
                    if(total.Elapsed.TotalSeconds>8) { result.Reason="manoeuvre planning timed out"; yield break; }
                    yield return null;
                }
                if (search.Result == null)
                {
                    result.Reason = search.Reason;
                    Log.Trace($"Manoeuvre search: {search.Reason}, {search.Expanded} expansions.");
                    world.Dump(target, parking);
                    yield break;
                }
                // The broad-phase snapshot is conservative for bodies. Check the actual surface and world too before
                // moving, spread over frames. A fresh scan while driving catches changed or newly spawned obstacles.
                for (var i = 0; i < search.Result.Points.Count; i += 3)
                {
                    if (stop() || car.Root == null || drive.Ended) yield break;
                    if (!world.Clear(search.Result.Points[i].Pose, false)) { result.Reason = "manoeuvre blocked: " + world.LastBlock; yield break; }
                    if (i % 30 == 0) yield return null;
                }
                if (stop() || car.Root == null || drive.Ended) yield break;
                drive.plan = search.Result; drive.world = world;
                drive.reservation.SetPlan(spec, search.Result);
                drive.driver = new ManoeuvreDriver(spec, search.Result); drive.traffic = new GleyTraffic(car.Root);
                drive.lastPosition = car.RearAxle;
                Log.Info($"Manoeuvre: {search.Result.Length:0.0} m, {search.Expanded} expansions, {total.ElapsedMilliseconds} ms to plan.");
                while (!drive.Ended)
                {
                    if (stop() || car.Root == null) { drive.Stop("cancelled"); break; }
                    yield return null;
                }
                result.Success = drive.Arrived; result.Reason = drive.Reason;
            }
            finally
            {
                cancelSearch?.Cancel();
                if (drive != null) { drive.Stop("cancelled"); UnityEngine.Object.Destroy(drive); }
                cancelSearch?.Dispose();
            }
        }

        private void FixedUpdate()
        {
            if (Ended || driver == null) return;
            try
            {
                if (car.Root == null || !OwnCarDrive.IsActive) { Stop("the car is no longer controlled by the ride"); return; }
                if (InstanceBehavior<UIs>.Instance.gameSpeed.Paused) { Hold(car); return; }
                if (OwnCarDrive.Trouble() is string trouble) { Stop(trouble); return; }
                elapsed += Time.fixedDeltaTime;
                var pose = Pose(car);
                traffic.ReadCars(car.Root.transform.position, 60, cars);
                // Another deck of road must not block this street's manoeuvre or its reservation.
                for (var i = cars.Count - 1; i >= 0; i--)
                    if (Mathf.Abs(traffic.Components[i].transform.position.y - car.Root.transform.position.y) >= 2)
                    { cars.RemoveAt(i); traffic.Components.RemoveAt(i); }
                var clearing = reservation?.Occupied(cars, traffic.Components) ?? true;
                var blocked = clearing || driver.Blocked(pose, cars, p => world.Clear(p, true));
                Waiting = blocked;
                waiting = blocked ? waiting + Time.fixedDeltaTime : 0f;
                var output = driver.Step(pose, blocked, Time.fixedDeltaTime);
                if (TestMode.On && elapsed >= nextTrace)
                {
                    nextTrace = elapsed + 2;
                    Log.Trace($"Manoeuvre step: {driver.Cursor}/{plan.Points.Count}, speed {pose.Speed:0.00}, want {output.Desired:0.00}, throttle {output.Throttle:0.00}, handbrake {output.Handbrake:0.00}, wheel {pose.Wheel:0.0}/{output.SteerDegrees:0.0}, blocked {blocked}, clearing {clearing}, held {reservation?.HeldCount ?? 0} {(blocked ? world.LastBlock : "")}");
                }
                if (driver.Failure.Length > 0) { Stop(driver.Failure); return; }
                if (driver.Done) { Arrived = true; Stop("completed"); return; }
                if (Vector3.Distance(lastPosition, car.RearAxle) > 0.2f) { lastPosition = car.RearAxle; lastProgress = elapsed; }
                stalled = elapsed - lastProgress;
                if (waiting > 30f || stalled > 35f || elapsed > 150f) { Stop("no safe progress during manoeuvre"); return; }
                var input = car.Vehicle.input;
                input.Steering = Mathf.Clamp(output.SteerDegrees / Mathf.Max(1, car.LockAt(pose.Speed)), -1, 1);
                input.Throttle = 0; input.Brakes = 0; input.Handbrake = output.Handbrake;
                if (output.Desired != 0 && output.Handbrake < 1)
                {
                    var reverse = output.Desired < 0;
                    var transmission = car.Vehicle.powertrain.transmission;
                    if (Mathf.Abs(pose.Speed) < 0.1f && (reverse ? transmission.Gear >= 0 : transmission.Gear <= 0)) transmission.ShiftInto(reverse ? -1 : 1);
                    if (reverse && input.swapInputInReverse) input.Brakes = output.Throttle;
                    else input.Throttle = output.Throttle;
                }
            }
            catch (Exception ex) { Log.Error("Manoeuvre: " + ex); Stop("controller failed"); }
        }

        public void Stop(string reason)
        {
            if (Ended) return;
            Ended = true; Reason = reason;
            searchCancellation?.Cancel(); searchCancellation=null;
            reservation?.Dispose(); reservation = null;
            if (car != null && car.Root != null) Hold(car);
            Log.Info("Manoeuvre: " + reason + ".");
            if (TestMode.On && car != null && car.Root != null && plan != null)
            {
                var p = Pose(car);
                Log.Trace($"Manoeuvre ended at ({p.X:0.00}, {p.Z:0.00}), heading {p.Heading:0.000}, wheel {p.Wheel:0.0}; goal ({plan.Goal.X:0.00}, {plan.Goal.Z:0.00}), heading {plan.Goal.Heading:0.000}; cursor {driver.Cursor}/{plan.Points.Count}.");
            }
        }
        private void OnDisable() { if (!Ended && car != null) Stop("controller disabled"); }
        private void OnCollisionEnter(Collision collision)
        {
            if (Ended || driver == null) return;
            foreach (var contact in collision.contacts)
                if (Mathf.Abs(contact.normal.y) < 0.5f) { Stop("contact with " + collision.gameObject.name); break; }
        }
        internal static void Hold(NwhCar car)
        {
            if (car.Root == null) return;
            car.Vehicle.input.Throttle = 0; car.Vehicle.input.Brakes = 0; car.Vehicle.input.Handbrake = 1;
        }
    }

    // Snapshot for search; precise live overlap/ground checks for execution. Own colliders and road surfaces do
    // not obstruct the car, but other cars, props, sidewalk and missing ground do. No road-planner caches are used.
    internal sealed class ManoeuvreWorld
    {
        public readonly ManoeuvreSpace Space;
        public bool Ready { get; }
        public string LastBlock { get; private set; } = "";
        private readonly NwhCar car;
        private readonly CarSpec spec;
        private readonly float height;
        private readonly Collider[] hits = new Collider[4096];
        private readonly List<Box> junctions = new List<Box>();
        private ManoeuvreGrid? surfaceGrid;
        private readonly Box parkedFootprint;
        private readonly int vehicles = LayerMask.GetMask("Vehicles", "PlayerVehicles", "ParkedVehicles");
        private readonly int solids = TestCar.ObstacleMask | LayerMask.GetMask("Roads", "Default");

        public ManoeuvreWorld(NwhCar car, CarSpec spec, CarPose goal, bool departure = false)
        {
            this.car = car; this.spec = spec;
            height = NwhPlanner.GroundBelow(GleyGraph.ToVec(car.RearAxle), car.Root.transform) ?? car.Root.transform.position.y;
            var start = NwhManoeuvre.Pose(car);
            parkedFootprint = ManoeuvrePlan.Body(spec,start,.35f);
            var reach = Mathf.Max(8, spec.FrontAhead + 4);
            Space = new ManoeuvreSpace { MinX = Mathf.Min(start.X, goal.X) - reach, MaxX = Mathf.Max(start.X, goal.X) + reach,
                MinZ = Mathf.Min(start.Z, goal.Z) - reach, MaxZ = Mathf.Max(start.Z, goal.Z) + reach };
            if (departure) { Space.MinX=start.X-96; Space.MaxX=start.X+96; Space.MinZ=start.Z-96; Space.MaxZ=start.Z+96; }
            // Constrain the search to the local street strip, preventing a detour through another lane/block.
            var s = Mathf.Sin(goal.Heading); var c = Mathf.Cos(goal.Heading);
            var side = (start.X - goal.X) * c - (start.Z - goal.Z) * s;
            if (!departure) Space.Allowed = p => { var x = (p.X - goal.X) * c - (p.Z - goal.Z) * s; return x >= Mathf.Min(0, side) - 0.5f && x <= Mathf.Max(0, side) + 0.5f; };
            var centre = new Vector3((Space.MinX + Space.MaxX) / 2, height + 1, (Space.MinZ + Space.MaxZ) / 2);
            var radius = departure ? 138 : ManoeuvrePlan.Distance(start, goal) / 2 + 16;
            // Junctions require the road driver's signals and right-of-way rules. Treat their lanes as local
            // manoeuvre boundaries, including when the parking plan first pulls forward beside a space.
            var graph = GleyGraph.Graph;
            if (graph != null && GleyGraph.Waypoints != null)
                foreach (var w in graph.Near(GleyGraph.ToVec(centre), radius))
                {
                    var p = graph.Position(w);
                    if (!graph.IsIntersection(w) || Mathf.Abs(p.Y - height) > 2) continue;
                    var forward = TrafficManager.Instance.GetWaypointOrientation(GleyGraph.Waypoints[w]) * Vector3.forward;
                    var limit = new Box(p.X, p.Z, Mathf.Atan2(forward.x, forward.z), 1.6f, 2.5f);
                    junctions.Add(limit); Space.Obstacles.Add(limit);
                }
            var n = departure ? Physics.OverlapBoxNonAlloc(centre,new Vector3(96,1.3f,96),hits,Quaternion.identity,vehicles|solids,QueryTriggerInteraction.Ignore)
                : Physics.OverlapSphereNonAlloc(centre, radius, hits, vehicles | solids, QueryTriggerInteraction.Ignore);
            Ready = n < hits.Length;
            for (var i = 0; i < n; i++)
            {
                var collider = hits[i]; var bounds = collider.bounds;
                if (collider.transform.IsChildOf(car.Root.transform) || bounds.max.y < height + 0.3f || bounds.min.y > height + 2.2f || Gates.IsOpenDoor(collider)) continue;
                if (Surface(collider, new Vector3(start.X, height, start.Z)) || Surface(collider, new Vector3(goal.X, height, goal.Z))) continue;
                Bounds local;
                if (collider is BoxCollider box) local = new Bounds(box.center, box.size);
                else if (collider is MeshCollider mesh && mesh.sharedMesh != null) local = mesh.sharedMesh.bounds;
                else { Space.Obstacles.Add(new Box(bounds.center.x, bounds.center.z, 0, bounds.extents.x, bounds.extents.z)); continue; }
                var at = collider.transform.TransformPoint(local.center);
                var scale = collider.transform.lossyScale;
                Space.Obstacles.Add(new Box(at.x, at.z, collider.transform.eulerAngles.y * Mathf.Deg2Rad,
                    local.extents.x * Mathf.Abs(scale.x), local.extents.z * Mathf.Abs(scale.z)));
            }
        }

        private bool Surface(Collider c, Vector3 at) => (solids & (1 << c.gameObject.layer)) != 0
            && c.Raycast(new Ray(at + Vector3.up * 2, Vector3.down), out var hit, 4)
            && hit.normal.y > 0.7f && Mathf.Abs(hit.point.y - height) < 0.3f;

        // Read surface constraints before the search, not only after it has chosen a path across pavement.
        // Legal parking may overlap pedestrian ground; other pedestrian ground remains out of bounds.
        public IEnumerator ReadSurface(CarPose goal)
        {
            if (surfaceGrid != null) { surfaceGrid.Guide(goal,Space.Obstacles); yield break; }
            const float cell = 0.5f;
            var grid = new ManoeuvreGrid(Space.MinX, Space.MinZ,
                Mathf.CeilToInt((Space.MaxX - Space.MinX) / cell), Mathf.CeilToInt((Space.MaxZ - Space.MinZ) / cell), cell);
            surfaceGrid = grid;
            var rendered = new ParkingSurface(grid);
            var read = rendered.Read(height);
            while (read.MoveNext()) yield return read.Current;
            var timer = Stopwatch.StartNew();
            for (var z = 0; z < grid.Height; z++) for (var x = 0; x < grid.Width; x++)
            {
                var point = new Vector3(grid.X(x), height, grid.Z(z));
                var ground = NwhPlanner.GroundBelow(GleyGraph.ToVec(point), car.Root.transform);
                grid.Set(x, z, ground != null && Mathf.Abs(ground.Value - height) <= 0.12f
                    && !rendered.PavementAt(point.x,point.z)
                    && (!Sidewalk.Available || !Sidewalk.At(GleyGraph.ToVec(point)) || ParkingAt(point) || rendered.AisleAt(point.x,point.z)));
                if (timer.Elapsed.TotalMilliseconds > 3) { yield return null; timer.Restart(); }
            }
            grid.Guide(goal, Space.Obstacles);
            // Preserve only the original legal bumper overhang, never an exception along the whole route.
            // Tyres stay on the surface even within that original footprint.
            Space.Allowed = p => SurfaceClear(p,.2f);
            Space.Estimate = grid.Estimate;
        }
        internal bool SurfaceClear(CarPose p, float bodyMargin) => surfaceGrid!.Contains(spec,p,tyresOnly:true)
            && surfaceGrid.Contains(spec,p,permittedOverhang:parkedFootprint,bodyMargin:bodyMargin);

        // Test-only geometry replay. This lives beside rig logs, never in a save or the shipped package.
        public void Dump(CarPose goal, bool parking)
        {
            if (!TestMode.On) return;
            try
            {
                var obstacles = new List<object>();
                foreach (var box in Space.Obstacles) obstacles.Add(new { box.X, box.Z,
                    Heading = Mathf.Atan2(box.Sin, box.Cos), box.HalfWidth, box.HalfLength });
                var dump = new { Start = NwhManoeuvre.Pose(car), Goal = goal, Parking = parking,
                    spec.Wheelbase, spec.BodyCentre, spec.BodyHalf, Lock = spec.LockAt(0), spec.TrackWidth,
                    Space.MinX, Space.MaxX, Space.MinZ, Space.MaxZ, Space.Margin, Obstacles = obstacles,
                    Surface = surfaceGrid == null ? null : new {surfaceGrid.MinX, surfaceGrid.MinZ, surfaceGrid.Cell,
                        BodyMargin=.2f, Overhang=parkedFootprint, Rows = surfaceGrid.Rows()} };
                var folder = System.IO.Path.Combine(GleyGraph.DataFolder, "tests");
                System.IO.Directory.CreateDirectory(folder);
                System.IO.File.AppendAllText(System.IO.Path.Combine(folder, "manoeuvre-worlds.jsonl"),
                    Newtonsoft.Json.JsonConvert.SerializeObject(dump) + "\n");
            }
            catch (Exception ex) { Log.Trace("Manoeuvre replay: " + ex.Message); }
        }

        public bool Clear(CarPose pose, bool live)
        {
            if (surfaceGrid != null && !SurfaceClear(pose,live ? 0 : .2f)) { LastBlock="outside the parking access surface"; return false; }
            var box = ManoeuvrePlan.Body(spec, pose, 0.15f);
            foreach (var junction in junctions)
                if (box.Overlaps(junction)) { LastBlock = "junction requires the road driver"; return false; }
            var centre = new Vector3(box.X, height + 1f, box.Z);
            var half = new Vector3(box.HalfWidth, 0.65f, box.HalfLength);
            var rotation = Quaternion.Euler(0, pose.Heading * Mathf.Rad2Deg, 0);
            var mask = vehicles | solids;
            if (live) mask |= LayerMask.GetMask("AiVehicles", "Human");
            var n = Physics.OverlapBoxNonAlloc(centre, half, hits, rotation, mask, QueryTriggerInteraction.Ignore);
            if (n == hits.Length) { LastBlock = "overlap scan saturated"; return false; }
            for (var i = 0; i < n; i++)
            {
                var hit = hits[i];
                if (!hit.transform.IsChildOf(car.Root.transform) && !Gates.IsOpenDoor(hit) && !Surface(hit, new Vector3(box.X, height, box.Z)))
                { LastBlock = hit.name + " (" + LayerMask.LayerToName(hit.gameObject.layer) + ")"; return false; }
            }
            var right = rotation * Vector3.right; var forward = rotation * Vector3.forward;
            foreach (var z in new[] { 0f, spec.Wheelbase })
                foreach (var x in new[] { -spec.TrackWidth / 2, spec.TrackWidth / 2 })
                {
                    var wheel = new Vector3(pose.X, height, pose.Z) + right * x + forward * z;
                    var ground = NwhPlanner.GroundBelow(GleyGraph.ToVec(wheel), car.Root.transform);
                    if (ground == null || Mathf.Abs(ground.Value - height) > 0.12f)
                    { LastBlock = $"ground at {wheel} ({ground?.ToString("0.00") ?? "missing"}, start {height:0.00})"; return false; }
                    // The pedestrian NavMesh can overlap a legal curb parking strip. The game's parking areas are
                    // authoritative there; elsewhere a wheel on pedestrian ground is a kerb crossing.
                    if (surfaceGrid == null && Sidewalk.Available && Sidewalk.At(GleyGraph.ToVec(wheel)) && !ParkingAt(wheel))
                    { LastBlock = "pavement at " + wheel; return false; }
                }
            return true;
        }
        private bool ParkingAt(Vector3 point)
        {
            var count = Physics.OverlapSphereNonAlloc(point, 0.08f, hits, LayerHelper.parkingAreaLayerMask, QueryTriggerInteraction.Collide);
            if (count == hits.Length) return false;
            for (var i = 0; i < count; i++)
            {
                var lane = hits[i].GetComponentInParent<ParkingLaneGenerator>();
                if (lane != null && !lane.isHandicapParking) return true;
            }
            return false;
        }
    }
}
