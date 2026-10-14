using System;
using System.Collections.Generic;

namespace DrivenRides.Core.Drive
{
    // The driver's side of the way on (NwhDrive; review rounds 2 and 3): it starts a look-ahead about once a second,
    // steps the planner's jobs a time budget each physics step, holds the car short of a step the planner says it must
    // not enter, and takes up a way on. What it says to do each step is a Result.
    //
    // The hold is a fixed point, set once (review round 3: the stop was moved on to where the car could stop
    // comfortably, past the step). The planner gives the limit the front must not pass: the start of a step, the
    // obstacle in a lane, the ramp of a sideways move. The front stops HoldShort before it if the car can at
    // PursuitDriver.Decel; if not, where Decel braking from now ends, if that is still short of the limit (review
    // round 4: a hold on the step the car was on fell behind its front and was taken as an emergency); else at the
    // limit, braking as hard as it takes. If that needs more than HardDecel, or the car stops past the limit, the step
    // is given up as blocked (the driver backs off and plans round it), never driven into. With no way round after
    // HoldSeconds standing at the hold, the drive ends.
    public sealed class WayOnControl
    {
        // The planner's jobs: the look-ahead (path position of the front, the car's pose), the way round a blocked step
        // (with what blocked it), and taking up a finished job's way on (the ride's route and the judge's; the plan to
        // drive, or null).
        public Func<int, Func<CarPose>, WayOnJob?>? LookAhead;
        public Func<int, string, Func<CarPose>, WayOnJob?>? Replan;
        public Func<WayOnJob, DrivePlan?>? Take;
        public Action<string>? Log;

        // Planning time per physics step (review round 2: a replan ran whole inside one FixedUpdate).
        public double BudgetMs = 3.0;
        public const float LookSeconds = 1f;
        public const float HoldShort = 2f;
        public const float HoldSeconds = 10f;
        // Stopped this far past the hold: the car is in the step.
        public const float PastHold = 0.3f;

        public enum Act { Drive, Wait, Restart, Block, End }

        public readonly struct Result
        {
            public readonly Act Act;
            public readonly DrivePlan? Plan;
            public readonly string Why;
            // For Block: the path position of the step given up (the one held short of).
            public readonly int Position;

            public Result(Act act, DrivePlan? plan = null, string why = "", int position = -1) { Act = act; Plan = plan; Why = why; Position = position; }
        }

        // Metres along the line where the front must stop for the planner, or null; why.
        public float? HoldAlong { get; private set; }
        public string HoldWhat { get; private set; } = "";
        public int HoldPosition { get; private set; } = -1;
        public WayOnJob? Job => job;

        private WayOnJob? job;
        private float nextLook;
        private float heldSeconds;
        private bool cannotStop;
        private float holdLimit;

        // A new line is driven (a way on taken up) or the car was found blocked: what was planned for the line it
        // drove goes, a job under way included (its pose and path are the old ones).
        public void Reset()
        {
            job = null;
            HoldAlong = null;
            HoldWhat = "";
            HoldPosition = -1;
            heldSeconds = 0f;
            cannotStop = false;
        }

        // Each physics step while the car drives (not blocked, not backing off), before the traffic decision, so a hold
        // set now is in it. position: the path position the front has reached; alongOf: each path position's metres
        // along the line; front: the front's metres along it; speed: m/s along the heading.
        public Result Ahead(float time, float dt, int position, IReadOnlyList<float> alongOf, float front, float speed, Func<CarPose> pose)
        {
            if (LookAhead != null && job == null && time >= nextLook)
            {
                nextLook = time + LookSeconds;
                try { job = LookAhead(position, pose); }
                catch (Exception ex) { Log?.Invoke("look-ahead: " + ex); job = null; }
            }
            if (job != null && job.Kind == "ahead")
            {
                var done = StepJob();
                // A hold the job has found is kept from the moment it is found, nearer ones replacing farther ones.
                if (job != null && job.HoldPosition >= 0) HoldAt(alongOf, job.HoldPosition, job.HoldOffset, job.HoldWhat, front, speed, nearerOnly: true);
                if (done && job != null)
                {
                    var finished = job;
                    job = null;
                    if (finished.Found != null)
                    {
                        DrivePlan? plan = null;
                        try { plan = Take?.Invoke(finished); } catch (Exception ex) { Log?.Invoke("take: " + ex); }
                        if (plan != null && plan.Path.Count >= 2) { Reset(); return new Result(Act.Restart, plan, finished.Found.Value.Note); }
                    }
                    // Its last word on the hold: where the plan driven now must stop, or nowhere.
                    if (finished.HoldPosition >= 0) HoldAt(alongOf, finished.HoldPosition, finished.HoldOffset, finished.HoldWhat, front, speed, nearerOnly: false);
                    else Release();
                }
            }
            if (HoldAlong is float at)
            {
                var stopped = Math.Abs(speed) < 0.2f;
                if (stopped && (cannotStop || front > holdLimit + PastHold))
                {
                    var why = $"could not stop short of {HoldWhat}";
                    var step = HoldPosition;
                    Reset();
                    return new Result(Act.Block, null, why, step);
                }
                heldSeconds = stopped ? heldSeconds + dt : 0f;
                if (heldSeconds > HoldSeconds && job == null) return new Result(Act.End, null, "blocked ahead: " + HoldWhat);
            }
            return new Result(Act.Drive);
        }

        // Each physics step once the car stands after backing off from a blocked step: the way round it planned over
        // the next steps (Wait meanwhile), then taken up (Restart) or, with none, the end of the drive.
        public Result Blocked(int position, string what, Func<CarPose> pose)
        {
            if (job == null || job.Kind != "replanned")
            {
                try { job = Replan?.Invoke(position, what, pose); }
                catch (Exception ex) { Log?.Invoke("replan: " + ex); job = null; }
                if (job == null) return new Result(Act.End, null, "blocked: " + what);
            }
            if (!StepJob()) return new Result(Act.Wait);
            if (job == null) return new Result(Act.End, null, "blocked: " + what);
            var finished = job;
            job = null;
            DrivePlan? plan = null;
            try { plan = Take?.Invoke(finished); } catch (Exception ex) { Log?.Invoke("take: " + ex); }
            Log?.Invoke($"planned in {finished.Milliseconds:0} ms over {finished.Slices} physics steps, the longest {finished.LongestSlice:0.0} ms");
            if (plan == null || plan.Path.Count < 2) return new Result(Act.End, null, "blocked: " + what);
            Reset();
            return new Result(Act.Restart, plan, finished.Found?.Note ?? "");
        }

        // The front stops short of the limit `offset` metres on from path position `at` (see the class).
        private void HoldAt(IReadOnlyList<float> alongOf, int at, float offset, string what, float front, float speed, bool nearerOnly)
        {
            if (at < 0 || at >= alongOf.Count) return;
            var limit = alongOf[at] + offset;
            if (nearerOnly && HoldAlong != null && holdLimit <= limit + 0.01f) return;
            if (HoldAlong != null && Math.Abs(holdLimit - limit) <= 0.01f) { HoldWhat = what; return; }
            var v = Math.Max(0f, speed);
            var gentle = front + v * v / (2f * PursuitDriver.Decel) + 0.2f;
            float stop;
            if (limit - HoldShort >= gentle) { stop = limit - HoldShort; cannotStop = false; }
            else if (limit - PastHold >= gentle) { stop = gentle; cannotStop = false; }
            else
            {
                stop = limit - PastHold;
                var room = stop - front;
                cannotStop = room <= 0f || v * v / (2f * room) > PursuitDriver.HardDecel;
            }
            holdLimit = limit;
            HoldAlong = stop;
            HoldWhat = what;
            HoldPosition = at;
            Log?.Invoke($"holding short of {what}: limit {limit:0.0} m, stop {stop:0.0} m, front {front:0.0} m at {v:0.0} m/s{(cannotStop ? $", more than {PursuitDriver.HardDecel:0} m/s² to stop: given up once standing" : "")}.");
        }

        private void Release()
        {
            if (HoldAlong != null) Log?.Invoke($"hold short of {HoldWhat} released.");
            HoldAlong = null;
            HoldWhat = "";
            HoldPosition = -1;
            heldSeconds = 0f;
            cannotStop = false;
        }

        // One physics step's share of the job; true when it is done. A job that throws is dropped and counts as done.
        private bool StepJob()
        {
            if (job == null) return true;
            try { return job.Step(BudgetMs); }
            catch (Exception ex)
            {
                Log?.Invoke("planning: " + ex);
                job = null;
                return true;
            }
        }
    }
}
