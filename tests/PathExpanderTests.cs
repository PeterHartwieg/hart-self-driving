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
    public void AddedTurnKeepsViasAndSkipsTwoExitWaypointsOnlyWhenStraight(bool longExit)
    {
        var (graph, entry, exit) = TurnTestGraph.Turn();
        int[] route = longExit ? new[] { 0, 1, entry, exit, exit + 1, exit + 2, exit + 3 } : new[] { 0, 1, entry, exit, exit + 1 };
        var expanded = PathExpander.Expand(graph, route);
        int[] vias = graph.TurnVias(entry, exit).ToArray();
        Assert.NotEmpty(vias);
        int[] expected = new[] { 0, 1, entry }.Concat(vias).Concat(longExit ? new[] { exit + 2, exit + 3 } : new[] { exit, exit + 1 }).ToArray();
        Assert.Equal(expected, expanded.Path);
        Assert.Equal(Enumerable.Range(0, 4 + vias.Length), expanded.SlowPath.OrderBy(i => i));
    }

    [Fact]
    public void UTurnKeepsAllExitWaypointsAndDoesNotSlowThePath()
    {
        var (graph, entry, exit) = TurnTestGraph.UTurn();
        var route = new[] { 0, entry, graph.UTurn(entry), graph.UTurn(entry) + 1, exit };
        var expanded = PathExpander.Expand(graph, route);
        Assert.Equal(route.Take(2).Concat(graph.TurnVias(entry, graph.UTurn(entry))).Concat(route.Skip(2)), expanded.Path);
        Assert.Empty(expanded.SlowPath);
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
