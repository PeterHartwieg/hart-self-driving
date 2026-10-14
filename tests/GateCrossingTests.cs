using System.Linq;
using DrivenRides.Core.Routing;
using Xunit;

namespace DrivenRides.Core.Tests;

// A gate across a lane (the game's FenceDoor; T3 on 0.1.81, ride 16's Hamptons gate): which steps go through it, and
// where a ride that can't go through ends.
public class GateCrossingTests
{
    // A lane north, a waypoint every 7 m, and a gate (its doors and trigger) across it from z 26 to 30.
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
        // 21 -> 28 enters the box, 28 -> 35 leaves it.
        Assert.Equal(new[] { (lane[3], lane[4]), (lane[4], lane[5]) }.OrderBy(s => s.Item1), GateCrossing.Steps(graph, gate).OrderBy(s => s.Item1));
    }

    [Fact]
    public void ARideThatCantGoThroughEndsBeforeTheGateWithRoomToStop()
    {
        var (graph, lane, gate) = Gated();
        // Crossing at step 3 (21 -> 28): waypoint 3 at z 21 is 5 m short of the box, more than the 3 m clear.
        Assert.Equal(3, GateCrossing.DropOff(graph, lane, 3, gate, 3f));
        // With 6 m to keep clear, one waypoint further back (z 14).
        Assert.Equal(2, GateCrossing.DropOff(graph, lane, 3, gate, 6f));
        Assert.Equal(-1, GateCrossing.DropOff(graph, lane, 3, gate, 40f));
    }
}
