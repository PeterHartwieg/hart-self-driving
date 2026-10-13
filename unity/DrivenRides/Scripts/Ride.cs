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
    // One driven ride, from the moment the TravelCoroutine prefix accepts it until it completes, finishes the
    // vanilla way, or is cancelled. Plan: docs/implementation-plan.md, "How the mod hooks in" and "The ride".
    internal sealed class Ride
    {
        private enum Kind { Taxi, Stand, PrivateDriver, OwnCar, Test }
        private enum Phase { Boarding, Driving, Bridging, Stopping, Finishing, Done }
        private enum Outcome { None, Complete, Vanilla }

        private const long PlanBudgetMs = 100;
        private const int PlanStep = 500;
        private const float MapWaitSeconds = 3f;
        private const float StopWaitSeconds = 5f;
        private const double ArriveMetres = 12;
        private const int ReplanStep = 1500;
        private const float ReplanSecondsLimit = 2f;

        private static readonly FieldInfo? TravelField = AccessTools.Field(typeof(TaxiSystem), "_travelCoroutine");
        private static readonly FieldInfo? TrafficVehiclesField = AccessTools.Field(typeof(TrafficManager), "trafficVehicles");
        private static readonly FieldInfo? TimeScaleTweenField = AccessTools.Field(typeof(GameSpeedController), "timeScaleTween");

        public static Ride? Active { get; private set; }

        // Captured when the prefix accepts the ride; nothing needed for finishing lives only on a pooled component.
        private readonly TaxiSystem? taxiSystem;
        private readonly ITaxi? taxi;
        private readonly Kind kind;
        private readonly EntityController? target;
        private readonly GameInstance save;
        private readonly PlayerController player;
        private readonly GameManager manager;
        private readonly string happinessModifier;
        private readonly string? driverVehicleType;
        private readonly int driverTopSpeed;
        private readonly VehicleComponent prefab;
        private readonly int vehicleType;
        private readonly List<Goal> goals;
        private readonly double startMinute;
        private readonly float timeMultiplier;
        private float rideSpeed;
        private readonly List<int> owned = new List<int>();
        private readonly VehicleComponent car;
        private readonly int carIndex;
        // A car the mod loaded onto the route's first waypoint (a stand's, or a parked driver's replacement).
        private readonly bool onFirstWaypoint;
        // An own-car ride: the player's car, hidden where it stands, and the stand-in's dress (docs/own-car-self-drive.md).
        private CarController? ownCar;
        private StandIn? standIn;
        private bool standInRemoved;
        private bool ownCarEntered;
        // The car stands somewhere new (the end of the trip), so a cancel leaves the player beside it there.
        private bool ownCarPlaced;
        // Fuel is charged for the trip as first planned; a replan keeps only the rest of the way in the route.
        private readonly double plannedMetres;

        // Vanilla's exit warps the player a frame later and, with no room, calls EnterVehicle (CarController.cs:640-650).
        // A ride that has given up must not be re-entered by that call, so EnterVehicle on the car is refused for a few
        // frames after the exit, whatever happens to the ride (the EnterVehicle prefix).
        private static CarController? exitingCar;
        private static int exitBlockUntilFrame;
        private static bool exitReEnterRefused;

        public static bool RefusesEnter(CarController car)
        {
            if (exitingCar == null || !ReferenceEquals(car, exitingCar) || Time.frameCount > exitBlockUntilFrame) return false;
            exitReEnterRefused = true;
            Log.Info("Own car: the game tried to put the player back in after the exit; refused.");
            return true;
        }

        private Route route;
        // The route as Gley drives it: the planner's waypoints with each added turn's curve waypoint put in.
        private List<int> path;
        // Path positions of added turns (entry to exit, U-turns not included), driven at TurnSpeed.
        private readonly HashSet<int> slowPath = new HashSet<int>();
        // Path positions of U-turns, driven at UTurnSpeed in a long car.
        private readonly HashSet<int> uTurnPath = new HashSet<int>();
        // The added turns driven along a driver's arc, in route order; the next one, and how far along it the front
        // axle is once the ride steers it.
        private readonly List<(int Entry, int Exit, Polyline Line)> arcs = new List<(int, int, Polyline)>();
        private int arcAt;
        private float arcAlong = -1f;
        private bool arcHeld;

        private float appliedTop = -1f;
        private const float TurnSpeed = 25f;
        // A long car takes a U-turn slower: at 25 km/h a 6.7 m car went wide into the far kerb (turn tests); shorter
        // cars made the same U-turns at 25 (Peter: only for big cars).
        private const float UTurnSpeed = 15f;
        private const float LongCarMetres = 5.5f;
        private RouteProgress? progress;
        private readonly RideTrace trace = new RideTrace();
        private CollisionProbe? probe;
        private readonly List<AudioSource> muted = new List<AudioSource>();
        // Where each waypoint sits in the current route, for the HasReachedOrPassedWaypoint prefix.
        private readonly Dictionary<int, int> routeIndex = new Dictionary<int, int>();
        private RideWatch? watch;
        private double alongOffset;
        private double lastAlong;
        private double unpausedSeconds;
        private float stopWait;
        private Vector3 pickupPosition;
        private Quaternion pickupRotation;
        private GameObject? anchor;
        private RideAnchor? anchorComponent;
        private bool attached;
        // The player has been attached once (and so hidden); a cancel after the detach shows them again.
        private bool boarded;
        private bool departed;
        private bool cameraSet;
        private bool scaleSet;
        private bool mapClosed;
        private bool ownTimeMachine;
        private bool timeMachineEnded;
        private bool cancelled;
        private bool finished;
        private bool throwNext;
        private bool boardFailed;
        private bool faded;
        private bool farDrop;
        // The door only for a destination far from where the car can stop; otherwise the player gets out by the car and
        // walks (Peter: a 100 m teleport at a Hamptons house felt wrong).
        private const float FarDropMetres = 200f;
        private PlanJob? replanJob;
        private float replanSeconds;
        private bool fakeSaveChange;
        private Phase phase = Phase.Boarding;
        private Outcome outcome = Outcome.None;
        private string outcomeReason = "";

        public bool ShowPanel => phase == Phase.Driving || phase == Phase.Stopping;
        public string PanelText { get; private set; } = "";

        // The car Gley must leave alone: PrivateDriverVehicle.Update is skipped for it.
        public static bool OwnsCarObject(GameObject car) => Active != null && !Active.finished && Active.OwnsCar && Active.car.gameObject == car;
        public static bool OwnsPlayer => Active != null && !Active.finished && Active.attached;
        public static bool IsDriving => Active != null && !Active.finished && !Active.cancelled && Active.phase == Phase.Driving;
        public static Vector3 PickupPosition => Active!.pickupPosition;
        public static Quaternion PickupRotation => Active!.pickupRotation;

        private Ride(TaxiSystem? taxiSystem, ITaxi? taxi, Kind kind, EntityController? target, float minutes, VehicleComponent car,
            VehicleComponent prefab, int vehicleType, List<Goal> goals, Route route, bool onFirstWaypoint)
        {
            this.onFirstWaypoint = onFirstWaypoint;
            this.taxiSystem = taxiSystem;
            this.taxi = taxi;
            this.kind = kind;
            this.target = target;
            this.car = car;
            carIndex = car.GetIndex();
            this.prefab = prefab;
            this.vehicleType = vehicleType;
            this.goals = goals;
            this.route = route;
            plannedMetres = route.LengthMetres;
            carLength = LengthOf(prefab);
            carSteering = new Steering(PooledCopy(prefab));
            nwhPlanner = kind == Kind.Test ? null : planNwh;
            nwhPlanner?.BeginRide();
            path = Expand(route);
            save = SaveGameManager.Current;
            manager = InstanceBehavior<GameManager>.Instance;
            player = manager.playerController;
            happinessModifier = taxi?.GetHappinessModifierName() ?? "";
            driverVehicleType = taxi is PrivateDriverVehicle pd ? pd.vehicleInstance?.vehicleTypeName : null;
            driverTopSpeed = taxi is PrivateDriverVehicle driver ? driver.vehicleInstance?.VehicleType?.maxSpeed ?? 0 : 0;
            startMinute = NowMinutes();
            timeMultiplier = taxi?.GetTimeMultiplier() ?? 1f;
            rideSpeed = Options.RideSpeed;
        }

        // Test rides (docs/turn-tests.md): the real driving code on a car the test session loaded, with no passenger,
        // camera, clock or bookkeeping. The session's anchor follows the car; the session owns the player.
        public static Ride StartTest(VehicleComponent car, VehicleComponent prefab, Route route, List<int>? path, IEnumerable<int>? slow,
            int topSpeedKmh, float speed, RideAnchor anchor, string traceFile)
        {
            if (Active != null) throw new InvalidOperationException("a ride is active");
            var ride = new Ride(null, null, Kind.Test, null, 0f, car, prefab, (int)car.vehicleType, new List<Goal>(), route, true)
            {
                testTopSpeed = topSpeedKmh,
                TraceFile = traceFile,
            };
            ride.rideSpeed = speed;
            if (path != null)
            {
                ride.path = path;
                ride.arcs.Clear();
                ride.slowPath.Clear();
                ride.uTurnPath.Clear();
                if (slow != null) ride.slowPath.UnionWith(slow);
            }
            ride.anchorComponent = anchor;
            anchor.Target = car.transform;
            anchor.Valid = () => ride.OwnsCar;
            ride.mapClosed = true;
            Active = ride;
            ride.Depart();
            return ride;
        }

        private int testTopSpeed;
        public string? TraceFile { get; private set; }
        public bool Ended => outcome != Outcome.None || finished;
        public string EndReason => outcomeReason;
        public bool Arrived => outcome == Outcome.Complete;
        public bool WaitingNow => departed && OwnsCar && IsWaiting();
        public IReadOnlyList<int> DrivenPath => path;

        // Ends a test ride: the car goes, the anchor stays where it is, nothing touches the player.
        public void EndTest()
        {
            if (finished) return;
            finished = true;
            phase = Phase.Done;
            try { ApplyScale(false); } catch (Exception ex) { Log.Error("Test end, time scale: " + ex.Message); }
            try { if (OwnsCar) TrafficManager.Instance.RemoveVehicle(carIndex); } catch (Exception ex) { Log.Error("Test end, car: " + ex.Message); }
            if (anchorComponent != null) { anchorComponent.Target = null; anchorComponent.Valid = null; }
            anchorComponent = null;
            DropState();
        }

        // The TravelCoroutine prefix. Returns the ride's iterator, or null for a vanilla ride. Vanilla's TravelTo has
        // already checked and charged; until a stand's car is loaded nothing here changes the game, and the ride's
        // static state is committed only as the last step.
        public static IEnumerator? TryCreate(TaxiSystem taxiSystem, EntityController target, float minutes)
        {
            var own = InstanceBehavior<CityManager>.Instance?.cityMap?.Taxi is OwnCarTaxi;
            var ride = TryCreateRide(taxiSystem, target, minutes);
            if (ride != null || !own) return ride;
            // Vanilla's ride would take the player to the door and leave the car behind; instead nothing happens.
            Notice.Show("Your car can't drive itself there.");
            return NoRide(taxiSystem);
        }

        private static IEnumerator NoRide(TaxiSystem taxiSystem)
        {
            try { InstanceBehavior<CityManager>.Instance.cityMap.Close(); }
            catch (Exception ex) { Log.Error("No own-car ride, map: " + ex.Message); }
            // TravelTo stores this iterator after its first step; the field is cleared once it is stored.
            yield return null;
            TravelField?.SetValue(taxiSystem, null);
        }

        private static IEnumerator? TryCreateRide(TaxiSystem taxiSystem, EntityController target, float minutes)
        {
            VehicleComponent? loaded = null;
            PrivateDriverVehicle? parked = null;
            CarController? ownCar = null;
            StandIn? dressed = null;
            var dressStandIn = false;
            try
            {
                if (Active != null) return Vanilla("a ride is still active");
                var taxi = InstanceBehavior<CityManager>.Instance.cityMap.Taxi;
                if (!(taxi is MonoBehaviour taxiObject) || taxiObject == null) return Vanilla("no taxi");
                if (PlayerActivityUI.IsPanelOpen) return Vanilla("an activity panel is open");
                if (BuildingManager.IsInsideBuilding) return Vanilla("the player is inside a building");
                // The Hamptons are driven too: the lane graph runs across the bridge (Peter, after the first rides).
                // Vanilla's TravelTo has already checked the private fence.

                Kind kind;
                VehicleComponent? car = null;
                VehicleComponent? prefab;
                switch (taxi)
                {
                    case PrivateDriverVehicle pd:
                        kind = Kind.PrivateDriver;
                        // Called from indoors, the driver waits as a parked car (PrivateDriverHelpers
                        // .SummonPrivateDriverVehicleFromIndoors), not a traffic car; it is swapped for one on the lane
                        // beside it once the route is known.
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
                    // Vanilla's exit warps to this point and, if it can't, puts the player back in (CarController.cs:640-650).
                    if (!NavMesh.SamplePosition(ownCar.GetNavMeshTargetPosition(), out _, 1f, NavMesh.AllAreas)) return Vanilla("there's no room to get out of the car");
                        var pick = StandIn.Pick(ownCar, out var standInWhy);
                        if (pick == null) return Vanilla(standInWhy);
                        Log.Info("Own car: the stand-in is " + standInWhy + ".");
                        prefab = pick.Value.Prefab;
                        dressStandIn = pick.Value.Dress;
                        break;
                    default:
                        return Vanilla("unknown taxi " + taxi.GetType().Name);
                }
                if (kind == Kind.OwnCar ? !Options.OwnCar : kind == Kind.PrivateDriver ? !Options.PrivateDriver : !Options.Taxis) return Vanilla(SwitchedOff);
                if (kind != Kind.Stand && kind != Kind.OwnCar && parked == null && (car == null || car.GetIndex() < 0)) return Vanilla("the taxi has no traffic car");
                if (prefab == null) return Vanilla("no vehicle prefab");
                planLength = LengthOf(prefab);
                planSteering = new Steering(PooledCopy(prefab));
                planNwh = PlanForNwh ? NwhPlanner.ForTrafficPrefab(prefab.gameObject, 0f) : null;

                var graph = GleyGraph.Graph;
                var manager = TrafficManager.HasInstance && TrafficManager.IsInitialized ? TrafficManager.Instance : null;
                if (graph == null || manager == null) return Vanilla("traffic is not ready");
                var vehicleType = (int)(car != null ? car.vehicleType : prefab.vehicleType);

                Waypoint? spawn = null;
                int start;
                if (kind == Kind.Stand)
                {
                    var y = taxiObject.transform.position.y;
                    // checkFreeFor makes Gley test that the spawn waypoint is free (DensityManager.cs:244-256).
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
                    spawn = SpawnBeside(manager, ownCar.transform, prefab);
                    if (spawn == null) return Vanilla("no free lane beside the car");
                    start = spawn.listIndex;
                }
                else
                {
                    var next = manager.GetTargetWaypoint(car!.GetIndex());
                    if (next == null) return Vanilla("the taxi has no target waypoint");
                    start = next.listIndex;
                }

                var destination = target.GetNavMeshTargetPosition();
                // Goals in steps, each wider than the last, until one gives a route: the kerb on the destination's side
                // of the street (a driver lets you out on your side), any kerb, any lane, then lanes of any type
                // further out (Hamptons houses stand back on private roads; spike 15 found nothing within 60 m).
                var destinationPoint = GleyGraph.ToVec(destination);
                var steps = new[]
                {
                    ("the destination's side", new GoalOptions { KerbLane = true, TargetOnRight = true }),
                    ("the other side", new GoalOptions { KerbLane = true }),
                    ("any lane", new GoalOptions { MinStraightMetres = 0f }),
                    ("within 150 m", new GoalOptions { Radius = 150f, MinStraightMetres = 0f, MaxHeightDiff = 6f, AnyVehicleType = true }),
                    ("within 300 m", new GoalOptions { Radius = 300f, MinStraightMetres = 0f, MaxHeightDiff = 10f, AnyVehicleType = true }),
                };
                long planMs = 0;
                var why = "no goal";
                Route? route = null;
                List<Goal> goals = new List<Goal>();
                foreach (var (label, options) in steps)
                {
                    goals = GoalPicker.Pick(graph, destinationPoint, vehicleType, options);
                    if (goals.Count == 0) continue;
                    route = Plan(graph, start, goals, vehicleType, out var ms, out why);
                    planMs += ms;
                    if (route != null)
                    {
                        if (label != "the destination's side") Log.Info($"Goal: {label} ({goals.Count} goals).");
                        break;
                    }
                }
                if (goals.Count == 0) return Vanilla($"no lane waypoint within 300 m of the destination at {destination}");
                var origin = car != null ? car.transform : ownCar != null ? ownCar.transform : null;
                if (route == null && origin != null)
                {
                    // The summoned driver stops at the waypoint nearest the player, whatever kind of lane it is; some are
                    // pockets with no lane out. Then the route starts from a proper traffic lane nearby, and Gley steers
                    // the car straight to it.
                    Log.Info("No route from the car's waypoint: " + GleyGraph.Diagnose(graph, start, goals, vehicleType));
                    route = PlanFromNearby(graph, origin, start, goals, vehicleType, ref planMs);
                    if (route != null) Log.Info($"Start bridged: from waypoint {start} to {route.Waypoints[0]}, {Vector3.Distance(origin.position, GleyGraph.Waypoints![route.Waypoints[0]].position):0} m from the car.");
                    // A stand-in starts on its spawn waypoint and skips it, so it must be loaded where the route now
                    // starts, if that place is free.
                    if (route != null && ownCar != null)
                    {
                        var first = route.Waypoints[0];
                        // The car's own colliders off for the free check, as in SpawnBeside: it may stand in that lane.
                        spawn = WithCollidersOff(ownCar.transform, () => manager.GetClosestWaypoint(GleyGraph.Waypoints![first].position, 1f, w => w.listIndex == first, prefab));
                        if (spawn == null) return Vanilla("the nearby lane the route starts on isn't free");
                        start = first;
                    }
                }
                // Steps the game's data doesn't have are checked against the world; a blocked one is remembered and the
                // route planned again (spike 17: an added right turn ran into concrete barricades in a junction).
                for (var attempt = 0; route != null && attempt < 6; attempt++)
                {
                    var blocked = planNwh != null ? planNwh.BlockedSteps(graph, route.Waypoints) : FindBlockedSteps(graph, route.Waypoints, planSteering);
                    if (blocked.Count == 0) break;
                    foreach (var (from, to, what) in blocked)
                    {
                        if (planNwh != null) planNwh.Block(from, to, NwhPlanner.CauseOf(graph, from, to));
                        else GleyGraph.BlockStep(from, to);
                        Log.Info($"Step {from} -> {to} blocked by '{what}'.");
                    }
                    route = Plan(graph, route.Waypoints[0], goals, vehicleType, out var ms, out why);
                    planMs += ms;
                }
                if (route == null) return Vanilla($"no route ({why}, {planMs} ms)");
                if (ownCar != null && !OwnCarTaxi.HasFuelFor(ownCar, (float)route.LengthMetres)) return Vanilla($"not enough fuel for {route.LengthMetres / 1000.0:0.0} km");

                if (kind == Kind.Stand)
                {
                    if (route.Waypoints.Count < 2) return Vanilla("the stand's route is too short");
                    if (!HasInactivePooledCar(prefab)) return Vanilla("no idle taxi car in the traffic pool");
                    loaded = manager.LoadVehicle(prefab.gameObject, spawn, true);
                    if (loaded == null) return Vanilla("the stand's car could not be loaded");
                    var ahead = GleyGraph.Waypoints![route.Waypoints[1]].position - spawn!.position;
                    if (ahead.sqrMagnitude > 0.01f) loaded.transform.rotation = Quaternion.LookRotation(ahead);
                    // Hold it until Depart, as vanilla's summon does for a car without a path.
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
                    Log.Info($"Driver swapped from a parked car to a traffic car at waypoint {spawn!.listIndex}, {Vector3.Distance(parked.transform.position, spawn.position):0.0} m away.");
                }
                else if (ownCar != null)
                {
                    if (!HasInactivePooledCar(prefab)) return Vanilla("no idle stand-in car in the traffic pool");
                    loaded = manager.LoadVehicle(prefab.gameObject, spawn, true);
                    if (loaded == null) return Vanilla("the stand-in car could not be loaded");
                    if (route.Waypoints.Count > 1)
                    {
                        var ahead = GleyGraph.Waypoints![route.Waypoints[1]].position - spawn!.position;
                        if (ahead.sqrMagnitude > 0.01f) loaded.transform.rotation = Quaternion.LookRotation(ahead);
                    }
                    AIEvents.TriggerChangeDrivingStateEvent(loaded.GetIndex(), SpecialDriveActionTypes.StopNow, 0f);
                    // Dressed while the player still sits in the car (the driver and the player's own figure are left
                    // out), then the car is hidden; the map covers both.
                    if (dressStandIn) dressed = StandIn.Dress(loaded, ownCar);
                    OwnCarTaxi.Hide(ownCar);
                    car = loaded;
                    Log.Info($"Own car: stand-in loaded at waypoint {spawn!.listIndex}, {Vector3.Distance(ownCar.transform.position, spawn.position):0.0} m from the car.");
                }

                GleyGraph.DumpRide(start, goals, route.Waypoints, destination);
                var addedTurns = 0;
                for (var k = 0; k + 1 < route.Waypoints.Count; k++)
                {
                    foreach (var t in graph.MissingTurns(route.Waypoints[k])) if (t == route.Waypoints[k + 1]) addedTurns++;
                    if (graph.IsUTurn(route.Waypoints[k], route.Waypoints[k + 1])) addedTurns++;
                }
                if (addedTurns > 0) Log.Info($"Route uses {addedTurns} turn{(addedTurns == 1 ? "" : "s")} or U-turn{(addedTurns == 1 ? "" : "s")} the game's traffic data doesn't have.");
                var ride = new Ride(taxiSystem, taxi, kind, target, minutes, car!, prefab, vehicleType, goals, route, spawn != null)
                {
                    ownCar = ownCar,
                    standIn = dressed,
                };
                loaded = null;
                Log.Info($"Ride accepted: {kind}, {route.Waypoints.Count} waypoints, {route.LengthMetres:0} m, drive {route.DriveSeconds:0} s, walk {route.WalkSeconds:0} s, planned in {planMs} ms, budget {minutes:0.0} game minutes.");
                Active = ride;
                return ride.Run();
            }
            catch (Exception ex)
            {
                Log.Error("Ride not started: " + ex);
                if (dressed != null)
                {
                    try { dressed.Undress(); } catch (Exception undressEx) { Log.Error("Stand-in not undressed: " + undressEx.Message); }
                }
                if (ownCar != null) OwnCarTaxi.Show(ownCar);
                if (loaded != null)
                {
                    try { TrafficManager.Instance.RemoveVehicle(loaded.GetIndex()); }
                    catch (Exception removeEx) { Log.Error("Loaded car not removed: " + removeEx.Message); }
                }
                return null;
            }
        }

        private const string SwitchedOff = "switched off in the options";

        private static IEnumerator? Vanilla(string reason)
        {
            Log.Info("Vanilla ride: " + reason + ".");
            // Spike builds say why on screen too; a ride switched off is just a vanilla ride.
            if (reason != SwitchedOff) Notice.Show("Driven Rides: vanilla ride, " + reason + ".");
            return null;
        }

        // The length of the car a route is planned for: manoeuvres that failed in turn tests for cars this long are
        // left out (TestedSteps).
        private static float planLength;
        private static Steering planSteering;
        // A ride car driven by its own physics (NWH) is planned with its drivable prefab's shape and Core's feasibility
        // check instead of the Gley model (NwhPlanner); TestedSteps is then a record, not a filter. Off until rides
        // are driven that way (docs/implementation-plan.md, phase 5); the rig's driver=nwh uses NwhPlanner directly.
        internal static bool PlanForNwh { get; set; }
        private static NwhPlanner? planNwh;
        private readonly NwhPlanner? nwhPlanner;
        private float carLength;
        private Steering carSteering;

        // A pooled copy's measurements: Gley fills length and width in when it builds the pool.
        private static VehicleComponent PooledCopy(VehicleComponent prefab)
        {
            if (TrafficVehiclesField?.GetValue(TrafficManager.Instance) is TrafficVehicles vehicles)
                foreach (var v in vehicles.GetVehicleList())
                    if (v != null && v.prefab == prefab.gameObject && v.length > 0f) return v;
            return prefab;
        }

        private static float LengthOf(VehicleComponent prefab) => PooledCopy(prefab).length;



        private static Route? Plan(LaneGraph graph, int start, List<Goal> goals, int vehicleType, out long ms, out string why)
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
            while (job.Status == PlanStatus.Running && watch.ElapsedMilliseconds < PlanBudgetMs)
                job.Step(PlanStep, () => watch.ElapsedMilliseconds >= PlanBudgetMs);
            ms = watch.ElapsedMilliseconds;
            why = job.Status == PlanStatus.Running ? "over the time budget" : job.Status.ToString();
            // Small steps keep the overshoot small; a route found after the budget counts as none.
            if (job.Status == PlanStatus.Found && ms > PlanBudgetMs) { why = "found after the time budget"; return null; }
            return job.Status == PlanStatus.Found ? job.Result : null;
        }

        // Up to six proper traffic lanes within 50 m of the car, in front of it first and nearest first, with nothing
        // built in between; the first that reaches a goal wins.
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
            for (var k = 0; k < candidates.Count && k < 6; k++)
            {
                var route = Plan(graph, candidates[k].Index, goals, vehicleType, out var ms, out _);
                planMs += ms;
                if (route != null) return route;
            }
            return null;
        }

        // Street props and buildings a car can hit: barricades, medians, posts.
        private static readonly int ObstacleMask = LayerMask.GetMask("StreetProps") | LayerHelper.buildingsLayerMask;

        // What the world check needs of a car, from a pooled copy (Gley fills them in when it builds the pool): its
        // steering, and its body box in its own space, as the turn tests measure it.
        private readonly struct Steering
        {
            public readonly CarShape Shape;
            public readonly float HalfWidth, Length, Pivot;
            public readonly Vector3 BodyCentre, BodyHalf;

            public Steering(VehicleComponent copy)
            {
                HalfWidth = copy.colliderWidth / 2f;
                Length = copy.length;
                var front = copy.frontTrigger != null ? copy.transform.InverseTransformPoint(copy.frontTrigger.position).z : copy.length / 2f;
                TestCar.BodyBox(copy, out BodyCentre, out BodyHalf);
                // Gley puts the centre of mass between the wheels when it builds the pool.
                var mass = copy.rb != null ? copy.rb.centerOfMass : BodyCentre;
                // The car turns as the player's drivable version of it does (DrivableSteering); the ride gives Gley
                // these numbers for its car. A car the player can't drive (the taxi) keeps Gley's wheel distance with
                // its lock held to a FallbackRadius circle.
                var drivable = DrivableSteering.For(copy.prefab != null ? copy.prefab : copy.gameObject, ManoeuvreSpeed / 3.6f);
                var wheelDistance = drivable?.Wheelbase ?? copy.wheelDistance;
                var steer = drivable?.Steer ?? Mathf.Min(copy.maxSteer, Mathf.Atan(copy.wheelDistance / FallbackRadius) * Mathf.Rad2Deg);
                // The ride turns the car about its centre of mass (TurnAboutMiddle), so that is the model's pivot: the
                // front axle and the body box are measured from it.
                Pivot = mass.z;
                Shape = new CarShape(wheelDistance, steer, front - mass.z, mass.y, 0f);
                BodyCentre.z -= mass.z;
                if (Logged.Add(copy.name))
                    Log.Info($"Steering for {copy.name}: pivot {Pivot:0.00} m ahead of the origin, wheel distance {Shape.WheelDistance:0.00} m, lock {Shape.MaxSteerDegrees:0.0} ({(drivable != null ? "its drivable version" : "Gley's, held to " + FallbackRadius + " m")}; Gley {copy.wheelDistance:0.00} m, {copy.maxSteer:0.0}), circle {MinRadius:0.0} m, front axle {Shape.FrontAxle:0.00} m ahead, centre of mass {Shape.CentreAhead:0.00} m ahead and {Shape.CentreHeight:0.00} m up, body {BodyHalf * 2f} at {BodyCentre}.");
            }

            private static readonly HashSet<string> Logged = new HashSet<string>();
            // The tightest circle for a car without a drivable version: about the drivable cars' own.
            private const float FallbackRadius = 5f;
            // The lock is the drivable car's at this speed (NWH narrows it as the car goes faster): a driver makes a
            // tight turn slowly. At 25 km/h the 6.7 m car's circle was 8.1 m and it lost all but one U-turn (turn test
            // 0.1.49).
            private const float ManoeuvreSpeed = 10f;

            public float MinRadius => Shape.WheelDistance / Mathf.Tan(Shape.MaxSteerDegrees * Mathf.Deg2Rad);

            // How much quicker than Gley the ride car's driver turns the wheel: three times for a car with a wide
            // turning circle (the 6.7 m car lagged into turns at Gley's rate), Gley's own rate for a nimble one, which
            // at 3x cut its U-turns across to the far side early (taxi and limo, turn test 0.1.47; at Gley's rate in
            // 0.1.31 they passed).
            public float SteeringFactor => Mathf.Clamp(1f + (MinRadius - 3.5f), 1f, 3f);
        }

        // Room kept around the body box on an added turn: the drive model was up to 0.15 m off the recorded track,
        // and the turn tests fail a car that comes within 0.1 m of anything.
        private const float TurnMargin = 0.2f;
        private static readonly Collider[] turnHits = new Collider[8];
        private static readonly int RoadsMask = LayerMask.GetMask("Roads");
        // A turn's way is checked against the road's own colliders from this height above the lane up: a slope or a
        // crown stays under it, the divider a car got stuck on (0.7 m, turn test 0.1.41) does not.
        private const float RoadClearance = 0.35f;

        // An added turn driven as Gley will drive it (Core's TurnDrive), from its entry through its vias to the aim,
        // with the car's body box checked against street props and buildings every half metre.
        private static bool TurnClear(LaneGraph graph, int entry, int exit, int aim, Steering car, out string what, float speedKmh = TurnSpeed)
        {
            what = "";
            if (car.Shape.WheelDistance <= 0f || !graph.PlainHeading(entry, out var dx, out var dz)) return true;
            var targets = new List<Vec3>();
            foreach (var via in graph.TurnVias(entry, exit)) targets.Add(graph.Position(via));
            targets.Add(graph.Position(aim));
            // Gley moves on from the aim while the car is still turning; the rest of the swing, 20 m down the lane.
            targets.AddRange(TurnDrive.LaneAfter(graph, aim, 20f));
            return BodyClear(TurnDrive.Drive(graph.Position(entry), dx, dz, targets, car.Shape, speedKmh / 3.6f, car.SteeringFactor), car, out what);
        }

        // The car's body box, TurnMargin larger, at each pose of a modelled drive; null poses: the car never got there.
        private static bool BodyClear(List<Core.Ride.Pose>? poses, Steering car, out string what)
        {
            what = "";
            if (poses == null) { what = "a turn the car can't make"; return false; }
            var half = car.BodyHalf + new Vector3(TurnMargin, 0f, TurnMargin);
            // Against the road's own colliders only from RoadClearance up, so the road surface is left out but a divider
            // built into the road is not.
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

        // How far ahead of the car's pivot, along a turn's arc, the ride puts Gley's target: its front axle and this
        // much more. The modelled 6.7 m car kept its front axle, middle and rear axle within 0.3 m of every catalogue
        // arc with it (TurnDrive.Pursue); a fixed 3.5 m from the pivot put the limo's target behind its front axle and
        // its wheel flicked from side to side (Peter, turn test 0.1.47).
        private const float ArcLead = 1.1f;

        private static float ArcLookahead(Steering car) => car.Shape.FrontAxle + ArcLead;

        // An added turn's driver's arc, if the junction has room for one this car can follow and the car, pursuing it
        // as Gley will, touches nothing.
        private static Polyline? ClearArc(LaneGraph graph, int entry, int exit, Steering car)
        {
            if (car.Shape.WheelDistance <= 0f) return null;
            var line = TurnArc.Fit(graph, entry, exit, car.MinRadius);
            if (line == null) return null;
            var poses = TurnDrive.Pursue(line, car.Shape, TurnSpeed / 3.6f, car.SteeringFactor, ArcLookahead(car));
            if (poses == null) return null;
            // The lead-in is the entry lane, driven as any lane is; the check starts where the car's nose reaches the
            // entry waypoint.
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

        // The steps of a route the game's data doesn't have, checked against street props and buildings.
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
                if (Contains(graph.Successors(w[k]), w[k + 1]) || expanded.Arcs.Contains((w[k], w[k + 1]))) continue;
                points.Clear();
                points.Add(all[w[k]].position);
                foreach (var via in graph.TurnVias(w[k], w[k + 1])) points.Add(all[via].position);
                // A U-turn is driven as Gley will drive it, through its vias, at the speed the ride takes it: a long car
                // that can't make one cleanly goes round the block (Peter: few drivers would U-turn a car that size).
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
                // A turn with guide waypoints is checked along them, 1.5 m wide; a short one without, along its chord
                // back as well, since the car cuts that corner.
                var turn = Contains(graph.MissingTurns(w[k]), w[k + 1]);
                // A lane change's sweep is as wide as the car: 0.8 m let a 2.3 m car change lanes into a lamp post by
                // the kerb (turn test 0.1.33).
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

        private static readonly FieldInfo? PhoneDriverField = AccessTools.Field(typeof(SmartphonePrivateDriverUI), "_privateDriverVehicle");

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

        private static double NowMinutes()
        {
            var game = SaveGameManager.Current;
            return game == null ? 0 : game.Day * 1440.0 + game.Hour * 60.0 + game.Minute;
        }

        private static bool TimeMachineRunning => InstanceBehavior<UIs>.Instance.timeMachine.isRunning;

        private double LeftMetres => progress?.LeftMetres ?? route.LengthMetres;

        internal bool OwnsCar =>
            car != null && car.gameObject.activeInHierarchy && car.GetIndex() == carIndex &&
            (!departed || ReferenceEquals(car.presetPath, owned));

        private bool SaveChanged => fakeSaveChange || !ReferenceEquals(SaveGameManager.Current, save);

        // The ride's iterator. The finally block runs on normal exits and exceptions; a game unload abandons the
        // iterator without running it, so OnGameUnloaded cancels the ride itself.
        private IEnumerator Run()
        {
            try
            {
                if (kind == Kind.OwnCar)
                {
                    var exit = ExitOwnCar();
                    while (exit.MoveNext()) yield return exit.Current;
                    if (cancelled) yield break;
                    if (boardFailed)
                    {
                        Cancel("the player couldn't get out of the car");
                        // As vanilla does when there's no room to get out: back in the car (the refusal has run out).
                        if (exitReEnterRefused && ownCar != null && SaveGameManager.Current?.ActiveVehicleId == null) ownCar.EnterVehicle();
                        Notice.Show("There's no room to get out of the car here.");
                        yield break;
                    }
                }
                try { Board(); }
                catch (Exception ex) { boardFailed = true; Log.Error("Boarding failed: " + ex); }
                // At least one yield before any exit: TravelTo stores the coroutine only after this first step, so an
                // exit before it would leave a dead handle in TaxiSystem's field.
                yield return null;
                if (boardFailed) { Cancel("boarding failed"); yield break; }
                var waited = 0f;
                while (!mapClosed && waited < MapWaitSeconds && outcome == Outcome.None)
                {
                    if (cancelled) yield break;
                    waited += Time.unscaledDeltaTime;
                    yield return null;
                }
                if (cancelled) yield break;
                if (outcome == Outcome.None) Depart();
                while (outcome == Outcome.None)
                {
                    if (cancelled) yield break;
                    if (bridgeSkip != null)
                    {
                        var skip = SkipBridge();
                        while (skip.MoveNext()) yield return skip.Current;
                        if (cancelled) yield break;
                        continue;
                    }
                    yield return null;
                }
                if (cancelled) yield break;
                phase = Phase.Finishing;
                ApplyScale(false);
                Log.Info($"Ride ending: {outcome} ({outcomeReason}), {NowMinutes() - startMinute:0.0} game minutes so far.");
                // An arrival needs no fade: the player steps out beside the car (Peter). Skip and the vanilla finish
                // fade, as vanilla does. The game's fades are stepped here, so an exception in them runs the finally.
                // A destination far from where the car stops (a house back from the road) gets the vanilla door.
                farDrop = outcome == Outcome.Complete && target != null && car != null && Vector3.Distance(car.transform.position, target.GetNavMeshTargetPosition()) > FarDropMetres;
                if (outcome == Outcome.Vanilla || farDrop)
                {
                    faded = true;
                    var fade = UiFader.Fade(1f);
                    while (fade.MoveNext()) yield return fade.Current;
                    if (cancelled) yield break;
                }
                // Stepped here rather than yielded as a nested coroutine, so an exception inside it runs this
                // iterator's finally block.
                var steps = Bookkeeping();
                while (steps.MoveNext()) yield return steps.Current;
                if (cancelled) yield break;
                if (outcome == Outcome.Complete) ReleaseComplete();
                else ReleaseVanilla();
                RestoreCamera();
                if (faded)
                {
                    var unfade = UiFader.UnFade();
                    while (unfade.MoveNext()) yield return unfade.Current;
                    faded = false;
                }
                // Autopark still belongs to the ride, so nothing else starts meanwhile and a cancel stops it. The game's
                // autopark fades on its own; the flag lets a cancel unfade.
                if (kind == Kind.OwnCar && ownCarEntered && ownCar != null)
                {
                    faded = true;
                    // Autopark checks the player is still in the car before it moves it, and always unfades after its fade.
                    var park = Flatten(OwnCarTaxi.Autopark(ownCar, () => cancelled || SaveChanged));
                    while (park.MoveNext()) { yield return park.Current; if (cancelled) yield break; }
                    faded = false;
                }
                Finish();
            }
            finally
            {
                if (!finished) Cancel("the ride iterator stopped early");
            }
        }

        // Steps nested iterators here instead of handing them to Unity, so an exception in one (the game's fades inside
        // autopark) surfaces in the ride's iterator and runs its cleanup. Yield instructions still go to Unity.
        private static IEnumerator Flatten(IEnumerator root)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var top = stack.Peek();
                if (!top.MoveNext()) { stack.Pop(); continue; }
                if (top.Current is IEnumerator inner && !(top.Current is CustomYieldInstruction)) { stack.Push(inner); continue; }
                yield return top.Current;
            }
        }

        // Vanilla's way out of the car: the game warps the player to the car's door a frame later, or puts them back in
        // if there is no room. The map still covers the screen.
        private static void RefuseReEntryFor(CarController car)
        {
            exitingCar = car;
            exitBlockUntilFrame = Time.frameCount + 3;
            exitReEnterRefused = false;
        }

        private IEnumerator ExitOwnCar()
        {
            RefuseReEntryFor(ownCar!);
            // The map hides the HUD panel the game reads the parking state from on exit (SetParkingData), so the
            // state of where the car stands is written here.
            try
            {
                ownCar!.UpdateParkingZone();
                var info = InstanceBehavior<UIs>.Instance.playerHUD.itemPanelUI.vehicleInfo;
                if (info.isCommonVehicle)
                {
                    ownCar.vehicleInstance.parkingState = info.currentParkingState;
                    ownCar.vehicleInstance.parkingNeighbourhood = info.currentParkingNeighbourhood;
                }
            }
            catch (Exception ex) { Log.Error("Own car parking state: " + ex.Message); }
            try { ownCar!.ExitVehicle(); }
            catch (Exception ex) { boardFailed = true; Log.Error("Own car exit: " + ex); }
            yield return null;
            yield return null;
            if (!boardFailed && (exitReEnterRefused || SaveGameManager.Current?.ActiveVehicleId != null)) boardFailed = true;
            // Wait out the refusal, so the game's late call can't land after boarding.
            while (Time.frameCount <= exitBlockUntilFrame) yield return null;
            exitingCar = null;
        }

        // The end of an own-car ride: the stand-in goes back to the pool and the car takes its place, with the player in
        // the driver's seat. A skip or a failure puts the car on the lane nearest the destination, as vanilla puts a
        // passenger at the door.
        private IEnumerator ArriveInOwnCar()
        {
            var real = ownCar!;
            var destination = target != null ? target.GetNavMeshTargetPosition() : real.transform.position;
            Vector3 position;
            Quaternion rotation;
            string where;
            // Where the dressed body sat, which for a dressed stand-in isn't its root.
            var standInAt = standIn != null ? standIn.BodyPosition : car != null ? car.transform.position : real.transform.position;
            if (outcome == Outcome.Complete && OwnsCar) { position = standInAt; rotation = car.transform.rotation; where = "where the stand-in stopped"; }
            else
            {
                var y = destination.y;
                var lane = TrafficManager.Instance.GetClosestWaypoint(destination, 50f, w => IsValidForLeaving(w, y), prefab);
                if (lane != null) { position = lane.position; rotation = TrafficManager.Instance.GetWaypointOrientation(lane); where = $"on the lane at waypoint {lane.listIndex}"; }
                else if (OwnsCar) { position = standInAt; rotation = car.transform.rotation; where = "where the stand-in was"; }
                else { position = real.transform.position; rotation = real.transform.rotation; where = "where it was parked (no lane near the destination)"; }
            }
            RemoveStandIn();
            OwnCarTaxi.Show(real);
            ownCarPlaced = true;
            OwnCarTaxi.Teleport(real, position, rotation);
            VehicleHelper.DestroyBlockingVehicles(real.gameObject, real.vehicleType, real.transform);
            real.SavePosition();
            OwnCarTaxi.ChargeFuel(real, (float)plannedMetres);
            Log.Info($"Own car placed {where}, {Vector3.Distance(position, destination):0} m from the destination.");
            Detach(BesideOwnCar());
            // The agent counts as on the NavMesh from the next frame; then in through vanilla's door. Anything that took
            // over meanwhile (the hospital, a load) keeps the player.
            yield return null;
            if (cancelled || SaveChanged) yield break;
            // Entered only once vanilla's EnterVehicle has run to its end: it sets ActiveVehicleId before its event,
            // the parenting and the engine, so a throw there leaves a half-entered car.
            try
            {
                real.EnterVehicle();
                ownCarEntered = SaveGameManager.Current?.ActiveVehicleId == real.vehicleInstance.id;
            }
            catch (Exception ex)
            {
                Log.Error("Own car: getting back in failed: " + ex);
                if (SaveGameManager.Current?.ActiveVehicleId == real.vehicleInstance.id)
                {
                    RefuseReEntryFor(real);
                    try { real.ExitVehicle(); }
                    catch (Exception exitEx)
                    {
                        // Vanilla's exit failed too: undo by hand what EnterVehicle had set, so the game doesn't think the
                        // player is driving.
                        Log.Error("Own car: undoing the half entry: " + exitEx.Message);
                        try
                        {
                            SaveGameManager.Current!.ActiveVehicleId = null;
                            real.controlledByPlayer = false;
                            // As vanilla's exit: the player back under the game manager, the figure under the player.
                            if (player.transform.IsChildOf(real.transform)) player.transform.SetParent(manager.transform, true);
                            var figure = player.Character.transform;
                            if (figure.IsChildOf(real.transform)) { figure.SetParent(player.transform, false); figure.localPosition = Vector3.zero; figure.localRotation = Quaternion.identity; }
                            player.UnsetNavigationBlocker(NavigationBlocker.Vehicle);
                            try { AccessTools.Method(typeof(CarController), "StopEngine")?.Invoke(real, null); } catch (Exception engineEx) { Log.Error("Own car: engine: " + engineEx.Message); }
                            player.Character.WarpSafely(BesideOwnCar());
                            // Walking on foot needs no selected vehicle (PlayerController.Update); the HUD panel's close
                            // clears it, and so does this if the close fails.
                            try { InstanceBehavior<UIs>.Instance.playerHUD.itemPanelUI.Toggle(false); } catch (Exception panelEx) { Log.Error("Own car: vehicle panel: " + panelEx.Message); }
                            manager.selectedVehicle = null;
                            SetGameCamera(PedestrianCameraField?.GetValue(manager));
                        }
                        catch (Exception handEx) { Log.Error("Own car: undoing the half entry by hand: " + handEx.Message); }
                    }
                }
            }
            real.SavePosition();
            if (!ownCarEntered) { player.Character.ToggleVisibility(true); Log.Error("Own car: the player didn't get back in; on foot beside the car."); }
            Log.Info($"Own-car ride done: {NowMinutes() - startMinute:0.0} game minutes in all.");
        }

        // A NavMesh point by the own car where it stands now; the pickup point if there is none.
        private Vector3 BesideOwnCar()
        {
            if (ownCar == null) return pickupPosition;
            return NavMesh.SamplePosition(ownCar.transform.position, out var hit, 4f, NavMesh.AllAreas) ? hit.position : ownCar.GetNavMeshTargetPosition();
        }

        private void RemoveStandIn()
        {
            if (standInRemoved) return;
            standInRemoved = true;
            try { standIn?.Undress(); } catch (Exception ex) { Log.Error("Stand-in undress: " + ex.Message); }
            standIn = null;
            try { if (OwnsCar) { car.presetPath = null; TrafficManager.Instance.RemoveVehicle(carIndex); } }
            catch (Exception ex) { Log.Error("Stand-in removal: " + ex.Message); }
        }

        private void Board()
        {
            pickupPosition = player.transform.position;
            pickupRotation = player.transform.rotation;
            player.ResetNavigation();
            anchor = new GameObject("DrivenRides.Anchor");
            anchor.transform.SetPositionAndRotation(car.transform.position, car.transform.rotation);
            anchor.AddComponent<RideAnchor>().Target = car.transform;
            anchorComponent = anchor.GetComponent<RideAnchor>();
            anchorComponent.Valid = () => OwnsCar;
            // Set first, so cleanup also undoes a partly finished attachment.
            attached = true;
            boarded = true;
            // Also sets NavigationBlocker.Taxi, disables the capsule and the agent, and tells Gley the collider is gone.
            player.AttachToObject(anchor.transform, NavigationBlocker.Taxi);
            player.Character.ToggleVisibility(false);
            // The driver's own Update keeps its "Private driver (… m)" guider up; the mod skips that Update while it
            // drives, so the guider is taken down here (Peter: it stayed during the ride).
            if (kind == Kind.PrivateDriver)
            {
                try { UI.Guiders.GuidersManager.ResetGuider(UI.Guiders.DirectionGuiderType.PrivateDriver); }
                catch (Exception ex) { Log.Error("Guider: " + ex.Message); }
            }
            InstanceBehavior<CityManager>.Instance.cityMap.Close();
            Log.Info($"Boarded at {pickupPosition}.");
        }

        public static void OnCityMapClosed()
        {
            if (Active != null) Active.mapClosed = true;
        }

        private void Depart()
        {
            if (!OwnsCar) { EndWith(Outcome.Vanilla, "the car was gone before departure"); return; }
            if (kind == Kind.Taxi) taxi!.DriveAway();
            owned.Clear();
            // A loaded car sits on its spawn waypoint; the route starts there.
            for (var i = onFirstWaypoint ? 1 : 0; i < path.Count; i++) owned.Add(path[i]);
            car.presetPath = owned;
            departed = true;
            SetTopSpeed();
            Go();
            progress = new RouteProgress(Points(path));
            IndexRoute();
            watch = new RideWatch(unpausedSeconds, route.DriveSeconds);
            // The car's own traffic music (AiCarMusic, a one-in-ten chance per car) played at full volume with the
            // camera on the car (spike 18). Muted for the ride.
            foreach (var music in car.GetComponentsInChildren<AiCarMusic>(true))
                if (music.musicPlayer != null && !music.musicPlayer.mute) { music.musicPlayer.mute = true; muted.Add(music.musicPlayer); }
            if (kind != Kind.Test)
            {
                probe = car.gameObject.AddComponent<CollisionProbe>();
                probe.Trace = trace;
                SwitchCamera();
            }
            phase = Phase.Driving;
            Log.Info($"Departed: {owned.Count} waypoints for Gley, light cycle {GreenLightTime()} s.");
        }

        private static List<Vec3> Points(List<int> waypoints)
        {
            var all = GleyGraph.Waypoints!;
            var points = new List<Vec3>(waypoints.Count);
            foreach (var i in waypoints) points.Add(GleyGraph.ToVec(all[i].position));
            return points;
        }

        // Puts each added turn's and U-turn's waypoints between its entry and exit (Core's PathExpander).
        private List<int> Expand(Route r)
        {
            var steering = carSteering;
            var graph = GleyGraph.Graph;
            if (nwhPlanner != null && graph != null)
            {
                var plan = nwhPlanner.Plan(graph, r.Waypoints);
                nwhPlan = plan;
                arcs.Clear();
                arcAt = 0;
                arcAlong = -1f;
                foreach (var (at, line) in plan.Arcs) arcs.Add((plan.Path[at], plan.Path[at + 1], line));
                slowPath.Clear();
                slowPath.UnionWith(plan.Expanded.SlowPath);
                uTurnPath.Clear();
                uTurnPath.UnionWith(plan.Expanded.UTurnPath);
                return plan.Path;
            }
            var expanded = PathExpander.Expand(graph, r.Waypoints, (e, x, a) => TurnClear(e, x, a, steering), (e, x) => ArcClear(e, x, steering));
            arcs.Clear();
            arcAt = 0;
            arcAlong = -1f;
            if (graph != null)
                foreach (var (entry, exit) in expanded.Arcs)
                    if (ClearArc(graph, entry, exit, steering) is Polyline line) arcs.Add((entry, exit, line));
            slowPath.Clear();
            slowPath.UnionWith(expanded.SlowPath);
            uTurnPath.Clear();
            uTurnPath.UnionWith(expanded.UTurnPath);
            return expanded.Path;
        }

        private static bool Contains(IReadOnlyList<int> list, int value)
        {
            for (var k = 0; k < list.Count; k++) if (list[k] == value) return true;
            return false;
        }

        private static string GreenLightTime()
        {
            try { return InstanceBehavior<CityManager>.Instance.trafficComponent.greenLightTime.ToString("0.#"); }
            catch { return "?"; }
        }

        // Every frame, from RideHost.Update.
        public void Tick()
        {
            if (finished || cancelled) return;
            if (SaveChanged) { if (kind == Kind.Test) { EndWith(Outcome.Vanilla, "the save changed"); return; } Dispose(); return; }
            var paused = InstanceBehavior<UIs>.Instance.gameSpeed.Paused;
            if (!paused) unpausedSeconds += Time.unscaledDeltaTime;
            if (phase != Phase.Driving && phase != Phase.Stopping) return;
            RefreshPhoneClock();
            if (!OwnsCar) { EndWith(Outcome.Vanilla, "the car is no longer the ride's"); return; }

            if (throwNext) { throwNext = false; throw new InvalidOperationException("Debug: exception inside the ride's frame update."); }
            progress!.Update(GleyGraph.ToVec(car.transform.position));
            // An arc turn the car is past without having started it (a bridge skip, a replan) is done with.
            while (arcAlong < 0f && arcAt < arcs.Count && (!routeIndex.TryGetValue(arcs[arcAt].Exit, out var exitAt) || exitAt < progress.NextIndex - 1)) arcAt++;
            var along = alongOffset + progress.AlongMetres;
            var traffic = TrafficManager.Instance;
            trace.Sample(car.transform.position, car.transform.eulerAngles.y, car.rb != null ? car.rb.velocity.magnitude : 0f,
                traffic.GetTargetWaypoint(carIndex)?.listIndex ?? -1, traffic.GetCurrentDrivingState(carIndex).Item2.ToString(), along, progress.LeftMetres, owned.Count);
            var slow = false;
            var crawl = false;
            for (var k = progress.NextIndex - 1; k <= progress.NextIndex + 1; k++) { slow |= slowPath.Contains(k); crawl |= uTurnPath.Contains(k); }
            ApplyTopSpeed(crawl && carLength >= LongCarMetres ? UTurnSpeed : slow ? TurnSpeed : TopSpeed);
            lastAlong = along;

            if (phase == Phase.Driving)
            {
                if (replanJob != null) StepReplan();
                else if (owned.Count <= 1 || progress.PointsLeft <= 1) StopAtEnd();
                else if (OnBridge()) return;
                else if (StepBlockedAhead()) Replan();
                else
                {
                    HandleStops();
                    if (IsWaiting()) watch!.Waiting(unpausedSeconds);
                    switch (watch!.Update(unpausedSeconds, along))
                    {
                        case WatchVerdict.Restart: Restart(); break;
                        case WatchVerdict.Replan: Replan(); break;
                        case WatchVerdict.GiveUp: EndWith(Outcome.Vanilla, "watchdog: " + watch.LastReason); break;
                    }
                }
            }
            else
            {
                stopWait += Time.unscaledDeltaTime;
                if ((car.rb != null && car.rb.velocity.magnitude < 0.5f && LeftMetres < ArriveMetres) || stopWait > StopWaitSeconds)
                    EndWith(Outcome.Complete, "arrived");
            }
            ApplyScale(phase == Phase.Driving || phase == Phase.Stopping);
            PanelText = $"  {LeftMetres / 1000.0:0.0} km to go";
        }

        // Takes off the stops that the summon, the arrival, a crash or the hold put on the car, and nothing else: a
        // car ahead (StopInDistance, Follow) and a red light being obeyed stay, or the car drives into what is in front
        // of it (spike 13: into the car ahead in its own lane right after boarding, when this cleared every action).
        // Gley keeps two records: the AI's list of actions and the physics' driving state. A hard stop writes the
        // driving state directly, and Gley only updates it when the AI's top action changes, so the top action is
        // applied again explicitly.
        private static readonly MethodInfo? NewActionMethod = AccessTools.Method(typeof(DrivingAI), "NewDriveActionArrived");
        private static readonly MethodInfo? ApplyActionMethod = AccessTools.Method(typeof(DrivingAI), "ApplyAction");
        private static readonly FieldInfo? CurrentActionField = AccessTools.Field(typeof(DrivingAI), "currentActiveAction");

        // Takes one action off the car and reapplies the AI's top action to the driving state.
        private void ClearAction(SpecialDriveActionTypes action)
        {
            var ai = DrivingAIField?.GetValue(TrafficManager.Instance);
            if (ai == null || NewActionMethod == null || ApplyActionMethod == null || CurrentActionField == null) return;
            NewActionMethod.Invoke(ai, new object[] { carIndex, action, false });
            if (CurrentActionField.GetValue(ai) is SpecialDriveActionTypes[] current && carIndex < current.Length)
            {
                current[carIndex] = SpecialDriveActionTypes.Destroyed;
                ApplyActionMethod.Invoke(ai, new object[] { carIndex });
            }
        }

        private void Go()
        {
            var traffic = TrafficManager.Instance;
            var ai = DrivingAIField?.GetValue(traffic);
            if (ai == null || NewActionMethod == null || ApplyActionMethod == null || CurrentActionField == null)
            {
                Log.Error("Go: Gley's DrivingAI methods not found.");
                return;
            }
            var next = traffic.GetTargetWaypoint(carIndex);
            var atRed = Options.TrafficLights && next != null && next.stop;
            foreach (var action in new[] { SpecialDriveActionTypes.StopNow, SpecialDriveActionTypes.TempStop, SpecialDriveActionTypes.StopInPoint })
            {
                if (action == SpecialDriveActionTypes.StopInPoint && atRed) continue;
                NewActionMethod.Invoke(ai, new object[] { carIndex, action, false });
            }
            if (CurrentActionField.GetValue(ai) is SpecialDriveActionTypes[] current && carIndex < current.Length)
            {
                // A value the AI never picks, so ApplyAction sees a change and sends the driving state.
                current[carIndex] = SpecialDriveActionTypes.Destroyed;
                ApplyActionMethod.Invoke(ai, new object[] { carIndex });
            }
            traffic.VehicleUpdateWaypoint(car);
        }

        // Long enough not to keep pushing into a person or car that stopped it (spike 16).
        private const float BumpSeconds = 3f;
        private float stopNowSeconds;
        private float giveWaySeconds;
        private int loggedRed = -1;

        // Red lights are Gley's own: it stops a car whose target is a red stop line (StopInPoint) and lets it go on
        // green. For a car with a preset path it would also move the target past the stop line once the car is within
        // 5 m, stopped or not; the WaypointRequested prefix (HoldsAtRed) blocks that for the ride car. With the option
        // off, a car stopped at a red is sent on. A bump leaves a StopNow that Gley never clears for a car on a path,
        // so the car is sent on after a moment.
        private void HandleStops()
        {
            var traffic = TrafficManager.Instance;
            var next = traffic.GetTargetWaypoint(carIndex);
            var state = traffic.GetCurrentDrivingState(carIndex).Item2;
            var red = next != null && next.stop;
            if (red && state == SpecialDriveActionTypes.StopInPoint)
            {
                if (!Options.TrafficLights) { Go(); Event("Light: red, driving through (option off)."); return; }
                if (loggedRed != next!.listIndex) { loggedRed = next.listIndex; Log.Info($"Light: waiting at the red at waypoint {next.listIndex}."); }
            }
            // Gley's give-way mode, set at a give-way waypoint, isn't taken off a car on a preset path: on spike 20 the
            // car crawled at 5 m/s through the Hamptons. Off again once the car's target isn't a give-way point.
            if (state == SpecialDriveActionTypes.GiveWay && next != null && !next.giveWay)
            {
                giveWaySeconds += Time.unscaledDeltaTime;
                if (giveWaySeconds >= 3f) { giveWaySeconds = 0f; ClearAction(SpecialDriveActionTypes.GiveWay); Event("Give way: off again."); }
            }
            else giveWaySeconds = 0f;
            if (state == SpecialDriveActionTypes.StopNow)
            {
                stopNowSeconds += Time.unscaledDeltaTime;
                if (stopNowSeconds >= BumpSeconds) { stopNowSeconds = 0f; Go(); Event("Bump: going again."); }
            }
            else stopNowSeconds = 0f;
        }

        // The WaypointRequested prefix: true while the ride car's target is a red stop line and lights are obeyed.
        public static bool HoldsAtRed(int vehicleIndex)
        {
            var ride = Active;
            if (ride == null || ride.finished || ride.cancelled || !ride.departed || ride.carIndex != vehicleIndex || !Options.TrafficLights || !ride.OwnsCar) return false;
            var next = TrafficManager.Instance.GetTargetWaypoint(vehicleIndex);
            return next != null && next.stop;
        }

        // Gley gives each car a random top speed between its model's minimum and maximum. The private driver drives
        // the player's own vehicle, so its top speed is that vehicle type's (a better car rides faster, Peter); a
        // taxi gets its traffic model's maximum. Gley still caps it at the road's limit.
        private static readonly FieldInfo? DrivingAIField = AccessTools.Field(typeof(TrafficManager), "drivingAI");
        private static readonly MethodInfo? SetMaxSpeedMethod = AccessTools.Method(typeof(DrivingAI), "setVehMaxSpeed");
        private static readonly FieldInfo? PowerStepField = AccessTools.Field(typeof(TrafficManager), "vehiclePowerStep");
        // Gley turns the wheel to full lock in one second (VehicleComponent.GetSteeringStep); at 25 km/h that is 7 m of
        // an 8 m U-turn. The ride car's driver turns it up to three times as fast (Steering.SteeringFactor), as a
        // driver does at low speed. The pool slot gets its own step back when the ride lets the car go.
        private static readonly FieldInfo? SteeringStepField = AccessTools.Field(typeof(TrafficManager), "vehicleSteeringStep");
        private float steeringWas = -1f;
        private float wheelDistanceWas = -1f;
        private float maxSteerWas = -1f;
        private static readonly FieldInfo? WheelDistanceField = AccessTools.Field(typeof(TrafficManager), "vehicleWheelDistance");
        private static readonly FieldInfo? MaxSteerField = AccessTools.Field(typeof(TrafficManager), "vehicleMaxSteer");
        private const float AccelerationSeconds = 5f;

        private float TopSpeed => testTopSpeed > 0 ? testTopSpeed : driverTopSpeed > 0 ? driverTopSpeed : car.maxPossibleSpeed;

        // Changes the car's top speed in Gley when it differs from the last one set; Gley reads it at the next waypoint.
        private void ApplyTopSpeed(float speed)
        {
            if (Mathf.Approximately(speed, appliedTop)) return;
            try
            {
                var ai = DrivingAIField?.GetValue(TrafficManager.Instance);
                if (ai == null || SetMaxSpeedMethod == null) return;
                SetMaxSpeedMethod.Invoke(ai, new object[] { carIndex, speed });
                appliedTop = speed;
                if (speed < TopSpeed) trace.Note($"slow {speed:0} km/h for an added turn");
            }
            catch (Exception ex) { Log.Error("Top speed: " + ex.Message); }
        }

        private void SetTopSpeed()
        {
            try
            {
                var ai = DrivingAIField?.GetValue(TrafficManager.Instance);
                if (ai == null || SetMaxSpeedMethod == null) { Log.Error("Top speed: Gley's DrivingAI not found."); return; }
                var top = TopSpeed;
                SetMaxSpeedMethod.Invoke(ai, new object[] { carIndex, (float)top });
                appliedTop = top;
                // Gley sets the acceleration so a car reaches its rolled top speed in its model's accelerationTime
                // (10 s by default): slow off the line. The ride car reaches its top speed in AccelerationSeconds.
                if (PowerStepField?.GetValue(TrafficManager.Instance) is NativeArray<float> power && carIndex < power.Length)
                    power[carIndex] = top / 3.6f / (AccelerationSeconds / Time.fixedDeltaTime);
                if (SteeringStepField?.GetValue(TrafficManager.Instance) is NativeArray<float> steering && carIndex < steering.Length && steeringWas < 0f
                    && WheelDistanceField?.GetValue(TrafficManager.Instance) is NativeArray<float> wheels && carIndex < wheels.Length
                    && MaxSteerField?.GetValue(TrafficManager.Instance) is NativeArray<float> lockArray && carIndex < lockArray.Length)
                {
                    steeringWas = steering[carIndex];
                    wheelDistanceWas = wheels[carIndex];
                    maxSteerWas = lockArray[carIndex];
                    // The drivable car's wheelbase and lock (Steering), its wheel turned to full lock in a third of a
                    // second or more (SteeringFactor), as TurnDrive models it.
                    wheels[carIndex] = carSteering.Shape.WheelDistance;
                    lockArray[carIndex] = carSteering.Shape.MaxSteerDegrees;
                    steering[carIndex] = carSteering.Shape.MaxSteerDegrees * Time.fixedDeltaTime * carSteering.SteeringFactor;
                }
                Log.Info($"Top speed {top} km/h ({(driverTopSpeed > 0 ? $"vehicle type {driverVehicleType}" : "traffic model")}; Gley model {car.minPossibleSpeed}-{car.maxPossibleSpeed}, rolled {car.GetMaxSpeed()}).");
            }
            catch (Exception ex) { Log.Error("Top speed: " + ex.Message); }
        }

        // Each physics step, before Gley's drive job: while the front axle is on an arc turn, Gley's target for the car
        // is the point ArcLookahead on along the arc. TrafficManager keeps the target positions in an array it only
        // rewrites when the car's waypoint changes, and DriveJob steers the front axle at it. After the arc the real
        // target goes back. Gley still pops the exit lane's waypoints as the car passes them.
        internal static void SteerArcs() => Active?.SteerArc();

        private void SteerArc()
        {
            if (!departed || finished || cancelled || arcAt >= arcs.Count || car == null || progress == null || !OwnsCar) return;
            var (entry, exit, line) = arcs[arcAt];
            // Stopping at a light or giving way, Gley brakes by the distance to its real target.
            if (TrafficManager.Instance.GetCurrentDrivingState(carIndex).Item2 != SpecialDriveActionTypes.Forward)
            {
                if (arcAlong >= 0f && !arcHeld) { arcHeld = true; RestoreTarget(); }
                return;
            }
            arcHeld = false;
            var front = car.GetFrontAxle().position;
            if (arcAlong < 0f)
            {
                // It starts with the front axle on the lead-in, by its lane, before Gley lets go of the entry.
                if (!routeIndex.TryGetValue(entry, out var k) || k > progress.NextIndex + 2) return;
                var start = line.At(0f);
                var on = line.At(1f);
                var dir = new Vector3(on.X - start.X, 0f, on.Z - start.Z).normalized;
                var offset = new Vector3(front.x - start.X, 0f, front.z - start.Z);
                var ahead = Vector3.Dot(offset, dir);
                if (ahead < 0f || ahead > TurnArc.LeadIn + 6f || (offset - dir * ahead).magnitude > 2.5f) return;
                arcAlong = 0f;
                Event($"Turn {entry} -> {exit}: steering along its {line.Length:0} m arc.");
            }
            var pivot = car.rb != null ? car.rb.worldCenterOfMass : car.transform.position;
            arcAlong = line.Project(pivot.x, pivot.z, arcAlong);
            var lookahead = ArcLookahead(carSteering);
            if (arcAlong + lookahead >= line.Length) { EndArc(); return; }
            var carrot = line.At(arcAlong + lookahead);
            SetTarget(new Vector3(carrot.X, carrot.Y, carrot.Z));
        }

        private void EndArc()
        {
            arcAt++;
            arcAlong = -1f;
            arcHeld = false;
            RestoreTarget();
        }

        private void RestoreTarget()
        {
            var target = TrafficManager.Instance.GetTargetWaypoint(carIndex);
            if (target != null) SetTarget(target.position);
        }

        // After TrafficManager.FixedUpdate: Gley turns a car about its origin, which on the taxi and the limo is the
        // rear axle, so the ride car pivoted visibly on its back wheels (Peter, turn test 0.1.49). The ride car turns
        // about its centre of mass instead: after the step's rotation (MoveRotation, applied in this physics step) the
        // body is moved by the few centimetres that keep the centre of mass where rotating about it would. Moved, not
        // pushed: added velocity built up through Gley's drive force and threw the car off at 400 m/s (turn test
        // 0.1.50).
        private static readonly FieldInfo? RotationAngleField = AccessTools.Field(typeof(TrafficManager), "vehicleRotationAngle");

        internal static void TurnAboutMiddle() => Active?.TurnAboutCentre();

        private void TurnAboutCentre()
        {
            if (!departed || finished || cancelled || car == null || car.rb == null || !OwnsCar) return;
            if (!(RotationAngleField?.GetValue(TrafficManager.Instance) is NativeArray<float> angles) || carIndex >= angles.Length) return;
            var degrees = angles[carIndex];
            if (Mathf.Abs(degrees) < 0.0001f) return;
            var rb = car.rb;
            // From the centre of mass to the origin, flat; rotating the origin about the centre of mass by the step's
            // angle moves it by (R - 1) of that.
            var offset = rb.position - rb.worldCenterOfMass;
            offset.y = 0f;
            var moved = Quaternion.Euler(0f, degrees, 0f) * offset - offset;
            if (moved.sqrMagnitude > 0.04f) return;
            rb.position += moved;
        }

        // Gley's float3 array, written by reflection: the mod doesn't reference Unity.Mathematics. The boxed copy
        // shares the native buffer.
        private static readonly FieldInfo? TargetPositionField = AccessTools.Field(typeof(TrafficManager), "vehicleTargetWaypointPosition");
        private static MethodInfo? targetSetter;
        private static ConstructorInfo? float3Constructor;

        private void SetTarget(Vector3 at)
        {
            try
            {
                var array = TargetPositionField?.GetValue(TrafficManager.Instance);
                if (array == null) return;
                if (targetSetter == null || float3Constructor == null)
                {
                    var type = array.GetType();
                    targetSetter = type.GetMethod("set_Item");
                    float3Constructor = type.GetGenericArguments()[0].GetConstructor(new[] { typeof(float), typeof(float), typeof(float) });
                }
                targetSetter!.Invoke(array, new[] { carIndex, float3Constructor!.Invoke(new object[] { at.x, at.y, at.z }) });
            }
            catch (Exception ex) { Log.Error("Arc target: " + ex.Message); arcAt = arcs.Count; arcAlong = -1f; }
        }

        // The bridge to the Hamptons is skipped as the game skips it for the player's own car (SkipBridgeHelper):
        // entering a bridge trigger, the car is moved behind a fade to the trigger's far-side target facing its way,
        // and the clock runs the game's own bridge time (Peter).
        private static readonly FieldInfo? SkipTargetsField = AccessTools.Field(typeof(Streets.BridgeTriggerController), "skipTargets");
        private (Transform Target, int PathIndex)? bridgeSkip;
        private readonly HashSet<Streets.BridgeTriggerController> skipped = new HashSet<Streets.BridgeTriggerController>();
        private Streets.BridgeTriggerController[]? bridges;
        private const float MinBridgeSkipMetres = 200f;

        private bool OnBridge()
        {
            var here = car.transform.position;
            // Found once per ride: a scene search every frame is what made 0.1.5 lag.
            bridges ??= Object.FindObjectsOfType<Streets.BridgeTriggerController>();
            foreach (var bridge in bridges)
            {
                if (bridge == null) continue;
                if (skipped.Contains(bridge)) continue;
                var trigger = bridge.GetComponent<Collider>();
                if (trigger == null || !trigger.bounds.Contains(here)) continue;
                skipped.Add(bridge);
                // A test never crosses a bridge (its cases stay clear of the triggers); if one does, it ends here.
                if (kind == Kind.Test) { EndWith(Outcome.Vanilla, "bridge"); return true; }
                if (!(SkipTargetsField?.GetValue(bridge) is Transform[] targets) || targets.Length == 0) { Event("Bridge: no skip targets."); return false; }
                // As SkipBridgeHelper.FindTarget: the target facing most nearly the car's way, here also ahead of it.
                Transform? best = null;
                var bestAngle = float.MaxValue;
                foreach (var t in targets)
                {
                    if (t == null || Vector3.Dot(t.position - here, car.transform.forward) < 0f) continue;
                    var angle = Mathf.Abs(Mathf.DeltaAngle(car.transform.eulerAngles.y, t.eulerAngles.y));
                    if (angle < bestAngle) { bestAngle = angle; best = t; }
                }
                if (best == null) { Event("Bridge: no skip target ahead."); return false; }
                var all = GleyGraph.Waypoints!;
                var at = -1;
                var nearest = 40f;
                for (var k = progress!.NextIndex; k + 1 < path.Count; k++)
                {
                    var d = Vector3.Distance(all[path[k]].position, best.position);
                    if (d < nearest) { nearest = d; at = k; }
                }
                if (at < 0) { Event("Bridge: the route doesn't pass the far side; driving on."); return false; }
                // Only a real crossing: the far end's own trigger, met just after landing, is no bridge to skip (spike
                // 16 skipped twice, the second time 41 m).
                if (Vector3.Distance(here, all[path[at]].position) < MinBridgeSkipMetres) return false;
                bridgeSkip = (best, at);
                phase = Phase.Bridging;
                return true;
            }
            return false;
        }

        private IEnumerator SkipBridge()
        {
            var (target, at) = bridgeSkip!.Value;
            bridgeSkip = null;
            var all = GleyGraph.Waypoints!;
            var from = car.transform.position;
            var to = all[path[at]].position;
            Event($"Bridge: skipping {Vector3.Distance(from, to):0} m to waypoint {path[at]}.");
            faded = true;
            var fade = UiFader.Fade();
            while (fade.MoveNext()) yield return fade.Current;
            if (cancelled) yield break;

            // The car onto the far side, facing along the route; the path from there on.
            var ahead = all[path[at + 1]].position - to;
            var rotation = ahead.sqrMagnitude > 0.01f ? Quaternion.LookRotation(new Vector3(ahead.x, 0f, ahead.z)) : target.rotation;
            if (car.rb != null)
            {
                car.rb.velocity = Vector3.zero;
                car.rb.angularVelocity = Vector3.zero;
                car.rb.position = to + Vector3.up * 0.3f;
                car.rb.rotation = rotation;
            }
            car.transform.SetPositionAndRotation(to + Vector3.up * 0.3f, rotation);
            // Every bridge trigger near the landing is done with too.
            if (bridges != null)
                foreach (var other in bridges)
                    if (other != null && Vector3.Distance(other.transform.position, to) < 300f) skipped.Add(other);
            alongOffset += progress!.AlongMetres;
            var rest = path.GetRange(at, path.Count - at);
            var shifted = new HashSet<int>();
            foreach (var k in slowPath) if (k >= at) shifted.Add(k - at);
            slowPath.Clear();
            slowPath.UnionWith(shifted);
            shifted.Clear();
            foreach (var k in uTurnPath) if (k >= at) shifted.Add(k - at);
            uTurnPath.Clear();
            uTurnPath.UnionWith(shifted);
            path = rest;
            owned.Clear();
            for (var k = 1; k < path.Count; k++) owned.Add(path[k]);
            progress = new RouteProgress(Points(path));
            IndexRoute();
            lastAlong = alongOffset;
            Go();
            yield return null;

            // The game's own bridge time: 0.04 game minutes per metre at the game speed (SkipBridgeHelper).
            var minutes = Vector3.Distance(from, to) * 0.04f * PlayerPrefSettings.GameSpeed;
            timeMachineEnded = false;
            Action ended = () => timeMachineEnded = true;
            GlobalEvents.onTimeMachineEnded += ended;
            try
            {
                Timestamp goal = TimeHelper.Now();
                goal.AddMinutes(minutes);
                ownTimeMachine = true;
                try { InstanceBehavior<UIs>.Instance.timeMachine.StartTimeMachine(goal, disableCancel: true); }
                finally { ownTimeMachine = false; }
                while (!timeMachineEnded && !cancelled) yield return null;
            }
            finally { GlobalEvents.onTimeMachineEnded -= ended; }
            if (cancelled) yield break;

            var unfade = UiFader.UnFade();
            while (unfade.MoveNext()) yield return unfade.Current;
            faded = false;
            watch!.Waiting(unpausedSeconds);
            phase = Phase.Driving;
        }

        // A route is planned with the world as loaded then; far away (the Hamptons from Manhattan) the props may not
        // be there yet. So each added step is checked again once the car is within 80 m of it.
        private readonly HashSet<(int, int)> checkedNear = new HashSet<(int, int)>();
        private float nextNearCheck;

        private bool StepBlockedAhead()
        {
            if (Time.unscaledTime < nextNearCheck) return false;
            nextNearCheck = Time.unscaledTime + 0.5f;
            var graph = GleyGraph.Graph;
            if (graph == null) return false;
            var all = GleyGraph.Waypoints!;
            var here = car.transform.position;
            var w = route.Waypoints;
            for (var k = 0; k + 1 < w.Count; k++)
            {
                if (Contains(graph.Successors(w[k]), w[k + 1]) || checkedNear.Contains((w[k], w[k + 1]))) continue;
                if (!routeIndex.TryGetValue(w[k], out var at) || at < progress!.NextIndex - 1) continue;
                if (Vector3.Distance(here, all[w[k]].position) > 80f) continue;
                checkedNear.Add((w[k], w[k + 1]));
                // The next few waypoints too, so an added turn aims where the ride's path aims (PathExpander).
                var ahead = new List<int>();
                for (var q = k; q < w.Count && q <= k + 4; q++) ahead.Add(w[q]);
                var from = w[k];
                var to = w[k + 1];
                // The NWH check is asked afresh: the parked cars near the step are loaded now.
                nwhPlanner?.Check.Forget(from, to);
                var blocked = (nwhPlanner != null ? nwhPlanner.BlockedSteps(graph, ahead, 0f) : FindBlockedSteps(graph, ahead, carSteering)).FindAll(b => b.From == from && b.To == to);
                if (blocked.Count == 0) continue;
                // A test drives its case as given and reports the block; the rides' blocked-step set stays as it was.
                if (kind == Kind.Test) { EndWith(Outcome.Vanilla, $"replanned: step {w[k]} -> {w[k + 1]} blocked by '{blocked[0].What}'"); return false; }
                if (nwhPlanner != null) nwhPlanner.Block(w[k], w[k + 1], NwhPlanner.CauseOf(graph, w[k], w[k + 1]));
                else GleyGraph.BlockStep(w[k], w[k + 1]);
                Event($"Step {w[k]} -> {w[k + 1]} ahead blocked by '{blocked[0].What}'; replanning.");
                return true;
            }
            return false;
        }

        private void StopAtEnd()
        {
            TrafficManager.Instance.SetVehicleAction(car, SpecialDriveActionTypes.StopInPoint, true);
            // Cleared in place: presetPath stays this same non-null list, so Gley never despawns the car.
            owned.Clear();
            phase = Phase.Stopping;
            Event($"Stopping at the end of the route, {LeftMetres:0} m from its last waypoint.");
        }

        public int Restarts { get; private set; }

        private void Restart()
        {
            Restarts++;
            Go();
            watch!.Restarted(unpausedSeconds);
            Event("Watchdog: restart (" + watch.LastReason + ").");
        }

        // Gley signals a red light by "stop" on the target waypoint and StopInPoint, the car in front by
        // StopInDistance. A waiting car isn't stuck, and a restart would clear those stops.
        private bool IsWaiting()
        {
            var traffic = TrafficManager.Instance;
            var next = traffic.GetTargetWaypoint(carIndex);
            var state = traffic.GetCurrentDrivingState(carIndex).Item2;
            return (next != null && next.stop) || state == SpecialDriveActionTypes.StopInPoint ||
                   state == SpecialDriveActionTypes.GiveWay || state == SpecialDriveActionTypes.StopInDistance;
        }

        // Starts a replan; StepReplan advances it over the next frames.
        private void Replan()
        {
            if (kind == Kind.Test) { EndWith(Outcome.Vanilla, "replanned: watchdog, " + (watch?.LastReason ?? "")); return; }
            var graph = GleyGraph.Graph;
            var next = TrafficManager.Instance.GetTargetWaypoint(carIndex);
            if (graph == null || next == null) { EndWith(Outcome.Vanilla, "replan had no start"); return; }
            replanJob = RoutePlanner.Begin(graph, new RouteRequest
            {
                Start = next.listIndex,
                Goals = goals,
                VehicleType = vehicleType,
                IsDisabled = GleyGraph.IsDisabled,
                IsBlockedStep = (a, b) => GleyGraph.IsBlockedStep(a, b) || (nwhPlanner != null ? nwhPlanner.IsBlocked(a, b) : TestedSteps.Blocks(a, b, carLength)),
                WideUTurns = nwhPlanner != null,
            });
            replanSeconds = 0f;
            Log.Info("Watchdog: replanning (" + watch!.LastReason + ").");
        }

        // The NWH ride's plan for the present route (its path is `path`), for the NwhDrive that drives it.
        private DrivePlan? nwhPlan;

        // NwhDrive.Replan for an NWH ride (phase 5 hands it to the ride's NwhDrive): the step the car found blocked is
        // blocked for this ride, the rest planned again by the NWH planner from where the car stands, joining its
        // pose (NwhPlanner.PlanRoundBlock), and the new route taken as a replan is (progress, waypoint index,
        // watchdog); the new path goes back to the drive, which drives on without stopping. Null: no way on, the
        // drive ends and the watchdog finishes the ride the vanilla way.
        internal DrivePlan? NwhBlocked(int position, string what, float x, float z, float heading)
        {
            var graph = GleyGraph.Graph;
            if (nwhPlanner == null || graph == null) return null;
            return TakeNwhPlan(nwhPlanner.PlanRoundBlock(graph, route.Waypoints, path, position, what, new Vec3(x, 0f, z), heading, goals, vehicleType));
        }

        // NwhDrive.LookAhead for an NWH ride: the planner's look-ahead (lanes loaded since, manoeuvres checked again
        // near), and a new way on taken up the same way.
        private readonly NwhPlanner.Watch nwhWatch = new NwhPlanner.Watch();

        internal DrivePlan? NwhLookAhead(int position, float x, float z, float heading)
        {
            var graph = GleyGraph.Graph;
            if (nwhPlanner == null || graph == null) return null;
            var found = nwhPlanner.LookAhead(graph, route.Waypoints, path, position, new Vec3(x, 0f, z), heading, goals, vehicleType, nwhWatch);
            return found == null ? null : TakeNwhPlan(found);
        }

        private DrivePlan? TakeNwhPlan((DrivePlan Plan, List<int> Route, string Note)? found)
        {
            if (found == null) { Event("NWH: no way on joins the car."); return null; }
            var (plan, next, note) = found.Value;
            Event("NWH: " + note + ".");
            var graph = GleyGraph.Graph!;
            alongOffset += progress!.AlongMetres;
            route = Route.Fixed(graph, next);
            path = Expand(route);
            owned.Clear();
            owned.AddRange(path);
            progress = new RouteProgress(Points(path));
            IndexRoute();
            watch?.Replanned(unpausedSeconds);
            return nwhPlan;
        }

        private void StepReplan()
        {
            var job = replanJob!;
            job.Step(ReplanStep);
            replanSeconds += Time.unscaledDeltaTime;
            if (job.Status == PlanStatus.Running && replanSeconds < ReplanSecondsLimit) return;
            replanJob = null;
            if (job.Status != PlanStatus.Found || job.Result == null) { EndWith(Outcome.Vanilla, $"replan found no route ({job.Status})"); return; }
            alongOffset += progress!.AlongMetres;
            route = job.Result;
            path = Expand(route);
            owned.Clear();
            owned.AddRange(path);
            progress = new RouteProgress(Points(path));
            IndexRoute();
            // Gley takes the new list at its next waypoint request. At a light or in a queue a restart would clear
            // the stop, so it only happens when the car isn't waiting.
            if (IsWaiting()) watch!.Waiting(unpausedSeconds);
            else Restart();
            watch!.Replanned(unpausedSeconds);
            Log.Info($"Watchdog: replanned, {route.Waypoints.Count} waypoints, {route.LengthMetres:0} m, {job.Expanded} expanded.");
        }

        private void Event(string text)
        {
            Log.Info(text);
            trace.Note(text);
        }

        private void IndexRoute()
        {
            routeIndex.Clear();
            for (var k = 0; k < path.Count; k++) routeIndex[path[k]] = k;
        }

        // The HasReachedOrPassedWaypoint prefix. Gley counts a waypoint as passed once it is behind the car's nose,
        // and pops every such waypoint off the path at once; in a U-turn or a lane change, more so at 3x, the car
        // faces sideways for a moment and Gley emptied the rest of the route (spike 8: stopped 272 m short after
        // hitting a wall). For the ride car a waypoint counts as passed only when the car is within 5 m of it or
        // the route progress, which follows the route and not the car's nose, is past it. Null for other cars.
        public static bool? HasPassed(int vehicleIndex, int waypointIndex, Vector3 waypoint, Vector3 carCentre)
        {
            var ride = Active;
            if (ride == null || ride.finished || ride.cancelled || !ride.departed || ride.carIndex != vehicleIndex || ride.progress == null || !ride.OwnsCar) return null;
            if ((waypoint - carCentre).sqrMagnitude < 25f) return true;
            return ride.routeIndex.TryGetValue(waypointIndex, out var k) && k < ride.progress.NextIndex;
        }

        // The ride car's centre of mass, as Gley measures from it; null for other cars.
        public static Vector3? CarCentre(int vehicleIndex)
        {
            var ride = Active;
            if (ride == null || ride.carIndex != vehicleIndex || ride.car == null || ride.car.rb == null) return null;
            return ride.car.rb.worldCenterOfMass;
        }

        // The phone sets its clock once when it opens, the game being paused while it is up; during a ride time goes
        // on, so the clock is set again every second while the phone is open.
        private static readonly FieldInfo? PhoneTimeLabelField = AccessTools.Field(typeof(UI.Smartphone.FullMenu), "timeLabel");
        private static readonly MethodInfo? SetFormattedTimeMethod = AccessTools.Method(typeof(TimeHelper), "SetCurrentFormattedTime");
        private UI.Smartphone.FullMenu? phone;
        private float nextPhoneClock;

        private void RefreshPhoneClock()
        {
            if (!UI.Smartphone.FullMenu.IsOpen || Time.unscaledTime < nextPhoneClock) return;
            nextPhoneClock = Time.unscaledTime + 1f;
            try
            {
                phone ??= Object.FindObjectOfType<UI.Smartphone.FullMenu>();
                var label = phone != null ? PhoneTimeLabelField?.GetValue(phone) : null;
                if (label != null) SetFormattedTimeMethod?.Invoke(null, new[] { label });
            }
            catch (Exception ex) { Log.Error("Phone clock: " + ex.Message); nextPhoneClock = float.MaxValue; }
        }

        public void RequestSkip()
        {
            if (phase == Phase.Boarding || phase == Phase.Driving || phase == Phase.Stopping)
                EndWith(Outcome.Vanilla, "skipped");
        }

        private void EndWith(Outcome result, string reason)
        {
            if (outcome != Outcome.None) return;
            outcome = result;
            outcomeReason = reason;
            phase = Phase.Finishing;
        }

        private IEnumerator Bookkeeping()
        {
            // Skip and the vanilla finish charge what vanilla would for the rest of the way; an arrival charges nothing
            // more, the ride's clock already ran.
            var left = outcome == Outcome.Vanilla ? VanillaMinutesLeft() : 0;
            if (left > 0.01)
            {
                timeMachineEnded = false;
                Action ended = () => timeMachineEnded = true;
                GlobalEvents.onTimeMachineEnded += ended;
                try
                {
                    Timestamp goal = TimeHelper.Now();
                    goal.AddMinutes((float)left);
                    ownTimeMachine = true;
                    try { InstanceBehavior<UIs>.Instance.timeMachine.StartTimeMachine(goal, disableCancel: true); }
                    finally { ownTimeMachine = false; }
                    // Teardown takes a second after isRunning turns false; onTimeMachineEnded marks its end.
                    while (!timeMachineEnded && !cancelled) yield return null;
                }
                finally { GlobalEvents.onTimeMachineEnded -= ended; }
                if (cancelled) yield break;
            }

            if (kind == Kind.OwnCar)
            {
                var arrive = ArriveInOwnCar();
                while (arrive.MoveNext()) yield return arrive.Current;
                yield break;
            }
            var game = SaveGameManager.Current;
            if (kind == Kind.PrivateDriver) game.achievementsData.privateDriverRides++;
            else game.achievementsData.taxiRides++;
            GameEvent.Invoke(string.Empty);
            var place = PlacePlayer();
            // The agent counts as on the NavMesh only from the next frame on, so the check waits one.
            yield return null;
            if (!player.Character.navmeshAgent.isOnNavMesh)
            {
                Log.Info($"Player not on the NavMesh at {place} after one frame; warping there again.");
                player.Character.WarpSafely(place);
                yield return null;
                if (!player.Character.navmeshAgent.isOnNavMesh)
                {
                    Log.Error($"Player still not on the NavMesh at {place}; back to the pickup point.");
                    player.Character.WarpSafely(pickupPosition);
                }
            }
            GameEvent.Invoke("ba:gameevent_completedtaxiride");
            if (taxi is Object alive && alive != null) taxi.OnTravelFinished();
            else if (driverVehicleType == "ba:vehicletype_limo")
            {
                HappinessHelper.AddModifier("ba:happinessmodifier_limo");
                EnergyHelper.GenerateEnergy(5f);
                manager.ChangeHunger(10);
            }
            if (!string.IsNullOrEmpty(happinessModifier)) HappinessHelper.AddModifier(happinessModifier);
            Log.Info($"Ride done: {NowMinutes() - startMinute:0.0} game minutes in all.");
        }

        private Vector3 PlacePlayer()
        {
            var destination = target != null ? target.GetNavMeshTargetPosition() : pickupPosition;
            // An arrival puts the player beside the car, kerb side first (Peter); the vanilla finish at the door.
            var place = outcome == Outcome.Complete && !farDrop && BesideCar(out var beside) ? beside
                : NavMesh.SamplePosition(destination, out var hit, 5f, NavMesh.AllAreas) ? hit.position : destination;
            Detach(place);
            player.Character.ToggleVisibility(true);
            player.Character.Reset();
            return place;
        }

        private bool BesideCar(out Vector3 place)
        {
            place = default;
            if (car == null || !car.gameObject.activeInHierarchy) return false;
            var t = car.transform;
            var offset = Mathf.Max(1f, car.colliderWidth / 2f) + 0.8f;
            foreach (var side in new[] { t.right, -t.right })
            {
                if (NavMesh.SamplePosition(t.position + side * offset, out var hit, 1.5f, NavMesh.AllAreas)) { place = hit.position; return true; }
            }
            return false;
        }

        // Moves the transform onto the NavMesh first: DeattachFromObject re-enables the agent at once.
        private void Detach(Vector3 place)
        {
            if (!attached) return;
            if (anchorComponent != null) anchorComponent.Target = null;
            player.transform.position = place;
            player.DeattachFromObject(NavigationBlocker.Taxi);
            attached = false;
            player.Character.WarpSafely(place);
        }

        private void ReleaseComplete()
        {
            if (kind == Kind.OwnCar) return;
            if (kind == Kind.PrivateDriver)
            {
                var mine = OwnsCar;
                // As after a vanilla ride, the driver leaves the phone; non-instant, so the car drives off.
                if (taxi is PrivateDriverVehicle pd && SmartphonePrivateDriverUI.CurrentVehicle == pd)
                    InstanceBehavior<UIs>.Instance.smartphoneUI.DismissPrivateDriver(true, null, false);
                // DriveAway restores the state saved when the player called the driver, a stop, so it stayed put
                // (spike 13); vanilla never shows this because it removes the car. Send it on.
                if (mine && car != null && car.gameObject.activeInHierarchy && car.GetIndex() == carIndex)
                {
                    car.presetPath = null;
                    Go();
                }
                return;
            }
            if (OwnsCar) ReleaseToTraffic();
        }

        private void ReleaseToTraffic()
        {
            car.presetPath = null;
            Go();
        }

        private void ReleaseVanilla()
        {
            if (kind == Kind.OwnCar) return;
            var y = player.transform.position.y;
            var leave = TrafficManager.Instance.GetClosestWaypoint(player.transform.position, 50f, w => IsValidForLeaving(w, y), prefab);
            var ours = OwnsCar;
            switch (kind)
            {
                case Kind.Taxi:
                    if (!ours) break;
                    if (leave != null) { car.presetPath = null; taxi!.InstantiateVehicle(leave); }
                    else ReleaseToTraffic();
                    break;
                case Kind.PrivateDriver:
                    if (ours && leave != null) { car.presetPath = null; taxi!.InstantiateVehicle(leave); }
                    else if (taxi is PrivateDriverVehicle pd && SmartphonePrivateDriverUI.CurrentVehicle == pd)
                        InstanceBehavior<UIs>.Instance.smartphoneUI.DismissPrivateDriver(true, null, true);
                    break;
                case Kind.Stand:
                    if (ours) ReleaseToTraffic();
                    if (leave != null && HasInactivePooledCar(prefab)) taxi!.InstantiateVehicle(leave);
                    break;
            }
        }

        // The cameras are Cinemachine types, which the mod doesn't reference (the SDK's package version may differ
        // from the game's), so they are reached by reflection.
        private static readonly FieldInfo? VehicleCameraField = AccessTools.Field(typeof(GameManager), "vehicleCamera");
        private static readonly FieldInfo? PedestrianCameraField = AccessTools.Field(typeof(GameManager), "pedestrianCamera");
        private static readonly MethodInfo? SetCameraMethod = AccessTools.Method(typeof(CameraHelper), "SetCamera");

        private static Transform? CameraTarget(object? camera, string property) =>
            camera == null ? null : AccessTools.Property(camera.GetType(), property)?.GetValue(camera) as Transform;

        private void SetGameCamera(object? camera)
        {
            if (camera == null || SetCameraMethod == null) throw new InvalidOperationException("camera or CameraHelper.SetCamera not found");
            SetCameraMethod.Invoke(null, new[] { camera });
        }

        private void SwitchCamera()
        {
            try
            {
                // The pedestrian camera, which follows the player riding along on the anchor: it zooms (wheel) and turns
                // (right mouse button), so a passenger can look around; the vehicle camera does neither (Peter).
                SetGameCamera(PedestrianCameraField?.GetValue(manager));
                cameraSet = true;
            }
            catch (Exception ex) { Log.Error("Camera: " + ex.Message); }
        }

        private void RestoreCamera()
        {
            if (!cameraSet) return;
            cameraSet = false;
            if (manager == null) return;
            // Back in the own car, its own camera.
            if (kind == Kind.OwnCar && ownCar != null && ownCar.controlledByPlayer)
            {
                try { ownCar.UpdateCamera(); } catch (Exception ex) { Log.Error("Camera restore: " + ex.Message); }
                return;
            }
            try { SetGameCamera(PedestrianCameraField?.GetValue(manager)); }
            catch (Exception ex) { Log.Error("Camera restore: " + ex.Message); }
        }

        private void ApplyScale(bool want)
        {
            var speed = InstanceBehavior<UIs>.Instance.gameSpeed;
            if (want)
            {
                if (speed.Paused || TimeMachineRunning || TweenActive(speed)) return;
                if (Mathf.Approximately(rideSpeed, 1f)) { RestoreScale(); return; }
                if (!Mathf.Approximately(Time.timeScale, rideSpeed)) Time.timeScale = rideSpeed;
                scaleSet = true;
            }
            else RestoreScale();
        }

        private void RestoreScale()
        {
            if (!scaleSet) return;
            if (InstanceBehavior<UIs>.Instance == null || InstanceBehavior<UIs>.Instance.gameSpeed == null) { Time.timeScale = 1f; scaleSet = false; return; }
            // While paused the game's own tween owns the scale and sets it back on unpause.
            if (InstanceBehavior<UIs>.Instance.gameSpeed.Paused) return;
            Time.timeScale = 1f;
            scaleSet = false;
        }

        private static bool TweenActive(GameSpeedController speed) =>
            TimeScaleTweenField?.GetValue(speed) is Tween tween && tween.IsActive() && tween.IsPlaying();

        private void Finish()
        {
            if (finished) return;
            finished = true;
            phase = Phase.Done;
            DropState();
            Log.Info("Ride finished.");
        }

        // Clears the mod's own objects and static state; TaxiSystem's field only while this ride owns it.
        private void DropState()
        {
            if (arcAlong >= 0f && TrafficManager.HasInstance) EndArc();
            if (steeringWas >= 0f)
            {
                try
                {
                    if (TrafficManager.HasInstance && SteeringStepField?.GetValue(TrafficManager.Instance) is NativeArray<float> steering && steering.IsCreated && carIndex < steering.Length)
                        steering[carIndex] = steeringWas;
                    if (TrafficManager.HasInstance && WheelDistanceField?.GetValue(TrafficManager.Instance) is NativeArray<float> wheels && wheels.IsCreated && carIndex < wheels.Length && wheelDistanceWas > 0f)
                        wheels[carIndex] = wheelDistanceWas;
                    if (TrafficManager.HasInstance && MaxSteerField?.GetValue(TrafficManager.Instance) is NativeArray<float> lockArray && lockArray.IsCreated && carIndex < lockArray.Length && maxSteerWas > 0f)
                        lockArray[carIndex] = maxSteerWas;
                }
                catch (Exception ex) { Log.Error("Steering restore: " + ex.Message); }
                steeringWas = -1f;
            }
            if (probe != null) Object.Destroy(probe);
            probe = null;
            foreach (var source in muted) if (source != null) source.mute = false;
            muted.Clear();
            trace.Save(TraceFile);
            if (anchor != null)
            {
                // Destroying the anchor destroys its children; a player still under it must never go with it.
                for (var i = anchor.transform.childCount - 1; i >= 0; i--) anchor.transform.GetChild(i).SetParent(null, true);
                Object.Destroy(anchor);
            }
            anchor = null;
            if (Active == this)
            {
                if (taxiSystem != null) TravelField?.SetValue(taxiSystem, null);
                Active = null;
            }
        }

        // Hospital, unload, a foreign time machine, or an iterator that stopped early: drop the mod's changes now,
        // synchronously, through the captured owners, before the interrupting system goes on. No completion.
        // handedOver: the hospital or an unload takes the screen and the player from here, so no unfade.
        public void Cancel(string reason, bool handedOver = false)
        {
            if (finished || cancelled) return;
            cancelled = true;
            Log.Info("Ride cancelled: " + reason + ".");
            replanJob = null;
            if (attached && player != null)
            {
                try
                {
                    var here = player.transform.position;
                    // An own-car ride leaves the player by their car, which stays where it was.
                    var place = kind == Kind.OwnCar ? (ownCarPlaced ? BesideOwnCar() : pickupPosition)
                        : NavMesh.SamplePosition(here, out var hit, 10f, NavMesh.AllAreas) ? hit.position : pickupPosition;
                    Detach(place);
                }
                catch (Exception ex) { Log.Error("Cancel, detach: " + ex); }
                try { player.Character.ToggleVisibility(true); }
                catch (Exception ex) { Log.Error("Cancel, visibility: " + ex.Message); }
            }
            try { RestoreCamera(); } catch (Exception ex) { Log.Error("Cancel, camera: " + ex.Message); }
            try { RestoreScale(); } catch (Exception ex) { Log.Error("Cancel, time scale: " + ex.Message); }
            // A loaded car was held before departure, so it is released even if the ride never left.
            if (kind == Kind.OwnCar)
            {
                RemoveStandIn();
                try
                {
                    if (ownCar != null && !ownCarEntered)
                    {
                        OwnCarTaxi.Show(ownCar);
                        // Back at the start, parked cars may have filled the gap: cleared as vanilla does after a load.
                        if (!ownCarPlaced) VehicleHelper.DestroyBlockingVehicles(ownCar.gameObject, ownCar.vehicleType, onlyParkedVehicles: true);
                    }
                }
                catch (Exception ex) { Log.Error("Cancel, own car: " + ex.Message); }
                try { if (!mapClosed && CityMap.IsOpen) InstanceBehavior<CityManager>.Instance.cityMap.Close(); }
                catch (Exception ex) { Log.Error("Cancel, map: " + ex.Message); }
                // Before departure the map's close may have put the car camera back on the hidden car.
                try { if (ownCar != null && !ownCar.controlledByPlayer && manager != null && !handedOver) SetGameCamera(PedestrianCameraField?.GetValue(manager)); }
                catch (Exception ex) { Log.Error("Cancel, camera: " + ex.Message); }
                // Detached but not yet back in the car: the figure is still hidden.
                try { if (!ownCarEntered && !attached && boarded && player != null) player.Character.ToggleVisibility(true); }
                catch (Exception ex) { Log.Error("Cancel, visibility: " + ex.Message); }
            }
            else try { if (OwnsCar && (departed || onFirstWaypoint)) { if (kind == Kind.Test) TrafficManager.Instance.RemoveVehicle(carIndex); else ReleaseToTraffic(); } } catch (Exception ex) { Log.Error("Cancel, car: " + ex.Message); }
            if (faded && !handedOver)
            {
                faded = false;
                try
                {
                    // The ride's fade may still be running; left alone it would finish after the unfade and stay black.
                    DOTween.Kill(InstanceBehavior<UIs>.Instance.blackOverlay);
                    RideHost.Run(UiFader.UnFade());
                }
                catch (Exception ex) { Log.Error("Cancel, unfade: " + ex.Message); }
            }
            finished = true;
            phase = Phase.Done;
            DropState();
        }

        // The save changed under the ride without an unload event. A new save doesn't prove the old objects are gone:
        // if the captured player survives, cancel through the captured owners; otherwise only drop the mod's state,
        // unparenting anything still under the anchor so destroying it can't take a game object along.
        private void Dispose()
        {
            if (finished || cancelled) return;
            // The game helpers Cancel uses reach the current GameManager; only safe while it is still the ride's.
            if (player != null && manager != null && InstanceBehavior<GameManager>.Instance == manager) { Cancel("the save changed"); return; }
            cancelled = true;
            finished = true;
            Log.Info("Ride disposed: the save changed and its owners are gone or replaced.");
            if (anchor != null)
            {
                for (var i = anchor.transform.childCount - 1; i >= 0; i--) anchor.transform.GetChild(i).SetParent(null, true);
                Object.Destroy(anchor);
                anchor = null;
            }
            if (Active == this) Active = null;
        }

        public static void CancelActive(string reason, bool handedOver = false) => Active?.Cancel(reason, handedOver);

        // During a ride the clock runs at the options' rate, 0 to 100 %, a third by default (Peter, 11 and 12 October
        // 2026). Ticks are game minutes from scaled time, so at 33 % and 3x ride speed the clock runs at about its
        // normal rate. The ride's own time machine (Skip) is left alone.

        public static void CapTick(ref float minutes)
        {
            // A test session holds the clock (docs/turn-tests.md); a time machine that slips past its guards still ends.
            if (TurnTests.Running) { if (!TimeMachineRunning) minutes = 0f; return; }
            var ride = Active;
            if (ride == null || ride.finished || ride.cancelled) return;
            if (ride.SaveChanged) { ride.Dispose(); return; }
            // Back in the own car (autopark may still be fading): the trip is over for the clock.
            if (ride.ownCarEntered) return;
            if (!TimeMachineRunning) minutes *= Options.ClockFactor;
        }

        // Vanilla's charge for the straight line from the car to the destination (TaxiSystem.GetTravelDurationMinutes).
        private double VanillaMinutesLeft()
        {
            var from = car != null && car.gameObject.activeInHierarchy ? car.transform.position : player.transform.position;
            var to = target != null ? target.GetNavMeshTargetPosition() : from;
            return Vector3.Distance(from, to) * 0.04 * PlayerPrefSettings.GameSpeed * timeMultiplier;
        }

        public float RideSpeed => rideSpeed;

        // Turn tests at an automatic speed set it every frame, quietly.
        public void SetTestSpeed(float speed) => rideSpeed = speed;

        // The panel's speed buttons; the choice is remembered for the next ride.
        public void SetSpeed(float speed)
        {
            rideSpeed = speed;
            Options.RememberSpeed(speed);
            Log.Info($"Ride speed {speed:0}x.");
        }


        // The StartTimeMachine prefix: a time machine the ride didn't start cancels it first.
        public static void OnTimeMachineStarting()
        {
            TurnTests.OnForeignTimeMachine();
            var ride = Active;
            if (ride != null && !ride.ownTimeMachine) ride.Cancel("another time machine started");
        }

        // Debug keys.
        public static void DebugRemoveCar()
        {
            var ride = Active;
            if (ride == null) { Notice.Show("No ride."); return; }
            TrafficManager.Instance.RemoveVehicle(ride.carIndex);
            Log.Info("Debug: ride car removed.");
        }

        public static void DebugThrow()
        {
            if (Active == null) { Notice.Show("No ride."); return; }
            Active.throwNext = true;
        }

        public static void DebugFakeSaveChange()
        {
            if (Active == null) { Notice.Show("No ride."); return; }
            Active.fakeSaveChange = true;
            Log.Info("Debug: the ride's save now differs from the current one.");
        }

        public static void DebugClear()
        {
            Active?.Cancel("debug clear");
            var system = InstanceBehavior<TaxiSystem>.Instance;
            if (system != null && TravelField?.GetValue(system) != null)
            {
                TravelField.SetValue(system, null);
                Log.Info("Debug: TaxiSystem's travel coroutine field cleared.");
            }
        }
    }
}
