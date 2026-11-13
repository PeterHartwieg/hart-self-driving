#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DrivenRides.Core.Drive;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;
using DrivenRides.Core.Testing;
using GleyTrafficSystem;
using Helpers;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace DrivenRides
{
    internal sealed class NwhDrive : MonoBehaviour
    {
        public NwhCar Car = null!;
        public float TrackWidth;
        public CarSpec? Spec;
        public List<(int Position, float From, float To, float Offset)> Shifts = new List<(int, float, float, float)>();
        public HashSet<int>? Fillets;
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
        private readonly BrakeEvents slowdowns = new BrakeEvents();
        private bool backing;
        private string? blockedWhat;
        private Recovery? recovery;
        private float pushSeconds, staticHeldSeconds;
        private bool stuckReplanned;
        private int lastBlockerLayer = -1;
        private string lastBlocker = "";

        public event Action<int, string>? Blocked;
        public readonly WayOnControl WayOn = new WayOnControl { Log = text => Log.Trace("NWH drive: " + text) };
        private int blockedPosition;

        public int Contacts, ContactsCaused, Stalls, RedRuns;
        public int UnsettledDecisions;
        private readonly Dictionary<int, (Vector3 At, float Ms, float Time)> triggerStarts = new Dictionary<int, (Vector3, float, float)>();
        public readonly List<(float Ms, float Metres, float Seconds)> TriggerStops = new List<(float, float, float)>();
        private Vector3 preVelocity;
        private float standing;
        private float prevFront;
        private readonly Dictionary<int, bool> stoppable = new Dictionary<int, bool>();
        public readonly List<(string Kind, string Json)> Dumps = new List<(string, string)>();
        private const int MaxDumps = 3;

        public bool Ended { get; private set; }
        public bool Arrived { get; private set; }
        public string EndReason { get; private set; } = "";
        public bool WaitingNow { get; private set; }
        public float Along => driver.Along;
        internal Polyline PlannedLine => line.Line;
        public bool Recording;
        public bool SlowdownDumps = true;
        private const float SlowdownDumpEvery = 10f;
        private float slowdownDumpAt = float.MinValue;
        public bool MeasureCost;
        public CostSamples? FrameCost, FrameSteps;
        private int costFrame = -1;
        private float costThisFrame;
        private int stepsThisFrame;
        private double scaleSum;
        private int scaleFrames;
        public const double WayOnFrameMs = 3.0;
        private const double WayOnMinStepMs = 0.5;
        private int budgetFrame = -1;
        private double budgetLeftMs;
        private const int TraceLines = 30000;
        private const string TraceHeader = "t,along,lateral,speed,desired,steer,input,angle,throttle,brake,handbrake,gear,stop,gap,yield,cap,traffic,forced,marginal\n";
        private string[]? traceRing;
        private int traceNext, traceCount;
        private readonly StringBuilder traceLine = new StringBuilder(160);
        public CostSamples? ReadCost, DecideCost, CarsRead;
        private static readonly double MicrosPerTick = 1e6 / Stopwatch.Frequency;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public void Begin(NwhCar car, List<int> path, IReadOnlyList<(int Entry, Polyline Arc)> arcs, IEnumerable<(int Position, float SpeedKmh)> slow, float topKmh)
        {
            Car = car;
            topMs = topKmh / 3.6f;
            if (Recording && traceRing == null)
            {
                traceRing = new string[TraceLines];
                ReadCost = new CostSamples();
                DecideCost = new CostSamples();
                CarsRead = new CostSamples();
            }
            if (MeasureCost && DecideCost == null)
            {
                ReadCost = new CostSamples();
                DecideCost = new CostSamples();
                CarsRead = new CostSamples();
            }
            if ((Recording || MeasureCost) && FrameCost == null)
            {
                FrameCost = new CostSamples();
                FrameSteps = new CostSamples();
            }
            UsePath(path, arcs, slow);
            var rearNow = car.RearAxle;
            driver.StartAt(rearNow.x, rearNow.z);
            lastProgressAlong = driver.Along;
            prevFront = driver.Along + car.Wheelbase;
            gley = new GleyTraffic(car.Root);
            startedAt = Time.time;
            lastProgressTime = Time.time;
        }

        public void Begin(NwhCar car, DrivePlan plan, CarSpec spec, float topKmh)
        {
            TrackWidth = spec.TrackWidth;
            Spec = spec;
            Shifts = plan.Shifts;
            Fillets = plan.Fillets;
            Begin(car, plan.Path, plan.Arcs, plan.Slow, topKmh);
        }

        public void DriveOn(DrivePlan plan)
        {
            if (Ended || driver == null) return;
            blockedWhat = null;
            recovery = null;
            backing = false;
            pushSeconds = staticHeldSeconds = 0f;
            Restart(plan);
        }

        public bool GiveUpStep(string why)
        {
            if (Ended || driver == null || blockedWhat != null || WayOn.Replan == null) return false;
            var front = driver.Along + Car.FrontAhead;
            var position = 0;
            while (position + 1 < path.Count && line.AlongOf[position + 1] <= front) position++;
            return Block(position, front, why);
        }

        public float LeftMetres => driver == null ? 0f : Mathf.Max(0f, driver.Length - driver.Along - Car.FrontAhead);

        private void Restart(DrivePlan plan)
        {
            gley.LeaveAll();
            Shifts = plan.Shifts;
            Fillets = plan.Fillets;
            UsePath(plan.Path, plan.Arcs, plan.Slow);
            WayOn.Reset();
            lastInput = default;
            var rearNow = Car.RearAxle;
            driver.StartAt(rearNow.x, rearNow.z);
            lastProgressAlong = driver.Along;
            lastProgressTime = Time.time;
            stillSeconds = standing = 0f;
            prevFront = Car.Wheelbase;
            stoppable.Clear();
            castHold = null;
            castClearFor = 0f;
            Log.Trace($"NWH drive: driving on along a new path of {plan.Path.Count} waypoints, {line.Line.Length:0} m.");
        }

        private void UsePath(List<int> path, IReadOnlyList<(int Entry, Polyline Arc)> arcs, IEnumerable<(int Position, float SpeedKmh)> slow)
        {
            var car = Car;
            this.path = path;
            this.arcs.Clear();
            slowStretches.Clear();
            this.arcs.AddRange(arcs);
            var all = GleyGraph.Waypoints!;
            var points = new List<Vec3>(path.Count);
            foreach (var i in path) points.Add(GleyGraph.ToVec(all[i].position));
            line = DriveLine.Build(points, arcs, Fillets).Shifted(Shifts);
            driver = new PursuitDriver(line, car.Wheelbase, car.FrontAhead, topMs, TrackWidth);
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
            traffic = new TrafficDriver(GleyGraph.Graph!, line, driver, body) { Model = Spec?.Model(0, 0, 0) };
        }

        private void FixedUpdate()
        {
            if (Ended || driver == null || Car?.Vehicle == null) return;
            try { DriveStep(); }
            catch (Exception ex)
            {
                // Unity abandons a failed FixedUpdate and calls it again next step. End the drive once instead,
                // so Ride takes its vanilla completion path and the passenger is not left in a stationary car.
                Log.Error("NWH drive failed: " + ex);
                End("drive failed: " + ex.GetType().Name, false);
            }
        }

        private void DriveStep()
        {
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
            preVelocity = Car.Body.velocity;
            var speed = Vector3.Dot(Car.Body.velocity, forward);
            var heading = Mathf.Atan2(forward.x, forward.z);
            var front = driver.Along + Car.FrontAhead;

            Marks(front);
            var ticks = Stopwatch.GetTimestamp();
            gley.ReadCars(rear, TrafficDriver.SeeMetres, cars);
            var read = Stopwatch.GetTimestamp();
            ReadCost?.Add((float)((read - ticks) * MicrosPerTick));
            CarsRead?.Add(cars.Count);
            TriggerStopsStep();
            if (Recording || SlowdownDumps) recorder.Record(Time.time, rear, heading, speed, driver.Along, vehicle.steering.angle, driver.Integral, cars, gley.Components);
            var castGap = HoldForCast(GapAhead(speed));
            if (blockedWhat == null && !backing)
            {
                var at = 0;
                while (at + 1 < path.Count && line.AlongOf[at + 1] <= front) at++;
                var planTicks = Stopwatch.GetTimestamp();
                WayOn.BudgetMs = StepBudget();
                var ahead = WayOn.Ahead(Time.time, Time.fixedDeltaTime, at, line.AlongOf, front, speed, PoseNow);
                budgetLeftMs -= (Stopwatch.GetTimestamp() - planTicks) * MicrosPerTick / 1000.0;
                if (ahead.Act == WayOnControl.Act.Restart && ahead.Plan != null) { Restart(ahead.Plan); return; }
                if (ahead.Act == WayOnControl.Act.End) { End(ahead.Why, false); return; }
                if (ahead.Act == WayOnControl.Act.Block) Block(ahead.Position >= 0 ? ahead.Position : at, front, ahead.Why);
            }
            var snapshot = new TrafficSnapshot
            {
                Time = Time.time, RearX = rear.x, RearZ = rear.z, Heading = heading, Speed = speed, Wheel = vehicle.steering.angle, Marks = marks, Cars = cars, Flags = gley,
                GapBehind = traffic.BackingOff || decision.GapAhead < 4f || Mathf.Abs(speed) < TrafficDriver.StandingMs ? GapBehind() : null, GapAhead = castGap,
                PlannerHoldAlong = WayOn.HoldAlong,
            };
            ticks = Stopwatch.GetTimestamp();
            traffic.Decide(snapshot, decision);
            var decideMicros = (float)((Stopwatch.GetTimestamp() - ticks) * MicrosPerTick);
            DecideCost?.Add(decideMicros);
            PerFrame(decideMicros);
            foreach (var e in decision.Events)
            {
                gley.Apply(e);
                Log.Trace($"NWH drive: {(e.Kind == JunctionEventKind.Enter ? "in" : "out of")} intersection {e.Junction} at {driver.Along:0} m.");
            }
            if (decision.Unsettled)
            {
                UnsettledDecisions++;
                Log.Trace($"NWH drive: the final pass still tightened the stops after {decision.PassRounds} rounds; a stop now, the emergency rule's ({driver.Along:0} m, {speed:0.0} m/s).");
            }
            if (decision.Why != lastWhy)
            {
                if (decision.Why.Length > 0) Log.Trace($"NWH drive: {decision.Why} at {driver.Along:0} m, {speed:0.0} m/s{(decision.YieldNote.Length > 0 ? " (" + decision.YieldNote + ")" : "")}.");
                lastWhy = decision.Why;
            }
            var slowdown = slowdowns.Step(Time.time, driver.Along, speed, vehicle.steering.angle, decision.Why, decision.Note, decision.StopIsRed);
            if (slowdown != null)
            {
                Log.Trace("NWH drive: " + slowdown);
                SlowdownDump();
            }

            var input = vehicle.input;
            var transmission = vehicle.powertrain.transmission;
            if (blockedWhat != null)
            {
                recovery ??= new Recovery();
                var centre = t.TransformPoint(Car.BodyCentre);
                var next = recovery.Next(centre.x, centre.z, heading, Car.BodyHalf.x, Car.BodyHalf.z, speed, GapBehind(), cars, Time.fixedDeltaTime);
                input.Steering = 0f;
                input.Throttle = 0f;
                if (next == Recovery.Step.Done)
                {
                    input.Brakes = 0f;
                    input.Handbrake = 1f;
                    if (Mathf.Abs(speed) > 0.2f) return;
                    if (WayOn.Job == null) Log.Trace($"NWH drive: {recovery.Ended} ({recovery.Backed:0.0} m); asking for another way.");
                    var planTicks = Stopwatch.GetTimestamp();
                    WayOn.BudgetMs = StepBudget();
                    var way = WayOn.Blocked(blockedPosition, blockedWhat, PoseNow);
                    budgetLeftMs -= (Stopwatch.GetTimestamp() - planTicks) * MicrosPerTick / 1000.0;
                    if (way.Act == WayOnControl.Act.Wait) return;
                    if (way.Act != WayOnControl.Act.Restart || way.Plan == null) { End(way.Why.Length > 0 ? way.Why : "blocked: " + blockedWhat, false); return; }
                    blockedWhat = null;
                    recovery = null;
                    input.Handbrake = 0f;
                    Restart(way.Plan);
                    return;
                }
                input.Handbrake = next == Recovery.Step.Brake ? 1f : 0f;
                input.Brakes = next == Recovery.Step.Reverse ? 0.35f : 0f;
                driver.BackTo(rear.x, rear.z);
                lastProgressTime = Time.time;
                return;
            }
            if (decision.BackOff)
            {
                input.Steering = 0f;
                input.Throttle = 0f;
                input.Handbrake = 0f;
                input.Brakes = 0.35f;
                driver.BackTo(rear.x, rear.z);
                if (!backing) Log.Trace($"NWH drive: backing off from traffic car {decision.BackOffFrom}.");
                backing = true;
                lastProgressTime = Time.time;
                WaitingNow = true;
                Tally(speed, front - (Car.FrontAhead - Car.Wheelbase));
                return;
            }
            backing = false;
            var gap = castGap;
            var gapSpeed = 0f;
            if (decision.GapAhead is float carGap && (gap == null || carGap < gap)) { gap = carGap; gapSpeed = decision.GapSpeed; lastBlockerLayer = -1; }
            var stopAlong = decision.StopAlong;
            if (WayOn.HoldAlong is float holdAt && (stopAlong == null || holdAt < stopAlong)) stopAlong = holdAt;
            lastInput = new DriveInput
            {
                RearX = rear.x, RearZ = rear.z, Heading = heading, SpeedMs = speed, Dt = Time.fixedDeltaTime, WheelDegrees = vehicle.steering.angle,
                StopAlong = stopAlong, GapAhead = gap, GapSpeed = gapSpeed, YieldAlong = decision.YieldAlong, MaxSpeed = decision.MaxSpeed,
            };
            var o = driver.Step(lastInput);
            var lockNow = Mathf.Max(1f, Car.LockAt(speed));
            var steerInput = Mathf.Clamp(o.SteerDegrees / lockNow, -1f, 1f);
            input.Steering = steerInput;
            input.Throttle = o.Throttle;
            input.Brakes = o.Brake;
            input.Handbrake = o.Handbrake;
            if (o.Throttle > 0.05f && transmission.Gear <= 0) transmission.ShiftInto(1);
            WaitingNow = o.Holding && speed < 0.5f;

            if (Recording) TraceStep(o, speed, steerInput, transmission.Gear, gap);

            Tally(speed, o.Along + Car.Wheelbase);
            if (Ended) return;
            if (o.Along + Car.FrontAhead >= driver.Length - 1.5f && Mathf.Abs(speed) < 0.3f) { End("arrived", true); return; }
            if (BlockedRule(o, speed, gap)) return;
            if (o.Along > lastProgressAlong + 2f || WaitingNow) { lastProgressAlong = o.Along; lastProgressTime = Time.time; stuckReplanned = false; }
            else if (Time.time - lastProgressTime > 30f)
            {
                lastProgressTime = Time.time;
                if (!stuckReplanned && WayOn.Replan != null) { stuckReplanned = true; Block(o, "stuck: no progress for 30 s"); return; }
                End("stuck", false);
                return;
            }
            stillSeconds = Mathf.Abs(speed) < 0.2f && !WaitingNow && o.Desired > 1f ? stillSeconds + Time.fixedDeltaTime : 0f;
            if (stillSeconds > 1f && stillSeconds < 1.03f) Log.Trace($"NWH drive: standing with throttle {o.Throttle:0.00}, gear {transmission.Gear}, engine {(vehicle.powertrain.engine.IsRunning ? "running" : "off")}, rpm {vehicle.powertrain.engine.OutputRPM:0}.");
        }

        private void TraceStep(DriveOutput o, float speed, float steerInput, int gear, float? gap)
        {
            var vehicle = Car.Vehicle;
            var sb = traceLine.Clear();
            sb.Append((Time.time - startedAt).ToString("0.00", Inv)).Append(',').Append(o.Along.ToString("0.0", Inv)).Append(',')
                .Append(o.Lateral.ToString("0.00", Inv)).Append(',').Append(speed.ToString("0.00", Inv)).Append(',').Append(o.Desired.ToString("0.00", Inv)).Append(',')
                .Append(o.SteerDegrees.ToString("0.0", Inv)).Append(',').Append(steerInput.ToString("0.00", Inv)).Append(',').Append(vehicle.steering.angle.ToString("0.0", Inv)).Append(',')
                .Append(o.Throttle.ToString("0.00", Inv)).Append(',').Append(o.Brake.ToString("0.00", Inv)).Append(',').Append(o.Handbrake.ToString("0", Inv)).Append(',')
                .Append(gear).Append(',').Append(decision.StopAlong?.ToString("0.0", Inv) ?? "").Append(',').Append(gap?.ToString("0.0", Inv) ?? "").Append(',')
                .Append(decision.YieldAlong?.ToString("0.0", Inv) ?? "").Append(',').Append(decision.MaxSpeed?.ToString("0.0", Inv) ?? "").Append(',')
                .Append(decision.Why.Replace(',', ';')).Append(',').Append(',')
                .Append('\n');
            if (traceRing == null) return;
            traceRing[traceNext] = sb.ToString();
            traceNext = (traceNext + 1) % TraceLines;
            if (traceCount < TraceLines) traceCount++;
        }

        public string TraceText()
        {
            var all = new StringBuilder(TraceHeader, 64 + traceCount * 120);
            var first = (traceNext - traceCount + TraceLines) % TraceLines;
            if (traceRing != null) for (var k = 0; k < traceCount; k++) all.Append(traceRing[(first + k) % TraceLines]);
            return all.ToString();
        }

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
                    if (Mathf.Abs(speed) < 0.5f && along > prevFront && along - prevFront < 10f) stoppable[k] = true;
                    if (prevFront < along && front >= along && stoppable.TryGetValue(k, out var could) && could)
                    {
                        RedRuns++;
                        Log.Trace($"NWH drive: ran the red at path position {k} ({along:0} m).");
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
                Log.Trace($"NWH drive: standing 30 s at {driver.Along:0} m ({decision.Why}).");
                Dump("stall", $"standing 30 s ({decision.Why})");
            }
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (Ended || collision.rigidbody == null) return;
            var other = collision.rigidbody.GetComponent<VehicleComponent>();
            if (other == null) return;
            Contacts++;
            var theirV = Vector3.zero;
            var theirHeading = 0f;
            var sampled = false;
            for (var i = 0; i < gley.Components.Count && i < cars.Count; i++)
                if (gley.Components[i] == other) { theirV = new Vector3(cars[i].Vx, 0f, cars[i].Vz); theirHeading = cars[i].Heading; sampled = true; break; }
            var normal = collision.contactCount > 0 ? collision.GetContact(0).normal : Vector3.zero;
            var ourC = Car.Body.worldCenterOfMass;
            var theirC = other.rb != null ? other.rb.worldCenterOfMass : other.transform.position;
            var forward = Car.Root.transform.forward;
            var caused = !sampled || TrafficVerdict.CausedByRide(preVelocity.x, preVelocity.z, Mathf.Atan2(forward.x, forward.z), ourC.x, ourC.z, theirV.x, theirV.z, theirHeading, theirC.x, theirC.z);
            var ours = new Vector2(preVelocity.x, preVelocity.z).magnitude;
            var theirs = new Vector2(theirV.x, theirV.z).magnitude;
            var towards = normal.sqrMagnitude > 0f ? Mathf.Abs(Vector3.Dot(preVelocity, Vector3.ProjectOnPlane(normal, Vector3.up).normalized)) : 0f;
            if (caused) ContactsCaused++;
            Log.Trace($"NWH drive: contact with traffic car {other.GetIndex()} at {driver.Along:0} m, this car {ours:0.0} m/s before it ({towards:0.0} along the contact normal), it {theirs:0.0} m/s, {(caused ? "caused by this car" : "caused by the other car")} ({decision.Why}).");
            Dump("contact", $"contact with traffic car {other.GetIndex()}: this car {ours:0.0} m/s, it {theirs:0.0} m/s");
        }

        private void TriggerStopsStep()
        {
            for (var i = 0; i < cars.Count; i++)
            {
                var c = cars[i];
                var at = new Vector3(c.X, 0f, c.Z);
                if (triggerStarts.TryGetValue(c.Id, out var start))
                {
                    if (!c.StoppedForUs) { triggerStarts.Remove(c.Id); continue; }
                    if (c.Speed >= 0.1f) continue;
                    var metres = Vector3.Distance(start.At, at);
                    TriggerStops.Add((start.Ms, metres, Time.time - start.Time));
                    Log.Trace($"NWH drive: traffic car {c.Id} stopped for this car in its trigger: {metres:0.00} m and {Time.time - start.Time:0.00} s from {start.Ms:0.0} m/s.");
                    triggerStarts.Remove(c.Id);
                }
                else if (c.StoppedForUs && c.Speed >= 0.5f) triggerStarts[c.Id] = (at, c.Speed, Time.time);
            }
        }

        private void SlowdownDump()
        {
            var e = slowdowns.Last;
            if (!SlowdownDumps || e.Low > 1f || e.Reason.Length == 0 || e.Red || e.Reason.StartsWith("stop line") || e.Reason.StartsWith("give-way line")) return;
            if (Time.time - slowdownDumpAt < SlowdownDumpEvery) return;
            slowdownDumpAt = Time.time;
            try
            {
                var name = $"slowdown-{DateTime.Now:yyyyMMdd-HHmmss}-{driver!.Along:0}m.json";
                var what = $"slowdown {e.Peak:0.0} to {e.Low:0.0} m/s for {e.Seconds:0.0} s, wheel up to {e.Wheel:0} degrees, for: {e.Reason}";
                var s = recorder.Build(name, $"{what}; {Car!.TypeName}, version {DrivenRidesMod.Version}", Car, topMs, TrackWidth, path, line!, arcs, slowStretches, gley);
                if (s == null) return;
                var folder = System.IO.Path.Combine(GleyGraph.DataFolder, "tests");
                System.IO.Directory.CreateDirectory(folder);
                System.IO.File.WriteAllText(System.IO.Path.Combine(folder, name), s.ToJson());
                Log.Trace($"NWH drive: slowdown dump {name}.");
            }
            catch (Exception ex) { Log.Error("Slowdown dump: " + ex); }
        }

        private void Dump(string kind, string what)
        {
            if (Dumps.Count >= MaxDumps) return;
            try
            {
                var s = recorder.Build($"{kind}-{Dumps.Count + 1}", $"{what}; {Car.TypeName}, version {DrivenRidesMod.Version}", Car, topMs, TrackWidth, path, line, arcs, slowStretches, gley);
                if (s != null) Dumps.Add((kind, s.ToJson()));
            }
            catch (Exception ex) { Log.Error("Traffic dump: " + ex); }
        }

        private const float CastClearSeconds = 0.8f;
        private float? castHold;
        private float castClearFor;

        private float? HoldForCast(float? gap)
        {
            if (gap is float g && g < PursuitDriver.StopGap + 1f) { castHold = g; castClearFor = 0f; return gap; }
            if (castHold is not float held) return gap;
            castClearFor = gap == null || gap > PursuitDriver.StopGap + 3f ? castClearFor + Time.fixedDeltaTime : 0f;
            if (castClearFor >= CastClearSeconds) { castHold = null; return gap; }
            return gap is float now ? Mathf.Min(now, held) : held;
        }

        private CarPose PoseNow()
        {
            var forward = Car.Root.transform.forward;
            var rear = Car.RearAxle;
            var stop = lastInput.StopAlong;
            if (WayOn.HoldAlong is float hold && (stop == null || hold < stop)) stop = hold;
            return new CarPose(rear.x, rear.z, Mathf.Atan2(forward.x, forward.z), Vector3.Dot(Car.Body.velocity, forward), Car.Vehicle.steering.angle)
            {
                StopAlong = stop, YieldAlong = lastInput.YieldAlong, GapAhead = lastInput.GapAhead, GapSpeed = lastInput.GapSpeed, MaxSpeed = lastInput.MaxSpeed,
            };
        }

        private DriveInput lastInput;

        private bool BlockedRule(DriveOutput o, float speed, float? gap)
        {
            var dt = Time.fixedDeltaTime;
            if (decision.BlockedBy >= 0) return Block(o, decision.BlockedWhy);
            pushSeconds = o.Throttle > 0.5f && Mathf.Abs(speed) < 0.2f ? pushSeconds + dt : 0f;
            var still = gap != null && WaitingNow && lastBlockerLayer >= 0
                && (lastBlockerLayer == LayerHelper.ParkedVehiclesLayerIndex || (TestCar.ObstacleMask & (1 << lastBlockerLayer)) != 0);
            staticHeldSeconds = still ? staticHeldSeconds + dt : 0f;
            if (pushSeconds < 2f && staticHeldSeconds < 3f) return false;
            return Block(o, pushSeconds >= 2f ? "no motion at full throttle" : $"held by '{lastBlocker}' {gap:0.0} m ahead, which doesn't move");
        }

        private bool Block(DriveOutput o, string what)
        {
            var front = o.Along + Car.FrontAhead;
            var position = 0;
            while (position + 1 < path.Count && line.AlongOf[position + 1] <= front) position++;
            return Block(position, front, what);
        }

        private bool Block(int position, float front, string what)
        {
            Log.Trace($"NWH drive: blocked ({what}) at path position {position}, {front:0} m along; backing off.");
            blockedWhat = what;
            blockedPosition = position;
            recovery = null;
            WayOn.Reset();
            pushSeconds = staticHeldSeconds = 0f;
            try { Blocked?.Invoke(position, what); } catch (Exception ex) { Log.Error("NWH drive, blocked: " + ex.Message); }
            return true;
        }

        private bool OnOwnPath(Vector3 point)
        {
            var local = Car.Root.transform.InverseTransformPoint(point);
            local.z -= Car.RearZ;
            if (local.z <= 0f) return false;
            var curvature = (float)CarModel.Curvature(Car.Vehicle.steering.angle, Car.Wheelbase, TrackWidth);
            float off;
            if (Mathf.Abs(curvature) < 1e-3f) off = Mathf.Abs(local.x);
            else
            {
                var radius = 1f / curvature;
                off = Mathf.Abs(Mathf.Sqrt((local.x - radius) * (local.x - radius) + local.z * local.z) - Mathf.Abs(radius));
            }
            return off <= Car.BodyHalf.x + 0.3f;
        }

        private void End(string reason, bool arrived)
        {
            if (!Ended && DecideCost != null && DecideCost.Total > 0) Log.Info(CostLine());
            Ended = true;
            Arrived = arrived;
            EndReason = reason;
            if (Car?.Vehicle != null) { Car.Vehicle.input.Throttle = 0f; Car.Vehicle.input.Handbrake = 1f; }
            gley?.LeaveAll();
        }

        private double StepBudget()
        {
            if (Time.frameCount != budgetFrame) { budgetFrame = Time.frameCount; budgetLeftMs = WayOnFrameMs; }
            return Math.Max(WayOnMinStepMs, budgetLeftMs);
        }

        private void PerFrame(float micros)
        {
            if (FrameCost == null || FrameSteps == null) return;
            if (Time.frameCount != costFrame)
            {
                if (costFrame >= 0) { FrameCost.Add(costThisFrame); FrameSteps.Add(stepsThisFrame); }
                costFrame = Time.frameCount;
                scaleSum += Time.timeScale;
                scaleFrames++;
                costThisFrame = 0f;
                stepsThisFrame = 0;
            }
            costThisFrame += micros;
            stepsThisFrame++;
        }

        public string CostLine() => DecideCost == null || ReadCost == null || CarsRead == null ? "NWH drive: traffic cost not recorded." :
            $"NWH drive: traffic cost over {DecideCost.Total} steps: reading Gley's cars within {TrafficDriver.SeeMetres:0} m " +
            $"({CarsRead.Percentile(50):0} cars at the median, {CarsRead.Max:0} at most) p50 {ReadCost.Percentile(50):0} µs, p99 {ReadCost.Percentile(99):0} µs; " +
            $"deciding p50 {DecideCost.Percentile(50):0} µs, p99 {DecideCost.Percentile(99):0} µs, max {DecideCost.Max:0} µs" +
            (FrameCost != null && FrameSteps != null && FrameCost.Total > 0
                ? $"; per frame at {scaleSum / Math.Max(1, scaleFrames):0.#}x on average ({FrameSteps.Percentile(50):0} physics steps at the median, {FrameSteps.Max:0} at most) p50 {FrameCost.Percentile(50):0} µs, p99 {FrameCost.Percentile(99):0} µs, max {FrameCost.Max:0} µs."
                : ".");

        public void Stop(string reason) { if (!Ended) End(reason, false); }

        private void OnDisable() => gley?.LeaveAll();

        private static int obstacleMask = -1;
        private readonly RaycastHit[] hits = new RaycastHit[16];

        private static int humanMask = -1;

        // Not Gley's cars: they are the traffic decisions' (GleyTraffic). People only with the option "Stop for
        // pedestrians" (Options.Pedestrians, off by default): the NWH car's layer doesn't collide with them (phase 1,
        // 13 October 2026), so with it off the car drives through them as Gley's cars do, and nothing else sees them.
        private static int Mask()
        {
            if (obstacleMask < 0)
            {
                obstacleMask = (1 << LayerHelper.VehiclesLayerIndex) | (1 << LayerHelper.PlayerVehiclesLayerIndex);
                var parked = LayerMask.NameToLayer("ParkedVehicles");
                if (parked >= 0) obstacleMask |= 1 << parked;
                var human = LayerMask.NameToLayer("Human");
                humanMask = human >= 0 ? 1 << human : 0;
            }
            return Options.Pedestrians ? obstacleMask | humanMask : obstacleMask;
        }

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
            lastBlockerLayer = -1;
            for (var i = 0; i < n; i++)
            {
                var h = hits[i];
                if (h.rigidbody == Car.Body || h.collider.transform.IsChildOf(t)) continue;
                var point = h.distance > 0f ? h.point : h.collider.bounds.center;
                var (_, side) = line.Locate(point.x, point.z, driver.Along, reach + 10f);
                if (Mathf.Abs(side) > Car.BodyHalf.x + 0.4f && !OnOwnPath(point)) continue;
                var d = h.distance + 0.3f;
                if (best == null || d < best) { best = d; lastBlocker = $"{h.collider.name} ({LayerMask.LayerToName(h.collider.gameObject.layer)})"; lastBlockerLayer = h.collider.gameObject.layer; }
            }
            return best;
        }

        private float? GapBehind()
        {
            var t = Car.Root.transform;
            var half = new Vector3(Car.BodyHalf.x * 0.8f, 0.4f, 0.1f);
            var origin = t.TransformPoint(new Vector3(Car.BodyCentre.x, 0f, Car.BodyCentre.z - Car.BodyHalf.z)) - t.forward * 0.3f;
            origin.y = t.position.y + 0.8f;
            var mask = Mask();
            if (NwhCar.GleyLayer >= 0) mask |= 1 << NwhCar.GleyLayer;
            var n = Physics.BoxCastNonAlloc(origin, half, -t.forward, hits, t.rotation, TrafficDriver.MaxBackOff + TrafficDriver.BackOffClear, mask, QueryTriggerInteraction.Ignore);
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
