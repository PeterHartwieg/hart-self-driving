using System;

namespace DrivenRides.Core.Drive
{
    // A ride's slowdowns, one line each (Peter, 14 October: "when turning it sometimes brakes for no obvious reason for
    // a second or two"; a real ride writes no per-step trace). A slowdown is the speed falling Drop below its highest of
    // the last PeakSeconds; it ends when the speed is back up by Recover, or after MaxSeconds (then it waited). The
    // line has the speeds, how long it took, how far the wheel was turned, and the traffic decision's reason when the
    // speed had fallen Onset (with its note: the car, the pose, the times; none: the plan's own speed).
    public sealed class BrakeEvents
    {
        public const float Drop = 2f, Recover = 1.5f, MaxSeconds = 10f, PeakSeconds = 3f, Onset = 0.5f;

        private float peak = -1f, peakAt, low, lowAt, wheel;
        private bool open;
        private string reason = "";
        private YieldNote note;
        private bool red, caught;

        // The slowdown the last line was about, for the simulation's counts.
        public (float Peak, float Low, float Seconds, bool Back, float Wheel, string Reason, YieldNoteKind Kind, int Car, bool Red) Last { get; private set; }

        // One physics step: the time, metres along the line, the speed, the wheel's angle (degrees), the decision's
        // reason and note, and whether a red holds the car. A line when a slowdown ends, else null; nothing allocated
        // otherwise (it runs every physics step).
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
