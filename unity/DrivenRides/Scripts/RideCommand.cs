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
    //   ride [kind=taxi|stand|driver] [min=500] [max=1500] [x=.. z=..] [seed=N] [speed=1|2|3] [hamptons=on]
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
                default:
                    result["refused"] = "unknown kind " + kind;
                    yield break;
            }

            // The map in taxi mode, as the taxi's click opens it; then the map's Go.
            InstanceBehavior<CityManager>.Instance.cityMap.SetTaxiMode(taxi);
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
            // To the end of the ride, vanilla or driven, at most 20 minutes.
            while ((Ride.Active != null || TaxiSystem.IsTraveling) && Time.unscaledTime - started < 1200f) yield return null;
            result["seconds"] = Math.Round(Time.unscaledTime - started, 1);
            result["end"] = Ride.LastEnd.Length > 0 ? Ride.LastEnd : "vanilla ride";
            if (Ride.Active != null || TaxiSystem.IsTraveling) result["end"] = "still under way after 20 minutes";
            var tally = Ride.LastTally;
            result["contacts"] = tally.Contacts;
            result["contactsCaused"] = tally.Caused;
            result["redRuns"] = tally.RedRuns;
            result["stalls"] = tally.Stalls;
            result["arrivedMetres"] = Math.Round(Vector3.Distance(player.position, target.GetNavMeshTargetPosition()), 1);
        }

        // A building by the map's coordinates (x, z: the nearest one), or a random one between min and max metres from
        // the player, Manhattan only unless hamptons=on.
        private static EntityController? Destination(Dictionary<string, string> args, Vector3 from, JObject result)
        {
            float Arg(string name, float fallback) => args.TryGetValue(name, out var v) && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : fallback;
            var all = Object.FindObjectsOfType<CityBuildingController>().Where(b => b != null && b.building != null).ToList();
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
