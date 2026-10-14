using System;
using System.Collections.Generic;
using System.IO;
using Xunit;
using Xunit.Abstractions;

namespace DrivenRides.Core.Tests;

// Phase 5, 2x and 3x ride speed: the ride sets Time.timeScale and leaves fixedDeltaTime alone, so the driver and the
// traffic rules see the same physics step at any speed (the simulation's Dt), and a rendered frame runs more of them.
// The decisions themselves don't change with speed; what does is their cost per frame. This measures it on the T0
// templates and the in-game dumps: each decision's cost, taken in sequence, summed over the physics steps a 60 fps
// frame holds at 1x, 2x and 3x (the game decides every step; the simulation every DecideEvery steps, so its samples
// stand in for consecutive steps).
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

    // The decisions in sequence, grouped into frames of speed * FrameSeconds of physics time each.
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
