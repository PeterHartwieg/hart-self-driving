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
            if (!Options.OwnCar) { Notice.Show("Self driving for your own car is off in the options."); return; }
            if (!(VehicleHelper.GetCurrentVehicleBase() is CarController car) || car.vehicleType == null || car.vehicleType.spawnInPlayerObject)
            {
                Notice.Show("Only a car can drive itself.");
                return;
            }
            if (BuildingManager.IsInsideBuilding || UndergroundParkingManager.IsInsideParking) { Notice.Show("Drive out onto the street first."); return; }
            if (car.CurrentSpeed > 5f) { Notice.Show("Stop the car first."); return; }
            // The game's own test (VehicleParkingHelper.ParkInClosestSpot): a condition of 1 is a wreck.
            if (Mathf.Abs(car.GetCurrentCondition() - 1f) < Mathf.Epsilon) { Notice.Show("The car is too damaged to drive."); return; }
            if (instance == null)
            {
                var host = new GameObject("DrivenRides.OwnCarTaxi");
                Object.DontDestroyOnLoad(host);
                instance = host.AddComponent<OwnCarTaxi>();
            }
            instance.Car = car;
            Log.Info($"Own car: choosing a destination for {car.vehicleType.vehicleTypeName}.");
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
            else Log.Info($"Own car: planned as {best.name} ({length:0.0} m, {bestDiff:0.0} m off; the car has no traffic twin).");
            return best;
        }

        // The ride car for the seam (NwhRideCar): the player inside, the car not the mod's, the keys handed back at
        // every end, and at the end the arrival: a skipped or given-up trip puts the car on the lane nearest the
        // destination first (as vanilla puts a passenger at the door), then autopark as the options say.
        public static NwhRideCar RideCar(CarController car, NwhCar wrapped, DrivenRides.Core.Drive.CarSpec spec, EntityController? target) => new NwhRideCar
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
            Arrive = () => Arrive(car, target),
        };

        private static IEnumerator Arrive(CarController car, EntityController? target)
        {
            if (car == null) yield break;
            var drive = car.GetComponent<NwhDrive>();
            if (drive != null && !drive.Arrived && target != null)
            {
                var destination = target.GetNavMeshTargetPosition();
                var y = destination.y;
                var lane = TrafficManager.Instance.GetClosestWaypoint(destination, 50f,
                    w => w.allowedCars != null && w.allowedCars.Count > 0 && TrafficManager.WithinHeight(w, y, 5f) && TrafficManager.IsNotIntersection(w));
                if (lane != null)
                {
                    Teleport(car, lane.position, TrafficManager.Instance.GetWaypointOrientation(lane));
                    VehicleHelper.DestroyBlockingVehicles(car.gameObject, car.vehicleType, car.transform);
                    car.SavePosition();
                    Log.Info($"Own car: the trip was cut short; the car is on the lane at waypoint {lane.listIndex}, {Vector3.Distance(lane.position, destination):0} m from the destination.");
                }
                else Log.Info("Own car: the trip was cut short and there is no lane near the destination; the car stays where it is.");
                yield return null;
            }
            var park = Autopark(car, () => false);
            while (park.MoveNext()) yield return park.Current;
        }

        // Fuel for the way driven, as the game charges a skipped bridge (SkipBridgeHelper.GetFuelConsumption).
        private static readonly MethodInfo? FuelMethod = AccessTools.Method(typeof(SkipBridgeHelper), "GetFuelConsumption");

        private static float FuelFor(CarController car, float metres)
        {
            if (SaveGameManager.Current?.gameVariables.disableVehicleFuel == true || car.fuelModule == null || !car.fuelModule.useFuel) return 0f;
            return FuelMethod?.Invoke(null, new object[] { car, metres }) is float used && used > 0f ? used : 0f;
        }

        // As the bridge skip refuses a car without the fuel for it: the trip needs about what the bridge skip charges for
        // the planned way (the car then burns its own as it drives).
        public static bool HasFuelFor(CarController car, float metres)
        {
            try { return car.GetCurrentFuel() >= FuelFor(car, metres); }
            catch (Exception ex) { Log.Error("Own car fuel check: " + ex.Message); return true; }
        }

        // Autopark as the options say: off, only cars that have it, or every car. The game's autopark is a fade and a
        // teleport into the nearest free spot (VehicleParkingHelper.ParkInClosestSpot), and it doesn't check the car.
        private static readonly MethodInfo? FindSpotMethod = AccessTools.Method(typeof(VehicleParkingHelper), "FindNearestFreeSpot");
        private static readonly FieldInfo? ParkingHelperField = AccessTools.Field(typeof(CarController), "vehicleParkingHelper");

        // Stepped by the ride, which stops stepping when it is cancelled; stop says so between steps too.
        public static IEnumerator Autopark(CarController car, Func<bool> stop)
        {
            var mode = Options.Autopark;
            var supported = car.vehicleType != null && car.vehicleType.autoParkSupported;
            if (mode == AutoparkMode.Off || (mode == AutoparkMode.CarsWithIt && !supported))
            {
                Log.Info($"Own car: no autopark ({(mode == AutoparkMode.Off ? "off in the options" : "the car has none")}).");
                yield break;
            }
            // The spots near the car are rebuilt on entering it; give them two frames.
            yield return null;
            yield return null;
            if (stop() || car == null || !car.controlledByPlayer) yield break;
            // As vanilla, which only offers a spot below 10 km/h (VehicleParkingHelper.Update): a player already
            // driving off isn't pulled into a spot.
            if (car.CurrentSpeed >= 10f) { Log.Info("Own car: no autopark, the car is moving."); yield break; }
            // As vanilla: a wrecked car doesn't autopark.
            if (Mathf.Abs(car.GetCurrentCondition() - 1f) < Mathf.Epsilon) { Log.Info("Own car: no autopark, the car is wrecked."); yield break; }
            if (!(ParkingHelperField?.GetValue(car) is VehicleParkingHelper helper) || helper == null || FindSpotMethod == null)
            {
                Log.Error("Own car autopark: the car has no parking helper.");
                yield break;
            }
            AutoParkSpot? spot = null;
            try { spot = FindSpotMethod.Invoke(helper, null) as AutoParkSpot; }
            catch (Exception ex) { Log.Error("Own car autopark: " + ex.Message); }
            if (spot == null)
            {
                Notice.Show("No free parking spot here; park the car yourself.");
                Log.Info("Own car: no free autopark spot nearby.");
                yield break;
            }
            Log.Info($"Own car: autoparking {Vector3.Distance(car.transform.position, spot.transform.position):0.0} m away{(supported ? "" : " (the car has no autopark of its own)")}.");
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
            Log.Info(parked ? "Own car: autoparked." : "Own car: autopark called off (the player got out, or the spot was taken).");
        }

        // The game's teleport writes the rigidbody; its transform follows only at the next physics step, and the parking
        // check, the save and anything placed by the car read the transform, so it is brought along at once.
        public static void Teleport(CarController car, Vector3 position, Quaternion rotation)
        {
            VehicleHelper.TeleportVehicleToGround(car, position, rotation);
            if (car.TryGetComponent<Rigidbody>(out var body)) { car.transform.SetPositionAndRotation(body.position, body.rotation); Physics.SyncTransforms(); }
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
            Log.Info($"Own car: driving itself ({wrapped.TypeName}, wheelbase {wrapped.Wheelbase:0.00} m).");
            return wrapped;
        }

        // Back to the player's keys, the car held on its brakes until they take over. Safe to call twice.
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
            Log.Info("Own car: the keys are the player's again (" + reason + ").");
        }

        private static void Zero(NWH.VehiclePhysics2.VehicleController vehicle)
        {
            var input = vehicle.input;
            input.Throttle = 0f;
            input.Steering = 0f;
            input.Handbrake = 0f;
            input.Brakes = 1f;
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
                Log.Info("Own car: the player got out during the trip.");
                Ride.CancelActive("the player got out of the car");
                End("the player got out");
            }
            finally { ending = false; }
        }

        public static void Drop()
        {
            active = null;
            ending = false;
        }
    }

    internal enum AutoparkMode { Off = 0, CarsWithIt = 1, Every = 2 }
}
