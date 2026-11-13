using System;
using System.Collections.Generic;
using DrivenRides.Core.Routing;

namespace DrivenRides.Core.Drive
{
    // Conflicts operations. Fields remain in TrafficDriver.cs to preserve initialization order.
    public sealed partial class TrafficDriver
    {

        private bool BuildCorridor(in OtherCar c, in TrafficSnapshot s, float horizon) => BuildCorridor(c, s.Cars, s.Flags, horizon);

        private bool BuildCorridor(in OtherCar c, IReadOnlyList<OtherCar> cars, ILiveFlags flags, float horizon)
        {
            corridor.Build(graph, c, flags, horizon, SweepMetres, roadHeld, SpeedingUp(c.Id), LeaderOf(c, cars), Cruising(c.Id));
            if (corridor.MaxX < sweepMinX || corridor.MinX > sweepMaxX || corridor.MaxZ < sweepMinZ || corridor.MinZ > sweepMaxZ) return false;
            corridor.Clip(sweepMinX, sweepMinZ, sweepMaxX, sweepMaxZ);
            return true;
        }

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

        private void Give(TrafficDecision d, in OtherCar c, float? give, float time, float front, float? way = null)
        {
            if (give is float given) yieldMemory[c.Id] = (given, time);
            else if (yieldMemory.TryGetValue(c.Id, out var kept))
            {
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

        private float Holds(TrafficDecision d, in TrafficSnapshot s, float front)
        {
            var hold = float.MaxValue;
            leaderCap = float.MaxValue;
            if (d.StopAlong is float stop) hold = stop;
            if (standingGap is float sg) hold = Math.Min(hold, front + Math.Max(0f, sg - PursuitDriver.StopGap));
            if (d.GapAhead is float ga)
            {
                var leader = -1f;
                for (var i = 0; i < s.Cars.Count; i++) if (s.Cars[i].Id == d.GapFor && moving.Contains(d.GapFor)) leader = s.Cars[i].Speed;
                if (leader < 0f) hold = Math.Min(hold, front + Math.Max(0f, ga - PursuitDriver.StopGap));
                else leaderCap = leader;
            }
            if (s.GapAhead is float og) hold = Math.Min(hold, front + Math.Max(0f, og - PursuitDriver.StopGap));
            if (s.PlannerHoldAlong is float ph) hold = Math.Min(hold, ph);
            return hold;
        }

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

        private bool Committed(TrafficDecision d, in TrafficSnapshot s, float front, float horizon)
        {
            if (deferred.Count == 0) return false;
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
                    if (deferredGo[j] != Go.Stop && deferredGo[j] != Go.StopShort) { deferredGo[j] = GoOn(s, j, horizon, front); deferredMargin[j] = clearMargin; }
            }
            for (var j = 0; j < deferred.Count; j++)
            {
                var c = s.Cars[deferred[j].Car];
                BuildCorridor(c, s, horizon);
                conflictNote = deferred[j].Note;
                var go = deferredGo[j];
                if (go != Go.Stop && go != Go.StopShort) yieldMemory.Remove(c.Id);
                var at = go == Go.Stop ? front : go == Go.StopShort ? StopShortAt(deferred[j].RunStart) : (float?)null;
                if (go == Go.Stop && !d.EmergencyFor.Contains(c.Id)) d.EmergencyFor.Add(c.Id);
                var rs = deferred[j].RunStart;
                Give(d, c, at, s.Time, front, rs > 0 ? rearAt[rs - 1] + body.FrontAhead : front);
            }
            return stopNow;
        }


        private bool CanStopShort(int runStart, float front)
        {
            var v = slowSpeed[0];
            return HardStop(v) <= rearAt[runStart - 1] + body.FrontAhead - front;
        }

        private float StopShortAt(int runStart) => rearAt[runStart - 1] + body.FrontAhead;


        private Go GoOn(in TrafficSnapshot s, int j, float horizon, float front)
        {
            var (i, runStart, exit, runFrom, _) = deferred[j];
            var c = s.Cars[i];
            BuildCorridor(c, s, horizon);
            if (Clears(c, runStart, exit, runFrom, headway)) return Go.On;
            if (runStart > 0 && CanStopShort(runStart, front)) return Go.StopShort;
            return Go.Stop;
        }

        private bool Clears(in OtherCar c, int runStart, int exit, float runFrom, float margin)
        {
            if (runStart == 0 && exit >= 0)
            {
                var spare = corridor.Reach(runFrom) - safeLate[exit];
                clearMargin = spare;
                if (spare > margin) return true;
            }
            return CommittedClear(c, runStart, safeLate, safeSpeed, margin, false);
        }

        private void KeepClear(TrafficDecision d, float front)
        {
            for (var round = 0; round < 4; round++)
            {
                var stopFront = float.MaxValue;
                if (d.StopAlong is float a) stopFront = Math.Min(stopFront, a);
                if (d.YieldAlong is float y) stopFront = Math.Min(stopFront, y);
                if (standingGap is float g) stopFront = Math.Min(stopFront, front + Math.Max(0f, g - PursuitDriver.StopGap));
                stopFront = Math.Min(stopFront, holdFront);
                if (stopFront == float.MaxValue) return;
                var p = -1;
                for (var k = count - 1; k >= 0; k--) if (rearAt[k] + body.FrontAhead <= stopFront + 0.5f) { p = k; break; }
                if (p <= 0 || keepClearFrom[p] < 0) return;
                var clear = keepClearFrom[p] == 0 ? front : rearAt[keepClearFrom[p] - 1] + body.FrontAhead;
                if (d.YieldAlong is float old && old <= clear) return;
                if (clear < HardStopFront(front) && !d.EmergencyFor.Contains(keepClearFor[p])) d.EmergencyFor.Add(keepClearFor[p]);
                d.YieldAlong = clear;
                d.YieldWay = clear;
                d.YieldFor = keepClearFor[p];
                d.Note = new YieldNote { Kind = YieldNoteKind.KeepClear, Car = keepClearFor[p] };
            }
        }

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

        private bool StillInTheWay()
        {
            for (var k = 0; k < count; k++)
                if (CoversSwept(k, out _, out var ub, out _, out _) && ub > -corridor.Length) return true;
            return false;
        }

        private bool WillStopForUs(in OtherCar c, int runStart, float runFrom)
        {
            var t = late[runStart];
            var then = Math.Min(corridor.TopSpeed, (float)Math.Sqrt(c.Speed * c.Speed + 2f * Corridor.Accel * Math.Max(0f, runFrom)));
            var stopping = then * then / (2f * PursuitDriver.LeaderBrake);
            return runFrom > 0f && then <= NoseInMs && stopping + 1f < c.TriggerLength && runFrom - corridor.Far(t) >= stopping + 1f;
        }

        private static bool TriggerStops(in OtherCar c, float ua) => ua >= c.Speed * TriggerReact + TriggerStopMetres + 1f;

        private bool SpeedingUp(int id) => pace.TryGetValue(id, out var p) && p.Gain > 0.5f;

        private bool Cruising(int id) => steadySince.TryGetValue(id, out var since) && now - since >= CruisingSeconds;

        private bool BehindRear(in OtherCar c)
        {
            float hx = poses[0].Sin, hz = poses[0].Cos;
            var front = (c.X - poses[0].X) * hx + (c.Z - poses[0].Z) * hz + c.Length / 2f * ((float)Math.Sin(c.Heading) * hx + (float)Math.Cos(c.Heading) * hz);
            return front < -poses[0].HalfLength;
        }

        private bool AheadBy(float t, float ub) => corridor.Speed * t - corridor.Length >= ub;

        private bool JoinsBehind(int k)
        {
            for (var j = k + 1; j < count; j++)
            {
                if (!CoversSwept(j, out _, out _, out var sin, out var cos)) return false;
                if (sin * poses[j].Sin + cos * poses[j].Cos > 0.64f) return true;
            }
            return false;
        }

        private bool CommittedClear(in OtherCar c, int runStart, float[] times, float[] speeds, float margin, bool quick)
        {
            var from = float.MaxValue;
            var behindFrom = -1;
            clearMargin = float.MaxValue;
            clearsOutside = false;
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
                    if (AheadBy(early[k], ub) || followed.Contains(c.Id)) continue;
                    if (float.IsPositiveInfinity(t)) return StaysBehind(c, 0f, ua, null, null, 0, quick);
                    if (k > runStart && behindFrom == runStart) continue;
                    if (!StaysBehind(c, t, ua - corridor.Far(t), times, speeds, k, quick)) return false;
                    behindFrom = runStart;
                }
                else
                {
                    if (float.IsPositiveInfinity(t)) return false;
                    if (corridor.Near(Math.Max(0f, early[k] - margin)) - corridor.Length > ub) continue;
                    var spare = corridor.Reach(ua) - t;
                    clearMargin = Math.Min(clearMargin, spare);
                    if (spare <= margin) return false;
                }
            }
            return true;
        }

        private bool StopsForUs(in OtherCar c, int k, float ua)
        {
            if (ua <= 0f || c.TriggerLength <= 0f) return false;
            var t = safeLate[k];
            if (float.IsPositiveInfinity(t) || float.IsNaN(t)) return false;
            var margin = crossingWas.Contains(c.Id) ? StopsForUsMargin / 2f : StopsForUsMargin;
            var fastest = Math.Max(corridor.TopSpeed, c.Speed);
            if (fastest * TriggerReact + TriggerStopMetres + margin > c.TriggerLength) return false;
            var stop = Their(t) * TriggerReact + TriggerStopMetres + margin;
            if (ua - corridor.Far(t) < stop) return false;
            for (var j = k + 1; j < count; j++)
                if (CoversSwept(j, out var uj, out _, out _, out _) && uj < ua - stop) return false;
            crossingNow.Add(c.Id);
            return true;
        }

        private float Their(float t) => corridor.SpeedingUp ? Math.Min(corridor.TopSpeed, corridor.Speed + Corridor.Accel * t) : corridor.Speed;

        private bool StaysBehind(in OtherCar c, float t0, float gap, float[]? times, float[]? speeds, int k, bool quick)
        {
            var their = corridor.SpeedingUp ? Math.Min(corridor.TopSpeed, corridor.Speed + Corridor.Accel * t0) : corridor.Speed;
            var cruise = corridor.SpeedingUp ? corridor.TopSpeed : corridor.Speed;
            return Follower.StaysBehind(their, cruise, Corridor.Accel, c.TriggerLength, gap, t0, times, speeds, count, k, quick);
        }

        private bool AheadAlong(float ua)
        {
            for (var k = 3; k < Math.Min(count, 8); k++)
                if (CoversSwept(k, out var a, out _, out _, out _)) return a > ua + 1.5f;
            return false;
        }

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

        private float? Conflict(in OtherCar c, float front, int index)
        {
            var goesOn = false;
            var runStart = -1;
            var runFrom = float.MaxValue;
            var entry = -1;
            for (var k = 0; k < count; k++)
            {
                var standsAt = rearAt[k] + body.FrontAhead;
                if (standsAt > holdFront + SweepStep) break;
                if (!CoversSwept(k, out var ua, out var ub, out var sin, out var cos)) { runStart = -1; runFrom = float.MaxValue; continue; }
                if (runStart < 0) runStart = k;
                runFrom = Math.Min(runFrom, ua);
                var dot = sin * poses[k].Sin + cos * poses[k].Cos;
                var same = dot > 0.64f;
                if (k == 0 && ua > 0f && dot > -0.64f && (same || AheadAlong(ua)) && (!c.MayChangeLane || BehindRear(c))
                    && (slowSpeed[0] >= MovingMs || !BehindRear(c) || dot > LinedUp || Math.Max(0f, corridor.Speed * corridor.Speed - slowSpeed[0] * slowSpeed[0]) / (2f * FollowBrake) + FollowGap < ua)) return null;
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
                    if (AheadBy(early[k], ub)) conflict = false;
                    else if (comingUpBehind) conflict = false;
                }
                else if (conflict && dot > 0f && AheadBy(early[k], ub) && JoinsBehind(k)) conflict = false;
                if (!conflict && dot < -0.64f && k == count - 1 && ua >= 0f && HeadOnGoesOn()) conflict = true;
                if (!conflict) continue;
                if (goesOn || StopsForUs(c, k, ua)) { goesOn = true; continue; }
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
                    var room = stopAt - front;
                    var v = fastSpeed[0];
                    if (v > StandingMs && HardStop(v) > room)
                    {
                        deferred.Add((index, runStart, -1, runFrom, conflictNote));
                        deferNow = true;
                        return null;
                    }
                    return stopAt;
                }
                var exit = -1;
                for (var j = k + 1; j < count; j++)
                {
                    if (!CoversSwept(j, out var ja, out _, out _, out _)) { exit = j; break; }
                    runFrom = Math.Min(runFrom, ja);
                }
                deferred.Add((index, 0, exit, runFrom, conflictNote));
                deferNow = true;
                return null;
            }
            return null;
        }
    }
}
