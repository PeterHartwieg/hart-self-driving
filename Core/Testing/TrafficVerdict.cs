namespace DrivenRides.Core.Testing
{
    // T3's traffic invariants in the verdict (docs/implementation-plan.md, "Tests"; review round 1, A3/S3): a run that
    // the judge passed fails if the ride car caused a contact with a traffic car, ran a red it could have stopped for,
    // or stood 30 s away from a red. A contact another car caused (one that moved sideways into the ride car) is an
    // incident, not a failure. The rig applies it to each NWH run and tools/rejudge to recorded ones.
    public static class TrafficVerdict
    {
        public static void Apply(JudgeResult result, int contactsCaused, int contactsTaken, int redRuns, int stalls)
        {
            if (contactsTaken > 0) result.Incidents.Add($"traffic-contact-taken x{contactsTaken}");
            var failed = false;
            if (contactsCaused > 0) { result.Reasons.Add("traffic-contact"); failed = true; }
            if (redRuns > 0) { result.Reasons.Add("red-run"); failed = true; }
            if (stalls > 0) { result.Reasons.Add("standstill"); failed = true; }
            if (failed && result.Verdict == "pass") result.Verdict = "fail";
        }

        // Who caused a contact, from both cars' velocities the physics step before it (the impact changes them) and
        // headings. The other car's only when it moved sideways into the ride car (review round 9: Gley changes lanes
        // and merges without looking; Peter crashes there driving himself): going the same way (within the angle the
        // traffic rules count as the same way), coming across the two cars' mean heading towards it (over 0.1 m/s), the
        // ride car not (0.1 m/s at most). Everything else, the ride car's moving into a car,
        // following and junctions included, counts as the ride car's.
        public static bool CausedByRide(float ourVx, float ourVz, float ourHeading, float ourX, float ourZ, float theirVx, float theirVz, float theirHeading, float theirX, float theirZ)
        {
            var sameWay = System.Math.Cos(theirHeading - ourHeading) > 0.64;
            // Across the two cars' mean heading, towards the other car: each one's own turn counts as its sideways move.
            var mean = System.Math.Atan2(System.Math.Sin(ourHeading) + System.Math.Sin(theirHeading), System.Math.Cos(ourHeading) + System.Math.Cos(theirHeading));
            float rx = (float)System.Math.Cos(mean), rz = -(float)System.Math.Sin(mean);
            if ((theirX - ourX) * rx + (theirZ - ourZ) * rz < 0f) { rx = -rx; rz = -rz; }
            var theirs = -(theirVx * rx + theirVz * rz);
            var ours = ourVx * rx + ourVz * rz;
            return !(sameWay && theirs > 0.1f && ours <= 0.1f);
        }
    }
}
