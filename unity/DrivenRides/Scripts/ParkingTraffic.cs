#nullable enable
using System;
using System.Collections.Generic;
using DrivenRides.Core.Drive;
using DrivenRides.Core.Parking;
using GleyTrafficSystem;
using UnityEngine;

namespace DrivenRides
{
    // Only the local manoeuvre owns this lease. Gley's actions, waypoints and saved state are never replaced.
    internal sealed class ParkingTraffic : IDisposable
    {
        internal static bool HooksReady;
        private static ParkingTraffic? active;
        private readonly NwhManoeuvre owner;
        private readonly GleyTraffic traffic;
        private readonly List<OtherCar> cars = new List<OtherCar>();
        private TrafficReservation region;
        public int HeldCount { get; private set; }
        private int maxHeld;
        public static int ActiveCount => Current == null ? 0 : 1;
        private static ParkingTraffic? Current => active != null && active.owner != null &&
            active.owner.isActiveAndEnabled && !active.owner.Ended && OwnCarDrive.IsActive ? active : null;

        public ParkingTraffic(NwhManoeuvre owner, NwhCar car, TrafficReservation region)
        {
            if (!HooksReady || Current != null) throw new InvalidOperationException("traffic reservation is unavailable");
            this.owner = owner; this.region = region; traffic = new GleyTraffic(car.Root);
            active = this;
            Log.Info("Parking traffic: reserved the local manoeuvre area.");
        }

        public void SetPlan(CarSpec car, ManoeuvrePlan plan) => region = TrafficReservation.ForPlan(car, plan, region.Height);

        public bool Occupied(IReadOnlyList<OtherCar> nearby, IReadOnlyList<VehicleComponent> components)
        {
            for (var i = 0; i < nearby.Count; i++)
                if (components[i] != null && region.Touches(TrafficReservation.Body(nearby[i]), components[i].transform.position.y)) return true;
            return false;
        }

        // Ignore Gley's propulsion/rotation for one FixedUpdate only. A Harmony finalizer releases every flag
        // even when another patch or Gley throws. Its own suspension, lights and collision sensing continue.
        // Previously ignored cars belong to someone else; never acquire or release those.
        internal sealed class Frame : IDisposable
        {
            internal readonly TrafficManager Manager;
            internal readonly List<VehicleComponent> Held = new List<VehicleComponent>();
            internal Frame(TrafficManager manager) { Manager = manager; }
            public void Dispose()
            {
                if (Manager == null) return;
                foreach (var v in Held)
                    if (v != null) Manager.SetVehicleIgnored(v.GetIndex(), false);
                Held.Clear();
            }
        }

        internal static void BeforeTraffic(Frame frame)
        {
            var lease = Current;
            if (lease == null) return;
            try { lease.BrakeEntrants(frame); }
            catch (Exception ex) { lease.owner.Stop("traffic reservation failed: " + ex.Message); }
        }

        private void BrakeEntrants(Frame frame)
        {
            var a = region.Area;
            traffic.ReadCars(new Vector3(a.X, region.Height, a.Z), a.HalfLength + a.HalfWidth + 120, cars);
            HeldCount = 0;
            for (var i = 0; i < cars.Count; i++)
            {
                var v = traffic.Components[i];
                if (v.rb.isKinematic || frame.Manager.IsVehicleIgnored(v.GetIndex())) continue;
                var target = GleyTraffic.At(cars[i].Target);
                var distance = region.StopDistance(cars[i], v.transform.position.y,
                    target == null ? (DrivenRides.Core.Routing.Vec3?)null : GleyGraph.ToVec(target.position));
                if (float.IsPositiveInfinity(distance)) continue;
                frame.Held.Add(v);
                frame.Manager.SetVehicleIgnored(v.GetIndex(), true);
                var velocity = v.rb.velocity;
                var speed = cars[i].Speed;
                var factor = speed > 0.001f ? TrafficReservation.BrakedSpeed(speed, distance, Time.fixedDeltaTime) / speed : 0;
                // No teleport, kinematic toggle or restored velocity: brake horizontally and keep gravity/suspension.
                v.rb.velocity = new Vector3(velocity.x * factor, velocity.y, velocity.z * factor);
                HeldCount++;
            }
            maxHeld = Math.Max(maxHeld, HeldCount);
        }

        internal static bool AllowsSpawn(Vector3 position, float halfLength, float halfWidth, float frontOffset, Quaternion rotation)
        {
            var lease = Current;
            if (lease == null) return true;
            // PositionValidator receives the front-wheel waypoint, not the body centre.
            var middle = position - rotation * new Vector3(0, 0, frontOffset);
            return !lease.region.Touches(new Box(middle.x, middle.z, rotation.eulerAngles.y * Mathf.Deg2Rad, halfWidth, halfLength), middle.y);
        }

        public void Dispose()
        {
            if (active != this) return;
            active = null;
            Log.Info($"Parking traffic: released (held up to {maxHeld} cars).");
        }

        internal static void ReleaseActive() { active?.owner.Stop("traffic reservation hooks removed"); active?.Dispose(); }
    }
}
