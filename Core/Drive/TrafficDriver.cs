using System;
using System.Collections.Generic;
using DrivenRides.Core.Routing;

namespace DrivenRides.Core.Drive
{
    public sealed partial class TrafficDriver
    {
        public const float SweepMetres = 40f;
        public const float SeeMetres = 150f;
        public const float SweepStep = 1f;
        private const float MaxHorizon = 12f;
        private const float KeepClearReach = 160f;
        public const int MaxPassRounds = 2;
        public int PassRoundsLimit { get; set; } = MaxPassRounds;

        // The final pass ran out of rounds still tightening the stops: a stop now for every car a stop is for, the
        // emergency rule's (review round 8, CC2; round 9: the forced continuation went).
        private void Fallback(TrafficDecision d, in TrafficSnapshot s, float front, float horizon)
        {
            fallbackCars.Clear();
            for (var j = 0; j < deferred.Count; j++) fallbackCars.Add(s.Cars[deferred[j].Car].Id);
            foreach (var id in holds.Keys) if (!fallbackCars.Contains(id)) fallbackCars.Add(id);
            if (d.YieldFor >= 0 && !fallbackCars.Contains(d.YieldFor)) fallbackCars.Add(d.YieldFor);
            if (!(d.YieldAlong <= front)) d.YieldWay = front;
            d.YieldAlong = Math.Min(d.YieldAlong ?? float.MaxValue, front);
            if (d.YieldFor < 0 && fallbackCars.Count > 0) d.YieldFor = fallbackCars[0];
            for (var i = 0; i < fallbackCars.Count; i++) if (!d.EmergencyFor.Contains(fallbackCars[i])) d.EmergencyFor.Add(fallbackCars[i]);
        }

        private readonly List<int> fallbackCars = new List<int>();
        public const float Headway = TrafficMargins.Headway;
        public const float StartHeadway = TrafficMargins.StartHeadway;
        public const float SideMargin = TrafficMargins.SideMargin;
        public const float EndMargin = TrafficMargins.EndMargin;
        public const float GroundMargin = 0.4f;
        public const float SqueezeMargin = 0.1f;
        public const float StandingMs = 0.4f;
        public const float MovingMs = 0.8f;
        public const float NoseRoom = 2f;
        public const float StuckSeconds = 60f;
        public const float ConflictMemory = TrafficMargins.ConflictMemory;
        public const float FollowBrake = TrafficMargins.JoinBrake;
        public const float FollowGap = TrafficMargins.JoinGap;
        public const float CreepMs = TrafficMargins.CreepMs;
        public const float NoseInMs = 5f;
        public const float SqueezeMs = TrafficMargins.SqueezeMs;
        public const float PassingNear = 0.5f;
        public const float FastAccel = 3f;
        public const float SlowAccel = 2f;
        public const float BackOffClear = 0.8f;
        public const float MaxBackOff = 6f;
        public const float MinBackOff = 1.5f;
        public const float RoomBehind = 0.8f;
        public const float BackOffMs = 1.2f;
        public const float TriggerStopMetres = 1.25f;
        public const float TriggerReact = 0.1f;
        public const float TriggerReactCautious = 0.2f;
        public const float TriggerBrake = 8f;
        public const float BrakeLatency = 0.15f;
        public const float StopUncertainty = 0.43f;
        public const float LaunchDelay = 0.4f;

        private readonly LaneGraph graph;
        private readonly DriveLine line;
        private readonly PursuitDriver driver;
        private readonly RideBody body;
        private Corridor corridor;
        private readonly Corridor mainCorridor = new Corridor();
        private readonly HashSet<int> heldFor = new HashSet<int>();
        private readonly HashSet<int> present = new HashSet<int>();
        private readonly HashSet<int> moving = new HashSet<int>();
        private readonly Dictionary<int, float> standingSince = new Dictionary<int, float>();
        private readonly Dictionary<int, (float Speed, float Time, float Gain)> pace = new Dictionary<int, (float, float, float)>();
        private readonly Dictionary<int, (float Along, float Time)> yieldMemory = new Dictionary<int, (float, float)>();
        private int backingFrom = -1;
        private float backStartX, backStartZ;

        private const int MaxPoses = 64;
        private readonly Box[] poses = new Box[MaxPoses];
        private readonly Box[] alt = new Box[MaxPoses], alt2 = new Box[MaxPoses];
        private readonly bool[] altOn = new bool[MaxPoses], alt2On = new bool[MaxPoses];
        private readonly float[] grow = new float[MaxPoses];
        private readonly float[] planned = new float[MaxPoses], rearAt = new float[MaxPoses], early = new float[MaxPoses], late = new float[MaxPoses], slowSpeed = new float[MaxPoses], fastSpeed = new float[MaxPoses];
        private readonly float[] safeLate = new float[MaxPoses], safeSpeed = new float[MaxPoses];

        private float holdFront, safeFor, leaderCap = float.MaxValue;
        private readonly List<(int Car, int RunStart, int Exit, float RunFrom, YieldNote Note)> deferred = new List<(int, int, int, float, YieldNote)>();
        private readonly List<Go> deferredGo = new List<Go>();
        private readonly List<float> deferredMargin = new List<float>();
        private readonly HashSet<int> followed = new HashSet<int>();
        private bool deferNow;
        private int nosedInFor = -1;
        private readonly float[] headings = new float[MaxPoses];
        private readonly int[] keepClearFrom = new int[MaxPoses], keepClearFor = new int[MaxPoses];
        private int count;
        private YieldNote conflictNote;
        private float headway = Headway;
        private int whyKind, whyId = -1;
        private string whyText = "";
        private bool noseIn;
        private float? standingGap;
        private readonly List<(float From, float To, float Ms)> creeps = new List<(float, float, float)>();
        private float sweepMinX, sweepMinZ, sweepMaxX, sweepMaxZ;

        public JunctionRules Rules { get; } = new JunctionRules();
        private readonly Func<int, int> roadHeld;

        private readonly Predicate<int> absent;
        private readonly List<int> scratch = new List<int>();

        private void Forget<T>(Dictionary<int, T> memory)
        {
            if (memory.Count == 0) return;
            scratch.Clear();
            foreach (var id in memory.Keys) if (!present.Contains(id)) scratch.Add(id);
            for (var i = 0; i < scratch.Count; i++) memory.Remove(scratch[i]);
        }

        public TrafficDriver(LaneGraph graph, DriveLine line, PursuitDriver driver, RideBody body)
        {
            absent = id => !present.Contains(id);
            corridor = mainCorridor;
            for (var i = 0; i < 24; i++) corridorPool.Add(new Corridor());
            this.graph = graph;
            this.line = line;
            this.driver = driver;
            this.body = body;
            roadHeld = Rules.RoadHeld;
            Rules.Overhang = Math.Max(0f, body.FrontAhead - body.Wheelbase);
            line.FirstPointFrom(0f);
            driver.PlannedSpeedOnward(0f);
        }

        public bool BackingOff => backingFrom >= 0 || laneBackFrom >= 0 || standOffBacking;

        public Box BodyAt(float rearX, float rearZ, float heading)
        {
            float hx = (float)Math.Sin(heading), hz = (float)Math.Cos(heading);
            var middle = (body.FrontAhead - body.RearBehind) / 2f;
            return new Box(rearX + hx * middle, rearZ + hz * middle, heading, body.HalfWidth, body.Length / 2f);
        }

        public static Box BodyOf(in OtherCar c) => new Box(c.X, c.Z, c.Heading, c.Width / 2f, c.Length / 2f);

        public static Box TriggerOf(in OtherCar c)
        {
            float hx = (float)Math.Sin(c.Heading), hz = (float)Math.Cos(c.Heading);
            var ahead = c.Length / 2f + c.TriggerLength / 2f;
            return new Box(c.X + hx * ahead, c.Z + hz * ahead, c.Heading, c.Width / 2f, c.TriggerLength / 2f);
        }

        public TrafficDecision Decide(in TrafficSnapshot s, TrafficDecision d)
        {
            d.Clear();
            if (line.Line.Points.Count < 2)
            {
                d.StopAlong = 0f;
                d.MaxSpeed = 0f;
                return d;
            }
            standingGap = null;
            var along = line.Locate(s.RearX, s.RearZ, Math.Max(0f, driver.Along - 1f), 12f).Along;
            var front = along + body.FrontAhead;
            var speed = s.Speed;
            Rules.Step(s.Marks, front, along - body.RearBehind, speed, s.Time, d);

            var reachV = Math.Max(0f, speed) + FastAccel;
            var stopping = reachV * BrakeLatency + reachV * reachV / (2f * PursuitDriver.Decel) + body.Length + EndMargin + StopUncertainty;
            var sweepLength = Math.Min((MaxPoses - 2) * SweepStep, Math.Max(SweepMetres, stopping));
            var end = along + sweepLength;
            if (d.StopAlong is float stop) end = Math.Min(end, stop - body.FrontAhead + 1f);
            creeps.Clear();
            Foresee(s, along, Math.Min(end, line.Line.Length - body.FrontAhead), stopping);
            Sweep(s, along, end);

            present.Clear();
            for (var i = 0; i < s.Cars.Count; i++) present.Add(s.Cars[i].Id);
            // Whatever is remembered of a car goes when it is out of reading range: Gley reuses pooled indices, and a
            // new car under an old id must not inherit its standing time or its yield (review round 1, O nit).
            heldFor.RemoveWhere(absent);
            moving.RemoveWhere(absent);
            Forget(yieldMemory);
            Forget(standingSince);
            Forget(pace);
            Forget(approachStood);
            Forget(holds);
            Forget(steadySince);
            Forget(verdictLog);
            if (lastSpeed >= 0f && s.Time > lastSpeedTime)
            {
                var dt = s.Time - lastSpeedTime;
                rideDecel += ((lastSpeed - s.Speed) / dt - rideDecel) * Math.Min(1f, dt / BrakingSmoothing);
            }
            lastSpeed = s.Speed;
            lastSpeedTime = s.Time;
            braking = rideDecel > BrakingDecel && s.Speed > 0.3f;
            approachBuilt.Clear();
            poolUsed = 0;
            (crossingWas, crossingNow) = (crossingNow, crossingWas);
            crossingNow.Clear();
            nosedIn.Clear();
            heldNow.Clear();
            holdsApplied = null;
            yieldWithoutHolds = null;
            now = s.Time;
            for (var i = 0; i < s.Cars.Count; i++)
            {
                var c = s.Cars[i];
                if (pace.TryGetValue(c.Id, out var was) && s.Time - was.Time > 0.05f)
                {
                    var gain = (c.Speed - was.Speed) / (s.Time - was.Time);
                    pace[c.Id] = (c.Speed, s.Time, was.Gain + (gain - was.Gain) * Math.Min(1f, (s.Time - was.Time) / 0.5f));
                }
                else if (!pace.ContainsKey(c.Id)) pace[c.Id] = (c.Speed, s.Time, 0f);
                if (pace.TryGetValue(c.Id, out var paceNow) && Math.Abs(paceNow.Gain) < SteadyGain && c.Speed > MovingMs) { if (!steadySince.ContainsKey(c.Id)) steadySince[c.Id] = s.Time; }
                else steadySince.Remove(c.Id);
                if (moving.Contains(c.Id) ? c.Speed < StandingMs : c.Speed <= MovingMs)
                {
                    moving.Remove(c.Id);
                    if (!standingSince.ContainsKey(c.Id)) standingSince[c.Id] = s.Time;
                }
                else { moving.Add(c.Id); standingSince.Remove(c.Id); }
            }

            var backCandidate = -1;
            var standing = Math.Abs(speed) < 0.3f;
            var cars = s.Cars;
            for (var i = 0; i < cars.Count; i++)
            {
                var c = cars[i];
                if (moving.Contains(c.Id)) { heldFor.Remove(c.Id); continue; }
                if (c.StoppedForUs) heldFor.Add(c.Id);
                var other = BodyOf(c);
                var touch = FirstTouch(other, GroundMargin, 0);
                if (!heldFor.Contains(c.Id))
                {
                    if (ComingIn(c, s, along, out var inLane, out var inAlong) && inAlong > along)
                    {
                        var at = FirstTouch(inLane, GroundMargin, 0);
                        if (at >= 0) Gap(d, at, along, c.Id);
                    }
                    if (touch < 0 || !Ahead(c, touch)) continue;
                    var body0 = FirstTouch(other, SqueezeMargin, 0);
                    if (body0 >= 0)
                    {
                        var onward = Math.Max(0f, c.Vx * poses[body0].Sin + c.Vz * poses[body0].Cos);
                        Gap(d, body0, along, c.Id, onward < LeaderOffMs, onward);
                    }
                    if (body0 < 0 || body0 > touch)
                    {
                        var last = touch;
                        while (last + 1 < count && poses[last + 1].Grown(GroundMargin, 0f).Overlaps(other)) last++;
                        Creep(touch, last, SqueezeMs);
                    }
                    continue;
                }
                var trigger = TriggerOf(c);
                if (standing && touch >= 0 && rearAt[touch] - along <= NoseRoom && Ahead(c, touch))
                {
                    Gap(d, touch, along, c.Id);
                    if (backCandidate < 0) backCandidate = c.Id;
                    continue;
                }
                var inZone = poses[0].Grown(GroundMargin, 0f).Overlaps(other.Grown(0.3f, 0.3f)) || poses[0].Grown(GroundMargin, 0f).Overlaps(trigger.Grown(0.3f, 0.3f));
                if (!inZone)
                {
                    var zoneTouch = FirstZoneTouch(other, trigger, 0.3f, 0);
                    if (zoneTouch >= 0) { Gap(d, zoneTouch, along, c.Id); continue; }
                    var o = other.Grown(PassingNear, PassingNear);
                    var tr = trigger.Grown(PassingNear, PassingNear);
                    var near = -1;
                    for (var k = 0; k < count && near < 0; k++) if (poses[k].Overlaps(o) || poses[k].Overlaps(tr)) near = k;
                    if (near >= 0)
                    {
                        var last = near;
                        while (last + 1 < count && (poses[last + 1].Overlaps(o) || poses[last + 1].Overlaps(tr))) last++;
                        Creep(near, last, CreepMs);
                    }
                    continue;
                }
                var exit = FirstOutOfZone(other, trigger);
                if (touch >= 0 && (exit < 0 || touch < exit))
                {
                    Gap(d, touch, along, c.Id);
                    if (standing && backCandidate < 0) backCandidate = c.Id;
                }
                else Creep(0, exit < 0 ? count - 1 : Math.Max(0, exit - 1), CreepMs);
            }
            Back(s, d, backCandidate);
            if (!d.BackOff) LaneBack(s, d, standing);
            if (!d.BackOff) StandOff(s, d, standing, along);
            if (d.BackOff)
            {
                if (whyKind != 7 || whyId != d.BackOffFrom) { whyKind = 7; whyId = d.BackOffFrom; whyText = $"backing off from {d.BackOffFrom}"; }
                d.Why = whyText;
                return d;
            }
            if (creeps.Count > 0)
            {
                var now = CapAt(along);
                if (now < 100f) d.MaxSpeed = now;
                Retime(s);
            }

            headway = Headway + (Math.Abs(speed) < 0.5f ? StartHeadway : 0f);
            var horizon = Math.Min(MaxHorizon, late[count - 1] + headway);
            approachHorizon = horizon;
            sweepMinX = sweepMinZ = float.MaxValue;
            sweepMaxX = sweepMaxZ = float.MinValue;
            for (var k = 0; k < count; k++)
            {
                Extend(poses[k].Grown(SideMargin + grow[k], EndMargin));
                if (k > 0) Extend(mids[k].Grown(SideMargin + grow[k], EndMargin));
                if (altOn[k]) Extend(alt[k].Grown(SideMargin + grow[k], EndMargin));
                if (alt2On[k]) Extend(alt2[k].Grown(SideMargin + grow[k], EndMargin));
            }
            for (var k = 0; k < count; k++) keepClearFrom[k] = -1;
            followed.Clear();
            for (var i = 0; i < cars.Count; i++)
            {
                var c = cars[i];
                if (!moving.Contains(c.Id) || heldFor.Contains(c.Id)) continue;
                var touch = FirstTouch(BodyOf(c), GroundMargin, 0);
                if (touch >= 0 && Ahead(c, touch) && !crossingWas.Contains(c.Id))
                {
                    Gap(d, touch, along, c.Id, false, Math.Max(0f, c.Vx * poses[touch].Sin + c.Vz * poses[touch].Cos));
                    if (RearAhead(c, poses[touch])) followed.Add(c.Id);
                }
                else if (ComingIn(c, s, along, out var inLane, out var inAlong) && inAlong > along)
                {
                    var at = FirstTouch(inLane, GroundMargin, 0);
                    if (at >= 0) Gap(d, at, along, c.Id, false, Math.Max(0f, c.Vx * poses[at].Sin + c.Vz * poses[at].Cos));
                }
            }
            holdFront = Holds(d, s, front);
            SafeTimes(holdFront);
            deferred.Clear();
            approachCars.Clear();
            for (var i = 0; i < cars.Count; i++)
            {
                var c = cars[i];
                if (!moving.Contains(c.Id) || heldFor.Contains(c.Id)) continue;
                if (OutOfReach(c, horizon) || !BuildCorridor(c, s, horizon))
                {
                    approachCars.Add(i);
                    continue;
                }
                deferNow = false;
                nosedInFor = -1;
                var give = Conflict(c, front, i);
                if (deferNow)
                {
                    if (holds.TryGetValue(c.Id, out var kept) && HardStopFront(front) <= kept.Way) approachCars.Add(i);
                    else holds.Remove(c.Id);
                    continue;
                }
                if (nosedInFor == c.Id)
                {
                    nosedIn.Add(c.Id);
                    holds.Remove(c.Id);
                }
                else
                {
                    approachCars.Add(i);
                    if (give is float g) Hold(c.Id, g, false);
                }
                if (nosedInFor == c.Id) yieldMemory.Remove(c.Id);
                Give(d, c, give, s.Time, front);
            }
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            for (var k = 0; k < count && rearAt[k] + body.FrontAhead <= holdFront + SweepStep; k++)
            {
                var side = SideMargin + grow[k];
                Within(poses[k].Grown(side, EndMargin), ref minX, ref minZ, ref maxX, ref maxZ);
                if (k > 0) Within(mids[k].Grown(side, EndMargin), ref minX, ref minZ, ref maxX, ref maxZ);
                if (altOn[k]) Within(alt[k].Grown(side, EndMargin), ref minX, ref minZ, ref maxX, ref maxZ);
                if (alt2On[k]) Within(alt2[k].Grown(side, EndMargin), ref minX, ref minZ, ref maxX, ref maxZ);
            }
            for (var i = 0; i < cars.Count && minX <= maxX; i++)
            {
                var c = cars[i];
                if (!moving.Contains(c.Id) || heldFor.Contains(c.Id)) continue;
                corridor.Build(graph, c, s.Flags, 0f, KeepClearReach, roadHeld, SpeedingUp(c.Id), LeaderOf(c, cars), Cruising(c.Id));
                if (corridor.MaxX >= minX && corridor.MinX <= maxX && corridor.MaxZ >= minZ && corridor.MinZ <= maxZ) { corridor.Clip(minX, minZ, maxX, maxZ); MarkKeepClear(c, true); }
            }
            for (var i = 0; i < cars.Count && minX <= maxX; i++)
            {
                var c = cars[i];
                if (!moving.Contains(c.Id) || heldFor.Contains(c.Id) || !OutOfReach(c, horizon) || OutOfReach(c, MaxHorizon)) continue;
                corridor.Build(graph, c, s.Flags, MaxHorizon, SweepMetres, roadHeld, SpeedingUp(c.Id), LeaderOf(c, cars), Cruising(c.Id));
                if (corridor.MaxX >= minX && corridor.MinX <= maxX && corridor.MaxZ >= minZ && corridor.MinZ <= maxZ) { corridor.Clip(minX, minZ, maxX, maxZ); MarkKeepClear(c, false); }
            }
            for (var i = 0; i < cars.Count; i++)
            {
                var c = cars[i];
                if (moving.Contains(c.Id) || heldFor.Contains(c.Id)) continue;
                if (standingSince.TryGetValue(c.Id, out var since) && s.Time - since > StuckSeconds) continue;
                approachCars.Add(i);
            }
            if (noseIn)
            {
                d.NoseIn = true;
                var now = CapAt(along);
                if (now < 100f && (d.MaxSpeed == null || now < d.MaxSpeed)) d.MaxSpeed = now;
                noseIn = false;
            }
            KeepClear(d, front);
            var settled = false;
            for (var round = 0; round < PassRoundsLimit && !settled; round++)
            {
                var before = d.YieldAlong;
                Committed(d, s, front, horizon);
                KeepClear(d, front);
                Approaches(d, s, front);
                KeepClear(d, front);
                d.PassRounds = round + 1;
                settled = d.YieldAlong == before;
            }
            // Still tightening after the last round (review round 7, BB4): judged once more on the trajectory as it ends;
            // still not settled, the result is never driven as it is but replaced by the emergency rule's (review
            // round 8, CC2: Fallback).
            if (!settled)
            {
                var before = d.YieldAlong;
                Committed(d, s, front, horizon);
                KeepClear(d, front);
                if (d.YieldAlong != before)
                {
                    d.Unsettled = true;
                    Fallback(d, s, front, horizon);
                }
            }
            Stuck(s, d, standing);

            var kind = 0;
            var id = -1;
            var nearest = float.MaxValue;
            if (d.StopAlong is float sa && sa < nearest) { nearest = sa; kind = d.StopIsRed ? 1 : 2; }
            if (d.YieldAlong is float ya && ya < nearest) { nearest = ya; kind = 3; id = d.YieldFor; }
            if (d.GapAhead is float ga && front + Math.Max(0f, ga - PursuitDriver.StopGap) < nearest) { nearest = front + Math.Max(0f, ga - PursuitDriver.StopGap); kind = 4; id = d.GapFor; }
            if (nearest - front >= 60f) kind = 0;
            if (kind == 0 && d.MaxSpeed != null) kind = 5;
            if (kind == 3 && d.ApproachFor >= 0 && d.YieldFor == d.ApproachFor) kind = 8;
            if (d.BlockedBy >= 0) { kind = 6; id = d.BlockedBy; }
            if (kind != whyKind || id != whyId)
            {
                whyKind = kind;
                whyId = id;
                whyText = kind switch
                {
                    1 => "stop line", 2 => "give-way line", 3 => $"giving way to {id}", 4 => $"behind {id}", 5 => "creeping", 6 => "blocked: " + d.BlockedWhy, 8 => $"keeping the option to stop short of {id}'s way", _ => "",
                };
            }
            d.Why = whyText;
            return d;
        }

        private enum Go { On, Stop, StopShort }

        private readonly Box[] mids = new Box[MaxPoses];

        public CarModel? Model { get; set; }
        private PursuitDriver? foreseer;
        private float[] goX = new float[MaxPoses], goZ = new float[MaxPoses], goH = new float[MaxPoses];
        private bool[] goTurning = new bool[MaxPoses];
        private float[] brakeX = new float[MaxPoses], brakeZ = new float[MaxPoses], brakeH = new float[MaxPoses];
        private float[] keepX = new float[MaxPoses], keepZ = new float[MaxPoses], keepH = new float[MaxPoses];
        private int goCell, goCount, brakeCell, brakeCount, keepCell, keepCount;
        private enum Pace { Go, Brake, Keep }
        public const float ForeseeEvery = 0.2f, ForeseeSeconds = 8f, ForeseeStep = 0.02f;
        public const int ForeseeBudget = 40;

        private bool running;
        private Pace runPace, next = Pace.Go;
        private float runEnd, runNear, runKeep, runT, runPrevAlong, runPx, runPz, runPh, runFrom;
        private int runFirst, runN;
        private bool runSettled;
        private float[] runX = new float[MaxPoses], runZ = new float[MaxPoses], runH = new float[MaxPoses];
        private bool[] runTurning = new bool[MaxPoses];
        private float goStarted = float.MinValue, foreseenTo;

        private bool Ahead(in OtherCar c, int k) => Before(c, poses[0]) || (k > 0 && Before(c, poses[k]));

        private static bool Before(in OtherCar c, in Box pose) => (c.X - pose.X) * pose.Sin + (c.Z - pose.Z) * pose.Cos > 0f;

        private static bool RearAhead(in OtherCar c, in Box pose)
        {
            float rx = c.X - (float)Math.Sin(c.Heading) * c.Length / 2f, rz = c.Z - (float)Math.Cos(c.Heading) * c.Length / 2f;
            return (rx - pose.X) * pose.Sin + (rz - pose.Z) * pose.Cos > 0f;
        }

        private int FirstTouch(in Box other, float margin, int from)
        {
            for (var k = from; k < count; k++) if (poses[k].Grown(margin, 0f).Overlaps(other)) return k;
            return -1;
        }

        private int FirstZoneTouch(in Box other, in Box trigger, float margin, int from)
        {
            var o = other.Grown(margin, margin);
            var t = trigger.Grown(margin, margin);
            for (var k = from; k < count; k++)
            {
                var p = poses[k].Grown(GroundMargin, 0f);
                if (p.Overlaps(o) || p.Overlaps(t)) return k;
            }
            return -1;
        }

        private int FirstOutOfZone(in Box other, in Box trigger)
        {
            var o = other.Grown(BackOffClear, BackOffClear);
            var t = trigger.Grown(BackOffClear, BackOffClear);
            for (var k = 1; k < count; k++) if (!poses[k].Overlaps(o) && !poses[k].Overlaps(t)) return k;
            return -1;
        }

        private void Creep(int from, int last, float ms) => creeps.Add((rearAt[from], rearAt[last] + SweepStep, ms));

        private float CapAt(float at)
        {
            var cap = float.MaxValue;
            foreach (var (from, to, ms) in creeps)
            {
                if (at > to) continue;
                cap = Math.Min(cap, at >= from ? ms : PursuitDriver.Approach(ms, from - at));
            }
            return cap;
        }

        // A traffic car whose target waypoint is on the ride car's way, not behind its rear, but that is not in its lane
        // yet: Gley changes lanes and merges without looking for the ride car, so a careful driver treats it as coming
        // in already: a car of its size on the ride car's line, where it is along it (`box`, `alongAt`). Review round
        // 9: six of the eight contacts in two T3 runs on 0.1.80 were cars moving sideways into the ride car's lane,
        // their target in it 1 to 10 s before (the ride car queued beside their rear, or stood as they came in).
        private bool ComingIn(in OtherCar c, in TrafficSnapshot s, float along, out Box box, out float alongAt)
        {
            box = default;
            alongAt = 0f;
            if (c.Target < 0 || s.Marks == null) return false;
            var on = false;
            for (var i = 0; i < s.Marks.Count && !on; i++) on = s.Marks[i].Waypoint == c.Target && s.Marks[i].Along > along - body.RearBehind;
            if (!on) return false;
            var at = line.Locate(c.X, c.Z, Math.Max(0f, along - SweepMetres), 2f * SweepMetres);
            if (Math.Abs(at.Lateral) < body.HalfWidth) return false;
            Vec3 p = line.Line.At(at.Along), q = line.Line.At(at.Along + 0.5f);
            box = new Box(p.X, p.Z, (float)Math.Atan2(q.X - p.X, q.Z - p.Z), c.Width / 2f, c.Length / 2f);
            alongAt = at.Along;
            return true;
        }

        private void Gap(TrafficDecision d, int pose, float along, int id, bool standing = true, float onwardMs = 0f)
        {
            var gap = pose == 0 ? 0f : rearAt[pose - 1] - along + SweepStep / 2f;
            if (d.GapAhead == null || gap < d.GapAhead) { d.GapAhead = gap; d.GapFor = id; d.GapSpeed = standing ? 0f : onwardMs; }
            if (standing && (standingGap == null || gap < standingGap)) standingGap = gap;
        }

        public const float StandOffSeconds = 2f, StandOffClear = 0.5f, StandOffRoom = 10f, StandOffHold = 10f, StandOffMoved = 1f;
        private int standOffFrom = -1;
        private float standOffX, standOffZ, standOffSince, standOffWaited, rideStoodSince = -1f;
        private bool standOffBacking;
        private readonly Dictionary<int, (float X, float Z)> standOffGiven = new Dictionary<int, (float, float)>();

        private int laneBackFrom = -1;
        private float laneBackX, laneBackZ;

        public const float LinedUp = 0.966f;

        public const float LeaderOffMs = PursuitDriver.LeaderOffMs;
        private readonly Dictionary<int, float> steadySince = new Dictionary<int, float>();
        private const float SteadyGain = 0.3f;
        private const float CruisingSeconds = 2f;

        public const float StopsForUsMargin = 0.5f;

        private HashSet<int> crossingWas = new HashSet<int>(), crossingNow = new HashSet<int>();

        private float clearMargin;
        private bool clearsOutside;

        private const float HeadOnLookOn = 30f;

        private readonly List<(int Index, int Id, (float Way, float Since, bool Cautious) Hold)> released = new List<(int, int, (float, float, bool))>();


        private float approachHorizon;
        private ILiveFlags approachFlags = null!;
        private IReadOnlyList<OtherCar> approachCarsList = Array.Empty<OtherCar>();
        private readonly List<int> approachCars = new List<int>();
        private readonly Dictionary<int, (float Front, float Time)> approachStood = new Dictionary<int, (float, float)>();
        private const float ApproachStoodSeconds = 10f;

        private const float ApproachSeconds = 6f;

        private readonly Dictionary<int, Corridor> approachBuilt = new Dictionary<int, Corridor>();
        private readonly List<Corridor> corridorPool = new List<Corridor>();
        private int poolUsed;

        private bool braking;
        private float rideDecel, lastSpeed = -1f, lastSpeedTime;
        public const float BrakingDecel = 1f;
        private const float BrakingSmoothing = 0.1f;

        private bool emergencyStop;

        private enum Verdict { None, Passed, Past, Clear, Outside, Late, Stop }
        private Verdict verdict;
        private readonly Dictionary<int, (float Way, float Since, bool Cautious)> holds = new Dictionary<int, (float, float, bool)>();
        private readonly HashSet<int> judged = new HashSet<int>(), fresh = new HashSet<int>(), nosedIn = new HashSet<int>(), heldNow = new HashSet<int>();
        private float? holdsApplied, yieldWithoutHolds;
        public const float ReleaseMargin = BrakeLatency + LaunchDelay;
        private readonly float[] releaseLate = new float[MaxPoses];

        private float speedCap = float.MaxValue;
        private float now;
        private readonly Dictionary<int, ulong> verdictLog = new Dictionary<int, ulong>();

    }
}
