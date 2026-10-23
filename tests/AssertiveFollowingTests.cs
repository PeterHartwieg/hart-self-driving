using System;
using System.Linq;
using DrivenRides.Core.Drive;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;
using Xunit;
using Xunit.Abstractions;

namespace DrivenRides.Core.Tests;

// Shorter moving gaps must work through the actual pedal outputs, including lag, when the leader brakes.
// CarModel alone follows Desired directly and cannot expose a car coasting through a shrinking gap.
public sealed class AssertiveFollowingTests
{
    private readonly ITestOutputHelper output;
    public AssertiveFollowingTests(ITestOutputHelper output) => this.output = output;

    private sealed class NoFlags : ILiveFlags
    {
        public bool Stop(int waypoint) => false;
        public bool GiveWayEntry(int waypoint, out int junction, out int road) { junction = road = -1; return false; }
    }

    [Theory]
    [InlineData(3.8f, 1f, 1f, 7f)]
    [InlineData(4.45f, 1.35f, 1.246f, 10f)]
    [InlineData(7f, 2f, 1.25f, 13f)]
    public void ClosesTheMovingGapAndBrakesWithTheLeader(float frontAhead, float rearBehind, float halfWidth, float leaderMs)
    {
        var line = DriveLine.Build(Enumerable.Range(0, 301).Select(k => new Vec3(0, 0, k * 5f)).ToList(), Array.Empty<(int, Polyline)>());
        var wheelbase = frontAhead - 0.85f;
        var driver = new PursuitDriver(line, wheelbase, frontAhead, 13.9f);
        var graph = new TurnTestGraph();
        graph.Add(0, 0);
        var traffic = new TrafficDriver(graph.Build(), line, driver,
            new RideBody { Wheelbase = wheelbase, FrontAhead = frontAhead, RearBehind = rearBehind, HalfWidth = halfWidth });
        const float length = 7.43f, dt = 0.02f;
        var leaderRear = frontAhead + 25f;
        var leaderSpeed = leaderMs;
        var cars = new[] { new OtherCar { Id = 27, Heading = 0f, Length = length, Width = 2.4f, TriggerLength = 3f, Target = -1 } };
        var decision = new TrafficDecision();
        float rearZ = 0f, speed = leaderMs, throttle = 0f, brake = 0f, minGap = 25f, headwaySum = 0f, speedSum = 0f;
        var samples = 0;
        for (var step = 0; step < 4500; step++)
        {
            var time = step * dt;
            // A steady stream, then ordinary traffic braking to a standstill at 4 m/s².
            if (time >= 65f) leaderSpeed = Math.Max(0f, leaderSpeed - 4f * dt);
            leaderRear += leaderSpeed * dt;
            cars[0].Z = leaderRear + length / 2f;
            cars[0].Vz = leaderSpeed;
            traffic.Decide(new TrafficSnapshot { Time = time, RearX = 0f, RearZ = rearZ, Heading = 0f, Speed = speed,
                Marks = Array.Empty<PathMark>(), Cars = cars, Flags = new NoFlags() }, decision);
            var o = driver.Step(new DriveInput { RearX = 0f, RearZ = rearZ, Heading = 0f, SpeedMs = speed, Dt = dt,
                StopAlong = decision.StopAlong, YieldAlong = decision.YieldAlong, GapAhead = decision.GapAhead,
                GapSpeed = decision.GapSpeed, MaxSpeed = decision.MaxSpeed });
            throttle += (o.Throttle - throttle) * dt / 0.1f;
            brake += (Math.Max(o.Brake, o.Handbrake) - brake) * dt / 0.1f;
            speed = Math.Max(0f, speed + (throttle * 3f - brake * PursuitDriver.BrakePerInput - 0.1f) * dt);
            rearZ += speed * dt;
            var gap = leaderRear - (rearZ + frontAhead);
            minGap = Math.Min(minGap, gap);
            if (time >= 45f && time < 65f)
            {
                headwaySum += gap / Math.Max(0.1f, speed);
                speedSum += speed;
                samples++;
            }
        }
        var headway = headwaySum / samples;
        output.WriteLine($"leader {leaderMs:0.0} m/s, body {frontAhead + rearBehind:0.00} m: moving gap {headway:0.00} s, closest bumper gap {minGap:0.00} m, final speed {speed:0.00} m/s");
        Assert.InRange(speedSum / samples, leaderMs - 0.3f, leaderMs + 0.3f);
        Assert.InRange(headway, 0.3f, 0.8f);
        Assert.True(minGap > 0f, $"routine leader braking caused contact: gap {minGap:0.00} m");
        Assert.InRange(speed, 0f, 0.1f);
    }
}
