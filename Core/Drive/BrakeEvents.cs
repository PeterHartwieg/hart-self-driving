using System;

namespace DrivenRides.Core.Drive
{
    public sealed class BrakeEvents
    {
        public const float Drop = 2f, Recover = 1.5f, MaxSeconds = 10f, PeakSeconds = 3f, Onset = 0.5f;

        private float peak = -1f, peakAt, low, lowAt, wheel;
        private bool open;
        private string reason = "";
        private YieldNote note;
        private bool red, caught;

        public (float Peak, float Low, float Seconds, bool Back, float Wheel, string Reason, YieldNoteKind Kind, int Car, bool Red) Last { get; private set; }

        public string? Step(float time, float along, float speed, float wheelDegrees, string why, in YieldNote yieldNote, bool redAhead)
        {
            if (!open)
            {
                if (peak < 0f || speed >= peak || time - peakAt > PeakSeconds)
                {
                    peak = speed;
                    peakAt = time;
                    reason = "";
                    note = default;
                    caught = false;
                    red = false;
                    wheel = 0f;
                }
                if (!caught && peak - speed >= Onset) { caught = true; reason = why; note = yieldNote; }
                red |= redAhead;
                wheel = Math.Max(wheel, Math.Abs(wheelDegrees));
                if (peak - speed < Drop) return null;
                open = true;
                low = speed;
                lowAt = time;
            }
            if (!caught) { caught = true; reason = why; note = yieldNote; }
            red |= redAhead;
            wheel = Math.Max(wheel, Math.Abs(wheelDegrees));
            if (speed < low) { low = speed; lowAt = time; }
            var back = speed >= low + Recover;
            if (!back && time - peakAt < MaxSeconds) return null;
            var line = $"slowdown at {along:0} m: {peak:0.0} to {low:0.0} m/s in {lowAt - peakAt:0.0} s, "
                + (back ? $"back up after {time - peakAt:0.0} s" : $"still under {low + Recover:0.0} m/s after {MaxSeconds:0} s")
                + $", wheel up to {wheel:0} degrees, for: {(reason.Length > 0 ? reason : "the plan's speed")}"
                + (note.Kind != YieldNoteKind.None ? $" ({note})" : "") + (red ? ", a red ahead" : "") + ".";
            Last = (peak, low, time - peakAt, back, wheel, reason, note.Kind, note.Car, red);
            open = false;
            peak = speed;
            peakAt = time;
            reason = "";
            note = default;
            caught = false;
            red = false;
            wheel = 0f;
            return line;
        }
    }
}
