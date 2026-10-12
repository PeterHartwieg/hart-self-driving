using System;
using System.Linq;
using DrivenRides.Core.Routing;
using DrivenRides.Core.Testing;
using Xunit;

namespace DrivenRides.Core.Tests;

public sealed class JudgeTests
{
    private static (LaneGraph Graph, Fixture Fixture) Straight(bool opposing = false, bool extraOwnLane = false, float otherHeight = 0)
    {
        var b = new TurnTestGraph();
        int[] route = Enumerable.Range(0, 8).Select(i => b.Add(0, i * 10)).ToArray(); b.Link(route);
        if (opposing)
        {
            int[] other = Enumerable.Range(0, 8).Select(i => b.Add(-6, (7 - i) * 10, y: otherHeight)).ToArray(); b.Link(other);
        }
        if (extraOwnLane)
        {
            int[] own = Enumerable.Range(0, 8).Select(i => b.Add(-3, i * 10)).ToArray(); b.Link(own);
        }
        return (b.Build(), new Fixture { Route = route.ToList(), Kind = CaseKind.Ride, Manoeuvre = CaseKind.Ride, EntryIndex = 1, ExitIndex = 2 });
    }
    private static Judge Make(LaneGraph graph, Fixture f) => new(graph, CaseGeometry.Build(graph, f), f);
    private static Sample At(int step, float x, float z, double? time = null, float heading = 0, float y = 0) => new()
    {
        Step = step, Time = time ?? step, Centre = new Vec3(x, y, z), FrontAxle = new Vec3(x, y, z),
        HeadingDegrees = heading, SpeedMs = 8,
        Footprint = new[] { new Vec3(x - 1, y, z - 2), new Vec3(x + 1, y, z - 2), new Vec3(x - 1, y, z + 2), new Vec3(x + 1, y, z + 2),
            new Vec3(x - 1, y, z), new Vec3(x + 1, y, z), new Vec3(x, y, z - 2), new Vec3(x, y, z + 2) }
    };
    private static void Finish(Judge judge, float x = 0, float yaw = 0)
    {
        judge.Add(At(0, 0, 0)); judge.Add(At(1, 0, 11)); judge.Add(At(2, 0, 21));
        judge.Add(At(3, x, 31)); judge.Add(At(4, x, 41)); judge.Add(At(5, x, 51, heading: yaw));
    }

    [Fact]
    public void CleanPassRecordsGatesAndEntrySpeed()
    {
        var (graph, f) = Straight(); var judge = Make(graph, f); Finish(judge);
        Assert.True(judge.Finished); Assert.False(judge.Failed);
        var result = judge.Result(false);
        Assert.Equal("pass", result.Verdict); Assert.Equal(new[] { "entry", "exit", "last" }, result.GatesPassed);
        Assert.Equal(8, result.EntrySpeed); Assert.Equal(0, result.LastGateHeadingError);
        result.Reasons.Add("changed snapshot"); Assert.Empty(judge.Result(false).Reasons);
    }

    [Fact]
    public void GatesUseFrontAxleAndCheckCrossingOrderWithinOneSample()
    {
        var (graph, f) = Straight(); var judge = Make(graph, f);
        var s = At(0, 0, 0); s.FrontAxle = new Vec3(0, 0, 8); judge.Add(s);
        s = At(1, 0, 8); s.FrontAxle = new Vec3(0, 0, 22); judge.Add(s);
        Assert.Equal(new[] { "entry", "exit" }, judge.Result(false).GatesPassed);
        Assert.False(judge.Failed);
        f.EntryIndex = 2; f.ExitIndex = 1;
        judge = Make(graph, f); judge.Add(At(0, 0, 0)); judge.Add(At(1, 0, 25));
        Assert.Contains("missed-gate:entry", judge.Result(false).Reasons);
    }

    [Fact]
    public void MissingEntryGateFailsWhenExitCrosses()
    {
        var (graph, f) = Straight(); var judge = Make(graph, f);
        judge.Add(At(0, 5, 8)); judge.Add(At(1, 5, 12)); judge.Add(At(2, 0, 18)); judge.Add(At(3, 0, 21));
        Assert.Contains("missed-gate:entry", judge.Result(false).Reasons);
        Assert.Equal(3, judge.FailedStep);
    }

    [Theory]
    [InlineData(1.1f, 0f, "exit-lane")]
    [InlineData(0f, 11f, "exit-heading")]
    [InlineData(0f, 349f, "exit-heading")]
    public void LastGateChecksCentreAndWrappedYaw(float side, float yaw, string reason)
    {
        var (graph, f) = Straight(); var judge = Make(graph, f); Finish(judge, side, yaw);
        Assert.Contains(reason, judge.Result(false).Reasons);
        Assert.Equal(5, judge.FailedStep);
    }

    [Fact]
    public void PlainIntrusionUsesFootprintAndTheNearestSameDirectionLane()
    {
        var (graph, f) = Straight(true, true); var judge = Make(graph, f);
        judge.Add(At(0, -3, 1)); Assert.False(judge.Failed);
        judge.Add(At(1, -4, 2));
        Assert.Contains("intrusion", judge.Result(false).Reasons);
        Assert.Equal(0.5f, judge.Result(false).MaxIntrusion, 4);
        Assert.Equal(4, judge.Result(false).PlainDeviationMax, 4);
    }

    [Fact]
    public void PlainIntrusionHonoursDepthThresholdAndHeight()
    {
        var (graph, f) = Straight(true); var judge = Make(graph, f);
        judge.Add(At(0, -2.2f, 1)); Assert.False(judge.Failed);
        judge.Add(At(1, -2.4f, 2)); Assert.True(judge.Failed);
        (graph, f) = Straight(true, otherHeight: 2); judge = Make(graph, f);
        judge.Add(At(0, -5, 1)); Assert.False(judge.Failed);
    }

    [Fact]
    public void PlainIntrusionChecksEitherSide()
    {
        var b = new TurnTestGraph();
        int[] own = Enumerable.Range(0, 8).Select(i => b.Add(0, i * 10)).ToArray();
        int[] other = Enumerable.Range(0, 8).Select(i => b.Add(6, (7 - i) * 10)).ToArray(); b.Link(own); b.Link(other);
        var judge = Make(b.Build(), new Fixture { Route = own.ToList(), Manoeuvre = CaseKind.Ride, EntryIndex = 1, ExitIndex = 2 });
        judge.Add(At(0, 2.4f, 1)); Assert.Contains("intrusion", judge.Result(false).Reasons);
    }

    [Fact]
    public void TurnedIntoRoadIntrusionCountsFromTheRoadsMouthOn()
    {
        var b = new TurnTestGraph();
        int run = b.Add(0, -10), entry = b.Add(0, 0), plain = b.Add(0, 10);
        int[] exit = Enumerable.Range(0, 7).Select(i => b.Add(-10 - i * 10, 10)).ToArray();
        int[] oncoming = Enumerable.Range(0, 9).Select(i => b.Add(-70 + i * 10, 4)).ToArray();
        b.Link(run, entry, plain); b.Link(exit); b.Link(oncoming);
        var f = new Fixture { Route = new[] { run, entry }.Concat(exit).ToList(), Kind = CaseKind.Turn, Manoeuvre = CaseKind.Turn, EntryIndex = 1, ExitIndex = 2 };
        // Past the mouth (5 m into the road turned into), 2 m over the midline towards the oncoming lane.
        var judge = Make(b.Build(), f);
        judge.Add(At(0, 0, -1)); Assert.False(judge.Failed);
        var sample = At(1, 0, 1); sample.Footprint = new[] { new Vec3(-15, 0, 5) }; judge.Add(sample);
        Assert.Contains("intrusion", judge.Result(false).Reasons);
        Assert.Equal(2, judge.Result(false).MaxIntrusion, 4);
        // Inside the junction a left turn sweeps across those lanes: not counted.
        judge = Make(b.Build(), f); judge.Add(At(0, 0, -1));
        sample.Footprint = new[] { new Vec3(-5, 0, 5) }; judge.Add(sample);
        Assert.DoesNotContain("intrusion", judge.Result(false).Reasons);
    }

    [Fact]
    public void UTurnFailsOnFarSideBeforeTurnGateButAllowsItAfterwards()
    {
        var (graph, entry, _) = TurnTestGraph.UTurn();
        var f = Catalogue.Build(graph).Single(c => c.Kind == CaseKind.UTurn && c.Route[c.EntryIndex] == entry);
        var judge = Make(graph, f);
        judge.Add(At(0, -10, 0, heading: 90));
        var s = At(1, 0, 15, heading: 90); judge.Add(s);
        Assert.Contains("far-side-early", judge.Result(false).Reasons);
        judge = Make(graph, f);
        judge.Add(At(0, -10, 0, heading: 90)); judge.Add(At(1, 0, 0, heading: 90)); judge.Add(At(2, 13, 0, heading: 90));
        judge.Add(At(3, 20, 15, heading: 270)); judge.Add(At(4, 0, 15, heading: 270)); judge.Add(At(5, -16, 15, heading: 270));
        judge.Add(At(6, -31, 15, heading: 270)); judge.Add(At(7, -46, 15, heading: 270));
        Assert.Equal("pass", judge.Result(false).Verdict);
    }

    [Fact]
    public void FarSideUsesFootprintAndHeight()
    {
        var (graph, entry, _) = TurnTestGraph.UTurn();
        var f = Catalogue.Build(graph).Single(c => c.Kind == CaseKind.UTurn && c.Route[c.EntryIndex] == entry);
        var judge = Make(graph, f);
        var s = At(0, 0, 10, y: 3); s.Footprint = new[] { new Vec3(0, 3, 15) }; judge.Add(s);
        Assert.False(judge.Failed);
        s.Step = 1; s.Footprint = new[] { new Vec3(0, 0, 15) }; judge.Add(s);
        Assert.Contains("far-side-early", judge.Result(false).Reasons);
    }

    [Fact]
    public void StuckResetsForMovementGatesAndWaiting()
    {
        var (graph, f) = Straight(); var judge = Make(graph, f);
        judge.Add(At(0, 0, 0, 0)); judge.Add(At(1, 0, 0, 19)); Assert.False(judge.Failed);
        judge.Add(At(2, 0, 0, 20)); Assert.Contains("stuck", judge.Result(false).Reasons);
        judge = Make(graph, f); judge.Add(At(0, 0, 0, 0));
        var s = At(1, 0, 0, 25); s.Waiting = true; judge.Add(s);
        s = At(2, 0, 0, 50); s.Waiting = true; judge.Add(s);
        judge.Add(At(3, 0, 0, 60)); judge.Add(At(4, 0, 2, 79)); judge.Add(At(5, 0, 2, 98));
        Assert.False(judge.Failed);
        judge = Make(graph, f); judge.Add(At(0, 0, 9, 0)); judge.Add(At(1, 0, 10.1f, 19)); judge.Add(At(2, 0, 10.1f, 21));
        Assert.False(judge.Failed);
    }

    [Fact]
    public void ContactNearMissAndReplanLatchFirstFailureAndKeepLaterReasons()
    {
        var (graph, f) = Straight(); var judge = Make(graph, f);
        var s = At(2, 0, 0); s.NearMiss = 0.3f; judge.Add(s); Assert.False(judge.Failed);
        s.Step = 3; s.NearMiss = 0.2f; judge.Add(s); Assert.False(judge.Failed);
        s.Step = 4; s.NearMiss = 0.1f; judge.Add(s);
        judge.Contact(5, "building"); judge.Incident(6, "car"); judge.Replanned("blocked step"); judge.Contact(7, "prop");
        Assert.Equal(4, judge.FailedStep);
        var result = judge.Result(true);
        Assert.Equal("fail", result.Verdict); Assert.Equal(new[] { "near-miss", "contact", "replanned" }, result.Reasons);
        Assert.Equal(0.1f, result.NearMiss); Assert.Equal(4, result.Incidents.Count);
    }

    [Fact]
    public void IncidentsAreRecordedWithoutFailureAndTimeoutIsIncomplete()
    {
        var (graph, f) = Straight(); var judge = Make(graph, f);
        judge.Incident(1, "pedestrian"); Assert.False(judge.Failed);
        Assert.Equal("incomplete", judge.Result(false).Verdict); Assert.Equal("incomplete", judge.Result(true).Verdict);
        Finish(judge); Assert.Equal("incomplete", judge.Result(true).Verdict);
        Assert.Equal("pass", judge.Result(false).Verdict);
        judge = Make(graph, f); judge.Contact(8, "prop"); Assert.Equal(8, judge.FailedStep);
        judge = Make(graph, f); judge.Add(At(9, 0, 0)); judge.Replanned("watchdog"); Assert.Equal(9, judge.FailedStep);
    }
    [Fact]
    public void TurnExitPlainSegmentsStillDetectIntrusionBeyondTheReceivingRoadWindow()
    {
        var b = new TurnTestGraph();
        int run = b.Add(10, -10), entry = b.Add(10, 0), ahead = b.Add(10, 10);
        int[] exit = Enumerable.Range(0, 7).Select(i => b.Add(-10 - i * 10, 10)).ToArray();
        int[] oncoming = Enumerable.Range(0, 9).Select(i => b.Add(-70 + i * 10, 4)).ToArray();
        b.Link(run, entry, ahead); b.Link(exit); b.Link(oncoming);
        var f = new Fixture { Route = new[] { run, entry }.Concat(exit).ToList(), Kind = CaseKind.Turn, Manoeuvre = CaseKind.Turn, EntryIndex = 1, ExitIndex = 2 };
        var judge = Make(b.Build(), f);
        judge.Add(At(0, 10, -1)); judge.Add(At(1, 10, 1)); judge.Add(At(2, -11, 10, heading: 270));
        judge.Add(At(3, -21, 10, heading: 270)); judge.Add(At(4, -31, 10, heading: 270));
        Assert.False(judge.Failed);
        var s = At(5, -37, 10, heading: 270); s.Footprint = new[] { new Vec3(-37, 0, 5) }; judge.Add(s);
        Assert.False(judge.Finished);
        Assert.Contains("intrusion", judge.Result(false).Reasons);
    }

}
