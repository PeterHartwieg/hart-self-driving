using System;
using System.Collections.Generic;
using DrivenRides.Core.Drive;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;
using Xunit;

namespace DrivenRides.Core.Tests;

public sealed class AheadTests
{
    private sealed class NoFlags : ILiveFlags
    {
        public bool Stop(int waypoint) => false;
        public bool GiveWayEntry(int waypoint, out int junction, out int road) { junction = road = -1; return false; }
    }

    private static DriveLine Hairpin()
    {
        var points = new List<Vec3>();
        for (var z = 0f; z < 4f; z += 0.5f) points.Add(new Vec3(0, 0, z));
        for (var a = 0f; a < Math.PI; a += 0.05f) points.Add(new Vec3(6f - 6f * (float)Math.Cos(a), 0, 4f + 6f * (float)Math.Sin(a)));
        for (var z = 4f; z >= -40f; z -= 0.5f) points.Add(new Vec3(12f, 0, z));
        return DriveLine.Build(points, Array.Empty<(int, Polyline)>());
    }

    private static TrafficDecision Decide(OtherCar other)
    {
        var line = Hairpin();
        var car = RideCar.Dash;
        var driver = new PursuitDriver(line, car.Wheelbase, car.FrontAhead, 13.9f);
        var graph = new TurnTestGraph();
        graph.Add(0, 0);
        var traffic = new TrafficDriver(graph.Build(), line, driver, new RideBody { Wheelbase = car.Wheelbase, FrontAhead = car.FrontAhead, RearBehind = car.RearBehind, HalfWidth = car.HalfWidth });
        var decision = new TrafficDecision();
        traffic.Decide(new TrafficSnapshot { Time = 0f, RearX = 0f, RearZ = 0f, Heading = 0f, Speed = 4f, Marks = Array.Empty<PathMark>(), Cars = new[] { other }, Flags = new NoFlags() }, decision);
        return decision;
    }

    [Fact]
    public void ACarStandingInTheLaneAUTurnEntersIsAhead()
    {
        var d = Decide(new OtherCar { Id = 1, X = 12f, Z = -3f, Heading = (float)Math.PI, Length = 4.5f, Width = 1.9f, TriggerLength = 3f, Target = -1 });
        Assert.Equal(1, d.GapFor);
        Assert.NotNull(d.GapAhead);
    }

    [Fact]
    public void ACarStandingBehindTheRideCarIsNotAhead()
    {
        var d = Decide(new OtherCar { Id = 1, X = 0f, Z = -6.5f, Heading = 0f, Length = 4.5f, Width = 1.9f, TriggerLength = 3f, Target = -1 });
        Assert.Null(d.GapAhead);
    }
}
