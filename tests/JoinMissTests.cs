using System;
using System.Linq;
using DrivenRides.Core.Drive;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;
using Xunit;

namespace DrivenRides.Core.Tests;

// The own car's start check says which of the join conditions failed (DriveLine.JoinMiss).
public sealed class JoinMissTests
{
    // A line north along x = 0 from z = 0 to z = 100.
    private static DriveLine North() =>
        DriveLine.Build(Enumerable.Range(0, 11).Select(k => new Vec3(0, 0, k * 10)).ToList(), Array.Empty<(int, Polyline)>());

    [Fact]
    public void ACarOnTheLineJoins()
    {
        Assert.Null(North().JoinMiss(0.5f, 20f, 0.1, out var along));
        Assert.Equal(20f, along, 1);
    }

    [Fact]
    public void ACarBesideTheLineIsTooFar()
    {
        var miss = North().JoinMiss(3f, 20f, 0, out _);
        Assert.StartsWith("3.0 m from the line", miss);
    }

    [Fact]
    public void ACarFacingAcrossTheLaneIsOffHeading()
    {
        var miss = North().JoinMiss(0f, 20f, Math.PI / 2, out _);
        Assert.StartsWith("facing 90 degrees off the line", miss);
    }

    [Fact]
    public void ACarNearTheEndHasTooLittleLine()
    {
        var miss = North().JoinMiss(0f, 95f, 0, out _);
        Assert.StartsWith("5 m of line left", miss);
    }
}
