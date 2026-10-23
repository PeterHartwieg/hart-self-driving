#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HarmonyLib;
using Helpers;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DrivenRides
{
    // Test-mode only. Uses the game's parking-slot poses, never clears an occupied space.
    internal static class ParkingTests
    {
        private static readonly System.Reflection.FieldInfo? SideBySide = AccessTools.Field(typeof(ParkingLaneGenerator), "sideBySideParkingSpots");
        private static bool Bays(ParkingLaneGenerator lane) => SideBySide?.GetValue(lane) is bool value && value;
        private static string Key(ParkingLaneGenerator lane)
        {
            var p = lane.GetWorldCenterPosition();
            return FormattableString.Invariant($"{p.x:0.00},{p.z:0.00},{p.y:0.00}:{lane.name}");
        }
        public static void Survey(string folder)
        {
            var rows = new JArray();
            foreach (var lane in Object.FindObjectsOfType<ParkingLaneGenerator>(true).OrderBy(Key))
            {
                var p = lane.GetWorldCenterPosition();
                rows.Add(new JObject { ["key"] = Key(lane), ["bays"] = Bays(lane), ["active"] = lane.gameObject.activeInHierarchy,
                    ["at"] = new JArray(p.x, p.y, p.z), ["yaw"] = lane.transform.eulerAngles.y,
                    ["parent"] = lane.transform.parent != null ? lane.transform.parent.name : "",
                    ["building"] = lane.building != null ? lane.building.StreetName + "#" + lane.building.StreetNumber : "" });
            }
            File.WriteAllText(Path.Combine(folder, "parking-sites.json"), rows.ToString());
            Log.Info($"Parking test: surveyed {rows.Count} parking areas.");
        }

        public static void GroundSurvey(string folder)
        {
            var at = InstanceBehavior<GameManager>.Instance.playerController.transform.position;
            string ScenePath(Transform t) => t.parent != null ? ScenePath(t.parent) + "/" + t.name : t.name;
            var rows = new JArray();
            foreach (var renderer in Object.FindObjectsOfType<MeshRenderer>())
            {
                if (Vector3.Distance(renderer.bounds.ClosestPoint(at), at) > 65) continue;
                rows.Add(new JObject { ["path"] = ScenePath(renderer.transform), ["layer"] = LayerMask.LayerToName(renderer.gameObject.layer),
                    ["centre"] = new JArray(renderer.bounds.center.x, renderer.bounds.center.y, renderer.bounds.center.z),
                    ["size"] = new JArray(renderer.bounds.size.x, renderer.bounds.size.y, renderer.bounds.size.z),
                    ["materials"] = new JArray(renderer.sharedMaterials.Select(m => m != null ? m.name : "")),
                    ["mesh"] = renderer.GetComponent<MeshFilter>()?.sharedMesh?.name ?? "",
                    ["readable"] = renderer.GetComponent<MeshFilter>()?.sharedMesh?.isReadable ?? false,
                    ["staticBatch"] = renderer.isPartOfStaticBatch, ["submeshStart"] = renderer.subMeshStartIndex });
            }
            File.WriteAllText(Path.Combine(folder, "parking-ground.json"), rows.ToString());
            var triangles = new JArray();
            foreach (var renderer in Object.FindObjectsOfType<MeshRenderer>())
            {
                if (Vector3.Distance(renderer.bounds.ClosestPoint(at), at) > 50 || renderer.gameObject.layer != LayerMask.NameToLayer("Sidewalk")) continue;
                var mesh = renderer.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh == null) continue;
                for (var sub = 0; sub < renderer.sharedMaterials.Length; sub++)
                {
                    var material = renderer.sharedMaterials[sub]?.name ?? "";
                    if (!material.Contains("RoadSideCurb") && !material.Contains("Asphalt")) continue;
                    var points = ParkingSurface.Triangles(renderer, sub);
                    if (points == null) continue;
                    for (var i = 0; i < points.Length; i += 3)
                    {
                        var a = points[i]; var b = points[i+1]; var c = points[i+2];
                        if (Mathf.Abs(a.y - at.y) > 1 || Mathf.Abs(b.y-a.y) > 0.02f || Mathf.Abs(c.y-a.y) > 0.02f) continue;
                        if ((a - b).sqrMagnitude < 0.01f) continue;
                        triangles.Add(new JObject { ["path"] = ScenePath(renderer.transform), ["material"] = material,
                            ["a"] = new JArray(a.x,a.y,a.z), ["b"] = new JArray(b.x,b.y,b.z), ["c"] = new JArray(c.x,c.y,c.z) });
                    }
                }
            }
            File.WriteAllText(Path.Combine(folder, "parking-triangles.json"), triangles.ToString());
            Log.Info($"Parking test: surveyed {rows.Count} nearby surfaces and props.");
        }

        public static IEnumerator Seat(CarController owner, Dictionary<string, string> args, JObject result)
        {
            if (!TestMode.On || SaveGameManager.Current == null) { result["refused"]="parking fixture requires a loaded test world"; yield break; }
            var seed = int.Parse(args["parkseed"], CultureInfo.InvariantCulture);
            var random = new System.Random(seed);
            var mode = args.TryGetValue("parking", out var m) ? m : "bays";
            var lanes = Object.FindObjectsOfType<ParkingLaneGenerator>(true)
                .Where(l => !l.isHandicapParking && !l.designatedDeliveryVanSpot && l.gameObject.activeInHierarchy
                    && (mode == "all" || Bays(l) == (mode != "parallel")))
                .OrderBy(Key).ToList();
            if (args.TryGetValue("parknear", out var near))
            {
                var xy = near.Split(','); var x = float.Parse(xy[0], CultureInfo.InvariantCulture); var z = float.Parse(xy[1], CultureInfo.InvariantCulture);
                lanes = lanes.OrderBy(l => Vector2.Distance(new Vector2(x, z), new Vector2(l.GetWorldCenterPosition().x, l.GetWorldCenterPosition().z))).Take(4).ToList();
            }
            for (var i = lanes.Count - 1; i > 0; i--) { var j = random.Next(i + 1); var old = lanes[i]; lanes[i] = lanes[j]; lanes[j] = old; }
            result["parkingSeed"] = seed; result["parkingMode"] = mode;
            var wrapped = NwhCar.Wrap(owner.vehicleController, owner.gameObject, "", out _);
            if (wrapped == null) { result["refused"] = "cannot measure the test car"; yield break; }
            var player = InstanceBehavior<GameManager>.Instance.playerController;
            if (VehicleHelper.GetCurrentVehicleBase() is VehicleController oldCar) { oldCar.ExitVehicle(); yield return null; }
            foreach (var lane in lanes.Take(20))
            {
                // Populate the area before choosing a free space. The seeded pose is recorded as well: occupancy
                // may differ after a reload even for the same seed.
                player.Character.WarpSafely(lane.GetWorldCenterPosition());
                var settle = Time.unscaledTime + 2f;
                while (Time.unscaledTime < settle) yield return null;
                for (var attempt = 0; attempt < 8; attempt++)
                {
                    Vector3 p; Quaternion r; bool found;
                    var state = UnityEngine.Random.state;
                    try { UnityEngine.Random.InitState(random.Next()); found = lane.TryGetRandomFreeSpotForPlayerVehicle(out p, out r); }
                    finally { UnityEngine.Random.state = state; }
                    if (!found) break;
                    if (args.TryGetValue("parkreverse", out var reverse) && reverse == "on") r *= Quaternion.Euler(0, 180, 0);
                    // Slot poses name the body's centre. Account for this model's origin before teleporting.
                    p -= r * new Vector3(wrapped.BodyCentre.x, 0, wrapped.BodyCentre.z);
                    var ground = NwhPlanner.GroundBelow(GleyGraph.ToVec(p), owner.transform);
                    if (ground == null) continue;
                    p.y = ground.Value;
                    var centre = p + r * wrapped.BodyCentre;
                    if (Physics.OverlapBox(centre, wrapped.BodyHalf + Vector3.one * 0.1f, r,
                            TestCar.ObstacleMask | LayerMask.GetMask("Vehicles", "PlayerVehicles", "ParkedVehicles", "AiVehicles"), QueryTriggerInteraction.Ignore)
                        .Any(c => !c.transform.IsChildOf(owner.transform))) continue;
                    OwnCarTaxi.Teleport(owner, p, r);
                    yield return null;
                    if (!OwnCarParking.IsLegal(owner)) continue;
                    player.Character.WarpSafely(owner.GetNavMeshTargetPosition()); yield return null;
                    owner.EnterVehicle();
                    result["parkingSite"] = Key(lane); result["parkingBays"] = Bays(lane);
                    result["parkingStart"] = new JArray(p.x, p.y, p.z, r.eulerAngles.y);
                    result["parkingLegal"] = true;
                    Log.Info($"Parking test: seed {seed}, {Key(lane)}, pose {p}, yaw {r.eulerAngles.y:0.0}.");
                    yield break;
                }
            }
            result["refused"] = "no free legal test parking space for this car";
        }
    }
}
