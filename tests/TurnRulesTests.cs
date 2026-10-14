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
