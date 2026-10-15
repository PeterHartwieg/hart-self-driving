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

    [Fact]
    public void NoLimitByDefaultSoFartherReachableGoalsStay()
    {
        // Twenty parallel one-way stretches near the target; a cut to the nearest few could keep only unreachable lanes.
        var points = new System.Collections.Generic.List<Vec3>();
        var edges = new System.Collections.Generic.List<int[]>();
        for (var lane = 0; lane < 20; lane++)
            for (var i = 0; i < 4; i++)
            {
                points.Add(new Vec3(i * 10, 0, lane * 2));
                edges.Add(i == 3 ? Array.Empty<int>() : new[] { points.Count });
            }
        var graph = RoutingTests.Graph(points.ToArray(), edges.ToArray());
        var goals = GoalPicker.Pick(graph, new Vec3(15, 0, 0), 0, new GoalOptions { MinStraightMetres = 10 });
        Assert.True(goals.Count > 12, $"{goals.Count} goals");
    }

    private static LaneGraph SetbackDestinationRoad()
    {
        // The door is 85 m back from a one-way road. Reaching the lane beside it needs a drive round the block;
        // stopping at the entrance is quicker overall, but leaves the passenger 131 m from the door.
        Vec3[] points = { new(-10, 0, 0), new(0, 0, 0), new(-200, 0, 0), new(-200, 0, 300),
            new(100, 0, 300), new(100, 0, 0), new(110, 0, 0) };
        int[][] edges = { new[] { 1 }, new[] { 2 }, new[] { 3 }, new[] { 4 }, new[] { 5 }, new[] { 6 }, Array.Empty<int>() };
        return RoutingTests.Graph(points, edges);
    }

    [Fact]
    public void WideSearchDrivesToNearerDropOffInsteadOfStoppingAtRoadEntrance()
    {
        var graph = SetbackDestinationRoad();
        var target = new Vec3(100, 0, -85);
        var options = new GoalOptions { Radius = 150, MinStraightMetres = 0, AnyVehicleType = true };
        Assert.Empty(GoalPicker.Pick(graph, target, 0));
        var broad = RoutePlanner.Plan(graph, new RouteRequest { Start = 0, Goals = GoalPicker.Pick(graph, target, 0, options) }, int.MaxValue);
        Assert.Equal(1, broad.Result!.Waypoints.Last()); // The old wide fallback chooses the entrance.

        var batches = GoalPicker.PickNearbyFirst(graph, target, 0, options).ToList();
        var near = RoutePlanner.Plan(graph, new RouteRequest { Start = 0, Goals = batches[0] }, int.MaxValue);
        Assert.Equal(PlanStatus.Found, near.Status);
        Assert.Equal(5, near.Result!.Waypoints.Last());
        Assert.True(near.Result.WalkSeconds < broad.Result.WalkSeconds);
        Assert.Equal(new[] { 5 }, batches[0].Select(g => g.Waypoint));
        Assert.Equal(new[] { 5, 1 }, batches.Last().Select(g => g.Waypoint));
    }

    [Fact]
    public void WideSearchRetainsFartherGoalsWhenNearestRoadIsBlocked()
    {
        var graph = SetbackDestinationRoad();
        var options = new GoalOptions { Radius = 150, MinStraightMetres = 0 };
        var batches = GoalPicker.PickNearbyFirst(graph, new Vec3(100, 0, -85), 0, options).ToList();
        var jobs = batches.Select(goals => RoutePlanner.Plan(graph, new RouteRequest
        {
            Start = 0, Goals = goals, IsBlockedStep = (from, to) => from == 4 && to == 5,
        }, int.MaxValue)).ToList();
        Assert.Equal(PlanStatus.NoPath, jobs[0].Status);
        Assert.Equal(PlanStatus.Found, jobs.Last().Status);
        Assert.Equal(1, jobs.Last().Result!.Waypoints.Last());
    }

    [Fact]
    public void NearbyGoalsCanIncludeRoadsWithoutTrafficPermissions()
    {
        var graph = Chain(masks: new[] { 1, 1, 0, 0, 0, 0, 0 });
        var target = new Vec3(50, 0, -10);
        var options = new GoalOptions { AnyVehicleType = true, MinStraightMetres = 0 };
        var goals = GoalPicker.Pick(graph, target, 0, options);
        Assert.Equal(5, goals[0].Waypoint);
        var route = RoutePlanner.Plan(graph, new RouteRequest { Start = 0, Goals = goals }, int.MaxValue);
        Assert.Equal(PlanStatus.Found, route.Status);
        Assert.Equal(5, route.Result!.Waypoints.Last());
    }
}
