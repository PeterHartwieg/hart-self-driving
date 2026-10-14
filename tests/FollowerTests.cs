using DrivenRides.Core.Drive;
using Xunit;

namespace DrivenRides.Core.Tests;

// Review round 5 (Y4): the car behind is followed through in time, with no answer on a moment's equal speeds and none
// by running out of time.
public sealed class FollowerTests
{
    // The ride car's poses a metre apart, at the given speeds, with the times they make.
    private static (float[] Times, float[] Speeds) Trajectory(params float[] speeds)
    {
        var times = new float[speeds.Length];
        for (var k = 1; k < speeds.Length; k++)
            times[k] = speeds[k - 1] + speeds[k] < 0.1f ? float.PositiveInfinity : times[k - 1] + 2f / (speeds[k - 1] + speeds[k]);
        return (times, speeds);
    }

    [Fact]
    public void EqualSpeedsThenTheRideCarBrakingIsNotSafeWhenTheTriggerIsShort()
    {
        // Both at 8 m/s, 6 m apart; the ride car brakes to a stop over its next 16 m. A car with a 1.5 m trigger,
        // braking at its own brakes after its reaction, runs into it; before, the equal speeds said safe at once.
        var speeds = new float[17];
        for (var k = 0; k <= 16; k++) speeds[k] = 8f * (float)System.Math.Sqrt(System.Math.Max(0f, 1f - k / 16f));
        var (times, s) = Trajectory(speeds);
        Assert.False(Follower.StaysBehind(8f, 8f, 2f, 1.5f, 6f, 0f, times, s, s.Length, 0, false));
        // With a long trigger it stops in time.
        Assert.True(Follower.StaysBehind(8f, 8f, 2f, 12f, 6f, 0f, times, s, s.Length, 0, false));
    }

    // Review round 8: a car closing in slowly from far back is followed past the horizon to its trigger reaching the
    // body (both at a steady pace by then), not counted as unsafe for want of an answer (round 5 had it so, and a car
    // 100 m back flipped the cautious verdict on a merge to a stop the ride car could no longer make: T0 merge 1).
    [Fact]
    public void ASlowCatchUpIsFollowedToTheTrigger()
    {
        // The ride car keeps 5 m/s past its known times; the car behind cruises at 5.5 m/s and closes 0.5 m a second
        // from 20 m with a 3 m trigger: it reaches the trigger after 34 s, past the horizon (30 s), and stops in it.
        var (times, s) = Trajectory(5f, 5f, 5f);
        Assert.True(Follower.StaysBehind(5.5f, 5.5f, 2f, 3f, 20f, 0f, times, s, s.Length, 0, false));
        // Closing at 10 m/s with a 1.5 m trigger, however far back: it can't stop in it.
        Assert.False(Follower.StaysBehind(15f, 15f, 2f, 1.5f, 200f, 0f, times, s, s.Length, 0, false));
        // Faster than the car can go: safe for good.
        var (times2, s2) = Trajectory(6f, 6f, 6f);
        Assert.True(Follower.StaysBehind(5.5f, 5.5f, 2f, 3f, 20f, 0f, times2, s2, s2.Length, 0, false));
    }

    [Fact]
    public void AStandingBodyIsSafeOnlyWhereTheTriggerStopsTheCar()
    {
        Assert.True(Follower.StaysBehind(5f, 5f, 2f, 4f, 20f, 0f, null, null, 0, 0, true));
        Assert.False(Follower.StaysBehind(12f, 12f, 2f, 1.5f, 20f, 0f, null, null, 0, 0, true));
    }
}
