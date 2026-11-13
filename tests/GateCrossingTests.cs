using System.Linq;
using DrivenRides.Core.Routing;
using Xunit;

namespace DrivenRides.Core.Tests;

public class GateCrossingTests
{
    private static (LaneGraph Graph, int[] Lane, RoadGate Gate) Gated()
    {
        var b = new TurnTestGraph();
        var lane = Enumerable.Range(0, 8).Select(i => b.Add(0, i * 7)).ToArray();
        b.Link(lane);
        return (b.Build(), lane, new RoadGate { MinX = -3f, MaxX = 3f, MinZ = 26f, MaxZ = 30f, Y = 0f, Locked = true, Name = "gate" });
    }

    [Fact]
    public void TheStepsThroughTheGateAreFound()
    {
        var (graph, lane, gate) = Gated();
        Assert.Equal(new[] { (lane[3], lane[4]), (lane[4], lane[5]) }.OrderBy(s => s.Item1), GateCrossing.Steps(graph, gate).OrderBy(s => s.Item1));
    }

    [Fact]
    public void ARideThatCantGoThroughEndsBeforeTheGateWithRoomToStop()
    {
        var (graph, lane, gate) = Gated();
        Assert.Equal(3, GateCrossing.DropOff(graph, lane, 3, gate, 3f));
        Assert.Equal(2, GateCrossing.DropOff(graph, lane, 3, gate, 6f));
        Assert.Equal(-1, GateCrossing.DropOff(graph, lane, 3, gate, 40f));
    }
}
