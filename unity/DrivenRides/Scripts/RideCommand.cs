#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Buildings;
using GleyTrafficSystem;
using HarmonyLib;
using Helpers;
using Newtonsoft.Json.Linq;
using Player.HUD.SmartphoneUI;
using PlayerActivity;
using UI;
using UnityEngine;
using Vehicles.Taxis;
using Object = UnityEngine.Object;

namespace DrivenRides
{
    // The rig's `ride` command (docs/turn-tests.md): a real ride through the game's own taxi flow, started from the
    // Mac, so rides are tested unattended. It hails the nearest taxi from traffic (or uses the nearest taxi stand, or
    // calls the private driver and waits for it), picks a destination building, and calls TaxiSystem.TravelTo as the
    // map's "Go" button does: the fare, the mod's TravelCoroutine prefix and the whole ride are the real ones. One
    // line per ride goes to ride-results.jsonl beside the rig's results.
    //
    //   ride [kind=taxi|stand|driver|own] [vehicle=..] [min=500] [max=1500] [x=.. z=..] [seed=N] [speed=1|2|3] [hamptons=on]
    //        [autopark=off|cars|every] [skip=N] [exit=N] [kerb=M] (own: the player's own car drives itself; it is put on a
    //        lane by the player first, or M metres to the right of it with kerb=M; skip presses Skip N seconds in, exit
    //        gets the player out N seconds in)
    // `ridemode nwh|gley|options` picks car physics or the Gley rides for the rides after it (TurnTests).
    internal static class RideCommand
    {
        public static bool Busy { get; private set; }

        private static readonly MethodInfo? TaxiStop = AccessTools.Method(typeof(TaxiController), "RequestVehicleStop");

        public static IEnumerator Run(Dictionary<string, string> args, string folder)
        {
            if (Busy) { Log.Error("Ride command: one is already running."); yield break; }
            Busy = true;
            var result = new JObject { ["version"] = DrivenRidesMod.Version, ["started"] = DateTime.Now.ToString("s", CultureInfo.InvariantCulture) };
            try
            {
                var steps = Steps(args, result);
                while (true)
                {
                    object? current;
                    try
                    {
                        if (!steps.MoveNext()) break;
                        current = steps.Current;
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Ride command: " + ex);
                        result["error"] = ex.Message;
                        break;
                    }
                    yield return current;
                }
            }
            finally
            {
                Busy = false;
                result["ended"] = DateTime.Now.ToString("s", CultureInfo.InvariantCulture);
                try { File.AppendAllText(Path.Combine(folder, "ride-results.jsonl"), result.ToString(Newtonsoft.Json.Formatting.None) + "\n"); }
                catch (Exception ex) { Log.Error("Ride command, results: " + ex.Message); }
                Log.Info("Ride command: " + result.ToString(Newtonsoft.Json.Formatting.None));
            }
        }

        private static IEnumerator Steps(Dictionary<string, string> args, JObject result)
        {
            var kind = args.TryGetValue("kind", out var k) ? k : "taxi";
            result["kind"] = kind;
            result["carPhysics"] = Options.CarPhysics;
            string? refuse = null;
            if (SaveGameManager.Current == null) refuse = "no game loaded";
            else if (TurnTests.Running) refuse = "turn tests are running";
            else if (Ride.Active != null || TaxiSystem.IsTraveling) refuse = "a ride is under way";
            else if (BuildingManager.IsInsideBuilding) refuse = "the player is inside a building";
            else if (CityMap.IsOpen || PlayerActivityUI.IsPanelOpen) refuse = "the map or an activity panel is open";
            if (refuse != null) { result["refused"] = refuse; yield break; }

            var player = InstanceBehavior<GameManager>.Instance.playerController.transform;
            var target = Destination(args, player.position, result);
            if (target == null) { result["refused"] = "no destination building found"; yield break; }

            ITaxi? taxi = null;
            CarController? ownCar = null;
            switch (kind)
            {
                case "taxi":
                    var hailed = Object.FindObjectsOfType<TaxiController>()
                        .Where(t => t != null && t.gameObject.activeInHierarchy && t.GetComponent<VehicleComponent>() is VehicleComponent v && v.GetIndex() >= 0)
                        .OrderBy(t => Vector3.Distance(t.transform.position, player.position)).FirstOrDefault();
                    if (hailed == null || Vector3.Distance(hailed.transform.position, player.position) > 200f) { result["refused"] = "no taxi in traffic within 200 m"; yield break; }
                    // As a click on the taxi does: the car stops where it is.
                    TaxiStop?.Invoke(hailed, null);
                    result["taxiMetres"] = Math.Round(Vector3.Distance(hailed.transform.position, player.position), 1);
                    taxi = hailed;
                    break;
                case "stand":
                    var stand = Object.FindObjectsOfType<PermanentTaxiController>().Where(t => t != null && t.gameObject.activeInHierarchy)
                        .OrderBy(t => Vector3.Distance(t.transform.position, player.position)).FirstOrDefault();
                    if (stand == null || Vector3.Distance(stand.transform.position, player.position) > 400f) { result["refused"] = "no taxi stand within 400 m"; yield break; }
                    result["taxiMetres"] = Math.Round(Vector3.Distance(stand.transform.position, player.position), 1);
                    taxi = stand;
                    break;
                case "driver":
                    // Called as the L key calls it (the phone's own handler), unless one waits already; then it drives
                    // to the player and stops, which takes up to a couple of minutes.
                    if (SmartphonePrivateDriverUI.CurrentVehicle == null)
                    {
                        var vehicles = SaveGameManager.Current.privateDriverVehicleInstances;
                        if (vehicles == null || vehicles.Count == 0) { result["refused"] = "no private driver vehicle"; yield break; }
                        var vehicle = args.TryGetValue("vehicle", out var which) ? vehicles.FirstOrDefault(v => v.vehicleTypeName.Contains(which)) ?? vehicles[0] : vehicles[0];
                        InstanceBehavior<UIs>.Instance.smartphoneUI.privateDriverUI.OnClickVehicle(vehicle);
                        result["called"] = vehicle.vehicleTypeName;
                    }
                    var waitFrom = Time.unscaledTime;
                    PrivateDriverVehicle? driver = null;
                    while (Time.unscaledTime - waitFrom < 180f)
                    {
                        driver = SmartphonePrivateDriverUI.CurrentVehicle;
                        if (driver != null)
                        {
                            var body = driver.GetComponent<Rigidbody>();
                            var near = Vector3.Distance(driver.transform.position, player.position) < 25f;
                            // A parked driver (called from indoors) is there at once.
                            if (driver.GetVehicleIndex() < 0 || (near && (body == null || body.velocity.magnitude < 0.5f))) break;
                        }
                        yield return null;
                    }
                    if (driver == null) { result["refused"] = "the driver didn't come"; yield break; }
                    result["waitedSeconds"] = Math.Round(Time.unscaledTime - waitFrom, 1);
                    result["taxiMetres"] = Math.Round(Vector3.Distance(driver.transform.position, player.position), 1);
                    taxi = driver;
                    break;
                case "own":
                    CarController? own = null;
                    var seat = OwnCarSeat(args, result, c => own = c);
                    while (seat.MoveNext()) yield return seat.Current;
                    if (own == null) yield break;
                    ownCar = own;
                    result["fuelBefore"] = Math.Round(own.GetCurrentFuel(), 3);
                    if (args.TryGetValue("autopark", out var ap))
                        Options.AutoparkOverride = ap == "off" ? AutoparkMode.Off : ap == "every" ? AutoparkMode.Every : AutoparkMode.CarsWithIt;
                    result["autopark"] = Options.Autopark.ToString();
                    break;
                default:
                    result["refused"] = "unknown kind " + kind;
                    yield break;
            }

            // The map in taxi mode, as the taxi's click opens it (for the own car, as L in the driver's seat does);
            // then the map's Go.
            if (ownCar != null)
            {
                OwnCarTaxi.Open();
                if (!CityMap.IsOpen) { result["refused"] = "L in the car didn't open the map (see the notice in the log)"; Options.AutoparkOverride = null; yield break; }
            }
            else InstanceBehavior<CityManager>.Instance.cityMap.SetTaxiMode(taxi);
            var until = Time.unscaledTime + 0.5f;
            while (Time.unscaledTime < until) yield return null;
            Ride.LastEnd = "";
            Ride.LastTally = default;
            var started = Time.unscaledTime;
            InstanceBehavior<TaxiSystem>.Instance.TravelTo(target);
            yield return null;
            result["driven"] = Ride.Active != null;
            if (Ride.Active != null && args.TryGetValue("speed", out var sp) && float.TryParse(sp, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
                Ride.Active.SetTestSpeed(Mathf.Clamp(speed, 1f, 3f));
            if (!TaxiSystem.IsTraveling && Ride.Active == null) { result["refused"] = "TravelTo started no ride (fare refused, or the map closed)"; yield break; }
            // To the end of the ride, vanilla or driven, at most 20 minutes. skip=N presses Skip N seconds in; exit=N
            // gets the player out of the own car N seconds in.
            float After(string name) => args.TryGetValue(name, out var v) && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : -1f;
            var skipAt = After("skip");
            var exitAt = After("exit");
            while ((Ride.Active != null || TaxiSystem.IsTraveling) && Time.unscaledTime - started < 1200f)
            {
                var elapsed = Time.unscaledTime - started;
                if (skipAt >= 0f && elapsed >= skipAt && Ride.Active != null) { Ride.Active.RequestSkip(); result["skipped"] = Math.Round(elapsed, 1); skipAt = -1f; }
                if (exitAt >= 0f && elapsed >= exitAt && ownCar != null) { ownCar.ExitVehicle(); result["gotOut"] = Math.Round(elapsed, 1); exitAt = -1f; }
                yield return null;
            }
            result["seconds"] = Math.Round(Time.unscaledTime - started, 1);
            result["end"] = Ride.LastEnd.Length > 0 ? Ride.LastEnd : "vanilla ride";
            if (Ride.Active != null || TaxiSystem.IsTraveling) result["end"] = "still under way after 20 minutes";
            var tally = Ride.LastTally;
            result["contacts"] = tally.Contacts;
            result["contactsCaused"] = tally.Caused;
            result["redRuns"] = tally.RedRuns;
            result["stalls"] = tally.Stalls;
            result["arrivedMetres"] = Math.Round(Vector3.Distance(player.position, target.GetNavMeshTargetPosition()), 1);
            if (ownCar != null)
            {
                // Autopark at the end runs inside the ride; give a parked car a moment to settle, then the car's state.
                var settle = Time.unscaledTime + 2f;
                while (Time.unscaledTime < settle) yield return null;
                result["inCar"] = SaveGameManager.Current?.ActiveVehicleId == ownCar.vehicleInstance.id;
                result["keysBack"] = ownCar.vehicleController.input.autoSetInput;
                result["driveLeft"] = ownCar.GetComponent<NwhDrive>() != null;
                result["parkingState"] = ownCar.vehicleInstance.parkingState.ToString();
                result["fuelAfter"] = Math.Round(ownCar.GetCurrentFuel(), 3);
                result["carMetres"] = Math.Round(Vector3.Distance(ownCar.transform.position, target.GetNavMeshTargetPosition()), 1);
                Options.AutoparkOverride = null;
            }
        }

        // The player's own car (vehicle=..: by type name), put on the nearest free driving lane by the player, facing
        // along it, and the player in the driver's seat, as if they had stopped there.
        private static IEnumerator OwnCarSeat(Dictionary<string, string> args, JObject result, Action<CarController> found)
        {
            var cars = VehicleHelper.AllPlayerVehicles.OfType<CarController>().Where(c => c != null && c.vehicleType != null && !c.vehicleType.spawnInPlayerObject).ToList();
            var car = args.TryGetValue("vehicle", out var which) ? cars.FirstOrDefault(c => c.vehicleType.vehicleTypeName.Contains(which)) : null;
            car ??= VehicleHelper.GetCurrentVehicleBase() as CarController ?? cars.FirstOrDefault();
            if (car == null) { result["refused"] = "the player owns no car"; yield break; }
            result["car"] = car.vehicleType.vehicleTypeName;
            result["autoparkSupported"] = car.vehicleType.autoParkSupported;
            var player = InstanceBehavior<GameManager>.Instance.playerController;
            // kerb=M: seated M metres to the right of the lane, as a car parked at the kerb (it pulls out first).
            var kerb = args.TryGetValue("kerb", out var kv) && float.TryParse(kv, NumberStyles.Float, CultureInfo.InvariantCulture, out var km) ? km : 0f;
            if (kerb != 0f && ReferenceEquals(VehicleHelper.GetCurrentVehicleBase(), car))
            {
                car.ExitVehicle();
                var settleOut = Time.unscaledTime + 1f;
                while (Time.unscaledTime < settleOut) yield return null;
            }
            if (!ReferenceEquals(VehicleHelper.GetCurrentVehicleBase(), car))
            {
                if (VehicleHelper.GetCurrentVehicleBase() is VehicleController other) other.ExitVehicle();
                var y = player.transform.position.y;
                var lane = TrafficManager.Instance.GetClosestWaypoint(player.transform.position, 80f,
                    w => w.neighbors != null && w.neighbors.Count > 0 && TrafficManager.WithinHeight(w, y, 3f) && TrafficManager.IsNotIntersection(w)
                         && w.allowedCars != null && w.allowedCars.Contains(VehicleTypes.Car));
                if (lane == null) { result["refused"] = "no driving lane within 80 m of the player"; yield break; }
                var rotation = TrafficManager.Instance.GetWaypointOrientation(lane);
                // The car's rear axle on the lane waypoint, as a car stopped in the lane stands.
                var wrapped = NwhCar.Wrap(car.vehicleController, car.gameObject, "", out _);
                var at = lane.position + rotation * new Vector3(kerb, 0f, -(wrapped != null ? wrapped.RearZ : 0f));
                OwnCarTaxi.Teleport(car, at, rotation);
                VehicleHelper.DestroyBlockingVehicles(car.gameObject, car.vehicleType, car.transform);
                result["seatedAt"] = new JArray(Math.Round(at.x, 1), Math.Round(at.z, 1), lane.listIndex);
                yield return null;
                player.Character.WarpSafely(car.GetNavMeshTargetPosition());
                yield return null;
                car.EnterVehicle();
            }
            // Settled on its wheels and standing.
            var until = Time.unscaledTime + 8f;
            var body = car.vehicleController.vehicleRigidbody;
            while (Time.unscaledTime < until && (body == null || body.velocity.magnitude > 0.1f || Time.unscaledTime < until - 6f)) yield return null;
            if (!ReferenceEquals(VehicleHelper.GetCurrentVehicleBase(), car)) { result["refused"] = "couldn't seat the player in the car"; yield break; }
            found(car);
        }

        // A building by the map's coordinates (x, z: the nearest one), or a random one between min and max metres from
        // the player, Manhattan only unless hamptons=on.
        private static EntityController? Destination(Dictionary<string, string> args, Vector3 from, JObject result)
        {
            float Arg(string name, float fallback) => args.TryGetValue(name, out var v) && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : fallback;
            // Inactive ones too: the Hamptons' buildings are switched off while the player is in Manhattan (hamptons=on
            // found 0 candidates). None behind a gated community's gate: the game's taxi refuses those unless the player
            // owns a building inside (TaxiSystem.CanEnterPrivateFence).
            var all = Object.FindObjectsOfType<CityBuildingController>(true).Where(b => b != null && b.building != null && b.building.locatedInsidePrivateFenceIndex < 0).ToList();
            CityBuildingController? pick;
            if (args.ContainsKey("x") && args.ContainsKey("z"))
            {
                var at = new Vector3(Arg("x", 0f), 0f, Arg("z", 0f));
                pick = all.OrderBy(b => Flat(b.transform.position - at)).FirstOrDefault();
            }
            else
            {
                float min = Arg("min", 500f), max = Arg("max", 1500f);
                var hamptons = args.TryGetValue("hamptons", out var h) && h == "on";
                var fits = all.Where(b => (hamptons || b.building.Neighbourhood != "ba:neighborhood_thehamptons") && Flat(b.transform.position - from) >= min && Flat(b.transform.position - from) <= max)
                    .OrderBy(b => b.GetInstanceID()).ToList();
                var random = new System.Random(args.TryGetValue("seed", out var s) && int.TryParse(s, out var seed) ? seed : Environment.TickCount);
                pick = fits.Count > 0 ? fits[random.Next(fits.Count)] : null;
                result["candidates"] = fits.Count;
            }
            if (pick == null) return null;
            result["target"] = $"{pick.building.StreetNumber} {pick.building.StreetName}";
            var p = pick.GetNavMeshTargetPosition();
            result["targetAt"] = new JArray(Math.Round(p.x, 1), Math.Round(p.z, 1));
            result["straightMetres"] = Math.Round(Flat(p - from), 0);
            return pick;
        }

        private static float Flat(Vector3 v) => new Vector2(v.x, v.z).magnitude;
    }
}
