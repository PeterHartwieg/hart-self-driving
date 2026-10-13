using System;
using System.Collections.Generic;

namespace DrivenRides.Core.Drive
{
    // The way back from a blocked step (NwhDrive's blocked rule): a short, slow reverse along the line while there is
    // room behind, then the step is given up and the planner asked for another way. The first version reversed for
    // 1.5 s whatever stood behind (review round 1: a car against a kerb could back into a following car or a person).
    // Room is the traffic back-off's: nothing the adapter sees behind within RoomBehind (parked cars, people, Gley's
    // cars on their layer) and no traffic car's body within RoomBehind of the rear. No room: the car holds where it is.
    public sealed class Recovery
    {
        // At most this far back, no faster than this, and no longer than this.
        public const float MaxMetres = 2f;
        public const float MaxMs = 1f;
        public const float MaxSeconds = 4f;

        public enum Step { Reverse, Brake, Done }

        private float startX, startZ, seconds;
        private bool started;

        public float Backed { get; private set; }
        // Why it stopped backing: "" while it goes on.
        public string Ended { get; private set; } = "";

        // One physics step. Body centre and heading (radians, as atan2(dx, dz)), the body's half width and half length,
        // the speed along the heading (negative backwards), the adapter's gap behind the rear bumper, the traffic cars.
        public Step Next(float centreX, float centreZ, float heading, float halfWidth, float halfLength, float speedMs,
            float? gapBehind, IReadOnlyList<OtherCar> cars, float dt)
        {
            if (!started) { started = true; startX = centreX; startZ = centreZ; }
            seconds += dt;
            Backed = (float)Math.Sqrt((centreX - startX) * (centreX - startX) + (centreZ - startZ) * (centreZ - startZ));
            if (Backed >= MaxMetres) return Finish("backed off " + MaxMetres.ToString("0") + " m");
            if (seconds >= MaxSeconds) return Finish("backing time used up");
            if (!RoomBehind(centreX, centreZ, heading, halfWidth, halfLength, gapBehind, cars)) return Finish(Backed < 0.05f ? "no room behind; held" : "no more room behind");
            return -speedMs > MaxMs ? Step.Brake : Step.Reverse;
        }

        private Step Finish(string why)
        {
            if (Ended.Length == 0) Ended = why;
            return Step.Done;
        }

        public static bool RoomBehind(float centreX, float centreZ, float heading, float halfWidth, float halfLength, float? gapBehind, IReadOnlyList<OtherCar> cars)
        {
            if (gapBehind is float g && g < TrafficDriver.RoomBehind) return false;
            float hx = (float)Math.Sin(heading), hz = (float)Math.Cos(heading);
            var behind = new Box(centreX - hx * TrafficDriver.RoomBehind, centreZ - hz * TrafficDriver.RoomBehind, heading, halfWidth, halfLength);
            foreach (var c in cars)
            {
                if ((c.X - centreX) * hx + (c.Z - centreZ) * hz > 0f) continue;
                if (behind.Overlaps(TrafficDriver.BodyOf(c))) return false;
            }
            return true;
        }
    }
}
