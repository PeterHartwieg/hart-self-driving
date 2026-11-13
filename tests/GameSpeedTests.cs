using System;
using System.Collections.Generic;
using System.IO;
using Xunit;
using Xunit.Abstractions;

namespace DrivenRides.Core.Tests;

[Collection(nameof(DecideCostTests))]
public sealed class GameSpeedTests
{
    private readonly ITestOutputHelper output;
    public GameSpeedTests(ITestOutputHelper output) => this.output = output;

    private const float FrameSeconds = 1f / 60f;
    // A frame's decisions at 3x stay under half a frame (16.7 ms at 60 fps). Loose on purpose: a shared
    // test machine is noisy, and in game NwhDrive logs the real numbers per ride.
    private const float MaxP99FrameMicros = 8000f;

    [Fact]
    public void DecisionCostPerFrameAtRideSpeeds()
    {
        foreach (var name in TrafficTemplates.Names) TrafficTests.Run(TrafficTemplates.Build(name, 100));
        var micros = new List<float>();
        foreach (var name in TrafficTemplates.Names)
            for (var seed = 0; seed < 8; seed++) micros.AddRange(TrafficTests.Run(TrafficTemplates.Build(name, seed)).DecideMicros);
        var folder = TrafficScenarioJson.Folder();
        if (folder != null)
            foreach (var file in Directory.GetFiles(folder, "*.json"))
                micros.AddRange(TrafficTests.Run(TrafficScenarioJson.Load(File.ReadAllText(file))).DecideMicros);
        Assert.NotEmpty(micros);

        foreach (var speed in new[] { 1f, 2f, 3f })
        {
            var frames = PerFrame(micros, speed);
            frames.Sort();
            var p50 = frames[frames.Count / 2];
            var p99 = frames[frames.Count * 99 / 100];
            output.WriteLine($"{speed:0}x: {speed * FrameSeconds / TrafficSim.Dt:0.00} physics steps a frame at 60 fps; decisions per frame p50 {p50:0} µs, p99 {p99:0} µs, max {frames[^1]:0} µs over {frames.Count} frames");
            Assert.True(p99 < MaxP99FrameMicros, $"{speed:0}x: p99 {p99:0} µs a frame");
        }
    }

    private static List<float> PerFrame(List<float> decisions, float speed)
    {
        var frames = new List<float>();
        var stepsPerFrame = speed * FrameSeconds / TrafficSim.Dt;
        var owed = 0f;
        var k = 0;
        while (k < decisions.Count)
        {
            owed += stepsPerFrame;
            var sum = 0f;
            while (owed >= 1f && k < decisions.Count) { sum += decisions[k++]; owed -= 1f; }
            frames.Add(sum);
        }
        return frames;
    }
}
