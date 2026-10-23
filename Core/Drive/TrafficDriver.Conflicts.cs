using System;
using System.Collections.Generic;
using DrivenRides.Core.Routing;

namespace DrivenRides.Core.Drive
{
    // Conflicts operations. Fields remain in TrafficDriver.cs to preserve initialization order.
    public sealed partial class TrafficDriver
    {

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

        // A car whose front trigger will see the body and stop it short (Gley stops for anything in it within about a
        // metre: "stopped for this car in its trigger: 0.97 m and 0.40 s from 13.6 m/s"), ua metres ahead of its front.
        private static bool TriggerStops(in OtherCar c, float ua) => ua >= c.Speed * TriggerReact + TriggerStopMetres + 1f;

        // Where the car stops from v at Decel after the brakes' latency, with the uncertainty to spare.
        private static float StoppingRoom(float v) => v * BrakeLatency + v * v / (2f * PursuitDriver.Decel) + StopUncertainty;

        // Gaining speed at more than half a metre a second each second.
        private bool SpeedingUp(int id) => pace.TryGetValue(id, out var p) && p.Gain > 0.5f;

        // Seen at a steady pace (gaining or losing under SteadyGain) for CruisingSeconds: a slow car doing so is cruising,
        // not pulling away to the road's limit (review round 8: T0 head-on, cars crawling at 4 m/s for many seconds were
        // judged as if about to reach 15 m/s, and the ride car waited outside a one-lane stretch for half a minute).
        private bool Cruising(int id) => steadySince.TryGetValue(id, out var since) && now - since >= CruisingSeconds;

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

        // Gley stops a car for anything in its front trigger, from any speed, within about a metre (T3 and own-car logs:
        // "stopped for this car in its trigger: 0.97 m and 0.40 s from 13.6 m/s"; its trigger reaches 4.5 m from the
        // front wheels). A moving car whose way the body comes into at pose k, ua metres ahead of its front, stops short
        // of it if its trigger reaches past what it needs to stop from the speed it can have by then (TriggerReact,
        // TriggerStopMetres and StopsForUsMargin), and its front is no nearer than that when the body is there at the
        // latest. Once the ride car goes on in front of a car, half the margin is enough, so it doesn't flip back to
        // waiting half way as the estimates move (Peter, 14 October: "a lot of these weird stops
        // come from being overly careful with Gley cars ... they would always stop for us").
        private bool StopsForUs(in OtherCar c, int k, float ua)
        {
            if (ua <= 0f || c.TriggerLength <= 0f) return false;
            var t = safeLate[k];
            if (float.IsPositiveInfinity(t) || float.IsNaN(t)) return false;
            var margin = crossingWas.Contains(c.Id) ? StopsForUsMargin / 2f : StopsForUsMargin;
            // Its trigger must catch the body in time at any speed it can reach (a car speeding up after the ride car
            // went on must not outgrow it), and it must be far enough back at the speed it has by then.
            var fastest = Math.Max(corridor.TopSpeed, c.Speed);
            if (fastest * TriggerReact + TriggerStopMetres + margin > c.TriggerLength) return false;
            var stop = Their(t) * TriggerReact + TriggerStopMetres + margin;
            if (ua - corridor.Far(t) < stop) return false;
            // Where it stands then must not be on the ride car's way on: else each waits for the other (a car turning
            // across the ride car's path, stopped by its trigger in the junction, with the ride car behind it).
            for (var j = k + 1; j < count; j++)
                if (CoversSwept(j, out var uj, out _, out _, out _) && uj < ua - stop) return false;
            crossingNow.Add(c.Id);
            return true;
        }

        private float Their(float t) => corridor.SpeedingUp ? Math.Min(corridor.TopSpeed, corridor.Speed + Corridor.Accel * t) : corridor.Speed;

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

        // Layer 2 for one moving car whose corridor is built: where the front gives way, or null.
        private float? Conflict(in OtherCar c, float front, int index)
        {
            var goesOn = false;
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
                // Standing in its lane already, lined up with it (not nosing in at an angle), going on only takes the
                // ride car further ahead of it: never a reason to stay (Peter's own-car trip, 14 October: stopped in its
                // lane after a corner for cars coming up behind at 12 to 14 m/s).
                if (k == 0 && ua > 0f && dot > -0.64f && (same || AheadAlong(ua)) && (!c.MayChangeLane || BehindRear(c))
                    && (slowSpeed[0] >= MovingMs || !BehindRear(c) || dot > LinedUp || Math.Max(0f, corridor.Speed * corridor.Speed - slowSpeed[0] * slowSpeed[0]) / (2f * FollowBrake) + FollowGap < ua)) return null;
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
                    // One behind, in the lane the ride car joins: as any car whose way the body comes into (the
                    // window above, then StopsForUs below).
                }
                // Coming into its way at an angle behind it, as it goes on ahead, and on along its way after it: joining
                // its lane behind it, followed (Gap), not crossed (review round 9: swept where the car really goes, the
                // body cuts into a lane at an angle that the planned line only reached straightened out).
                else if (conflict && dot > 0f && AheadBy(early[k], ub) && JoinsBehind(k)) conflict = false;
                // Against its way along a stretch that goes on past the sweep: the ride car can't be out of it
                // before the car comes (head-on in one lane).
                if (!conflict && dot < -0.64f && k == count - 1 && ua >= 0f && HeadOnGoesOn()) conflict = true;
                if (!conflict) continue;
                // A moving car whose front trigger will catch the body in time and stop it short: no reason to wait
                // (Peter, 14 October: "they would always stop for us, so we should just go").
                // At any angle, head-on too: Peter, 14 October, on 0.1.111-go2, "it's now first stopping and then going
                // to cut into the path of an oncoming car. Why are we not just continuing". Where the stopped car would
                // stand on the ride car's way on, StopsForUs says no. The poses further on are still looked at, for where
                // the ride car must not come to a stand (keep clear).
                if (goesOn || StopsForUs(c, k, ua)) { goesOn = true; continue; }
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
    }
}
