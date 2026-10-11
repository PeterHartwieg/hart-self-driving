using System;
using System.Linq;
using DrivenRides.Core.Routing;
using Xunit;

namespace DrivenRides.Core.Tests;

public sealed class GoalPickerTests
{
    private static LaneGraph Chain(float spacing = 10, bool[]? intersections = null, int[]? masks = null, float[]? heights = null)
    {
        var points = Enumerable.Range(0, 7).Select(i => new Vec3(i * spacing, heights?[i] ?? 0, 0)).ToArray();
        var edges = Enumerable.Range(0, 7).Select(i => i == 6 ? Array.Empty<int>() : new[] { i + 1 }).ToArray();
        return RoutingTests.Graph(points, edges, intersections: intersections, masks: masks);
    }

    [Fact]
    public void StraightStretchCombinesBothDirectionsAndSortsByWalkTime()
    {
        var options = new GoalOptions { Radius = 60, MinStraightMetres = 40, MaxGoals = 3 };
        var goals = GoalPicker.Pick(Chain(), new Vec3(32, 0, 0), 0, options);
        Assert.Equal(new[] { 3, 4, 2 }, goals.Select(g => g.Waypoint));
        Assert.Equal(2 / 1.4f, goals[0].WalkSeconds, 5);
        Assert.Empty(GoalPicker.Pick(Chain(), default, 0, new GoalOptions { MinStraightMetres = 41 }));
        Assert.Empty(GoalPicker.Pick(Chain(), default, 0, new GoalOptions { MaxGoals = 0 }));
    }

    [Fact]
    public void FiltersRadiusHeightIntersectionsAndVehicleTypes()
    {
        var graph = Chain(intersections: new[] { true, false, false, false, false, false, true },
            masks: new[] { 1, 1, 2, 1, 1, 1, 1 }, heights: new float[] { 0, 0, 0, 4, 0, 0, 0 });
        var goals = GoalPicker.Pick(graph, new Vec3(20, 0, 0), 0, new GoalOptions { Radius = 20, MinStraightMetres = 15 });
        Assert.Equal(new[] { 1, 4 }, goals.Select(g => g.Waypoint));
        var otherVehicle = GoalPicker.Pick(graph, new Vec3(20, 0, 0), 1);
        Assert.Equal(2, Assert.Single(otherVehicle).Waypoint);
    }

    [Fact]
    public void BranchAndIntersectionBreakStretch()
    {
        Vec3[] positions = { new(0, 0, 0), new(10, 0, 0), new(20, 0, 0), new(30, 0, 0), new(40, 0, 0), new(30, 0, 10) };
        int[][] edges = { new[] { 1 }, new[] { 2 }, new[] { 3, 5 }, new[] { 4 }, Array.Empty<int>(), Array.Empty<int>() };
        var graph = RoutingTests.Graph(positions, edges);
        Assert.Empty(GoalPicker.Pick(graph, new Vec3(20, 0, 0), 0));
        Assert.Empty(GoalPicker.Pick(Chain(intersections: new[] { false, false, true, false, true, false, false }), new Vec3(30, 0, 0), 0));
    }

    [Fact]
    public void CycleTerminatesAndDoesNotCountRepeatedEdges()
    {
        var graph = RoutingTests.Graph(new[] { new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(2, 0, 0) },
            new[] { new[] { 1 }, new[] { 2 }, new[] { 0 } });
        Assert.Empty(GoalPicker.Pick(graph, default, 0, new GoalOptions { MinStraightMetres = 100 }));
    }

    [Fact]
    public void RejectsInvalidOptions()
    {
        Assert.Throws<ArgumentException>(() => GoalPicker.Pick(Chain(), default, 0, new GoalOptions { WalkSpeedMs = 0 }));
        Assert.Throws<ArgumentException>(() => GoalPicker.Pick(Chain(), default, 0, new GoalOptions { MinStraightMetres = -1 }));
    }
}
