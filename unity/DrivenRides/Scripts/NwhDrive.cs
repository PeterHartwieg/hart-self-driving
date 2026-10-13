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
    // a player's keys would (VehicleInputHandler with autoSetInput off). Traffic is Core's TrafficDriver
    // (docs/implementation-plan.md, "The four layers"), fed each physics step from Gley (GleyTraffic): the path's
    // waypoints with their live flags, every active traffic car within TrafficDriver.SeeMetres. A box cast ahead finds
    // what Gley doesn't know about: parked cars, the player's cars and people (the NWH car's layer doesn't collide
    // with people, phase 1). A failed traffic run (contact with a Gley car, a long standstill, a red run) leaves a T0
    // scenario in Dumps.
    internal sealed class NwhDrive : MonoBehaviour
    {
        public NwhCar Car = null!;
        // The front track for NWH's Ackermann (CarModel.TrackWidth), set before Begin; 0 steers as for a bicycle.
        public float TrackWidth;
        private PursuitDriver driver = null!;
        private TrafficDriver traffic = null!;
        private GleyTraffic gley = null!;
        private readonly TrafficRecorder recorder = new TrafficRecorder();
        private DriveLine line = null!;
        private List<int> path = null!;
        private readonly List<(int Entry, Polyline Arc)> arcs = new List<(int, Polyline)>();
        private readonly List<(float From, float To, float Ms)> slowStretches = new List<(float, float, float)>();
        private float topMs;
        private float rearBehind;
        private bool started;
        private float startedAt;
        private float lastProgressAlong;
        private float lastProgressTime;
        private float stillSeconds;
        private readonly List<PathMark> marks = new List<PathMark>();
        private readonly List<OtherCar> cars = new List<OtherCar>();
        private readonly TrafficDecision decision = new TrafficDecision();
        private string lastWhy = "";
        private bool backing;

        // T3's tally: contacts with traffic cars (and who was moving), standstills over 30 s away from a red, red runs.
        public int Contacts, ContactsMoving, Stalls, RedRuns;
        private float standing;
        private float prevFront;
        private readonly Dictionary<int, bool> stoppable = new Dictionary<int, bool>();
        // Scenario dumps of this run's failures (kind, JSON), at most MaxDumps.
        public readonly List<(string Kind, string Json)> Dumps = new List<(string, string)>();
        private const int MaxDumps = 3;

        public bool Ended { get; private set; }
        public bool Arrived { get; private set; }
        public string EndReason { get; private set; } = "";
        public bool WaitingNow { get; private set; }
        public float Along => driver.Along;
        public readonly StringBuilder Trace = new StringBuilder("t,along,lateral,speed,desired,steer,input,angle,throttle,brake,handbrake,gear,stop,gap,yield,cap,traffic\n");
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // The ride's path (waypoint indices) and its added turns' arcs (path position of the entry, the arc), the
        // stretches driven slowly (path positions), the top speed.
        public void Begin(NwhCar car, List<int> path, IReadOnlyList<(int Entry, Polyline Arc)> arcs, IEnumerable<(int Position, float SpeedKmh)> slow, float topKmh)
        {
            Car = car;
            this.path = path;
            this.arcs.AddRange(arcs);
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
                slowStretches.Add((from, Math.Max(to, from + 1f), kmh / 3.6f));
            }
            rearBehind = car.RearZ - (car.BodyCentre.z - car.BodyHalf.z);
            var body = new RideBody { Wheelbase = car.Wheelbase, FrontAhead = car.FrontAhead, RearBehind = rearBehind, HalfWidth = car.BodyHalf.x };
            traffic = new TrafficDriver(GleyGraph.Graph!, line, driver, body);
            gley = new GleyTraffic(car.Root);
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
            var heading = Mathf.Atan2(forward.x, forward.z);
            var front = driver.Along + Car.FrontAhead;

            // The traffic snapshot: the path's waypoints from a little behind to 80 m ahead, the Gley cars round it.
            Marks(front);
            gley.ReadCars(rear, TrafficDriver.SeeMetres, cars);
            recorder.Record(Time.time, rear, heading, speed, driver.Along, cars, gley.Components);
            traffic.Decide(new TrafficSnapshot
            {
                Time = Time.time, RearX = rear.x, RearZ = rear.z, Heading = heading, Speed = speed, Marks = marks, Cars = cars, Flags = gley,
                GapBehind = traffic.BackingOff || decision.GapAhead < 4f ? GapBehind() : null,
            }, decision);
            foreach (var e in decision.Events)
            {
                gley.Apply(e);
                Log.Info($"NWH drive: {(e.Kind == JunctionEventKind.Enter ? "in" : "out of")} intersection {e.Junction} at {driver.Along:0} m.");
            }
            if (decision.Why != lastWhy)
            {
                if (decision.Why.Length > 0) Log.Info($"NWH drive: {decision.Why} at {driver.Along:0} m, {speed:0.0} m/s{(decision.YieldNote.Length > 0 ? " (" + decision.YieldNote + ")" : "")}.");
                lastWhy = decision.Why;
            }

            var input = vehicle.input;
            var transmission = vehicle.powertrain.transmission;
            if (decision.BackOff)
            {
                // Backing off along the line: NWH's automatic gearbox shifts into reverse on the brake at a standstill
                // and the brake then drives the car backwards (swapInputInReverse).
                input.Steering = 0f;
                input.Throttle = 0f;
                input.Handbrake = 0f;
                input.Brakes = 0.35f;
                driver.BackTo(rear.x, rear.z);
                if (!backing) Log.Info($"NWH drive: backing off from traffic car {decision.BackOffFrom}.");
                backing = true;
                lastProgressTime = Time.time;
                WaitingNow = true;
                Tally(speed, front);
                return;
            }
            backing = false;
            var gap = GapAhead(speed);
            if (decision.GapAhead is float carGap && (gap == null || carGap < gap)) gap = carGap;
            var o = driver.Step(new DriveInput
            {
                RearX = rear.x, RearZ = rear.z, Heading = heading, SpeedMs = speed, Dt = Time.fixedDeltaTime, WheelDegrees = vehicle.steering.angle,
                StopAlong = decision.StopAlong, GapAhead = gap, YieldAlong = decision.YieldAlong, MaxSpeed = decision.MaxSpeed,
            });
            var lockNow = Mathf.Max(1f, Car.LockAt(speed));
            var steerInput = Mathf.Clamp(o.SteerDegrees / lockNow, -1f, 1f);
            input.Steering = steerInput;
            input.Throttle = o.Throttle;
            input.Brakes = o.Brake;
            input.Handbrake = o.Handbrake;
            if (o.Throttle > 0.05f && transmission.Gear <= 0) transmission.ShiftInto(1);
            WaitingNow = o.Holding && speed < 0.5f;

            Trace.Append((Time.time - startedAt).ToString("0.00", Inv)).Append(',').Append(o.Along.ToString("0.0", Inv)).Append(',')
                .Append(o.Lateral.ToString("0.00", Inv)).Append(',').Append(speed.ToString("0.00", Inv)).Append(',').Append(o.Desired.ToString("0.00", Inv)).Append(',')
                .Append(o.SteerDegrees.ToString("0.0", Inv)).Append(',').Append(steerInput.ToString("0.00", Inv)).Append(',').Append(vehicle.steering.angle.ToString("0.0", Inv)).Append(',')
                .Append(o.Throttle.ToString("0.00", Inv)).Append(',').Append(o.Brake.ToString("0.00", Inv)).Append(',').Append(o.Handbrake.ToString("0", Inv)).Append(',')
                .Append(transmission.Gear).Append(',').Append(decision.StopAlong?.ToString("0.0", Inv) ?? "").Append(',').Append(gap?.ToString("0.0", Inv) ?? "").Append(',')
                .Append(decision.YieldAlong?.ToString("0.0", Inv) ?? "").Append(',').Append(decision.MaxSpeed?.ToString("0.0", Inv) ?? "").Append(',')
                .Append(decision.Why.Replace(',', ';')).Append('\n');

            Tally(speed, o.Along + Car.FrontAhead);
            if (Ended) return;
            if (o.Along + Car.FrontAhead >= driver.Length - 1.5f && Mathf.Abs(speed) < 0.3f) { End("arrived", true); return; }
            // Stuck: 30 s without 2 m of progress while nothing holds the car.
            if (o.Along > lastProgressAlong + 2f || WaitingNow) { lastProgressAlong = o.Along; lastProgressTime = Time.time; }
            else if (Time.time - lastProgressTime > 30f) { End("stuck", false); return; }
            stillSeconds = Mathf.Abs(speed) < 0.2f && !WaitingNow && o.Desired > 1f ? stillSeconds + Time.fixedDeltaTime : 0f;
            if (stillSeconds > 1f && stillSeconds < 1.03f) Log.Info($"NWH drive: standing with throttle {o.Throttle:0.00}, gear {transmission.Gear}, engine {(vehicle.powertrain.engine.IsRunning ? "running" : "off")}, rpm {vehicle.powertrain.engine.OutputRPM:0}.");
        }

        // The path's waypoints round the car, with Gley's live flags; CanChange() asked only where the rules want it.
        private void Marks(float front)
        {
            marks.Clear();
            var all = GleyGraph.Waypoints;
            if (all == null) return;
            var from = driver.Along - rearBehind - 10f;
            for (var k = 0; k < path.Count; k++)
            {
                var along = line.AlongOf[k];
                if (along < from) continue;
                if (along > front + 80f) break;
                var w = all[path[k]];
                if (w == null) continue;
                var (junction, road) = gley.JunctionOf(path[k]);
                var m = new PathMark
                {
                    Position = k, Waypoint = path[k], Along = along, Stop = w.stop && Options.TrafficLights, GiveWay = w.giveWay, Enter = w.enter, Exit = w.exit,
                    Junction = junction, Road = road,
                };
                if (traffic.Rules.ShouldAsk(m, front)) m.Free = GleyTraffic.CanChange(path[k]);
                marks.Add(m);
            }
        }

        // T3's judging, as T0's invariants: contact (OnCollisionEnter), a standstill over 30 s away from a red, a red
        // run (the front past a `stop` waypoint that was set while the car could still stop for it at HardDecel).
        private void Tally(float speed, float front)
        {
            var all = GleyGraph.Waypoints;
            if (all != null)
                for (var k = 0; k < path.Count; k++)
                {
                    var along = line.AlongOf[k];
                    if (along < prevFront - 5f) continue;
                    if (along > front + 80f) break;
                    var w = all[path[k]];
                    if (w == null || !w.stop || !Options.TrafficLights) { stoppable.Remove(k); continue; }
                    if (!stoppable.ContainsKey(k) && along > prevFront) stoppable[k] = along - prevFront - speed * 0.1f > speed * speed / (2f * PursuitDriver.HardDecel);
                    if (prevFront < along && front >= along && stoppable.TryGetValue(k, out var could) && could)
                    {
                        RedRuns++;
                        Log.Info($"NWH drive: ran the red at path position {k} ({along:0} m).");
                        Dump("red-run", $"front past the set stop at path position {k}");
                    }
                }
            prevFront = front;
            var atRed = decision.StopIsRed && decision.StopAlong - front < 8f;
            var before = standing;
            standing = Mathf.Abs(speed) < 0.3f && !decision.BackOff && !atRed ? standing + Time.fixedDeltaTime : 0f;
            if (before <= 30f && standing > 30f)
            {
                Stalls++;
                Log.Info($"NWH drive: standing 30 s at {driver.Along:0} m ({decision.Why}).");
                Dump("stall", $"standing 30 s ({decision.Why})");
            }
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (Ended || collision.rigidbody == null) return;
            var other = collision.rigidbody.GetComponent<VehicleComponent>();
            if (other == null) return;
            var ours = Car.Body.velocity.magnitude;
            var theirs = other.rb != null ? other.rb.velocity.magnitude : 0f;
            Contacts++;
            if (ours > 0.5f) ContactsMoving++;
            Log.Info($"NWH drive: contact with traffic car {other.GetIndex()} at {driver.Along:0} m, this car {ours:0.0} m/s, it {theirs:0.0} m/s ({decision.Why}).");
            Dump("contact", $"contact with traffic car {other.GetIndex()}: this car {ours:0.0} m/s, it {theirs:0.0} m/s");
        }

        private void Dump(string kind, string what)
        {
            if (Dumps.Count >= MaxDumps) return;
            try
            {
                var s = recorder.Build($"{kind}-{Dumps.Count + 1}", $"{what}; {Car.TypeName}, version {DrivenRidesMod.Version}", Car, topMs, path, line, arcs, slowStretches, gley);
                if (s != null) Dumps.Add((kind, s.ToJson()));
            }
            catch (Exception ex) { Log.Error("Traffic dump: " + ex); }
        }

        private void End(string reason, bool arrived)
        {
            Ended = true;
            Arrived = arrived;
            EndReason = reason;
            gley?.LeaveAll();
            if (Car?.Vehicle != null) { Car.Vehicle.input.Throttle = 0f; Car.Vehicle.input.Handbrake = 1f; }
        }

        public void Stop(string reason) { if (!Ended) End(reason, false); }

        // Destroyed with the car on every exit: never leave an intersection counting it (it would hold its light).
        private void OnDisable() => gley?.LeaveAll();

        private static int obstacleMask = -1;
        private readonly RaycastHit[] hits = new RaycastHit[16];

        private static int Mask()
        {
            if (obstacleMask >= 0) return obstacleMask;
            // Not Gley's cars: they are the traffic decisions' (GleyTraffic). People must stay here: the NWH car's
            // layer doesn't collide with them (phase 1, 13 October 2026).
            obstacleMask = (1 << LayerHelper.VehiclesLayerIndex) | (1 << LayerHelper.PlayerVehiclesLayerIndex);
            foreach (var name in new[] { "Human", "ParkedVehicles" })
            {
                var layer = LayerMask.NameToLayer(name);
                if (layer >= 0) obstacleMask |= 1 << layer;
            }
            return obstacleMask;
        }

        // The nearest parked car, player's car or person in the car's way, metres from its front bumper: a box as
        // wide as the car, cast along its heading, short while it turns (a straight cast would find the parked cars
        // round the corner).
        private float? GapAhead(float speed)
        {
            var t = Car.Root.transform;
            var turning = Mathf.Abs(Car.Vehicle.steering.angle) > 8f;
            var reach = turning ? 6f : Mathf.Clamp(6f + speed * 2.5f, 8f, 40f);
            var half = new Vector3(Car.BodyHalf.x * 0.8f, 0.4f, 0.1f);
            var origin = Car.FrontBumper + t.forward * 0.3f;
            origin.y = t.position.y + 0.8f;
            var n = Physics.BoxCastNonAlloc(origin, half, t.forward, hits, t.rotation, reach, Mask(), QueryTriggerInteraction.Ignore);
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
                if (best == null || d < best) best = d;
            }
            return best;
        }

        // Room behind the rear bumper for backing off: the same cast, backwards, a few metres.
        private float? GapBehind()
        {
            var t = Car.Root.transform;
            var half = new Vector3(Car.BodyHalf.x * 0.8f, 0.4f, 0.1f);
            var origin = t.TransformPoint(new Vector3(Car.BodyCentre.x, 0f, Car.BodyCentre.z - Car.BodyHalf.z)) - t.forward * 0.3f;
            origin.y = t.position.y + 0.8f;
            var mask = Mask();
            if (NwhCar.GleyLayer >= 0) mask |= 1 << NwhCar.GleyLayer;
            var n = Physics.BoxCastNonAlloc(origin, half, -t.forward, hits, t.rotation, 4f, mask, QueryTriggerInteraction.Ignore);
            float? best = null;
            for (var i = 0; i < n; i++)
            {
                var h = hits[i];
                if (h.rigidbody == Car.Body || h.collider.transform.IsChildOf(t)) continue;
                var d = h.distance + 0.3f;
                if (best == null || d < best) best = d;
            }
            return best;
        }
    }
}
