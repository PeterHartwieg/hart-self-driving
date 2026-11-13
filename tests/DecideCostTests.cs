using System;
using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace DrivenRides.Core.Tests;

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
        var summary = $"{decides} decisions, {bytes} bytes, {allocating} allocating with the same reason";
        Assert.True(bytes / (double)decides < 20.0, $"{(double)bytes / decides:0.0} bytes a decision ({summary})");
        Assert.True(allocating < decides / 50, $"{allocating} of {decides} decisions allocated with the same reason ({summary})");
    }
}
