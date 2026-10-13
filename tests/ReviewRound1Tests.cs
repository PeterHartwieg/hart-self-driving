using System;
using System.Collections.Generic;
using System.Linq;
using DrivenRides.Core.Drive;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;
using DrivenRides.Core.Testing;
using Xunit;

namespace DrivenRides.Core.Tests;

// The planner's fixes from review round 1 (build/review-nwh/fixes-r1.md, P1 to P11).
public sealed class ReviewRound1Tests
{
    private static CarSpec Small() => new("small", 2.5f, _ => 40f, new Vec3(0, 0.7f, 1.25f), new Vec3(0.9f, 0.7f, 2f), 50f, 1.5f);

    [Fact]
    public void P1RecoveryBacksOffOnlyWithRoomBehind()
    {
        var none = new List<OtherCar>();
        // Room: it reverses, no faster than MaxMs, and stops after MaxMetres.
        var r = new Recovery();
        Assert.Equal(Recovery.Step.Reverse, r.Next(0, 0, 0, 1f, 2.5f, 0f, null, none, 0.02f));
        Assert.Equal(Recovery.Step.Brake, r.Next(0, -0.5f, 0, 1f, 2.5f, -1.5f, null, none, 0.02f));
        Assert.Equal(Recovery.Step.Done, r.Next(0, -Recovery.MaxMetres - 0.1f, 0, 1f, 2.5f, -0.5f, null, none, 0.02f));
        // Something the adapter sees right behind (a person, a parked car): held, not reversed.
        var held = new Recovery();
        Assert.Equal(Recovery.Step.Done, held.Next(0, 0, 0, 1f, 2.5f, 0f, 0.5f, none, 0.02f));
        Assert.Contains("no room", held.Ended);
        // A traffic car close behind: held too.
        var car = new OtherCar { Id = 7, X = 0, Z = -5.5f, Heading = 0, Length = 4.5f, Width = 1.9f };
        var queued = new Recovery();
        Assert.Equal(Recovery.Step.Done, queued.Next(0, 0, 0, 1f, 2.5f, 0f, null, new List<OtherCar> { car }, 0.02f));
        // One 10 m behind leaves room.
        car.Z = -12f;
        Assert.Equal(Recovery.Step.Reverse, new Recovery().Next(0, 0, 0, 1f, 2.5f, 0f, null, new List<OtherCar> { car }, 0.02f));
    }

    [Fact]
    public void P2ANewLineMustJoinTheCarWhereItStands()
    {
        var points = Enumerable.Range(0, 11).Select(k => new Vec3(0, 0, k * 10)).ToList();
        var line = DriveLine.Build(points, Array.Empty<(int, Polyline)>());
        Assert.True(line.Joins(0.5f, 23f, 0.1, out var at));
        Assert.InRange(at, 22f, 24f);
        Assert.False(line.Joins(3f, 23f, 0, out _));
        Assert.False(line.Joins(0f, 23f, 50 * Math.PI / 180, out _));
        Assert.False(line.Joins(0f, 92f, 0, out _));
    }

    [Fact]
    public void P3TheDrivenLineIsSweptAsDriven()
    {
        var (graph, entry, exit) = TurnTestGraph.Turn();
        var route = new List<int> { 0, 1, entry, exit, exit + 1, exit + 2, exit + 3 };
        var clear = new Feasibility(graph, Small(), _ => null);
        var plan = DrivePlan.Build(graph, route, Small(), clear.Arc, clear.Clear);
        var line = plan.Line(graph);
        Assert.Null(clear.Sweep(plan, line, 5f, line.Line.Length - 10f));
        // A post standing on the line 30 m along: found where the car would meet it.
        var on = line.Line.At(30f);
        string? World(BodyBox box)
        {
            float s = (float)Math.Sin(box.Heading), c = (float)Math.Cos(box.Heading);
            float dx = on.X - box.Centre.X, dz = on.Z - box.Centre.Z;
            return Math.Abs(dx * c - dz * s) <= box.Half.X && Math.Abs(dx * s + dz * c) <= box.Half.Z ? "post" : null;
        }
        var check = new Feasibility(graph, Small(), World);
        Assert.StartsWith("post", check.Sweep(plan, line, 5f, line.Line.Length - 10f));
    }

    [Fact]
    public void P5ASlopeIsNoKerbARaisedWheelIs()
    {
        // Four wheels on a 3% slope along the car: the front 0.11 m above the rear, none off the plane of the others.
        var slope = new[] { new Vec3(-0.75f, 0, 0), new Vec3(0.75f, 0, 0), new Vec3(-0.75f, 0.1125f, 3.75f), new Vec3(0.75f, 0.1125f, 3.75f) };
        for (var i = 0; i < 4; i++) Assert.InRange(WheelPlane.Rise(slope, i)!.Value, -0.01f, 0.01f);
        var kerb = new[] { new Vec3(-0.75f, 0, 0), new Vec3(0.75f, 0.15f, 0), new Vec3(-0.75f, 0, 3.75f), new Vec3(0.75f, 0, 3.75f) };
        Assert.InRange(WheelPlane.Rise(kerb, 1)!.Value, 0.14f, 0.16f);
        // The check drives a turn on a 3% slope without calling it a kerb.
        var (graph, entry, exit) = TurnTestGraph.Turn();
        var onSlope = new Feasibility(graph, Small(), _ => null) { GroundAt = p => 0.03f * p.Z };
        Assert.True(onSlope.Step(entry, exit).Feasible, onSlope.Step(entry, exit).ToString());
    }

    [Fact]
    public void P7AStepWithNoCaseToCheckItOnIsNotDriven()
    {
        var (graph, entry, exit) = TurnTestGraph.Turn();
        var check = new Feasibility(graph, Small(), _ => null, options: new CatalogueOptions { NearBridge = _ => true });
        var verdict = check.Step(entry, exit);
        Assert.False(verdict.Feasible);
        Assert.Contains(Feasibility.NoCase, verdict.Reason);
    }

    [Fact]
    public void P10ThePavementIsAskedEveryHalfMetre()
    {
        var b = new TurnTestGraph();
        int[] lane = Enumerable.Range(0, 8).Select(i => b.Add(0, i * 10)).ToArray();
        b.Link(lane);
        var graph = b.Build();
        var f = new Fixture { Route = lane.ToList(), Kind = CaseKind.Ride, Manoeuvre = CaseKind.Ride, EntryIndex = 1, ExitIndex = 2 };
        var judge = new Judge(graph, CaseGeometry.Build(graph, f), f);
        var asked = 0;
        judge.PavementAt = _ => { asked++; return false; };
        for (var step = 0; step < 100; step++)
        {
            var z = step * 0.1f;
            judge.Add(new Sample { Step = step, Time = step * 0.02, Centre = new Vec3(0, 0, z), FrontAxle = new Vec3(0, 0, z + 1),
                Footprint = new[] { new Vec3(-1, 0, z - 2), new Vec3(1, 0, z - 2), new Vec3(1, 0, z + 2), new Vec3(-1, 0, z + 2) } });
        }
        // 10 m: about 20 checks of four corners, not 100.
        Assert.InRange(asked, 60, 90);
    }
}
