using System;
using System.Linq;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;
using Xunit;

namespace DrivenRides.Core.Tests;

public sealed class PathExpanderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddedTurnKeepsViasAndAimsAtAnExitWaypointTenMetresOn(bool longExit)
    {
        // The exit lies 10 m along its lane from the last via, so the car aims at it directly.
        var (graph, entry, exit) = TurnTestGraph.Turn();
        int[] route = longExit ? new[] { 0, 1, entry, exit, exit + 1, exit + 2, exit + 3 } : new[] { 0, 1, entry, exit, exit + 1 };
        var expanded = PathExpander.Expand(graph, route);
        int[] vias = graph.TurnVias(entry, exit).ToArray();
        Assert.NotEmpty(vias);
        int[] expected = new[] { 0, 1, entry }.Concat(vias).Concat(route.Skip(3)).ToArray();
        Assert.Equal(expected, expanded.Path);
        Assert.Equal(Enumerable.Range(0, 4 + vias.Length), expanded.SlowPath.OrderBy(i => i));
    }

    [Fact]
    public void AddedTurnSkipsAtMostTwoExitWaypointsThatAreTooNear()
    {
        // A right turn whose exit lane starts 3 m past the via: the 3 m and 8 m waypoints are too near to aim at.
        var b = new TurnTestGraph();
        int a = b.Add(0, -30), run = b.Add(0, -20), entry = b.Add(0, -10);
        int j1 = b.Add(0, -5, true), j2 = b.Add(0, 5, true), straight = b.Add(0, 10), end = b.Add(0, 20);
        b.Link(a, run, entry, j1, j2, straight, end);
        int west = b.Add(-20, -2), crossEntry = b.Add(-10, -2), cj1 = b.Add(-5, -2, true), cj2 = b.Add(-1, -2, true);
        int exit = b.Add(3, -2), x2 = b.Add(8, -2), x3 = b.Add(13, -2), x4 = b.Add(18, -2);
        b.Link(west, crossEntry, cj1, cj2, exit, x2, x3, x4);
        var graph = b.Build(true);
        var vias = graph.TurnVias(entry, exit).ToArray();
        Assert.NotEmpty(vias);
        var expanded = PathExpander.Expand(graph, new[] { a, run, entry, exit, x2, x3, x4 });
        Assert.Equal(new[] { a, run, entry }.Concat(vias).Concat(new[] { x3, x4 }), expanded.Path);
    }

    [Fact]
    public void UTurnKeepsAllExitWaypointsAndIsDrivenAtTurnSpeed()
    {
        var (graph, entry, exit) = TurnTestGraph.UTurn();
        var route = new[] { 0, entry, graph.UTurn(entry), graph.UTurn(entry) + 1, exit };
        var expanded = PathExpander.Expand(graph, route);
        Assert.Equal(route.Take(2).Concat(graph.TurnVias(entry, graph.UTurn(entry))).Concat(route.Skip(2)), expanded.Path);
        // Two positions before the entry, the entry, the vias and the U-turn's target.
        var vias = graph.TurnVias(entry, graph.UTurn(entry)).Count;
        Assert.Equal(Enumerable.Range(0, 2 + vias + 1), expanded.SlowPath.OrderBy(x => x));
        // The U-turn itself (its vias and target) is driven slower still.
        Assert.Equal(Enumerable.Range(2, vias + 1), expanded.UTurnPath.OrderBy(x => x));
        Assert.Equal(entry + 2, graph.UTurnStart(entry));
        Assert.Equal(-1, graph.UTurnStart(0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlainAndNullGraphCopyTheRoute(bool nullGraph)
    {
        var (graph, _, _) = TurnTestGraph.Turn();
        var route = new[] { 0, 1, 2 };
        var expanded = PathExpander.Expand(nullGraph ? null : graph, route);
        Assert.Equal(route, expanded.Path);
        Assert.Empty(expanded.SlowPath);
        expanded.Path.Clear();
        Assert.Equal(3, route.Length);
    }

    [Fact]
    public void FixedRouteCopiesWaypointsAndUsesMeanSpeedOnTheGroundPlane()
    {
        var graph = new LaneGraph(new[] { new Vec3(0, 0, 0), new Vec3(3, 8, 4), new Vec3(3, 8, 14) },
            new[] { new[] { 1 }, new[] { 2 }, Array.Empty<int>() }, new[] { 1, 1, 1 }, new bool[3], new[] { 36f, 72f, 36f });
        var points = new[] { 0, 1, 2 };
        var route = Route.Fixed(graph, points);
        points[2] = 0;
        Assert.Equal(new[] { 0, 1, 2 }, route.Waypoints);
        Assert.Equal(2, route.GoalIndex);
        Assert.Equal(15, route.LengthMetres);
        Assert.Equal(1, route.DriveSeconds, 5);
        Assert.Equal(0, route.WalkSeconds);
        Assert.True(graph.PlainHeading(0, out float dx, out float dz));
        Assert.Equal(0.6f, dx, 5);
        Assert.Equal(0.8f, dz, 5);
        Assert.False(graph.PlainHeading(2, out _, out _));
        Assert.Throws<ArgumentException>(() => Route.Fixed(graph, Array.Empty<int>()));
    }
}
