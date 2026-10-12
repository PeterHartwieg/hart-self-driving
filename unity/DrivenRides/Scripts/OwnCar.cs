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
            hiddenCars.Clear();
        }

        public void DriveAway() { }

        // Only vanilla's ride asks for these, and an own-car ride never falls back to it.
        public VehicleComponent GetVehiclePrefab() => null!;

        public VehicleComponent InstantiateVehicle(Waypoint waypoint) => null!;

        public string GetHappinessModifierName() => null!;

        // Hidden while the stand-in drives: no meshes, no colliders, held in place. It stays active, so it stays in the
        // game's list of player vehicles and saves where it stands.
        private static readonly Dictionary<CarController, (List<Renderer> Renderers, List<Collider> Colliders)> hiddenCars =
            new Dictionary<CarController, (List<Renderer>, List<Collider>)>();

        public static void Hide(CarController car)
        {
            if (hiddenCars.ContainsKey(car)) return;
            var renderers = new List<Renderer>();
            var colliders = new List<Collider>();
            foreach (var r in car.GetComponentsInChildren<Renderer>()) if (r.enabled) { r.enabled = false; renderers.Add(r); }
            foreach (var c in car.GetComponentsInChildren<Collider>()) if (c.enabled) { c.enabled = false; colliders.Add(c); }
            car.SetFreeze(true);
            // The map marker stays: vanilla's exit shows it, at the start, where the car is until it arrives.
            hiddenCars[car] = (renderers, colliders);
            Log.Info($"Own car hidden ({renderers.Count} parts, {colliders.Count} colliders).");
        }

        public static void Show(CarController car)
        {
            if (car == null || !hiddenCars.TryGetValue(car, out var was)) return;
            hiddenCars.Remove(car);
            foreach (var r in was.Renderers) if (r != null) r.enabled = true;
            foreach (var c in was.Colliders) if (c != null) c.enabled = true;
            car.SetFreeze(false);
        }

        // The hidden car's id, for the hourly parking charges; null when no car is hidden.
        public static string? HiddenId()
        {
            foreach (var car in hiddenCars.Keys) if (car != null && car.vehicleInstance != null) return car.vehicleInstance.id;
            return null;
        }

        // Fuel for the way driven, as the game charges a skipped bridge (SkipBridgeHelper.GetFuelConsumption).
        private static readonly MethodInfo? FuelMethod = AccessTools.Method(typeof(SkipBridgeHelper), "GetFuelConsumption");

        private static float FuelFor(CarController car, float metres)
        {
            if (SaveGameManager.Current?.gameVariables.disableVehicleFuel == true || car.fuelModule == null || !car.fuelModule.useFuel) return 0f;
            return FuelMethod?.Invoke(null, new object[] { car, metres }) is float used && used > 0f ? used : 0f;
        }

        // As the bridge skip refuses a car without the fuel for it.
        public static bool HasFuelFor(CarController car, float metres)
        {
            try { return car.GetCurrentFuel() >= FuelFor(car, metres); }
            catch (Exception ex) { Log.Error("Own car fuel check: " + ex.Message); return true; }
        }

        public static void ChargeFuel(CarController car, float metres)
        {
            try
            {
                var used = FuelFor(car, metres);
                if (used <= 0f) return;
                var left = Mathf.Max(0f, car.GetCurrentFuel() - used);
                car.SetFuel(left);
                Log.Info($"Own car: {used:0.00} fuel for {metres:0} m, {left:0.00} left.");
            }
            catch (Exception ex) { Log.Error("Own car fuel: " + ex.Message); }
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
                VehicleHelper.TeleportVehicleToGround(car, position, rotation);
                car.vehicleInstance.parkingState = ParkingState.Legal;
                if (car.poi != null) car.poi.SetBackground(InstanceBehavior<GlobalReferences>.Instance.vehiclePOIBackgroundColor);
                car.UpdateParkingZone();
                car.SavePosition();
                parked = true;
            }
            yield return UiFader.UnFade();
            Log.Info(parked ? "Own car: autoparked." : "Own car: autopark called off (the player got out, or the spot was taken).");
        }

        private static readonly MethodInfo? OccupiedMethod = AccessTools.Method(typeof(VehicleParkingHelper), "IsSpotOccupied", new[] { typeof(AutoParkSpot) });

        private static bool IsOccupied(VehicleParkingHelper helper, AutoParkSpot spot)
        {
            try { return OccupiedMethod?.Invoke(helper, new object[] { spot }) is bool taken && taken; }
            catch (Exception ex) { Log.Error("Own car autopark, spot check: " + ex.Message); return true; }
        }
    }

    internal enum AutoparkMode { Off = 0, CarsWithIt = 1, Every = 2 }
}
