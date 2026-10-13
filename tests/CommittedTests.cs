using System;
using System.Collections.Generic;
using System.Linq;
using DrivenRides.Core.Drive;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;
using Xunit;
using Xunit.Abstractions;

namespace DrivenRides.Core.Tests;

// Review round 2 (U1): a conflict found too late to stop short of a traffic car's way. Going on is safe only on the
// trajectory the car can keep under its other stops (a standing car just past the lane, a person, a stop line,
// other yields), with headway, and a car behind counted on to brake only once the body is in its trigger. Best-case
// acceleration never makes it safe: a standing car past the lane would hold the ride car in the crossing car's way.
public sealed class CommittedTests
{
    private readonly ITestOutputHelper output;
    public CommittedTests(ITestOutputHelper output) => this.output = output;

    private sealed class NoFlags : ILiveFlags
    {
        public bool Stop(int waypoint) => false;
        public bool GiveWayEntry(int waypoint, out int junction, out int road) { junction = road = -1; return false; }
    }

    // The ride car goes north along x = 0; a lane runs east along z = 12, its car coming from the west.
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
        // Twice, so the second sees the cars' pace.
        for (var t = 0; t < 2; t++)
            traffic.Decide(new TrafficSnapshot { Time = t * 0.02f, RearX = 0f, RearZ = rear, Heading = 0f, Speed = rideMs, Marks = Array.Empty<PathMark>(), Cars = cars, Flags = new NoFlags(), GapAhead = personGap, PlannerHoldAlong = plannerHold }, decision);
        return decision;
    }

    // The lane's car 3 s off: going on clears the lane with the headway.
    [Fact]
    public void GoesOnWhenItClearsTheLane()
    {
        var d = Decide(rideFrontZ: 10.4f, rideMs: 7f, crossX: -30f, crossMs: 10f, standingAtZ: null);
        output.WriteLine($"{d.Why} yield {d.YieldAlong} gap {d.GapAhead}");
        Assert.Null(d.YieldAlong);
        Assert.False(d.ForcedCommit);
    }

    [Fact]
    public void StopsNowWhenACarStandingPastTheLaneWouldHoldItInTheWay()
    {
        // The standing car's rear 1 m past the lane: the ride car would stand across the lane behind it.
        var d = Decide(rideFrontZ: 10.4f, rideMs: 7f, crossX: -30f, crossMs: 10f, standingAtZ: 12f + 1.25f + 1f + 2.25f);
        output.WriteLine($"{d.Why} yield {d.YieldAlong} gap {d.GapAhead}");
        Assert.Equal(1, d.YieldFor);
        // At its front now (the line starts at z = -20): the most it can brake.
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

    // Review round 3 (V1): the planner's hold (a step ahead found blocked) is a firm stop like the others.
    [Fact]
    public void StopsNowWhenThePlannersHoldIsPastTheLane()
    {
        var free = Decide(rideFrontZ: 10.4f, rideMs: 7f, crossX: -30f, crossMs: 10f, standingAtZ: null);
        Assert.Null(free.YieldAlong);
        // The front to stop 4 m on, in the lane (z = 12 is 32 m along the line).
        var d = Decide(rideFrontZ: 10.4f, rideMs: 7f, crossX: -30f, crossMs: 10f, standingAtZ: null, plannerHold: 34.4f);
        output.WriteLine($"{d.Why} yield {d.YieldAlong} stop {d.StopAlong}");
        Assert.Equal(1, d.YieldFor);
        Assert.True(d.YieldAlong <= 30.5f);
    }

    // Review round 3 (V2): a collision only when standing in the way would be hit (a car whose trigger is too short to
    // stop it short of the body) and going on clears it by time: on, counted as forced.
    [Fact]
    public void GoesOnForcedOnlyWhenStandingWouldBeHit()
    {
        var d = Decide(rideFrontZ: 10.4f, rideMs: 10f, crossX: -16f, crossMs: 10f, standingAtZ: null, trigger: 1f);
        output.WriteLine($"{d.Why} yield {d.YieldAlong} forced {d.ForcedCommit}");
        Assert.True(d.ForcedCommit);
        Assert.Equal(1, d.ForcedFor);
        Assert.Null(d.YieldAlong);
        // The same with a trigger that stops the car for the body: a plain stop, nothing forced.
        var safe = Decide(rideFrontZ: 10.4f, rideMs: 10f, crossX: -16f, crossMs: 10f, standingAtZ: null);
        Assert.False(safe.ForcedCommit);
        Assert.True(safe.YieldAlong <= 30.5f);
    }

    // Review round 3 (V2): inside a one-lane stretch with a car coming the other way, standing is no collision (it stops
    // in its trigger; back-off and the planner resolve the stand-off): the ride car stops, nothing forced.
    [Fact]
    public void AHeadOnStandOffIsAStopNotAForcedCommit()
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
        output.WriteLine($"{d.Why} yield {d.YieldAlong} forced {d.ForcedCommit}");
        Assert.False(d.ForcedCommit);
        Assert.Equal(1, d.YieldFor);
        Assert.True(d.YieldAlong <= 30.5f);
    }
}
