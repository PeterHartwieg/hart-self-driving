#nullable enable
using System;
using System.Collections;
using DrivenRides.Core.Drive;
using UnityEngine;

namespace DrivenRides
{
    internal static class NwhRides
    {
        public static string? DrivableType(string? vehicleTypeName, GameObject trafficPrefab)
        {
            if (vehicleTypeName != null && NwhCar.HasPrefab(vehicleTypeName)) return vehicleTypeName;
            var twin = DrivableSteering.TypeOf(trafficPrefab.name);
            return twin != null && NwhCar.HasPrefab(twin) ? twin : null;
        }

        public static NwhPlanner? Planner(string? vehicleTypeName, GameObject trafficPrefab, float topKmh)
        {
            var type = DrivableType(vehicleTypeName, trafficPrefab);
            if (type != null) return NwhPlanner.For(type, topKmh);
            var donor = NwhDress.DonorFor(trafficPrefab, out _);
            return donor != null ? NwhPlanner.For(donor.TypeName, topKmh) : null;
        }

        public static NwhCar? Spawn(string? vehicleTypeName, GameObject trafficPrefab, Vector3 at, Quaternion rotation, out string why)
        {
            var type = DrivableType(vehicleTypeName, trafficPrefab);
            return type != null ? NwhCar.Spawn(type, at, rotation, out why) : NwhCar.SpawnDressed(trafficPrefab, at, rotation, out why);
        }

        public static void PlaceRearAxle(NwhCar car, Vector3 rearAxle, Quaternion rotation)
        {
            var at = rearAxle + Vector3.up * 0.4f - rotation * new Vector3(0f, 0f, car.RearZ);
            Place(car, at, rotation);
        }

        public static void PlaceBody(NwhCar car, Vector3 middle, float groundY, Quaternion rotation)
        {
            var at = new Vector3(middle.x, groundY + 0.4f, middle.z) - rotation * new Vector3(car.BodyCentre.x, 0f, car.BodyCentre.z);
            Place(car, at, rotation);
        }

        private static void Place(NwhCar car, Vector3 at, Quaternion rotation)
        {
            if (!car.Body.isKinematic)
            {
                car.Body.velocity = Vector3.zero;
                car.Body.angularVelocity = Vector3.zero;
            }
            car.Root.transform.SetPositionAndRotation(at, rotation);
            car.Body.position = at;
            car.Body.rotation = rotation;
            Physics.SyncTransforms();
        }

        public static Vector3 BodyMiddle(NwhCar car) => car.Root.transform.TransformPoint(car.BodyCentre);
    }

    internal sealed class NwhRideCar
    {
        public NwhCar Car = null!;
        public CarSpec Spec = null!;
        public bool PlayerInside { get; set; }
        public bool Owned { get; set; } = true;
        public Action<string>? End { get; set; }
        public Func<IEnumerator>? Arrive { get; set; }
    }
}
