#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using GleyTrafficSystem;
using Parking.UndergroundParking;
using HarmonyLib;
using Helpers;
using UI;
using UnityEngine;
using Vehicles;
using Vehicles.Taxis;
using Object = UnityEngine.Object;

namespace DrivenRides
{
    internal sealed class OwnCarTaxi : MonoBehaviour, ITaxi
    {
        private static OwnCarTaxi? instance;

        public CarController? Car { get; private set; }

        public static void Open()
        {
            if (!Options.OwnCar) { Notice.Text("drivenrides:notice_owncar_off"); return; }
            if (!(VehicleHelper.GetCurrentVehicleBase() is CarController car) || car.vehicleType == null || car.vehicleType.spawnInPlayerObject)
            {
                Notice.Text("drivenrides:notice_owncar_only_cars");
                return;
            }
            if (BuildingManager.IsInsideBuilding || UndergroundParkingManager.IsInsideParking) { Notice.Text("drivenrides:notice_owncar_street"); return; }
            var body = car.vehicleController?.vehicleRigidbody;
            if (body != null ? body.velocity.magnitude > 0.3f : car.CurrentSpeed > 1f) { Notice.Text("drivenrides:notice_owncar_stop"); return; }
            if (Mathf.Abs(car.GetCurrentCondition() - 1f) < Mathf.Epsilon) { Notice.Text("drivenrides:notice_owncar_damaged"); return; }
            if (instance == null)
            {
                var host = new GameObject("DrivenRides.OwnCarTaxi");
                Object.DontDestroyOnLoad(host);
                instance = host.AddComponent<OwnCarTaxi>();
            }
            instance.Car = car;
            var drivable = DrivableType(car);
            Log.Trace($"Own car: choosing a destination for {car.vehicleType.vehicleTypeName}" +
                      (drivable != car.vehicleType.vehicleTypeName ? $" (drives as {drivable ?? "nothing: no drivable prefab"})." : "."));
            InstanceBehavior<CityManager>.Instance.cityMap.SetTaxiMode(instance);
        }

        // The vehicle type whose drivable prefab this car is, or null. Another mod may give a car a type of its own:
        // Be A Taxi's shift taxi is a Vord Tiara Vic whose type is named "beataxi_vehicle_name" (a user's Player.log,
        // 16 October 2026). So after the type, the car's vehicle instance (the type the save keeps) and its object's
        // name are tried; each counts only if a drivable prefab goes by it.
        public static string? DrivableType(CarController car)
        {
            var candidates = new[] { car.vehicleType?.vehicleTypeName, car.vehicleInstance?.vehicleTypeName, car.name.Replace("(Clone)", "").Trim() };
            foreach (var name in candidates)
                if (!string.IsNullOrEmpty(name) && NwhCar.HasPrefab(name!)) return name;
            return null;
        }

        public static void Drop()
        {
            if (instance != null) Object.Destroy(instance.gameObject);
            instance = null;
            OwnCarDrive.Drop();
        }

        public void DriveAway() { }

        public VehicleComponent GetVehiclePrefab() => null!;

        public VehicleComponent InstantiateVehicle(Waypoint waypoint) => null!;

        public string GetHappinessModifierName() => null!;

        private static readonly FieldInfo? TrafficVehiclesField = AccessTools.Field(typeof(TrafficManager), "trafficVehicles");

        public static VehicleComponent? PlanPrefab(CarController car, out string why)
        {
            why = "";
            var twin = PrefabHelper.LoadPrefabAssetByName("Vehicles/" + NwhCar.Id(DrivableType(car) ?? car.vehicleType?.vehicleTypeName ?? car.name));
            var twinCar = twin != null ? twin.GetComponent<VehicleComponent>() : null;
            if (twinCar != null) return twinCar;
            var length = car.vehicleController != null ? NwhCar.Wrap(car.vehicleController, car.gameObject, "", out _)?.Length ?? 4.5f : 4.5f;
            if (!(TrafficVehiclesField?.GetValue(TrafficManager.Instance) is TrafficVehicles vehicles)) { why = "no traffic pool"; return null; }
            VehicleComponent? best = null;
            var bestDiff = float.MaxValue;
            foreach (var v in vehicles.GetVehicleList())
            {
                if (v == null || v.prefab == null || v.vehicleType != VehicleTypes.Car || v.prefab.GetComponent<TaxiController>() != null) continue;
                var diff = Mathf.Abs(v.length - length);
                if (diff < bestDiff) { bestDiff = diff; best = v.prefab.GetComponent<VehicleComponent>(); }
            }
            if (best == null) why = "no car in the traffic pool to plan with";
            else Log.Trace($"Own car: planned as {best.name} ({length:0.0} m, {bestDiff:0.0} m off; the car has no traffic twin).");
            return best;
        }

        public static NwhRideCar RideCar(CarController car, NwhCar wrapped, DrivenRides.Core.Drive.CarSpec spec, EntityController? target,
            VehicleComponent planPrefab, Func<bool> stop) => new NwhRideCar
        {
            Car = wrapped,
            Spec = spec,
            PlayerInside = true,
            Owned = false,
            End = reason =>
            {
                OwnCarDrive.End(reason);
                if (car != null && car.TryGetComponent<NwhDrive>(out var drive)) Object.Destroy(drive);
            },
            Arrive = () => Arrive(car, wrapped, target, planPrefab, stop),
        };

        private static IEnumerator Arrive(CarController car, NwhCar wrapped, EntityController? target, VehicleComponent planPrefab, Func<bool> stop)
        {
            if (car == null) yield break;
            var drive = car.GetComponent<NwhDrive>();
            if ((drive == null || !drive.Arrived) && target != null)
            {
                var destination = target.GetNavMeshTargetPosition();
                var y = destination.y;
                // As vanilla's leaving waypoint (TaxiSystem.IsValidWaypointForLeaving), free for a car of this size;
                // parked and traffic cars there are cleared below, as vanilla does, but another of the player's own cars
                // isn't, so a lane with one in the way is passed over.
                var lane = TrafficManager.Instance.GetClosestWaypoint(destination, 50f,
                    w => w.allowedCars != null && w.allowedCars.Count > 0 && TrafficManager.WithinHeight(w, y, 5f) && TrafficManager.IsNotIntersection(w)
                         && !PlayerCarIn(wrapped, RootFor(wrapped, w), TrafficManager.Instance.GetWaypointOrientation(w)), planPrefab);
                if (lane != null)
                {
                    var facing = TrafficManager.Instance.GetWaypointOrientation(lane);
                    Teleport(car, RootFor(wrapped, lane), facing);
                    VehicleHelper.DestroyBlockingVehicles(car.gameObject, car.vehicleType, car.transform);
                    car.SavePosition();
                    Log.Trace($"Own car: the trip was cut short; the car is on the lane at waypoint {lane.listIndex}, {Vector3.Distance(lane.position, destination):0} m from the destination.");
                }
                else Log.Trace("Own car: the trip was cut short and there is no lane near the destination; the car stays where it is.");
                yield return null;
            }
            var park = Autopark(car, stop, target != null ? target.GetNavMeshTargetPosition() : (Vector3?)null);
            while (park.MoveNext()) yield return park.Current;
        }

        private static readonly MethodInfo? FuelMethod = AccessTools.Method(typeof(SkipBridgeHelper), "GetFuelConsumption");

        private static float FuelFor(CarController car, float metres)
        {
            if (SaveGameManager.Current?.gameVariables.disableVehicleFuel == true || car.fuelModule == null || !car.fuelModule.useFuel) return 0f;
            return FuelMethod?.Invoke(null, new object[] { car, metres }) is float used && used > 0f ? used : 0f;
        }

        private static Vector3 RootFor(NwhCar car, Waypoint w) =>
            w.position - TrafficManager.Instance.GetWaypointOrientation(w) * new Vector3(0f, 0f, car.RearZ);

        private static readonly Collider[] Overlaps = new Collider[32];

        private static bool PlayerCarIn(NwhCar car, Vector3 at, Quaternion rotation)
        {
            var centre = at + rotation * car.BodyCentre;
            var n = Physics.OverlapBoxNonAlloc(centre, car.BodyHalf * 0.95f, Overlaps, rotation, ~0, QueryTriggerInteraction.Ignore);
            for (var k = 0; k < n; k++)
            {
                var other = Overlaps[k].GetComponentInParent<global::VehicleController>();
                if (other != null && other.gameObject != car.Root) return true;
            }
            return false;
        }

        public const string NotInLane = "pull out into the lane first";

        public static string? StartRefusal(CarController car, NwhPlanner planner, DrivenRides.Core.Drive.DrivePlan plan, DrivenRides.Core.Routing.LaneGraph graph, float sideways)
        {
            var wrapped = NwhCar.Wrap(car.vehicleController, car.gameObject, "", out var why);
            if (wrapped == null) return why;
            var forward = car.transform.forward;
            var speed = Vector3.Dot(wrapped.Body.velocity, forward);
            if (Mathf.Abs(speed) > 0.3f) return "stop the car first";
            var rear = wrapped.RearAxle;
            var pose = new DrivenRides.Core.Drive.CarPose(rear.x, rear.z, Mathf.Atan2(forward.x, forward.z), speed, car.vehicleController.steering.angle);
            var line = plan.Line(graph);
            var miss = line.JoinMiss(pose.X, pose.Z, pose.Heading, out _);
            if (miss != null)
            {
                var first = plan.Path.Count > 0 ? plan.Path[0].ToString() : "none";
                var step = sideways < float.MaxValue ? $"the lane step under the car, {sideways:+0.0;-0.0} m to its side" : "no lane step runs under the car its way";
                return FormattableString.Invariant($"{NotInLane}: {miss}; route starts at waypoint {first}, {step}");
            }
            var result = new DrivenRides.Core.Drive.Feasibility.SweepResult();
            foreach (var _ in planner.Check.JoinWork(plan, line, pose, result)) { }
            return result.What != null ? "it can't drive off from here (" + result.What + ")" : null;
        }

        public static int StartUnder(DrivenRides.Core.Routing.LaneGraph graph, CarController car, int nearest, Func<int, bool> goodStart, out float sideways)
        {
            sideways = float.MaxValue;
            var wrapped = NwhCar.Wrap(car.vehicleController, car.gameObject, "", out _);
            var rear = wrapped != null ? wrapped.RearAxle : car.transform.position;
            var forward = car.transform.forward;
            var best = nearest;
            var steps = new List<(int From, int To)>();
            foreach (var w in graph.Near(GleyGraph.ToVec(rear), 15f))
            {
                foreach (var j in graph.Successors(w)) steps.Add((w, j));
                foreach (var p in graph.Predecessors(w)) steps.Add((p, w));
            }
            foreach (var (i, j) in steps)
            {
                if (graph.IsIntersection(i) || !goodStart(i)) continue;
                var a = graph.Position(i);
                {
                    var b = graph.Position(j);
                    float dx = b.X - a.X, dz = b.Z - a.Z;
                    var length = Mathf.Sqrt(dx * dx + dz * dz);
                    if (length < 0.1f) continue;
                    float ux = dx / length, uz = dz / length;
                    if (ux * forward.x + uz * forward.z < 0.7f) continue;
                    var t = (rear.x - a.X) * ux + (rear.z - a.Z) * uz;
                    if (t < 0f || t > length) continue;
                    var side = (rear.x - a.X) * uz - (rear.z - a.Z) * ux;
                    if (Mathf.Abs(side) < Mathf.Abs(sideways)) { sideways = side; best = i; }
                }
            }
            return best;
        }

        public static bool HasFuelForBridge(CarController car, float metres)
        {
            try { return car.GetCurrentFuel() >= FuelFor(car, metres); }
            catch (Exception ex) { Log.Error("Own car bridge fuel check: " + ex.Message); return true; }
        }

        public static void ChargeBridgeFuel(CarController car, float metres)
        {
            try
            {
                var used = FuelFor(car, metres);
                if (used <= 0f) return;
                car.SetFuel(Mathf.Max(0f, car.GetCurrentFuel() - used));
                Log.Trace($"Own car: {used:0.00} fuel for the bridge ({metres:0} m).");
            }
            catch (Exception ex) { Log.Error("Own car bridge fuel: " + ex.Message); }
        }

        public static bool HasFuelFor(CarController car, float metres)
        {
            try { return SaveGameManager.Current?.gameVariables.disableVehicleFuel == true || car.fuelModule == null || !car.fuelModule.useFuel || car.GetCurrentFuel() > 0f; }
            catch (Exception ex) { Log.Error("Own car fuel check: " + ex.Message); return true; }
        }

        private static readonly FieldInfo? ParkingHelperField = AccessTools.Field(typeof(CarController), "vehicleParkingHelper");

        internal static bool WantsAutopark(CarController car) => Options.Autopark == AutoparkMode.Every
            || (Options.Autopark == AutoparkMode.CarsWithIt && car.vehicleType != null && car.vehicleType.autoParkSupported);

        public static IEnumerator Autopark(CarController car, Func<bool> stop, Vector3? door = null)
        {
            var mode = Options.Autopark;
            var supported = car.vehicleType != null && car.vehicleType.autoParkSupported;
            if (mode == AutoparkMode.Off || (mode == AutoparkMode.CarsWithIt && !supported))
            {
                Log.Trace($"Own car: no autopark ({(mode == AutoparkMode.Off ? "off in the options" : "the car has none")}).");
                yield break;
            }
            yield return null;
            if (stop() || car == null || !car.controlledByPlayer) yield break;
            if (car.CurrentSpeed >= 10f) { Log.Trace("Own car: no autopark, the car is moving."); yield break; }
            if (Mathf.Abs(car.GetCurrentCondition() - 1f) < Mathf.Epsilon) { Log.Trace("Own car: no autopark, the car is wrecked."); yield break; }
            if (!(ParkingHelperField?.GetValue(car) is VehicleParkingHelper helper) || helper == null)
            {
                Log.Error("Own car autopark: the car has no parking helper.");
                yield break;
            }
            var hunt = new SpotHunt();
            var hunting = Hunt(car, helper, hunt, door);
            while (hunting.MoveNext()) { yield return hunting.Current; if (stop()) yield break; }
            var spot = hunt.Found;
            if (spot == null || car == null || !car.controlledByPlayer)
            {
                if (spot == null) Notice.Text("drivenrides:notice_owncar_no_spot", ("metres", HuntRadii[HuntRadii.Length - 1].ToString("0")));
                Log.Trace($"Own car: no free autopark spot within {HuntRadii[HuntRadii.Length - 1]:0} m ({hunt.Seen} spots seen).");
                yield break;
            }
            Log.Trace($"Own car: autoparking {Vector3.Distance(car.transform.position, spot.transform.position):0.0} m away{(supported ? "" : " (the car has no autopark of its own)")}.");
            var position = spot.transform.position;
            var rotation = spot.transform.parent.rotation;
            if (spot.visuals == null) yield break;
            var spotWidth = spot.visuals.size.y;
            if (spot.visuals != null) spot.visuals.enabled = false;
            if (Ride.Active?.VisibleParking == true)
            {
                var type = DrivableType(car);
                var planner = type != null ? NwhPlanner.For(type, car.vehicleType.maxSpeed) : null;
                var wrapped = NwhCar.Wrap(car.vehicleController, car.gameObject, type ?? "", out _);
                if (planner != null && wrapped != null)
                {
                    var result = new ManoeuvreResult();
                    var visible = OwnCarParking.Park(car, wrapped, planner, position, rotation,
                        () => stop() || car == null || !car.controlledByPlayer || Ride.Active?.ParkingSkipped == true, result);
                    while (visible.MoveNext()) { yield return visible.Current; if (stop()) yield break; }
                    if (stop() || car == null || !car.controlledByPlayer) yield break;
                    if (result.Success)
                    {
                        car.UpdateParkingZone();
                        if (OwnCarParking.IsLegal(car))
                        {
                            car.vehicleInstance.parkingState = ParkingState.Legal;
                            car.SavePosition(); LastParkField?.SetValue(helper, car.transform.position);
                            if (car.poi != null) car.poi.SetBackground(InstanceBehavior<GlobalReferences>.Instance.vehiclePOIBackgroundColor);
                            ParkingLaneGenerator.RegenerateAutoParkSpotsNear(car.transform.position);
                            Log.Info("Own car: visibly parked."); yield break;
                        }
                        Log.Info("Own car: manoeuvre finished outside a legal parking area.");
                    }
                    else Log.Info("Own car: visible parking fell back (" + result.Reason + ").");
                }
            }
            yield return UiFader.Fade();
            var parked = false;
            if (!stop() && car != null && car.controlledByPlayer && !IsOccupiedAt(helper, position, rotation, spotWidth))
            {
                Teleport(car, position, rotation);
                car.vehicleInstance.parkingState = ParkingState.Legal;
                if (car.poi != null) car.poi.SetBackground(InstanceBehavior<GlobalReferences>.Instance.vehiclePOIBackgroundColor);
                car.UpdateParkingZone();
                car.SavePosition();
                LastParkField?.SetValue(helper, position);
                parked = true;
            }
            yield return UiFader.UnFade();
            Log.Trace(parked ? "Own car: autoparked." : "Own car: autopark called off (the player got out, or the spot was taken).");
        }

        // The game's teleport writes the rigidbody; its transform follows only at the next physics step, and the parking
        // check, the save and anything placed by the car read the transform, so it is brought along at once.
        public static void Teleport(CarController car, Vector3 position, Quaternion rotation)
        {
            VehicleHelper.TeleportVehicleToGround(car, position, rotation);
            if (car.TryGetComponent<Rigidbody>(out var body)) { car.transform.SetPositionAndRotation(body.position, body.rotation); Physics.SyncTransforms(); }
        }

        private static readonly float[] HuntRadii = { 20f, 50f, 100f, 150f };
        private static readonly MethodInfo? GenerateSpotsMethod = AccessTools.Method(typeof(ParkingLaneGenerator), "GenerateAutoParkSpots");
        private static readonly FieldInfo? DimensionsField = AccessTools.Field(typeof(VehicleParkingHelper), "_vcom");
        private static readonly FieldInfo? LaneColliderField = AccessTools.Field(typeof(ParkingLaneGenerator), "_collider");
        private static readonly Collider[] HuntHits = new Collider[4096];
        private const float HuntHeight = 3f;
        private const int LanesPerFrame = 8;

        private sealed class SpotHunt
        {
            public AutoParkSpot? Found;
            public readonly HashSet<AutoParkSpot> SeenSpots = new HashSet<AutoParkSpot>();
            public int Seen => SeenSpots.Count;
        }

        private static IEnumerator Hunt(CarController car, VehicleParkingHelper helper, SpotHunt hunt, Vector3? door)
        {
            var padding = InstanceBehavior<GlobalReferences>.Instance.autoParkSettings.VehiclePadding * 2f;
            var length = (DimensionsField?.GetValue(helper) as NWH.Common.CoM.VariableCenterOfMass)?.dimensions.z ?? 4.6f;
            var needs = length + padding;
            var made = new HashSet<ParkingLaneGenerator>();
            var reach = Reachable(car.transform.position);
            var visible = Ride.Active?.VisibleParking == true;
            var candidates = new List<AutoParkSpot>();
            AutoParkSpot? best = null;
            var bestScore = float.MaxValue;
            foreach (var radius in HuntRadii)
            {
                if (car == null) yield break;
                var at = car.transform.position;
                var n = Physics.OverlapSphereNonAlloc(at, radius, HuntHits, LayerHelper.parkingAreaLayerMask, QueryTriggerInteraction.Collide);
                var any = false;
                for (var k = 0; k < n; k++)
                {
                    var lane = HuntHits[k] != null ? HuntHits[k].GetComponentInParent<ParkingLaneGenerator>() : null;
                    if (lane == null || !ParkingNavigation.OrdinaryParallel(lane) || !made.Add(lane)) continue;
                    if (LaneColliderField != null && LaneColliderField.GetValue(lane) as Object == null) continue;
                    try { GenerateSpotsMethod?.Invoke(lane, null); any = true; }
                    catch (Exception ex) { Log.Error("Own car autopark, spots: " + ex.Message); }
                    if (made.Count % LanesPerFrame == 0) { yield return null; if (car == null) yield break; }
                }
                if (any) { yield return null; Physics.SyncTransforms(); }
                if (car == null) yield break;
                at = car.transform.position;
                n = Physics.OverlapSphereNonAlloc(at, radius, HuntHits, 1 << LayerHelper.AutoParkSpotsLayerIndex, QueryTriggerInteraction.Collide);
                for (var k = 0; k < n; k++)
                {
                    if (HuntHits[k] == null || !HuntHits[k].TryGetComponent<AutoParkSpot>(out var spot) || spot.maxVehicleLength < needs) continue;
                    var p = spot.transform.position;
                    var away = Vector3.Distance(at, p);
                    if (away > radius || !hunt.SeenSpots.Add(spot)) continue;
                    var parkingLane = spot.GetComponentInParent<ParkingLaneGenerator>();
                    if (parkingLane == null || !ParkingNavigation.OrdinaryParallel(parkingLane)) continue;
                    var score = away + (door is Vector3 dd ? 0.5f * Vector3.Distance(dd, p) : 0f);
                    if (away < 1.5f || Mathf.Abs(p.y - at.y) > HuntHeight || IsOccupied(helper, spot)) continue;
                    candidates.Add(spot);
                    if (score < bestScore && reach(p)) { best = spot; bestScore = score; }
                }
                if (!visible && best != null && bestScore <= radius) break;
            }
            if (visible && car != null)
            {
                var choice = new ParkingNavigation.Choice();
                var choose = ParkingNavigation.ChooseSpace(car, candidates, door, choice);
                while (choose.MoveNext()) yield return choose.Current;
                if (choice.Spot != null) best = choice.Spot;
            }
            hunt.Found = best;
        }

        private const float ReachMetres = 250f, ReachLane = 10f;

        private static Func<Vector3, bool> Reachable(Vector3 from)
        {
            var graph = GleyGraph.Graph;
            if (graph == null) return _ => true;
            if (!Gates.Read) Gates.Refresh(graph);
            var origin = GleyGraph.ToVec(from);
            var seen = new HashSet<int>();
            var queue = new Queue<int>();
            foreach (var w in graph.Near(origin, 15f)) if (seen.Add(w)) queue.Enqueue(w);
            while (queue.Count > 0)
            {
                var w = queue.Dequeue();
                foreach (var next in graph.Successors(w)) Visit(w, next, next);
                foreach (var prev in graph.Predecessors(w)) Visit(prev, w, prev);
            }
            void Visit(int a, int b, int other)
            {
                if (seen.Contains(other) || Gates.LockedStep(a, b)) return;
                if (DrivenRides.Core.Routing.Vec3.HorizontalDistance(origin, graph.Position(other)) > ReachMetres) return;
                seen.Add(other);
                queue.Enqueue(other);
            }
            return p =>
            {
                foreach (var w in graph.Near(GleyGraph.ToVec(p), ReachLane)) if (seen.Contains(w)) return true;
                return false;
            };
        }

        private static readonly FieldInfo? LastParkField = AccessTools.Field(typeof(VehicleParkingHelper), "_lastAutoParkPosition");
        private static readonly MethodInfo? OccupiedMethod = AccessTools.Method(typeof(VehicleParkingHelper), "IsSpotOccupied", new[] { typeof(AutoParkSpot) });
        private static readonly MethodInfo? OccupiedAtMethod = AccessTools.Method(typeof(VehicleParkingHelper), "IsSpotOccupied", new[] { typeof(Vector3), typeof(Quaternion), typeof(float) });

        private static bool IsOccupiedAt(VehicleParkingHelper helper, Vector3 position, Quaternion rotation, float width)
        {
            try { return !(OccupiedAtMethod?.Invoke(helper, new object[] { position, rotation, width }) is bool taken) || taken; }
            catch (Exception ex) { Log.Error("Own car autopark, position check: " + ex.Message); return true; }
        }

        private static bool IsOccupied(VehicleParkingHelper helper, AutoParkSpot spot)
        {
            try { return OccupiedMethod?.Invoke(helper, new object[] { spot }) is bool taken && taken; }
            catch (Exception ex) { Log.Error("Own car autopark, spot check: " + ex.Message); return true; }
        }
    }

    internal static class OwnCarDrive
    {
        private static CarController? active;
        private static bool ending;

        public static bool IsActive => active != null;
        public static CarController? Car => active;

        public static NwhCar? Begin(CarController car, out string why)
        {
            why = "";
            if (active != null) { why = "the car is already driving itself"; return null; }
            var vehicle = car.vehicleController;
            if (vehicle == null || vehicle.input == null) { why = "the car has no NWH controller"; return null; }
            var wrapped = NwhCar.Wrap(vehicle, car.gameObject, OwnCarTaxi.DrivableType(car) ?? (car.vehicleType != null ? car.vehicleType.vehicleTypeName : car.name), out why);
            if (wrapped == null) return null;
            vehicle.input.autoSetInput = false;
            Zero(vehicle);
            active = car;
            Log.Trace($"Own car: driving itself ({wrapped.TypeName}, wheelbase {wrapped.Wheelbase:0.00} m).");
            return wrapped;
        }

        public static void End(string reason)
        {
            var car = active;
            if (car == null) return;
            active = null;
            try
            {
                foreach (var drive in car.GetComponents<NwhDrive>()) { drive.Stop(reason); Object.Destroy(drive); }
                foreach (var move in car.GetComponents<NwhManoeuvre>()) { move.Stop(reason); Object.Destroy(move); }
                if (car != null && car.vehicleController?.input != null)
                {
                    Zero(car.vehicleController);
                    car.vehicleController.input.autoSetInput = true;
                }
            }
            catch (Exception ex) { Log.Error("Own car: handing the keys back: " + ex.Message); }
            Log.Trace("Own car: the keys are the player's again (" + reason + ").");
        }

        private static void Zero(NWH.VehiclePhysics2.VehicleController vehicle)
        {
            // Held on the handbrake: NWH swaps throttle and brakes in reverse (VehicleInputHandler.swapInputInReverse),
            // so the brake axis could drive a car in reverse backwards.
            var input = vehicle.input;
            input.Throttle = 0f;
            input.Brakes = 0f;
            input.Steering = 0f;
            input.Handbrake = 1f;
            vehicle.steering.externallyAddedAngle = 0f;
        }

        public static void AfterCarUpdate(CarController car)
        {
            if (active != null && ReferenceEquals(car, active)) car.vehicleController.steering.externallyAddedAngle = 0f;
        }

        public static void BeforeExit(CarController car)
        {
            if (active == null || !ReferenceEquals(car, active) || ending) return;
            ending = true;
            try
            {
                Log.Trace("Own car: the player got out during the trip.");
                Ride.CancelActive("the player got out of the car");
                End("the player got out");
            }
            finally { ending = false; }
        }

        public static bool AllowsTeleport()
        {
            if (active == null) return true;
            Notice.Text("drivenrides:notice_owncar_busy");
            return false;
        }

        public static string? Trouble()
        {
            var car = active;
            if (car == null) return null;
            if (car.fuelModule != null && car.fuelModule.useFuel && car.GetCurrentFuel() <= 0f) return OutOfFuel;
            if (Mathf.Abs(car.GetCurrentCondition() - 1f) < Mathf.Epsilon) return TooDamaged;
            return null;
        }

        private const string OutOfFuel = "The car is out of fuel";
        private const string TooDamaged = "The car is too damaged to drive";

        public static string TroubleKey(string trouble) =>
            trouble == OutOfFuel ? "drivenrides:notice_owncar_out_of_fuel" : "drivenrides:notice_owncar_damaged";

        public static void Drop()
        {
            active = null;
            ending = false;
        }
    }

    internal enum AutoparkMode { Off = 0, CarsWithIt = 1, Every = 2 }
}
