using DrivenRides.Core.Routing;

namespace DrivenRides.Core.Ride
{
    public enum TaxiStartKind { GleyTarget, AheadOfCab, Vanilla }

    // Where a hailed taxi's ride leaves from. Normally Gley's target waypoint for the cab. A cab another mod has taken
    // out of Gley's hands (Real Life Overhaul's City Cabs drive a called taxi to the caller with Gley told to ignore it)
    // still has the target it was loaded with, out of sight and hundreds of metres back. Its ride car starts on a free
    // lane waypoint just ahead of the cab instead, as a parked private driver's does. Only a ride with the car's own
    // physics can take it over that way: a Gley ride would need Gley to drive the cab again, so it is left to the game.
    public static class TaxiStart
    {
        // The waypoint search: from this far ahead of the cab, this far round, for a lane running at most 60 degrees
        // off the cab's heading.
        public const float SearchAheadMetres = 4f, SearchRadiusMetres = 30f, MinAlignment = 0.5f;

        public static TaxiStartKind Choose(bool ignoredByGley, bool carPhysics) =>
            !ignoredByGley ? TaxiStartKind.GleyTarget : carPhysics ? TaxiStartKind.AheadOfCab : TaxiStartKind.Vanilla;

        // A waypoint in front of the cab whose lane runs the cab's way. Headings are compared flat.
        public static bool IsAhead(Vec3 cab, Vec3 cabForward, Vec3 waypoint, Vec3 laneForward)
        {
            var toWaypoint = waypoint - cab;
            if (Flat(toWaypoint, cabForward) <= 0f) return false;
            var cabLength = Length(cabForward);
            var laneLength = Length(laneForward);
            if (cabLength < 1e-4f || laneLength < 1e-4f) return false;
            return Flat(laneForward, cabForward) / (cabLength * laneLength) > MinAlignment;
        }

        private static float Flat(Vec3 a, Vec3 b) => a.X * b.X + a.Z * b.Z;
        private static float Length(Vec3 a) => (float)System.Math.Sqrt(Flat(a, a));
    }
}
