using System;
using System.Collections.Generic;
using System.Linq;
using DrivenRides.Core.Drive;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;
using Xunit;
using Xunit.Abstractions;

namespace DrivenRides.Core.Tests;

public sealed class ReviewRound7Tests
{
    private readonly ITestOutputHelper output;
    public ReviewRound7Tests(ITestOutputHelper output) => this.output = output;

    private sealed class NoFlags : ILiveFlags
    {
        public bool Stop(int waypoint) => false;
        public bool GiveWayEntry(int waypoint, out int junction, out int road) { junction = road = -1; return false; }
    }

    private TrafficDecision Decide(bool bFirst, float frontZ, float rideMs, float ax, float bx, float bMs, float laneBZ, out TrafficDriver traffic, float bTrigger = 3f)
    {
        var car = RideCar.Dash;
        var line = DriveLine.Build(Enumerable.Range(0, 121).Select(k => new Vec3(0, 0, -40 + k)).ToList(), Array.Empty<(int, Polyline)>());
        var driver = new PursuitDriver(line, car.Wheelbase, car.FrontAhead, 13.9f);
        var b = new TurnTestGraph();
        var laneA = Enumerable.Range(0, 61).Select(k => b.Add(-150 + k * 5, 12)).ToArray();
        var laneB = Enumerable.Range(0, 61).Select(k => b.Add(150 - k * 5, laneBZ)).ToArray();
        b.Link(laneA);
        b.Link(laneB);
        var graph = b.Build();
        traffic = new TrafficDriver(graph, line, driver, new RideBody { Wheelbase = car.Wheelbase, FrontAhead = car.FrontAhead, RearBehind = car.RearBehind, HalfWidth = car.HalfWidth });
        var d = new TrafficDecision();
        var z = frontZ - car.FrontAhead;
        for (var step = 0; step < 3; step++)
        {
            var t = step * 0.04f;
            var a = new OtherCar { Id = 1, X = ax + 2f * t, Z = 12f, Heading = (float)(Math.PI / 2), Vx = 2f, Length = 4.5f, Width = 1.9f, TriggerLength = 3f,
                Target = laneA.Where(w => graph.Position(w).X > ax + 2f * t + 2.25f).DefaultIfEmpty(-1).First() };
            var a0 = new OtherCar { Id = 3, X = ax + 17f + 2f * t, Z = 12f, Heading = (float)(Math.PI / 2), Vx = 2f, Length = 4.5f, Width = 1.9f, TriggerLength = 3f,
                Target = laneA.Where(w => graph.Position(w).X > ax + 17f + 2f * t + 2.25f).DefaultIfEmpty(-1).First() };
            var bc = new OtherCar { Id = 2, X = bx - bMs * t, Z = laneBZ, Heading = -(float)(Math.PI / 2), Vx = -bMs, Length = 4.5f, Width = 1.9f, TriggerLength = bTrigger,
                Target = laneB.Where(w => graph.Position(w).X < bx - bMs * t - 2.25f).DefaultIfEmpty(-1).First() };
            var cars = bFirst ? new List<OtherCar> { bc, a0, a } : new List<OtherCar> { a0, a, bc };
            driver.StartAt(0f, z);
            traffic.Decide(new TrafficSnapshot { Time = t, RearX = 0f, RearZ = z, Heading = 0f, Speed = rideMs, Marks = Array.Empty<PathMark>(), Cars = cars, Flags = new NoFlags() }, d);
            z += rideMs * 0.04f;
        }
        output.WriteLine($"A {ax} lane B {laneBZ}, B from {bx} at {bMs} m/s: nose in {d.NoseIn}, yield {d.YieldAlong - 40f:0.0} for {d.YieldFor}, approach for {d.ApproachFor}: {d.Why} {d.YieldNote}");
        return d;
    }

    [Theory]
    [InlineData(false, 35f, true)]
    [InlineData(true, 35f, true)]
    [InlineData(false, 40f, false)]
    [InlineData(true, 40f, false)]
    public void NosingInStillJudgesTheSecondLane(bool bFirst, float bx, bool mustWait)
    {
        var d = Decide(bFirst, 8f, 0f, -11f, bx, 6f, 18f, out _, bTrigger: 1f);
        Assert.True(d.NoseIn, "not nosing in in front of car A");
        if (mustWait)
        {
            Assert.Equal(2, d.YieldFor);
            Assert.True(d.YieldAlong <= 18f - 0.95f + 40f, $"the stop for car B at {d.YieldAlong - 40f:0.0} is past its way");
        }
        else Assert.NotEqual(2, d.YieldFor);
    }

    [Theory]
    [InlineData(false, 35f)]
    [InlineData(true, 40f)]
    public void ACarThatStopsInItsTriggerIsNotWaitedFor(bool bFirst, float bx)
    {
        var d = Decide(bFirst, 8f, 0f, -11f, bx, 6f, 18f, out _, bTrigger: ScenarioBuilder.GleyTrigger);
        Assert.True(d.NoseIn, "not nosing in in front of car A");
        Assert.NotEqual(2, d.YieldFor);
    }

    [Theory]
    [InlineData("uturn", 2)]
    [InlineData("uturn", 11)]
    [InlineData("merge", 3)]
    [InlineData("headon", 6)]
    public void AStopIsKeptUntilTheCarHasGoneOrGoingOnIsClear(string template, int seed)
    {
        var r = TrafficTests.Run(TrafficTemplates.Build(template, seed));
        output.WriteLine($"{template} {seed}: {r}, again {r.ApproachesAgain}, option lost by {r.OptionLost:0.00} m");
        Assert.True(r.Failure == null, r.Failure);
        Assert.True(r.Arrived);
        Assert.Equal(0, r.ApproachesAgain);
        Assert.True(r.OptionLost <= 0f, $"short of the way by {r.OptionLost:0.00} m");
    }

    [Fact]
    public void AGrazeBetweenTwoPosesIsFoundEveryTime()
    {
        var r = TrafficTests.Run(TrafficTemplates.Build("priority", 41), true);
        var gave = r.Trace.ToString().Split('\n').Where(l => l.Contains("giving way to 13")).ToList();
        output.WriteLine($"priority 41: {r}, again {r.ApproachesAgain}; {gave.Count} trace lines giving way to 13");
        Assert.True(r.Failure == null, r.Failure);
        // Before: 1263 stops imposed again in 500 priority runs; one left here, a creep in a stand-off at 0.2 m/s.
        Assert.True(r.ApproachesAgain <= 1, $"{r.ApproachesAgain} stops imposed again");
    }

    [Theory]
    [InlineData(6f)]
    [InlineData(9f)]
    [InlineData(12f)]
    [InlineData(14f)]
    public void AClampedStopIsKeptThroughThePedals(float speed)
    {
        var car = RideCar.Dash;
        var hardRoom = speed * TrafficDriver.BrakeLatency + speed * speed / (2f * PursuitDriver.HardDecel);
        var plainRoom = TrafficDriver.StopUncertainty + 2f * TrafficDriver.BrakeLatency * speed + speed * speed / (2f * PursuitDriver.Decel);
        var cases = 0;
        for (var room = hardRoom; room <= plainRoom; room += 0.25f)
        {
            var line = DriveLine.Build(Enumerable.Range(0, 201).Select(k => new Vec3(0, 0, k)).ToList(), Array.Empty<(int, Polyline)>());
            var driver = new PursuitDriver(line, car.Wheelbase, car.FrontAhead, 13.9f);
            float z = 0f, v = speed, throttle = 0f, brake = 0f;
            var way = car.FrontAhead + room;
            var clamped = false;
            const float dt = 0.02f;
            for (var step = 0; step < 1000 && (v > 0.05f || step < 10); step++)
            {
                var front = z + car.FrontAhead;
                var stop = way;
                clamped |= front + v * TrafficDriver.BrakeLatency + v * v / (2f * PursuitDriver.HardDecel) > stop + 0.01f;
                var o = driver.Step(new DriveInput { RearX = 0f, RearZ = z, Heading = 0f, SpeedMs = v, Dt = dt, YieldAlong = stop });
                throttle += (o.Throttle - throttle) * dt / 0.1f;
                brake += (Math.Max(o.Brake, o.Handbrake) - brake) * dt / 0.1f;
                v = Math.Max(0f, v + (throttle * 3f - brake * PursuitDriver.BrakePerInput - 0.1f) * dt);
                z += v * dt;
            }
            var past = z + car.FrontAhead - way;
            if (clamped) cases++;
            output.WriteLine($"{speed} m/s, {room:0.00} m to the way: clamped {clamped}, stood {-past:0.00} m short of it");
            Assert.True(past <= 0f, $"{speed} m/s from {room:0.00} m: front {past:0.00} m past the way");
        }
        Assert.True(cases > 0);
    }
}
