using System;
using System.Collections.Generic;
using DrivenRides.Core.Routing;

namespace DrivenRides.Core.Drive
{
    // Approach operations. Fields remain in TrafficDriver.cs to preserve initialization order.
    public sealed partial class TrafficDriver
    {

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
            // One that will stop for the body in its trigger (StopsForUs): clear, as for the lenient judgement.
            if (CoversSwept(entry, out var eu, out _, out _, out _) && StopsForUs(c, entry, eu)) { verdict = Verdict.Clear; return float.MaxValue; }
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
        // The cautious verdicts on a car lately (one letter each: None, Passed, Past, Clear, Outside, Late, Stop; for tests
        // and the trace).
        public string VerdictsOn(int carId)
        {
            if (!verdictLog.TryGetValue(carId, out var log)) return "";
            var letters = "";
            for (; log != 0; log >>= 4) letters = "NPPCOLS"[(int)(log & 0xF) - 1] + letters;
            return letters;
        }
    }
}
