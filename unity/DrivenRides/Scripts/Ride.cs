#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using BigAmbitions.DayNightCycle;
using Buildings;
using DG.Tweening;
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
        private enum Kind { Taxi, Stand, PrivateDriver }
        private enum Phase { Boarding, Driving, Stopping, Finishing, Done }
        private enum Outcome { None, Complete, Vanilla }

        private const string Hamptons = "ba:neighborhood_thehamptons";
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
        private static readonly FieldInfo? DestinationDataField = AccessTools.Field(typeof(TaxiDestinationController), "taxiDestinationData");

        public static Ride? Active { get; private set; }

        // Captured when the prefix accepts the ride; nothing needed for finishing lives only on a pooled component.
        private readonly TaxiSystem taxiSystem;
        private readonly ITaxi taxi;
        private readonly Kind kind;
        private readonly EntityController target;
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

        private Route route;
        // The route as Gley drives it: the planner's waypoints with each added turn's curve waypoint put in.
        private List<int> path;
        private RouteProgress? progress;
        private readonly RideTrace trace = new RideTrace();
        private CollisionProbe? probe;
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
        public static Vector3 PickupPosition => Active!.pickupPosition;
        public static Quaternion PickupRotation => Active!.pickupRotation;

        private Ride(TaxiSystem taxiSystem, ITaxi taxi, Kind kind, EntityController target, float minutes, VehicleComponent car,
            VehicleComponent prefab, int vehicleType, List<Goal> goals, Route route)
        {
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
            path = Expand(route);
            save = SaveGameManager.Current;
            manager = InstanceBehavior<GameManager>.Instance;
            player = manager.playerController;
            happinessModifier = taxi.GetHappinessModifierName() ?? "";
            driverVehicleType = taxi is PrivateDriverVehicle pd ? pd.vehicleInstance?.vehicleTypeName : null;
            driverTopSpeed = taxi is PrivateDriverVehicle driver ? driver.vehicleInstance?.VehicleType?.maxSpeed ?? 0 : 0;
            startMinute = NowMinutes();
            timeMultiplier = taxi.GetTimeMultiplier();
            rideSpeed = Options.RideSpeed;
        }

        // The TravelCoroutine prefix. Returns the ride's iterator, or null for a vanilla ride. Vanilla's TravelTo has
        // already checked and charged; until a stand's car is loaded nothing here changes the game, and the ride's
        // static state is committed only as the last step.
        public static IEnumerator? TryCreate(TaxiSystem taxiSystem, EntityController target, float minutes)
        {
            VehicleComponent? loaded = null;
            try
            {
                if (!Options.DrivenRides) return null;
                if (Active != null) return Vanilla("a ride is still active");
                var taxi = InstanceBehavior<CityManager>.Instance.cityMap.Taxi;
                if (!(taxi is MonoBehaviour taxiObject) || taxiObject == null) return Vanilla("no taxi");
                if (PlayerActivityUI.IsPanelOpen) return Vanilla("an activity panel is open");
                if (BuildingManager.IsInsideBuilding) return Vanilla("the player is inside a building");
                if (IsHamptons(target)) return Vanilla("the destination is in the Hamptons");
                if (ClosestBuildingFromPlayer.Get()?.Neighbourhood == Hamptons) return Vanilla("the player is in the Hamptons");

                Kind kind;
                VehicleComponent? car = null;
                VehicleComponent? prefab;
                switch (taxi)
                {
                    case PrivateDriverVehicle pd:
                        kind = Kind.PrivateDriver;
                        if (pd.GetVehicleIndex() < 0) return Vanilla("the private driver is a parked car");
                        car = pd.GetComponent<VehicleComponent>();
                        prefab = car != null && car.prefab != null ? car.prefab.GetComponent<VehicleComponent>() : null;
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
                    default:
                        return Vanilla("unknown taxi " + taxi.GetType().Name);
                }
                if (kind != Kind.Stand && (car == null || car.GetIndex() < 0)) return Vanilla("the taxi has no traffic car");
                if (prefab == null) return Vanilla("no vehicle prefab");

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
                else
                {
                    var next = manager.GetTargetWaypoint(car!.GetIndex());
                    if (next == null) return Vanilla("the taxi has no target waypoint");
                    start = next.listIndex;
                }

                var destination = target.GetNavMeshTargetPosition();
                // First the kerb on the destination's side of the street (a driver lets you out on your side), then
                // any kerb if that side has no route.
                var destinationPoint = GleyGraph.ToVec(destination);
                var goals = GoalPicker.Pick(graph, destinationPoint, vehicleType, new GoalOptions { KerbLane = true, TargetOnRight = true });
                long planMs = 0;
                var why = "no goal on the destination's side";
                Route? route = null;
                if (goals.Count > 0) route = Plan(graph, start, goals, vehicleType, out planMs, out why);
                if (route == null)
                {
                    goals = GoalPicker.Pick(graph, destinationPoint, vehicleType, new GoalOptions { KerbLane = true });
                    if (goals.Count == 0) return Vanilla("no lane waypoint near the destination");
                    route = Plan(graph, start, goals, vehicleType, out planMs, out why);
                    if (route != null) Log.Info("No route to the destination's side of the street; dropping off on the other side.");
                }
                if (route == null && kind != Kind.Stand)
                {
                    // The summoned driver stops at the waypoint nearest the player, whatever kind of lane it is; some are
                    // pockets with no lane out. Then the route starts from a proper traffic lane nearby, and Gley steers
                    // the car straight to it.
                    Log.Info("No route from the car's waypoint: " + GleyGraph.Diagnose(graph, start, goals, vehicleType));
                    route = PlanFromNearby(graph, car!.transform, start, goals, vehicleType, ref planMs);
                    if (route != null) Log.Info($"Start bridged: from waypoint {start} to {route.Waypoints[0]}, {Vector3.Distance(car.transform.position, GleyGraph.Waypoints![route.Waypoints[0]].position):0} m from the car.");
                }
                if (route == null) return Vanilla($"no route ({why}, {planMs} ms)");

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

                GleyGraph.DumpRide(start, goals, route.Waypoints, destination);
                var addedTurns = 0;
                for (var k = 0; k + 1 < route.Waypoints.Count; k++)
                {
                    foreach (var t in graph.MissingTurns(route.Waypoints[k])) if (t == route.Waypoints[k + 1]) addedTurns++;
                    if (graph.UTurn(route.Waypoints[k]) == route.Waypoints[k + 1]) addedTurns++;
                }
                if (addedTurns > 0) Log.Info($"Route uses {addedTurns} turn{(addedTurns == 1 ? "" : "s")} or U-turn{(addedTurns == 1 ? "" : "s")} the game's traffic data doesn't have.");
                var ride = new Ride(taxiSystem, taxi, kind, target, minutes, car!, prefab, vehicleType, goals, route);
                Log.Info($"Ride accepted: {kind}, {route.Waypoints.Count} waypoints, {route.LengthMetres:0} m, drive {route.DriveSeconds:0} s, walk {route.WalkSeconds:0} s, planned in {planMs} ms, budget {minutes:0.0} game minutes.");
                Active = ride;
                return ride.Run();
            }
            catch (Exception ex)
            {
                Log.Error("Ride not started: " + ex);
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
            // Spike builds say why on screen too.
            Notice.Show("Driven Rides: vanilla ride, " + reason + ".");
            return null;
        }

        private static Route? Plan(LaneGraph graph, int start, List<Goal> goals, int vehicleType, out long ms, out string why)
        {
            var watch = Stopwatch.StartNew();
            var job = RoutePlanner.Begin(graph, new RouteRequest
            {
                Start = start,
                Goals = goals,
                VehicleType = vehicleType,
                IsDisabled = GleyGraph.IsDisabled,
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

        private static bool IsHamptons(EntityController target)
        {
            if (target is CityBuildingController building) return building.building?.Neighbourhood == Hamptons;
            if (target is TaxiDestinationController && DestinationDataField?.GetValue(target) is TaxiDestinationData data)
                return data.neighbourhood == Hamptons;
            return false;
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
                    yield return null;
                }
                if (cancelled) yield break;
                phase = Phase.Finishing;
                ApplyScale(false);
                Log.Info($"Ride ending: {outcome} ({outcomeReason}), {NowMinutes() - startMinute:0.0} game minutes so far.");
                // An arrival needs no fade: the player steps out beside the car (Peter). Skip and the vanilla finish
                // fade, as vanilla does. The game's fades are stepped here, so an exception in them runs the finally.
                if (outcome == Outcome.Vanilla)
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
                Finish();
            }
            finally
            {
                if (!finished) Cancel("the ride iterator stopped early");
            }
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
            // Also sets NavigationBlocker.Taxi, disables the capsule and the agent, and tells Gley the collider is gone.
            player.AttachToObject(anchor.transform, NavigationBlocker.Taxi);
            player.Character.ToggleVisibility(false);
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
            if (kind == Kind.Taxi) taxi.DriveAway();
            owned.Clear();
            // A stand's car sits on its spawn waypoint; the route starts there.
            for (var i = kind == Kind.Stand ? 1 : 0; i < path.Count; i++) owned.Add(path[i]);
            car.presetPath = owned;
            departed = true;
            SetTopSpeed();
            Go();
            progress = new RouteProgress(Points(path));
            IndexRoute();
            watch = new RideWatch(unpausedSeconds, route.DriveSeconds);
            probe = car.gameObject.AddComponent<CollisionProbe>();
            probe.Trace = trace;
            SwitchCamera();
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

        // Puts each added turn's and U-turn's waypoints (straight into the junction, then the curve) between its
        // entry and exit, so the car drives them as a driver would.
        private List<int> Expand(Route r)
        {
            var graph = GleyGraph.Graph;
            var result = new List<int>(r.Waypoints.Count + 4);
            for (var k = 0; k < r.Waypoints.Count; k++)
            {
                result.Add(r.Waypoints[k]);
                if (graph == null || k + 1 >= r.Waypoints.Count) continue;
                var vias = graph.TurnVias(r.Waypoints[k], r.Waypoints[k + 1]);
                foreach (var via in vias) result.Add(via);
            }
            return result;
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
            if (SaveChanged) { Dispose(); return; }
            var paused = InstanceBehavior<UIs>.Instance.gameSpeed.Paused;
            if (!paused) unpausedSeconds += Time.unscaledDeltaTime;
            if (phase != Phase.Driving && phase != Phase.Stopping) return;
            if (!OwnsCar) { EndWith(Outcome.Vanilla, "the car is no longer the ride's"); return; }

            if (throwNext) { throwNext = false; throw new InvalidOperationException("Debug: exception inside the ride's frame update."); }
            progress!.Update(GleyGraph.ToVec(car.transform.position));
            var along = alongOffset + progress.AlongMetres;
            var traffic = TrafficManager.Instance;
            trace.Sample(car.transform.position, car.transform.eulerAngles.y, car.rb != null ? car.rb.velocity.magnitude : 0f,
                traffic.GetTargetWaypoint(carIndex)?.listIndex ?? -1, traffic.GetCurrentDrivingState(carIndex).Item2.ToString(), along, progress.LeftMetres, owned.Count);
            lastAlong = along;

            if (phase == Phase.Driving)
            {
                if (replanJob != null) StepReplan();
                else if (owned.Count <= 1 || progress.PointsLeft <= 1) StopAtEnd();
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

        // Sets the car driving forward in both of Gley's records. The AI keeps a list of actions; the physics keeps a
        // driving state per car. A hard stop (the summon's StopNow, a crash) writes the physics state directly, and
        // ForceSetVehicleActiveAction only tells the physics when the AI's action changes, which it doesn't if the AI
        // already says Forward. So the state event goes out explicitly, as vanilla's DriveAway does.
        private void Go()
        {
            var traffic = TrafficManager.Instance;
            traffic.SetVehicleAction(car, SpecialDriveActionTypes.Forward, true);
            traffic.ForceSetVehicleActiveAction(car, SpecialDriveActionTypes.Forward);
            AIEvents.TriggerChangeDrivingStateEvent(carIndex, SpecialDriveActionTypes.Forward, 0f);
            traffic.VehicleUpdateWaypoint(car);
        }

        private const float BumpSeconds = 1.5f;
        private float stopNowSeconds;
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
        private const float AccelerationSeconds = 5f;

        private void SetTopSpeed()
        {
            try
            {
                var ai = DrivingAIField?.GetValue(TrafficManager.Instance);
                if (ai == null || SetMaxSpeedMethod == null) { Log.Error("Top speed: Gley's DrivingAI not found."); return; }
                var top = driverTopSpeed > 0 ? driverTopSpeed : car.maxPossibleSpeed;
                SetMaxSpeedMethod.Invoke(ai, new object[] { carIndex, (float)top });
                // Gley sets the acceleration so a car reaches its rolled top speed in its model's accelerationTime
                // (10 s by default): slow off the line. The ride car reaches its top speed in AccelerationSeconds.
                if (PowerStepField?.GetValue(TrafficManager.Instance) is NativeArray<float> power && carIndex < power.Length)
                    power[carIndex] = top / 3.6f / (AccelerationSeconds / Time.fixedDeltaTime);
                Log.Info($"Top speed {top} km/h ({(driverTopSpeed > 0 ? $"vehicle type {driverVehicleType}" : "traffic model")}; Gley model {car.minPossibleSpeed}-{car.maxPossibleSpeed}, rolled {car.GetMaxSpeed()}).");
            }
            catch (Exception ex) { Log.Error("Top speed: " + ex.Message); }
        }

        private void StopAtEnd()
        {
            TrafficManager.Instance.SetVehicleAction(car, SpecialDriveActionTypes.StopInPoint, true);
            // Cleared in place: presetPath stays this same non-null list, so Gley never despawns the car.
            owned.Clear();
            phase = Phase.Stopping;
            Event($"Stopping at the end of the route, {LeftMetres:0} m from its last waypoint.");
        }

        private void Restart()
        {
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
            var graph = GleyGraph.Graph;
            var next = TrafficManager.Instance.GetTargetWaypoint(carIndex);
            if (graph == null || next == null) { EndWith(Outcome.Vanilla, "replan had no start"); return; }
            replanJob = RoutePlanner.Begin(graph, new RouteRequest
            {
                Start = next.listIndex,
                Goals = goals,
                VehicleType = vehicleType,
                IsDisabled = GleyGraph.IsDisabled,
            });
            replanSeconds = 0f;
            Log.Info("Watchdog: replanning (" + watch!.LastReason + ").");
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
            if (ride == null || ride.finished || ride.cancelled || !ride.departed || ride.carIndex != vehicleIndex || ride.progress == null) return null;
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
            var place = outcome == Outcome.Complete && BesideCar(out var beside) ? beside
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
            if (kind == Kind.PrivateDriver)
            {
                if (OwnsCar) Go();
                // As after a vanilla ride, the driver leaves the phone; non-instant, so the car drives off.
                if (taxi is PrivateDriverVehicle pd && SmartphonePrivateDriverUI.CurrentVehicle == pd)
                    InstanceBehavior<UIs>.Instance.smartphoneUI.DismissPrivateDriver(true, null, false);
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
            var y = player.transform.position.y;
            var leave = TrafficManager.Instance.GetClosestWaypoint(player.transform.position, 50f, w => IsValidForLeaving(w, y), prefab);
            var ours = OwnsCar;
            switch (kind)
            {
                case Kind.Taxi:
                    if (!ours) break;
                    if (leave != null) { car.presetPath = null; taxi.InstantiateVehicle(leave); }
                    else ReleaseToTraffic();
                    break;
                case Kind.PrivateDriver:
                    if (ours && leave != null) { car.presetPath = null; taxi.InstantiateVehicle(leave); }
                    else if (taxi is PrivateDriverVehicle pd && SmartphonePrivateDriverUI.CurrentVehicle == pd)
                        InstanceBehavior<UIs>.Instance.smartphoneUI.DismissPrivateDriver(true, null, true);
                    break;
                case Kind.Stand:
                    if (ours) ReleaseToTraffic();
                    if (leave != null && HasInactivePooledCar(prefab)) taxi.InstantiateVehicle(leave);
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
                var vehicleCamera = VehicleCameraField?.GetValue(manager);
                var follow = CameraTarget(vehicleCamera, "Follow");
                Log.Info($"Camera: vehicle camera follows '{follow?.name ?? "nothing"}', looks at '{CameraTarget(vehicleCamera, "LookAt")?.name ?? "nothing"}'.");
                SetGameCamera(follow != null ? vehicleCamera : PedestrianCameraField?.GetValue(manager));
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
            Log.Info("Ride finished.");
        }

        // Clears the mod's own objects and static state; TaxiSystem's field only while this ride owns it.
        private void DropState()
        {
            if (probe != null) Object.Destroy(probe);
            probe = null;
            trace.Save();
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
                    var place = NavMesh.SamplePosition(here, out var hit, 10f, NavMesh.AllAreas) ? hit.position : pickupPosition;
                    Detach(place);
                }
                catch (Exception ex) { Log.Error("Cancel, detach: " + ex); }
                try { player.Character.ToggleVisibility(true); }
                catch (Exception ex) { Log.Error("Cancel, visibility: " + ex.Message); }
            }
            try { RestoreCamera(); } catch (Exception ex) { Log.Error("Cancel, camera: " + ex.Message); }
            try { RestoreScale(); } catch (Exception ex) { Log.Error("Cancel, time scale: " + ex.Message); }
            // A stand's car was loaded and held before departure, so it is released even if the ride never left.
            try { if (OwnsCar && (departed || kind == Kind.Stand)) ReleaseToTraffic(); } catch (Exception ex) { Log.Error("Cancel, car: " + ex.Message); }
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

        // During a ride the clock runs at a third of its speed (Peter, 11 October 2026). Ticks are game minutes from
        // scaled time, so at 3x ride speed the clock runs at its normal rate. The ride's own time machine (Skip) is
        // left alone.
        public const float ClockFactor = 1f / 3f;

        public static void CapTick(ref float minutes)
        {
            var ride = Active;
            if (ride == null || ride.finished || ride.cancelled) return;
            if (ride.SaveChanged) { ride.Dispose(); return; }
            if (!TimeMachineRunning) minutes *= ClockFactor;
        }

        // Vanilla's charge for the straight line from the car to the destination (TaxiSystem.GetTravelDurationMinutes).
        private double VanillaMinutesLeft()
        {
            var from = car != null && car.gameObject.activeInHierarchy ? car.transform.position : player.transform.position;
            var to = target != null ? target.GetNavMeshTargetPosition() : from;
            return Vector3.Distance(from, to) * 0.04 * PlayerPrefSettings.GameSpeed * timeMultiplier;
        }

        public float RideSpeed => rideSpeed;

        // The panel's speed buttons.
        public void SetSpeed(float speed)
        {
            rideSpeed = speed;
            Log.Info($"Ride speed {speed:0}x.");
        }

        // The StartTimeMachine prefix: a time machine the ride didn't start cancels it first.
        public static void OnTimeMachineStarting()
        {
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
