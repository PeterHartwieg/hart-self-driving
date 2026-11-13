using DrivenRides.Core.Routing;

namespace DrivenRides.Core.Ride
{
    public enum TaxiStartKind { GleyTarget, AheadOfCab, Vanilla }

    public static class TaxiStart
    {
        public const float SearchAheadMetres = 4f, SearchRadiusMetres = 30f, MinAlignment = 0.5f;

        public static TaxiStartKind Choose(bool ignoredByGley, bool carPhysics) =>
            !ignoredByGley ? TaxiStartKind.GleyTarget : carPhysics ? TaxiStartKind.AheadOfCab : TaxiStartKind.Vanilla;

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
