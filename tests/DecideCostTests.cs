using System;
using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace DrivenRides.Core.Tests;

// Review round 1 (O7): TrafficDriver.Decide runs every physics step in game. Once warmed up it allocates nothing,
// so a ride makes no garbage for Unity's collector. Run on its own (review round 3, V6): the allocation counter is
// per thread, but the first calls' JIT work lands on it, so one run of every template warms up before measuring.
[CollectionDefinition(nameof(DecideCostTests), DisableParallelization = true)]
public sealed class DecideCostCollection { }

[Collection(nameof(DecideCostTests))]
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
        foreach (var name in TrafficTemplates.Names) TrafficTests.Run(TrafficTemplates.Build(name, 100));
        var watch = Stopwatch.StartNew();
        foreach (var name in TrafficTemplates.Names)
            for (var seed = 0; seed < 8; seed++)
            {
                var r = TrafficTests.Run(TrafficTemplates.Build(name, seed));
                bytes += r.DecideBytes;
                decides += r.Decides;
                allocating += r.DecidesAllocating;
            }
        var micros = new System.Collections.Generic.List<float>();
        foreach (var name in TrafficTemplates.Names)
            for (var seed = 0; seed < 8; seed++) micros.AddRange(TrafficTests.Run(TrafficTemplates.Build(name, seed)).DecideMicros);
        micros.Sort();
        var dumps = new System.Collections.Generic.List<float>();
        foreach (var file in System.IO.Directory.GetFiles(TrafficScenarioJson.Folder()!, "*.json"))
            dumps.AddRange(TrafficTests.Run(TrafficScenarioJson.Load(System.IO.File.ReadAllText(file))).DecideMicros);
        dumps.Sort();
        output.WriteLine($"dumps: p50 {dumps[dumps.Count / 2]:0} µs, p99 {dumps[dumps.Count * 99 / 100]:0} µs, max {dumps[^1]:0} µs over {dumps.Count} decisions");
        output.WriteLine($"decide: p50 {micros[micros.Count / 2]:0} µs, p99 {micros[micros.Count * 99 / 100]:0} µs, max {micros[^1]:0} µs");
        output.WriteLine($"{decides} decisions, {bytes} bytes allocated, {(double)bytes / decides:0.0} a decision, {allocating} decisions after the first second allocating; {watch.ElapsedMilliseconds} ms in all");
        // A decision allocates only a new reason's text (logged in game when it changes) and its buffers growing to
        // more cars than before; not per step. Before review round 1 it was 543 bytes a decision, every one.
        var summary = $"{decides} decisions, {bytes} bytes, {allocating} allocating with the same reason";
        Assert.True(bytes / (double)decides < 20.0, $"{(double)bytes / decides:0.0} bytes a decision ({summary})");
        Assert.True(allocating < decides / 50, $"{allocating} of {decides} decisions allocated with the same reason ({summary})");
    }
}
