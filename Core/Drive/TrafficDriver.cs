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
        // Time either side of the ride car's own passage that a traffic car must keep clear of it.
        public const float Headway = 1.2f;
        // Pulling away from a standstill takes a gap this much longer, so a car that has just set off doesn't stop
        // again for the same traffic a metre on (T3 20261013-185723, ride 8: give way, go, give way at the turn).
        public const float StartHeadway = 0.8f;
        // Round the ride car's body in the corridor sweep: sideways and lengthways.
        public const float SideMargin = 0.4f;
        public const float EndMargin = 0.5f;
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
        public const float ConflictMemory = 1f;
        // A traffic car behind the ride car in a lane it joins: the braking it is left to stay behind, and the gap.
        public const float FollowBrake = 3f;
        public const float FollowGap = 2f;
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
        private readonly Corridor corridor = new Corridor();
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
        private readonly float[] rearAt = new float[MaxPoses], early = new float[MaxPoses], late = new float[MaxPoses], slowSpeed = new float[MaxPoses], fastSpeed = new float[MaxPoses];
        // The slow times and speeds again, under the stops the car already has (SafeTimes): a pose past one is never
        // reached (infinite time). What committing past a car's way counts on (review round 2, U1).
        private readonly float[] safeLate = new float[MaxPoses], safeSpeed = new float[MaxPoses];
        // The fast times and speeds under the same stops: the most the car can do, for when stopping is no better.

        // Where the front must stop at the latest, for the stops known (Holds), and that SafeTimes was worked out for.
        private float holdFront, safeFor, leaderCap = float.MaxValue;
        // Conflicts found too late to stop short of: decided once every other car's yield is known (Committed).
        // Remembered: committed to a moment ago (Approach), decided here too.
        private readonly List<(int Car, int RunStart, int Exit, float RunFrom, YieldNote Note, bool Remembered)> deferred = new List<(int, int, int, float, YieldNote, bool)>();
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
            this.graph = graph;
            this.line = line;
            this.driver = driver;
            this.body = body;
            roadHeld = Rules.RoadHeld;
        }

        public bool BackingOff => backingFrom >= 0;

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
            var end = Math.Min(along + SweepMetres, line.Line.Length - body.FrontAhead);
            if (d.StopAlong is float stop) end = Math.Min(end, stop - body.FrontAhead + 1f);
            creeps.Clear();
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
            Forget(committedTo);
            Forget(approachClearUntil);
            Forget(approachStood);
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
                Sweep(s, along, end);
            }

            // Layer 2: moving cars.
            headway = Headway + (Math.Abs(speed) < 0.5f ? StartHeadway : 0f);
            var horizon = Math.Min(12f, late[count - 1] + headway);
            approachHorizon = horizon;
            sweepMinX = sweepMinZ = float.MaxValue;
            sweepMaxX = sweepMaxZ = float.MinValue;
            for (var k = 0; k < count; k++)
            {
                var p = poses[k].Grown(SideMargin, EndMargin);
                sweepMinX = Math.Min(sweepMinX, p.X - p.ExtentX);
                sweepMaxX = Math.Max(sweepMaxX, p.X + p.ExtentX);
                sweepMinZ = Math.Min(sweepMinZ, p.Z - p.ExtentZ);
                sweepMaxZ = Math.Max(sweepMaxZ, p.Z + p.ExtentZ);
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
            }
            // The firm stops (stop line, the planner's hold, standing cars, parked cars and people) and the car followed.
            holdFront = Holds(d, s, front);
            SafeTimes(holdFront);
            deferred.Clear();
            approachCars.Clear();
            for (var i = 0; i < cars.Count; i++)
            {
                var c = cars[i];
                if (!moving.Contains(c.Id) || heldFor.Contains(c.Id) || OutOfReach(c, horizon)) continue;
                if (!BuildCorridor(c, s, horizon)) continue;
                deferNow = false;
                nosedInFor = -1;
                var give = Conflict(c, front, i);
                // Whatever the lenient judgement found (a yield further on, nothing), the cautious one keeps the
                // option to stop short of the first place the body comes into the car's way (review round 4, X4); in
                // the final pass, on the final trajectory.
                if (nosedInFor != c.Id) approachCars.Add(i);
                if (deferNow) continue;
                // Nosing in in front of it is a decision, not a different reading: the yield remembered from a moment
                // ago doesn't hold the ride car back (review round 2, U4).
                if (nosedInFor == c.Id) yieldMemory.Remove(c.Id);
                Give(d, c, give, s.Time, front);
            }
            // A car standing at its line (a red, a give-way) may set off now: judged cautiously too, the ride car keeps
            // the option to stop short of its way (a queue's first car at the light turning green found the ride car
            // too close to stop, review round 4, X4). Not one standing for the ride car (layer 3 is its) or stuck.
            for (var i = 0; i < cars.Count; i++)
            {
                var c = cars[i];
                if (moving.Contains(c.Id) || heldFor.Contains(c.Id) || OutOfReach(c, horizon)) continue;
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
            for (var round = 0; round < 4; round++)
            {
                var before = d.YieldAlong;
                Committed(d, s, front, horizon);
                KeepClear(d, front);
                Approaches(d, s, front);
                KeepClear(d, front);
                if (d.YieldAlong == before) break;
            }
            // Committed to only where the final judgement found it clear and nothing made the ride car stop short.
            if (d.ApproachFor < 0) for (var j = 0; j < commitCandidates.Count; j++) committedTo[commitCandidates[j]] = s.Time;
            commitCandidates.Clear();
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
            corridor.Build(graph, c, flags, horizon, SweepMetres, roadHeld, SpeedingUp(c.Id), LeaderOf(c, cars));
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
        private void Give(TrafficDecision d, in OtherCar c, float? give, float time, float front)
        {
            if (give is float given) yieldMemory[c.Id] = (given, time);
            else if (yieldMemory.TryGetValue(c.Id, out var kept))
            {
                // Given way to a moment ago and still in the ride car's way (its body or its way ahead covers the
                // sweep): keep giving way for ConflictMemory, so one frame of a different reading doesn't send the
                // ride car off in front of it (T3 20261013-211205, ride 9).
                // Not once it is the car ahead, followed on the ride car's way (its gap): it has gone by.
                if (time - kept.Time <= ConflictMemory && kept.Along > front - 0.5f && c.Id != d.GapFor && StillInTheWay())
                {
                    give = kept.Along;
                    conflictNote = new YieldNote { Kind = YieldNoteKind.Still };
                }
                else yieldMemory.Remove(c.Id);
            }
            if (give is float g && (d.YieldAlong == null || g < d.YieldAlong))
            {
                d.YieldAlong = g;
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
                    if (deferredGo[j] != Go.Stop) { deferredGo[j] = GoOn(s, j, horizon, front); deferredMargin[j] = clearMargin; }
            }
            for (var j = 0; j < deferred.Count; j++)
            {
                var c = s.Cars[deferred[j].Car];
                BuildCorridor(c, s, horizon);
                conflictNote = deferred[j].Note;
                // Going on past it is a decision, as nosing in is: no yield remembered from a moment ago holds the car
                // back inside its way.
                if (deferredGo[j] != Go.Stop) yieldMemory.Remove(c.Id);
                Give(d, c, deferredGo[j] == Go.Stop ? front : (float?)null, s.Time, front);
                if (deferredGo[j] == Go.Forced) { d.ForcedCommit = true; d.ForcedFor = c.Id; d.ForcedMargin = deferredMargin[j]; }
                if (deferredGo[j] == Go.Marginal) { d.MarginalCommit = true; d.MarginalFor = c.Id; d.MarginalMargin = deferredMargin[j]; }
            }
            return stopNow;
        }

        private enum Go { On, Stop, Forced, Marginal }

        // On or stop now, for a conflict the car can't stop short of:
        // - on if it clears on the trajectory it can keep under every constraint, with headway, a car behind counted
        //   on to brake only once the body is in its trigger;
        // - else stop, if standing there is free of a collision: out of the car's way, or where the car stops for the
        //   body in its trigger (a head-on stand-off too: back-off and the planner resolve it);
        // - else a collision is coming whatever it does (the car is about to touch it standing), and going on is the
        //   only way out: on (Forced) if the same trajectory clears with a positive time margin, else stop.
        // Review round 3, V2: before, a head-on stop counted as unsafe and the best case decided.
        private Go GoOn(in TrafficSnapshot s, int j, float horizon, float front)
        {
            var (i, runStart, exit, runFrom, _, remembered) = deferred[j];
            var c = s.Cars[i];
            BuildCorridor(c, s, horizon);
            if (Clears(c, runStart, exit, runFrom, headway)) return Go.On;
            // Committed to a moment ago: on while it still clears with no headway, counted as marginal (review round 5,
            // Y1); the memory keeps it from flickering, never past a car it no longer clears.
            if (remembered && Clears(c, runStart, exit, runFrom, 0f)) return Go.Marginal;
            if (StopSafe(c, front)) return Go.Stop;
            return Clears(c, runStart, exit, runFrom, 0f) ? Go.Forced : Go.Stop;
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

        // Stopping now at HardDecel: the body stands out of the car's way, or the car stops for it in its trigger
        // before it touches (StaysBehind), whichever way it faces. Judged where the body comes into the car's way on the
        // way to the stop, at the time it gets there, the car then as far on as it can be.
        private bool StopSafe(in OtherCar c, float front)
        {
            var v = slowSpeed[0];
            var standFront = front + v * v / (2f * PursuitDriver.HardDecel);
            for (var k = 0; k < count && rearAt[k] + body.FrontAhead <= standFront + 0.5f; k++)
            {
                // The body as it will stand, give or take StopUncertainty (the corridor has its own side margin), not the
                // margins kept for a moving body.
                if (!corridor.Covers(poses[k].Grown(StopUncertainty - Corridor.SideMargin, StopUncertainty), out var ua, out _, out _, out _)) continue;
                // The time to get there braking at HardDecel from v.
                var d = rearAt[k] - rearAt[0];
                var disc = v * v - 2f * PursuitDriver.HardDecel * d;
                var t = disc <= 0f ? v / PursuitDriver.HardDecel : (v - (float)Math.Sqrt(disc)) / PursuitDriver.HardDecel;
                return StaysBehind(c, t, ua - corridor.Far(t), null, null, 0, true);
            }
            return true;
        }

        // The poses from the car now (pose 0, as it stands) along the line, with the time window of each.
        private void Sweep(in TrafficSnapshot s, float along, float end)
        {
            poses[0] = BodyAt(s.RearX, s.RearZ, s.Heading);
            headings[0] = s.Heading;
            rearAt[0] = along;
            early[0] = late[0] = 0f;
            slowSpeed[0] = fastSpeed[0] = Math.Max(0f, s.Speed);
            count = 1;
            float fast = Math.Max(0f, s.Speed), slow = fast;
            var pts = line.Line.Points;
            var k = line.FirstPointFrom(along + SweepStep);
            for (var at = along + SweepStep; at <= end && count < MaxPoses; at += SweepStep)
            {
                // The rear axle on the line, and the line's heading there.
                while (k < pts.Count - 1 && line.Along(k) < at) k++;
                var b = pts[Math.Max(1, k)];
                var a = pts[Math.Max(0, k - 1)];
                var span = line.Along(Math.Max(1, k)) - line.Along(Math.Max(0, k - 1));
                var f = span > 1e-4f ? Math.Max(0f, Math.Min(1f, (at - line.Along(Math.Max(0, k - 1))) / span)) : 1f;
                float x = a.X + (b.X - a.X) * f, z = a.Z + (b.Z - a.Z) * f;
                var heading = (float)Math.Atan2(b.X - a.X, b.Z - a.Z);
                headings[count] = heading;
                poses[count] = BodyAt(x, z, heading);
                rearAt[count] = at;
                var limit = driver.PlannedSpeed(at, fast);
                if (creeps.Count > 0) limit = Math.Min(limit, CapAt(at));
                var nextFast = Math.Max(0.3f, Math.Min((float)Math.Sqrt(fast * fast + 2 * FastAccel * SweepStep), limit));
                var nextSlow = Math.Max(0.3f, Math.Min((float)Math.Sqrt(slow * slow + 2 * SlowAccel * SweepStep), limit));
                early[count] = early[count - 1] + 2 * SweepStep / Math.Max(0.3f, fast + nextFast);
                late[count] = late[count - 1] + 2 * SweepStep / Math.Max(0.3f, slow + nextSlow);
                fast = nextFast;
                slow = nextSlow;
                slowSpeed[count] = slow;
                fastSpeed[count] = fast;
                count++;
            }
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
                if (p <= 0 || keepClearFrom[p] < 1) return;
                var clear = rearAt[keepClearFrom[p] - 1] + body.FrontAhead;
                if (d.YieldAlong is float old && old <= clear) return;
                d.YieldAlong = clear;
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

        // The corridor built last covers a pose of the sweep at or ahead of the car's rear: it is still in the way.
        private bool StillInTheWay()
        {
            for (var k = 0; k < count; k++)
                if (corridor.Covers(poses[k].Grown(SideMargin, EndMargin), out _, out var ub, out _, out _) && ub > -corridor.Length) return true;
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
            // It stops short of the car's way under the stops it has: never in it.
            if (runStart > 0 && float.IsPositiveInfinity(times[runStart])) return true;
            for (var k = runStart; k < count; k++)
            {
                if (!corridor.Covers(poses[k].Grown(SideMargin, EndMargin), out var ua, out var ub, out var sin, out var cos))
                {
                    var spare = corridor.Reach(from) - times[k];
                    clearMargin = Math.Min(clearMargin, spare);
                    return spare > margin;
                }
                from = Math.Min(from, ua);
                var t = times[k];
                if (sin * poses[k].Sin + cos * poses[k].Cos > 0.64f)
                {
                    // Ahead of it on its way by the time the ride car is there at the earliest: followed, not crossed.
                    if (AheadBy(early[k], ub)) continue;
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
                if (corridor.Covers(poses[k].Grown(SideMargin, EndMargin), out var a, out _, out _, out _)) return a > ua + 1.5f;
            return false;
        }

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
                var pose = poses[k].Grown(SideMargin, EndMargin);
                if (!corridor.Covers(pose, out var ua, out var ub, out var sin, out var cos)) { runStart = -1; runFrom = float.MaxValue; continue; }
                if (runStart < 0) runStart = k;
                runFrom = Math.Min(runFrom, ua);
                var dot = sin * poses[k].Sin + cos * poses[k].Cos;
                var same = dot > 0.64f;
                // A car the ride car is ahead of on its way, going the same way along it, follows the ride car: not
                // its business (in a turn the body is across the lane, so the poses ahead tell the way, not pose 0).
                // One alongside about to move over isn't following it, though (ride 18): it must be behind its rear.
                // Never one coming the other way: the poses on can lie further along its way as the ride car turns
                // across in front of it (T0 priority, review round 4).
                if (k == 0 && ua > 0f && dot > -0.64f && (same || AheadAlong(ua)) && (!c.MayChangeLane || BehindRear(c))) return null;
                // Ground ahead of the car that it is coming into: the ride car must not come to a stop there.
                var comingUpBehind = same && followed.Contains(c.Id);
                if (ua > 0f && !(same && (AheadBy(early[k], ub) || comingUpBehind)) && entry < 0) entry = runStart;
                if (runStart > 0 && ua > 0f && !(same && (AheadBy(early[k], ub) || comingUpBehind)) && (keepClearFrom[k] < 0 || runStart < keepClearFrom[k]))
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
                // Against its way along a stretch that goes on past the sweep: the ride car can't be out of it
                // before the car comes (head-on in one lane).
                if (!conflict && dot < -0.64f && k == count - 1 && ua >= 0f) conflict = true;
                if (!conflict) continue;
                // Committed to going past it a moment ago, judged cautiously: decided in the final pass with the
                // conflicts found too late, on the same final trajectory (Committed; review round 5, Y1: before, it went
                // on here on a trajectory without this decision's yields and caps). The memory only keeps the decision
                // from flickering; it never waives clearing the car.
                var remembered = committedTo.TryGetValue(c.Id, out var since) && now - since <= CommitmentSeconds;
                // A car crawling this slowly stops for the ride car as soon as the ride car's body is in its front
                // trigger, well within the trigger's length (Gley's StopInDistance), if the body comes into its way
                // further ahead of its front than it needs to stop: the ride car noses in at a creep
                // instead of waiting for it, as drivers get out of a jam (T3 20261013-213209, ride 17: 62 s at a
                // merge, giving way to one crawling car after another).
                if (WillStopForUs(c, runStart, runFrom))
                {
                    var last = k;
                    while (last + 1 < count && corridor.Covers(poses[last + 1].Grown(SideMargin, EndMargin), out _, out _, out _, out _)) last++;
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
                    if (remembered || (v > StandingMs && v * BrakeLatency + v * v / (2f * PursuitDriver.HardDecel) > room))
                    {
                        deferred.Add((index, runStart, -1, runFrom, conflictNote, remembered));
                        deferNow = true;
                        return null;
                    }
                    return stopAt;
                }
                // Already inside its corridor: on if out before it can arrive, else stop here.
                var exit = -1;
                for (var j = k + 1; j < count; j++)
                {
                    if (!corridor.Covers(poses[j].Grown(SideMargin, EndMargin), out var ja, out _, out _, out _)) { exit = j; break; }
                    runFrom = Math.Min(runFrom, ja);
                }
                // Out before it can arrive, or joining its lane with the car behind able to stay behind: on; else stop
                // here. Decided with the other cars' yields known (Committed).
                deferred.Add((index, 0, exit, runFrom, conflictNote, remembered));
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
            commitCandidates.Clear();
            if (approachCars.Count == 0) return;
            var hold = Math.Min(holdFront, d.YieldAlong ?? float.MaxValue);
            speedCap = d.MaxSpeed ?? float.MaxValue;
            SafeTimes(hold);
            speedCap = float.MaxValue;
            // A verdict of clear holds for ApproachRecheck seconds, on this trajectory only (review round 5, Y1).
            var trajectory = (float)Math.Round(Math.Min(hold - front, 1000f) * 4f) + 10000f * (float)Math.Round(Math.Min(d.MaxSpeed ?? 100f, 100f) * 4f);
            var best = float.MaxValue;
            var bestFor = -1;
            for (var j = 0; j < approachCars.Count; j++)
            {
                var c = s.Cars[approachCars[j]];
                var stop = Approach(c, s, front, trajectory);
                if (stop < best) { best = stop; bestFor = c.Id; }
            }
            if (best == float.MaxValue || (d.YieldAlong is float y && y <= best)) return;
            d.YieldAlong = best;
            d.YieldFor = bestFor;
            d.ApproachFor = bestFor;
            d.ApproachAlong = best;
            d.Note = new YieldNote { Kind = YieldNoteKind.Approach, Car = bestFor };
        }

        // Where the front is to stop short of the car's way, or float.MaxValue if it needn't.
        private float Approach(in OtherCar c, in TrafficSnapshot s, float front, float trajectory)
        {
            // Found clear a moment ago on this trajectory (or nowhere near): not judged again for ApproachRecheck.
            if (approachClearUntil.TryGetValue(c.Id, out var known) && now < known.Until && known.Trajectory == trajectory) return float.MaxValue;
            // Only an entry within the stopping distance of the speed it can have in 2 s matters, and only a car that
            // can get there within ApproachSeconds: none further off is looked at (the check runs every step).
            var v = fastSpeed[0] + FastAccel * 2f;
            var near = v * BrakeLatency + v * v / (2f * PursuitDriver.Decel) + StopUncertainty + body.Length;
            var reach = c.Speed * ApproachSeconds + Corridor.Accel * ApproachSeconds * ApproachSeconds / 2f + c.Length + 5f;
            float dx = c.X - poses[0].X, dz = c.Z - poses[0].Z;
            if (dx * dx + dz * dz > (near + reach) * (near + reach))
            {
                approachClearUntil[c.Id] = (now + ApproachRecheck, trajectory);
                return float.MaxValue;
            }
            // Up to the road's limit, though no faster than a car close ahead of it that it can't pass (LeaderOf).
            corridor.Build(graph, c, s.Flags, Math.Min(approachHorizon, ApproachSeconds), near + 5f, roadHeld, true, LeaderOf(c, s.Cars));
            var stop = ApproachCautious(c, front);
            if (stop == float.MaxValue) approachClearUntil[c.Id] = (now + ApproachRecheck, trajectory);
            return stop;
        }

        private readonly Dictionary<int, (float Until, float Trajectory)> approachClearUntil = new Dictionary<int, (float, float)>();
        private const float ApproachRecheck = 0.1f;
        private float approachHorizon;
        // The cars judged cautiously this decision (moving ones with a corridor, and ones standing at their line).
        private readonly List<int> approachCars = new List<int>();
        // Where the front stood short of a car's way kept by Approach, and when.
        private readonly Dictionary<int, (float Front, float Time)> approachStood = new Dictionary<int, (float, float)>();
        private const float ApproachStoodSeconds = 10f;

        // How far ahead in time the cautious judgement looks for a car coming into the ride car's way.
        private const float ApproachSeconds = 6f;

        private float ApproachCautious(in OtherCar c, float front)
        {
            if (corridor.MaxX < sweepMinX || corridor.MinX > sweepMaxX || corridor.MaxZ < sweepMinZ || corridor.MinZ > sweepMaxZ) return float.MaxValue;
            // Where the body, with a moving body's margins as Conflict judges it, first comes into its way (the body
            // alone let a car the lenient judgement gave way to pass as clear: T0 priority, review round 5).
            var entry = -1;
            for (var k = 0; k < count && entry < 0; k++)
                if (corridor.Covers(poses[k].Grown(SideMargin, EndMargin), out var ua, out _, out _, out _) && ua > 0f) entry = k;
            if (entry <= 0) return float.MaxValue;
            var boundary = rearAt[entry - 1] + body.FrontAhead - StopUncertainty;
            var room = boundary - front;
            if (room <= 0f) return float.MaxValue;
            // Standing short of it, the option is kept: from there it sets off on the lenient judgement, as from any
            // give-way line, until its front is past where it stood (else it stops again at once on setting off, and
            // a dense merge never gave it a gap the cautious judgement takes; T0 merge).
            if (approachStood.TryGetValue(c.Id, out var stood) && front < stood.Front + 2f && now - stood.Time < ApproachStoodSeconds)
                return float.MaxValue;
            if (fastSpeed[0] < StandingMs && room < StopUncertainty + 1f)
            {
                approachStood[c.Id] = (front, now);
                return float.MaxValue;
            }
            // The highest speed from which it can still stop in `room`.
            var cap = PursuitDriver.Decel * (-BrakeLatency + (float)Math.Sqrt(BrakeLatency * BrakeLatency + 2f * room / PursuitDriver.Decel));
            // Nothing to keep yet: the car can't be that fast within the next two seconds.
            if (cap > fastSpeed[0] + FastAccel * 2f) return float.MaxValue;
            if (CommittedClear(c, entry, safeLate, safeSpeed, headway, false))
            {
                // Judged cautiously clear where the stopping option is about to go: committed to going on past it,
                // judged again each decision on the final trajectory (Committed, Go.Marginal).
                if (cap < fastSpeed[0] + FastAccel * 0.3f) commitCandidates.Add(c.Id);
                return float.MaxValue;
            }
            return boundary;
        }

        // The cars whose way the ride car committed to going past, judged cautiously (Approach), and when.
        private readonly Dictionary<int, float> committedTo = new Dictionary<int, float>();
        // Cars judged cautiously clear this decision; committed to only if no other car caps the speed (the judgement
        // counted on the trajectory without that cap).
        private readonly List<int> commitCandidates = new List<int>();
        // A speed cap on the trajectory the commitments count on (the approach cap and creeps of this decision).
        private float speedCap = float.MaxValue;
        private float now;
        // How long a commitment holds without being renewed.
        private const float CommitmentSeconds = 3f;

    }
}
