using DrivenRides.Core.Drive;
using Xunit;

namespace DrivenRides.Core.Tests;

// Peter, 14 October: in a queue the ride car sets off with the car ahead, not once that car is a safe distance away.
public sealed class PullAwayTests
{
    [Fact]
    public void SetsOffAsTheCarAheadDoes()
    {
        // Standing at the standing gap: nothing while the car ahead stands, going once it moves.
        Assert.Equal(0f, PursuitDriver.Follow(PursuitDriver.StopGap, 0f));
        Assert.True(PursuitDriver.Follow(PursuitDriver.StopGap + 0.3f, 1f) > 0.4f);
        // Before: the braking rule alone kept it standing until the car ahead was further off.
        var room = 0.3f + 1f / (2f * PursuitDriver.LeaderBrake);
        Assert.True(room < 1f);
        // Setting off, no faster than the car ahead and half a metre a second more.
        Assert.True(PursuitDriver.Follow(PursuitDriver.StopGap + 0.6f, 1f) <= 1.5f + 1e-3f);
    }
}
