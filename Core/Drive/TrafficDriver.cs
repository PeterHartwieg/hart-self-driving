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
    public sealed class TrafficDriver
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
        // only the margin touching. 2 m/s past a waiting car looked like a crawl (T3 20261013-185723, ride 8).
        public const float CreepMs = 3.5f;
        // A traffic car moving at most this fast stops for the ride car in its trigger: the ride car noses in.
        public const float NoseInMs = 5f;
        public const float SqueezeMs = 2f;
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
                    if (body0 >= 0) Gap(d, body0, along, c.Id);
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
                // would meet it (PursuitDriver.Follow).
                if (touch >= 0 && Ahead(c, touch))
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

        // The corridor of a moving car, clipped to the sweep's area; false if it doesn't come near it.
        private bool BuildCorridor(in OtherCar c, in TrafficSnapshot s, float horizon) => BuildCorridor(c, s.Cars, s.Flags, horizon);

        private bool BuildCorridor(in OtherCar c, IReadOnlyList<OtherCar> cars, ILiveFlags flags, float horizon)
        {
            corridor.Build(graph, c, flags, horizon, SweepMetres, roadHeld, SpeedingUp(c.Id), LeaderOf(c, cars), Cruising(c.Id));
            if (corridor.MaxX < sweepMinX || corridor.MinX > sweepMaxX || corridor.MaxZ < sweepMinZ || corridor.MinZ > sweepMaxZ) return false;
            corridor.Clip(sweepMinX, sweepMinZ, sweepMaxX, sweepMaxZ);
            return true;
        }

        // The speed of the nearest traffic car close ahead of `c` on its way (in its lane, going the same way, within
        // what it covers in 4 s and 15 m more), which it can't pass; float.MaxValue if none, if that car is pulling
        // away itself (a queue setting off from a light), or if `c` may change lanes and go round it (Follow or
        // Overtake: its corridor has the lanes beside; review round 3, V3).
        private float LeaderOf(in OtherCar c, IReadOnlyList<OtherCar> cars)
        {
            if (c.MayChangeLane) return float.MaxValue;
            float hx = (float)Math.Sin(c.Heading), hz = (float)Math.Cos(c.Heading);
            var best = float.MaxValue;
            var leader = float.MaxValue;
            var within = c.Speed * 4f + 15f;
            for (var i = 0; i < cars.Count; i++)
            {
                var o = cars[i];
                if (o.Id == c.Id) continue;
                float dx = o.X - c.X, dz = o.Z - c.Z;
                var ahead = dx * hx + dz * hz;
                if (ahead <= 0f || ahead > within || ahead >= best) continue;
                if (Math.Abs(dx * hz - dz * hx) > 2f) continue;
                if (Math.Sin(o.Heading) * hx + Math.Cos(o.Heading) * hz < 0.8) continue;
                best = ahead;
                leader = SpeedingUp(o.Id) ? float.MaxValue : o.Speed;
            }
            return leader;
        }

        // A car's answer from Conflict, with the memory of the yields of a moment ago, into the decision. The
        // corridor built last must be this car's.
        private void Give(TrafficDecision d, in OtherCar c, float? give, float time, float front, float? way = null)
        {
            if (give is float given) yieldMemory[c.Id] = (given, time);
            else if (yieldMemory.TryGetValue(c.Id, out var kept))
            {
                // Given way to a moment ago and still in the ride car's way (its body or its way ahead covers the
                // sweep): keep giving way for ConflictMemory, so one frame of a different reading doesn't send the
                // ride car off in front of it (T3 20261013-211205, ride 9).
                // Not once it is the car ahead, followed on the ride car's way (its gap): it has gone by.
                // Not one it can no longer make: that is the emergency rule's (review round 8, CC3).
                if (time - kept.Time <= ConflictMemory && kept.Along > front - 0.5f && c.Id != d.GapFor && kept.Along >= HardStopFront(front) && StillInTheWay())
                {
                    give = kept.Along;
                    conflictNote = new YieldNote { Kind = YieldNoteKind.Still };
                }
                else yieldMemory.Remove(c.Id);
            }
            if (give is float g && (d.YieldAlong == null || g < d.YieldAlong))
            {
                d.YieldAlong = g;
                d.YieldWay = way ?? g;
                d.YieldFor = c.Id;
                d.Note = conflictNote;
            }
        }

        // Where the front must stop at the latest for what is known before the moving cars' ways: the stop line, a
        // standing car, a parked car or a person (the adapter's cast). A moving car ahead is followed at its speed
        // (leaderCap), as PursuitDriver follows it.
        private float Holds(TrafficDecision d, in TrafficSnapshot s, float front)
        {
            var hold = float.MaxValue;
            leaderCap = float.MaxValue;
            if (d.StopAlong is float stop) hold = stop;
            if (standingGap is float sg) hold = Math.Min(hold, front + Math.Max(0f, sg - PursuitDriver.StopGap));
            if (d.GapAhead is float ga)
            {
                // Its own speed, not along the ride car's heading now: it is followed along the way it is on.
                var leader = -1f;
                for (var i = 0; i < s.Cars.Count; i++) if (s.Cars[i].Id == d.GapFor && moving.Contains(d.GapFor)) leader = s.Cars[i].Speed;
                if (leader < 0f) hold = Math.Min(hold, front + Math.Max(0f, ga - PursuitDriver.StopGap));
                else leaderCap = leader;
            }
            if (s.GapAhead is float og) hold = Math.Min(hold, front + Math.Max(0f, og - PursuitDriver.StopGap));
            if (s.PlannerHoldAlong is float ph) hold = Math.Min(hold, ph);
            return hold;
        }

        // The slow times again (SlowAccel, the planned speeds), under every constraint: the creeps, the car followed,
        // a standing start's LaunchDelay, and slowing at PursuitDriver.Decel to stop with the front at `hold`. The
        // trajectory the car can be counted on to keep; poses past the hold are never reached.
        private void SafeTimes(float hold)
        {
            safeFor = hold;
            safeLate[0] = 0f;
            safeSpeed[0] = slowSpeed[0];
            var v = slowSpeed[0];
            var delay = v < 0.3f ? LaunchDelay : 0f;
            var stopped = false;
            for (var k = 1; k < count; k++)
            {
                var room = hold - (rearAt[k] + body.FrontAhead);
                // Speeding up from the speed it has, not back to the profile's after a cap (review round 4, X1).
                var cap = Math.Min(Math.Min(slowSpeed[k], Math.Min(leaderCap, speedCap)), (float)Math.Sqrt(v * v + 2.0 * SlowAccel * (rearAt[k] - rearAt[k - 1])));
                if (creeps.Count > 0) cap = Math.Min(cap, CapAt(rearAt[k]));
                var next = stopped || room < 0f ? 0f : Math.Min(cap, (float)Math.Sqrt(2.0 * PursuitDriver.Decel * room));
                if (stopped || room < 0f || v + next < 0.1f)
                {
                    stopped = true;
                    safeLate[k] = float.PositiveInfinity;
                    safeSpeed[k] = 0f;
                    continue;
                }
                safeLate[k] = safeLate[k - 1] + 2f * (rearAt[k] - rearAt[k - 1]) / (v + next) + (k == 1 ? delay : 0f);
                safeSpeed[k] = next;
                v = next;
            }
        }

        // The conflicts found too late to stop short of the car's way (Conflict defers them), decided once every other
        // constraint is known (yields, keep-clear, creeps: SafeTimes under them). A stop now tightens them (the times
        // past the front are never reached), so the rest are decided again under it. True if one stops now.
        private bool Committed(TrafficDecision d, in TrafficSnapshot s, float front, float horizon)
        {
            if (deferred.Count == 0) return false;
            // Recomputed whatever the hold: creeps and caps added since change the times too (review round 4, X1).
            var hold = Math.Min(holdFront, d.YieldAlong ?? float.MaxValue);
            speedCap = d.MaxSpeed ?? float.MaxValue;
            SafeTimes(hold);
            speedCap = float.MaxValue;
            deferredGo.Clear();
            deferredMargin.Clear();
            // Published from this (the last, converged) evaluation only, the least margin over the cars (review round 6,
            // AA3).
            var stopNow = false;
            for (var j = 0; j < deferred.Count; j++)
            {
                var go = GoOn(s, j, horizon, front);
                deferredGo.Add(go);
                deferredMargin.Add(clearMargin);
                if (go == Go.Stop) stopNow = true;
            }
            if (stopNow && front < hold)
            {
                SafeTimes(front);
                for (var j = 0; j < deferred.Count; j++)
                    if (deferredGo[j] != Go.Stop && deferredGo[j] != Go.StopShort) { deferredGo[j] = GoOn(s, j, horizon, front); deferredMargin[j] = clearMargin; }
            }
            for (var j = 0; j < deferred.Count; j++)
            {
                var c = s.Cars[deferred[j].Car];
                BuildCorridor(c, s, horizon);
                conflictNote = deferred[j].Note;
                // Going on past it is a decision, as nosing in is: no yield remembered from a moment ago holds the car
                // back inside its way.
                var go = deferredGo[j];
                if (go != Go.Stop && go != Go.StopShort) yieldMemory.Remove(c.Id);
                var at = go == Go.Stop ? front : go == Go.StopShort ? StopShortAt(deferred[j].RunStart) : (float?)null;
                if (go == Go.Stop && !d.EmergencyFor.Contains(c.Id)) d.EmergencyFor.Add(c.Id);
                var rs = deferred[j].RunStart;
                Give(d, c, at, s.Time, front, rs > 0 ? rearAt[rs - 1] + body.FrontAhead : front);
            }
            return stopNow;
        }

        private enum Go { On, Stop, StopShort }


        // Under the final constraints the front can still stop short of the car's way (pose runStart): physically,
        // after the brakes' latency, at HardDecel.
        private bool CanStopShort(int runStart, float front)
        {
            var v = slowSpeed[0];
            return HardStop(v) <= rearAt[runStart - 1] + body.FrontAhead - front;
        }

        // Where the front is to stop short of the car's way: where the body would first come into it.
        private float StopShortAt(int runStart) => rearAt[runStart - 1] + body.FrontAhead;


        // On or stop, for a conflict found too late for the cautious judgement's stop: on if it clears on the trajectory
        // it can keep under every constraint, with headway, a car behind counted on to brake only once the body is in its
        // trigger; else a stop short of its way if it can still make one; else a stop now (the emergency rule's). Round
        // 9: going on with less than the headway (forced, marginal) went; the soak and the in-game dumps were no worse.
        private Go GoOn(in TrafficSnapshot s, int j, float horizon, float front)
        {
            var (i, runStart, exit, runFrom, _) = deferred[j];
            var c = s.Cars[i];
            BuildCorridor(c, s, horizon);
            if (Clears(c, runStart, exit, runFrom, headway)) return Go.On;
            if (runStart > 0 && CanStopShort(runStart, front)) return Go.StopShort;
            return Go.Stop;
        }

        // Out of the car's way before it arrives, `margin` seconds to spare, on the trajectory the car can keep.
        private bool Clears(in OtherCar c, int runStart, int exit, float runFrom, float margin)
        {
            // Already inside its way: out of it before it can arrive.
            if (runStart == 0 && exit >= 0)
            {
                var spare = corridor.Reach(runFrom) - safeLate[exit];
                clearMargin = spare;
                if (spare > margin) return true;
            }
            return CommittedClear(c, runStart, safeLate, safeSpeed, margin, false);
        }

        // The poses from the car now (pose 0, as it stands) along the line, with the time window of each.
        // The body halfway between pose k - 1 and pose k (Sweep).
        private readonly Box[] mids = new Box[MaxPoses];

        // The corridor covers pose k, with the margins, or the body on its way there from pose k - 1: a body that only
        // comes into a car's way between two poses is found at the later one, as one that comes into it there (review
        // round 7, BB1: at fixed poses such a graze was never found; with poses that slid with the car, one decision in
        // three, which made the stop come and go).
        // In the near field the poses are where the model foresees the body (Foresee), grown by the model's remaining error
        // while it turns, and the body braking hard from now is checked too.
        private bool CoversSwept(int k, out float ua, out float ub, out float sin, out float cos)
        {
            var side = SideMargin + grow[k];
            if (corridor.Covers(poses[k].Grown(side, EndMargin), out ua, out ub, out sin, out cos)) return true;
            if (k > 0 && corridor.Covers(mids[k].Grown(side, EndMargin), out ua, out ub, out sin, out cos)) return true;
            if (altOn[k] && corridor.Covers(alt[k].Grown(side, EndMargin), out ua, out ub, out sin, out cos)) return true;
            return alt2On[k] && corridor.Covers(alt2[k].Grown(side, EndMargin), out ua, out ub, out sin, out cos);
        }

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

        // The near field: from the car as it is (rear axle, heading, speed, wheel) through the stopping distance and
        // until the car has settled on the line, capped at `end`. The path going on first, then braking, then keeping
        // the speed, each from the car as it is when that one starts.
        private void Foresee(in TrafficSnapshot s, float along, float end, float near)
        {
            if (Model == null || s.Speed < -0.1f)
            {
                goCount = brakeCount = keepCount = 0;
                running = false;
                next = Pace.Go;
                goStarted = float.MinValue;
                return;
            }
            if (foreseer == null) foreseer = driver.Copy();
            var budget = ForeseeBudget;
            while (budget > 0)
            {
                if (!running)
                {
                    // Going on again once the last is ForeseeEvery old or no longer reaches through the near field;
                    // once a decision at most.
                    if (next == Pace.Go && (s.Time == goStarted || s.Time - goStarted < ForeseeEvery && s.Time >= goStarted && foreseenTo >= Math.Min(end, along + near))) return;
                    Start(s, along, end, near);
                }
                budget -= Run(budget);
                if (!running) Finish();
            }
        }

        private void Start(in TrafficSnapshot s, float along, float end, float near)
        {
            runPace = next;
            runFirst = (int)Math.Floor(along / SweepStep) + 1;
            runEnd = end;
            // On past the near field by as far as the car can go before the next foresight, so this one is reused
            // until then; braking, to a stand.
            var ahead = Math.Min(end, along + near + (Math.Max(0f, s.Speed) + FastAccel * ForeseeEvery) * ForeseeEvery);
            runNear = runPace == Pace.Brake ? end : ahead;
            runKeep = Math.Max(0f, s.Speed);
            runFrom = along;
            var m = Model!;
            m.X = s.RearX;
            m.Z = s.RearZ;
            m.Heading = s.Heading;
            m.Speed = runKeep;
            m.SetWheel(s.Wheel);
            foreseer!.Resume(along);
            runN = 0;
            runT = 0f;
            runPrevAlong = along;
            runPx = s.RearX;
            runPz = s.RearZ;
            runPh = s.Heading;
            runSettled = false;
            running = true;
            if (runPace == Pace.Go) goStarted = s.Time;
        }

        private void Finish()
        {
            if (runPace == Pace.Go)
            {
                (goX, runX) = (runX, goX);
                (goZ, runZ) = (runZ, goZ);
                (goH, runH) = (runH, goH);
                (goTurning, runTurning) = (runTurning, goTurning);
                goCell = runFirst;
                goCount = runN;
                foreseenTo = runFrom + runN * SweepStep;
                next = Pace.Brake;
            }
            else if (runPace == Pace.Brake)
            {
                (brakeX, runX) = (runX, brakeX);
                (brakeZ, runZ) = (runZ, brakeZ);
                (brakeH, runH) = (runH, brakeH);
                brakeCell = runFirst;
                brakeCount = runN;
                next = Pace.Keep;
            }
            else
            {
                (keepX, runX) = (runX, keepX);
                (keepZ, runZ) = (runZ, keepZ);
                (keepH, runH) = (runH, keepH);
                keepCell = runFirst;
                keepCount = runN;
                next = Pace.Go;
            }
        }

        // The model driven on from where the run got to, at most `budget` steps: on as the plan goes, braking at
        // HardDecel to a stand, or at the speed it had. Records the rear axle at each whole cell passed, until past
        // the near field and settled (or stood), at most to the end. The steps taken; `running` is false once done.
        private int Run(int budget)
        {
            var braking = runPace == Pace.Brake;
            var m = Model!;
            var p = foreseer!;
            // The path going on at the physics step; the two bracketing it (braking, keeping the speed) at twice it.
            var dt = runPace == Pace.Go ? ForeseeStep : 2f * ForeseeStep;
            var used = 0;
            while (used < budget)
            {
                if (runT >= ForeseeSeconds || runN >= MaxPoses) { running = false; break; }
                used++;
                var o = p.Step(m.Input(dt));
                var a = o.Along;
                if (runT > 0f)
                    for (var cell = runFirst + runN; cell * SweepStep <= a && runN < MaxPoses; cell++)
                    {
                        var f = a - runPrevAlong > 1e-4f ? (cell * SweepStep - runPrevAlong) / (a - runPrevAlong) : 1f;
                        f = Math.Max(0f, Math.Min(1f, f));
                        runX[runN] = runPx + ((float)m.X - runPx) * f;
                        runZ[runN] = runPz + ((float)m.Z - runPz) * f;
                        runH[runN] = runPh + (float)DriveLine.Wrap((float)m.Heading - runPh) * f;
                        runTurning[runN] = !runSettled;
                        runN++;
                    }
                runSettled = Math.Abs(o.Lateral) < Feasibility.SettledOff && Math.Abs(m.Steer) < Feasibility.SettledWheel;
                if (a >= runEnd || (runFirst + runN) * SweepStep > runEnd) { running = false; break; }
                if (!braking && a >= runNear && runSettled) { running = false; break; }
                runPrevAlong = a;
                runPx = (float)m.X;
                runPz = (float)m.Z;
                runPh = (float)m.Heading;
                if (braking) o.Desired = Math.Max(0f, (float)m.Speed - PursuitDriver.HardDecel * dt);
                if (runPace == Pace.Keep) o.Desired = runKeep;
                if (braking && m.Speed < 0.05) { running = false; break; }
                if (!braking && m.Speed < 0.05 && o.Desired < 0.05f) { running = false; break; }
                m.Step(o, dt);
                runT += dt;
            }
            return used;
        }

        private void Sweep(in TrafficSnapshot s, float along, float end)
        {
            poses[0] = BodyAt(s.RearX, s.RearZ, s.Heading);
            headings[0] = s.Heading;
            grow[0] = 0f;
            altOn[0] = alt2On[0] = false;
            rearAt[0] = along;
            early[0] = late[0] = 0f;
            slowSpeed[0] = fastSpeed[0] = Math.Max(0f, s.Speed);
            count = 1;
            float fast = Math.Max(0f, s.Speed), slow = fast;
            var pts = line.Line.Points;
            // The poses after the first lie at whole steps along the line, not a step on from wherever the car is: else
            // a body that only grazes a car's way was found in it one decision and not the next, as the samples slid
            // along with the car (T0 priority 41: giving way to an oncoming car every third decision; review round 7,
            // BB1).
            var first = (float)Math.Floor(along / SweepStep) * SweepStep + SweepStep;
            var k = Math.Min(line.FirstPointFrom(first), pts.Count - 1);
            var prevAt = along;
            float px = s.RearX, pz = s.RearZ, ph = s.Heading;
            for (var at = first; at <= end && count < MaxPoses; prevAt = at, at += SweepStep)
            {
                var ds = at - prevAt;
                // The rear axle on the line, and the line's heading there.
                while (k < pts.Count - 1 && line.Along(k) < at) k++;
                var b = pts[Math.Max(1, k)];
                var a = pts[Math.Max(0, k - 1)];
                var span = line.Along(Math.Max(1, k)) - line.Along(Math.Max(0, k - 1));
                var f = span > 1e-4f ? Math.Max(0f, (at - line.Along(Math.Max(0, k - 1))) / span) : 1f;
                if (k < pts.Count - 1) f = Math.Min(1f, f);
                float x = a.X + (b.X - a.X) * f, z = a.Z + (b.Z - a.Z) * f;
                var heading = (float)Math.Atan2(b.X - a.X, b.Z - a.Z);
                // Where the model foresees the body, in the near field; the planned line beyond.
                var cell = (int)Math.Round(at / SweepStep);
                grow[count] = 0f;
                altOn[count] = alt2On[count] = false;
                if (cell >= goCell && cell < goCell + goCount)
                {
                    var g = cell - goCell;
                    x = goX[g];
                    z = goZ[g];
                    heading = goH[g];
                    // While it turns, the margin round the body is at least the model's remaining error (StopUncertainty,
                    // the worst measured against NWH); settled, the side margin alone. The full error on top of the
                    // side margin reached into the next lane, and the ride car stood in junctions for oncoming cars
                    // passing in theirs (T0 priority 393, lights 288 and 437, one of them hit while standing).
                    grow[count] = goTurning[g] ? Math.Max(0f, StopUncertainty - SideMargin) : 0f;
                    var gb = cell - brakeCell;
                    if (gb >= 0 && gb < brakeCount)
                    {
                        alt[count] = BodyAt(brakeX[gb], brakeZ[gb], brakeH[gb]);
                        altOn[count] = true;
                    }
                    var gk = cell - keepCell;
                    if (gk >= 0 && gk < keepCount && cell < goCell + goCount)
                    {
                        alt2[count] = BodyAt(keepX[gk], keepZ[gk], keepH[gk]);
                        alt2On[count] = true;
                    }
                }
                headings[count] = heading;
                poses[count] = BodyAt(x, z, heading);
                mids[count] = BodyAt((px + x) / 2f, (pz + z) / 2f, (float)Math.Atan2(Math.Sin(ph) + Math.Sin(heading), Math.Cos(ph) + Math.Cos(heading)));
                px = x;
                pz = z;
                ph = heading;
                rearAt[count] = at;
                planned[count] = driver.PlannedSpeedOnward(at);
                var limit = planned[count];
                if (creeps.Count > 0) limit = Math.Min(limit, CapAt(at));
                Time(count, ds, limit, ref fast, ref slow);
                count++;
            }
        }

        // The speeds and times at pose k, from the pose before, at most `limit`.
        private void Time(int k, float ds, float limit, ref float fast, ref float slow)
        {
            var nextFast = Math.Max(0.3f, Math.Min((float)Math.Sqrt(fast * fast + 2 * FastAccel * ds), limit));
            var nextSlow = Math.Max(0.3f, Math.Min((float)Math.Sqrt(slow * slow + 2 * SlowAccel * ds), limit));
            early[k] = early[k - 1] + 2 * ds / Math.Max(0.3f, fast + nextFast);
            late[k] = late[k - 1] + 2 * ds / Math.Max(0.3f, slow + nextSlow);
            fast = nextFast;
            slow = nextSlow;
            slowSpeed[k] = slow;
            fastSpeed[k] = fast;
        }

        // The sweep's times again under the creeps found since, on the same poses. The plan's speed at each pose is
        // the one the sweep found: worked out for a faster car, it looks further ahead, but what lies past the nearer
        // look ahead is never below what the car can reach in a step, so the result is the same.
        private void Retime(in TrafficSnapshot s)
        {
            float fast = Math.Max(0f, s.Speed), slow = fast;
            for (var k = 1; k < count; k++) Time(k, rearAt[k] - rearAt[k - 1], Math.Min(planned[k], CapAt(rearAt[k])), ref fast, ref slow);
        }

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

        // Layer 3's back-off: start it for a car nose to nose with this one, keep it up until the ride car's body is
        // clear of that car's trigger, the car moves, MaxBackOff is used up or there is no room behind.
        private void Back(in TrafficSnapshot s, TrafficDecision d, int candidate)
        {
            if (backingFrom < 0 && candidate >= 0 && HasRoomBehind(s))
            {
                backingFrom = candidate;
                backStartX = s.RearX;
                backStartZ = s.RearZ;
            }
            if (backingFrom < 0) return;
            OtherCar? from = null;
            for (var i = 0; i < s.Cars.Count; i++) if (s.Cars[i].Id == backingFrom) { from = s.Cars[i]; break; }
            var backed = (float)Math.Sqrt((s.RearX - backStartX) * (s.RearX - backStartX) + (s.RearZ - backStartZ) * (s.RearZ - backStartZ));
            var keep = from is OtherCar && !moving.Contains(backingFrom) && backed < MaxBackOff && HasRoomBehind(s);
            if (keep)
            {
                var o2 = from!.Value;
                var p = poses[0];
                keep = backed < MinBackOff || p.Overlaps(BodyOf(o2).Grown(BackOffClear, BackOffClear)) || p.Overlaps(TriggerOf(o2).Grown(BackOffClear, BackOffClear))
                    || InTurnedTrigger(o2, s);
            }
            if (!keep) { backingFrom = -1; return; }
            d.BackOff = true;
            d.BackOffFrom = backingFrom;
        }


        // Standing with the body in the way of a car coming (with the margins round it), where standing isn't safe for
        // that car braking with its own brakes (TriggerBrake after TriggerReactCautious: before the point of no return
        // the quick stop isn't counted on), and it can back out of its way before the car is there: it backs out
        // (review round 8: the ride car stopped for a car in the far lane with its nose in the near lane, as a car came
        // into that lane; T0 crossing). It keeps backing until out of the car's way by BackOffClear, MaxBackOff back, or
        // the car is past.
        private void LaneBack(in TrafficSnapshot s, TrafficDecision d, bool standing)
        {
            if (laneBackFrom < 0)
            {
                if (!standing || !HasRoomBehindLane(s)) return;
                for (var i = 0; i < s.Cars.Count && laneBackFrom < 0; i++)
                {
                    var c = s.Cars[i];
                    if (!moving.Contains(c.Id) || heldFor.Contains(c.Id)) continue;
                    corridor.Build(graph, c, s.Flags, MaxHorizon, SweepMetres, roadHeld, SpeedingUp(c.Id), LeaderOf(c, s.Cars), Cruising(c.Id));
                    if (!corridor.Covers(poses[0].Grown(SideMargin, EndMargin), out var ua, out _, out var sin, out var cos) || ua <= 0f) continue;
                    // Only one coming across its way: not one going its way (it follows the ride car) nor one coming at it
                    // head-on (the stand-off is backing off from a car standing for it, and the planner's).
                    if (Math.Abs(sin * poses[0].Sin + cos * poses[0].Cos) > 0.64f) continue;
                    if (StaysBehind(c, 0f, ua, null, null, 0, false)) continue;
                    var out_ = BackOutDistance();
                    if (out_ < 0f) continue;
                    // Time to back out at BackOffMs, with a second to set off and stop, against the car's arrival.
                    if (out_ / BackOffMs + 1f >= corridor.Reach(ua)) continue;
                    if (!PathBehindFree(s, out_ + BackOffClear)) continue;
                    laneBackFrom = c.Id;
                    laneBackX = s.RearX;
                    laneBackZ = s.RearZ;
                }
                if (laneBackFrom < 0) return;
            }
            OtherCar? from = null;
            for (var i = 0; i < s.Cars.Count; i++) if (s.Cars[i].Id == laneBackFrom) { from = s.Cars[i]; break; }
            var backed = (float)Math.Sqrt((s.RearX - laneBackX) * (s.RearX - laneBackX) + (s.RearZ - laneBackZ) * (s.RearZ - laneBackZ));
            var keep = from is OtherCar && backed < MaxBackOff && HasRoomBehindLane(s);
            if (keep)
            {
                var c = from!.Value;
                corridor.Build(graph, c, s.Flags, MaxHorizon, SweepMetres, roadHeld, SpeedingUp(c.Id), LeaderOf(c, s.Cars), Cruising(c.Id));
                keep = corridor.Covers(poses[0].Grown(SideMargin + BackOffClear, EndMargin + BackOffClear), out var ua, out _, out _, out _) && ua > 0f;
            }
            if (!keep) { laneBackFrom = -1; return; }
            d.BackOff = true;
            d.BackOffFrom = laneBackFrom;
        }

        // A stand-off (review round 10: T3 ride 18, a car turning across the ride car's way waited with the ride car in
        // its trigger while the ride car waited behind it, until Gley's car crept into it): the ride car standing
        // StandOffSeconds and a car ahead in its way that waits for it (the ride car in its trigger, as Gley reports
        // it) standing as long. The ride car gives it room: it backs off while the car waits for it (or did within
        // StandOffClear: the trigger's reading comes and goes as the bodies move) or its body is in the car's way, the
        // way behind free, up to MaxBackOff. It stays where it is until the car's body is out of its way for
        // StandOffRoom on, StandOffHold at most. Again for the same car only once that has come StandOffMoved on: one
        // that doesn't go when given room waits for something else.
        private void StandOff(in TrafficSnapshot s, TrafficDecision d, bool standing, float along)
        {
            if (standing) { if (rideStoodSince < 0f) rideStoodSince = now; }
            else if (!standOffBacking) rideStoodSince = -1f;
            standOffBacking = false;
            Forget(standOffGiven);
            if (standOffFrom < 0)
            {
                if (!standing || rideStoodSince < 0f || now - rideStoodSince < StandOffSeconds) return;
                for (var i = 0; i < s.Cars.Count && standOffFrom < 0; i++)
                {
                    var c = s.Cars[i];
                    if (!c.StoppedForUs || moving.Contains(c.Id)) continue;
                    if (standOffGiven.TryGetValue(c.Id, out var was) && (c.X - was.X) * (c.X - was.X) + (c.Z - was.Z) * (c.Z - was.Z) < StandOffMoved * StandOffMoved) continue;
                    if (!standingSince.TryGetValue(c.Id, out var since) || now - since < StandOffSeconds) continue;
                    // Only one ahead in the ride car's way (its body or its trigger, which the ride car keeps out of),
                    // so each waits for the other and backing off gives it room: not one queued behind it (a U-turn's
                    // way comes back past that one).
                    if (!Before(c, poses[0])) continue;
                    var way = FirstZoneTouch(BodyOf(c), TriggerOf(c), 0.3f, 0);
                    if (way < 0 || rearAt[way] - along > StandOffRoom) continue;
                    standOffFrom = c.Id;
                    standOffX = s.RearX;
                    standOffZ = s.RearZ;
                    standOffSince = now;
                    standOffGiven[c.Id] = (c.X, c.Z);
                }
                if (standOffFrom < 0) return;
            }
            OtherCar? from = null;
            for (var i = 0; i < s.Cars.Count; i++) if (s.Cars[i].Id == standOffFrom) { from = s.Cars[i]; break; }
            if (!(from is OtherCar o) || now - standOffSince > StandOffHold) { standOffFrom = -1; return; }
            if (o.StoppedForUs) standOffWaited = now;
            var waits = now - standOffWaited < StandOffClear;
            var touch = FirstTouch(BodyOf(o), GroundMargin, 0);
            if (!waits && (touch < 0 || rearAt[touch] - along > StandOffRoom)) { standOffFrom = -1; return; }
            var backed = (float)Math.Sqrt((s.RearX - standOffX) * (s.RearX - standOffX) + (s.RearZ - standOffZ) * (s.RearZ - standOffZ));
            // Out of its trigger is not out of its way: a car turning across sweeps wider than its trigger reaches
            // (T3 ride 18, in the simulation: it set off once the trigger was clear and cut the corner into the ride
            // car's front). So also out of its way on along the lanes, with the margins and BackOffClear.
            corridor.Build(graph, o, s.Flags, MaxHorizon, SweepMetres, roadHeld, true, LeaderOf(o, s.Cars));
            var inWay = corridor.Covers(poses[0].Grown(SideMargin + BackOffClear, EndMargin + BackOffClear), out var ua, out _, out _, out _) && ua > 0f;
            if ((waits || inWay) && backed < MaxBackOff && PathBehindFree(s, SweepStep))
            {
                d.BackOff = true;
                d.BackOffFrom = o.Id;
                standOffBacking = true;
                return;
            }
            Gap(d, 0, along, o.Id);
        }

        public const float StandOffSeconds = 2f, StandOffClear = 0.5f, StandOffRoom = 10f, StandOffHold = 10f, StandOffMoved = 1f;
        private int standOffFrom = -1;
        private float standOffX, standOffZ, standOffSince, standOffWaited, rideStoodSince = -1f;
        private bool standOffBacking;
        private readonly Dictionary<int, (float X, float Z)> standOffGiven = new Dictionary<int, (float, float)>();

        // How far back the body is out of the corridor built last (with the margins and BackOffClear), or -1 if not
        // within MaxBackOff.
        private float BackOutDistance()
        {
            var p = poses[0];
            var heading = (float)Math.Atan2(p.Sin, p.Cos);
            for (var back = 0.25f; back <= MaxBackOff; back += 0.25f)
            {
                var b = new Box(p.X - p.Sin * back, p.Z - p.Cos * back, heading, p.HalfWidth, p.HalfLength).Grown(SideMargin + BackOffClear, EndMargin + BackOffClear);
                if (!corridor.Covers(b, out var ua, out _, out _, out _) || ua <= 0f) return back;
            }
            return -1f;
        }

        // The way back, `metres` from where the body stands, is free: nothing the adapter sees behind (parked cars,
        // people) and no traffic car's body on it (review round 9).
        private bool PathBehindFree(in TrafficSnapshot s, float metres)
        {
            if (s.GapBehind is float g && g < metres) return false;
            var p = poses[0];
            var heading = (float)Math.Atan2(p.Sin, p.Cos);
            for (var i = 0; i < s.Cars.Count; i++)
            {
                var other = BodyOf(s.Cars[i]).Grown(GroundMargin, GroundMargin);
                for (var back = SweepStep / 2f; back <= metres; back += SweepStep / 2f)
                    if (new Box(p.X - p.Sin * back, p.Z - p.Cos * back, heading, p.HalfWidth, p.HalfLength).Overlaps(other)) return false;
            }
            return true;
        }

        private bool HasRoomBehindLane(in TrafficSnapshot s)
        {
            var keep = backingFrom;
            backingFrom = laneBackFrom;
            var room = HasRoomBehind(s);
            backingFrom = keep;
            return room;
        }

        private int laneBackFrom = -1;
        private float laneBackX, laneBackZ;

        // Gley turns a car's trigger with its wheels (UpdateTriggerJob): along a bend the trigger follows its way, not
        // its heading. The body is in it if the car's way covers it within the trigger's length (and BackOffClear).
        private bool InTurnedTrigger(in OtherCar c, in TrafficSnapshot s)
        {
            corridor.Build(graph, c, s.Flags, 1f, c.TriggerLength + BackOffClear + 2f, roadHeld);
            return corridor.Covers(poses[0].Grown(BackOffClear, BackOffClear), out var ua, out _, out _, out _) && ua <= c.TriggerLength + BackOffClear;
        }

        // Room to back off: nothing the adapter sees behind (parked cars, people) and no traffic car's body within
        // RoomBehind of the ride car's rear.
        private bool HasRoomBehind(in TrafficSnapshot s)
        {
            if (s.GapBehind is float g && g < RoomBehind) return false;
            float hx = poses[0].Sin, hz = poses[0].Cos;
            var behind = new Box(poses[0].X - hx * RoomBehind, poses[0].Z - hz * RoomBehind, (float)Math.Atan2(hx, hz), poses[0].HalfWidth, poses[0].HalfLength);
            for (var i = 0; i < s.Cars.Count; i++)
            {
                var c = s.Cars[i];
                if (c.Id == backingFrom) continue;
                var other = BodyOf(c);
                if ((c.X - poses[0].X) * hx + (c.Z - poses[0].Z) * hz > 0f) continue;
                if (behind.Overlaps(other)) return false;
            }
            return true;
        }

        // Standing close behind a traffic car that won't move: the way is blocked (NwhDrive gives the step up, so the
        // planner can go round it, as for a parked car).
        private void Stuck(in TrafficSnapshot s, TrafficDecision d, bool standing)
        {
            if (!standing || !(d.GapAhead is float gap) || gap > PursuitDriver.StopGap + 2f || d.GapFor < 0 || heldFor.Contains(d.GapFor)) return;
            for (var i = 0; i < s.Cars.Count; i++)
            {
                var c = s.Cars[i];
                if (c.Id != d.GapFor || moving.Contains(c.Id) || !standingSince.TryGetValue(c.Id, out var since)) continue;
                var laneEnd = c.Target >= 0 && c.Target < graph.Count && graph.Successors(c.Target).Count == 0;
                if (laneEnd && s.Time - since > 3f) d.BlockedBy = c.Id;
                else if (s.Time - since > StuckSeconds) d.BlockedBy = c.Id;
                if (d.BlockedBy >= 0) d.BlockedWhy = laneEnd ? $"traffic car {c.Id} stands at a lane end in the way" : $"traffic car {c.Id} hasn't moved for {StuckSeconds:0} s";
                return;
            }
        }

        // Keep clear (as a driver keeps a junction clear): where the car will come to a stop, at a stop line, a yield
        // point or behind a standing car (following a moving one is no stop), its body must not stand in the way of a moving car that is coming. If it would, it
        // stops before entering that car's way instead (T0 lights: it crept into the oncoming lane behind a turning
        // car, stopped there, and the oncoming car stopped nose to nose with it).
        private void KeepClear(TrafficDecision d, float front)
        {
            for (var round = 0; round < 4; round++)
            {
                var stopFront = float.MaxValue;
                if (d.StopAlong is float a) stopFront = Math.Min(stopFront, a);
                if (d.YieldAlong is float y) stopFront = Math.Min(stopFront, y);
                if (standingGap is float g) stopFront = Math.Min(stopFront, front + Math.Max(0f, g - PursuitDriver.StopGap));
                // The firm stops too: parked cars and people, the planner's hold (review round 3, V1).
                stopFront = Math.Min(stopFront, holdFront);
                if (stopFront == float.MaxValue) return;
                var p = -1;
                for (var k = count - 1; k >= 0; k--) if (rearAt[k] + body.FrontAhead <= stopFront + 0.5f) { p = k; break; }
                if (p <= 0 || keepClearFrom[p] < 0) return;
                // Already in the car's way from where it is (pose 0): it doesn't go on to stand deeper in it, but stops
                // now, as hard as it can, the emergency rule's (review round 8: being in its margin doesn't lift the
                // keep-clear; T0 crossing). Standing there, it backs out if it can before the car comes (LaneBack).
                var clear = keepClearFrom[p] == 0 ? front : rearAt[keepClearFrom[p] - 1] + body.FrontAhead;
                if (d.YieldAlong is float old && old <= clear) return;
                // One the car can no longer make at HardDecel is the emergency rule's: braking as hard as it can to keep
                // out of the car's way rather than stand in it (review round 8, CC3; stopping where it can, at
                // HardDecel, left it standing further into a junction, and a lights run locked up: T0 lights 139).
                if (clear < HardStopFront(front) && !d.EmergencyFor.Contains(keepClearFor[p])) d.EmergencyFor.Add(keepClearFor[p]);
                d.YieldAlong = clear;
                d.YieldWay = clear;
                d.YieldFor = keepClearFor[p];
                d.Note = new YieldNote { Kind = YieldNoteKind.KeepClear, Car = keepClearFor[p] };
            }
        }

        // A car that can't reach the sweep's area along any way within the horizon (with the corridor's extra
        // stretch), or that has the whole area 15 m or more behind it, needs no corridor.
        private bool OutOfReach(in OtherCar c, float horizon)
        {
            var dx = Math.Max(0f, Math.Max(sweepMinX - c.X, c.X - sweepMaxX));
            var dz = Math.Max(0f, Math.Max(sweepMinZ - c.Z, c.Z - sweepMaxZ));
            var reach = c.Speed * horizon + Corridor.Accel * horizon * horizon / 2f + SweepMetres + c.Length + 5f;
            if (dx * dx + dz * dz > reach * reach) return true;
            float hx = (float)Math.Sin(c.Heading), hz = (float)Math.Cos(c.Heading);
            var ahead = Math.Max(Math.Max((sweepMinX - c.X) * hx + (sweepMinZ - c.Z) * hz, (sweepMaxX - c.X) * hx + (sweepMinZ - c.Z) * hz),
                                 Math.Max((sweepMinX - c.X) * hx + (sweepMaxZ - c.Z) * hz, (sweepMaxX - c.X) * hx + (sweepMaxZ - c.Z) * hz));
            return ahead < -15f;
        }

        private static void Within(in Box p, ref float minX, ref float minZ, ref float maxX, ref float maxZ)
        {
            minX = Math.Min(minX, p.X - p.ExtentX);
            maxX = Math.Max(maxX, p.X + p.ExtentX);
            minZ = Math.Min(minZ, p.Z - p.ExtentZ);
            maxZ = Math.Max(maxZ, p.Z + p.ExtentZ);
        }

        private void Extend(in Box p)
        {
            sweepMinX = Math.Min(sweepMinX, p.X - p.ExtentX);
            sweepMaxX = Math.Max(sweepMaxX, p.X + p.ExtentX);
            sweepMinZ = Math.Min(sweepMinZ, p.Z - p.ExtentZ);
            sweepMaxZ = Math.Max(sweepMaxZ, p.Z + p.ExtentZ);
        }

        // The corridor built last covers a pose of the sweep at or ahead of the car's rear: it is still in the way.
        private bool StillInTheWay()
        {
            for (var k = 0; k < count; k++)
                if (CoversSwept(k, out _, out var ub, out _, out _) && ub > -corridor.Length) return true;
            return false;
        }

        // Slow enough to stop within its trigger, with a metre to spare, braking as Gley's cars do.
        // Judged at the speed the car can have when its front comes up to where the body is in its way (it may be
        // pulling away, and the ride car nosing in at a creep takes longer than its sweep's times; review round 1,
        // O2, round 2, U4), and with the room it has left once the body is there.
        private bool WillStopForUs(in OtherCar c, int runStart, float runFrom)
        {
            var t = late[runStart];
            var then = Math.Min(corridor.TopSpeed, (float)Math.Sqrt(c.Speed * c.Speed + 2f * Corridor.Accel * Math.Max(0f, runFrom)));
            var stopping = then * then / (2f * PursuitDriver.LeaderBrake);
            // Only ahead of its front (a car the body would come up behind is followed, not nosed in on), and with a
            // metre to spare once it has stopped (two kept the ride car out of a crawl's gaps; review round 2, U4).
            return runFrom > 0f && then <= NoseInMs && stopping + 1f < c.TriggerLength && runFrom - corridor.Far(t) >= stopping + 1f;
        }

        // Gaining speed at more than half a metre a second each second.
        private bool SpeedingUp(int id) => pace.TryGetValue(id, out var p) && p.Gain > 0.5f;

        // Seen at a steady pace (gaining or losing under SteadyGain) for CruisingSeconds: a slow car doing so is cruising,
        // not pulling away to the road's limit (review round 8: T0 head-on, cars crawling at 4 m/s for many seconds were
        // judged as if about to reach 15 m/s, and the ride car waited outside a one-lane stretch for half a minute).
        private bool Cruising(int id) => steadySince.TryGetValue(id, out var since) && now - since >= CruisingSeconds;
        private readonly Dictionary<int, float> steadySince = new Dictionary<int, float>();
        private const float SteadyGain = 0.3f;
        private const float CruisingSeconds = 2f;

        // The car's front is behind the ride car's rear bumper.
        private bool BehindRear(in OtherCar c)
        {
            float hx = poses[0].Sin, hz = poses[0].Cos;
            var front = (c.X - poses[0].X) * hx + (c.Z - poses[0].Z) * hz + c.Length / 2f * ((float)Math.Sin(c.Heading) * hx + (float)Math.Cos(c.Heading) * hz);
            return front < -poses[0].HalfLength;
        }

        // A car on the same way is ahead of the ride car (to be followed, not given way to) only once its rear, not
        // its front, is past the stretch the ride car's body takes: an 11 m truck coming up beside the ride car as it
        // turned across its lane counted as ahead with its cab past the middle, and the ride car turned into its side
        // (T3 20261013-211205, ride 9).
        private bool AheadBy(float t, float ub) => corridor.Speed * t - corridor.Length >= ub;

        // The body, in the corridor's way from pose k, is still in it on down the sweep once it heads its way (the
        // corridor's own heading where it is, within the angle that counts as the same way).
        private bool JoinsBehind(int k)
        {
            for (var j = k + 1; j < count; j++)
            {
                if (!CoversSwept(j, out _, out _, out var sin, out var cos)) return false;
                if (sin * poses[j].Sin + cos * poses[j].Cos > 0.64f) return true;
            }
            return false;
        }

        // Going on from a conflict the car can't stop short of: on the trajectory it can be counted on to keep under
        // every constraint (SafeTimes), is it out of the car's way (crossing) `margin` seconds before the car arrives,
        // or, joining its lane, does the car behind stay behind? Never on the best case: the car might be held by
        // another stop inside the car's way (review round 2, U1; before, the early times with no headway). `quick`:
        // the car stops in its trigger as Gley's StopInDistance does (only past the point of no return); else at its
        // own brakes' limit (review round 4, X3). clearMargin: the least time to spare found, seconds.
        private bool CommittedClear(in OtherCar c, int runStart, float[] times, float[] speeds, float margin, bool quick)
        {
            var from = float.MaxValue;
            var behindFrom = -1;
            clearMargin = float.MaxValue;
            clearsOutside = false;
            // It stops short of the car's way under the stops it has: never in it. Staying outside, not clearing
            // through: no commitment comes of it (review round 6, AA1).
            if (runStart > 0 && float.IsPositiveInfinity(times[runStart])) { clearsOutside = true; return true; }
            for (var k = runStart; k < count; k++)
            {
                if (!CoversSwept(k, out var ua, out var ub, out var sin, out var cos))
                {
                    var spare = corridor.Reach(from) - times[k];
                    clearMargin = Math.Min(clearMargin, spare);
                    return spare > margin;
                }
                from = Math.Min(from, ua);
                var t = times[k];
                if (sin * poses[k].Sin + cos * poses[k].Cos > 0.64f)
                {
                    // Ahead of it on its way by the time the ride car is there at the earliest: followed, not crossed. So is
                    // one the body comes up behind (its rear ahead where they meet), as Conflict has it: the gap follows it
                    // (review round 7: the cautious judgement stopped the ride car for the car it was to follow).
                    if (AheadBy(early[k], ub) || followed.Contains(c.Id)) continue;
                    // Standing in its lane at the stop (pose k is never reached): it must stop behind the ride car
                    // when its trigger reaches it.
                    if (float.IsPositiveInfinity(t)) return StaysBehind(c, 0f, ua, null, null, 0, quick);
                    // Once it stays behind from the first pose in its lane on, it does from the later ones.
                    if (k > runStart && behindFrom == runStart) continue;
                    // No headway here: following the car behind through its trigger is the margin.
                    if (!StaysBehind(c, t, ua - corridor.Far(t), times, speeds, k, quick)) return false;
                    behindFrom = runStart;
                }
                else
                {
                    // Across its way: stopped there, or still there a headway after it can arrive.
                    if (float.IsPositiveInfinity(t)) return false;
                    // Gone past already, `margin` before the body gets there at the earliest (as Conflict's window).
                    if (corridor.Near(Math.Max(0f, early[k] - margin)) - corridor.Length > ub) continue;
                    var spare = corridor.Reach(ua) - t;
                    clearMargin = Math.Min(clearMargin, spare);
                    if (spare <= margin) return false;
                }
            }
            return true;
        }

        private float clearMargin;
        // The last CommittedClear's true was for staying outside the car's way, not clearing through it.
        private bool clearsOutside;

        // A Gley car coming up to the ride car's body, `gap` metres from it at time `t0`, the ride car from pose k on at
        // the given times and speeds (with no times, standing): followed through in time (Follower.StaysBehind; review
        // round 4, X2; round 5, Y4).
        private bool StaysBehind(in OtherCar c, float t0, float gap, float[]? times, float[]? speeds, int k, bool quick)
        {
            var their = corridor.SpeedingUp ? Math.Min(corridor.TopSpeed, corridor.Speed + Corridor.Accel * t0) : corridor.Speed;
            var cruise = corridor.SpeedingUp ? corridor.TopSpeed : corridor.Speed;
            return Follower.StaysBehind(their, cruise, Corridor.Accel, c.TriggerLength, gap, t0, times, speeds, count, k, quick);
        }

        // Whether the poses a few metres on lie further along the corridor than pose 0 (at `ua`): the ride car moves
        // along the other car's way, ahead of it.
        private bool AheadAlong(float ua)
        {
            for (var k = 3; k < Math.Min(count, 8); k++)
                if (CoversSwept(k, out var a, out _, out _, out _)) return a > ua + 1.5f;
            return false;
        }

        // The poses where the body would stand in the car's way (corridor built last), as Conflict marks them.
        private void MarkKeepClear(in OtherCar c, bool headOnOnly)
        {
            var runStart = -1;
            for (var k = 0; k < count; k++)
            {
                if (rearAt[k] + body.FrontAhead > holdFront + SweepStep) break;
                if (!CoversSwept(k, out var ua, out var ub, out var sin, out var cos)) { runStart = -1; continue; }
                if (runStart < 0) runStart = k;
                var dot = sin * poses[k].Sin + cos * poses[k].Cos;
                var same = dot > 0.64f;
                if (headOnOnly && dot >= -0.64f) continue;
                if (ua > 0f && (runStart > 0 || Math.Abs(dot) <= 0.64f) && !(same && (AheadBy(early[k], ub) || followed.Contains(c.Id))) && (keepClearFrom[k] < 0 || runStart < keepClearFrom[k]))
                {
                    keepClearFrom[k] = runStart;
                    keepClearFor[k] = c.Id;
                }
            }
        }

        // Past the sweep's last pose, the body stays in the car's way (head-on) for HeadOnLookOn more along the line: the
        // one-lane stretch goes on past the sweep. Else it ends just past it, and the poses within the sweep have judged
        // it (review round 8: a ride car waiting before a one-lane stretch, its sweep ending in the taper at the far end,
        // gave way to a car crawling 50 m off, half a minute; T0 head-on).
        private bool HeadOnGoesOn()
        {
            var last = rearAt[count - 1];
            var lineEnd = line.Line.Length - body.FrontAhead;
            for (var at = last + SweepStep; at <= last + HeadOnLookOn; at += SweepStep)
            {
                if (at > lineEnd) return true;
                var p = line.Line.At(at);
                var q = line.Line.At(Math.Min(line.Line.Length, at + 0.5f));
                var box = BodyAt(p.X, p.Z, (float)Math.Atan2(q.X - p.X, q.Z - p.Z)).Grown(SideMargin, EndMargin);
                if (!corridor.Covers(box, out _, out _, out var sin, out var cos) || sin * box.Sin + cos * box.Cos >= -0.64f) return false;
            }
            return true;
        }

        private const float HeadOnLookOn = 30f;

        // Layer 2 for one moving car whose corridor is built: where the front gives way, or null.
        private float? Conflict(in OtherCar c, float front, int index)
        {
            var runStart = -1;
            var runFrom = float.MaxValue;
            var entry = -1;
            for (var k = 0; k < count; k++)
            {
                // Past a firm stop (stop line, the planner's hold, a standing car, people) the body never gets; at it,
                // it stands for as long as it takes (review round 3, V1).
                var standsAt = rearAt[k] + body.FrontAhead;
                if (standsAt > holdFront + SweepStep) break;
                if (!CoversSwept(k, out var ua, out var ub, out var sin, out var cos)) { runStart = -1; runFrom = float.MaxValue; continue; }
                if (runStart < 0) runStart = k;
                runFrom = Math.Min(runFrom, ua);
                var dot = sin * poses[k].Sin + cos * poses[k].Cos;
                var same = dot > 0.64f;
                // A car the ride car is ahead of on its way, going the same way along it, follows the ride car: not
                // its business (in a turn the body is across the lane, so the poses ahead tell the way, not pose 0).
                // One alongside about to move over isn't following it, though (ride 18): it must be behind its rear.
                // Never one coming the other way: the poses on can lie further along its way as the ride car turns
                // across in front of it (T0 priority, review round 4).
                // A ride car standing there, with the car coming from behind it, only if the car can stay behind it as one
                // it joins in front of must (FollowBrake, FollowGap): standing with its nose just in the lane's margin, it
                // counted as ahead of a car coming at 12 m/s 23 m back, and pulled out in front of it (T0 merge 229,
                // review round 7).
                if (k == 0 && ua > 0f && dot > -0.64f && (same || AheadAlong(ua)) && (!c.MayChangeLane || BehindRear(c))
                    && (slowSpeed[0] >= MovingMs || !BehindRear(c) || Math.Max(0f, corridor.Speed * corridor.Speed - slowSpeed[0] * slowSpeed[0]) / (2f * FollowBrake) + FollowGap < ua)) return null;
                // Ground ahead of the car that it is coming into: the ride car must not come to a stop there.
                var comingUpBehind = same && followed.Contains(c.Id);
                if (ua > 0f && !(same && (AheadBy(early[k], ub) || comingUpBehind)) && entry < 0) entry = runStart;
                if (ua > 0f && (runStart > 0 || Math.Abs(dot) <= 0.64f) && !(same && (AheadBy(early[k], ub) || comingUpBehind)) && (keepClearFrom[k] < 0 || runStart < keepClearFrom[k]))
                {
                    keepClearFrom[k] = runStart;
                    keepClearFor[k] = c.Id;
                }
                var tLo = Math.Max(0f, early[k] - headway);
                var tHi = late[k] + headway;
                var stands = standsAt > holdFront - SweepStep;
                var conflict = (stands || corridor.Far(tHi) >= ua) && corridor.Near(tLo) - corridor.Length <= ub;
                if (same)
                {
                    // A car ahead of the ride car on the same way by then is caught up with, not crossed: the gap.
                    if (AheadBy(early[k], ub)) conflict = false;
                    // One the body comes up behind, its rear ahead where they meet: followed at its speed (Gap); a
                    // crawling car ahead was given way to as if it came from behind (review round 2, U4).
                    else if (comingUpBehind) conflict = false;
                    // One behind, in the lane the ride car joins, follows it once it is in: it must not need to brake
                    // harder than FollowBrake to stay behind (NWH spike U-turns: slow out of the turn, a car at
                    // 13 m/s close behind).
                    else
                    {
                        // At the pace it has, or, pulling away, at the speed it will have reached by then.
                        var their = corridor.SpeedingUp ? Math.Min(corridor.TopSpeed, corridor.Speed + Corridor.Accel * late[k]) : corridor.Speed;
                        var braking = Math.Max(0f, their * their - slowSpeed[k] * slowSpeed[k]) / (2f * FollowBrake);
                        conflict = corridor.Far(late[k]) + braking + FollowGap >= ua;
                    }
                }
                // Coming into its way at an angle behind it, as it goes on ahead, and on along its way after it: joining
                // its lane behind it, followed (Gap), not crossed (review round 9: swept where the car really goes, the
                // body cuts into a lane at an angle that the planned line only reached straightened out).
                else if (conflict && dot > 0f && AheadBy(early[k], ub) && JoinsBehind(k)) conflict = false;
                // Against its way along a stretch that goes on past the sweep: the ride car can't be out of it
                // before the car comes (head-on in one lane).
                if (!conflict && dot < -0.64f && k == count - 1 && ua >= 0f && HeadOnGoesOn()) conflict = true;
                if (!conflict) continue;
                // A car crawling this slowly stops for the ride car as soon as the ride car's body is in its front
                // trigger, well within the trigger's length (Gley's StopInDistance), if the body comes into its way
                // further ahead of its front than it needs to stop: the ride car noses in at a creep
                // instead of waiting for it, as drivers get out of a jam (T3 20261013-213209, ride 17: 62 s at a
                // merge, giving way to one crawling car after another).
                if (WillStopForUs(c, runStart, runFrom))
                {
                    var last = k;
                    while (last + 1 < count && CoversSwept(last + 1, out _, out _, out _, out _)) last++;
                    creeps.Add((rearAt[runStart], rearAt[last] + SweepStep, CreepMs));
                    noseIn = true;
                    nosedInFor = c.Id;
                    return null;
                }
                conflictNote = new YieldNote
                {
                    Kind = YieldNoteKind.Conflict, Pose = k, On = rearAt[k] - rearAt[0], From = ua, To = ub, Early = early[k], Late = late[k], Speed = corridor.Speed,
                    Same = same, HeadOn = dot < -0.64f,
                };
                if (runStart > 0)
                {
                    var stopAt = rearAt[runStart - 1] + body.FrontAhead;
                    // Too close to stop short of its way without braking harder than HardDecel: committed. Whether
                    // it goes on or stops now is decided once every car's yield is known (Committed).
                    var room = stopAt - front;
                    var v = fastSpeed[0];
                    // Standing (under StandingMs) it can always stop where it is.
                    if (v > StandingMs && HardStop(v) > room)
                    {
                        deferred.Add((index, runStart, -1, runFrom, conflictNote));
                        deferNow = true;
                        return null;
                    }
                    return stopAt;
                }
                // Already inside its corridor: on if out before it can arrive, else stop here.
                var exit = -1;
                for (var j = k + 1; j < count; j++)
                {
                    if (!CoversSwept(j, out var ja, out _, out _, out _)) { exit = j; break; }
                    runFrom = Math.Min(runFrom, ja);
                }
                // Out before it can arrive, or joining its lane with the car behind able to stay behind: on; else stop
                // here. Decided with the other cars' yields known (Committed).
                deferred.Add((index, 0, exit, runFrom, conflictNote));
                deferNow = true;
                return null;
            }
            return null;
        }

        // Before the body first comes into a car's way, keep the option to stop short of it: braking at
        // PursuitDriver.Decel after BrakeLatency, with StopUncertainty to spare. Unless, judged cautiously, going on
        // clears it (the car may speed up to the road's limit; it brakes for the body at its own brakes' TriggerBrake
        // after TriggerReactCautious; the final trajectory the car can keep, with headway), the front is to stop
        // there: a yield, so the driver brakes for it by distance as for any stop (review round 5, Y3: a speed cap
        // only, the pedals coasted within 0.7 m/s of it). Past the point where it can no longer stop so, the lenient
        // judgement of the emergency rule applies. The car's corridor is its usual one, its stops and give-ways as
        // they are set (review round 5, Y5: built as if every car ignored its red, the ride car slowed on its own
        // green). Review round 4, X4: commitments found too late were forced (96 in 500 priority runs).
        private void Approaches(TrafficDecision d, in TrafficSnapshot s, float front)
        {
            if (approachCars.Count == 0 && holds.Count == 0) return;
            // Every constraint but the stops kept for cars (holds): those are put back one by one below.
            var others = holdsApplied != null && d.YieldAlong == holdsApplied ? yieldWithoutHolds : d.YieldAlong;
            var baseHold = Math.Min(holdFront, others ?? float.MaxValue);
            var v = Math.Max(0f, slowSpeed[0]);
            var hard = front + HardStop(v);
            speedCap = d.MaxSpeed ?? float.MaxValue;

            // First the cars held for, each on the trajectory every other stop leaves, the other cars' kept stops
            // included and only its own removed (review round 8, CC1: before, one trajectory without any kept stop
            // judged them all, so a car could be lifted as clear on a trajectory that ignored another car's stop).
            judged.Clear();
            fresh.Clear();
            released.Clear();
            for (var j = 0; j < approachCars.Count; j++)
            {
                var c = s.Cars[approachCars[j]];
                // Nosing in, the ride car counts on the crawling car stopping for it and on the cars queued behind that
                // one, which can't go round it; every other car is judged (review round 7, BB2: before, one nose-in let
                // every car off).
                if (QueuedBehindNosedIn(c, s.Cars)) continue;
                judged.Add(c.Id);
                if (followed.Contains(c.Id)) { holds.Remove(c.Id); continue; }
                if (!holds.TryGetValue(c.Id, out var h)) continue;
                SafeTimes(Math.Min(baseHold, KeptStops(c.Id, v, hard)));
                var stop = Approach(c, s, front, true);
                corridor = mainCorridor;
                if (stop < float.MaxValue)
                {
                    Hold(c.Id, stop + StopUncertainty, true);
                    fresh.Add(c.Id);
                    if (emergencyStop && !d.EmergencyFor.Contains(c.Id)) d.EmergencyFor.Add(c.Id);
                }
                // Lifted when the car has gone by (or is now the car ahead, followed by the gap), the front is past
                // where it could stop short of its way, or going on past it is the emergency rule's (Late); or when
                // going on clears it with the release margin and neither judgement has found it for ConflictMemory
                // (the cautious one's verdict jumps as the poses and the car's way change: clear one decision, a stop
                // the next).
                else if (verdict == Verdict.Passed || verdict == Verdict.Past || verdict == Verdict.Late) holds.Remove(c.Id);
                else if (verdict == Verdict.Clear && now - h.Since >= ConflictMemory) { released.Add((approachCars[j], c.Id, h)); holds.Remove(c.Id); }
            }
            // A stop kept for a car not judged now goes: the car is gone, stands for the ride car, is nosed in front
            // of or queued behind one that is, or is too close to stop short of (the emergency rule's).
            scratch.Clear();
            foreach (var id in holds.Keys) if (!judged.Contains(id)) scratch.Add(id);
            for (var i = 0; i < scratch.Count; i++) holds.Remove(scratch[i]);

            // The cars lifted are judged again together, on the trajectory the stops still kept leave: one no longer
            // clear there is held again (CC1).
            if (released.Count > 0)
            {
                SafeTimes(Math.Min(baseHold, KeptStops(-1, v, hard)));
                for (var i = 0; i < released.Count; i++)
                {
                    var (index, _, h) = released[i];
                    var c = s.Cars[index];
                    var stop = Approach(c, s, front, true);
                    corridor = mainCorridor;
                    if (stop < float.MaxValue)
                    {
                        holds[c.Id] = h;
                        Hold(c.Id, stop + StopUncertainty, true);
                        fresh.Add(c.Id);
                        if (emergencyStop && !d.EmergencyFor.Contains(c.Id)) d.EmergencyFor.Add(c.Id);
                    }
                }
            }

            // Then every other car, on the trajectory with all the stops kept: a new stop where it isn't clear.
            SafeTimes(Math.Min(baseHold, KeptStops(-1, v, hard)));
            for (var j = 0; j < approachCars.Count; j++)
            {
                var c = s.Cars[approachCars[j]];
                if (!judged.Contains(c.Id) || holds.ContainsKey(c.Id) || followed.Contains(c.Id) || WasReleased(c.Id)) continue;
                var stop = Approach(c, s, front, false);
                corridor = mainCorridor;
                if (stop < float.MaxValue)
                {
                    Hold(c.Id, stop + StopUncertainty, true);
                    fresh.Add(c.Id);
                    if (emergencyStop && !d.EmergencyFor.Contains(c.Id)) d.EmergencyFor.Add(c.Id);
                }
            }
            speedCap = float.MaxValue;
            if (holds.Count == 0) return;

            // The stops kept: a cautious one short of the boundary by what the brakes take to act (StopShort). One it
            // can no longer make that wasn't found just now goes: going on past it was judged by the emergency rule
            // this decision, or found clear (a stop found just now that it can no longer make came from the emergency
            // rule itself: ApproachCautious).
            var best = float.MaxValue;
            var bestFor = -1;
            var bestWay = 0f;
            var bestCautious = false;
            scratch.Clear();
            foreach (var kv in holds)
            {
                var (way, _, cautious) = kv.Value;
                if (hard > way && !fresh.Contains(kv.Key)) { scratch.Add(kv.Key); continue; }
                var target = way;
                if (target < best) { best = target; bestFor = kv.Key; bestWay = way; bestCautious = cautious; }
            }
            for (var i = 0; i < scratch.Count; i++) holds.Remove(scratch[i]);
            if (bestFor < 0 || (d.YieldAlong is float y && y <= best)) return;
            yieldWithoutHolds = others;
            holdsApplied = best;
            d.YieldAlong = best;
            d.YieldWay = bestCautious ? bestWay - StopUncertainty : bestWay;
            d.YieldFor = bestFor;
            if (bestCautious)
            {
                d.ApproachFor = bestFor;
                d.ApproachAlong = bestWay - StopUncertainty;
                d.Note = new YieldNote { Kind = YieldNoteKind.Approach, Car = bestFor };
            }
        }

        // The nearest of the stops kept for cars other than `except` that the car can still make, as handed to the
        // driver (StopShort for a cautious one); float.MaxValue if none.
        private float KeptStops(int except, float v, float hard)
        {
            var best = float.MaxValue;
            foreach (var kv in holds)
            {
                if (kv.Key == except || hard > kv.Value.Way) continue;
                best = Math.Min(best, kv.Value.Way);
            }
            return best;
        }

        private bool WasReleased(int id)
        {
            for (var i = 0; i < released.Count; i++) if (released[i].Id == id) return true;
            return false;
        }

        // The cars lifted in this round of the final pass (their index in the snapshot, id, the stop as kept), judged
        // again together (CC1).
        private readonly List<(int Index, int Id, (float Way, float Since, bool Cautious) Hold)> released = new List<(int, int, (float, float, bool))>();

        // Keeps a stop for a car, at the front's place short of its way (`way`). Found again, it moves to where it is
        // found now (the nearest of this decision's): nearer at once; further on only because the body no longer
        // comes into the car's way before there (that part of it has gone by). Not found again, it stays where it was.
        private void Hold(int id, float way, bool cautious)
        {
            if (!holds.TryGetValue(id, out var h)) holds[id] = (way, now, cautious);
            else if (heldNow.Contains(id)) holds[id] = (Math.Min(h.Way, way), now, h.Cautious || cautious);
            else holds[id] = (way, now, cautious);
            heldNow.Add(id);
        }

        // Nosed in front of, or queued behind such a car in its lane, unable to go round it (the cars ahead of it on its
        // way, each closer than what it covers in 4 s and 15 m, as LeaderOf finds them).
        private bool QueuedBehindNosedIn(in OtherCar c, IReadOnlyList<OtherCar> cars)
        {
            if (nosedIn.Count == 0) return false;
            if (nosedIn.Contains(c.Id)) return true;
            var x = c;
            for (var hop = 0; hop < 8; hop++)
            {
                if (x.MayChangeLane) return false;
                var ahead = AheadInLane(x, cars);
                if (ahead < 0) return false;
                if (nosedIn.Contains(cars[ahead].Id)) return true;
                x = cars[ahead];
            }
            return false;
        }

        // The nearest car close ahead of `c` in its lane going its way (LeaderOf's), or -1.
        private static int AheadInLane(in OtherCar c, IReadOnlyList<OtherCar> cars)
        {
            float hx = (float)Math.Sin(c.Heading), hz = (float)Math.Cos(c.Heading);
            var best = float.MaxValue;
            var found = -1;
            var within = c.Speed * 4f + 15f;
            for (var i = 0; i < cars.Count; i++)
            {
                var o = cars[i];
                if (o.Id == c.Id) continue;
                float dx = o.X - c.X, dz = o.Z - c.Z;
                var ahead = dx * hx + dz * hz;
                if (ahead <= 0f || ahead > within || ahead >= best) continue;
                if (Math.Abs(dx * hz - dz * hx) > 2f) continue;
                if (Math.Sin(o.Heading) * hx + Math.Cos(o.Heading) * hz < 0.8) continue;
                best = ahead;
                found = i;
            }
            return found;
        }

        // Where the front is to stop short of the car's way, or float.MaxValue if it needn't.
        private float Approach(in OtherCar c, in TrafficSnapshot s, float front, bool held)
        {
            verdict = Verdict.None;
            emergencyStop = false;
            // Judged afresh every decision and every round of the final pass: no verdict is carried over (review round
            // 6, AA4: a cache keyed on the hold and the cap missed creeps, a leader's speed and changed flags).
            // Only an entry within the stopping distance of the speed it can have in 2 s matters, and only a car that
            // can get there within ApproachSeconds: none further off is looked at (the check runs every step).
            var v = fastSpeed[0] + FastAccel * 2f;
            var near = v * BrakeLatency + v * v / (2f * PursuitDriver.Decel) + StopUncertainty + body.Length;
            var reach = c.Speed * ApproachSeconds + Corridor.Accel * ApproachSeconds * ApproachSeconds / 2f + c.Length + 5f;
            float dx = c.X - poses[0].X, dz = c.Z - poses[0].Z;
            // A car held for is judged however far off it is now, over the whole sweep: lifted only on a verdict.
            if (!held && dx * dx + dz * dz > (near + reach) * (near + reach)) return float.MaxValue;
            // Up to the road's limit, though no faster than a car close ahead of it that it can't pass (LeaderOf). Over
            // the whole sweep, as the lenient judgement builds it, held or not: a shorter one ended a join early, so a
            // car was clear by one length and not by the other, and the stop came and went (review round 7, BB1).
            // A car held for has gone by only once its way, as far ahead in time as the lenient judgement ever looks
            // (MaxHorizon), no longer comes up ahead at all: not because the look ahead ended short of it (a car given
            // way to 4 s off counted as gone every other decision).
            if (held)
            {
                if (!Reuse(c.Id, 0)) { corridor.Build(graph, c, s.Flags, MaxHorizon, SweepMetres, roadHeld, true, LeaderOf(c, s.Cars)); ClipToSweep(); }
                if (!WayAhead()) { verdict = Verdict.Passed; return float.MaxValue; }
            }
            if (!Reuse(c.Id, 1)) { corridor.Build(graph, c, s.Flags, ApproachSeconds, SweepMetres, roadHeld, true, LeaderOf(c, s.Cars)); ClipToSweep(); }
            approachFlags = s.Flags;
            approachCarsList = s.Cars;
            var stop = ApproachCautious(c, front, held);
            // For tests and the trace: the cautious verdicts on each car, the latest last.
            // Kept as four bits a verdict (the last sixteen), so a decision allocates nothing; spelt out on request.
            var vc = (ulong)verdict + 1;
            verdictLog.TryGetValue(c.Id, out var log);
            if ((log & 0xF) != vc) verdictLog[c.Id] = (log << 4) | vc;
            return stop;
        }


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

        // A car's corridors for the cautious judgement depend only on this decision's snapshot (its flags, the cars,
        // the look ahead): built once a decision and reused in each round of the final pass (review round 7, BB5).
        // Points `corridor` at the one for (car, kind); true if it was built already.
        private bool Reuse(int id, int kind)
        {
            var key = id * 3 + kind;
            if (approachBuilt.TryGetValue(key, out var built)) { corridor = built; return true; }
            if (poolUsed == corridorPool.Count) corridorPool.Add(new Corridor());
            corridor = corridorPool[poolUsed++];
            approachBuilt[key] = corridor;
            return false;
        }

        private readonly Dictionary<int, Corridor> approachBuilt = new Dictionary<int, Corridor>();
        private readonly List<Corridor> corridorPool = new List<Corridor>();
        private int poolUsed;

        // Only the corridor's segments in the area the sweep covers are ever tested against the poses: the rest go, so
        // each pose tests a few (review round 10: the cost per step). Its bounds stay the whole corridor's.
        private void ClipToSweep() => corridor.Clip(sweepMinX, sweepMinZ, sweepMaxX, sweepMaxZ);

        // The corridor built last comes into the sweep somewhere ahead of the body.
        private bool WayAhead()
        {
            if (corridor.MaxX < sweepMinX || corridor.MinX > sweepMaxX || corridor.MaxZ < sweepMinZ || corridor.MinZ > sweepMaxZ) return false;
            for (var k = 0; k < count; k++) if (CoversSwept(k, out var ua, out _, out _, out _) && ua > 0f) return true;
            return false;
        }

        private float ApproachCautious(in OtherCar c, float front, bool held)
        {
            if (corridor.MaxX < sweepMinX || corridor.MinX > sweepMaxX || corridor.MaxZ < sweepMinZ || corridor.MinZ > sweepMaxZ) return float.MaxValue;
            // Where the body, with a moving body's margins as Conflict judges it, first comes into its way (the body
            // alone let a car the lenient judgement gave way to pass as clear: T0 priority, review round 5).
            var entry = -1;
            for (var k = 0; k < count && entry < 0; k++)
                if (CoversSwept(k, out var ua, out _, out _, out _) && ua > 0f) entry = k;
            // Its way doesn't come up ahead within the cautious look ahead (gone by, for a car not held for), or the
            // body is in it already (the emergency rule's).
            if (entry < 0) { if (!held) verdict = Verdict.Passed; return float.MaxValue; }
            if (entry == 0) { verdict = Verdict.Past; return float.MaxValue; }
            var boundary = rearAt[entry - 1] + body.FrontAhead - StopUncertainty;
            var room = boundary - front;
            if (room <= 0f) { verdict = Verdict.Past; return float.MaxValue; }
            // Lifting a stop kept for it: the brakes let go and the throttle takes hold, each with its own lag, so the car
            // may be on the trajectory judged up to ReleaseMargin late (review round 7, BB1). Clear on both: the
            // trajectory as it is and the same one the margin late (crossing ahead of a car, or joining in front of
            // one, with less time to spare). Being late only helps with a car that goes by first, which the fast
            // times already judge; that prediction can't get earlier as the car speeds up, so it needs no margin.
            if (held) for (var k = 0; k < count; k++) releaseLate[k] = k == 0 ? 0f : safeLate[k] + ReleaseMargin;
            // Standing short of it, the option is kept: from there it sets off on the lenient judgement, as from any
            // give-way line, until its front is past where it stood (else it stops again at once on setting off, and
            // a dense merge never gave it a gap the cautious judgement takes; T0 merge).
            // Setting off from there (review round 6, AA2: the same admission as anywhere it can still stop short): the
            // full headway on the car's usual way (its stops and give-ways as set, its own pace), braking for the body
            // at its own brakes; else it stays.
            var standingShort = fastSpeed[0] < StandingMs && room < StopUncertainty + 1f;
            if (standingShort || (approachStood.TryGetValue(c.Id, out var stood) && front < stood.Front + 2f && now - stood.Time < ApproachStoodSeconds))
            {
                if (standingShort) approachStood[c.Id] = (front, now);
                if (!Reuse(c.Id, 2)) { corridor.Build(graph, c, approachFlags, ApproachSeconds, SweepMetres, roadHeld, SpeedingUp(c.Id), LeaderOf(c, approachCarsList), Cruising(c.Id)); ClipToSweep(); }
                if (!ClearsOnRelease(c, entry, held)) { verdict = Verdict.Stop; return boundary; }
                verdict = clearsOutside ? Verdict.Outside : Verdict.Clear;
                return float.MaxValue;
            }
            // The highest speed from which it can still stop in `room`.
            var cap = PursuitDriver.Decel * (-BrakeLatency + (float)Math.Sqrt(BrakeLatency * BrakeLatency + 2f * room / PursuitDriver.Decel));
            // Nothing to keep yet: the car can't be that fast within the next two seconds.
            if (!held && cap > fastSpeed[0] + FastAccel * 2f) return float.MaxValue;
            if (ClearsOnRelease(c, entry, held))
            {
                if (clearsOutside) { verdict = Verdict.Outside; return float.MaxValue; }
                verdict = Verdict.Clear;
                return float.MaxValue;
            }
            // Past where it can still stop short of the car's way (after the brakes' latency, at HardDecel): a stop it
            // can't make is the emergency rule's, marked so (review round 8, CC3): it brakes as hard as it can.
            var v = Math.Max(0f, slowSpeed[0]);
            verdict = Verdict.Stop;
            emergencyStop = HardStop(v) > room + StopUncertainty;
            return boundary;
        }

        // Where the front can stop at the soonest: after the brakes' latency, at HardDecel.
        private float HardStopFront(float front)
        {
            var v = Math.Max(0f, slowSpeed[0]);
            return front + HardStop(v);
        }

        // How far the car runs on stopping as hard as it can from v: the brakes' latency first, unless it is braking
        // already (review round 8: a car braking smoothly to its stop counted as unable to make it in its last metre,
        // the latency added on top of braking it was already doing, and the emergency rule stopped it "now").
        private float HardStop(float v) => v * (braking ? 0f : BrakeLatency) + v * v / (2f * PursuitDriver.HardDecel);

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

        // The cautious judgement's clearance (CommittedClear on the final trajectory, with headway); for a car held for,
        // on the same trajectory ReleaseMargin late as well (ApproachCautious).
        private bool ClearsOnRelease(in OtherCar c, int entry, bool held)
        {
            if (!held) return CommittedClear(c, entry, safeLate, safeSpeed, headway, false);
            if (!CommittedClear(c, entry, releaseLate, safeSpeed, headway, false)) return false;
            var outside = clearsOutside;
            var spare = clearMargin;
            if (!CommittedClear(c, entry, safeLate, safeSpeed, headway + ReleaseMargin, false)) return false;
            clearsOutside |= outside;
            clearMargin = Math.Min(clearMargin, spare);
            return true;
        }

        // A speed cap on the trajectory the commitments count on (the approach cap and creeps of this decision).
        private float speedCap = float.MaxValue;
        private float now;
        // The cautious verdicts on a car lately (one letter each: None, Passed, Past, Clear, Outside, Late, Stop; for tests
        // and the trace).
        public string VerdictsOn(int carId)
        {
            if (!verdictLog.TryGetValue(carId, out var log)) return "";
            var letters = "";
            for (; log != 0; log >>= 4) letters = "NPPCOLS"[(int)(log & 0xF) - 1] + letters;
            return letters;
        }
        private readonly Dictionary<int, ulong> verdictLog = new Dictionary<int, ulong>();

    }
}
