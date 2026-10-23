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
    // Core's CarModel, locked at `lockDegrees`, driven by the driver at the speed it asks for.
    private static List<(float X, float Z, float Lateral, float Speed)> Drive(DriveLine line, float wheelbase, float lockDegrees,
        float startX, float startZ, float heading, float topMs = 14f, Func<float, float?>? stopAt = null, int steps = 4000)
    {
        var driver = new PursuitDriver(line, wheelbase, wheelbase + 1f, topMs);
        var car = new CarModel(wheelbase, lockDegrees, startX, startZ, heading);
        const float dt = 0.02f;
        var track = new List<(float, float, float, float)>();
        for (var i = 0; i < steps; i++)
        {
            var input = car.Input(dt);
            input.StopAlong = stopAt?.Invoke(driver.Along);
            var o = driver.Step(input);
            car.Step(o, dt);
            track.Add(((float)car.X, (float)car.Z, o.Lateral, (float)car.Speed));
            if (o.Along >= line.Line.Length - wheelbase - 1.5f && car.Speed < 0.05) break;
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
        // Faster cornering trades some precision for time: the old 4 m/s² tune stayed within 0.6 m here; at 6 it
        // stays within 0.8 m. The exit-lane and corner-speed checks below retain their tighter bounds.
        Assert.All(track.Skip(100), t => Assert.InRange(t.Lateral, -0.8f, 0.8f));
        var end = track[^1];
        Assert.InRange(end.Z, 29.6f, 30.4f);
        Assert.True(end.X > 60f);
        // It slows for the corner: no faster than the arc's corner speed plus a little.
        var cornerSpeed = Math.Sqrt(PursuitDriver.Lateral * arc.Radius);
        Assert.All(track.Where(t => t.X > 1f && t.Z < 29f), t => Assert.True(t.Speed <= cornerSpeed + 0.5, $"{t.Speed} m/s at ({t.X}, {t.Z})"));
    }

    [Theory]
    [InlineData(6.5f, 11.14f)]
    [InlineData(12f, 11.46f)]
    [InlineData(20f, 11.88f)]
    public void FasterCornersSaveTimeAndSettleOnTheExitLane(float radius, float previousSeconds)
    {
        // Matched 4 m/s² baseline: start 60 m before the arc, finish 20 m beyond it. Include braking and
        // acceleration, rather than testing only the requested speed. Steering lag and lock remain active.
        var exitZ = 20f + radius;
        var arc = TurnArc.Fit(new Vec3(0, 0, 20), 0, 1, new Vec3(radius, 0, exitZ), 1, 0, 3f, radius)!;
        var points = Enumerable.Range(0, 9).Select(k => new Vec3(0, 0, -40 + k * 7.5f))
            .Concat(Enumerable.Range(0, 11).Select(k => new Vec3(radius + k * 7, 0, exitZ))).ToList();
        var track = Drive(DriveLine.Build(points, new[] { (8, arc) }), 3.75f, 35f, 0, -40, 0);
        var arrival = track.FindIndex(t => t.X >= radius + 20f);
        Assert.True(arrival >= 0, "The car did not clear the corner.");
        Assert.True((arrival + 1) * 0.02f <= previousSeconds * 0.95f, "The complete corner approach must save at least 5%.");
        Assert.All(track.Take(arrival + 1), t => Assert.InRange(t.Lateral, -0.9f, 0.9f));
        Assert.InRange(track[arrival].Z, exitZ - 0.15f, exitZ + 0.15f);
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

    [Fact]
    public void DoesNotRunWideOutOfASharpCorner()
    {
        // A right angle at a via, as an added turn without an arc: north along x = 0, then east along z = 60.
        var points = Straight(new Vec3(0, 0, 0), new Vec3(0, 0, 60)).Concat(Straight(new Vec3(0, 0, 60), new Vec3(120, 0, 60)).Skip(1)).ToList();
        var line = DriveLine.Build(points, Array.Empty<(int, Polyline)>());
        var track = Drive(line, 3.75f, 37f, 0, 0, 0);
        // Once out of the corner (20 m on along the exit), the rear axle is back within 0.3 m of the lane and it never
        // swings more than 1.5 m past it on the far side.
        Assert.All(track.Where(t => t.X > 20f), t => Assert.InRange(t.Z, 59.7f, 60.3f));
        Assert.All(track, t => Assert.True(t.Z < 61.5f, $"({t.X}, {t.Z}) swung wide"));
    }

    // A traffic car with no target known: its corridor runs straight on along its heading.
    private static OtherCar Other(int id, float x, float z, float vx, float vz) => new()
    {
        Id = id, X = x, Z = z, Heading = (float)Math.Atan2(vx == 0 && vz == 0 ? 0 : vx, vx == 0 && vz == 0 ? 1 : vz), Vx = vx, Vz = vz,
        Length = 4.5f, Width = 1.9f, TriggerLength = 3f, Target = -1,
    };

    private sealed class NoFlags : ILiveFlags
    {
        public bool Stop(int waypoint) => false;
        public bool GiveWayEntry(int waypoint, out int junction, out int road) { junction = road = -1; return false; }
    }

    private static TrafficDecision Decide(DriveLine line, float rearZ, float speed, params OtherCar[] cars)
    {
        var driver = new PursuitDriver(line, 3f, 4f, 14f);
        driver.StartAt(0, rearZ);
        driver.Step(new DriveInput { RearX = 0, RearZ = rearZ, Heading = 0, SpeedMs = speed, Dt = 0.02f });
        var graph = new TurnTestGraph();
        graph.Add(0, 0);
        var traffic = new TrafficDriver(graph.Build(), line, driver, new RideBody { Wheelbase = 3f, FrontAhead = 4f, RearBehind = 1f, HalfWidth = 0.95f });
        var at = driver.Along;
        var heading = 0f;
        return traffic.Decide(new TrafficSnapshot
        {
            RearX = line.Line.At(at).X, RearZ = line.Line.At(at).Z, Heading = heading, Speed = speed, Marks = Array.Empty<PathMark>(), Cars = cars, Flags = new NoFlags(),
        }, new TrafficDecision());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ZeroDistanceDropOffHoldsWithoutSweepingANonexistentSegment(int points)
    {
        // rc4 in game: a ride accepted a singleton route and every FixedUpdate threw from TrafficDriver.Sweep.
        // Coincident waypoints also become a singleton when DriveLine removes their zero-length segment.
        var line = DriveLine.Build(Enumerable.Repeat(new Vec3(0, 0, 0), points).ToArray(), Array.Empty<(int, Polyline)>());
        Assert.Single(line.Line.Points);
        var decision = Decide(line, 0f, 0f);
        Assert.Equal(0f, decision.MaxSpeed);
        Assert.Equal(0f, decision.StopAlong);
        var driver = new PursuitDriver(line, 3f, 4f, 14f);
        var input = driver.Step(new DriveInput { Dt = 0.02f, MaxSpeed = decision.MaxSpeed, StopAlong = decision.StopAlong });
        Assert.Equal(0f, input.Throttle);
        Assert.Equal(1f, input.Handbrake);
    }

    [Fact]
    public void GivesWayToACrossingCarOnly()
    {
        var line = DriveLine.Build(Straight(new Vec3(0, 0, 0), new Vec3(0, 0, 200)), Array.Empty<(int, Polyline)>());
        // From the right at 10 m/s, 15 m off, crossing 8 m ahead of the front (z 20): give way short of where the
        // body would enter its way.
        var crossing = Decide(line, 8f, 5f, Other(1, 15f, 20f, -10f, 0f));
        Assert.NotNull(crossing.YieldAlong);
        Assert.InRange(crossing.YieldAlong!.Value, 12f, 18.5f);
        // Following on the line, a lane over in the same direction, oncoming a lane over, or standing beside: no.
        Assert.Null(Decide(line, 8f, 5f, Other(2, 0f, -2f, 0f, 12f)).YieldAlong);
        Assert.Null(Decide(line, 8f, 5f, Other(3, 3.5f, 8f, 0f, 12f)).YieldAlong);
        Assert.Null(Decide(line, 8f, 5f, Other(4, -3.2f, 60f, 0f, -12f)).YieldAlong);
        var standing = Decide(line, 8f, 5f, Other(5, 15f, 20f, 0f, 0f));
        Assert.Null(standing.YieldAlong);
        Assert.Null(standing.GapAhead);
        // A car that has already crossed and is moving away: no (NWH spike, nwh6: it stood 9 s after they had passed).
        Assert.Null(Decide(line, 8f, 5f, Other(6, -4f, 20f, -10f, 0f)).YieldAlong);
    }

    [Fact]
    public void GivesWayWhereTheBodyMeetsTheirWayNotTheLine()
    {
        // North, then a left turn at z 20 into a westbound lane at z 30; an oncoming (southbound) car will cross the
        // turn. The body enters its lane before the line does.
        var arc = TurnArc.Fit(new Vec3(0, 0, 20), 0, 1, new Vec3(-12, 0, 30), -1, 0, 6f)!;
        var points = Straight(new Vec3(0, 0, 0), new Vec3(0, 0, 20)).Concat(Straight(new Vec3(-12, 0, 30), new Vec3(-80, 0, 30))).ToList();
        var entry = points.FindIndex(p => Math.Abs(p.Z - 20) < 0.01f && p.X == 0);
        var line = DriveLine.Build(points, new[] { (entry, arc) });
        var give = Decide(line, 12f, 5f, Other(1, -3.5f, 60f, 0f, -10f)).YieldAlong;
        Assert.NotNull(give);
        // Where the line itself reaches the oncoming lane (x = -3.5 + 0.95 + 0.2), and the yield point must be well
        // short of it.
        var lineMeets = 0f;
        for (var s = 0f; s < line.Line.Length; s += 0.1f) if (line.Line.At(s).X <= -2.35f) { lineMeets = s; break; }
        Assert.True(give!.Value - 4f < lineMeets - 2f, $"gives way with the front at {give} m, the line meets their lane at {lineMeets} m");
    }

    [Fact]
    public void CreepsThroughTheTriggerOfACarWaitingForItAndNoFurther()
    {
        var line = DriveLine.Build(Straight(new Vec3(0, 0, 0), new Vec3(0, 0, 200)), Array.Empty<(int, Polyline)>());
        // Standing for the ride car, nose 1 m right of its body, its 3 m trigger across the ride car's way at z 20.
        var waiting = Other(1, 4.2f, 20f, 0f, 0f);
        waiting.StoppedForUs = true;
        waiting.Heading = -(float)Math.PI / 2;
        // In its trigger: the creep, and the car's body is no gap (it isn't in the way).
        var inside = Decide(line, 18f, 3f, waiting);
        Assert.InRange(inside.MaxSpeed ?? 99f, TrafficDriver.CreepMs - 0.01f, TrafficDriver.CreepMs + 0.01f);
        Assert.Null(inside.GapAhead);
        // Out of it: on at its own speed (T3 20261013-185723, ride 8: 4 s at 2 m/s after the turn).
        Assert.Null(Decide(line, 26f, 3f, waiting).MaxSpeed);
    }

    [Fact]
    public void BacksOffFromACarHoldingItAtAnyAngle()
    {
        var line = DriveLine.Build(Straight(new Vec3(0, 0, -50), new Vec3(0, 0, 200)), Array.Empty<(int, Polyline)>());
        // Standing, the ride car's front at z 24; a traffic car that stopped for it 0.8 m ahead, at 30, 60 and 90
        // degrees across its way (T3 20261013-191420: the back-off only knew nose to nose).
        foreach (var degrees in new[] { 30f, 60f, 90f })
        {
            var a = degrees * (float)Math.PI / 180f;
            var waiting = Other(1, 0f, 0f, 0f, 0f);
            waiting.Heading = -a;
            // Its nearest corner 0.8 m from the front bumper, its body across the line.
            waiting.X = 0f;
            waiting.Z = 24.8f + 2.25f * (float)Math.Cos(a) + 0.95f * (float)Math.Sin(a);
            waiting.StoppedForUs = true;
            var d = Decide(line, 20f, 0f, waiting);
            Assert.True(d.BackOff, $"{degrees} degrees: {d.Why}");
            // Not holding the ride car: no back-off, it is ground (a gap).
            waiting.StoppedForUs = false;
            var ground = Decide(line, 20f, 0f, waiting);
            Assert.False(ground.BackOff);
            Assert.NotNull(ground.GapAhead);
        }
    }

    [Fact]
    public void PassesACarParkedInTheLaneOnAShiftedLine()
    {
        // North along x = 0, waypoints every 8 m; the line moves 0.8 m left (negative) for 16 m from the fifth
        // waypoint, easing in and out over 10 m, and the car follows it.
        var points = Straight(new Vec3(0, 0, 0), new Vec3(0, 0, 200));
        var line = DriveLine.Build(points, Array.Empty<(int, Polyline)>()).Shifted(new[] { (5, 0f, 16f, -0.8f) });
        var at = line.AlongOf[5] + 6f;
        Assert.InRange(line.Line.At(at).X, -0.81f, -0.79f);
        Assert.InRange(line.Line.At(line.AlongOf[5] - 12f).X, -0.01f, 0.01f);
        Assert.InRange(line.Line.At(line.AlongOf[7] + 12f).X, -0.01f, 0.01f);
        for (var k = 1; k < line.AlongOf.Length; k++) Assert.True(line.AlongOf[k] > line.AlongOf[k - 1]);
        var track = Drive(line, 3.75f, 35f, 0, 0, 0);
        var passing = track.Where(t => t.Z > 46f && t.Z < 56f).ToList();
        Assert.NotEmpty(passing);
        Assert.True(passing.All(t => t.X > -0.95f && t.X < -0.6f), string.Join(" ", passing.Select(t => $"{t.Z:0.0}:{t.X:0.00}")));
    }

    [Fact]
    public void RoundsAPlainLanesCornerAsADriverDoes()
    {
        // A lane east that jogs 35 degrees for 7 m and back (T3's Hamptons drive): rounded at both corners, its
        // heading turns smoothly (no step over 6 degrees per half metre) and it cuts the corners by at most 1 m.
        var points = new List<Vec3> { new(-20, 0, 0), new(-10, 0, 0), new(0, 0, 0), new(5.7f, 0, -4f), new(15.7f, 0, -4f), new(25.7f, 0, -4f) };
        var sharp = DriveLine.Build(points, Array.Empty<(int, Polyline)>());
        var round = DriveLine.Build(points, Array.Empty<(int, Polyline)>(), new HashSet<int> { 2, 3 });
        double Steepest(DriveLine l)
        {
            var worst = 0.0;
            for (var a = 1f; a < l.Line.Length - 1.5f; a += 0.5f)
            {
                Vec3 p = l.Line.At(a - 0.5f), q = l.Line.At(a), r = l.Line.At(a + 0.5f);
                worst = Math.Max(worst, Math.Abs(DriveLine.Wrap(Math.Atan2(r.X - q.X, r.Z - q.Z) - Math.Atan2(q.X - p.X, q.Z - p.Z))));
            }
            return worst * 180 / Math.PI;
        }
        Assert.True(Steepest(sharp) > 30);
        Assert.True(Steepest(round) < 6, $"{Steepest(round):0.0} degrees");
        Assert.All(round.Line.Points, p => Assert.True(Math.Abs(sharp.Locate(p.X, p.Z, 0f, 100f).Lateral) <= DriveLine.FilletCut + 0.05f));
        for (var k = 1; k < round.AlongOf.Length; k++) Assert.True(round.AlongOf[k] > round.AlongOf[k - 1]);
    }
}
