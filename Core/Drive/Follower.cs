using System;

namespace DrivenRides.Core.Drive
{
    public static class Follower
    {
        public const float Step = 0.05f, Contact = 0.5f;
        public const int MaxSteps = 600;

        public static bool StaysBehind(float their, float cruise, float accel, float trigger, float gap, float t0,
            float[]? times, float[]? speeds, int count, int k, bool quick)
        {
            if (gap < Contact) return false;
            var react = quick ? TrafficDriver.TriggerReact : TrafficDriver.TriggerReactCautious;
            var braking = false;
            var stopLeft = TrafficDriver.TriggerStopMetres;
            var t = t0;
            var j = k;
            var known = times == null ? t0 : LastTime(times, count);
            for (var n = 0; n < MaxSteps; n++)
            {
                var ours = SpeedAt(t, times, speeds, count, ref j);
                // Both at a steady pace from here on (the ride car past its known times, the car at the most it goes),
                // the car closing in: skip to its trigger reaching the body. Else a car closing in slowly from far back
                // ran out of MaxSteps before its trigger got there and counted as not staying behind (review round 8:
                // T0 merge 1, a car 100 m back at 1.4 m/s faster flipped the cautious verdict to a stop the ride car
                // could no longer make).
                if (!braking && t > known && their >= cruise && their > ours && gap > trigger + Step * (their - ours))
                {
                    var skip = (gap - trigger) / (their - ours);
                    gap = trigger;
                    t += skip;
                }
                if (!braking && gap <= trigger) braking = true;
                if (braking)
                {
                    if (react > 0f) react -= Step;
                    else if (quick)
                    {
                        their = Math.Max(0f, their - their * their / (2f * Math.Max(0.05f, stopLeft)) * Step);
                        stopLeft = Math.Max(0f, stopLeft - their * Step);
                        if (stopLeft <= 0.05f) their = 0f;
                    }
                    else their = Math.Max(0f, their - TrafficDriver.TriggerBrake * Step);
                }
                else their = Math.Min(cruise, their + accel * Step);
                gap += (ours - their) * Step;
                if (gap < Contact) return false;
                if (braking && their <= 0f) return true;
                if (t > known && ours >= cruise) return true;
                t += Step;
            }
            return false;
        }

        public static float SpeedAt(float t, float[]? times, float[]? speeds, int count, ref int j)
        {
            if (times == null || speeds == null) return 0f;
            while (j + 1 < count && times[j + 1] <= t) j++;
            if (float.IsPositiveInfinity(times[j]) || t < times[j]) return 0f;
            if (j + 1 < count && !float.IsPositiveInfinity(times[j + 1]))
                return speeds[j] + (speeds[j + 1] - speeds[j]) * (t - times[j]) / Math.Max(1e-3f, times[j + 1] - times[j]);
            return j + 1 < count ? 0f : speeds[j];
        }

        public static float LastTime(float[] times, int count)
        {
            for (var k = count - 1; k >= 0; k--) if (!float.IsPositiveInfinity(times[k])) return times[k];
            return 0f;
        }
    }
}
