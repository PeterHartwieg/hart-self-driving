using System;
using System.Collections.Generic;
using System.Linq;
using DrivenRides.Core.Drive;
using DrivenRides.Core.Testing;
using Xunit;
using Xunit.Abstractions;

namespace DrivenRides.Core.Tests;

// Review round 8. The ride car crosses a two-lane road (lane A eastbound at z = 12, lane B westbound at z = 17) with
// no junction rules: it judges the gaps itself. Swept over its start speed and over the two cars' times, in small
// steps (adjacent positions and decision times, not single snapshots).
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

    // CC1 and CC3: crossing two lanes with a car on each, swept over the ride car's start speed and the cars' times in
    // small steps, both car orders (which car the snapshot lists first): no contact, no stall, and no stop the car can
    // no longer make handed to the driver except by the emergency rule. Every stop kept for a car is lifted only on a
    // trajectory with the other car's kept stop in place (CC1).
    // With car B listed first (and so the slower-reacting car 2 on lane A) 7 of the 1200 runs ended in a contact in
    // round 8: the ride car stopped for car B with its nose past car A's keep-clear point and stood across lane A. Now
    // the keep-clear holds from the body's own pose too (a stop there is the emergency rule's), and a car standing in a
    // crossing car's way backs out when it can (LaneBack).
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CrossingTwoLanesAcrossASweepOfTimes(bool bFirst)
    {
        var runs = 0;
        var gave = 0;
        var bad = new List<string>();
        foreach (var rideMs in new[] { 6f, 9f, 12f })
            for (var aAt = 0f; aAt <= 8f; aAt += 0.4f)
                for (var bAt = 0f; bAt <= 8f; bAt += 0.4f)
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

    // CC3: the merge runs that lost the stopping option in round 7, swept over the ride car's start speed: no contact,
    // no stall, and no unmakeable stop except by the emergency rule.
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

    // CC2: with the final pass given no rounds at all, every decision where judging the commitments still tightens the
    // stops runs out (the crossing's conflicts found too late make sure there are some); each is replaced by the
    // emergency rule's (a stop now for cars marked as the emergency rule's, or on with the floor as a forced commit),
    // never driven as it was. Deliberately exhausted: the soak never needs more than three rounds of eight.
    [Fact]
    public void AnUnsettledPassIsReplacedByTheEmergencyRule()
    {
        int unsettled = 0, replaced = 0, runs = 0;
        var bad = new List<string>();
        foreach (var rideMs in new[] { 6f, 9f, 12f })
            for (var aAt = 0f; aAt <= 8f; aAt += 0.8f)
                for (var bAt = 0f; bAt <= 8f; bAt += 0.8f)
                {
                    var r = new TrafficSim(Crossing(rideMs, aAt, bAt), false, 0).Run();
                    runs++;
                    unsettled += r.PassUnsettled;
                    replaced += r.FallbackOn + r.FallbackStop;
                    if (r.Failure != null) bad.Add($"{rideMs} m/s, A at {aAt:0.0} s, B at {bAt:0.0} s: {r}");
                }
        output.WriteLine($"{runs} runs, {unsettled} unsettled decisions, {replaced} replaced by the emergency rule, {bad.Count} failed");
        foreach (var l in bad.Take(10)) output.WriteLine(l);
        Assert.True(unsettled > 0, "the pass never ran out of rounds");
        Assert.Equal(unsettled, replaced);
        Assert.Empty(bad);
    }
}
