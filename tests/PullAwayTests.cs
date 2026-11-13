using DrivenRides.Core.Drive;
using Xunit;

namespace DrivenRides.Core.Tests;

public sealed class PullAwayTests
{
    [Fact]
    public void SetsOffAsTheCarAheadDoes()
    {
        Assert.Equal(0f, PursuitDriver.Follow(PursuitDriver.StopGap, 0f));
        Assert.True(PursuitDriver.Follow(PursuitDriver.StopGap + 0.3f, 1f) > 0.4f);
        var room = 0.3f + 1f / (2f * PursuitDriver.LeaderBrake);
        Assert.True(room < 1f);
        Assert.True(PursuitDriver.Follow(PursuitDriver.StopGap + 0.6f, 1f) <= 1.5f + 1e-3f);
    }
}
