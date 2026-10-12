using System;
using System.Linq;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;
using Xunit;

namespace DrivenRides.Core.Tests;

public sealed class TurnDriveTests
{
    // Turn test 0.1.39, turn-14053-18249: a 6.7 m car (wheel distance 4.02 m, max steer 35 degrees, front axle
    // 2.4 m ahead of its middle, centre of mass 0.39 m ahead and about 0.6 m up) turning left at 25 km/h with 3x steering, through two vias
    // straight on. The recorded middle of the car passed these points before it hit a traffic light.
    [Fact]
    public void FollowsTheRecordedTrackOfALeftTurn()
    {
        var car = new CarShape(4.02f, 35f, 2.4f, 0.6f, 0.39f);
        var targets = new[] { new Vec3(308.76f, 0, 225.06f), new Vec3(308.75f, 0, 218.32f), new Vec3(329.41f, 0, 212.46f) };
        var poses = TurnDrive.Drive(new Vec3(308.76f, 0, 231.02f), 0, -1, targets, car, 25 / 3.6f, 3f, 0.1f)!;
        foreach (var (x, z) in new[] { (308.88, 221.86), (309.29, 220.54), (310.0, 219.37), (310.96, 218.39), (312.08, 217.58) })
            Assert.True(poses.Min(p => Math.Sqrt((p.X - x) * (p.X - x) + (p.Z - z) * (p.Z - z))) < 0.35, $"no pose near ({x}, {z})");
        // It stops where Gley moves on from the aim: the centre of mass, 0.39 m ahead of the middle, within 5 m of it.
        var end = poses[^1];
        Assert.InRange(Math.Sqrt((end.X - 329.41) * (end.X - 329.41) + (end.Z - 212.46) * (end.Z - 212.46)), 5.1, 5.5);
    }

    [Fact]
    public void AStraightRunStaysOnTheLine()
    {
        var car = new CarShape(3f, 35f, 1.5f);
        var poses = TurnDrive.Drive(new Vec3(0, 0, 0), 0, 1, new[] { new Vec3(0, 0, 10), new Vec3(0, 2, 30) }, car, 7f, 3f)!;
        Assert.All(poses, p => Assert.InRange(p.X, -0.01, 0.01));
        Assert.InRange(poses[^1].Z, 24.9, 25.2);
        Assert.InRange(poses[^1].Y, 1.5, 2.0);
    }
}
