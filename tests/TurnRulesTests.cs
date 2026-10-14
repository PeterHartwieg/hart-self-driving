using DrivenRides.Core.Routing;
using DrivenRides.Core.Testing;
using Xunit;

namespace DrivenRides.Core.Tests;

// 0.1.100: the largest arc of a left turn swung from the stop line into the oncoming lanes. A turn's body keeps to
// the junction and its own way's lanes.
public class TurnRulesTests
{
    [Fact]
    public void AnOncomingLaneOutsideTheJunctionIsAnotherWays()
    {
        var b = new TurnTestGraph();
        int n0 = b.Add(0, 0), n1 = b.Add(0, 10), n2 = b.Add(0, 20);
        b.Link(n0, n1, n2);
        int s0 = b.Add(-4, 20), s1 = b.Add(-4, 10), s2 = b.Add(-4, 0);
        b.Link(s0, s1, s2);
        var graph = b.Build();
        var north = new[] { (0f, 1f) };
        Assert.True(Pavement.OnAnotherWaysLane(graph, new Vec3(-4f, 0f, 10f), north));
        Assert.False(Pavement.OnAnotherWaysLane(graph, new Vec3(0.5f, 0f, 10f), north));
        // Turning onto the southbound lane, it is the car's own way.
        Assert.False(Pavement.OnAnotherWaysLane(graph, new Vec3(-4f, 0f, 10f), new[] { (0f, 1f), (0f, -1f) }));
        // Off every lane: the pavement rule's, not this one's.
        Assert.False(Pavement.OnAnotherWaysLane(graph, new Vec3(10f, 0f, 10f), north));
    }
}

public class RoadsMeetTests
{
    // Two roads of two lanes, one each way, 4 m apart, crossing at the origin: their centre lines meet there.
    [Fact]
    public void TheMiddleIsWhereTheRoadsCentreLinesCross()
    {
        var b = new TurnTestGraph();
        // North-south road: northbound at x = 2, southbound at x = -2.
        int n0 = b.Add(2, -40), n1 = b.Add(2, -30), n2 = b.Add(2, -20), n3 = b.Add(2, -10);
        b.Link(n0, n1, n2, n3);
        int s0 = b.Add(-2, -10), s1 = b.Add(-2, -20), s2 = b.Add(-2, -30), s3 = b.Add(-2, -40);
        b.Link(s0, s1, s2, s3);
        // East-west road: westbound at z = 2, eastbound at z = -2.
        int w0 = b.Add(-10, 2), w1 = b.Add(-20, 2), w2 = b.Add(-30, 2), w3 = b.Add(-40, 2);
        b.Link(w0, w1, w2, w3);
        int e0 = b.Add(-40, -2), e1 = b.Add(-30, -2), e2 = b.Add(-20, -2), e3 = b.Add(-10, -2);
        b.Link(e0, e1, e2, e3);
        var graph = b.Build();
        var middle = DrivenRides.Core.Ride.TurnArc.RoadsMeet(graph, graph.Position(n3), 0, 1, graph.Position(w0), -1, 0);
        Assert.NotNull(middle);
        Assert.InRange(middle!.Value.X, -0.01f, 0.01f);
        Assert.InRange(middle.Value.Z, -0.01f, 0.01f);
    }
}
