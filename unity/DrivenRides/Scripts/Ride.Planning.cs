#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using BigAmbitions.DayNightCycle;
using Buildings;
using DG.Tweening;
using DrivenRides.Core.Drive;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;
using GleyTrafficSystem;
using HarmonyLib;
using Helpers;
using Player.HUD.SmartphoneUI;
using PlayerActivity;
using UI;
using Unity.Collections;
using UnityEngine;
using UnityEngine.AI;
using Vehicles.Taxis;
using Object = UnityEngine.Object;

namespace DrivenRides
{
    internal sealed partial class Ride
    {

        public static IEnumerator? TryCreate(TaxiSystem taxiSystem, EntityController target, float minutes)
        {
            var own = InstanceBehavior<CityManager>.Instance?.cityMap?.Taxi is OwnCarTaxi;
            var ride = TryCreateRide(taxiSystem, target, minutes);
            if (ride == null && own && planNwh != null) planNwh.Ignore = null;
            if (ride != null || !own) return ride;
            if (TestMode.On) Notice.Show("Your car can't drive itself there: " + lastVanillaReason + ".");
            else Notice.Text(lastVanillaReason == NoFuel ? "drivenrides:notice_owncar_no_fuel"
                : lastVanillaReason.StartsWith(OwnCarTaxi.NotInLane, StringComparison.Ordinal) ? "drivenrides:notice_owncar_lane"
                : lastVanillaReason.StartsWith(OwnNoRoute, StringComparison.Ordinal) ? "drivenrides:notice_owncar_no_route"
                : "drivenrides:notice_owncar_cannot");
            return NoRide(taxiSystem);
        }

        private static IEnumerator NoRide(TaxiSystem taxiSystem)
        {
            try { InstanceBehavior<CityManager>.Instance.cityMap.Close(); }
            catch (Exception ex) { Log.Error("No own-car ride, map: " + ex.Message); }
            yield return null;
            TravelField?.SetValue(taxiSystem, null);
        }

        private static IEnumerator? TryCreateRide(TaxiSystem taxiSystem, EntityController target, float minutes)
        {
            lastVanillaReason = "an error (see the log)";
            VehicleComponent? loaded = null;
            spawnedNwh = null;
            PrivateDriverVehicle? parked = null;
            CarController? ownCar = null;
            NwhCar? ownNwh = null;
            var ownSideways = float.MaxValue;
            var unpark = false;
            List<DepartureEntry>? ownEntries = null;
            try
            {
                if (Active != null) return Vanilla("a ride is still active");
                var taxi = InstanceBehavior<CityManager>.Instance.cityMap.Taxi;
                if (!(taxi is MonoBehaviour taxiObject) || taxiObject == null) return Vanilla("no taxi");
                if (PlayerActivityUI.IsPanelOpen) return Vanilla("an activity panel is open");
                if (BuildingManager.IsInsideBuilding) return Vanilla("the player is inside a building");

                Kind kind;
                VehicleComponent? car = null;
                VehicleComponent? prefab;
                switch (taxi)
                {
                    case PrivateDriverVehicle pd:
                        kind = Kind.PrivateDriver;
                        parked = pd.GetVehicleIndex() < 0 ? pd : null;
                        car = parked == null ? pd.GetComponent<VehicleComponent>() : null;
                        prefab = parked != null ? pd.GetVehiclePrefab()
                            : car != null && car.prefab != null ? car.prefab.GetComponent<VehicleComponent>() : null;
                        break;
                    case TaxiController tc:
                        kind = Kind.Taxi;
                        car = tc.GetComponent<VehicleComponent>();
                        prefab = car != null && car.prefab != null ? car.prefab.GetComponent<VehicleComponent>() : null;
                        break;
                    case PermanentTaxiController stand:
                        kind = Kind.Stand;
                        prefab = stand.GetVehiclePrefab();
                        break;
                    case OwnCarTaxi own:
                        kind = Kind.OwnCar;
                        ownCar = own.Car;
                        if (ownCar == null || !ownCar.gameObject.activeInHierarchy) return Vanilla("the car is gone");
                        if (!ReferenceEquals(VehicleHelper.GetCurrentVehicleBase(), ownCar)) return Vanilla("you're not in the car");
                        prefab = OwnCarTaxi.PlanPrefab(ownCar, out var planWhy);
                        if (prefab == null) return Vanilla(planWhy);
                        break;
                    default:
                        return Vanilla("unknown taxi " + taxi.GetType().Name);
                }
                if (kind == Kind.OwnCar ? !Options.OwnCar : kind == Kind.PrivateDriver ? !Options.PrivateDriver : !Options.Taxis) return Vanilla(SwitchedOff);
                if (kind != Kind.Stand && kind != Kind.OwnCar && parked == null && (car == null || car.GetIndex() < 0)) return Vanilla("the taxi has no traffic car");
                if (prefab == null) return Vanilla("no vehicle prefab");
                planLength = LengthOf(prefab);
                planSteering = new Steering(PooledCopy(prefab));
                var driverType = taxi is PrivateDriverVehicle driverTaxi ? driverTaxi.vehicleInstance?.vehicleTypeName : null;
                var driverTop = taxi is PrivateDriverVehicle topTaxi ? topTaxi.vehicleInstance?.VehicleType?.maxSpeed ?? 0 : 0;
                var nwhTop = driverTop > 0 ? driverTop : prefab.maxPossibleSpeed;
                planNwh = null;
                planNwhPlan = null;
                if (ownCar != null)
                {
                    var ownTop = ownCar.vehicleType != null && ownCar.vehicleType.maxSpeed > 0 ? ownCar.vehicleType.maxSpeed : prefab.maxPossibleSpeed;
                    var ownType = OwnCarTaxi.DrivableType(ownCar);
                    planNwh = ownType != null ? NwhPlanner.For(ownType, ownTop) : null;
                    if (planNwh == null) return Vanilla($"this car has no drivable shape to plan with ({ownCar.vehicleType?.vehicleTypeName ?? ownCar.name})");
                    planNwh.BeginRide();
                    planNwh.Ignore = ownCar.transform;
                }
                else if (DrivesWithNwh(kind))
                {
                    planNwh = NwhRides.Planner(driverType, prefab.gameObject, nwhTop);
                    if (planNwh == null) Log.Trace("NWH ride: no drivable car for " + prefab.name + "; a Gley ride.");
                    else { planNwh.BeginRide(); planNwh.Ignore = null; }
                }

                var graph = GleyGraph.Graph;
                var manager = TrafficManager.HasInstance && TrafficManager.IsInitialized ? TrafficManager.Instance : null;
                if (graph == null || manager == null) return Vanilla("traffic is not ready");
                var vehicleType = (int)(car != null ? car.vehicleType : prefab.vehicleType);

                Waypoint? spawn = null;
                int start;
                if (kind == Kind.Stand)
                {
                    var y = taxiObject.transform.position.y;
                    spawn = manager.GetClosestWaypoint(taxiObject.transform.position, 50f, w => IsGoodStart(w, y), prefab);
                    if (spawn == null) return Vanilla("no free lane waypoint by the taxi stand");
                    start = spawn.listIndex;
                }
                else if (parked != null)
                {
                    spawn = SpawnBeside(manager, parked.transform, prefab);
                    if (spawn == null) return Vanilla("no free lane waypoint by the parked driver");
                    start = spawn.listIndex;
                }
                else if (ownCar != null)
                {
                    var ownY = ownCar.transform.position.y;
                    start = OwnCarTaxi.StartUnder(graph, ownCar, -1, i => IsGoodStart(GleyGraph.Waypoints![i], ownY), out ownSideways);
                    var roadsideParallel = start >= 0 && Mathf.Abs(ownSideways) <= 6f
                        && OwnCarParking.IsLegal(ownCar, parallelOnly: true);
                    if (OwnCarParking.IsLegal(ownCar) && !roadsideParallel)
                    {
                        ownEntries = DepartureEntryPicker.Pick(graph, GleyGraph.ToVec(ownCar.transform.position), planNwh!.Car,
                            vehicleType, GleyGraph.IsDisabled, (a, b) => Gates.LockedStep(a, b) || GleyGraph.IsBlockedStep(a, b));
                        if (ownEntries.Count == 0) return Vanilla("no road entry near the parking area");
                        start = ownEntries[0].From;
                        unpark = true;
                        Log.Trace($"Own car: parked departure, {ownEntries.Count} possible road entries.");
                    }
                    else
                    {
                        if (start < 0) return Vanilla(OwnCarTaxi.NotInLane + ": no lane step runs under the car its way");
                        Log.Trace($"Own car: starts on the step from waypoint {start}, {ownSideways:+0.0;-0.0} m to the side of it.");
                    }
                }
                else
                {
                    var next = manager.GetTargetWaypoint(car!.GetIndex());
                    if (next == null) return Vanilla("the taxi has no target waypoint");
                    start = next.listIndex;
                }

                var destination = target.GetNavMeshTargetPosition();
                var destinationPoint = GleyGraph.ToVec(destination);
                var steps = new[]
                {
                    ("the destination's side", new GoalOptions { KerbLane = true, TargetOnRight = true }),
                    ("the other side", new GoalOptions { KerbLane = true }),
                    ("any lane", new GoalOptions { MinStraightMetres = 0f }),
                    ("any nearby lane type", new GoalOptions { MinStraightMetres = 0f, AnyVehicleType = true }),
                    ("within 150 m", new GoalOptions { Radius = 150f, MinStraightMetres = 0f, MaxHeightDiff = 6f, AnyVehicleType = true }),
                    ("within 300 m", new GoalOptions { Radius = 300f, MinStraightMetres = 0f, MaxHeightDiff = 10f, AnyVehicleType = true }),
                };
                long planMs = 0;
                var why = "no goal";
                Route? route = null;
                List<Goal> goals = new List<Goal>();
                var origin = car != null ? car.transform : ownCar != null ? ownCar.transform : null;
                Route? PlanStart(List<Goal> wanted, out long elapsed, out string reason)
                {
                    if (ownEntries == null) return Plan(graph, start, wanted, vehicleType, out elapsed, out reason);
                    var clock = Stopwatch.StartNew(); var tried = new HashSet<int>(); reason = "no route from a parking exit";
                    foreach (var entry in ownEntries)
                    {
                        if (!tried.Add(entry.From)) continue;
                        if (tried.Count > 12) break;
                        var candidate = planNwh!.PlanRoute(graph, entry.From, wanted, vehicleType);
                        if (candidate.Route == null || candidate.Plan == null) continue;
                        if (candidate.Plan.Line(graph).JoinMiss(entry.Position.X, entry.Position.Z, entry.Heading, out _) != null) continue;
                        planNwhPlan = candidate.Plan; elapsed = clock.ElapsedMilliseconds; return candidate.Route;
                    }
                    elapsed = clock.ElapsedMilliseconds; return null;
                }
                if (ownCar != null && planNwh != null && OwnCarTaxi.WantsAutopark(ownCar))
                {
                    goals = ParkingNavigation.Goals(destination, planNwh.Car, vehicleType);
                    if (goals.Count > 0)
                    {
                        route = PlanStart(goals, out var ms, out why);
                        planMs += ms;
                        if (route != null && planNwhPlan == null)
                        {
                            var clock = Stopwatch.StartNew();
                            var checkedRoute = planNwh.PlanRoute(graph, start, goals, vehicleType);
                            planMs += clock.ElapsedMilliseconds;
                            route = checkedRoute.Item1; planNwhPlan = checkedRoute.Item2;
                            if (route == null || planNwhPlan == null) { route = null; planNwhPlan = null; }
                            else Log.Trace($"Goal: parallel parking strips ({goals.Count} approaches, {checkedRoute.Item3}).");
                        }
                    }
                    if (route == null) Log.Trace("Goal: no reachable parallel parking strip; using the ordinary drop-off search.");
                }
                foreach (var (label, options) in steps)
                {
                    if (route != null) break;
                    var batches = options.Radius > 60f
                        ? GoalPicker.PickNearbyFirst(graph, destinationPoint, vehicleType, options)
                        : new[] { GoalPicker.Pick(graph, destinationPoint, vehicleType, options) };
                    foreach (var batch in batches)
                    {
                        goals = batch;
                        if (goals.Count == 0) continue;
                        route = PlanStart(goals, out var ms, out why);
                        planMs += ms;
                        if (route == null && origin != null && ownCar == null)
                            route = PlanFromNearby(graph, origin, start, goals, vehicleType, ref planMs);
                        if (route != null) break;
                    }
                    if (route == null) continue;
                    if (label != "the destination's side") Log.Trace($"Goal: {label} ({goals.Count} goals).");
                    break;
                }
                if (goals.Count == 0) return Vanilla($"no lane waypoint within 300 m of the destination at {destination}");
                if (route != null && origin != null && route.Waypoints[0] != start)
                {
                    Log.Trace($"Start bridged: from waypoint {start} to {route.Waypoints[0]}, {Vector3.Distance(origin.position, GleyGraph.Waypoints![route.Waypoints[0]].position):0} m from the car.");
                }
                if (route != null && planNwh != null && planNwhPlan == null)
                {
                    var nwhClock = Stopwatch.StartNew();
                    var (nwhRoute, nwhPlan, summary) = planNwh.PlanRoute(graph, route.Waypoints[0], goals, vehicleType);
                    planMs += nwhClock.ElapsedMilliseconds;
                    Log.Trace($"NWH ride planned for {planNwh.Car.Name} in {nwhClock.ElapsedMilliseconds} ms: {summary}.");
                    if (nwhRoute == null || nwhPlan == null) return Vanilla("no route this car can drive (" + summary + ")");
                    route = nwhRoute;
                    planNwhPlan = nwhPlan;
                }
                for (var attempt = 0; route != null && planNwh == null && attempt < 6; attempt++)
                {
                    var blocked = planNwh != null ? planNwh.BlockedSteps(graph, route.Waypoints) : FindBlockedSteps(graph, route.Waypoints, planSteering);
                    if (blocked.Count == 0 && planNwh != null && planNwh.Plan(graph, route.Waypoints, out var failed) == null) blocked = failed;
                    if (blocked.Count == 0) break;
                    foreach (var (from, to, what) in blocked)
                    {
                        if (planNwh != null) planNwh.Block(from, to, NwhPlanner.CauseOf(graph, from, to, what));
                        else GleyGraph.BlockStep(from, to);
                        Log.Trace($"Step {from} -> {to} blocked by '{what}'.");
                    }
                    route = Plan(graph, route.Waypoints[0], goals, vehicleType, out var ms, out why);
                    planMs += ms;
                }
                if (route == null && ownCar != null)
                    return Vanilla(ownSideways < float.MaxValue ? $"{OwnNoRoute}, waypoint {start} ({why}, {planMs} ms)" : $"{OwnCarTaxi.NotInLane}: no lane step runs under the car its way, nearest waypoint {start}");
                if (route == null) return Vanilla($"no route ({why}, {planMs} ms)");
                if (ownCar != null && !OwnCarTaxi.HasFuelFor(ownCar, (float)route.LengthMetres)) return Vanilla(NoFuel);
                if (ownCar != null && ownEntries == null && planNwh != null && planNwhPlan != null)
                {
                    var refusal = OwnCarTaxi.StartRefusal(ownCar, planNwh, planNwhPlan, graph, ownSideways);
                    if (refusal != null)
                    {
                        if (!refusal.StartsWith(OwnCarTaxi.NotInLane, StringComparison.Ordinal) || Mathf.Abs(ownSideways) > 6f)
                            return Vanilla(refusal);
                        unpark = true;
                    }
                }

                if (kind == Kind.Stand)
                {
                    if (route.Waypoints.Count < 2) return Vanilla("the stand's route is too short");
                    if (!HasInactivePooledCar(prefab)) return Vanilla("no idle taxi car in the traffic pool");
                    loaded = manager.LoadVehicle(prefab.gameObject, spawn, true);
                    if (loaded == null) return Vanilla("the stand's car could not be loaded");
                    var ahead = GleyGraph.Waypoints![route.Waypoints[1]].position - spawn!.position;
                    if (ahead.sqrMagnitude > 0.01f) loaded.transform.rotation = Quaternion.LookRotation(ahead);
                    AIEvents.TriggerChangeDrivingStateEvent(loaded.GetIndex(), SpecialDriveActionTypes.StopNow, 0f);
                    car = loaded;
                }
                else if (parked != null)
                {
                    if (!HasInactivePooledCar(prefab)) return Vanilla("no idle car of the driver's model in the traffic pool");
                    loaded = manager.LoadVehicle(prefab.gameObject, spawn, true);
                    if (loaded == null) return Vanilla("the driver's traffic car could not be loaded");
                    if (route.Waypoints.Count > 1)
                    {
                        var ahead = GleyGraph.Waypoints![route.Waypoints[1]].position - spawn!.position;
                        if (ahead.sqrMagnitude > 0.01f) loaded.transform.rotation = Quaternion.LookRotation(ahead);
                    }
                    AIEvents.TriggerChangeDrivingStateEvent(loaded.GetIndex(), SpecialDriveActionTypes.StopNow, 0f);
                    taxi = SwapDriver(parked, loaded);
                    car = loaded;
                    loaded = null;
                    Log.Trace($"Driver swapped from a parked car to a traffic car at waypoint {spawn!.listIndex}, {Vector3.Distance(parked.transform.position, spawn.position):0.0} m away.");
                }
                else if (ownCar != null)
                {
                    ownNwh = OwnCarDrive.Begin(ownCar, out var ownWhy);
                    if (ownNwh == null) return Vanilla(ownWhy);
                }

                if (planNwh != null && ownCar == null)
                {
                    var gleyT = car!.transform;
                    TestCar.BodyBox(car, out var gleyCentre, out _);
                    var middle = gleyT.TransformPoint(gleyCentre);
                    var ground = NwhPlanner.GroundBelow(GleyGraph.ToVec(middle), gleyT) ?? gleyT.position.y;
                    var facing = Quaternion.Euler(0f, gleyT.eulerAngles.y, 0f);
                    spawnedNwh = NwhRides.Spawn(driverType, prefab.gameObject, middle, facing, out var nwhWhy);
                    if (spawnedNwh == null) throw new InvalidOperationException("no NWH car: " + nwhWhy);
                    NwhRides.PlaceBody(spawnedNwh, middle, ground, facing);
                    if (spawnedNwh.Look == null) KeepColour(car.gameObject, spawnedNwh.Root, keep: true);
                    Log.Trace($"NWH ride: {NwhCar.Id(spawnedNwh.TypeName)}{(spawnedNwh.Look != null ? " dressed as " + spawnedNwh.Look : "")} where the traffic car {car.name} stands.");
                }
                if (TestMode.On) GleyGraph.DumpRide(start, goals, route.Waypoints, destination);
                var addedTurns = 0;
                for (var k = 0; k + 1 < route.Waypoints.Count; k++)
                {
                    foreach (var t in graph.MissingTurns(route.Waypoints[k])) if (t == route.Waypoints[k + 1]) addedTurns++;
                    if (graph.IsUTurn(route.Waypoints[k], route.Waypoints[k + 1])) addedTurns++;
                }
                if (addedTurns > 0) Log.Trace($"Route uses {addedTurns} turn{(addedTurns == 1 ? "" : "s")} or U-turn{(addedTurns == 1 ? "" : "s")} the game's traffic data doesn't have.");
                var ride = new Ride(taxiSystem, taxi, kind, target, minutes, car ?? prefab, prefab, vehicleType, goals, route, spawn != null)
                {
                    nwh = spawnedNwh != null ? new NwhRideCar { Car = spawnedNwh, Spec = planNwh!.Car } : null,
                    needsUnpark = unpark,
                    departureEntries = ownEntries,
                };
                if (ownNwh != null) ride.nwh = OwnCarTaxi.RideCar(ownCar!, ownNwh, planNwh!.Car, target, prefab, () => ride.cancelled || ride.SaveChanged);
                ownNwh = null;
                loaded = null;
                spawnedNwh = null;
                var address = target is CityBuildingController building && building.building != null
                    ? $"{building.building.StreetNumber} {building.building.StreetName}" : target.name;
                var dropOff = route.Waypoints[route.Waypoints.Count - 1];
                Log.Info($"Ride accepted: {kind}, {route.Waypoints.Count} waypoints, {route.LengthMetres:0} m, drive {route.DriveSeconds:0} s, walk {route.WalkSeconds:0} s, planned in {planMs} ms, budget {minutes:0.0} game minutes. Destination {address} at {destination}, drop-off {dropOff} at {GleyGraph.Waypoints![dropOff].position}.");
                Active = ride;
                return ride.Run();
            }
            catch (Exception ex)
            {
                Log.Error("Ride not started: " + ex);
                if (spawnedNwh?.Root != null) Object.Destroy(spawnedNwh.Root);
                spawnedNwh = null;
                if (ownNwh != null) OwnCarDrive.End("the ride didn't start");
                if (loaded != null)
                {
                    try { TrafficManager.Instance.RemoveVehicle(loaded.GetIndex()); }
                    catch (Exception removeEx) { Log.Error("Loaded car not removed: " + removeEx.Message); }
                }
                return null;
            }
        }

        private static IEnumerator? Vanilla(string reason)
        {
            Log.Info("Vanilla ride: " + reason + ".");
            lastVanillaReason = reason;
            var own = InstanceBehavior<CityManager>.Instance?.cityMap?.Taxi is OwnCarTaxi;
            if (reason != SwitchedOff && !own)
            {
                if (TestMode.On) Notice.Show("Driven Rides: vanilla ride, " + reason + ".");
                else Notice.Text("drivenrides:notice_vanilla_ride");
            }
            return null;
        }

        private static bool DrivesWithNwh(Kind kind) => Options.CarPhysics && (kind == Kind.Taxi || kind == Kind.Stand || kind == Kind.PrivateDriver);

        private static VehicleComponent PooledCopy(VehicleComponent prefab)
        {
            if (TrafficVehiclesField?.GetValue(TrafficManager.Instance) is TrafficVehicles vehicles)
                foreach (var v in vehicles.GetVehicleList())
                    if (v != null && v.prefab == prefab.gameObject && v.length > 0f) return v;
            return prefab;
        }

        private static float LengthOf(VehicleComponent prefab) => PooledCopy(prefab).length;



        private static Route? Plan(LaneGraph graph, int start, List<Goal> goals, int vehicleType, out long ms, out string why, long budgetMs = PlanBudgetMs)
        {
            var watch = Stopwatch.StartNew();
            var job = RoutePlanner.Begin(graph, new RouteRequest
            {
                Start = start,
                Goals = goals,
                VehicleType = vehicleType,
                IsDisabled = GleyGraph.IsDisabled,
                IsBlockedStep = (a, b) => GleyGraph.IsBlockedStep(a, b) || (planNwh != null ? planNwh.IsBlocked(a, b) : TestedSteps.Blocks(a, b, planLength)),
                WideUTurns = planNwh != null,
            });
            while (job.Status == PlanStatus.Running && watch.ElapsedMilliseconds < budgetMs)
                job.Step(PlanStep, () => watch.ElapsedMilliseconds >= budgetMs);
            ms = watch.ElapsedMilliseconds;
            why = job.Status == PlanStatus.Running ? "over the time budget" : job.Status.ToString();
            if (job.Status == PlanStatus.Found && ms > budgetMs) { why = "found after the time budget"; return null; }
            return job.Status == PlanStatus.Found ? job.Result : null;
        }

        private static Route? PlanFromNearby(LaneGraph graph, Transform car, int pocketStart, List<Goal> goals, int vehicleType, ref long planMs)
        {
            var here = car.position;
            var candidates = new List<(int Index, float Score)>();
            foreach (var i in graph.Near(GleyGraph.ToVec(here), 50f))
            {
                if (i == pocketStart || graph.IsIntersection(i) || !graph.Allows(i, vehicleType) || graph.Successors(i).Count == 0) continue;
                var at = GleyGraph.Waypoints![i].position;
                if (Mathf.Abs(at.y - here.y) > 3f) continue;
                var to = at - here;
                if (Physics.Raycast(here + Vector3.up, to, to.magnitude, LayerHelper.buildingsLayerMask, QueryTriggerInteraction.Ignore)) continue;
                var ahead = Vector3.Dot(to.normalized, car.forward) > 0f;
                candidates.Add((i, to.magnitude + (ahead ? 0f : 25f)));
            }
            candidates.Sort((a, b) => a.Score.CompareTo(b.Score));
            var watch = Stopwatch.StartNew();
            for (var k = 0; k < candidates.Count && watch.ElapsedMilliseconds < PlanBudgetMs; k++)
            {
                var route = Plan(graph, candidates[k].Index, goals, vehicleType, out var ms, out _, PlanBudgetMs - watch.ElapsedMilliseconds);
                planMs += ms;
                if (route != null) return route;
            }
            return null;
        }

        private static bool TurnClear(LaneGraph graph, int entry, int exit, int aim, Steering car, out string what, float speedKmh = TurnSpeed)
        {
            what = "";
            if (car.Shape.WheelDistance <= 0f || !graph.PlainHeading(entry, out var dx, out var dz)) return true;
            var targets = new List<Vec3>();
            foreach (var via in graph.TurnVias(entry, exit)) targets.Add(graph.Position(via));
            targets.Add(graph.Position(aim));
            targets.AddRange(TurnDrive.LaneAfter(graph, aim, 20f));
            return BodyClear(TurnDrive.Drive(graph.Position(entry), dx, dz, targets, car.Shape, speedKmh / 3.6f, car.SteeringFactor), car, out what);
        }

        private static bool BodyClear(List<Core.Ride.Pose>? poses, Steering car, out string what)
        {
            what = "";
            if (poses == null) { what = "a turn the car can't make"; return false; }
            var half = car.BodyHalf + new Vector3(TurnMargin, 0f, TurnMargin);
            var bottom = Mathf.Max(car.BodyCentre.y - car.BodyHalf.y, RoadClearance);
            var top = car.BodyCentre.y + car.BodyHalf.y;
            var roadHalf = new Vector3(half.x, Mathf.Max(0.05f, (top - bottom) / 2f), half.z);
            var roadCentre = new Vector3(car.BodyCentre.x, (top + bottom) / 2f, car.BodyCentre.z);
            foreach (var pose in poses)
            {
                var rotation = Quaternion.Euler(0f, pose.Heading * Mathf.Rad2Deg, 0f);
                var at = new Vector3(pose.X, pose.Y, pose.Z);
                if (Physics.OverlapBoxNonAlloc(at + rotation * car.BodyCentre, half, turnHits, rotation, ObstacleMask, QueryTriggerInteraction.Ignore) > 0
                    || Physics.OverlapBoxNonAlloc(at + rotation * roadCentre, roadHalf, turnHits, rotation, RoadsMask, QueryTriggerInteraction.Ignore) > 0)
                {
                    what = turnHits[0].name;
                    return false;
                }
            }
            return true;
        }

        private static float ArcLookahead(Steering car) => car.Shape.FrontAxle + ArcLead;

        private static Polyline? ClearArc(LaneGraph graph, int entry, int exit, Steering car)
        {
            if (car.Shape.WheelDistance <= 0f) return null;
            var line = TurnArc.Fit(graph, entry, exit, car.MinRadius);
            if (line == null) return null;
            var poses = TurnDrive.Pursue(line, car.Shape, TurnSpeed / 3.6f, car.SteeringFactor, ArcLookahead(car));
            if (poses == null) return null;
            var turning = new List<Core.Ride.Pose>();
            var along = 0f;
            foreach (var p in poses)
            {
                along = line.Project(p.X + car.Shape.FrontAxle * Mathf.Sin(p.Heading), p.Z + car.Shape.FrontAxle * Mathf.Cos(p.Heading), along);
                if (along >= TurnArc.LeadIn) turning.Add(p);
            }
            return BodyClear(turning, car, out _) ? line : null;
        }

        private static bool ArcClear(int entry, int exit, Steering car)
        {
            var graph = GleyGraph.Graph;
            return graph != null && ClearArc(graph, entry, exit, car) != null;
        }

        private static bool TurnClear(int entry, int exit, int aim, Steering car)
        {
            var graph = GleyGraph.Graph;
            return graph == null || TurnClear(graph, entry, exit, aim, car, out _);
        }

        private static List<(int From, int To, string What)> FindBlockedSteps(LaneGraph graph, IReadOnlyList<int> w, Steering car)
        {
            var halfWidth = car.HalfWidth;
            var expanded = PathExpander.Expand(graph, w, (e, x, a) => TurnClear(e, x, a, car), (e, x) => ArcClear(e, x, car));
            var aims = expanded.Aims;
            var result = new List<(int, int, string)>();
            var all = GleyGraph.Waypoints!;
            var points = new List<Vector3>();
            for (var k = 0; k + 1 < w.Count; k++)
            {
                if (IndexOf(graph.Successors(w[k]), w[k + 1]) >= 0 || expanded.Arcs.Contains((w[k], w[k + 1]))) continue;
                points.Clear();
                points.Add(all[w[k]].position);
                foreach (var via in graph.TurnVias(w[k], w[k + 1])) points.Add(all[via].position);
                if (graph.IsUTurn(w[k], w[k + 1]))
                {
                    if (!TurnClear(graph, w[k], w[k + 1], w[k + 1], car, out var uWhat, car.Length >= LongCarMetres ? UTurnSpeed : TurnSpeed))
                        result.Add((w[k], w[k + 1], uWhat));
                    continue;
                }
                if (aims.TryGetValue((w[k], w[k + 1]), out var aim))
                {
                    if (!TurnClear(graph, w[k], w[k + 1], aim.Aim, car, out var what)) result.Add((w[k], w[k + 1], what));
                    continue;
                }
                points.Add(all[w[k + 1]].position);
                var turn = IndexOf(graph.MissingTurns(w[k]), w[k + 1]) >= 0;
                var radius = turn ? 1.5f : Mathf.Max(0.8f, halfWidth + 0.1f);
                if (turn && points.Count == 2) points.Add(all[w[k]].position);
                for (var q = 0; q + 1 < points.Count; q++)
                {
                    var hits = Physics.OverlapCapsule(points[q] + Vector3.up * 1f, points[q + 1] + Vector3.up * 1f, radius, ObstacleMask, QueryTriggerInteraction.Ignore);
                    if (hits.Length == 0) continue;
                    result.Add((w[k], w[k + 1], hits[0].name));
                    break;
                }
            }
            return result;
        }

        private static bool IsGoodStart(Waypoint w, float y) =>
            w.neighbors != null && w.neighbors.Count > 0 && TrafficManager.WithinHeight(w, y, 3f) && TrafficManager.IsNotIntersection(w);

        // A copy of TaxiSystem.IsValidWaypointForLeaving, which is private.
        private static bool IsValidForLeaving(Waypoint w, float y) =>
            w.allowedCars != null && w.allowedCars.Count > 0 && TrafficManager.WithinHeight(w, y, 5f) && TrafficManager.IsNotIntersection(w);

        private static bool HasInactivePooledCar(VehicleComponent prefab)
        {
            if (!(TrafficVehiclesField?.GetValue(TrafficManager.Instance) is TrafficVehicles vehicles)) return false;
            var asset = prefab.gameObject;
            foreach (var v in vehicles.GetVehicleList())
                if (v != null && v.prefab == asset && !v.gameObject.activeInHierarchy) return true;
            return false;
        }

        // The lane waypoint for a parked driver's traffic car: free, near the parked car, facing its way if one does.
        // The parked car's own colliders are off during the search, or it would block the lane it stands on (a
        // Hamptons driver parks on a waypoint by the house).
        private static Waypoint? SpawnBeside(TrafficManager manager, Transform parkedCar, VehicleComponent prefab)
        {
            var y = parkedCar.position.y;
            var forward = parkedCar.forward;
            return WithCollidersOff(parkedCar, () =>
                manager.GetClosestWaypoint(parkedCar.position, 30f, w => IsGoodStart(w, y) && Vector3.Dot(manager.GetWaypointOrientation(w) * Vector3.forward, forward) > 0.5f, prefab)
                ?? manager.GetClosestWaypoint(parkedCar.position, 30f, w => IsGoodStart(w, y), prefab));
        }

        // Gley's free check sees every collider; a car's own must not make its lane look taken.
        private static T WithCollidersOff<T>(Transform car, Func<T> check)
        {
            var colliders = car.GetComponentsInChildren<Collider>();
            var wasOn = new bool[colliders.Length];
            for (var k = 0; k < colliders.Length; k++) { wasOn[k] = colliders[k].enabled; colliders[k].enabled = false; }
            try { return check(); }
            finally
            {
                for (var k = 0; k < colliders.Length; k++) if (colliders[k] != null) colliders[k].enabled = wasOn[k];
            }
        }

        // Vanilla's InstantiateVehicle, without its dismissal (which would toggle the open map): the loaded car becomes
        // the phone's private driver, and the parked car goes back to the parking pool.
        private static PrivateDriverVehicle SwapDriver(PrivateDriverVehicle parked, VehicleComponent loaded)
        {
            if (loaded.TryGetComponent<PrivateDriverVehicle>(out var stale)) Object.DestroyImmediate(stale);
            var driver = loaded.gameObject.AddComponent<PrivateDriverVehicle>();
            PrivateDriverHelpers.SetupVehicle(driver, parked.vehicleInstance);
            var phone = InstanceBehavior<UIs>.Instance.smartphoneUI.privateDriverUI;
            if (PhoneDriverField != null && ReferenceEquals(PhoneDriverField.GetValue(phone), parked)) PhoneDriverField.SetValue(phone, driver);
            ParkingSimulator.ReleaseParkedVehicle(parked.gameObject);
            return driver;
        }
    }
}
