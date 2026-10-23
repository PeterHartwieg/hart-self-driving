using System;
using System.Linq;
using DrivenRides.Core.Drive;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;
using Xunit;
using Xunit.Abstractions;

namespace DrivenRides.Core.Tests;

// Reported 18 October: the food truck sees a queue at a red and slows, but hits its rear at 1.6–2.1 m/s.
// CarModel follows Desired directly; this regression must drive the pedals, including their response delay.
public sealed class FollowingPedalTests
{
    private readonly ITestOutputHelper output;
    public FollowingPedalTests(ITestOutputHelper output) => this.output = output;

    private sealed class NoFlags : ILiveFlags
    {
        public bool Stop(int waypoint) => false;
        public bool GiveWayEntry(int waypoint, out int junction, out int road) { junction = road = -1; return false; }
    }

    [Theory]
    [InlineData(3.8f, 1f, 1f, 7.2f)]
    [InlineData(4.45f, 1.35f, 1.246f, 7.2f)]
    [InlineData(4.45f, 1.35f, 1.246f, 10f)]
    [InlineData(4.45f, 1.35f, 1.246f, 13.9f)]
    [InlineData(7f, 2f, 1.25f, 10f)]
    public void StopsBehindAStandingCarThroughThePedals(float frontAhead, float rearBehind, float halfWidth, float startMs)
    {
        var line = DriveLine.Build(Enumerable.Range(0, 101).Select(k => new Vec3(0, 0, k * 5f)).ToList(), Array.Empty<(int, Polyline)>());
        var wheelbase = frontAhead - 0.85f;
        var driver = new PursuitDriver(line, wheelbase, frontAhead, 13.9f);
        var graph = new TurnTestGraph();
        graph.Add(0, 0);
        var traffic = new TrafficDriver(graph.Build(), line, driver,
            new RideBody { Wheelbase = wheelbase, FrontAhead = frontAhead, RearBehind = rearBehind, HalfWidth = halfWidth });
        // A stopped delivery truck, as in the recorded ride; its rear is initially 40 m from our front bumper.
        const float leaderLength = 7.43f, dt = 0.02f;
        var leaderRear = frontAhead + 40f;
        var cars = new[] { new OtherCar { Id = 27, X = 0f, Z = leaderRear + leaderLength / 2f, Heading = 0f,
            Length = leaderLength, Width = 2.4f, TriggerLength = 3f, Target = -1 } };
        var decision = new TrafficDecision();
        float rearZ = 0f, speed = startMs, throttle = 0f, brake = 0f, minGap = 40f;
        for (var step = 0; step < 1500; step++)
        {
            traffic.Decide(new TrafficSnapshot { Time = step * dt, RearX = 0f, RearZ = rearZ, Heading = 0f, Speed = speed,
                Marks = Array.Empty<PathMark>(), Cars = cars, Flags = new NoFlags() }, decision);
            var o = driver.Step(new DriveInput { RearX = 0f, RearZ = rearZ, Heading = 0f, SpeedMs = speed, Dt = dt,
                StopAlong = decision.StopAlong, YieldAlong = decision.YieldAlong, GapAhead = decision.GapAhead,
                GapSpeed = decision.GapSpeed, MaxSpeed = decision.MaxSpeed });
            // Same pedal-response model as ApproachPedalTests, independent of the desired speed.
            throttle += (o.Throttle - throttle) * dt / 0.1f;
            brake += (Math.Max(o.Brake, o.Handbrake) - brake) * dt / 0.1f;
            speed = Math.Max(0f, speed + (throttle * 3f - brake * PursuitDriver.BrakePerInput - 0.1f) * dt);
            rearZ += speed * dt;
            minGap = Math.Min(minGap, leaderRear - (rearZ + frontAhead));
        }
        output.WriteLine($"body {frontAhead + rearBehind:0.00} m, starting at {startMs:0.0} m/s: closest gap {minGap:0.00} m, final speed {speed:0.00} m/s");
        Assert.InRange(minGap, 1.5f, 3f);
        Assert.InRange(speed, 0f, 0.1f);
    }
}
