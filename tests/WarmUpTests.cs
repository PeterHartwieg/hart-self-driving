using System.Diagnostics;
using DrivenRides.Core.Drive;
using Xunit;
using Xunit.Abstractions;

namespace DrivenRides.Core.Tests;

[Collection(nameof(DecideCostTests))]
public sealed class WarmUpTests
{
    private readonly ITestOutputHelper output;
    public WarmUpTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public void WarmUpRunsTheTrafficDecisions()
    {
        var watch = Stopwatch.StartNew();
        Assert.Equal(600, TrafficWarmUp.Run());
        output.WriteLine($"first warm-up {watch.Elapsed.TotalMilliseconds:0.0} ms");
        watch.Restart();
        TrafficWarmUp.Run();
        output.WriteLine($"second warm-up {watch.Elapsed.TotalMilliseconds:0.0} ms");
    }
}
