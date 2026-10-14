using System;
using System.Linq;
using DrivenRides.Core.Drive;
using DrivenRides.Core.Routing;
using DrivenRides.Core.Testing;
using Xunit;

namespace DrivenRides.Core.Tests;

// The own car pulling out from the kerb (OwnCarTaxi.AddPullOut): the line's start moved sideways to under the car,
// easing into the lane over DriveLine.ShiftRamp, then judged by the start check (Feasibility.Join) like any join.
public sealed class PullOutTests
{
    private static CarSpec Small() => new("small", 2.5f, _ => 40f, new Vec3(0, 0.7f, 1.25f), new Vec3(0.9f, 0.7f, 2f), 50f, 1.5f);

    private static Func<BodyBox, string?> Post(float x, float z) => box =>
    {
        float s = (float)Math.Sin(box.Heading), c = (float)Math.Cos(box.Heading);
        float dx = x - box.Centre.X, dz = z - box.Centre.Z;
        return Math.Abs(dx * c - dz * s) <= box.Half.X && Math.Abs(dx * s + dz * c) <= box.Half.Z ? "parked car" : null;
    };

    // A lane north along x = 0, every 10 m; the car's route starts at z = 20.
    private static (LaneGraph Graph, int[] Route) North()
    {
        var b = new TurnTestGraph();
        var lane = Enumerable.Range(0, 13).Select(i => b.Add(0, i * 10)).ToArray();
        b.Link(lane);
        return (b.Build(), lane.Skip(2).ToArray());
    }

    // As OwnCarTaxi.AddPullOut: the whole first stretch up to the car at the offset, then the ramp into the lane.
    private static DrivePlan PulledOut(LaneGraph graph, int[] route, Feasibility check, float sideways, float along)
    {
        var plan = DrivePlan.Build(graph, route, Small(), check.Arc, check.Clear);
        plan.Shifts.Add((0, -1f, along, sideways));
        return plan;
    }

    [Fact]
    public void ACarAtTheKerbJoinsTheLineOnlyWithThePullOut()
    {
        var (graph, route) = North();
        var check = new Feasibility(graph, Small(), _ => null);
        // Parked 2.8 m to the right of the lane (east of a lane going north), its rear axle level with the route's start.
        var pose = new CarPose(2.8f, 22f, 0f);
        var plain = DrivePlan.Build(graph, route, Small(), check.Arc, check.Clear);
        Assert.False(plain.Line(graph).Joins(pose.X, pose.Z, pose.Heading, out _));
        var plan = PulledOut(graph, route, check, 2.8f, 2f);
        var line = plan.Line(graph);
        Assert.True(line.Joins(pose.X, pose.Z, pose.Heading, out _));
        Assert.Equal(2.8f, line.Line.At(2f).X, 1);
        Assert.Equal(0f, line.Line.At(2f + DriveLine.ShiftRamp + 1f).X, 1);
        Assert.Null(check.Join(plan, line, pose));
    }

    [Fact]
    public void ACarParkedTightlyAheadIsMetOnThePullOut()
    {
        var (graph, route) = North();
        // Another car's back 4.5 m ahead of the rear axle, in the same row of parked cars.
        var check = new Feasibility(graph, Small(), Post(2.8f, 26.5f));
        var plan = PulledOut(graph, route, check, 2.8f, 2f);
        Assert.StartsWith("joining the new line: parked car", check.Join(plan, plan.Line(graph), new CarPose(2.8f, 22f, 0f)));
    }
}
