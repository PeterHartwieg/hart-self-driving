using System;
using System.Linq;
using DrivenRides.Core.Routing;
using DrivenRides.Core.Testing;
using Xunit;

namespace DrivenRides.Core.Tests;

public sealed class GateTests
{
    [Fact]
    public void CrossingIsDirectedAndUsesTheIntersectionNotTheEndpoints()
    {
        var gate = new Gate("test", new Vec3(0, 0, 0), 0, 2);
        Assert.True(gate.Crossed(new Vec3(-6, 0, -3), new Vec3(6, 0, 3)));
        Assert.False(gate.Crossed(new Vec3(0, 0, 3), new Vec3(0, 0, -3)));
        Assert.False(gate.Crossed(new Vec3(5, 0, -3), new Vec3(5, 0, 3)));
        Assert.True(gate.Crossed(new Vec3(4, 0, -3), new Vec3(4, 0, 3)));
        Assert.False(gate.Crossed(new Vec3(0, 3, -3), new Vec3(0, 3, 3)));
        Assert.True(gate.Crossed(new Vec3(0, 4, -3), new Vec3(0, 0, 3)));
        Assert.False(gate.Crossed(new Vec3(0, 0, 0), new Vec3(0, 0, 0)));
        Assert.True(gate.Crossed(new Vec3(0, 0, -3), new Vec3(0, 0, 0)));
        Assert.Equal(3, gate.Lateral(new Vec3(3, 0, 7)));
        Assert.Equal(-3, new Gate("east", new Vec3(0, 0, 0), 1, 0).Lateral(new Vec3(0, 0, 3)));
    }

    [Fact]
    public void GeometryUsesCorrectRouteAndInterpolatesLastGate()
    {
        var (graph, entry, exit) = TurnTestGraph.Turn();
        var f = Catalogue.Build(graph).Single(c => c.Kind == CaseKind.Turn && c.Route[c.EntryIndex] == entry && c.Route[c.ExitIndex] == exit);
        f.Path = new() { 0, 0 }; f.SlowPath = new() { 0 };
        var geometry = CaseGeometry.Build(graph, f);
        Assert.Equal(graph.Position(entry), geometry.Gates[0].Point);
        Assert.Equal(graph.Position(exit), geometry.Gates[1].Point);
        Assert.Equal(40, geometry.Gates[2].Point.X);
        Assert.True(geometry.ExpandedPolyline.Count > 2);
        f.Route = new() { entry, exit, exit + 1 }; f.EntryIndex = 0; f.ExitIndex = 1;
        Assert.Equal(graph.Position(exit), CaseGeometry.Build(graph, f).Gates[2].Point);
    }

    [Fact]
    public void LastGateIsInterpolatedAcrossHeightChanges()
    {
        var b = new TurnTestGraph(); int a = b.Add(0, -10), e = b.Add(0, 0), x = b.Add(0, 10), last = b.Add(0, 50, y: 4), end = b.Add(0, 60, y: 4);
        b.Link(a, e, x, last, end);
        var f = new Fixture { Route = new() { a, e, x, last, end }, EntryIndex = 1, ExitIndex = 2 };
        var gate = CaseGeometry.Build(b.Build(), f).Gates[2];
        Assert.Equal(new Vec3(0, 3, 40), gate.Point);
    }
}
