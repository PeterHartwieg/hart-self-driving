using System;
using System.Linq;
using DrivenRides.Core.Drive;
using DrivenRides.Core.Routing;
using Xunit;

namespace DrivenRides.Core.Tests;

public sealed class CorridorOvershootTests
{
    private sealed class GiveWayAt : ILiveFlags
    {
        private readonly int line;
        public GiveWayAt(int line) => this.line = line;
        public bool Stop(int waypoint) => false;
        public bool GiveWayEntry(int waypoint, out int junction, out int road)
        {
            junction = waypoint == line ? 0 : -1;
            road = waypoint == line ? 2 : -1;
            return waypoint == line;
        }
    }

    private static (LaneGraph Graph, int First, int Line) Build()
    {
        var b = new TurnTestGraph();
        var first = b.Add(0, -30);
        var before = b.Add(0, -15);
        var line = b.Add(0, 0);
        var close = new int[6];
        for (var k = 0; k < 6; k++) close[k] = b.Add(0, 0.5f + k * 0.5f);
        var fork = close[5];
        var north = new[] { b.Add(0, 6), b.Add(0, 12), b.Add(0, 20) };
        var east = new[] { b.Add(3, 6), b.Add(8, 9), b.Add(15, 10) };
        b.Link(first, before, line);
        b.Link(new[] { line }.Concat(close).ToArray());
        b.Fork(fork, north[0], east[0]);
        b.Link(north);
        b.Link(east);
        return (b.Build(), first, line);
    }

    private static bool Covered(Corridor c, float x, float z) =>
        c.Covers(new Box(x, z, 0f, 0.2f, 0.2f), out _, out _, out _, out _);

    [Fact]
    public void TooFastToStopAtTheLineItOvershootsOnEveryWay()
    {
        var (graph, first, line) = Build();
        var corridor = new Corridor();
        var car = new OtherCar { Id = 1, X = 0f, Z = -7.25f, Heading = 0f, Vz = 10f, Length = 4.5f, Width = 1.9f, TriggerLength = 3f, Target = line };
        corridor.Build(graph, car, new GiveWayAt(line), 6f, 0f, _ => 1);
        Assert.True(Covered(corridor, 0f, 2f));
        Assert.True(Covered(corridor, 0f, 6.5f));
        Assert.True(Covered(corridor, 2.5f, 5.5f));
        Assert.False(Covered(corridor, 0f, 11f));
        Assert.False(Covered(corridor, 8f, 9f));
    }

    [Fact]
    public void AbleToStopItEndsAtTheLine()
    {
        var (graph, _, line) = Build();
        var corridor = new Corridor();
        var car = new OtherCar { Id = 1, X = 0f, Z = -17.25f, Heading = 0f, Vz = 6f, Length = 4.5f, Width = 1.9f, TriggerLength = 3f, Target = line };
        corridor.Build(graph, car, new GiveWayAt(line), 6f, 0f, _ => 1);
        Assert.False(Covered(corridor, 0f, 2f));
    }
}
