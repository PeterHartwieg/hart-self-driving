using DrivenRides.Core.Drive;
using Xunit;

namespace DrivenRides.Core.Tests;

public sealed class FollowerTests
{
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
        var speeds = new float[17];
        for (var k = 0; k <= 16; k++) speeds[k] = 8f * (float)System.Math.Sqrt(System.Math.Max(0f, 1f - k / 16f));
        var (times, s) = Trajectory(speeds);
        Assert.False(Follower.StaysBehind(8f, 8f, 2f, 1.5f, 6f, 0f, times, s, s.Length, 0, false));
        Assert.True(Follower.StaysBehind(8f, 8f, 2f, 12f, 6f, 0f, times, s, s.Length, 0, false));
    }

    [Fact]
    public void ASlowCatchUpIsFollowedToTheTrigger()
    {
        var (times, s) = Trajectory(5f, 5f, 5f);
        Assert.True(Follower.StaysBehind(5.5f, 5.5f, 2f, 3f, 20f, 0f, times, s, s.Length, 0, false));
        Assert.False(Follower.StaysBehind(15f, 15f, 2f, 1.5f, 200f, 0f, times, s, s.Length, 0, false));
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
