namespace DrivenRides.Core.Drive
{
    // Gameplay margins: favour progress and shorter trips over avoiding every possible contact (Peter, 21 October).
    // Every margin the traffic rules keep, in one place. Measured
    // facts about the cars (brake latency, launch delay, Gley's reaction, the model's error) stay where they are; these
    // are the choices of how much room to leave. The planner's kerb margin is not here: it keeps the car off the
    // pavement, not off other cars.
    public static class TrafficMargins
    {
        public const float FollowReaction = 0.3f;
        public const float FollowBrake = 3.5f;
        public const float LeaderBrake = 4f;
        public const float StopGap = 2.1f;

        public const float Decel = 3f;

        public const float Headway = 0.6f;
        public const float StartHeadway = 0.3f;

        public const float SideMargin = 0.3f;
        public const float EndMargin = 0.35f;

        public const float JoinBrake = 4f;
        public const float JoinGap = 1.2f;

        public const float CreepMs = 4.5f;
        public const float SqueezeMs = 3f;

        public const float ConflictMemory = 0f;
    }
}
