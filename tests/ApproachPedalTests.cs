using System;
using System.Collections.Generic;
using System.Linq;
using DrivenRides.Core.Drive;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;
using Xunit;
using Xunit.Abstractions;

namespace DrivenRides.Core.Tests;

// Review round 5 (Y3): keeping the option to stop short of a car's way must work through the pedals, not just a speed
// cap the pedal controller coasts down to (within 0.7 m/s of it, it neither brakes nor accelerates). Driven here by
// a car that answers the pedals (throttle and brake, with a lag), not one that follows the wished-for speed.
public sealed class ApproachPedalTests
{
    private readonly ITestOutputHelper output;
    public ApproachPedalTests(ITestOutputHelper output) => this.output = output;

    private sealed class NoFlags : ILiveFlags
    {
        public bool Stop(int waypoint) => false;
        public bool GiveWayEntry(int waypoint, out int junction, out int road) { junction = road = -1; return false; }
    }

    [Theory]
    [InlineData(9f, 12f, false)]
    [InlineData(12f, 14f, true)]
    [InlineData(13f, 13f, true)]
    public void TheCarKeepsItsStoppingOptionThroughThePedals(float rideMs, float crossMs, bool byApproach)
    {
        var car = RideCar.Dash;
        // North along x = 0; a lane east along z = 30, its car coming from the west so that it would arrive about when
        // the ride car does.
        var line = DriveLine.Build(Enumerable.Range(0, 101).Select(k => new Vec3(0, 0, -20 + k)).ToList(), Array.Empty<(int, Polyline)>());
        var driver = new PursuitDriver(line, car.Wheelbase, car.FrontAhead, 13.9f);
        var b = new TurnTestGraph();
        var lane = Enumerable.Range(0, 41).Select(k => b.Add(-150 + k * 5, 30)).ToArray();
        b.Link(lane);
        var graph = b.Build();
        var traffic = new TrafficDriver(graph, line, driver, new RideBody { Wheelbase = car.Wheelbase, FrontAhead = car.FrontAhead, RearBehind = car.RearBehind, HalfWidth = car.HalfWidth });
        float z = -car.FrontAhead, v = rideMs, throttle = 0f, brake = 0f;
        var crossX = -(30f / rideMs) * crossMs + 2f;
        const float dt = 0.02f;
        var decision = new TrafficDecision();
        var entered = false;
        var approached = false;
        var lost = 0f;
        for (var step = 0; step < 600; step++)
        {
            var t = step * dt;
            crossX += crossMs * dt;
            var cars = new[] { new OtherCar { Id = 1, X = crossX, Z = 30f, Heading = (float)(Math.PI / 2), Vx = crossMs, Length = 4.5f, Width = 1.9f, TriggerLength = 3f,
                Target = lane.Where(w => graph.Position(w).X > crossX + 2.25f).DefaultIfEmpty(-1).First() } };
            if (step % 2 == 0)
                traffic.Decide(new TrafficSnapshot { Time = t, RearX = 0f, RearZ = z, Heading = 0f, Speed = v, Marks = Array.Empty<PathMark>(), Cars = cars, Flags = new NoFlags() }, decision);
            approached |= decision.ApproachFor == 1;
            // Whenever it is to keep the option, it can still stop at PursuitDriver.Decel after the brakes' latency
            // short of the boundary (give or take StopUncertainty).
            if (decision.ApproachFor == 1)
                lost = Math.Max(lost, z + car.FrontAhead + v * TrafficDriver.BrakeLatency + v * v / (2f * PursuitDriver.Decel) - decision.ApproachAlong - TrafficDriver.StopUncertainty);
            var o = driver.Step(new DriveInput
            {
                RearX = 0f, RearZ = z, Heading = 0f, SpeedMs = v, Dt = dt, WheelDegrees = 0f,
                StopAlong = decision.StopAlong, YieldAlong = decision.YieldAlong, GapAhead = decision.GapAhead, GapSpeed = decision.GapSpeed, MaxSpeed = decision.MaxSpeed,
            });
            // The pedals act after a tenth of a second; full throttle 3 m/s², full brake PursuitDriver.BrakePerInput.
            throttle += (o.Throttle - throttle) * dt / 0.1f;
            brake += (Math.Max(o.Brake, o.Handbrake) - brake) * dt / 0.1f;
            v = Math.Max(0f, v + (throttle * 3f - brake * PursuitDriver.BrakePerInput - 0.1f) * dt);
            z += v * dt;
            // In the lane (its car's body half-width 0.95, the ride car's front) while the car hasn't gone by.
            var front = z + car.FrontAhead;
            if (front > 30f - 0.95f && z - car.RearBehind < 30f + 0.95f && crossX - 2.25f < car.HalfWidth && crossX + 2.25f > -car.HalfWidth) entered = true;
        }
        output.WriteLine($"{rideMs} m/s, car at {crossMs} m/s: approached {approached}, entered with the car there {entered}, option lost by {lost:0.00} m at most");
        // Held back by the cautious judgement where the lenient one alone was late, and never in the lane with the car.
        Assert.Equal(byApproach, approached);
        Assert.False(entered);
        Assert.True(lost <= 0.05f, $"the stopping option was lost by {lost:0.00} m");
    }

    // Why a stop and not a speed cap: driven through the pedals, a cap recomputed each step for the distance left
    // (the speed it can still stop from) is coasted down to, half a metre a second over it at a time, and the car runs
    // past the boundary; the same boundary as a stop is braked for by the distance left.
    [Fact]
    public void AStopKeepsTheBoundaryWhereACapRunsPastIt()
    {
        var capOver = Drive(useStop: false);
        var stopOver = Drive(useStop: true);
        output.WriteLine($"past the boundary: with a cap {capOver:0.00} m, with a stop {stopOver:0.00} m");
        Assert.True(capOver > TrafficDriver.StopUncertainty);
        Assert.True(stopOver <= TrafficDriver.StopUncertainty);
    }

    // How far the front ends up past the boundary.
    private static float Drive(bool useStop)
    {
        var car = RideCar.Dash;
        var line = DriveLine.Build(Enumerable.Range(0, 201).Select(k => new Vec3(0, 0, k)).ToList(), Array.Empty<(int, Polyline)>());
        var driver = new PursuitDriver(line, car.Wheelbase, car.FrontAhead, 13.9f);
        float z = 0f, v = 10f, throttle = 0f, brake = 0f;
        const float dt = 0.02f;
        var boundary = car.FrontAhead + v * TrafficDriver.BrakeLatency + v * v / (2f * PursuitDriver.Decel) + 1f;
        for (var step = 0; step < 1000 && (v > 0.05f || step < 10); step++)
        {
            var room = Math.Max(0f, boundary - (z + car.FrontAhead));
            var cap = PursuitDriver.Decel * (-TrafficDriver.BrakeLatency + (float)Math.Sqrt(TrafficDriver.BrakeLatency * TrafficDriver.BrakeLatency + 2f * room / PursuitDriver.Decel));
            var o = driver.Step(new DriveInput
            {
                RearX = 0f, RearZ = z, Heading = 0f, SpeedMs = v, Dt = dt,
                YieldAlong = useStop ? boundary : null, MaxSpeed = useStop ? null : cap,
            });
            throttle += (o.Throttle - throttle) * dt / 0.1f;
            brake += (Math.Max(o.Brake, o.Handbrake) - brake) * dt / 0.1f;
            v = Math.Max(0f, v + (throttle * 3f - brake * PursuitDriver.BrakePerInput - 0.1f) * dt);
            z += v * dt;
        }
        return z + car.FrontAhead - boundary;
    }
}
