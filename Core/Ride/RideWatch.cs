using System;

namespace DrivenRides.Core.Ride
{
    public enum WatchVerdict { Ok, Restart, Replan, GiveUp }

    public sealed class WatchLimits
    {
        public double RestartAfterSeconds = 15, RestartProgressMetres = 2;
        public double ReplanAfterSeconds = 30, ReplanProgressMetres = 5;
        public double GiveUpAfterReplanSeconds = 30;
        public double DeadlineFactor = 3, DeadlineExtraSeconds = 60;
    }

    public sealed class RideWatch
    {
        private readonly double start, deadlineDuration;
        private readonly double restartSeconds, restartMetres, replanSeconds, replanMetres, giveUpSeconds;
        private double restartTime, replanTime, restartAlong, replanAlong, along, lastTime;
        private bool replanned;
        private bool gaveUp;

        public RideWatch(double startSeconds, double expectedDriveSeconds, WatchLimits? limits = null)
        {
            limits ??= new WatchLimits();
            Finite(startSeconds, nameof(startSeconds));
            Nonnegative(expectedDriveSeconds, nameof(expectedDriveSeconds));
            Positive(limits.RestartAfterSeconds, nameof(limits.RestartAfterSeconds));
            Positive(limits.RestartProgressMetres, nameof(limits.RestartProgressMetres));
            Positive(limits.ReplanAfterSeconds, nameof(limits.ReplanAfterSeconds));
            Positive(limits.ReplanProgressMetres, nameof(limits.ReplanProgressMetres));
            Positive(limits.GiveUpAfterReplanSeconds, nameof(limits.GiveUpAfterReplanSeconds));
            Nonnegative(limits.DeadlineFactor, nameof(limits.DeadlineFactor));
            Nonnegative(limits.DeadlineExtraSeconds, nameof(limits.DeadlineExtraSeconds));
            start = lastTime = restartTime = replanTime = startSeconds;
            deadlineDuration = limits.DeadlineFactor * expectedDriveSeconds + limits.DeadlineExtraSeconds;
            Finite(deadlineDuration, nameof(expectedDriveSeconds));
            restartSeconds = limits.RestartAfterSeconds;
            restartMetres = limits.RestartProgressMetres;
            replanSeconds = limits.ReplanAfterSeconds;
            replanMetres = limits.ReplanProgressMetres;
            giveUpSeconds = limits.GiveUpAfterReplanSeconds;
        }

        public string LastReason { get; private set; } = "";

        public WatchVerdict Update(double nowSeconds, double alongMetres)
        {
            CheckTime(nowSeconds);
            Nonnegative(alongMetres, nameof(alongMetres));
            if (gaveUp) return WatchVerdict.GiveUp;
            along = Math.Max(along, alongMetres);
            if (nowSeconds - start > deadlineDuration) return GiveUp("Ride deadline exceeded");
            if (replanned && nowSeconds - replanTime > giveUpSeconds) return GiveUp("No progress after replan");
            if (along - restartAlong >= restartMetres) { restartAlong = along; restartTime = nowSeconds; }
            if (along - replanAlong >= replanMetres)
            {
                replanAlong = along;
                replanTime = nowSeconds;
                replanned = false;
            }
            if (replanned && nowSeconds - replanTime >= giveUpSeconds) return GiveUp("No progress after replan");
            if (!replanned && nowSeconds - replanTime >= replanSeconds)
            {
                LastReason = "Progress stalled before replan limit";
                return WatchVerdict.Replan;
            }
            if (nowSeconds - restartTime >= restartSeconds)
            {
                // Returning a restart also starts its window, even if the adapter delays acknowledgement.
                restartTime = nowSeconds;
                LastReason = "Progress stalled before restart limit";
                return WatchVerdict.Restart;
            }
            LastReason = "";
            return WatchVerdict.Ok;
        }

        public void Restarted(double nowSeconds)
        {
            CheckTime(nowSeconds);
            restartTime = nowSeconds;
            restartAlong = along;
        }
        // The car is waiting for a red light, a give-way or the car in front: the stall clocks start again, the
        // ride deadline doesn't move.
        public void Waiting(double nowSeconds)
        {
            CheckTime(nowSeconds);
            restartTime = nowSeconds;
            replanTime = nowSeconds;
        }

        public void Replanned(double nowSeconds)
        {
            CheckTime(nowSeconds);
            replanTime = nowSeconds;
            replanAlong = along;
            replanned = true;
        }
        private WatchVerdict GiveUp(string reason)
        {
            gaveUp = true;
            LastReason = reason;
            return WatchVerdict.GiveUp;
        }
        private void CheckTime(double now)
        {
            Finite(now, nameof(now));
            if (now < lastTime) throw new ArgumentOutOfRangeException(nameof(now), "Watch time cannot decrease.");
            lastTime = now;
        }
        private static void Finite(double value, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentOutOfRangeException(name);
        }
        private static void Nonnegative(double value, string name) { Finite(value, name); if (value < 0) throw new ArgumentOutOfRangeException(name); }
        private static void Positive(double value, string name) { Nonnegative(value, name); if (value == 0) throw new ArgumentOutOfRangeException(name); }
    }
}
