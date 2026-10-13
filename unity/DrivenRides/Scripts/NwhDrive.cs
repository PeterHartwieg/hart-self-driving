#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DrivenRides.Core.Drive;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;
using GleyTrafficSystem;
using Helpers;
using UnityEngine;

namespace DrivenRides
{
    // Drives an NwhCar along a ride's path with the car's own physics (docs/nwh-drive-spike.md): Core's
    // PursuitDriver decides the wheel angle and the pedals each physics step; this writes them to NWH's input the way
    // a player's keys would (VehicleInputHandler with autoSetInput off). Red lights are Gley's own state on the path's
    // waypoints; cars ahead are found with a box cast along the car's heading.
    internal sealed class NwhDrive : MonoBehaviour
    {
        public NwhCar Car = null!;
        // The front track for NWH's Ackermann (CarModel.TrackWidth), set before Begin; 0 steers as for a bicycle.
        public float TrackWidth;
        private PursuitDriver driver = null!;
        private DriveLine line = null!;
        private List<int> path = null!;
        private float topMs;
        private bool started;
        private float startedAt;
        private int nextLight;
        private readonly HashSet<int> runRed = new HashSet<int>();
        private float lastProgressAlong;
        private float lastProgressTime;
        private float stillSeconds;
        private float heldSeconds;
        private bool giving;
        private Rigidbody? lastBlockerBody;

        private bool BlockerWaitsForUs(Vector3 forward)
        {
            if (lastBlockerBody == null) return false;
            if (lastBlockerBody.velocity.magnitude > 0.3f) return false;
            return Vector3.Angle(Vector3.ProjectOnPlane(lastBlockerBody.transform.forward, Vector3.up), Vector3.ProjectOnPlane(forward, Vector3.up)) > 30f;
        }
        private float backOffUntil;
        private readonly List<(float, float, float, float)> traffic = new List<(float, float, float, float)>();
        private static readonly System.Reflection.FieldInfo? TrafficVehiclesField = HarmonyLib.AccessTools.Field(typeof(TrafficManager), "trafficVehicles");

        // Gley's cars within 50 m, with their velocities, for giving way.
        private List<(float, float, float, float)> MovingTraffic(Vector3 near)
        {
            traffic.Clear();
            if (!TrafficManager.HasInstance) return traffic;
            var pool = (TrafficVehiclesField?.GetValue(TrafficManager.Instance) as TrafficVehicles)?.GetVehicleList();
            if (pool == null) return traffic;
            foreach (var v in pool)
            {
                if (v == null || !v.gameObject.activeInHierarchy || v.rb == null) continue;
                var p = v.rb.position;
                if ((p - near).sqrMagnitude > 2500f) continue;
                // The car's front, where it would meet this one.
                var front = v.frontTrigger != null ? v.frontTrigger.position : p;
                var vel = v.rb.velocity;
                traffic.Add((front.x, front.z, vel.x, vel.z));
            }
            return traffic;
        }
        private string lastBlocker = "";

        public bool Ended { get; private set; }
        public bool Arrived { get; private set; }
        public string EndReason { get; private set; } = "";
        public bool WaitingNow { get; private set; }
        public float Along => driver.Along;
        public readonly StringBuilder Trace = new StringBuilder("t,along,lateral,speed,desired,steer,input,angle,throttle,brake,handbrake,gear,stop,gap,yield\n");
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // The ride's path (waypoint indices) and its added turns' arcs (path position of the entry, the arc), the
        // stretches driven slowly (path positions), the top speed.
        public void Begin(NwhCar car, List<int> path, IReadOnlyList<(int Entry, Polyline Arc)> arcs, IEnumerable<(int Position, float SpeedKmh)> slow, float topKmh)
        {
            Car = car;
            this.path = path;
            var all = GleyGraph.Waypoints!;
            var points = new List<Vec3>(path.Count);
            foreach (var i in path) points.Add(GleyGraph.ToVec(all[i].position));
            line = DriveLine.Build(points, arcs);
            topMs = topKmh / 3.6f;
            driver = new PursuitDriver(line, car.Wheelbase, car.FrontAhead, topMs, TrackWidth);
            // A slow position covers the stretch from it to the next position.
            foreach (var (position, kmh) in slow)
            {
                if (position < 0 || position >= path.Count) continue;
                var from = line.AlongOf[position];
                var to = position + 1 < path.Count ? line.AlongOf[position + 1] : from + 5f;
                driver.Slow(from, Math.Max(to, from + 1f), kmh / 3.6f);
            }
            startedAt = Time.time;
            lastProgressTime = Time.time;
        }

        private void FixedUpdate()
        {
            if (Ended || driver == null || Car?.Vehicle == null) return;
            var vehicle = Car.Vehicle;
            if (!started)
            {
                if (!vehicle.IsInitialized) return;
                vehicle.enabled = true;
                Car.Body.isKinematic = false;
                vehicle.powertrain.engine.StartEngine();
                started = true;
            }
            var t = Car.Root.transform;
            var forward = t.forward;
            var rear = Car.RearAxle;
            var speed = Vector3.Dot(Car.Body.velocity, forward);
            // Backing off: NWH's automatic gearbox shifts into reverse on the brake at a standstill and the brake then
            // drives the car backwards (swapInputInReverse).
            if (Time.time < backOffUntil)
            {
                vehicle.input.Steering = 0f;
                vehicle.input.Throttle = 0f;
                vehicle.input.Handbrake = 0f;
                vehicle.input.Brakes = 0.35f;
                lastProgressTime = Time.time;
                return;
            }
            var stop = RedAhead(speed);
            var gap = GapAhead(speed);
            var give = driver.YieldAlong(MovingTraffic(rear), speed);
            var o = driver.Step(new DriveInput
            {
                RearX = rear.x, RearZ = rear.z, Heading = Mathf.Atan2(forward.x, forward.z), SpeedMs = speed, Dt = Time.fixedDeltaTime, WheelDegrees = vehicle.steering.angle,
                StopAlong = stop, GapAhead = gap, YieldAlong = give,
            });
            var lockNow = Mathf.Max(1f, Car.LockAt(speed));
            var steerInput = Mathf.Clamp(o.SteerDegrees / lockNow, -1f, 1f);
            var input = vehicle.input;
            input.Steering = steerInput;
            input.Throttle = o.Throttle;
            input.Brakes = o.Brake;
            input.Handbrake = o.Handbrake;
            var transmission = vehicle.powertrain.transmission;
            if (o.Throttle > 0.05f && transmission.Gear <= 0) transmission.ShiftInto(1);
            WaitingNow = o.Holding && speed < 0.5f;
            if (give != null && !giving) Log.Info($"NWH drive: giving way at {give:0} m along (front at {o.Along + Car.FrontAhead:0}).");
            giving = give != null;
            heldSeconds = WaitingNow && gap != null ? heldSeconds + Time.fixedDeltaTime : 0f;
            // Nose to nose with a traffic car: it stops for this car in its trigger and this car for it, for good
            // (Peter, NWH spike: crashed again and is now stuck). This one backs off a little so the other can go.
            // A traffic car standing across this car's way (not queued ahead in the same direction) is waiting for
            // this one too (Peter, NWH spike: it stopped for traffic but then just stood there, 2 m from it).
            if (heldSeconds > 5f && gap < 3.5f && lastBlocker.Contains("AiVehicles") && BlockerWaitsForUs(forward))
            {
                Log.Info($"NWH drive: held 5 s behind '{lastBlocker}' {gap:0.0} m ahead; backing off.");
                backOffUntil = Time.time + 2f;
                heldSeconds = 0f;
            }
            else if (heldSeconds > 5f && heldSeconds < 5.03f) Log.Info($"NWH drive: held 5 s behind '{lastBlocker}' {gap:0.0} m ahead.");

            Trace.Append((Time.time - startedAt).ToString("0.00", Inv)).Append(',').Append(o.Along.ToString("0.0", Inv)).Append(',')
                .Append(o.Lateral.ToString("0.00", Inv)).Append(',').Append(speed.ToString("0.00", Inv)).Append(',').Append(o.Desired.ToString("0.00", Inv)).Append(',')
                .Append(o.SteerDegrees.ToString("0.0", Inv)).Append(',').Append(steerInput.ToString("0.00", Inv)).Append(',').Append(vehicle.steering.angle.ToString("0.0", Inv)).Append(',')
                .Append(o.Throttle.ToString("0.00", Inv)).Append(',').Append(o.Brake.ToString("0.00", Inv)).Append(',').Append(o.Handbrake.ToString("0", Inv)).Append(',')
                .Append(transmission.Gear).Append(',').Append(stop?.ToString("0.0", Inv) ?? "").Append(',').Append(gap?.ToString("0.0", Inv) ?? "").Append(',').Append(give?.ToString("0.0", Inv) ?? "").Append('\n');

            if (o.Along + Car.FrontAhead >= driver.Length - 1.5f && Mathf.Abs(speed) < 0.3f) { End("arrived", true); return; }
            // Stuck: 30 s without 2 m of progress while nothing holds the car.
            if (o.Along > lastProgressAlong + 2f || WaitingNow) { lastProgressAlong = o.Along; lastProgressTime = Time.time; }
            else if (Time.time - lastProgressTime > 30f) { End("stuck", false); return; }
            stillSeconds = Mathf.Abs(speed) < 0.2f && !WaitingNow && o.Desired > 1f ? stillSeconds + Time.fixedDeltaTime : 0f;
            if (stillSeconds > 1f && stillSeconds < 1.03f) Log.Info($"NWH drive: standing with throttle {o.Throttle:0.00}, gear {transmission.Gear}, engine {(vehicle.powertrain.engine.IsRunning ? "running" : "off")}, rpm {vehicle.powertrain.engine.OutputRPM:0}.");
        }

        private void End(string reason, bool arrived)
        {
            Ended = true;
            Arrived = arrived;
            EndReason = reason;
            if (Car?.Vehicle != null) { Car.Vehicle.input.Throttle = 0f; Car.Vehicle.input.Handbrake = 1f; }
        }

        public void Stop(string reason) { if (!Ended) End(reason, false); }

        // The first red on the path ahead that the car can still stop for, as metres along the line where its front
        // must stop. A red that would need more than HardDecel when first seen is driven through, as on amber.
        private float? RedAhead(float speed)
        {
            if (!Options.TrafficLights) return null;
            var all = GleyGraph.Waypoints;
            if (all == null) return null;
            var front = driver.Along + Car.FrontAhead;
            while (nextLight < path.Count && line.AlongOf[nextLight] < front - 1f) nextLight++;
            for (var k = nextLight; k < path.Count; k++)
            {
                var room = line.AlongOf[k] - front;
                if (room > 70f) break;
                var w = all[path[k]];
                if (w == null || !w.stop || runRed.Contains(k)) continue;
                if (room > 0f && speed * speed / (2f * room) > PursuitDriver.HardDecel) { runRed.Add(k); continue; }
                return line.AlongOf[k];
            }
            return null;
        }

        private static int obstacleMask = -1;
        private readonly RaycastHit[] hits = new RaycastHit[16];

        // The nearest car or person in the car's way, metres from its front bumper: a box as wide as the car, cast
        // along its heading, short while it turns (a straight cast would find the parked cars round the corner).
        private float? GapAhead(float speed)
        {
            if (obstacleMask < 0)
            {
                obstacleMask = (1 << LayerHelper.VehiclesLayerIndex) | (1 << LayerHelper.PlayerVehiclesLayerIndex);
                if (NwhCar.GleyLayer >= 0) obstacleMask |= 1 << NwhCar.GleyLayer;
                foreach (var name in new[] { "Human", "ParkedVehicles" })
                {
                    var layer = LayerMask.NameToLayer(name);
                    if (layer >= 0) obstacleMask |= 1 << layer;
                }
            }
            var t = Car.Root.transform;
            var turning = Mathf.Abs(Car.Vehicle.steering.angle) > 8f;
            var reach = turning ? 6f : Mathf.Clamp(6f + speed * 2.5f, 8f, 40f);
            var half = new Vector3(Car.BodyHalf.x * 0.8f, 0.4f, 0.1f);
            var origin = Car.FrontBumper + t.forward * 0.3f;
            origin.y = t.position.y + 0.8f;
            var n = Physics.BoxCastNonAlloc(origin, half, t.forward, hits, t.rotation, reach, obstacleMask, QueryTriggerInteraction.Ignore);
            float? best = null;
            for (var i = 0; i < n; i++)
            {
                var h = hits[i];
                if (h.rigidbody == Car.Body || h.collider.transform.IsChildOf(t)) continue;
                // Only what stands on the car's way: within its half width and a little of the line.
                var point = h.distance > 0f ? h.point : h.collider.bounds.center;
                var (_, side) = line.Locate(point.x, point.z, driver.Along, reach + 10f);
                if (Mathf.Abs(side) > Car.BodyHalf.x + 0.4f) continue;
                var d = h.distance + 0.3f;
                if (best == null || d < best) { best = d; lastBlocker = $"{h.collider.name} ({LayerMask.LayerToName(h.collider.gameObject.layer)})"; lastBlockerBody = h.rigidbody; }
            }
            return best;
        }
    }
}
