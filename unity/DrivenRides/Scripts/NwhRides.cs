#nullable enable
using System;
using System.Collections;
using DrivenRides.Core.Drive;
using UnityEngine;

namespace DrivenRides
{
    // Which drivable car a ride car is driven as, and putting it on the road: one place for real rides (Ride) and the
    // rig's driver=nwh cases (TurnTests), so both drive the same car the same way (docs/implementation-plan.md,
    // "NWH driving", phase 5).
    internal static class NwhRides
    {
        // The vehicle type whose drivable prefab drives a ride car: the type's own if it has one (the private driver's
        // vehicle), else the traffic prefab's drivable twin. Null: none, so the car is the closest drivable car
        // dressed as the traffic model (NwhDress; the taxi).
        public static string? DrivableType(string? vehicleTypeName, GameObject trafficPrefab)
        {
            if (vehicleTypeName != null && NwhCar.HasPrefab(vehicleTypeName)) return vehicleTypeName;
            var twin = DrivableSteering.TypeOf(trafficPrefab.name);
            return twin != null && NwhCar.HasPrefab(twin) ? twin : null;
        }

        // The planner for that car (its drivable prefab's shape), or for a dressed car the donor's; null if neither.
        public static NwhPlanner? Planner(string? vehicleTypeName, GameObject trafficPrefab, float topKmh)
        {
            var type = DrivableType(vehicleTypeName, trafficPrefab);
            if (type != null) return NwhPlanner.For(type, topKmh);
            var donor = NwhDress.DonorFor(trafficPrefab, out _);
            return donor != null ? NwhPlanner.For(donor.TypeName, topKmh) : null;
        }

        // The car itself, awake at `at` (the prefab's origin) facing `rotation`.
        public static NwhCar? Spawn(string? vehicleTypeName, GameObject trafficPrefab, Vector3 at, Quaternion rotation, out string why)
        {
            var type = DrivableType(vehicleTypeName, trafficPrefab);
            return type != null ? NwhCar.Spawn(type, at, rotation, out why) : NwhCar.SpawnDressed(trafficPrefab, at, rotation, out why);
        }

        // Puts a car with its rear axle over `rearAxle` (a lane waypoint), 0.4 m up so it settles onto its wheels,
        // at rest. The prefab's origin is not its rear axle.
        public static void PlaceRearAxle(NwhCar car, Vector3 rearAxle, Quaternion rotation)
        {
            var at = rearAxle + Vector3.up * 0.4f - rotation * new Vector3(0f, 0f, car.RearZ);
            Place(car, at, rotation);
        }

        // Puts a car with its body's middle over `middle` (where a traffic car's body stood), on the ground at
        // `groundY`, at rest.
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

        // The body's middle in the world.
        public static Vector3 BodyMiddle(NwhCar car) => car.Root.transform.TransformPoint(car.BodyCentre);
    }

    // A ride's car driven by its own physics, from wherever it came (the seam for every kind of ride):
    // - a taxi or a stand: the dressed taxi (NwhRides.Spawn), the mod's own car;
    // - the private driver: the drivable car of the driver's vehicle type, swapped in for the summoned traffic car;
    // - the player's own car (filled in from OwnCar.cs): the player's real car, not the mod's.
    internal sealed class NwhRideCar
    {
        public NwhCar Car = null!;
        // The shape the planner planned with (NwhPlanner.Car); the driver and the traffic rules drive with it.
        public CarSpec Spec = null!;
        // The player sits inside, in the driver's seat with the car's own camera: no attach, anchor, hiding, exit or
        // entry, and no placement at the end (Arrive instead). A cancel leaves the player and the car where they are.
        public bool PlayerInside { get; set; }
        // The mod's own car: at the ride's end a traffic car of the ride's model takes its place and drives on, and it
        // is destroyed. Not owned (the player's car): End hands it back and it stays.
        public bool Owned { get; set; } = true;
        // Called once at every end of the ride, a cancel included, with the reason (the own car: OwnCarDrive.End,
        // which hands control back and is safe to call twice).
        public Action<string>? End { get; set; }
        // With PlayerInside, the arrival the ride steps instead of placing the player (the own car's autopark, which
        // wants the car below 10 km/h: the drive has stopped it by then). Null: nothing.
        public Func<IEnumerator>? Arrive { get; set; }
    }
}
