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
            for (var i = 0; i < cars.Count; i++)
            {
                var c = cars[i];
                if (!moving.Contains(c.Id) || heldFor.Contains(c.Id)) continue;
                var touch = FirstTouch(BodyOf(c), GroundMargin, 0);
                // A car ahead on the ride car's way, moving: followed at its speed along that way where the body
                // would meet it (PursuitDriver.Follow).
                if (touch >= 0 && Ahead(c, touch)) Gap(d, touch, along, c.Id, false, Math.Max(0f, c.Vx * poses[touch].Sin + c.Vz * poses[touch].Cos));
                if (OutOfReach(c, horizon)) continue;
                corridor.Build(graph, c, s.Flags, horizon, SweepMetres, roadHeld, SpeedingUp(c.Id));
                if (corridor.MaxX < sweepMinX || corridor.MinX > sweepMaxX || corridor.MaxZ < sweepMinZ || corridor.MinZ > sweepMaxZ) continue;
                corridor.Clip(sweepMinX, sweepMinZ, sweepMaxX, sweepMaxZ);
                var give = Conflict(c, front);
                if (give is float given) yieldMemory[c.Id] = (given, s.Time);
                else if (yieldMemory.TryGetValue(c.Id, out var kept))
                {
                    // Given way to a moment ago and still in the ride car's way (its body or its way ahead covers the
                    // sweep): keep giving way for ConflictMemory, so one frame of a different reading doesn't send the
                    // ride car off in front of it (T3 20261013-211205, ride 9).
                    if (s.Time - kept.Time <= ConflictMemory && kept.Along > front - 0.5f && StillInTheWay())
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
            if (noseIn)
            {
                var now = CapAt(along);
                if (now < 100f && (d.MaxSpeed == null || now < d.MaxSpeed)) d.MaxSpeed = now;
                noseIn = false;
            }
            KeepClear(d, front);
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
            if (d.BlockedBy >= 0) { kind = 6; id = d.BlockedBy; }
            if (kind != whyKind || id != whyId)
            {
                whyKind = kind;
                whyId = id;
                whyText = kind switch
                {
                    1 => "stop line", 2 => "give-way line", 3 => $"giving way to {id}", 4 => $"behind {id}", 5 => "creeping", 6 => "blocked: " + d.BlockedWhy, _ => "",
                };
            }
            d.Why = whyText;
            return d;
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
                keep = backed < MinBackOff || p.Overlaps(BodyOf(o2).Grown(BackOffClear, BackOffClear)) || p.Overlaps(TriggerOf(o2).Grown(BackOffClear, BackOffClear));
            }
            if (!keep) { backingFrom = -1; return; }
            d.BackOff = true;
            d.BackOffFrom = backingFrom;
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
        // Judged at the speed the car can have reached by the time the ride car's body comes into its way (it may be
        // pulling away; review round 1, O2), and with the room it has left by then.
        private bool WillStopForUs(in OtherCar c, int runStart, float runFrom)
        {
            var t = late[runStart];
            var then = Math.Min(corridor.TopSpeed, c.Speed + Corridor.Accel * t);
            var stopping = then * then / (2f * PursuitDriver.LeaderBrake);
            return then <= NoseInMs && stopping + 1f < c.TriggerLength && runFrom - corridor.Far(t) >= stopping + 2f;
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

        // Going on as fast as the car can from now (the early times and their speeds): clear of the car's corridor run
        // from runStart before it arrives, or, joining its lane, far enough ahead that it can follow at FollowBrake.
        private bool CommittedClear(int runStart)
        {
            var from = float.MaxValue;
            for (var k = runStart; k < count; k++)
            {
                if (!corridor.Covers(poses[k].Grown(SideMargin, EndMargin), out var ua, out var ub, out var sin, out var cos))
                    return early[k] < corridor.Reach(from);
                from = Math.Min(from, ua);
                if (sin * poses[k].Sin + cos * poses[k].Cos > 0.64f)
                {
                    if (AheadBy(early[k], ub)) continue;
                    var their = corridor.SpeedingUp ? Math.Min(corridor.TopSpeed, corridor.Speed + Corridor.Accel * early[k]) : corridor.Speed;
                    // Committed, the car behind is left HardDecel to stay behind, not FollowBrake.
                    var braking = Math.Max(0f, their * their - fastSpeed[k] * fastSpeed[k]) / (2f * PursuitDriver.HardDecel);
                    if (corridor.Far(early[k]) + braking + FollowGap >= ua) return false;
                }
                else if (corridor.Far(early[k]) >= ua && corridor.Near(early[k]) - corridor.Length <= ub) return false;
            }
            return true;
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
        private float? Conflict(in OtherCar c, float front)
        {
            var runStart = -1;
            var runFrom = float.MaxValue;
            for (var k = 0; k < count; k++)
            {
                var pose = poses[k].Grown(SideMargin, EndMargin);
                if (!corridor.Covers(pose, out var ua, out var ub, out var sin, out var cos)) { runStart = -1; runFrom = float.MaxValue; continue; }
                if (runStart < 0) runStart = k;
                runFrom = Math.Min(runFrom, ua);
                var dot = sin * poses[k].Sin + cos * poses[k].Cos;
                var same = dot > 0.64f;
                // A car the ride car is ahead of on its way, going the same way along it, follows the ride car: not
                // its business (in a turn the body is across the lane, so the poses ahead tell the way, not pose 0).
                // One alongside about to move over isn't following it, though (ride 18): it must be behind its rear.
                if (k == 0 && ua > 0f && (same || AheadAlong(ua)) && (!c.MayChangeLane || BehindRear(c))) return null;
                // Ground ahead of the car that it is coming into: the ride car must not come to a stop there.
                if (runStart > 0 && ua > 0f && !(same && AheadBy(early[k], ub)) && (keepClearFrom[k] < 0 || runStart < keepClearFrom[k]))
                {
                    keepClearFrom[k] = runStart;
                    keepClearFor[k] = c.Id;
                }
                var tLo = Math.Max(0f, early[k] - headway);
                var tHi = late[k] + headway;
                var conflict = corridor.Far(tHi) >= ua && corridor.Near(tLo) - corridor.Length <= ub;
                if (same)
                {
                    // A car ahead of the ride car on the same way by then is caught up with, not crossed: the gap.
                    if (AheadBy(early[k], ub)) conflict = false;
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
                    // Too close to stop short of its way without braking harder than HardDecel: committed. Going on
                    // as fast as the car can, is it out of the way (crossing) or far enough ahead (joining its lane)
                    // before the car comes? Then on; a stop it can't make would leave the nose in the car's way (T0
                    // pullaway: a conflict found a metre short of the lane at 5 m/s).
                    var room = stopAt - front;
                    var v = fastSpeed[0];
                    if (room < 0.5f || v * v / (2f * room) > PursuitDriver.HardDecel)
                        return CommittedClear(runStart) ? (float?)null : front;
                    return stopAt;
                }
                // Already inside its corridor: on if out before it can arrive, else stop here.
                var exit = -1;
                for (var j = k + 1; j < count; j++)
                {
                    if (!corridor.Covers(poses[j].Grown(SideMargin, EndMargin), out var ja, out _, out _, out _)) { exit = j; break; }
                    runFrom = Math.Min(runFrom, ja);
                }
                if (exit >= 0 && late[exit] < corridor.Reach(runFrom)) return null;
                // Joining its lane (no way out of it ahead): on if, going as fast as it can, the car behind is left
                // HardDecel to stay behind; stopping would leave the body in its way.
                if (CommittedClear(0)) return null;
                return front;
            }
            return null;
        }
    }
}
