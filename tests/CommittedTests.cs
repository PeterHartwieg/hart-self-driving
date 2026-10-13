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
    private static TrafficDecision Decide(float rideFrontZ, float rideMs, float crossX, float crossMs, float? standingAtZ, float? personGap = null)
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
            new OtherCar { Id = 1, X = crossX, Z = 12f, Heading = (float)(Math.PI / 2), Vx = crossMs, Length = 4.5f, Width = 1.9f, TriggerLength = 3f,
                Target = lane.First(w => graph.Position(w).X > crossX + 2.25f) },
        };
        if (standingAtZ is float z)
            cars.Add(new OtherCar { Id = 2, X = 0f, Z = z, Heading = 0f, Length = 4.5f, Width = 1.9f, TriggerLength = 3f, Target = -1 });
        var decision = new TrafficDecision();
        // Twice, so the second sees the cars' pace.
        for (var t = 0; t < 2; t++)
            traffic.Decide(new TrafficSnapshot { Time = t * 0.02f, RearX = 0f, RearZ = rear, Heading = 0f, Speed = rideMs, Marks = Array.Empty<PathMark>(), Cars = cars, Flags = new NoFlags(), GapAhead = personGap }, decision);
        return decision;
    }

    // The lane's car 1.6 s off: stopping short is no longer possible at 7 m/s, and going on clears the lane.
    [Fact]
    public void GoesOnWhenItClearsTheLane()
    {
        var d = Decide(rideFrontZ: 10.4f, rideMs: 7f, crossX: -16f, crossMs: 10f, standingAtZ: null);
        output.WriteLine($"{d.Why} yield {d.YieldAlong} gap {d.GapAhead}");
        Assert.Null(d.YieldAlong);
    }

    [Fact]
    public void StopsNowWhenACarStandingPastTheLaneWouldHoldItInTheWay()
    {
        // The standing car's rear 1 m past the lane: the ride car would stand across the lane behind it.
        var d = Decide(rideFrontZ: 10.4f, rideMs: 7f, crossX: -16f, crossMs: 10f, standingAtZ: 12f + 1.25f + 1f + 2.25f);
        output.WriteLine($"{d.Why} yield {d.YieldAlong} gap {d.GapAhead}");
        Assert.Equal(1, d.YieldFor);
        // At its front now (the line starts at z = -20): the most it can brake.
        Assert.True(d.YieldAlong <= 30.5f);
    }

    [Fact]
    public void StopsNowWhenAPersonPastTheLaneWouldHoldItInTheWay()
    {
        var d = Decide(rideFrontZ: 10.4f, rideMs: 7f, crossX: -16f, crossMs: 10f, standingAtZ: null, personGap: 4.5f);
        output.WriteLine($"{d.Why} yield {d.YieldAlong} gap {d.GapAhead}");
        Assert.Equal(1, d.YieldFor);
        // At its front now (the line starts at z = -20): the most it can brake.
        Assert.True(d.YieldAlong <= 30.5f);
    }
}
