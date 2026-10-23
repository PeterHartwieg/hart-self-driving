using System;
using System.Collections.Generic;
using System.Linq;
using DrivenRides.Core.Drive;
using DrivenRides.Core.Routing;
using Xunit;

namespace DrivenRides.Core.Tests;

public sealed class ParkingGoalTests
{
    private static CarSpec Car => new("vord", 2.665f, _ => 35f, new Vec3(0, 0.8f, 1.199f),
        new Vec3(0.897f, 0.8f, 2.461f), 50, 1.504f, 140);

    private static LaneGraph Streets(float[] lanes, float[]? heights = null, bool loop = false, int junction = -1)
    {
        var p = new List<Vec3>(); var edges = new List<int[]>(); var intersections = new List<bool>();
        for (var lane = 0; lane < lanes.Length; lane++)
            for (var k = 0; k <= 20; k++)
            {
                p.Add(new Vec3(loop && lane == 1 ? 100 - k * 5 : k * 5, heights?[lane] ?? 0, lanes[lane]));
                edges.Add(k < 20 ? new[] { lane * 21 + k + 1 } : loop ? new[] { lane == 0 ? 21 : 0 } : Array.Empty<int>());
                intersections.Add(k == junction);
            }
        return RoutingTests.Graph(p.ToArray(), edges.ToArray(), intersections: intersections.ToArray());
    }

    [Theory]
    [InlineData(-3f)]
    [InlineData(3f)]
    public void RoadStopsAheadOfParkingOnEitherSide(float z)
    {
        var graph = Streets(new[] { 0f });
        var strip = new ParkingStrip(new Vec3(40, 0, z), MathF.PI / 2, 1.2f, 15);
        var goals = ParkingGoalPicker.Pick(graph, new[] { strip }, new Vec3(40, 0, z * 3), Car, 0);
        Assert.NotEmpty(goals);
        Assert.All(goals, goal =>
        {
            var road = graph.Position(goal.Waypoint);
            var parkedCentreX = road.X - Car.BodyHalf.Z - 3;
            Assert.InRange(parkedCentreX, 25 + Car.Length / 2 + 0.5f, 55 - Car.Length / 2 - 0.5f);
            Assert.Equal(0, road.Z);
        });
    }

    [Fact]
    public void InnerLaneCannotReachAcrossTheLaneBesideTheStrip()
    {
        var graph = Streets(new[] { 0f, -1.6f });
        var strip = new ParkingStrip(new Vec3(40, 0, -4.2f), MathF.PI / 2, 1.2f, 15);
        var goals = ParkingGoalPicker.Pick(graph, new[] { strip }, strip.Centre, Car, 0);
        Assert.NotEmpty(goals);
        Assert.All(goals, g => Assert.Equal(-1.6f, graph.Position(g.Waypoint).Z));
        Assert.False(ParkingGoalPicker.Adjacent(graph, strip, new Vec3(40, 0, 0)));
    }

    [Fact]
    public void ParkingStripDirectionMakesTheRoadRouteGoRoundTheBlock()
    {
        var graph = Streets(new[] { 0f, 6f }, loop: true);
        var strip = new ParkingStrip(new Vec3(40, 0, 3), MathF.PI / 2, 1.2f, 15);
        var goals = ParkingGoalPicker.Pick(graph, new[] { strip }, new Vec3(40, 0, 8), Car, 0);
        Assert.NotEmpty(goals);
        Assert.All(goals, g => Assert.Equal(0, graph.Position(g.Waypoint).Z));
        var start = 21 + 12; // Opposite carriageway, directly beside the building, facing west.
        var job = RoutePlanner.Plan(graph, new RouteRequest { Start = start, Goals = goals }, int.MaxValue);
        Assert.Equal(PlanStatus.Found, job.Status);
        Assert.Contains(0, job.Result!.Waypoints);
        Assert.Equal(0, graph.Position(job.Result.Waypoints.Last()).Z);
    }

    [Fact]
    public void OtherLevelsAndStripsTooSmallForTheCarDoNotBecomeRoadGoals()
    {
        var graph = Streets(new[] { 0f, 0f }, new[] { 0f, 4f });
        var strip = new ParkingStrip(new Vec3(40, 0, -3), MathF.PI / 2, 1.2f, 15);
        var goals = ParkingGoalPicker.Pick(graph, new[] { strip }, strip.Centre, Car, 0);
        Assert.NotEmpty(goals);
        Assert.All(goals, g => Assert.Equal(0, graph.Position(g.Waypoint).Y));
        Assert.Empty(ParkingGoalPicker.Pick(graph, new[] { new ParkingStrip(strip.Centre, strip.Heading, 0.8f, 15) }, strip.Centre, Car, 0));
        Assert.Empty(ParkingGoalPicker.Pick(graph, new[] { new ParkingStrip(strip.Centre, strip.Heading, 1.2f, 2.5f) }, strip.Centre, Car, 0));
        Assert.Empty(ParkingGoalPicker.Pick(graph, new[] { strip }, strip.Centre, Car, 1));
        Assert.Empty(ParkingGoalPicker.Pick(graph, Array.Empty<ParkingStrip>(), strip.Centre, Car, 0));
    }

    [Fact]
    public void SpaceApproachLeavesRoomToStageBeforeHandingOverToTheManoeuvrePlanner()
    {
        var graph = Streets(new[] { 0f });
        var rear = new Vec3(40, 0, -3);
        var goals = ParkingGoalPicker.ForSpace(graph, rear, MathF.PI / 2, Car, 0);
        Assert.NotEmpty(goals);
        Assert.All(goals, g => Assert.InRange(graph.Position(g.Waypoint).X - rear.X, Car.FrontAhead + 3, Car.FrontAhead + 9));
        var junction = Streets(new[] { 0f }, junction: 10);
        Assert.Empty(ParkingGoalPicker.ForSpace(junction, new Vec3(46, 0, -3), MathF.PI / 2, Car, 0));
    }
}
