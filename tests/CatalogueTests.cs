using System;
using System.Linq;
using DrivenRides.Core.Routing;
using DrivenRides.Core.Testing;
using Xunit;

namespace DrivenRides.Core.Tests;

public sealed class CatalogueTests
{
    [Fact]
    public void TurnWalksThroughAlignedForksAndHonoursWholeSegmentBudgets()
    {
        var (graph, entry, exit) = TurnTestGraph.Turn();
        Fixture fixture = Catalogue.Build(graph, new CatalogueOptions { RunUpMetres = 15, RunOutMetres = 25 }).Single(f => f.Kind == CaseKind.Turn && f.Route[f.EntryIndex] == entry && f.Route[f.ExitIndex] == exit);
        Assert.Equal(new[] { 1, entry, exit, exit + 1, exit + 2 }, fixture.Route);
        Assert.Equal(1, fixture.EntryIndex);
        Assert.Equal(2, fixture.ExitIndex);
        Assert.Equal(90, fixture.SharpnessDegrees, 3);
        Assert.DoesNotContain(Catalogue.Build(graph, new CatalogueOptions { NearBridge = p => p.X == 20 }), f => f.Id == fixture.Id);
    }

    [Fact]
    public void RunUpSelectsAlignedPredecessorAndStopsAtAnAmbiguousFork()
    {
        var b = new TurnTestGraph();
        int run0 = b.Add(0, -50), run1 = b.Add(0, -40), entry = b.Add(0, -30), junction = b.Add(0, -15, true), straight = b.Add(0, 0);
        int branch = b.Add(-10, -40), exit = b.Add(10, -20), after = b.Add(20, -20), end = b.Add(30, -20), preExit = b.Add(5, -20, true);
        b.Link(run0, run1, entry, junction, straight); b.Link(branch, entry); b.Link(preExit, exit, after, end);
        var graph = b.Build(true);
        var fixture = Catalogue.Build(graph).Single(f => f.Kind == CaseKind.Turn && f.Route[f.EntryIndex] == entry);
        Assert.Equal(new[] { run0, run1, entry }, fixture.Route.Take(fixture.EntryIndex + 1));
        int duplicate = b.Add(0, -40); b.Link(duplicate, entry);
        fixture = Catalogue.Build(b.Build(true)).Single(f => f.Kind == CaseKind.Turn && f.Route[f.EntryIndex] == entry);
        Assert.Equal(0, fixture.EntryIndex);
    }

    [Fact]
    public void RunOutChoosesTheAlignedSuccessorAndStopsAtATie()
    {
        var b = new TurnTestGraph();
        int a = b.Add(0, -30), e = b.Add(0, -10), j = b.Add(0, -5, true), end = b.Add(0, 10);
        int cross = b.Add(5, -2, true), x = b.Add(10, -2), next = b.Add(20, -2), aligned = b.Add(30, -2), side = b.Add(20, 8);
        b.Link(a, e, j, end); b.Link(cross, x, next); b.Fork(next, aligned, side);
        var graph = b.Build(true);
        Fixture f = Catalogue.Build(graph).Single(c => c.Kind == CaseKind.Turn && c.Route[c.EntryIndex] == e);
        Assert.Equal(aligned, f.Route.Last());
        int tie = b.Add(30, -2); b.Fork(next, aligned, tie);
        f = Catalogue.Build(b.Build(true)).Single(c => c.Kind == CaseKind.Turn && c.Route[c.EntryIndex] == e);
        Assert.Equal(next, f.Route.Last());
    }

    [Fact]
    public void UTurnIncludesJunctionTargetThroughFirstPlainExit()
    {
        var (graph, entry, exit) = TurnTestGraph.UTurn();
        var f = Catalogue.Build(graph).Single(c => c.Kind == CaseKind.UTurn && c.Route[c.EntryIndex] == entry);
        Assert.Equal(exit, f.Route[f.ExitIndex]);
        Assert.Equal(graph.UTurn(entry), f.Route[f.EntryIndex + 1]);
        Assert.Equal(graph.UTurnStart(entry), f.TurnGateWaypoint);
        Assert.Equal(180, f.SharpnessDegrees, 3);
    }

    [Fact]
    public void LaneChangesAreDiagonalAndDeduplicatedByDirectedLanePair()
    {
        var b = new TurnTestGraph();
        int[] a = Enumerable.Range(0, 8).Select(i => b.Add(0, i * 10)).ToArray();
        int[] c = Enumerable.Range(0, 8).Select(i => b.Add(6, i * 10)).ToArray();
        b.Link(a); b.Link(c);
        for (int k = 0; k < 6; k++) b.Beside(a[k], c[k]);
        int j = b.Add(0, 80, true); b.Link(a[7], j);
        var changes = Catalogue.Build(b.Build(), new CatalogueOptions { RunUpMetres = 0, RunOutMetres = 0 }).Where(f => f.Kind == CaseKind.LaneChange).ToList();
        var f = Assert.Single(changes);
        Assert.Equal(new[] { a[5], c[7] }, f.Route);
        Assert.True(f.SharpnessDegrees > 0);
    }

    [Fact]
    public void NarrowLaneChangeNeedsANearbyJunction()
    {
        var b = new TurnTestGraph();
        int[] a = Enumerable.Range(0, 5).Select(i => b.Add(0, i * 10)).ToArray();
        int[] c = Enumerable.Range(0, 5).Select(i => b.Add(3, i * 10)).ToArray();
        b.Link(a); b.Link(c); b.Beside(a[0], c[0]);
        Assert.DoesNotContain(Catalogue.Build(b.Build()), f => f.Kind == CaseKind.LaneChange);
        int j = b.Add(0, 45, true); b.Link(a[4], j);
        Assert.Contains(Catalogue.Build(b.Build()), f => f.Kind == CaseKind.LaneChange);
    }

    [Fact]
    public void RampWindowsMergeButSeparateStretchesStaySeparate()
    {
        var b = new TurnTestGraph();
        int[] chain = Enumerable.Range(0, 26).Select(i => b.Add(i * 10, 0, y: i < 3 ? 0 : i < 18 ? 5 : 10)).ToArray();
        b.Link(chain);
        var ramps = Catalogue.Build(b.Build(), new CatalogueOptions { RunUpMetres = 0, RunOutMetres = 0 }).Where(f => f.Kind == CaseKind.Ramp).ToList();
        Assert.Equal(2, ramps.Count);
        Assert.All(ramps, f => Assert.True(Math.Abs(b.Build().Position(f.Route[f.ExitIndex]).Y - b.Build().Position(f.Route[f.EntryIndex]).Y) > 3));
    }

    [Fact]
    public void RideSplitsAtBridgeAndDropsShortPieces()
    {
        var b = new TurnTestGraph();
        int[] route = Enumerable.Range(0, 12).Select(i => b.Add(i * 10, 0)).ToArray(); b.Link(route);
        var pieces = Catalogue.FromRide(b.Build(), route, 7, new CatalogueOptions { NearBridge = p => p.X == 50 });
        Assert.Equal(2, pieces.Count);
        Assert.Equal(route.Take(5), pieces[0].Route);
        Assert.Equal(route.Skip(6), pieces[1].Route);
        Assert.All(pieces, f => { Assert.Equal(7, f.RideLine); Assert.Equal(0, f.EntryIndex); Assert.Equal(f.Route.Count - 1, f.ExitIndex); });
        Assert.Empty(Catalogue.FromRide(b.Build(), route.Take(4).ToArray(), 7, new CatalogueOptions()));
    }
    [Fact]
    public void WideLaneChangeStillChoosesTheNearestJunctionBeyondFortyMetres()
    {
        var b = new TurnTestGraph();
        int[] a = Enumerable.Range(0, 16).Select(i => b.Add(0, i * 10)).ToArray();
        int[] c = Enumerable.Range(0, 16).Select(i => b.Add(6, i * 10)).ToArray();
        b.Link(a); b.Link(c); b.Beside(a[0], c[0]); b.Beside(a[1], c[1]);
        int j = b.Add(0, 160, true); b.Link(a[15], j);
        var fixture = Assert.Single(Catalogue.Build(b.Build()), f => f.Kind == CaseKind.LaneChange);
        Assert.Equal(a[1], fixture.Route[fixture.EntryIndex]);
        Assert.Equal(c[3], fixture.Route[fixture.ExitIndex]);
    }

    [Fact]
    public void RampSharpnessIncludesTheFullHeadingChangeAlongTheRoute()
    {
        var b = new TurnTestGraph();
        int a = b.Add(0, 0), first = b.Add(0, 10, y: 2), second = b.Add(10, 20, y: 4), exit = b.Add(20, 20, y: 6), end = b.Add(30, 20, y: 6);
        b.Link(a, first, second, exit, end);
        var ramp = Assert.Single(Catalogue.Build(b.Build(), new CatalogueOptions { RunUpMetres = 0, RunOutMetres = 0 }), f => f.Kind == CaseKind.Ramp);
        Assert.Equal(90, ramp.SharpnessDegrees, 3);
    }

}
