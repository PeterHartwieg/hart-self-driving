using System;

namespace DrivenRides.Core.Ride
{
    public sealed class ClockBudget
    {
        public const double TimeMachineTolerance = 0.001;
        private readonly double maxPace, seedSpeed;
        private double seconds, metres;

        public ClockBudget(double startMinute, double budgetMinutes, double maxPace, double seedSpeedMs = 8.0, double seedSeconds = 10.0)
        {
            RequireFinite(startMinute, nameof(startMinute));
            RequireNonnegative(budgetMinutes, nameof(budgetMinutes));
            RequireNonnegative(maxPace, nameof(maxPace));
            RequireNonnegative(seedSpeedMs, nameof(seedSpeedMs));
            RequireNonnegative(seedSeconds, nameof(seedSeconds));
            if (seedSpeedMs == 0) throw new ArgumentOutOfRangeException(nameof(seedSpeedMs));
            StartMinute = startMinute;
            DeadlineMinute = startMinute + budgetMinutes;
            RequireFinite(DeadlineMinute, nameof(budgetMinutes));
            this.maxPace = maxPace;
            seedSpeed = seedSpeedMs;
            seconds = seedSeconds;
            metres = seedSpeedMs * seedSeconds;
            RequireFinite(metres, nameof(seedSeconds));
        }

        public double StartMinute { get; }
        public double DeadlineMinute { get; }
        public double AverageSpeedMs => seconds > 0 ? metres / seconds : seedSpeed;
        public double Left(double nowMinute)
        {
            RequireFinite(nowMinute, nameof(nowMinute));
            return DeadlineMinute - nowMinute;
        }

        public void Observe(double scaledDeltaSeconds, double metresDriven)
        {
            RequireNonnegative(scaledDeltaSeconds, nameof(scaledDeltaSeconds));
            RequireNonnegative(metresDriven, nameof(metresDriven));
            if (scaledDeltaSeconds == 0) return;
            seconds += scaledDeltaSeconds;
            metres += metresDriven;
        }

        public double Pace(double nowMinute, double metresLeft)
        {
            RequireFinite(metresLeft, nameof(metresLeft));
            double left = Left(nowMinute);
            double remaining = metresLeft <= 0 ? 0 : AverageSpeedMs > 0 ? metresLeft / AverageSpeedMs : double.PositiveInfinity;
            return remaining <= 0.001 ? maxPace : Math.Max(0, Math.Min(maxPace, left / remaining));
        }

        public double GameTick(double nowMinute, double scaledDeltaSeconds, double metresLeft)
        {
            RequireFinite(scaledDeltaSeconds, nameof(scaledDeltaSeconds));
            return CapGameTick(nowMinute, scaledDeltaSeconds * Pace(nowMinute, metresLeft));
        }
        public double CapGameTick(double nowMinute, double requested) => Cap(requested, Left(nowMinute));
        public double CapTimeMachineTick(double nowMinute, double requested) => Cap(requested, Left(nowMinute) + TimeMachineTolerance);

        private static double Cap(double requested, double left)
        {
            RequireFinite(requested, nameof(requested));
            return Math.Max(0, Math.Min(requested, left));
        }
        private static void RequireFinite(double value, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentOutOfRangeException(name);
        }
        private static void RequireNonnegative(double value, string name)
        {
            RequireFinite(value, name);
            if (value < 0) throw new ArgumentOutOfRangeException(name);
        }
    }
}
