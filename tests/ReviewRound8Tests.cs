using System;
using System.Collections.Generic;
using System.Linq;
using DrivenRides.Core.Drive;
using DrivenRides.Core.Testing;
using Xunit;
using Xunit.Abstractions;

namespace DrivenRides.Core.Tests;

public sealed class ReviewRound8Tests
{
    private readonly ITestOutputHelper output;
    public ReviewRound8Tests(ITestOutputHelper output) => this.output = output;

    internal static TrafficScenario Crossing(float rideMs, float aAt, float bAt, float aPace = 0.9f, float bPace = 0.9f, bool bFirst = false)
    {
        var b = new ScenarioBuilder();
        var ride = b.Lane(0f, -80f, 0f, 80f);
        var laneA = b.Lane(-70f, 12f, 150f, 12f);
        var laneB = b.Lane(70f, 17f, -150f, 17f);
        b.Ride(ride, RideCar.Dash with { StartMs = rideMs });
        if (bFirst) b.Car(bAt, laneB, bPace);
        b.Car(aAt, laneA, aPace);
        if (!bFirst) b.Car(bAt, laneB, bPace);
        b.S.Name = $"crossing-{rideMs}-{aAt}-{bAt}";
        b.S.Seconds = 40f;
        return b.S;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CrossingTwoLanesAcrossASweepOfTimes(bool bFirst)
    {
        var runs = 0;
        var gave = 0;
        var bad = new List<string>();
        var step = TrafficTests.Full ? 0.4f : 1.2f;
        foreach (var rideMs in new[] { 6f, 9f, 12f })
            for (var aAt = 0f; aAt <= 8f; aAt += step)
                for (var bAt = 0f; bAt <= 8f; bAt += step)
                {
                    var r = TrafficTests.Run(Crossing(rideMs, aAt, bAt, bFirst: bFirst));
                    runs++;
                    if (r.GaveWayTo.Count > 0) gave++;
                    if (r.Failure != null || !r.Arrived || r.NotEmergency != null)
                        bad.Add($"{rideMs} m/s, A at {aAt:0.0} s, B at {bAt:0.0} s: {r}; {r.NotEmergency}");
                }
        output.WriteLine($"{runs} runs, {gave} gave way, {bad.Count} bad");
        foreach (var l in bad.Take(10)) output.WriteLine(l);
        Assert.Empty(bad);
        Assert.True(gave > runs / 4, $"only {gave} of {runs} runs gave way: the sweep doesn't exercise the crossing");
    }

    [Fact]
    public void MergesAcrossASweepOfStartSpeeds()
    {
        var bad = new List<string>();
        var runs = 0;
        foreach (var seed in new[] { 1, 106, 121, 130, 135 })
            for (var dv = -1.5f; dv <= 1.5f; dv += 0.25f)
            {
                var sc = TrafficTemplates.Build("merge", seed);
                sc.Ride.Speed = Math.Max(0f, sc.Ride.Speed + dv);
                var r = TrafficTests.Run(sc);
                runs++;
                if (r.Failure != null || !r.Arrived || r.NotEmergency != null) bad.Add($"merge {seed} {dv:+0.00;-0.00}: {r}; {r.NotEmergency}");
            }
        output.WriteLine($"{runs} runs, {bad.Count} bad");
        foreach (var l in bad.Take(10)) output.WriteLine(l);
        Assert.Empty(bad);
    }

    [Fact]
    public void AnUnsettledPassIsReplacedByTheEmergencyRule()
    {
        int unsettled = 0, replaced = 0, runs = 0;
        var bad = new List<string>();
        var step = TrafficTests.Full ? 0.8f : 1.6f;
        foreach (var rideMs in new[] { 6f, 9f, 12f })
            for (var aAt = 0f; aAt <= 8f; aAt += step)
                for (var bAt = 0f; bAt <= 8f; bAt += step)
                {
                    var r = new TrafficSim(Crossing(rideMs, aAt, bAt), false, 0).Run();
                    runs++;
                    unsettled += r.PassUnsettled;
                    replaced += r.FallbackStop;
                    if (r.Failure != null) bad.Add($"{rideMs} m/s, A at {aAt:0.0} s, B at {bAt:0.0} s: {r}");
                }
        output.WriteLine($"{runs} runs, {unsettled} unsettled decisions, {replaced} replaced by the emergency rule, {bad.Count} failed");
        foreach (var l in bad.Take(10)) output.WriteLine(l);
        Assert.True(unsettled > 0, "the pass never ran out of rounds");
        Assert.Equal(unsettled, replaced);
        Assert.Empty(bad);
    }
}
