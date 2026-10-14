using System;
using System.Collections.Generic;
using System.Linq;
using DrivenRides.Core.Drive;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;
using Xunit;
using Xunit.Abstractions;

namespace DrivenRides.Core.Tests;

// Review round 6. AA1: a hold short of a car's way makes the ride car stay outside it, which is not clearing through it:
// no commitment comes of it. AA2: a remembered commitment never lowers the headway while the car can still stop short
// of the car's way; going on with less than the headway is only for a car past that point.
public sealed class ReviewRound6Tests
{
    private readonly ITestOutputHelper output;
    public ReviewRound6Tests(ITestOutputHelper output) => this.output = output;

    private sealed class NoFlags : ILiveFlags
    {
        public bool Stop(int waypoint) => false;
        public bool GiveWayEntry(int waypoint, out int junction, out int road) { junction = road = -1; return false; }
    }

    private sealed class Run
    {
        public readonly List<string> Bad = new List<string>();
        public bool Crossed;
        public float CrossedAt = float.NaN, AWhenCrossed = float.NaN;
    }

    // The ride car goes north along x = 0 (the line starts at z = -40: metres along it are z + 40); lane A runs east
    // along z = 12, lane B west along z = 17 with a car far off. The planner holds the front at `holdZ` until
    // `release` seconds, then lets it go. The ride car brakes for the stops it is given as the driver does (from when
    // it needs more than Decel, at most HardDecel) and speeds up at 2 m/s² to `rideMs` otherwise.
    private Run Drive(bool bFirst, float holdZ, float holdFrom, float release, float ax0, float rideMs, float rearZ0, int steps)
    {
        var car = RideCar.Dash;
        var line = DriveLine.Build(Enumerable.Range(0, 121).Select(k => new Vec3(0, 0, -40 + k)).ToList(), Array.Empty<(int, Polyline)>());
        var driver = new PursuitDriver(line, car.Wheelbase, car.FrontAhead, 13.9f);
        var b = new TurnTestGraph();
        var laneA = Enumerable.Range(0, 61).Select(k => b.Add(-200 + k * 5, 12)).ToArray();
        var laneB = Enumerable.Range(0, 61).Select(k => b.Add(200 - k * 5, 17)).ToArray();
        b.Link(laneA);
        b.Link(laneB);
        var graph = b.Build();
        var traffic = new TrafficDriver(graph, line, driver, new RideBody { Wheelbase = car.Wheelbase, FrontAhead = car.FrontAhead, RearBehind = car.RearBehind, HalfWidth = car.HalfWidth });
        const float dt = 0.04f;
        float z = rearZ0, v = rideMs, ax = ax0, bx = 150f;
        var d = new TrafficDecision();
        var run = new Run();
        for (var step = 0; step < steps; step++)
        {
            var t = step * dt;
            ax += 10f * dt;
            bx -= 10f * dt;
            var a = new OtherCar { Id = 1, X = ax, Z = 12f, Heading = (float)(Math.PI / 2), Vx = 10f, Length = 4.5f, Width = 1.9f, TriggerLength = 3f,
                Target = laneA.Where(w => graph.Position(w).X > ax + 2.25f).DefaultIfEmpty(-1).First() };
            var bc = new OtherCar { Id = 2, X = bx, Z = 17f, Heading = -(float)(Math.PI / 2), Vx = -10f, Length = 4.5f, Width = 1.9f, TriggerLength = 3f,
                Target = laneB.Where(w => graph.Position(w).X < bx - 2.25f).DefaultIfEmpty(-1).First() };
            var cars = bFirst ? new List<OtherCar> { bc, a } : new List<OtherCar> { a, bc };
            float? hold = t >= holdFrom && t < release ? holdZ + 40f : (float?)null;
            // The driver keeps up with the car, as on the road (the traffic driver looks for the car near it).
            driver.StartAt(0f, z);
            traffic.Decide(new TrafficSnapshot { Time = t, RearX = 0f, RearZ = z, Heading = 0f, Speed = v, Marks = Array.Empty<PathMark>(), Cars = cars, Flags = new NoFlags(), PlannerHoldAlong = hold }, d);
            var front = z + car.FrontAhead;
            if (step % 10 == 0) output.WriteLine($"{t:0.00} front {front:0.00} v {v:0.0} A {ax:0.0} hold {hold:0.0} yield {d.YieldAlong - 40f:0.0} {d.Why}");
            if (!run.Crossed && z - car.RearBehind > 13f) { run.Crossed = true; run.CrossedAt = t; run.AWhenCrossed = ax; }
            // In A's lane (z 11 to 13) with car A's front at the body: a contact.
            if (front > 11f && z - car.RearBehind < 13f && ax + 2.25f > -car.HalfWidth && ax - 2.25f < car.HalfWidth)
                run.Bad.Add($"{t:0.00} s: in A's way with A at {ax:0.0}");
            var target = Math.Min(Math.Min(d.YieldAlong ?? float.MaxValue, d.StopAlong ?? float.MaxValue), hold ?? float.MaxValue) - 40f;
            var room = target - front;
            var need = room <= 0.05f ? float.MaxValue : v * v / (2f * room);
            if (need > PursuitDriver.Decel) v = Math.Max(0f, v - Math.Min(need, PursuitDriver.HardDecel) * dt);
            else v = Math.Min(rideMs, v + 2f * dt);
            if (room <= 0.05f && v < 1f) v = 0f;
            z += v * dt;
        }
        foreach (var l in run.Bad.Take(5)) output.WriteLine(l);
        return run;
    }

    // AA1: held short of lane A, the ride car stays outside A's way, which the cautious judgement finds clear without
    // the car ever clearing through it. Released with car A coming, it must wait with the full headway: no commitment
    // was made, so nothing lets it go on with less, whichever order the cars come in.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StayingOutsideIsNotClearingThrough(bool bFirst)
    {
        // Held 2.5 m short of A's lane until 4 s; car A (10 m/s) reaches the ride car's path at about 5.7 s.
        var run = Drive(bFirst, holdZ: 8.5f, holdFrom: 0f, release: 4f, ax0: -57f, rideMs: 8f, rearZ0: -25f, steps: 300);
        Assert.Empty(run.Bad);
        // It went only after A had passed (A's rear past the ride car's side).
        Assert.True(run.Crossed);
        Assert.True(run.AWhenCrossed - 2.25f > RideCar.Dash.HalfWidth, $"crossed with A at {run.AWhenCrossed:0.0}");
    }
}
