namespace DrivenRides.Core.Drive
{
    // Every margin the traffic rules keep, in one place (review round 9: Peter, "it's a game after all"). Measured
    // facts about the cars (brake latency, launch delay, Gley's reaction, the model's error) stay where they are; these
    // are the choices of how much room to leave. The planner's kerb margin is not here: it keeps the car off the
    // pavement, not off other cars.
    public static class TrafficMargins
    {
        // Following a moving car (PursuitDriver.Follow): how long after the car ahead starts braking the ride car
        // brakes too, how hard each is taken to brake, and the gap left when both stand. Together they set the
        // headway: about 1.3 s behind a car at 10 m/s with 0.6 s.
        public const float FollowReaction = 0.33f;
        public const float FollowBrake = 3f;
        public const float LeaderBrake = 4f;
        public const float StopGap = 2.5f;

        // How early the ride car slows for a stop, a light or a car given way to: the braking it plans with (m/s²).
        public const float Decel = 3f;

        // Gap acceptance at give-ways, merges and crossings: the time either side of the ride car's own passage that
        // a traffic car must keep clear of it, and how much longer when pulling away from a standstill.
        public const float Headway = 0.8f;
        public const float StartHeadway = 0.55f;

        // Room round the ride car's body when its way is checked against a moving car's: sideways and lengthways (m).
        public const float SideMargin = 0.3f;
        public const float EndMargin = 0.35f;

        // A car the ride car joins in front of must stay behind it braking at no more than this (m/s²), with this gap.
        public const float JoinBrake = 3f;
        public const float JoinGap = 1.4f;

        // How long a car given way to stays a conflict once the check stops finding one (s).
        public const float ConflictMemory = 0f;

        // The emergency rule's floor: the least time a continuation past a car's way may have to spare, in seconds
        // plus metres of the body's position at the speed (TrafficDriver.MarginFloor).
        public const float FloorSeconds = 0.25f;
        public const float FloorMetres = 0.3f;

        // The stopping reserve: how far short of a car's way the ride car keeps the option to stop (m), and how many of
        // the brakes' latencies at its speed on top (TrafficDriver.StopShort).
        public const float ReserveMetres = 0.3f;
        public const float ReserveLatencies = 1.5f;
    }
}
