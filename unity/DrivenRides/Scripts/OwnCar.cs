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
    // The player's own car driving itself (docs/own-car-self-drive.md). L in the driver's seat opens the city map in
    // taxi mode with this as the taxi; the TravelCoroutine prefix then runs an own-car ride: the car is hidden where it
    // stands, a stand-in drives, and at the end the car is put where the stand-in stopped with the player in it.
    // The fare is nothing (GetPrice prefix), and nothing taxi-like is counted.
    internal sealed class OwnCarTaxi : MonoBehaviour, ITaxi
    {
        private static OwnCarTaxi? instance;

        public CarController? Car { get; private set; }

        // Opens the destination map for the car the player sits in, or says why not.
        public static void Open()
        {
            if (!Options.OwnCar) { Notice.Text("drivenrides:notice_owncar_off"); return; }
            if (!(VehicleHelper.GetCurrentVehicleBase() is CarController car) || car.vehicleType == null || car.vehicleType.spawnInPlayerObject)
            {
                Notice.Text("drivenrides:notice_owncar_only_cars");
                return;
            }
            if (BuildingManager.IsInsideBuilding || UndergroundParkingManager.IsInsideParking) { Notice.Text("drivenrides:notice_owncar_street"); return; }
            // The same standstill the start check asks for (StartRefusal), so the map doesn't open for a trip it refuses.
            var body = car.vehicleController?.vehicleRigidbody;
            if (body != null ? body.velocity.magnitude > 0.3f : car.CurrentSpeed > 1f) { Notice.Text("drivenrides:notice_owncar_stop"); return; }
            // The game's own test (VehicleParkingHelper.ParkInClosestSpot): a condition of 1 is a wreck.
            if (Mathf.Abs(car.GetCurrentCondition() - 1f) < Mathf.Epsilon) { Notice.Text("drivenrides:notice_owncar_damaged"); return; }
            if (instance == null)
            {
                var host = new GameObject("DrivenRides.OwnCarTaxi");
                Object.DontDestroyOnLoad(host);
                instance = host.AddComponent<OwnCarTaxi>();
            }
            instance.Car = car;
            Log.Trace($"Own car: choosing a destination for {car.vehicleType.vehicleTypeName}.");
            InstanceBehavior<CityManager>.Instance.cityMap.SetTaxiMode(instance);
        }

        public static void Drop()
        {
            if (instance != null) Object.Destroy(instance.gameObject);
            instance = null;
            OwnCarDrive.Drop();
        }

        public void DriveAway() { }

        // Only vanilla's ride asks for these, and an own-car ride never falls back to it.
        public VehicleComponent GetVehiclePrefab() => null!;

        public VehicleComponent InstantiateVehicle(Waypoint waypoint) => null!;

        public string GetHappinessModifierName() => null!;

        // A traffic model to plan with: Gley answers questions about lanes and free waypoints for a pooled car type,
        // so the car's own traffic twin if it has one, else the pooled car closest in length (cars only, never a taxi).
        // Nothing is loaded; the car itself drives.
        private static readonly FieldInfo? TrafficVehiclesField = AccessTools.Field(typeof(TrafficManager), "trafficVehicles");

        public static VehicleComponent? PlanPrefab(CarController car, out string why)
        {
            why = "";
            var twin = PrefabHelper.LoadPrefabAssetByName("Vehicles/" + NwhCar.Id(car.vehicleType?.vehicleTypeName ?? car.name));
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

        // The ride car for the seam (NwhRideCar): the player inside, the car not the mod's, the keys handed back at
        // every end, and at the end the arrival: a skipped or given-up trip puts the car on the lane nearest the
        // destination first (as vanilla puts a passenger at the door), then autopark as the options say.
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
                // The drive was added to the player's car; it goes with the ride.
                if (car != null && car.TryGetComponent<NwhDrive>(out var drive)) Object.Destroy(drive);
            },
            Arrive = () => Arrive(car, wrapped, target, planPrefab, stop),
        };

        private static IEnumerator Arrive(CarController car, NwhCar wrapped, EntityController? target, VehicleComponent planPrefab, Func<bool> stop)
        {
            if (car == null) yield break;
            var drive = car.GetComponent<NwhDrive>();
            // Cut short: skipped, given up, or never driven off at all.
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
                    // The rear axle on the waypoint, as a car stopped in the lane stands, so a trip can start from here.
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

        // Fuel for the way driven, as the game charges a skipped bridge (SkipBridgeHelper.GetFuelConsumption).
        private static readonly MethodInfo? FuelMethod = AccessTools.Method(typeof(SkipBridgeHelper), "GetFuelConsumption");

        private static float FuelFor(CarController car, float metres)
        {
            if (SaveGameManager.Current?.gameVariables.disableVehicleFuel == true || car.fuelModule == null || !car.fuelModule.useFuel) return 0f;
            return FuelMethod?.Invoke(null, new object[] { car, metres }) is float used && used > 0f ? used : 0f;
        }

        // Where the car's root goes for its rear axle to stand on a waypoint, facing along it: the placement and its
        // occupancy check use the same pose.
        private static Vector3 RootFor(NwhCar car, Waypoint w) =>
            w.position - TrafficManager.Instance.GetWaypointOrientation(w) * new Vector3(0f, 0f, car.RearZ);

        // Whether another of the player's cars stands where this car's body would be with its root at `at`.
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

        // The own car starts where it stands: the planned line must start under it, and the car must be able to drive
        // onto it from its pose (the planner's join check, Feasibility.Join), or the trip is refused. A car at the kerb
        // is the player's to pull out (Peter, 14 October 2026).
        // The refusal for a car the line doesn't run under; the reason after it is for the log.
        public const string NotInLane = "pull out into the lane first";

        // `sideways`: the rear axle's offset from the lane step the route starts on (StartUnder), MaxValue when no
        // step runs under the car; for the log.
        public static string? StartRefusal(CarController car, NwhPlanner planner, DrivenRides.Core.Drive.DrivePlan plan, DrivenRides.Core.Routing.LaneGraph graph, float sideways)
        {
            var wrapped = NwhCar.Wrap(car.vehicleController, car.gameObject, "", out var why);
            if (wrapped == null) return why;
            var forward = car.transform.forward;
            // The check is made for the car as it is: standing, with its wheels where they are.
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

        // The route's first waypoint: the start of the lane step that runs under the car's rear axle, along the car's
        // way, the nearest such step sideways (`sideways`: the rear axle's offset from it, right positive, for the log;
        // the start check refuses more than 1.5 m). Falls back to the waypoint nearest the car (`nearest`, sideways at
        // MaxValue) when no step runs under it.
        public static int StartUnder(DrivenRides.Core.Routing.LaneGraph graph, CarController car, int nearest, Func<int, bool> goodStart, out float sideways)
        {
            sideways = float.MaxValue;
            var wrapped = NwhCar.Wrap(car.vehicleController, car.gameObject, "", out _);
            var rear = wrapped != null ? wrapped.RearAxle : car.transform.position;
            var forward = car.transform.forward;
            var best = nearest;
            // The steps from each waypoint near the car and into it, so a long step whose start is far behind counts too.
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
                    // Inside the step: the line starts at the step's start, so a rear axle behind it is off the line.
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

        // The bridge skip's fuel, as the game charges it (SkipBridgeHelper.GetFuelConsumption): the ride's own skip
        // teleports the car across, so the fuel the car would have burnt there is taken here.
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

        // The trip needs fuel in the tank; the car burns its own as it drives, and an empty tank mid-trip ends it
        // (OwnCarDrive.Trouble).: the trip needs about what the bridge skip charges for
        // the planned way (the car then burns its own as it drives).
        public static bool HasFuelFor(CarController car, float metres)
        {
            try { return SaveGameManager.Current?.gameVariables.disableVehicleFuel == true || car.fuelModule == null || !car.fuelModule.useFuel || car.GetCurrentFuel() > 0f; }
            catch (Exception ex) { Log.Error("Own car fuel check: " + ex.Message); return true; }
        }

        // Autopark as the options say: off, only cars that have it, or every car. The game's autopark is a fade and a
        // teleport into the nearest free spot (VehicleParkingHelper.ParkInClosestSpot), and it doesn't check the car.
        private static readonly FieldInfo? ParkingHelperField = AccessTools.Field(typeof(CarController), "vehicleParkingHelper");

        // Stepped by the ride, which stops stepping when it is cancelled; stop says so between steps too.
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
            // As vanilla, which only offers a spot below 10 km/h (VehicleParkingHelper.Update): a player already
            // driving off isn't pulled into a spot.
            if (car.CurrentSpeed >= 10f) { Log.Trace("Own car: no autopark, the car is moving."); yield break; }
            // As vanilla: a wrecked car doesn't autopark.
            if (Mathf.Abs(car.GetCurrentCondition() - 1f) < Mathf.Epsilon) { Log.Trace("Own car: no autopark, the car is wrecked."); yield break; }
            if (!(ParkingHelperField?.GetValue(car) is VehicleParkingHelper helper) || helper == null)
            {
                Log.Error("Own car autopark: the car has no parking helper.");
                yield break;
            }
            // The closest free spot, hunted for further out until one is found (Peter, 14 October 2026).
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
            // The steps of the game's ParkInClosestSpot, done here so each stage is known: the fade, a last check (the
            // player still in the car, the spot still free), the teleport and the legal parking state, and the
            // unfade, which always runs once the fade has.
            var position = spot.transform.position;
            var rotation = spot.transform.parent.rotation;
            if (spot.visuals != null) spot.visuals.enabled = false;
            yield return UiFader.Fade();
            var parked = false;
            if (!stop() && car != null && car.controlledByPlayer && !IsOccupied(helper, spot))
            {
                Teleport(car, position, rotation);
                car.vehicleInstance.parkingState = ParkingState.Legal;
                if (car.poi != null) car.poi.SetBackground(InstanceBehavior<GlobalReferences>.Instance.vehiclePOIBackgroundColor);
                car.UpdateParkingZone();
                car.SavePosition();
                // As vanilla: no new spot is offered while the car stays where it was parked.
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

        // How far out the hunt for a spot goes, in steps: the nearest free spot within the first radius that has one.
        private static readonly float[] HuntRadii = { 20f, 50f, 100f, 150f };
        private static readonly MethodInfo? GenerateSpotsMethod = AccessTools.Method(typeof(ParkingLaneGenerator), "GenerateAutoParkSpots");
        private static readonly FieldInfo? DimensionsField = AccessTools.Field(typeof(VehicleParkingHelper), "_vcom");
        private static readonly FieldInfo? LaneColliderField = AccessTools.Field(typeof(ParkingLaneGenerator), "_collider");
        private static readonly Collider[] HuntHits = new Collider[4096];
        // A spot no further above or below the car than this: not a ramp, a deck or a road over it.
        private const float HuntHeight = 3f;
        // Lanes whose spots are made per frame, so a dense district doesn't do them all at once.
        private const int LanesPerFrame = 8;

        private sealed class SpotHunt
        {
            public AutoParkSpot? Found;
            // Each spot once, though every ring's sphere holds the rings before it.
            public readonly HashSet<AutoParkSpot> SeenSpots = new HashSet<AutoParkSpot>();
            public int Seen => SeenSpots.Count;
        }

        // The game makes autopark spots only in the gaps of the parking lanes within 6 m of a car the player gets into,
        // out of or teleports (ParkingLaneGenerator.RegenerateAutoParkSpotsNear), and its search only looks around the
        // car (VehicleParkingHelper.FindNearestFreeSpot). The hunt makes the spots on every parking lane within each
        // radius in turn and takes the nearest free one long enough for the car, by the game's own measures (the car's
        // length plus the padding, VehicleParkingHelper.IsSpotOccupied).
        // Nearest counts the walk to the door too (`door`, the destination), so a spot down the same street beats one as
        // near through the block on the street behind.
        private static IEnumerator Hunt(CarController car, VehicleParkingHelper helper, SpotHunt hunt, Vector3? door)
        {
            var padding = InstanceBehavior<GlobalReferences>.Instance.autoParkSettings.VehiclePadding * 2f;
            var length = (DimensionsField?.GetValue(helper) as NWH.Common.CoM.VariableCenterOfMass)?.dimensions.z ?? 4.6f;
            var needs = length + padding;
            var made = new HashSet<ParkingLaneGenerator>();
            var reach = Reachable(car.transform.position);
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
                    if (lane == null || !made.Add(lane)) continue;
                    // As vanilla's RegenerateAutoParkSpotsNear: only lanes with their collider.
                    if (LaneColliderField != null && LaneColliderField.GetValue(lane) as Object == null) continue;
                    try { GenerateSpotsMethod?.Invoke(lane, null); any = true; }
                    catch (Exception ex) { Log.Error("Own car autopark, spots: " + ex.Message); }
                    if (made.Count % LanesPerFrame == 0) { yield return null; if (car == null) yield break; }
                }
                // The spots it replaced go at the end of the frame; the new ones are found after it.
                if (any) { yield return null; Physics.SyncTransforms(); }
                if (car == null) yield break;
                at = car.transform.position;
                n = Physics.OverlapSphereNonAlloc(at, radius, HuntHits, 1 << LayerHelper.AutoParkSpotsLayerIndex, QueryTriggerInteraction.Collide);
                for (var k = 0; k < n; k++)
                {
                    if (HuntHits[k] == null || !HuntHits[k].TryGetComponent<AutoParkSpot>(out var spot) || spot.maxVehicleLength < needs) continue;
                    hunt.SeenSpots.Add(spot);
                    var p = spot.transform.position;
                    var away = Vector3.Distance(at, p);
                    var score = away + (door is Vector3 dd ? 0.5f * Vector3.Distance(dd, p) : 0f);
                    // As vanilla: not a spot the car already stands in. The sphere catches a spot whose collider reaches
                    // into it, and a spot's collider spans its whole gap: only one whose middle (where the car goes) is
                    // inside this radius counts. Not on another level, and on a road the car can reach without a
                    // locked gate.
                    if (away < 1.5f || away > radius || score >= bestScore || Mathf.Abs(p.y - at.y) > HuntHeight || !reach(p) || IsOccupied(helper, spot)) continue;
                    best = spot;
                    bestScore = score;
                }
                // A spot further out is more than `radius` away, so it can't score lower than that.
                if (best != null && bestScore <= radius) break;
            }
            hunt.Found = best;
        }

        // Whether the car could drive to a point's lane: the lane waypoints joined to those near the car, either way
        // along a lane (a one-way street is reached round the block), within ReachMetres of the car, without a step
        // through a gate that is locked for the player (Gates). A spot counts if a waypoint within ReachLane of it is
        // among them. Without the lane graph everything counts.
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

        private static bool IsOccupied(VehicleParkingHelper helper, AutoParkSpot spot)
        {
            try { return OccupiedMethod?.Invoke(helper, new object[] { spot }) is bool taken && taken; }
            catch (Exception ex) { Log.Error("Own car autopark, spot check: " + ex.Message); return true; }
        }
    }

    // The own car driving itself with its own physics (docs/implementation-plan.md, NWH driving, phase 5): the player
    // stays in the driver's seat; the ride's NwhDrive writes the car's input in place of the player's keys. Begin hands
    // the input over (NWH's autoSetInput off, as for the mod's other NWH cars), End hands it back however the ride
    // ends. While it runs, the game's steering assist is held off (it adds its own angle each frame) and getting out
    // ends the ride where the car is.
    internal static class OwnCarDrive
    {
        private static CarController? active;
        private static bool ending;

        public static bool IsActive => active != null;
        public static CarController? Car => active;

        // The ride car for the own car: the player's car as it stands, wrapped for NwhDrive.
        public static NwhCar? Begin(CarController car, out string why)
        {
            why = "";
            if (active != null) { why = "the car is already driving itself"; return null; }
            var vehicle = car.vehicleController;
            if (vehicle == null || vehicle.input == null) { why = "the car has no NWH controller"; return null; }
            var wrapped = NwhCar.Wrap(vehicle, car.gameObject, car.vehicleType != null ? car.vehicleType.vehicleTypeName : car.name, out why);
            if (wrapped == null) return null;
            vehicle.input.autoSetInput = false;
            Zero(vehicle);
            active = car;
            Log.Trace($"Own car: driving itself ({wrapped.TypeName}, wheelbase {wrapped.Wheelbase:0.00} m).");
            return wrapped;
        }

        // Back to the player's keys, the car held on its handbrake until they take over. Safe to call twice.
        public static void End(string reason)
        {
            var car = active;
            if (car == null) return;
            active = null;
            try
            {
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

        // CarController.Update postfix: the steering assist (Settings) adds an angle every frame; not while the car
        // drives itself.
        public static void AfterCarUpdate(CarController car)
        {
            if (active != null && ReferenceEquals(car, active)) car.vehicleController.steering.externallyAddedAngle = 0f;
        }

        // CarController.ExitVehicle prefix: getting out ends the ride first, so the car is the player's again (and
        // stops) before the game takes them out. A ride ending on its own hands back through End, not here.
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

        // ItemPanelUI.ClickAutoPark and SkipBridgeHelper.SkipBridge prefixes: the game's own autopark and bridge skip
        // teleport the car out from under the drive; not while the car drives itself. The ride's own autopark at the
        // end and its own bridge skip don't go through them.
        public static bool AllowsTeleport()
        {
            if (active == null) return true;
            Notice.Text("drivenrides:notice_owncar_busy");
            return false;
        }

        // Why the trip can't go on, checked by the ride each frame: an empty tank or a wreck (condition 1, as the
        // game's own checks) would stall the drive until the watchdog gave up. Null while all is well.
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

        // The notice for a Trouble text.
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
