using System;
using System.Collections.Generic;
using DrivenRides.Core.Routing;

namespace DrivenRides.Core.Drive
{
    // Sweep operations. Fields remain in TrafficDriver.cs to preserve initialization order.
    public sealed partial class TrafficDriver
    {

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
    }
}
