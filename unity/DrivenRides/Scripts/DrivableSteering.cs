#nullable enable
using System;
using System.Collections.Generic;
using GleyTrafficSystem;
using HarmonyLib;
using Helpers;
using UnityEngine;
using Vehicles.VehicleTypes;

namespace DrivenRides
{
    // The steering the game gives a car when the player drives it (its drivable prefab, NWH Vehicle Physics): the
    // wheelbase from its wheels and its lock at a speed. Gley's numbers for the same car's traffic twin turn far
    // tighter (the limo on a 2 m circle, the taxi on 3.2 m), and the ride car pivoted round corners on its back wheels
    // (Peter, turn test 0.1.48: "when I drive the cars manually they do not have such a tight turning radius").
    internal static class DrivableSteering
    {
        private static readonly System.Reflection.FieldInfo? TypesField = AccessTools.Field(typeof(VehicleTypeHelper), "VehicleTypes");
        // Traffic prefab name to vehicle type name; built once.
        private static Dictionary<string, string>? typeOfTraffic;
        private static readonly Dictionary<string, (float Wheelbase, float MaxSteer, AnimationCurve? Curve)?> known =
            new Dictionary<string, (float, float, AnimationCurve?)?>();

        // The wheelbase and the lock (degrees) at speedMs of the drivable version of the car whose traffic prefab
        // this is; null for a car the player can't drive (the taxi) or one whose prefab doesn't load.
        public static (float Wheelbase, float Steer)? For(GameObject? trafficPrefab, float speedMs)
        {
            if (trafficPrefab == null) return null;
            var type = TypeOf(trafficPrefab.name);
            if (type == null) return null;
            if (!known.TryGetValue(type, out var found))
            {
                found = Load(type);
                known[type] = found;
            }
            if (found == null) return null;
            var (wheelbase, maxSteer, curve) = found.Value;
            // NWH: the lock is maximumSteerAngle times the curve at speed / 50 m/s.
            var factor = curve != null ? Mathf.Clamp01(curve.Evaluate(speedMs / 50f)) : 1f;
            return (wheelbase, maxSteer * factor);
        }

        private static string? TypeOf(string trafficPrefabName)
        {
            if (typeOfTraffic == null)
            {
                typeOfTraffic = new Dictionary<string, string>();
                if (TypesField?.GetValue(null) is Dictionary<string, VehicleType> types)
                    foreach (var t in types.Values)
                    {
                        if (t == null || string.IsNullOrEmpty(t.vehicleTypeName)) continue;
                        var prefab = PrefabHelper.LoadPrefabAssetByName("Vehicles/" + Id(t.vehicleTypeName));
                        if (prefab != null && prefab.GetComponent<VehicleComponent>() != null) typeOfTraffic[prefab.name] = t.vehicleTypeName;
                    }
            }
            return typeOfTraffic.TryGetValue(trafficPrefabName, out var name) ? name : null;
        }

        private static (float, float, AnimationCurve?)? Load(string type)
        {
            try
            {
                var asset = PrefabHelper.LoadPrefabAssetByName("Vehicles/PlayerVehicles/" + Id(type));
                var vehicle = asset != null ? asset.GetComponentInChildren<NWH.VehiclePhysics2.VehicleController>(true) : null;
                if (asset == null || vehicle == null) { Log.Info($"Drivable steering for {type}: no drivable prefab."); return null; }
                float min = float.MaxValue, max = float.MinValue;
                foreach (var wheel in asset.GetComponentsInChildren<NWH.Common.Vehicles.WheelUAPI>(true))
                {
                    var z = asset.transform.InverseTransformPoint(wheel.transform.position).z;
                    min = Mathf.Min(min, z);
                    max = Mathf.Max(max, z);
                }
                var wheelbase = max - min;
                var steer = vehicle.steering.maximumSteerAngle;
                if (wheelbase < 1f || steer < 5f) { Log.Info($"Drivable steering for {type}: wheelbase {wheelbase:0.00} m, lock {steer:0.0}, not usable."); return null; }
                var curve = vehicle.steering.speedSensitiveSteeringCurve;
                Log.Info($"Drivable steering for {type}: wheelbase {wheelbase:0.00} m, lock {steer:0.0} degrees ({steer * Mathf.Clamp01(curve?.Evaluate(25f / 3.6f / 50f) ?? 1f):0.0} at 25 km/h).");
                return (wheelbase, steer, curve);
            }
            catch (Exception ex) { Log.Error($"Drivable steering for {type}: {ex.Message}"); return null; }
        }

        private static string Id(string vehicleTypeName)
        {
            var at = vehicleTypeName.IndexOf("vehicletype_", StringComparison.Ordinal);
            return at >= 0 ? vehicleTypeName.Substring(at + "vehicletype_".Length) : vehicleTypeName;
        }
    }
}
