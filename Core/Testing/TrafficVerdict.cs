namespace DrivenRides.Core.Testing
{
    // T3's traffic invariants in the verdict (docs/implementation-plan.md, "Tests"; review round 1, A3/S3): a run that
    // the judge passed fails if the ride car caused a contact with a traffic car, ran a red it could have stopped for,
    // or stood 30 s away from a red. A contact another car caused (the ride car standing, or not moving towards it)
    // is an incident, not a failure. The rig applies it to each NWH run and tools/rejudge to recorded ones.
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

        // Who caused a contact, from both cars' velocities the physics step before it (the impact changes them: a car
        // stopped dead by it looked as if it had stood) and the contact's normal (review round 2, U2). Each car's speed
        // along the normal towards the other: the other car caused it only if it was coming on (over 0.5 m/s) and the
        // ride car was not (under 0.3 m/s). Anything less clear counts as the ride car's. The normal's sign doesn't
        // matter: it is turned to point from the ride car's centre towards the other car's.
        public static bool CausedByRide(float ourVx, float ourVz, float ourX, float ourZ, float theirVx, float theirVz, float theirX, float theirZ, float normalX, float normalZ)
        {
            var length = (float)System.Math.Sqrt(normalX * normalX + normalZ * normalZ);
            if (length < 1e-3f) { normalX = theirX - ourX; normalZ = theirZ - ourZ; length = (float)System.Math.Sqrt(normalX * normalX + normalZ * normalZ); }
            if (length < 1e-3f) return true;
            normalX /= length;
            normalZ /= length;
            if (normalX * (theirX - ourX) + normalZ * (theirZ - ourZ) < 0f) { normalX = -normalX; normalZ = -normalZ; }
            var ours = ourVx * normalX + ourVz * normalZ;
            var theirs = -(theirVx * normalX + theirVz * normalZ);
            return !(theirs > 0.5f && ours < 0.3f);
        }
    }
}
