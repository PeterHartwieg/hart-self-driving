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
    }
}
