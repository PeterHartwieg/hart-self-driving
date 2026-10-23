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
    internal sealed partial class Ride
    {
        private enum Kind { Taxi, Stand, PrivateDriver, OwnCar, Test }
        private enum Phase { Boarding, Unparking, Driving, Bridging, Stopping, Parking, Finishing, Done }
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
        // An NWH ride (docs/implementation-plan.md, phase 5): the car driven by its own physics (NwhRideCar, the seam
        // for every kind of ride car) and its driver, the same NwhDrive the rig drives with. `car` is then only the
        // traffic car the ride was called with, taken away at boarding. Null: a Gley ride.
        private NwhRideCar? nwh;
        private NwhDrive? nwhDrive;
        private bool nwhReleased;
        private bool needsUnpark, skipParking;
        private List<DepartureEntry>? departureEntries;
        public bool VisibleParking => outcome == Outcome.Complete && !skipParking;
        public bool ParkingSkipped => skipParking;
        internal bool ParkingApproach { get; set; }
        public string? PanelStatus => phase == Phase.Unparking ? "drivenrides:panel_unparking"
            : phase == Phase.Parking ? "drivenrides:panel_parking" : null;
        // The car the player sees and rides in.
        private Transform Body => nwh != null && nwh.Car.Root != null ? nwh.Car.Root.transform : car.transform;
        private bool PlayerInside => nwh != null && nwh.PlayerInside;

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
        private RideRadio? radio;
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

        public bool ShowPanel => phase == Phase.Driving || phase == Phase.Stopping || phase == Phase.Unparking || phase == Phase.Parking;
        // What the ride panel shows as the distance left, updated while the ride drives or stops.
        public double PanelLeftMetres { get; private set; }

        // The car Gley must leave alone: PrivateDriverVehicle.Update is skipped for it.
        public static bool OwnsCarObject(GameObject car) => Active != null && !Active.finished && Active.nwh == null && Active.OwnsCar && Active.car.gameObject == car;
        // The ride drives a Gley car: the patches on Gley's driving touch only that car (an NWH ride's traffic car is
        // gone, and Gley may have given its index to another).
        private bool GleyDriven => nwh == null;
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
            // The own car has no traffic car: `car` is then a prefab asset (no scene), whose stored index belongs to no
            // car in the pool, so -1, which no Gley call can reach.
            carIndex = car.gameObject.scene.IsValid() ? car.GetIndex() : -1;
            this.prefab = prefab;
            this.vehicleType = vehicleType;
            this.goals = goals;
            this.route = route;
            carLength = LengthOf(prefab);
            carSteering = new Steering(PooledCopy(prefab));
            nwhPlanner = kind == Kind.Test ? null : planNwh;
            // The plan PlanRoute checked for this route is driven as it is (Expand).
            nwhTaken = nwhPlanner != null ? planNwhPlan : null;
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

        private const string SwitchedOff = "switched off in the options";
        private const string NoFuel = "the tank is empty";
        private const string OwnNoRoute = "no route there from the lane the car stands in";

        private static string lastVanillaReason = "";

        // The length of the car a route is planned for: manoeuvres that failed in turn tests for cars this long are
        // left out (TestedSteps).
        private static float planLength;
        private static Steering planSteering;
        // A ride car driven by its own physics (NWH) is planned with its drivable prefab's shape and Core's feasibility
        // check instead of the Gley model (NwhPlanner); TestedSteps is then a record, not a filter.
        private static NwhPlanner? planNwh;
        private static DrivePlan? planNwhPlan;
        private static NwhCar? spawnedNwh;
        private readonly NwhPlanner? nwhPlanner;
        private float carLength;
        private Steering carSteering;

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
                    Log.Trace($"Steering for {copy.name}: pivot {Pivot:0.00} m ahead of the origin, wheel distance {Shape.WheelDistance:0.00} m, lock {Shape.MaxSteerDegrees:0.0} ({(drivable != null ? "its drivable version" : "Gley's, held to " + FallbackRadius + " m")}; Gley {copy.wheelDistance:0.00} m, {copy.maxSteer:0.0}), circle {MinRadius:0.0} m, front axle {Shape.FrontAxle:0.00} m ahead, centre of mass {Shape.CentreAhead:0.00} m ahead and {Shape.CentreHeight:0.00} m up, body {BodyHalf * 2f} at {BodyCentre}.");
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

        // How far ahead of the car's pivot, along a turn's arc, the ride puts Gley's target: its front axle and this
        // much more. The modelled 6.7 m car kept its front axle, middle and rear axle within 0.3 m of every catalogue
        // arc with it (TurnDrive.Pursue); a fixed 3.5 m from the pivot put the limo's target behind its front axle and
        // its wheel flicked from side to side (Peter, turn test 0.1.47).
        private const float ArcLead = 1.1f;

        private static readonly FieldInfo? PhoneDriverField = AccessTools.Field(typeof(SmartphonePrivateDriverUI), "_privateDriverVehicle");

        private static int IndexOf(IReadOnlyList<int> list, int value)
        {
            for (var k = 0; k < list.Count; k++) if (list[k] == value) return k;
            return -1;
        }

        private static double NowMinutes()
        {
            var game = SaveGameManager.Current;
            return game == null ? 0 : game.Day * 1440.0 + game.Hour * 60.0 + game.Minute;
        }

        private static bool TimeMachineRunning => InstanceBehavior<UIs>.Instance.timeMachine.isRunning;

        private double LeftMetres => progress?.LeftMetres ?? route.LengthMetres;

        internal bool OwnsCar => nwh != null
            ? !nwhReleased && nwh.Car.Root != null && nwh.Car.Root.activeInHierarchy
            : car != null && car.gameObject.activeInHierarchy && car.GetIndex() == carIndex &&
              (!departed || ReferenceEquals(car.presetPath, owned));

        private bool SaveChanged => fakeSaveChange || !ReferenceEquals(SaveGameManager.Current, save);

        // The ride's iterator. The finally block runs on normal exits and exceptions; a game unload abandons the
        // iterator without running it, so OnGameUnloaded cancels the ride itself.
        private IEnumerator Run()
        {
            try
            {
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
                if (outcome == Outcome.None && needsUnpark && nwh != null && nwhPlan != null)
                {
                    phase = Phase.Unparking;
                    var result = new ManoeuvreResult();
                    var unpark = Flatten(departureEntries != null && nwhPlanner != null
                        ? OwnCarParking.Depart(nwh.Car, nwhPlanner, departureEntries, goals, vehicleType,
                            () => cancelled || SaveChanged || outcome != Outcome.None, result, (newRoute, newPlan) =>
                            { route = newRoute; nwhTaken = newPlan; path = Expand(route); })
                        : OwnCarParking.Unpark(nwh.Car, nwh.Spec, nwhPlan, () => cancelled || SaveChanged || outcome != Outcome.None, result));
                    while (unpark.MoveNext()) { yield return unpark.Current; if (cancelled) yield break; }
                    if (cancelled) yield break;
                    if (outcome == Outcome.None)
                    {
                        var owner = OwnCarDrive.Car;
                        var refusal = owner != null && nwhPlanner != null
                            ? OwnCarTaxi.StartRefusal(owner, nwhPlanner, nwhPlan, GleyGraph.Graph!, 0f) : "the car is gone";
                        if (!result.Success || refusal != null)
                        {
                            Notice.Text("drivenrides:notice_unpark_failed");
                            Cancel("unparking: " + (result.Success ? refusal : result.Reason)); yield break;
                        }
                    }
                }
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
                farDrop = !PlayerInside && outcome == Outcome.Complete && target != null && OwnsCar && Vector3.Distance(Body.position, target.GetNavMeshTargetPosition()) > FarDropMetres;
                // The car stands while the player gets out (the drive has stopped it on arrival; Skip stops it here).
                nwhDrive?.Stop(outcomeReason);
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
                if (nwh != null) ReleaseNwh(outcomeReason, swap: true);
                else if (outcome == Outcome.Complete) ReleaseComplete();
                else ReleaseVanilla();
                RestoreCamera();
                if (faded)
                {
                    var unfade = UiFader.UnFade();
                    while (unfade.MoveNext()) yield return unfade.Current;
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

        private void Board()
        {
            pickupPosition = player.transform.position;
            pickupRotation = player.transform.rotation;
            if (nwh != null) HandOverToNwh();
            if (PlayerInside)
            {
                // The player stays in the driver's seat with the car's own camera (NwhRideCar.PlayerInside).
                InstanceBehavior<CityManager>.Instance.cityMap.Close();
                Log.Trace($"Boarded inside the car at {pickupPosition}.");
                return;
            }
            player.ResetNavigation();
            anchor = new GameObject("DrivenRides.Anchor");
            anchor.transform.SetPositionAndRotation(Body.position, Body.rotation);
            anchor.AddComponent<RideAnchor>().Target = Body;
            anchorComponent = anchor.GetComponent<RideAnchor>();
            anchorComponent.Valid = () => OwnsCar;
            // Set first, so cleanup also undoes a partly finished attachment.
            attached = true;
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
            if (kind == Kind.Taxi || kind == Kind.Stand || kind == Kind.PrivateDriver)
                radio = RideRadio.Begin(manager, save);
            Log.Trace($"Boarded at {pickupPosition}.");
        }

        public static void OnCityMapClosed()
        {
            if (Active != null) Active.mapClosed = true;
        }

        // ---- NWH rides (docs/implementation-plan.md, phase 5) ----

        // Boarding an NWH ride: the traffic car the ride was called with goes back to Gley's pool while the map still
        // covers the screen; the NWH car stands where it stood. A private driver's car takes its PrivateDriverVehicle
        // with it, which takes the driver off the phone, as a vanilla ride does at its end.
        private void HandOverToNwh()
        {
            if (nwh == null || nwh.PlayerInside || car == null) return;
            try
            {
                if (car.gameObject.activeInHierarchy && car.GetIndex() == carIndex) TrafficManager.Instance.RemoveVehicle(carIndex);
                Log.Trace($"NWH ride: traffic car {carIndex} handed back to the pool; {NwhCar.Id(nwh.Car.TypeName)} drives.");
            }
            catch (Exception ex) { Log.Error("NWH ride, traffic car: " + ex.Message); }
        }

        // Watchdog limits for an NWH ride: the driver handles its own stops (lights, traffic, blocks and their
        // replans), so there is no restart. No 5 m of progress for 20 s (unscaled, pauses left out) gives up the step
        // ahead, and the planner plans round it; no 5 m in the 30 s after that ends the ride the vanilla way. The
        // ride deadline stays three times the planned driving time plus a minute. The drive's own stuck limit is 30 s
        // of game time.
        private static readonly WatchLimits NwhWatchLimits = new WatchLimits { RestartAfterSeconds = 1e6, ReplanAfterSeconds = 20 };

        private void DepartNwh()
        {
            if (!OwnsCar) { EndWith(Outcome.Vanilla, "the car was gone before departure"); return; }
            if (nwhPlan == null) { EndWith(Outcome.Vanilla, "no checked plan to drive"); return; }
            var n = nwh!;
            var drive = n.Car.Root.AddComponent<NwhDrive>();
            // The traffic cost records and the slowdown dumps are for test sessions only (TestMode).
            drive.MeasureCost = TestMode.On;
            drive.SlowdownDumps = TestMode.On;
            // The planner's way on, as the rig's ride cases have it: round a blocked step, the look-ahead, taking up.
            drive.WayOn.Replan = NwhBlocked;
            drive.WayOn.LookAhead = NwhLookAhead;
            drive.WayOn.Take = NwhTake;
            drive.Begin(n.Car, nwhPlan, n.Spec, nwhPlan.TopKmh > 0 ? nwhPlan.TopKmh : TopSpeed);
            nwhDrive = drive;
            departed = true;
            progress = new RouteProgress(Points(path));
            IndexRoute();
            watch = new RideWatch(unpausedSeconds, route.DriveSeconds, NwhWatchLimits);
            if (!PlayerInside) SwitchCamera();
            phase = Phase.Driving;
            Log.Trace($"Departed (NWH): {path.Count} waypoints, {route.LengthMetres:0} m, top {(nwhPlan.TopKmh > 0 ? nwhPlan.TopKmh : TopSpeed):0} km/h, light cycle {GreenLightTime()} s.");
        }

        private void TickNwh()
        {
            // The own car out of fuel or wrecked mid-trip: the trip ends where the car stopped.
            if (kind == Kind.OwnCar && OwnCarDrive.Trouble() is string trouble) { Notice.Text(OwnCarDrive.TroubleKey(trouble)); Cancel(trouble.ToLowerInvariant()); return; }
            var drive = nwhDrive!;
            var body = Body;
            progress!.Update(GleyGraph.ToVec(body.position));
            var along = alongOffset + progress.AlongMetres;
            var speed = nwh!.Car.Body.velocity.magnitude;
            trace.Sample(body.position, body.eulerAngles.y, speed, -1, drive.WaitingNow ? "waiting" : "driving", along, progress.LeftMetres, path.Count);
            lastAlong = along;
            if (phase == Phase.Driving)
            {
                if (drive.Ended)
                {
                    if (drive.Arrived) EndWith(Outcome.Complete, "arrived");
                    else EndWith(Outcome.Vanilla, "NWH drive: " + drive.EndReason);
                }
                else if (OnBridge()) return;
                else
                {
                    // The watchdog on progress along the route: waiting at a light or in traffic isn't stalling.
                    if (drive.WaitingNow) watch!.Waiting(unpausedSeconds);
                    switch (watch!.Update(unpausedSeconds, along))
                    {
                        case WatchVerdict.Restart: watch.Restarted(unpausedSeconds); break;
                        case WatchVerdict.Replan:
                            // Stalled: the step ahead is given up as a blocked one and the planner plans round it, as
                            // for any block the drive finds itself.
                            Event("Watchdog: " + watch.LastReason + (drive.GiveUpStep("watchdog: " + watch.LastReason)
                                ? "; giving up the step ahead and planning round it." : "; the drive is replanning already."));
                            watch.Replanned(unpausedSeconds);
                            break;
                        // Still stuck: the vanilla finish (fade, the rest of the way charged, the door).
                        case WatchVerdict.GiveUp: EndWith(Outcome.Vanilla, "watchdog: " + watch.LastReason); break;
                    }
                }
            }
            ApplyScale(phase == Phase.Driving);
            PanelLeftMetres = LeftMetres;
        }

        // The end of an NWH ride, once (every exit and a cancel come here): the drive stops, the seam's End runs, and
        // the mod's own car goes. With `swap` (a finish, not a cancel), a traffic car of the ride's model takes its
        // place on the lane and drives on, as the car does after a vanilla ride; the player has got out already.
        private void ReleaseNwh(string reason, bool swap)
        {
            if (nwh == null || nwhReleased) return;
            nwhReleased = true;
            try { nwhDrive?.Stop(reason); } catch (Exception ex) { Log.Error("NWH release, drive: " + ex.Message); }
            if (nwhDrive != null && nwhDrive.DecideCost != null && nwhDrive.DecideCost.Total > 0) Log.Trace(nwhDrive.CostLine());
            if (nwhDrive != null) Log.Trace($"NWH ride tally: {nwhDrive.Contacts} contacts with traffic cars ({nwhDrive.ContactsCaused} caused by the ride car), {nwhDrive.RedRuns} red runs, {nwhDrive.Stalls} standstills over 30 s.");
            LastTally = nwhDrive != null ? (nwhDrive.Contacts, nwhDrive.ContactsCaused, nwhDrive.RedRuns, nwhDrive.Stalls) : default;
            try { nwh.End?.Invoke(reason); } catch (Exception ex) { Log.Error("NWH release, end: " + ex.Message); }
            if (!nwh.Owned) return;
            var root = nwh.Car.Root;
            if (root == null) return;
            if (swap)
            {
                try { TrafficTakesOver(root.transform); }
                catch (Exception ex) { Log.Error("NWH release, traffic car: " + ex.Message); }
            }
            Object.Destroy(root);
        }

        // A traffic car of the ride's model on the lane where the NWH car stands, in its pose, if Gley has one free.
        private void TrafficTakesOver(Transform body)
        {
            var manager = TrafficManager.Instance;
            var at = body.position;
            var forward = body.forward;
            var y = at.y;
            var leave = WithCollidersOff(body, () => manager.GetClosestWaypoint(at, 15f,
                w => IsValidForLeaving(w, y) && Vector3.Dot(manager.GetWaypointOrientation(w) * Vector3.forward, forward) > 0.5f && Vector3.Dot(w.position - at, forward) > 0f, prefab));
            if (leave == null || !HasInactivePooledCar(prefab)) { Log.Trace("NWH ride: no free lane or traffic car to take over; the car just goes."); return; }
            var middle = NwhRides.BodyMiddle(nwh!.Car);
            // Out of the way first: the traffic car is loaded where the ride car's colliders still are.
            foreach (var c in body.GetComponentsInChildren<Collider>()) c.enabled = false;
            var loaded = manager.LoadVehicle(prefab.gameObject, leave, true);
            if (loaded == null) { Log.Trace("NWH ride: the traffic car could not be loaded; the car just goes."); return; }
            TestCar.BodyBox(loaded, out var centre, out _);
            var facing = Quaternion.Euler(0f, body.eulerAngles.y, 0f);
            var position = new Vector3(middle.x, loaded.transform.position.y, middle.z) - facing * new Vector3(centre.x, 0f, centre.z);
            if (loaded.rb != null) { loaded.rb.position = position; loaded.rb.rotation = facing; }
            loaded.transform.SetPositionAndRotation(position, facing);
            if (nwh.Car.Look == null) KeepColour(body.gameObject, loaded.gameObject, keep: false);
            Log.Trace($"NWH ride: traffic car {loaded.GetIndex()} takes the ride car's place, heading for waypoint {leave.listIndex}.");
        }

        // Each car the game spawns gets a random paint (RandomVehicleColor), so the ride's swaps (traffic car to NWH
        // car at boarding, NWH car to traffic car at the end) would change the car's colour twice (Peter, 14 October
        // 2026: the private driver's car came in three colours). The new car takes the old one's paint. With `keep`
        // the new car is the mod's own, and its random paint is switched off so a re-enable can't repaint it.
        private static void KeepColour(GameObject from, GameObject to, bool keep)
        {
            var source = from.GetComponentInChildren<CarFeatures>(true);
            var target = to.GetComponentInChildren<CarFeatures>(true);
            if (source == null || target == null || source.VehicleColor == null)
                throw new InvalidOperationException($"Cannot keep the car's colour: source paint {(source != null && source.VehicleColor != null ? "present" : "missing")}, target CarFeatures {(target != null ? "present" : "missing")}.");
            if (keep) foreach (var random in to.GetComponentsInChildren<RandomVehicleColor>(true)) Object.Destroy(random);
            target.SetColor(source.VehicleColor);
        }

        // How the last ride ended, and the last NWH ride's tally (the rig's ride command records them).
        public static string LastEnd { get; set; } = "";
        public static (int Contacts, int Caused, int RedRuns, int Stalls) LastTally { get; set; }

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
            if (phase == Phase.Unparking || phase == Phase.Parking)
            {
                // Precision manoeuvres run at normal world speed. A road approach to a space uses the ride speed.
                ApplyScale(phase == Phase.Parking && ParkingApproach);
                if (!OwnsCar) { Cancel("the manoeuvring car is gone"); return; }
                if (OwnCarDrive.Trouble() is string trouble) { Notice.Text(OwnCarDrive.TroubleKey(trouble)); Cancel(trouble); }
                return;
            }
            if (phase != Phase.Driving && phase != Phase.Stopping) return;
            RefreshPhoneClock();
            if (!OwnsCar) { EndWith(Outcome.Vanilla, "the car is no longer the ride's"); return; }

            if (throwNext) { throwNext = false; throw new InvalidOperationException("Debug: exception inside the ride's frame update."); }
            if (nwh != null) { TickNwh(); return; }
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
            PanelLeftMetres = LeftMetres;
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

        // Long enough not to keep pushing into a person or car that stopped it (spike 16).
        private const float BumpSeconds = 3f;
        private float stopNowSeconds;
        private float giveWaySeconds;
        private int loggedRed = -1;

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

        // After TrafficManager.FixedUpdate: Gley turns a car about its origin, which on the taxi and the limo is the
        // rear axle, so the ride car pivoted visibly on its back wheels (Peter, turn test 0.1.49). The ride car turns
        // about its centre of mass instead: after the step's rotation (MoveRotation, applied in this physics step) the
        // body is moved by the few centimetres that keep the centre of mass where rotating about it would. Moved, not
        // pushed: added velocity built up through Gley's drive force and threw the car off at 400 m/s (turn test
        // 0.1.50).
        private static readonly FieldInfo? RotationAngleField = AccessTools.Field(typeof(TrafficManager), "vehicleRotationAngle");

        // Gley's float3 array, written by reflection: the mod doesn't reference Unity.Mathematics. The boxed copy
        // shares the native buffer.
        private static readonly FieldInfo? TargetPositionField = AccessTools.Field(typeof(TrafficManager), "vehicleTargetWaypointPosition");
        private static MethodInfo? targetSetter;
        private static ConstructorInfo? float3Constructor;

        // The bridge to the Hamptons is skipped as the game skips it for the player's own car (SkipBridgeHelper):
        // entering a bridge trigger, the car is moved behind a fade to the trigger's far-side target facing its way,
        // and the clock runs the game's own bridge time (Peter).
        private static readonly FieldInfo? SkipTargetsField = AccessTools.Field(typeof(Streets.BridgeTriggerController), "skipTargets");
        private (Transform Target, int PathIndex)? bridgeSkip;
        private readonly HashSet<Streets.BridgeTriggerController> skipped = new HashSet<Streets.BridgeTriggerController>();
        private Streets.BridgeTriggerController[]? bridges;
        private const float MinBridgeSkipMetres = 200f;

        // A route is planned with the world as loaded then; far away (the Hamptons from Manhattan) the props may not
        // be there yet. So each added step is checked again once the car is within 80 m of it.
        private readonly HashSet<(int, int)> checkedNear = new HashSet<(int, int)>();
        private float nextNearCheck;

        public int Restarts { get; private set; }

        // The NWH ride's plan for the present route (its path is `path`), for the NwhDrive that drives it.
        private DrivePlan? nwhPlan;

        // NwhDrive.Replan for an NWH ride (phase 5 hands it to the ride's NwhDrive): the step the car found blocked is
        // blocked for this ride, the rest planned again by the NWH planner from where the car stands, joining its
        // pose (NwhPlanner.PlanRoundBlock), as a job the drive steps; NwhTake takes up its way on.
        internal WayOnJob? NwhBlocked(int position, string what, Func<CarPose> pose)
        {
            var graph = GleyGraph.Graph;
            if (nwhPlanner == null || graph == null) return null;
            return nwhPlanner.PlanRoundBlock(route.Waypoints, path, position, what, pose, goals, vehicleType);
        }

        // NwhDrive.LookAhead for an NWH ride: the planner's look-ahead (lanes loaded since, manoeuvres checked again
        // near, steps ahead blocked now), as a job the drive steps.
        private readonly WayOnPlanner.Watch nwhWatch = new WayOnPlanner.Watch();

        internal WayOnJob? NwhLookAhead(int position, Func<CarPose> pose)
        {
            var graph = GleyGraph.Graph;
            if (nwhPlanner == null || graph == null) return null;
            return nwhPlanner.LookAhead(route.Waypoints, path, position, pose, goals, vehicleType, nwhWatch);
        }

        // NwhDrive.Take for an NWH ride: a job's way on taken as a replan is (progress, waypoint index, watchdog); the
        // new path goes back to the drive, which drives on without stopping. Null: no way on.
        private DrivePlan? nwhTaken;

        internal DrivePlan? NwhTake(WayOnJob job)
        {
            if (job.Found == null) { Event("NWH: " + (job.Note.Length > 0 ? job.Note : "no way on joins the car") + "."); return null; }
            var (plan, next, note) = job.Found.Value;
            Event("NWH: " + note + ".");
            var graph = GleyGraph.Graph!;
            alongOffset += progress!.AlongMetres;
            route = Route.Fixed(graph, next);
            nwhTaken = plan;
            path = Expand(route);
            owned.Clear();
            owned.AddRange(path);
            progress = new RouteProgress(Points(path));
            IndexRoute();
            watch?.Replanned(unpausedSeconds);
            return nwhPlan;
        }

        private void Event(string text)
        {
            Log.Trace(text);
            trace.Note(text);
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
            if (phase == Phase.Parking) { skipParking = true; return; }
            if (phase == Phase.Boarding || phase == Phase.Unparking || phase == Phase.Driving || phase == Phase.Stopping)
                EndWith(Outcome.Vanilla, "skipped");
        }

        private void EndWith(Outcome result, string reason)
        {
            if (outcome != Outcome.None) return;
            outcome = result;
            outcomeReason = reason;
            LastEnd = $"{result}: {reason}";
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

            if (PlayerInside)
            {
                // The seam's arrival (the own car's autopark) instead of placing the player; the car has stopped.
                Log.Trace($"Ride done, the player inside the car: {NowMinutes() - startMinute:0.0} game minutes in all.");
                if (nwh!.Arrive != null)
                {
                    // The arrival may fade (the own car's autopark): a cancel meanwhile must unfade (Cancel, `faded`).
                    var wasFaded = faded;
                    faded = true;
                    phase = Phase.Parking;
                    var arriving = Flatten(nwh.Arrive());
                    while (arriving.MoveNext()) { yield return arriving.Current; if (cancelled) yield break; }
                    phase = Phase.Finishing;
                    faded = wasFaded;
                }
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
                Log.Trace($"Player not on the NavMesh at {place} after one frame; warping there again.");
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
            Log.Trace($"Ride done: {NowMinutes() - startMinute:0.0} game minutes in all.");
        }

        private Vector3 PlacePlayer()
        {
            var destination = target != null ? target.GetNavMeshTargetPosition() : pickupPosition;
            // An arrival puts the player beside the car, kerb side first (Peter); the vanilla finish at the door.
            var place = outcome == Outcome.Complete && !farDrop && BesideCar(out var beside) ? beside
                : NavMesh.SamplePosition(destination, out var hit, 5f, NavMesh.AllAreas) ? hit.position : destination;
            Detach(place, show: true);
            player.Character.Reset();
            return place;
        }

        private bool BesideCar(out Vector3 place)
        {
            place = default;
            if (!OwnsCar) return false;
            var t = Body;
            var offset = Mathf.Max(1f, nwh != null ? nwh.Car.BodyHalf.x : car.colliderWidth / 2f) + 0.8f;
            foreach (var side in new[] { t.right, -t.right })
            {
                if (NavMesh.SamplePosition(t.position + side * offset, out var hit, 1.5f, NavMesh.AllAreas)) { place = hit.position; return true; }
            }
            return false;
        }

        // Moves the transform onto the NavMesh first: DeattachFromObject re-enables the agent and calls ResetPath at
        // once. show: the hidden figure is shown there before that. Hiding (ToggleVisibility(false)) deactivates the
        // character's GameObject, so ResetPath on its still inactive agent logged '"ResetPath" can only be called on
        // an active agent' at every arrival (Peter's private driver ride on 0.1.94, and every Gley ride before).
        private void Detach(Vector3 place, bool show = false)
        {
            if (!attached) return;
            if (anchorComponent != null) anchorComponent.Target = null;
            player.transform.position = place;
            if (show) player.Character.ToggleVisibility(true);
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
            Log.Trace("Ride finished.");
        }

        // Clears the mod's own objects and static state; TaxiSystem's field only while this ride owns it.
        private void DropState()
        {
            radio?.End();
            radio = null;
            if (kind == Kind.OwnCar && nwhPlanner != null) nwhPlanner.Ignore = null;
            if (arcAlong >= 0f && TrafficManager.HasInstance && GleyDriven) EndArc();
            // Whatever way the ride ended, its NWH car never outlives it.
            try { ReleaseNwh("the ride ended", swap: false); } catch (Exception ex) { Log.Error("NWH release: " + ex.Message); }
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
            LastEnd = "Cancelled: " + reason;
            Log.Info("Ride cancelled: " + reason + ".");
            replanJob = null;
            if (attached && player != null)
            {
                try
                {
                    var here = player.transform.position;
                    var place = NavMesh.SamplePosition(here, out var hit, 10f, NavMesh.AllAreas) ? hit.position : pickupPosition;
                    Detach(place, show: true);
                }
                catch (Exception ex) { Log.Error("Cancel, detach: " + ex); }
                // Shown already by the detach; again in case the detach threw before it.
                try { player.Character.ToggleVisibility(true); }
                catch (Exception ex) { Log.Error("Cancel, visibility: " + ex.Message); }
            }
            try { RestoreCamera(); } catch (Exception ex) { Log.Error("Cancel, camera: " + ex.Message); }
            try { RestoreScale(); } catch (Exception ex) { Log.Error("Cancel, time scale: " + ex.Message); }
            // A loaded car was held before departure, so it is released even if the ride never left. An NWH ride's car
            // stops where it is and goes (the own car: its End hands it back where it stands, the player inside).
            if (nwh != null) ReleaseNwh("cancelled: " + reason, swap: false);
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
            radio?.End();
            radio = null;
            Log.Info("Ride disposed: the save changed and its owners are gone or replaced.");
            try { ReleaseNwh("disposed", swap: false); } catch (Exception ex) { Log.Error("NWH release: " + ex.Message); }
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
            if (!TimeMachineRunning) minutes *= Options.ClockFactor;
        }

        // Vanilla's charge for the straight line from the car to the destination (TaxiSystem.GetTravelDurationMinutes).
        private double VanillaMinutesLeft()
        {
            var from = nwh != null ? (OwnsCar ? Body.position : player.transform.position)
                : car != null && car.gameObject.activeInHierarchy ? car.transform.position : player.transform.position;
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
            Log.Trace($"Ride speed {speed:0}x.");
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
            if (ride.nwh != null) { if (ride.nwh.Owned && ride.nwh.Car.Root != null) Object.Destroy(ride.nwh.Car.Root); }
            else TrafficManager.Instance.RemoveVehicle(ride.carIndex);
            Log.Trace("Debug: ride car removed.");
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
            Log.Trace("Debug: the ride's save now differs from the current one.");
        }

        public static void DebugClear()
        {
            Active?.Cancel("debug clear");
            var system = InstanceBehavior<TaxiSystem>.Instance;
            if (system != null && TravelField?.GetValue(system) != null)
            {
                TravelField.SetValue(system, null);
                Log.Trace("Debug: TaxiSystem's travel coroutine field cleared.");
            }
        }
    }
}
