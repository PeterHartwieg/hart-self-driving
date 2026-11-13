using System;
using System.Collections.Generic;

namespace DrivenRides.Core.Drive
{
    public sealed class WayOnControl
    {
        public Func<int, Func<CarPose>, WayOnJob?>? LookAhead;
        public Func<int, string, Func<CarPose>, WayOnJob?>? Replan;
        public Func<WayOnJob, DrivePlan?>? Take;
        public Action<string>? Log;

        public double BudgetMs = 3.0;
        public const float LookSeconds = 1f;
        public const float HoldShort = 2f;
        public const float HoldSeconds = 10f;
        public const float PastHold = 0.3f;

        public enum Act { Drive, Wait, Restart, Block, End }

        public readonly struct Result
        {
            public readonly Act Act;
            public readonly DrivePlan? Plan;
            public readonly string Why;
            public readonly int Position;

            public Result(Act act, DrivePlan? plan = null, string why = "", int position = -1) { Act = act; Plan = plan; Why = why; Position = position; }
        }

        public float? HoldAlong { get; private set; }
        public string HoldWhat { get; private set; } = "";
        public int HoldPosition { get; private set; } = -1;
        public WayOnJob? Job => job;

        private WayOnJob? job;
        private float nextLook;
        private float heldSeconds;
        private bool cannotStop;
        private float holdLimit;

        public void Reset()
        {
            job = null;
            HoldAlong = null;
            HoldWhat = "";
            HoldPosition = -1;
            heldSeconds = 0f;
            cannotStop = false;
        }

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
