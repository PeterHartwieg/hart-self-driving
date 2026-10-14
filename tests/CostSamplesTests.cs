using DrivenRides.Core.Drive;
using Xunit;

namespace DrivenRides.Core.Tests;

public sealed class CostSamplesTests
{
    [Fact]
    public void PercentilesOfTheSamplesKept()
    {
        var c = new CostSamples();
        Assert.Equal(0f, c.Percentile(50));
        for (var k = 1; k <= 100; k++) c.Add(k);
        Assert.Equal(50f, c.Percentile(50));
        Assert.Equal(99f, c.Percentile(99));
        Assert.Equal(100f, c.Max);
    }

    [Fact]
    public void TheRingKeepsTheLatestSamples()
    {
        var c = new CostSamples();
        for (var k = 0; k < CostSamples.Capacity; k++) c.Add(1f);
        for (var k = 0; k < CostSamples.Capacity; k++) c.Add(2f);
        Assert.Equal(2 * CostSamples.Capacity, c.Total);
        Assert.Equal(2f, c.Percentile(1));
    }
}
