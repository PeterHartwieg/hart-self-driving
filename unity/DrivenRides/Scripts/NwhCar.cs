#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using GleyTrafficSystem;
using HarmonyLib;
using Helpers;
using NWH.VehiclePhysics2.Modules.Fuel;
using UnityEngine;
using NwhController = NWH.VehiclePhysics2.VehicleController;
using Object = UnityEngine.Object;

namespace DrivenRides
{
    // A drivable car (the prefab the player drives, Vehicles/PlayerVehicles/<id>) with only its physics: the game's
    // own scripts (CarController and the rest, which expect a saved VehicleInstance and a player) are taken off before
    // it wakes, so what is left is NWH Vehicle Physics 2 and the car's colliders. The NWH drive spike
    // (docs/nwh-drive-spike.md) drives it with NwhDrive.
    internal sealed class NwhCar
    {
        public GameObject Root = null!;
        public NwhController Vehicle = null!;
        public Rigidbody Body = null!;
        public string TypeName = "";
        // A car dressed as a traffic car that has no drivable twin (NwhDress): that traffic prefab's name; null for a
        // drivable car in its own looks. The physics, the steering and the body box are always TypeName's.
        public string? Look;
        // In the car's own space: the axles' z, the body box.
        public float RearZ, FrontZ, MaxSteer;
        public Vector3 BodyCentre, BodyHalf;
        public AnimationCurve? SpeedCurve;

        public float Wheelbase => FrontZ - RearZ;
        public float Length => BodyHalf.z * 2f;
        // The front bumper, metres ahead of the rear axle.
        public float FrontAhead => BodyCentre.z + BodyHalf.z - RearZ;
        public Vector3 RearAxle => Root.transform.TransformPoint(new Vector3(0f, 0f, RearZ));
        public Vector3 FrontAxle => Root.transform.TransformPoint(new Vector3(0f, 0f, FrontZ));
        public Vector3 FrontBumper => Root.transform.TransformPoint(new Vector3(BodyCentre.x, BodyCentre.y, BodyCentre.z + BodyHalf.z));

        // The lock at a speed: NWH narrows maximumSteerAngle by the curve at speed / 50 m/s.
        public float LockAt(float speedMs) => MaxSteer * (SpeedCurve != null ? Mathf.Clamp01(SpeedCurve.Evaluate(Mathf.Abs(speedMs) / 50f)) : 1f);

        private static readonly HashSet<string> Logged = new HashSet<string>();

        public static string Id(string vehicleTypeName)
        {
            var at = vehicleTypeName.IndexOf("vehicletype_", StringComparison.Ordinal);
            return at >= 0 ? vehicleTypeName.Substring(at + "vehicletype_".Length) : vehicleTypeName;
        }

        public static bool HasPrefab(string vehicleTypeName)
        {
            var asset = PrefabHelper.LoadPrefabAssetByName("Vehicles/PlayerVehicles/" + Id(vehicleTypeName));
            return asset != null && asset.GetComponentInChildren<NwhController>(true) != null;
        }

        public static NwhCar? Spawn(string vehicleTypeName, Vector3 at, Quaternion rotation, out string why) =>
            Spawn(vehicleTypeName, at, rotation, null, out why);

        // A drivable car that looks like a traffic car without a drivable twin (the taxi): the drivable car closest in
        // size, with the traffic model's meshes on it (NwhDress). The donor is picked once per traffic prefab.
        public static NwhCar? SpawnDressed(GameObject trafficPrefab, Vector3 at, Quaternion rotation, out string why)
        {
            var donor = NwhDress.DonorFor(trafficPrefab, out why);
            if (donor == null) return null;
            return Spawn(donor.TypeName, at, rotation, car => NwhDress.Dress(car, trafficPrefab, donor), out why);
        }

        // dress runs on the stripped car before it wakes, so whatever it adds wakes with the car; it returns why it
        // failed, or null.
        private static NwhCar? Spawn(string vehicleTypeName, Vector3 at, Quaternion rotation, Func<NwhCar, string?>? dress, out string why)
        {
            why = "";
            var holder = new GameObject("DrivenRides.NwhHolder");
            holder.SetActive(false);
            GameObject? root = null;
            try
            {
                root = PrefabHelper.CreatePrefab("Vehicles/PlayerVehicles/" + Id(vehicleTypeName), holder.transform);
                root.name = "DrivenRides.NwhCar." + Id(vehicleTypeName);
                var vehicle = root.GetComponentInChildren<NwhController>(true);
                if (vehicle == null) { why = "the drivable prefab has no NWH VehicleController"; Object.Destroy(root); return null; }
                var stripped = Strip(root, out var kept);
                vehicle.input.autoSetInput = false;
                try { vehicle.soundManager.mixer = InstanceBehavior<GlobalReferences>.Instance.vehicleAudioMixer; }
                catch (Exception ex) { Log.Error("NWH car, sound mixer: " + ex.Message); }
                foreach (var fuel in root.GetComponentsInChildren<FuelModuleWrapper>(true)) fuel.module.useFuel = false;
                var body = vehicle.vehicleRigidbody != null ? vehicle.vehicleRigidbody : root.GetComponent<Rigidbody>();
                if (body == null) { why = "no rigidbody"; Object.Destroy(root); return null; }

                var car = new NwhCar { Root = root, Vehicle = vehicle, Body = body, TypeName = vehicleTypeName };
                var zs = vehicle.powertrain.wheels.Where(w => w?.wheelUAPI != null).Select(w => root.transform.InverseTransformPoint(w.wheelUAPI.transform.position).z).ToList();
                if (zs.Count < 2) { why = "no wheels"; Object.Destroy(root); return null; }
                car.FrontZ = zs.Max();
                car.RearZ = zs.Min();
                car.MaxSteer = vehicle.steering.maximumSteerAngle;
                car.SpeedCurve = vehicle.steering.speedSensitiveSteeringCurve;
                Measure(car);
                if (dress != null)
                {
                    var failed = dress(car);
                    if (failed != null) { why = failed; Object.Destroy(root); return null; }
                }

                // The layer the game gives a car the player drives (VehicleController.EnterVehicle), so traffic treats it
                // as it treats the player's car.
                var layerWas = LayerMask.LayerToName(root.layer);
                root.layer = LayerHelper.VehiclesLayerIndex;
                root.transform.SetPositionAndRotation(at, rotation);
                // Out of the inactive holder: the car wakes (Awake now, Start next frame), with only its physics.
                root.transform.SetParent(null, true);
                body.isKinematic = false;
                foreach (var w in vehicle.powertrain.wheels) if (w?.wheelUAPI != null) w.wheelUAPI.enabled = true;
                if (Logged.Add(vehicleTypeName))
                    Log.Trace($"NWH car {vehicleTypeName}: wheelbase {car.Wheelbase:0.00} m, lock {car.MaxSteer:0.0} ({car.LockAt(25f / 3.6f):0.0} at 25 km/h, {car.LockAt(50f / 3.6f):0.0} at 50), " +
                             $"front {car.FrontAhead:0.00} m ahead of the rear axle, body {car.BodyHalf * 2f} at {car.BodyCentre}, mass {body.mass:0} kg, " +
                             $"gearbox {vehicle.powertrain.transmission.transmissionType} ({vehicle.powertrain.transmission.automaticTransmissionDNRShiftType}), layer {layerWas}, now {LayerMask.LayerToName(root.layer)}; " +
                             $"took off {stripped}; kept {kept}. {GleySees(root)}");
                return car;
            }
            catch (Exception ex)
            {
                why = "spawn: " + ex.Message;
                Log.Error("NWH car spawn: " + ex);
                if (root != null) Object.Destroy(root);
                return null;
            }
            finally { Object.Destroy(holder); }
        }

        // Takes off every script from the game's own assemblies, children included. Some require others, so a few
        // passes; whatever can't go is reported.
        private static string Strip(GameObject root, out string kept)
        {
            var gone = new SortedDictionary<string, int>();
            for (var pass = 0; pass < 4; pass++)
            {
                var any = false;
                foreach (var b in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (b == null || !IsGameScript(b)) continue;
                    var name = b.GetType().Name;
                    Object.DestroyImmediate(b);
                    if (b != null) continue;
                    gone[name] = gone.TryGetValue(name, out var n) ? n + 1 : 1;
                    any = true;
                }
                if (!any) break;
            }
            var left = root.GetComponentsInChildren<MonoBehaviour>(true).Where(b => b != null).GroupBy(b => (IsGameScript(b) ? "LEFT " : "") + b.GetType().Name)
                .Select(g => g.Count() > 1 ? $"{g.Key} x{g.Count()}" : g.Key);
            kept = string.Join(", ", left);
            return string.Join(", ", gone.Select(p => p.Value > 1 ? $"{p.Key} x{p.Value}" : p.Key));
        }

        private static bool IsGameScript(MonoBehaviour b)
        {
            var assembly = b.GetType().Assembly.GetName().Name;
            return assembly.StartsWith("BigAmbitions", StringComparison.Ordinal) || assembly == "Assembly-CSharp";
        }

        internal static void Measure(NwhCar car)
        {
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = -min;
            var found = false;
            var t = car.Root.transform;
            foreach (var c in car.Root.GetComponentsInChildren<Collider>(true))
            {
                if (c.isTrigger) continue;
                Bounds b;
                if (c is BoxCollider box) b = new Bounds(box.center, box.size);
                else if (c is MeshCollider mesh && mesh.sharedMesh != null) b = mesh.sharedMesh.bounds;
                else if (c is CapsuleCollider cap) b = new Bounds(cap.center, Vector3.one * cap.radius * 2f + (cap.direction == 2 ? Vector3.forward : cap.direction == 1 ? Vector3.up : Vector3.right) * (cap.height - cap.radius * 2f));
                else if (c is SphereCollider sphere) b = new Bounds(sphere.center, Vector3.one * sphere.radius * 2f);
                else continue;
                for (var i = 0; i < 8; i++)
                {
                    var corner = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var local = t.InverseTransformPoint(c.transform.TransformPoint(corner));
                    min = Vector3.Min(min, local);
                    max = Vector3.Max(max, local);
                    found = true;
                }
            }
            if (!found) { min = new Vector3(-0.9f, 0f, car.RearZ - 1f); max = new Vector3(0.9f, 1.5f, car.FrontZ + 1f); }
            car.BodyCentre = (min + max) / 2f;
            car.BodyHalf = (max - min) / 2f;
        }

        // Whether Gley's traffic sees this car: a Gley car's front trigger reacts to its own layer and to its
        // building, obstacle and player layers (VehicleComponent.OnTriggerEnter).
        private static readonly System.Reflection.FieldInfo? ObstacleField = AccessTools.Field(typeof(VehicleComponent), "obstacleLayers");
        private static readonly System.Reflection.FieldInfo? PlayerField = AccessTools.Field(typeof(VehicleComponent), "playerLayers");
        private static readonly System.Reflection.FieldInfo? BuildingField = AccessTools.Field(typeof(VehicleComponent), "buildingLayers");
        private static readonly System.Reflection.FieldInfo? TrafficVehiclesField = AccessTools.Field(typeof(TrafficManager), "trafficVehicles");

        public static int GleyLayer = -1;

        public static string GleySees(GameObject root)
        {
            try
            {
                var pool = (TrafficVehiclesField?.GetValue(TrafficManager.Instance) as TrafficVehicles)?.GetVehicleList();
                var gley = pool?.FirstOrDefault(v => v != null);
                if (gley == null) return "No Gley car to compare with.";
                GleyLayer = gley.gameObject.layer;
                int Mask(System.Reflection.FieldInfo? f) => f?.GetValue(gley) is LayerMask m ? m.value : 0;
                var layers = root.GetComponentsInChildren<Collider>(true).Where(c => !c.isTrigger).Select(c => c.gameObject.layer).Distinct().ToList();
                var what = layers.Select(l =>
                {
                    var bit = 1 << l;
                    var seenAs = l == GleyLayer ? "a Gley car's own layer" : (Mask(PlayerField) & bit) != 0 ? "the player" : (Mask(ObstacleField) & bit) != 0 ? "an obstacle" : (Mask(BuildingField) & bit) != 0 ? "a building" : "nothing";
                    return $"{LayerMask.LayerToName(l)} ({seenAs})";
                });
                return $"Gley cars are on {LayerMask.LayerToName(GleyLayer)}; this car's colliders: {string.Join(", ", what)}.";
            }
            catch (Exception ex) { return "Gley layers: " + ex.Message; }
        }
        // The player's own car as it stands, for the own car driving itself (docs/own-car-self-drive.md): nothing is
        // spawned or taken off; its CarController, fuel and parking stay the game's. Input is handed over by OwnCarDrive.
        internal static NwhCar? Wrap(NwhController vehicle, GameObject root, string vehicleTypeName, out string why)
        {
            why = "";
            var body = vehicle.vehicleRigidbody != null ? vehicle.vehicleRigidbody : root.GetComponent<Rigidbody>();
            if (body == null) { why = "no rigidbody"; return null; }
            var zs = vehicle.powertrain.wheels.Where(w => w?.wheelUAPI != null).Select(w => root.transform.InverseTransformPoint(w.wheelUAPI.transform.position).z).ToList();
            if (zs.Count < 2) { why = "no wheels"; return null; }
            var car = new NwhCar
            {
                Root = root, Vehicle = vehicle, Body = body, TypeName = vehicleTypeName,
                FrontZ = zs.Max(), RearZ = zs.Min(),
                MaxSteer = vehicle.steering.maximumSteerAngle, SpeedCurve = vehicle.steering.speedSensitiveSteeringCurve,
            };
            // The live car's own colliders only: enabled ones, and not the player's figure, which the game parents into
            // the car while the player drives (Measure is for a car spawned from its prefab).
            MeasureLive(car);
            return car;
        }

        private static void MeasureLive(NwhCar car)
        {
            var t = car.Root.transform;
            var figure = InstanceBehavior<GameManager>.Instance?.playerController?.Character?.transform;
            // Nor the autopark gap collider, which the game turns on only while a spot is offered.
            var gap = car.Root.GetComponentInChildren<VehicleParkingHelper>(true)?.parallelParkingCollider;
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = -min;
            var found = false;
            foreach (var c in car.Root.GetComponentsInChildren<Collider>(false))
            {
                if (c.isTrigger || !c.enabled || c == gap || (figure != null && c.transform.IsChildOf(figure))) continue;
                Bounds b;
                if (c is BoxCollider box) b = new Bounds(box.center, box.size);
                else if (c is MeshCollider mesh && mesh.sharedMesh != null) b = mesh.sharedMesh.bounds;
                else continue;
                for (var i = 0; i < 8; i++)
                {
                    var corner = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var local = t.InverseTransformPoint(c.transform.TransformPoint(corner));
                    min = Vector3.Min(min, local);
                    max = Vector3.Max(max, local);
                    found = true;
                }
            }
            if (!found) { Measure(car); return; }
            car.BodyCentre = (min + max) / 2f;
            car.BodyHalf = (max - min) / 2f;
        }
    }
}
