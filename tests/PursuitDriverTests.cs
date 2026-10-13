using System;
using System.Collections.Generic;
using System.Linq;
using DrivenRides.Core.Drive;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;
using Xunit;

namespace DrivenRides.Core.Tests;

public sealed class PursuitDriverTests
{
    // A kinematic car (bicycle model about the rear axle) with a wheel that turns at most `rate` degrees a second
    // and locks at `lockDegrees`, driven by the driver at the speed it asks for (pedals ignored).
    private static List<(float X, float Z, float Lateral, float Speed)> Drive(DriveLine line, float wheelbase, float lockDegrees,
        float startX, float startZ, float heading, float topMs = 14f, Func<float, float?>? stopAt = null, int steps = 4000)
    {
        var driver = new PursuitDriver(line, wheelbase, wheelbase + 1f, topMs);
        double x = startX, z = startZ, h = heading, steer = 0, speed = 0;
        const float dt = 0.02f;
        var track = new List<(float, float, float, float)>();
        for (var i = 0; i < steps; i++)
        {
            var o = driver.Step(new DriveInput { RearX = (float)x, RearZ = (float)z, Heading = (float)h, SpeedMs = (float)speed, Dt = dt, StopAlong = stopAt?.Invoke(driver.Along) });
            var want = Math.Max(-lockDegrees, Math.Min(lockDegrees, o.SteerDegrees));
            steer += Math.Max(-180 * dt, Math.Min(180 * dt, want - steer));
            // The car reaches the desired speed at up to 3 m/s² either way.
            speed += Math.Max(-3 * dt, Math.Min(3 * dt, o.Desired - speed));
            h += speed * Math.Tan(steer * Math.PI / 180) / wheelbase * dt;
            x += speed * Math.Sin(h) * dt;
            z += speed * Math.Cos(h) * dt;
            track.Add(((float)x, (float)z, o.Lateral, (float)speed));
            if (o.Along >= line.Line.Length - wheelbase - 1.5f && speed < 0.05) break;
        }
        return track;
    }

    private static List<Vec3> Straight(Vec3 from, Vec3 to, float every = 8f)
    {
        var n = (int)Math.Ceiling(Vec3.HorizontalDistance(from, to) / every);
        return Enumerable.Range(0, n + 1).Select(k => from + (to - from) * ((float)k / n)).ToList();
    }

    [Fact]
    public void FollowsADriversArcRoundACorner()
    {
        // North along x = 0, a right turn into an eastbound lane at z = 30: the lanes cross at (0, 30).
        var arc = TurnArc.Fit(new Vec3(0, 0, 20), 0, 1, new Vec3(12, 0, 30), 1, 0, 6f)!;
        Assert.NotNull(arc);
        var points = Straight(new Vec3(0, 0, -40), new Vec3(0, 0, 20)).Concat(Straight(new Vec3(12, 0, 30), new Vec3(80, 0, 30)).Skip(0)).ToList();
        var entry = points.FindIndex(p => p.Z == 20 && p.X == 0);
        var line = DriveLine.Build(points, new[] { (entry, arc) });
        var track = Drive(line, 3.75f, 35f, 0, -40, 0);
        // On the arc and after it, the rear axle stays within 0.4 m of the line; it ends on the exit lane.
        Assert.All(track.Skip(100), t => Assert.InRange(t.Lateral, -0.4f, 0.4f));
        var end = track[^1];
        Assert.InRange(end.Z, 29.6f, 30.4f);
        Assert.True(end.X > 60f);
        // It slows for the corner: no faster than the arc's corner speed plus a little.
        var cornerSpeed = Math.Sqrt(PursuitDriver.Lateral * arc.Radius);
        Assert.All(track.Where(t => t.X > 1f && t.Z < 29f), t => Assert.True(t.Speed <= cornerSpeed + 0.5, $"{t.Speed} m/s at ({t.X}, {t.Z})"));
    }

    [Fact]
    public void ComesBackToTheLineFromASideOffset()
    {
        var line = DriveLine.Build(Straight(new Vec3(0, 0, 0), new Vec3(0, 0, 200)), Array.Empty<(int, Polyline)>());
        var track = Drive(line, 2.8f, 35f, 1.5f, 0, 0);
        var settled = track.Where(t => t.Z > 60f).ToList();
        Assert.NotEmpty(settled);
        Assert.All(settled, t => Assert.InRange(t.X, -0.15f, 0.15f));
    }

    [Fact]
    public void StopsAtAStopLine()
    {
        var line = DriveLine.Build(Straight(new Vec3(0, 0, 0), new Vec3(0, 0, 300)), Array.Empty<(int, Polyline)>());
        // The front (wheelbase + 1 m ahead of the rear axle) must stop by z = 120.
        var track = Drive(line, 2.8f, 35f, 0, 0, 0, stopAt: _ => 120f, steps: 3000);
        var front = track[^1].Z + 3.8f;
        Assert.True(track[^1].Speed < 0.1f);
        Assert.InRange(front, 110f, 120.2f);
    }

    [Fact]
    public void SlowStretchIsReachedAtItsSpeed()
    {
        var line = DriveLine.Build(Straight(new Vec3(0, 0, 0), new Vec3(0, 0, 300)), Array.Empty<(int, Polyline)>());
        var driver = new PursuitDriver(line, 2.8f, 3.8f, 14f);
        driver.Slow(150f, 170f, 7f);
        Assert.Equal(14f, driver.PlannedSpeed(0f, 14f));
        Assert.InRange(driver.PlannedSpeed(150f, 7f), 6.99f, 7.01f);
        // 30 m before the stretch, no faster than slowing from there at Decel allows.
        Assert.InRange(driver.PlannedSpeed(120f, 14f), 7f, (float)Math.Sqrt(49 + 2 * PursuitDriver.Decel * 30) + 0.01f);
    }

    [Fact]
    public void ArcReplacesTheWaypointsItCutsPast()
    {
        var arc = TurnArc.Fit(new Vec3(0, 0, 20), 0, 1, new Vec3(12, 0, 30), 1, 0, 6f)!;
        var points = Straight(new Vec3(0, 0, -40), new Vec3(0, 0, 20)).Concat(Straight(new Vec3(12, 0, 30), new Vec3(80, 0, 30))).ToList();
        var entry = points.FindIndex(p => p.Z == 20 && p.X == 0);
        var line = DriveLine.Build(points, new[] { (entry, arc) });
        // Along grows with the path, and no point of the line is behind the one before it on the exit lane.
        for (var k = 1; k < points.Count; k++) Assert.True(line.AlongOf[k] >= line.AlongOf[k - 1] - 0.01f, $"position {k}");
        var exitLane = line.Line.Points.Where(p => p.Z > 29.9f).Select(p => p.X).ToList();
        for (var k = 1; k < exitLane.Count; k++) Assert.True(exitLane[k] > exitLane[k - 1]);
    }

    [Fact]
    public void LocatesAPointBesideTheLine()
    {
        var line = DriveLine.Build(Straight(new Vec3(0, 0, 0), new Vec3(0, 0, 100)), Array.Empty<(int, Polyline)>());
        var (along, side) = line.Locate(1.5f, 40f, 30f, 20f);
        Assert.InRange(along, 39.9f, 40.1f);
        Assert.InRange(side, 1.49f, 1.51f);
        Assert.InRange(line.Locate(-2f, 45f, 30f, 20f).Lateral, -2.01f, -1.99f);
    }
}
