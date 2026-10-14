using System.Linq;
using DrivenRides.Core.Drive;
using DrivenRides.Core.Routing;
using Xunit;

namespace DrivenRides.Core.Tests;

// The option "Rides ignore speed limits": the adapter plans with no road limit (limitKmh null), and the plan is then
// held only by the car's top speed and its corners.
public sealed class SpeedLimitTests
{
    private static CarSpec Car() => new("small", 2.5f, _ => 40f, new Vec3(0, 0.7f, 1.25f), new Vec3(0.9f, 0.7f, 2f), 80f, 1.5f);

    private static (LaneGraph Graph, int[] Lane) North()
    {
        var b = new TurnTestGraph();
        var lane = Enumerable.Range(0, 13).Select(i => b.Add(0, i * 10)).ToArray();
        b.Link(lane);
        return (b.Build(), lane);
    }

    [Fact]
    public void WithLimitsTheCarIsHeldToLimitFactorOfTheRoadsLimit()
    {
        var (graph, lane) = North();
        var check = new Feasibility(graph, Car(), _ => null);
        var plan = DrivePlan.Build(graph, lane, Car(), check.Arc, check.Clear, _ => 50f);
        Assert.Equal(lane.Length, plan.Slow.Count);
        Assert.All(plan.Slow, s => Assert.Equal(50f * DrivePlan.LimitFactor, s.SpeedKmh, 3));
    }

    [Fact]
    public void WithoutLimitsOnlyTheCarsTopSpeedHoldsItOnAStraightLane()
    {
        var (graph, lane) = North();
        var check = new Feasibility(graph, Car(), _ => null);
        var plan = DrivePlan.Build(graph, lane, Car(), check.Arc, check.Clear, null);
        Assert.Empty(plan.Slow);
        Assert.Equal(80f, plan.TopKmh);
    }
}
