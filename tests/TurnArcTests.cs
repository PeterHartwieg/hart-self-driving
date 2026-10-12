using System;
using System.Linq;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;
using Xunit;

namespace DrivenRides.Core.Tests;

public sealed class TurnArcTests
{
    // Entry heading north at (0, -20), exit heading east at (10, 10): the lanes cross at (0, 10). The exit waypoint
    // 10 m past the crossing allows 12 m of tangent (2 m of slack), so the arc is 12 m wide, centred on (12, -2).
    [Fact]
    public void FitsTheWidestArcTheJunctionAllows()
    {
        var line = TurnArc.Fit(new Vec3(0, 0, -20), 0, 1, new Vec3(10, 0, 10), 1, 0, 5f)!;
        Assert.NotNull(line);
        var arc = line.Points.Where(p => p.Z > -2.01f && p.X < 12.01f).ToList();
        Assert.Equal(-20 - TurnArc.LeadIn, line.Points[0].Z, 2);
        Assert.All(arc, p => Assert.InRange(Math.Sqrt((p.X - 12) * (p.X - 12) + (p.Z + 2) * (p.Z + 2)), 11.9, 12.1));
        var end = line.Points[^1];
        Assert.InRange(end.X, 21.9, 22.1);
        Assert.InRange(end.Z, 9.9, 10.1);
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(-1f)]
    public void TurnsEitherWay(float side)
    {
        var line = TurnArc.Fit(new Vec3(0, 0, -20), 0, 1, new Vec3(10 * side, 0, 10), side, 0, 5f)!;
        foreach (var p in line.Points) Assert.True(p.X * side > -0.01f, $"({p.X}, {p.Z}) on the wrong side");
        Assert.InRange(line.Points[^1].X * side, 21.9, 22.1);
    }

    [Fact]
    public void NoArcWhereTheCarCannotFollowOne()
    {
        // 3 m of room: an arc of 3 m, under a 5.74 m car's circle.
        Assert.Null(TurnArc.Fit(new Vec3(0, 0, 7), 0, 1, new Vec3(1, 0, 10), 1, 0, 5.74f));
        // Straight on and a U-turn are not one arc's work.
        Assert.Null(TurnArc.Fit(new Vec3(0, 0, 0), 0, 1, new Vec3(1, 0, 20), 0.1f, 0.995f, 5f));
        Assert.Null(TurnArc.Fit(new Vec3(0, 0, 0), 0, 1, new Vec3(6, 0, 0), 0, -1, 5f));
    }

    // The 6.7 m car pursuing the 12 m arc stays near it and comes out straight in the exit lane.
    [Fact]
    public void ACarFollowsTheArc()
    {
        var line = TurnArc.Fit(new Vec3(0, 0, -20), 0, 1, new Vec3(10, 0, 10), 1, 0, 5.74f)!;
        var car = new CarShape(4.02f, 35f, 2.4f, 0.6f, 0.39f);
        var poses = TurnDrive.Pursue(line, car, 25 / 3.6f, 3f, 3f, 0.25f)!;
        Assert.NotNull(poses);
        var worst = 0.0;
        foreach (var p in poses)
        {
            // Its front axle, which it steers by.
            double fx = p.X + 2.4 * Math.Sin(p.Heading), fz = p.Z + 2.4 * Math.Cos(p.Heading);
            worst = Math.Max(worst, line.Points.Min(q => Math.Sqrt((q.X - fx) * (q.X - fx) + (q.Z - fz) * (q.Z - fz))));
        }
        Assert.True(worst < 0.8, $"the front axle came {worst:0.00} m off the line");
        var end = poses[^1];
        Assert.InRange(end.Z, 9.6, 10.4);
        Assert.InRange(end.X + 2.4, 18, 22);
        Assert.InRange(end.Heading * 180 / Math.PI, 85, 95);
    }
}
