using System;
using System.Collections.Generic;
using System.Linq;
using DrivenRides.Core.Drive;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;
using Xunit;
using Xunit.Abstractions;

namespace DrivenRides.Core.Tests;

public sealed class CommittedTests
{
    private readonly ITestOutputHelper output;
    public CommittedTests(ITestOutputHelper output) => this.output = output;

    private sealed class NoFlags : ILiveFlags
    {
        public bool Stop(int waypoint) => false;
        public bool GiveWayEntry(int waypoint, out int junction, out int road) { junction = road = -1; return false; }
    }

    internal static TrafficDecision Decide(float rideFrontZ, float rideMs, float crossX, float crossMs, float? standingAtZ, float? personGap = null, float? plannerHold = null, float trigger = 3f)
    {
        var car = RideCar.Dash;
        var line = DriveLine.Build(Enumerable.Range(0, 81).Select(k => new Vec3(0, 0, -20 + k)).ToList(), Array.Empty<(int, Polyline)>());
        var driver = new PursuitDriver(line, car.Wheelbase, car.FrontAhead, 13.9f);
        var b = new TurnTestGraph();
        var lane = Enumerable.Range(0, 25).Select(k => b.Add(-60 + k * 5, 12)).ToArray();
        b.Link(lane);
        var graph = b.Build();
        var traffic = new TrafficDriver(graph, line, driver, new RideBody { Wheelbase = car.Wheelbase, FrontAhead = car.FrontAhead, RearBehind = car.RearBehind, HalfWidth = car.HalfWidth });
        var rear = rideFrontZ - car.FrontAhead;
        driver.StartAt(0f, rear);
        var cars = new List<OtherCar>
        {
            new OtherCar { Id = 1, X = crossX, Z = 12f, Heading = (float)(Math.PI / 2), Vx = crossMs, Length = 4.5f, Width = 1.9f, TriggerLength = trigger,
                Target = lane.First(w => graph.Position(w).X > crossX + 2.25f) },
        };
        if (standingAtZ is float z)
            cars.Add(new OtherCar { Id = 2, X = 0f, Z = z, Heading = 0f, Length = 4.5f, Width = 1.9f, TriggerLength = 3f, Target = -1 });
        var decision = new TrafficDecision();
        for (var t = 0; t < 2; t++)
            traffic.Decide(new TrafficSnapshot { Time = t * 0.02f, RearX = 0f, RearZ = rear, Heading = 0f, Speed = rideMs, Marks = Array.Empty<PathMark>(), Cars = cars, Flags = new NoFlags(), GapAhead = personGap, PlannerHoldAlong = plannerHold }, decision);
        return decision;
    }

    [Fact]
    public void GoesOnWhenItClearsTheLane()
    {
        var d = Decide(rideFrontZ: 10.4f, rideMs: 7f, crossX: -30f, crossMs: 10f, standingAtZ: null);
        output.WriteLine($"{d.Why} yield {d.YieldAlong} gap {d.GapAhead}");
        Assert.Null(d.YieldAlong);
    }

    [Fact]
    public void StopsNowWhenACarStandingPastTheLaneWouldHoldItInTheWay()
    {
        var d = Decide(rideFrontZ: 10.4f, rideMs: 7f, crossX: -30f, crossMs: 10f, standingAtZ: 12f + 1.25f + 1f + 2.25f);
        output.WriteLine($"{d.Why} yield {d.YieldAlong} gap {d.GapAhead}");
        Assert.Equal(1, d.YieldFor);
        Assert.True(d.YieldAlong <= 30.5f);
    }

    [Fact]
    public void StopsNowWhenAPersonPastTheLaneWouldHoldItInTheWay()
    {
        var d = Decide(rideFrontZ: 10.4f, rideMs: 7f, crossX: -30f, crossMs: 10f, standingAtZ: null, personGap: 4.5f);
        output.WriteLine($"{d.Why} yield {d.YieldAlong} gap {d.GapAhead}");
        Assert.Equal(1, d.YieldFor);
        Assert.True(d.YieldAlong <= 30.5f);
    }

    [Fact]
    public void StopsNowWhenThePlannersHoldIsPastTheLane()
    {
        var free = Decide(rideFrontZ: 10.4f, rideMs: 7f, crossX: -30f, crossMs: 10f, standingAtZ: null);
        Assert.Null(free.YieldAlong);
        var d = Decide(rideFrontZ: 10.4f, rideMs: 7f, crossX: -30f, crossMs: 10f, standingAtZ: null, plannerHold: 34.4f);
        output.WriteLine($"{d.Why} yield {d.YieldAlong} stop {d.StopAlong}");
        Assert.Equal(1, d.YieldFor);
        Assert.True(d.YieldAlong <= 30.5f);
    }

    [Theory]
    [InlineData(1f)]
    public void TooLateAndNotClearIsAStopNow(float trigger)
    {
        var d = Decide(rideFrontZ: 10.4f, rideMs: 10f, crossX: -12f, crossMs: 10f, standingAtZ: null, trigger: trigger);
        output.WriteLine($"{d.Why} yield {d.YieldAlong}");
        Assert.True(d.YieldAlong <= 30.5f);
        Assert.Contains(1, d.EmergencyFor);
    }

    [Fact]
    public void ClearsAShortGapInsteadOfStoppingForThePreviousExtraHeadway()
    {
        var d = Decide(rideFrontZ: 10.4f, rideMs: 10f, crossX: -18f, crossMs: 10f, standingAtZ: null, trigger: 1f);
        Assert.Null(d.YieldAlong);
        Assert.DoesNotContain(1, d.EmergencyFor);
    }

    [Fact]
    public void GoesOnWhenTheCarsTriggerStopsItShort()
    {
        var d = Decide(rideFrontZ: 10.4f, rideMs: 10f, crossX: -18f, crossMs: 10f, standingAtZ: null, trigger: 5f);
        output.WriteLine($"{d.Why} yield {d.YieldAlong}");
        Assert.Null(d.YieldAlong);
        Assert.DoesNotContain(1, d.EmergencyFor);
    }

    [Fact]
    public void AHeadOnStandOffIsAStop()
    {
        var car = RideCar.Dash;
        var line = DriveLine.Build(Enumerable.Range(0, 81).Select(k => new Vec3(0, 0, -20 + k)).ToList(), Array.Empty<(int, Polyline)>());
        var driver = new PursuitDriver(line, car.Wheelbase, car.FrontAhead, 13.9f);
        var b = new TurnTestGraph();
        var lane = Enumerable.Range(0, 17).Select(k => b.Add(0, 60 - k * 5)).ToArray();
        b.Link(lane);
        var graph = b.Build();
        var traffic = new TrafficDriver(graph, line, driver, new RideBody { Wheelbase = car.Wheelbase, FrontAhead = car.FrontAhead, RearBehind = car.RearBehind, HalfWidth = car.HalfWidth });
        var rear = 10f - car.FrontAhead;
        driver.StartAt(0f, rear);
        var cars = new[] { new OtherCar { Id = 1, X = 0f, Z = 30f, Heading = (float)Math.PI, Vz = -6f, Length = 4.5f, Width = 1.9f, TriggerLength = 3f, Target = lane.First(w => graph.Position(w).Z < 27.75f) } };
        var d = new TrafficDecision();
        for (var t = 0; t < 2; t++)
            traffic.Decide(new TrafficSnapshot { Time = t * 0.02f, RearX = 0f, RearZ = rear, Heading = 0f, Speed = 7f, Marks = Array.Empty<PathMark>(), Cars = cars, Flags = new NoFlags() }, d);
        output.WriteLine($"{d.Why} yield {d.YieldAlong}");
        Assert.Equal(1, d.YieldFor);
        Assert.True(d.YieldAlong <= 30.5f);
    }
}
