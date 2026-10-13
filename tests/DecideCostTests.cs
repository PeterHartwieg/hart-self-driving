using System;
using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace DrivenRides.Core.Tests;

// Review round 1 (O7): TrafficDriver.Decide runs every physics step in game. Once warmed up it allocates nothing,
// so a ride makes no garbage for Unity's collector.
public sealed class DecideCostTests
{
    private readonly ITestOutputHelper output;
    public DecideCostTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public void DecideAllocatesNothingPerStep()
    {
        long bytes = 0;
        var decides = 0;
        var allocating = 0;
        var watch = Stopwatch.StartNew();
        foreach (var name in TrafficTemplates.Names)
            for (var seed = 0; seed < 8; seed++)
            {
                var r = TrafficTests.Run(TrafficTemplates.Build(name, seed));
                bytes += r.DecideBytes;
                decides += r.Decides;
                allocating += r.DecidesAllocating;
            }
        output.WriteLine($"{decides} decisions, {bytes} bytes allocated, {(double)bytes / decides:0.0} a decision, {allocating} decisions after the first second allocating; {watch.ElapsedMilliseconds} ms in all");
        // A decision allocates only a new reason's text (logged in game when it changes) and its buffers growing to
        // more cars than before; not per step. Before review round 1 it was 543 bytes a decision, every one.
        Assert.True(bytes / (double)decides < 20.0, $"{(double)bytes / decides:0.0} bytes a decision");
        Assert.True(allocating < decides / 50, $"{allocating} of {decides} decisions allocated with the same reason");
    }
}
