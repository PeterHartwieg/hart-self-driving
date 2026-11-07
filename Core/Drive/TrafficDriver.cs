using System;
using System.Collections.Generic;
using DrivenRides.Core.Routing;

namespace DrivenRides.Core.Drive
{
    // The ride car's traffic decisions, layers 1 to 3 of docs/implementation-plan.md ("The four layers"), from one
    // snapshot per physics step. Its answers go into PursuitDriver's input (StopAlong, YieldAlong, GapAhead,
    // MaxSpeed); steering and the speed plan stay the driver's.
    //
    // The ride car's body (not its line) is swept along the next SweepMetres of its line, at the speeds the driver
    // plans, speeding up from now as fast as the car can and as slowly as it might: each pose has a window of time in
    // which the body may be there. Then:
    // - a traffic car standing still is ground, not traffic: the first pose that touches its body is a gap ahead
    //   (where only the margin touches, the ride car squeezes past at a crawl);
    // - a moving traffic car has a corridor (Corridor); the first pose where the body enters it while the car may be
    //   there, give or take Headway, is a conflict. The ride car gives way short of where its body first enters that
    //   corridor (in a U-turn, well before its line crosses the other lanes: NWH spike, the U-turn into an oncoming
    //   car). A car it would only catch up with from behind is followed instead (the gap), and one following it is
    //   not its business; one behind in a lane it joins must be left FollowBrake to stay behind it;
    // - a stretch against a car's way that runs on past the sweep (head-on in one lane) is a conflict whenever the
    //   car is coming;
    // - already inside the corridor, it drives on if its body is out before the other car can arrive, and stops if
    //   not;
    // - keep clear: wherever it will come to a stop, its body must not stand in a moving car's way (KeepClear);
    // - layer 3, one priority rule: a car standing because of the ride car (it has the ride car's collider in its
    //   obstacleList) is no conflict. Its body and its front trigger are ground: the ride car stops short of them,
    //   creeps past where it is near them, and, standing nose to nose with it inside its trigger, backs off along
    //   its line until out of the trigger, so the other car can go (NWH spike: both stopped for good, 0.3 m apart).
    //   The ride car keeps treating such a car this way until it moves, so it doesn't drive straight back in.
    public sealed partial class TrafficDriver
    {
        public const float SweepMetres = 40f;
        // The adapter passes every traffic car this near the rear axle. Head-on in one lane, the ride car must see
        // the other car before it is a braking distance from the narrow part, while the other car is still the
        // narrow part's length and a few seconds away (T0 headon: 60 m and 120 m saw it too late).
        public const float SeeMetres = 150f;
        public const float SweepStep = 1f;
        // The furthest ahead in time a car's way is looked at.
        private const float MaxHorizon = 12f;
        // How far along an oncoming car's way the keep-clear looks: as far as a corridor goes (Corridor's own limit).
        private const float KeepClearReach = 160f;
        // The final pass's rounds at most (Decide): two settle every T0 run (round 9; eight before).
        public const int MaxPassRounds = 2;
        // As used: MaxPassRounds, lower only in tests that make the pass run out (CC2).
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
        // Time either side of the ride car's own passage that a traffic car must keep clear of it; pulling away from a
        // standstill takes a gap this much longer, so a car that has just set off doesn't stop again for the same
        // traffic a metre on (T3 20261013-185723, ride 8). The margins are in TrafficMargins.
        public const float Headway = TrafficMargins.Headway;
        public const float StartHeadway = TrafficMargins.StartHeadway;
        // Round the ride car's body in the corridor sweep: sideways and lengthways.
        public const float SideMargin = TrafficMargins.SideMargin;
        public const float EndMargin = TrafficMargins.EndMargin;
        // Round the body for ground (standing cars), and the least room to squeeze past one at a crawl.
        public const float GroundMargin = 0.4f;
        public const float SqueezeMargin = 0.1f;
        // A traffic car counts as moving above MovingMs and as standing again below StandingMs: a car inching at a
        // lane end flipped between ground and traffic every half second (T3 20261013-191420, ride 1).
        public const float StandingMs = 0.4f;
        public const float MovingMs = 0.8f;
        // A traffic car standing nose to nose this near (metres the front can still move), with the ride car in its
        // trigger: back off, whatever the angle.
        public const float NoseRoom = 2f;
        // A standing traffic car in the way that won't move: one at the end of a lane that leads nowhere (Gley's cars
        // wait there for good, T3 20261013-191420, ride 1: 300 s), or any that hasn't moved for StuckSeconds.
        public const float StuckSeconds = 60f;
        // How long a car given way to stays a conflict once the sweep stops finding one, while it is still in the way.
        public const float ConflictMemory = TrafficMargins.ConflictMemory;
        // A traffic car behind the ride car in a lane it joins: the braking it is left to stay behind, and the gap.
        public const float FollowBrake = TrafficMargins.JoinBrake;
        public const float FollowGap = TrafficMargins.JoinGap;
        // Past a car that waits for the ride car (in or beside its trigger), and squeezing past a standing car with
        // only the margin touching. The speeds are gameplay choices alongside the gap margins.
        public const float CreepMs = TrafficMargins.CreepMs;
        // A traffic car moving at most this fast stops for the ride car in its trigger: the ride car noses in.
        public const float NoseInMs = 5f;
        public const float SqueezeMs = TrafficMargins.SqueezeMs;
        // Within this of a waiting car's body or trigger, the ride car is passing it.
        public const float PassingNear = 0.5f;
        // The ride car speeding up from now, at most (CarModel.Accel) and at least, m/s².
        public const float FastAccel = 3f;
        public const float SlowAccel = 2f;
        // Backing off: clear of the other car's trigger by this much, at most this far, with this much room behind.
        public const float BackOffClear = 0.8f;
        public const float MaxBackOff = 6f;
        // At least this far, so the other car has room to go round the ride car's nose whatever the angle.
        public const float MinBackOff = 1.5f;
        public const float RoomBehind = 0.8f;
        public const float BackOffMs = 1.2f;
        // How far a Gley car runs on once something is in its front trigger: StopInDistance's action value
        // (DrivingAI.GetActionValue), over which DriveJob takes its target speed down to 0 and then StopNow, and its
        // speed follows the target each physics step (ForceMode.VelocityChange). What a car is counted on for when the
        // ride car's body is in its way (StaysBehind, StopSafe; review round 3, V2).
        public const float TriggerStopMetres = 1.25f;
        // Before that, from the trigger's contact to Gley's new action, about this long (its events and the driving
        // AI's next update); the T0 simulation's cars take 0.05 to 0.15 s. The quick stop is counted on only past the
        // point of no return (the emergency rule); before it, the cautious judgement takes TriggerReactCautious and a
        // car's own brakes, TriggerBrake (review round 4, X3).
        public const float TriggerReact = 0.1f;
        public const float TriggerReactCautious = 0.2f;
        public const float TriggerBrake = 8f;
        // Stopping short of a car's way takes, besides the braking at PursuitDriver.Decel: the brakes' latency (the
        // rig's traces: 0.10 s to lose 0.3 m/s at the median, 0.14 s at the 99th percentile of 79 brakings from over
        // 4 m/s) and the model's uncertainty where the body stands (0.43 m measured; review round 4, X4).
        public const float BrakeLatency = 0.15f;
        public const float StopUncertainty = 0.43f;
        // A standing ride car moves off this long after the throttle (the rig's -nwh.csv traces: the limo 0.4 s to
        // 1 m/s in 38 launches); then it speeds up at SlowAccel at least (the traces' full-throttle median is 2.1 to
        // 5.8 m/s² by speed and car).
        public const float LaunchDelay = 0.4f;

        private readonly LaneGraph graph;
        private readonly DriveLine line;
        private readonly PursuitDriver driver;
        private readonly RideBody body;
        // The corridor being judged: the shared one, or one the cautious judgement built this decision (Approach).
        private Corridor corridor;
        private readonly Corridor mainCorridor = new Corridor();
        private readonly HashSet<int> heldFor = new HashSet<int>();
        private readonly HashSet<int> present = new HashSet<int>();
        private readonly HashSet<int> moving = new HashSet<int>();
        private readonly Dictionary<int, float> standingSince = new Dictionary<int, float>();
        // Each car's speed a moment ago and how fast it has been gaining speed (m/s², smoothed): a car pulling away
        // from a light (T0 pullaway) passed for one keeping its pace.
        private readonly Dictionary<int, (float Speed, float Time, float Gain)> pace = new Dictionary<int, (float, float, float)>();
        // The cars the ride car gave way to lately: where, and when it last found the conflict.
        private readonly Dictionary<int, (float Along, float Time)> yieldMemory = new Dictionary<int, (float, float)>();
        private int backingFrom = -1;
        private float backStartX, backStartZ;

        private const int MaxPoses = 64;
        private readonly Box[] poses = new Box[MaxPoses];
        // Per pose: the body braking hard from now instead (the near field's other candidate; alt[k] valid while
        // altOn[k]), and how much it is grown by for the model's remaining error.
        private readonly Box[] alt = new Box[MaxPoses], alt2 = new Box[MaxPoses];
        private readonly bool[] altOn = new bool[MaxPoses], alt2On = new bool[MaxPoses];
        private readonly float[] grow = new float[MaxPoses];
        private readonly float[] planned = new float[MaxPoses], rearAt = new float[MaxPoses], early = new float[MaxPoses], late = new float[MaxPoses], slowSpeed = new float[MaxPoses], fastSpeed = new float[MaxPoses];
        // The slow times and speeds again, under the stops the car already has (SafeTimes): a pose past one is never
        // reached (infinite time). What committing past a car's way counts on (review round 2, U1).
        private readonly float[] safeLate = new float[MaxPoses], safeSpeed = new float[MaxPoses];
        // The fast times and speeds under the same stops: the most the car can do, for when stopping is no better.

        // Where the front must stop at the latest, for the stops known (Holds), and that SafeTimes was worked out for.
        private float holdFront, safeFor, leaderCap = float.MaxValue;
        // Conflicts found too late to stop short of: decided once every other car's yield is known (Committed).
        private readonly List<(int Car, int RunStart, int Exit, float RunFrom, YieldNote Note)> deferred = new List<(int, int, int, float, YieldNote)>();
        private readonly List<Go> deferredGo = new List<Go>();
        private readonly List<float> deferredMargin = new List<float>();
        // Moving cars the ride car comes up behind on its way (their rear ahead of its front where it meets them):
        // followed at their speed (Gap), never given way to as if they were coming from behind.
        private readonly HashSet<int> followed = new HashSet<int>();
        private bool deferNow;
        private int nosedInFor = -1;
        // Per pose: the earliest pose where the body enters a moving car's way that this pose is in, or -1.
        private readonly float[] headings = new float[MaxPoses];
        private readonly int[] keepClearFrom = new int[MaxPoses], keepClearFor = new int[MaxPoses];
        private int count;
        private YieldNote conflictNote;
        private float headway = Headway;
        private int whyKind, whyId = -1;
        private string whyText = "";
        private bool noseIn;
        // The nearest gap to a standing car: where the ride car will come to a stop behind it.
        private float? standingGap;
        // Stretches of the line (rear-axle metres along) driven at a creep, and their speed.
        private readonly List<(float From, float To, float Ms)> creeps = new List<(float, float, float)>();
        // The area the sweep covers, with the margins.
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
            // Made up front, not in a decision: enough for the cars a decision judges cautiously, as a rule.
            for (var i = 0; i < 24; i++) corridorPool.Add(new Corridor());
            this.graph = graph;
            this.line = line;
            this.driver = driver;
            this.body = body;
            roadHeld = Rules.RoadHeld;
            Rules.Overhang = Math.Max(0f, body.FrontAhead - body.Wheelbase);
            // The line's lengths and the plan's onward speeds are worked out on first use: here, at the ride's start,
            // not in its first physics step (review round 11).
            line.FirstPointFrom(0f);
            driver.PlannedSpeedOnward(0f);
        }

        public bool BackingOff => backingFrom >= 0 || laneBackFrom >= 0 || standOffBacking;

        // The ride car's body at a rear-axle position and heading.
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
            // A valid zero-distance route can contain just its drop-off waypoint (or coincident points that Build
            // coalesces). There is no segment to sweep or drive; hold the car so the adapter can finish the ride.
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

            // The sweep ends where the car must stop anyway, or at the end of the line.
            // At least as far as the car needs to stop from the speed it can have in a second, with the body and its
            // margins past that: a conflict first seen inside the stopping distance is found too late however it is
            // judged (review round 8: T0 merge at 14 m/s, the stopping distance at Decel as long as the 40 m sweep,
            // the merge point was seen only past the point of no return).
            var reachV = Math.Max(0f, speed) + FastAccel;
            var stopping = reachV * BrakeLatency + reachV * reachV / (2f * PursuitDriver.Decel) + body.Length + EndMargin + StopUncertainty;
            var sweepLength = Math.Min((MaxPoses - 2) * SweepStep, Math.Max(SweepMetres, stopping));
            // Past the end of the line it drives, straight on along its last stretch: the way goes on there (the next
            // step, the next leg) and what stands just past the end must be seen in time (review round 9: T3 ride 17,
            // twice at 11 to 12 m/s into a car standing 2 m past the end of the leg's line, seen 0.2 s before).
            var end = along + sweepLength;
            if (d.StopAlong is float stop) end = Math.Min(end, stop - body.FrontAhead + 1f);
            creeps.Clear();
            // The near field (that stopping distance) where the model foresees the body (review round 9, DD1).
            Foresee(s, along, Math.Min(end, line.Line.Length - body.FrontAhead), stopping);
            Sweep(s, along, end);

            // Layer 3 first: cars standing for the ride car, ground, backing off.
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
                    // One about to come into the ride car's lane: kept behind where it will be in it.
                    if (ComingIn(c, s, along, out var inLane, out var inAlong) && inAlong > along)
                    {
                        var at = FirstTouch(inLane, GroundMargin, 0);
                        if (at >= 0) Gap(d, at, along, c.Id);
                    }
                    if (touch < 0 || !Ahead(c, touch)) continue;
                    // Only the margin touches: squeeze past at a crawl (a long car finishing a turn beside a car
                    // waiting at its line; T0 priority). Where the body itself would touch, stop short.
                    var body0 = FirstTouch(other, SqueezeMargin, 0);
                    // A car ahead setting off (still under MovingMs) is followed at the pace it has along the ride car's
                    // way from LeaderOffMs, so the ride car sets off with it as a driver in a queue does (Peter, 14
                    // October: "it waits for the car in front to get to a safe distance instead of accelerating with
                    // them").
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
                // It waits for the ride car: its trigger is ground too. Nose to nose with it, whatever the angle: back
                // off out of its trigger, then wait for it to go.
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
                    // Passing beside it: at a creep while the body is within PassingNear of it, no further.
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
                    // Nose to nose inside its trigger: neither can go.
                    Gap(d, touch, along, c.Id);
                    if (standing && backCandidate < 0) backCandidate = c.Id;
                }
                // Through its trigger, out before its body: at a creep until out of it, then on.
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

            // Layer 2: moving cars.
            headway = Headway + (Math.Abs(speed) < 0.5f ? StartHeadway : 0f);
            var horizon = Math.Min(MaxHorizon, late[count - 1] + headway);
            approachHorizon = horizon;
            sweepMinX = sweepMinZ = float.MaxValue;
            sweepMaxX = sweepMaxZ = float.MinValue;
            for (var k = 0; k < count; k++)
            {
                // Every box CoversSwept checks, as it grows them.
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
                // A car ahead on the ride car's way, moving: followed at its speed along that way where the body
                // would meet it (PursuitDriver.Follow). Not one the ride car is going on in front of (StopsForUs): its
                // body is in the poses because its way crosses them, and braking for it there is what brings the two
                // together.
                if (touch >= 0 && Ahead(c, touch) && !crossingWas.Contains(c.Id))
                {
                    Gap(d, touch, along, c.Id, false, Math.Max(0f, c.Vx * poses[touch].Sin + c.Vz * poses[touch].Cos));
                    if (RearAhead(c, poses[touch])) followed.Add(c.Id);
                }
                // One coming into the ride car's lane ahead of it: followed where it will be in it.
                else if (ComingIn(c, s, along, out var inLane, out var inAlong) && inAlong > along)
                {
                    var at = FirstTouch(inLane, GroundMargin, 0);
                    if (at >= 0) Gap(d, at, along, c.Id, false, Math.Max(0f, c.Vx * poses[at].Sin + c.Vz * poses[at].Cos));
                }
            }
            // The firm stops (stop line, the planner's hold, standing cars, parked cars and people) and the car followed.
            holdFront = Holds(d, s, front);
            SafeTimes(holdFront);
            deferred.Clear();
            approachCars.Clear();
            for (var i = 0; i < cars.Count; i++)
            {
                var c = cars[i];
                if (!moving.Contains(c.Id) || heldFor.Contains(c.Id)) continue;
                // Out of the lenient judgement's reach, it is still judged cautiously (its own reach: the car may speed up
                // to the road's limit), so the clear-through prediction is kept before the stopping option goes (review
                // round 8, CC3: a car 85 m back on a merge, out of the lenient reach, was first judged when the ride car
                // could no longer stop short of its way, T0 merge 106); a car held for, until a verdict lifts the stop.
                if (OutOfReach(c, horizon) || !BuildCorridor(c, s, horizon))
                {
                    approachCars.Add(i);
                    continue;
                }
                deferNow = false;
                nosedInFor = -1;
                var give = Conflict(c, front, i);
                // Whatever the lenient judgement found (a yield further on, nothing), the cautious one keeps the
                // option to stop short of the first place the body comes into the car's way (review round 4, X4); in
                // the final pass, on the final trajectory. Not a car already too close to stop short of: that one the
                // commitment decides (Committed).
                if (deferNow)
                {
                    // Too close to stop short of: the emergency rule's (Committed), not a stop kept for it. Unless the
                    // cautious judgement keeps one for it that the car can still make: that stays, judged as before
                    // (review round 9: dropped here, it came back a moment later past where the car could make it, T0
                    // lights 55).
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
                    // Given way to: the stop is kept until going on is shown to be clear (Approaches).
                    if (give is float g) Hold(c.Id, g, false);
                }
                // Nosing in in front of it is a decision, not a different reading: the yield remembered from a moment
                // ago doesn't hold the ride car back (review round 2, U4).
                if (nosedInFor == c.Id) yieldMemory.Remove(c.Id);
                Give(d, c, give, s.Time, front);
            }
            // Where the ride car would come to a stand is kept clear of a car out of the lenient look ahead too, over the
            // longest one (review round 8: it had none, and the ride car stopped across its lane to wait for a car in
            // the next one, T0 crossing).
            // An oncoming car's way (head-on) is kept clear as far as it goes, not as far as the car gets in some time: a
            // boundary that grew with the look ahead crept towards the ride car as the car came, faster than the ride
            // car could brake for it, and it came to a stand inside a one-lane stretch (review round 8, T0 head-on).
            // Only the poses up to where the car stops are kept clear (MarkKeepClear): their area, and none if there are
            // none.
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
            // A car standing at its line (a red, a give-way) may set off now: judged cautiously too, the ride car keeps
            // the option to stop short of its way (a queue's first car at the light turning green found the ride car
            // too close to stop, review round 4, X4). Not one standing for the ride car (layer 3 is its) or stuck.
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
            // The final pass. Every constraint first (yields, keep-clear, creeps), then, on the trajectory they leave,
            // the commitments (conflicts found too late, and those remembered) and the cautious approach (stops short of
            // a car's way); whatever these add tightens the trajectory, so they are judged again, until nothing changes
            // (review round 3, V1; round 4, X1; round 5, Y1).
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

            // The reason given is the nearest of the stops ahead; the string is made only when it changes.
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

        // The poses from the car now (pose 0, as it stands) along the line, with the time window of each.
        // The body halfway between pose k - 1 and pose k (Sweep).
        private readonly Box[] mids = new Box[MaxPoses];

        // The ride car's model (wheelbase, lock, Ackermann, yaw factor, the wheel's rate): with it the near field is
        // swept where the car will really be, not on the planned line (review round 9, DD1: the body's heading lags the
        // line's into a turn, and its corner came into the way of a car stopping at its give-way line, found only at
        // the body's own pose). Without it, the planned line throughout.
        public CarModel? Model { get; set; }
        private PursuitDriver? foreseer;
        // Foreseen rear-axle poses at whole SweepStep cells along the line, from goCell: going on (as the driver drives
        // the plan, no stops), braking at HardDecel, and keeping the speed it has (as while a car ahead, a creep or a
        // cap holds it): the driver steers for the speed it has, so the three bracket the paths between. Whether the
        // car is still turning there (Feasibility's settled test).
        private float[] goX = new float[MaxPoses], goZ = new float[MaxPoses], goH = new float[MaxPoses];
        private bool[] goTurning = new bool[MaxPoses];
        private float[] brakeX = new float[MaxPoses], brakeZ = new float[MaxPoses], brakeH = new float[MaxPoses];
        private float[] keepX = new float[MaxPoses], keepZ = new float[MaxPoses], keepH = new float[MaxPoses];
        private int goCell, goCount, brakeCell, brakeCount, keepCell, keepCount;
        private enum Pace { Go, Brake, Keep }
        // Foreseen again every ForeseeEvery (the poses are at fixed cells along the line, so the last ones hold in
        // between), and at most ForeseeSeconds on.
        public const float ForeseeEvery = 0.2f, ForeseeSeconds = 8f, ForeseeStep = 0.02f;
        // The model's steps one decision takes at most. A foresight runs on over as many decisions as it needs, the
        // last finished one in use meanwhile: the one going on took up to 330 steps, most of a decision's time (review
        // round 10: 7.8 ms at the 99th percentile in game).
        public const int ForeseeBudget = 40;

        // The foresight being run: which path, from where, and how far it has got. Its poses go into the run buffers,
        // swapped with the path's own when it is done.
        private bool running;
        private Pace runPace, next = Pace.Go;
        private float runEnd, runNear, runKeep, runT, runPrevAlong, runPx, runPz, runPh, runFrom;
        private int runFirst, runN;
        private bool runSettled;
        private float[] runX = new float[MaxPoses], runZ = new float[MaxPoses], runH = new float[MaxPoses];
        private bool[] runTurning = new bool[MaxPoses];
        private float goStarted = float.MinValue, foreseenTo;

        // In the ride car's way where its body first touches the car (pose k): ahead of the middle of that pose,
        // seen along that pose's heading. A car behind the middle of every pose that touches it is not in the way.
        // On a U-turn the car standing in the lane turned into lies behind the ride car as it stands now, but ahead
        // of the pose that reaches it (review round 1, O6). The heading now still counts, so no car is dropped that
        // was ahead before.
        private bool Ahead(in OtherCar c, int k) => Before(c, poses[0]) || (k > 0 && Before(c, poses[k]));

        private static bool Before(in OtherCar c, in Box pose) => (c.X - pose.X) * pose.Sin + (c.Z - pose.Z) * pose.Cos > 0f;

        // The car's rear is ahead of the middle of the pose that first touches it, along that pose's heading: the body
        // comes up behind it, not beside it (an 11 m truck alongside has its rear beside or behind the body: ride 9).
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

        // A creep from pose `from` to pose `last` (loops at the callers, not a predicate: no garbage per step).
        private void Creep(int from, int last, float ms) => creeps.Add((rearAt[from], rearAt[last] + SweepStep, ms));

        // The speed allowed at `at` metres along: a creep inside a creep stretch, slowing to it before one.
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
            // In the lane already: its own body says so.
            if (Math.Abs(at.Lateral) < body.HalfWidth) return false;
            Vec3 p = line.Line.At(at.Along), q = line.Line.At(at.Along + 0.5f);
            box = new Box(p.X, p.Z, (float)Math.Atan2(q.X - p.X, q.Z - p.Z), c.Width / 2f, c.Length / 2f);
            alongAt = at.Along;
            return true;
        }

        // A gap ahead: how far the front can move before the pose at `pose` touches.
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

        // Lined up with a car's lane: within about 15 degrees of it.
        public const float LinedUp = 0.966f;

        public const float LeaderOffMs = PursuitDriver.LeaderOffMs;
        private readonly Dictionary<int, float> steadySince = new Dictionary<int, float>();
        private const float SteadyGain = 0.3f;
        private const float CruisingSeconds = 2f;

        public const float StopsForUsMargin = 0.5f;

        // The cars the ride car goes on in front of, last decision and this one (swapped, not copied: UnionWith boxes
        // its enumerator, garbage every physics step).
        private HashSet<int> crossingWas = new HashSet<int>(), crossingNow = new HashSet<int>();

        private float clearMargin;
        // The last CommittedClear's true was for staying outside the car's way, not clearing through it.
        private bool clearsOutside;

        private const float HeadOnLookOn = 30f;

        // The cars lifted in this round of the final pass (their index in the snapshot, id, the stop as kept), judged
        // again together (CC1).
        private readonly List<(int Index, int Id, (float Way, float Since, bool Cautious) Hold)> released = new List<(int, int, (float, float, bool))>();


        private float approachHorizon;
        private ILiveFlags approachFlags = null!;
        private IReadOnlyList<OtherCar> approachCarsList = Array.Empty<OtherCar>();
        // The cars judged cautiously this decision (moving ones with a corridor, and ones standing at their line).
        private readonly List<int> approachCars = new List<int>();
        // Where the front stood short of a car's way kept by Approach, and when.
        private readonly Dictionary<int, (float Front, float Time)> approachStood = new Dictionary<int, (float, float)>();
        private const float ApproachStoodSeconds = 10f;

        // How far ahead in time the cautious judgement looks for a car coming into the ride car's way.
        private const float ApproachSeconds = 6f;

        private readonly Dictionary<int, Corridor> approachBuilt = new Dictionary<int, Corridor>();
        private readonly List<Corridor> corridorPool = new List<Corridor>();
        private int poolUsed;

        // The ride car is slowing at more than BrakingDecel (smoothed over BrakingSmoothing): its brakes are on.
        private bool braking;
        private float rideDecel, lastSpeed = -1f, lastSpeedTime;
        public const float BrakingDecel = 1f;
        private const float BrakingSmoothing = 0.1f;

        // The last stop ApproachCautious gave is the emergency rule's: one the car can no longer make.
        private bool emergencyStop;

        // What the cautious judgement found for a car: nothing to keep yet (None), gone by (Passed), the front past its
        // stop (Past), clear through (Clear), outside its way under the other stops (Outside), past the stopping option
        // and on with the floor (Late), or a stop (Stop).
        private enum Verdict { None, Passed, Past, Clear, Outside, Late, Stop }
        private Verdict verdict;
        // The stops kept for cars (review round 7, BB1): where the front stops short of the car's way (the way's
        // place along the line), when last found, and whether found cautiously (stopped short by the brakes' lag and
        // the stop uncertainty) or by the lenient judgement (at the way).
        private readonly Dictionary<int, (float Way, float Since, bool Cautious)> holds = new Dictionary<int, (float, float, bool)>();
        private readonly HashSet<int> judged = new HashSet<int>(), fresh = new HashSet<int>(), nosedIn = new HashSet<int>(), heldNow = new HashSet<int>();
        // The yield Approaches set this decision, and the yield before it (every other constraint's).
        private float? holdsApplied, yieldWithoutHolds;
        // Lifting a stop kept for a car: the brakes' latency and the throttle's (LaunchDelay, measured from a
        // standstill) on top of the headway.
        public const float ReleaseMargin = BrakeLatency + LaunchDelay;
        private readonly float[] releaseLate = new float[MaxPoses];

        // A speed cap on the trajectory the commitments count on (the approach cap and creeps of this decision).
        private float speedCap = float.MaxValue;
        private float now;
        private readonly Dictionary<int, ulong> verdictLog = new Dictionary<int, ulong>();

    }
}
