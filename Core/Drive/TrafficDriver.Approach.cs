using System;
using System.Collections.Generic;
using DrivenRides.Core.Routing;

namespace DrivenRides.Core.Drive
{
    // Approach operations. Fields remain in TrafficDriver.cs to preserve initialization order.
    public sealed partial class TrafficDriver
    {

        private void Approaches(TrafficDecision d, in TrafficSnapshot s, float front)
        {
            if (approachCars.Count == 0 && holds.Count == 0) return;
            var others = holdsApplied != null && d.YieldAlong == holdsApplied ? yieldWithoutHolds : d.YieldAlong;
            var baseHold = Math.Min(holdFront, others ?? float.MaxValue);
            var v = Math.Max(0f, slowSpeed[0]);
            var hard = front + HardStop(v);
            speedCap = d.MaxSpeed ?? float.MaxValue;

            judged.Clear();
            fresh.Clear();
            released.Clear();
            for (var j = 0; j < approachCars.Count; j++)
            {
                var c = s.Cars[approachCars[j]];
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
                else if (verdict == Verdict.Passed || verdict == Verdict.Past || verdict == Verdict.Late) holds.Remove(c.Id);
                else if (verdict == Verdict.Clear && now - h.Since >= ConflictMemory) { released.Add((approachCars[j], c.Id, h)); holds.Remove(c.Id); }
            }
            scratch.Clear();
            foreach (var id in holds.Keys) if (!judged.Contains(id)) scratch.Add(id);
            for (var i = 0; i < scratch.Count; i++) holds.Remove(scratch[i]);

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

        private void Hold(int id, float way, bool cautious)
        {
            if (!holds.TryGetValue(id, out var h)) holds[id] = (way, now, cautious);
            else if (heldNow.Contains(id)) holds[id] = (Math.Min(h.Way, way), now, h.Cautious || cautious);
            else holds[id] = (way, now, cautious);
            heldNow.Add(id);
        }

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

        private float Approach(in OtherCar c, in TrafficSnapshot s, float front, bool held)
        {
            verdict = Verdict.None;
            emergencyStop = false;
            var v = fastSpeed[0] + FastAccel * 2f;
            var near = v * BrakeLatency + v * v / (2f * PursuitDriver.Decel) + StopUncertainty + body.Length;
            var reach = c.Speed * ApproachSeconds + Corridor.Accel * ApproachSeconds * ApproachSeconds / 2f + c.Length + 5f;
            float dx = c.X - poses[0].X, dz = c.Z - poses[0].Z;
            if (!held && dx * dx + dz * dz > (near + reach) * (near + reach)) return float.MaxValue;
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

        private bool Reuse(int id, int kind)
        {
            var key = id * 3 + kind;
            if (approachBuilt.TryGetValue(key, out var built)) { corridor = built; return true; }
            if (poolUsed == corridorPool.Count) corridorPool.Add(new Corridor());
            corridor = corridorPool[poolUsed++];
            approachBuilt[key] = corridor;
            return false;
        }

        private void ClipToSweep() => corridor.Clip(sweepMinX, sweepMinZ, sweepMaxX, sweepMaxZ);

        private bool WayAhead()
        {
            if (corridor.MaxX < sweepMinX || corridor.MinX > sweepMaxX || corridor.MaxZ < sweepMinZ || corridor.MinZ > sweepMaxZ) return false;
            for (var k = 0; k < count; k++) if (CoversSwept(k, out var ua, out _, out _, out _) && ua > 0f) return true;
            return false;
        }

        private float ApproachCautious(in OtherCar c, float front, bool held)
        {
            if (corridor.MaxX < sweepMinX || corridor.MinX > sweepMaxX || corridor.MaxZ < sweepMinZ || corridor.MinZ > sweepMaxZ) return float.MaxValue;
            var entry = -1;
            for (var k = 0; k < count && entry < 0; k++)
                if (CoversSwept(k, out var ua, out _, out _, out _) && ua > 0f) entry = k;
            if (entry < 0) { if (!held) verdict = Verdict.Passed; return float.MaxValue; }
            if (entry == 0) { verdict = Verdict.Past; return float.MaxValue; }
            if (CoversSwept(entry, out var eu, out _, out _, out _) && StopsForUs(c, entry, eu)) { verdict = Verdict.Clear; return float.MaxValue; }
            var boundary = rearAt[entry - 1] + body.FrontAhead - StopUncertainty;
            var room = boundary - front;
            if (room <= 0f) { verdict = Verdict.Past; return float.MaxValue; }
            if (held) for (var k = 0; k < count; k++) releaseLate[k] = k == 0 ? 0f : safeLate[k] + ReleaseMargin;
            var standingShort = fastSpeed[0] < StandingMs && room < StopUncertainty + 1f;
            if (standingShort || (approachStood.TryGetValue(c.Id, out var stood) && front < stood.Front + 2f && now - stood.Time < ApproachStoodSeconds))
            {
                if (standingShort) approachStood[c.Id] = (front, now);
                if (!Reuse(c.Id, 2)) { corridor.Build(graph, c, approachFlags, ApproachSeconds, SweepMetres, roadHeld, SpeedingUp(c.Id), LeaderOf(c, approachCarsList), Cruising(c.Id)); ClipToSweep(); }
                if (!ClearsOnRelease(c, entry, held)) { verdict = Verdict.Stop; return boundary; }
                verdict = clearsOutside ? Verdict.Outside : Verdict.Clear;
                return float.MaxValue;
            }
            var cap = PursuitDriver.Decel * (-BrakeLatency + (float)Math.Sqrt(BrakeLatency * BrakeLatency + 2f * room / PursuitDriver.Decel));
            if (!held && cap > fastSpeed[0] + FastAccel * 2f) return float.MaxValue;
            if (ClearsOnRelease(c, entry, held))
            {
                if (clearsOutside) { verdict = Verdict.Outside; return float.MaxValue; }
                verdict = Verdict.Clear;
                return float.MaxValue;
            }
            var v = Math.Max(0f, slowSpeed[0]);
            verdict = Verdict.Stop;
            emergencyStop = HardStop(v) > room + StopUncertainty;
            return boundary;
        }

        private float HardStopFront(float front)
        {
            var v = Math.Max(0f, slowSpeed[0]);
            return front + HardStop(v);
        }

        private float HardStop(float v) => v * (braking ? 0f : BrakeLatency) + v * v / (2f * PursuitDriver.HardDecel);

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
        public string VerdictsOn(int carId)
        {
            if (!verdictLog.TryGetValue(carId, out var log)) return "";
            var letters = "";
            for (; log != 0; log >>= 4) letters = "NPPCOLS"[(int)(log & 0xF) - 1] + letters;
            return letters;
        }
    }
}
