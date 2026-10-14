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
        // Never faster than the car ahead plus a little, nor than StartMs, while the braking rule says less.
        Assert.True(PursuitDriver.Follow(PursuitDriver.StopGap + 2f, 2f) <= 2.5f + 1e-3f);
        Assert.True(PursuitDriver.Follow(PursuitDriver.StopGap + 0.1f, 0.2f) <= 0.01f);
    }
}
