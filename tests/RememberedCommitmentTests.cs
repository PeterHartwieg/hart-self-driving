using System;
using System.Collections.Generic;
using System.Linq;
using DrivenRides.Core.Drive;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;
using Xunit;
using Xunit.Abstractions;

namespace DrivenRides.Core.Tests;

// Review round 5 (Y1): a commitment remembered from a moment ago is judged again in the final pass, on the trajectory
// every constraint leaves. Here the ride car commits to crossing lane A; then car B comes on lane B just past it and
// the ride car has to give way to B, which would leave it standing across lane A. Whatever the order of the cars, the
// ride car must not go on into lane A with car A coming: it stops short of A's way, or, too late for that, it is
// judged by the emergency rule (a stop, or a forced commit), never waved on by the memory.
public sealed class RememberedCommitmentTests
{
    private readonly ITestOutputHelper output;
    public RememberedCommitmentTests(ITestOutputHelper output) => this.output = output;

    private sealed class NoFlags : ILiveFlags
    {
        public bool Stop(int waypoint) => false;
        public bool GiveWayEntry(int waypoint, out int junction, out int road) { junction = road = -1; return false; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ANewYieldPastARememberedCrossingIsJudgedAgain(bool bFirst)
    {
        var car = RideCar.Dash;
        var line = DriveLine.Build(Enumerable.Range(0, 121).Select(k => new Vec3(0, 0, -40 + k)).ToList(), Array.Empty<(int, Polyline)>());
        var driver = new PursuitDriver(line, car.Wheelbase, car.FrontAhead, 13.9f);
        var b = new TurnTestGraph();
        // Lane A east along z = 12, lane B west along z = 17.
        var laneA = Enumerable.Range(0, 41).Select(k => b.Add(-150 + k * 5, 12)).ToArray();
        var laneB = Enumerable.Range(0, 41).Select(k => b.Add(150 - k * 5, 17)).ToArray();
        b.Link(laneA);
        b.Link(laneB);
        var graph = b.Build();
        var traffic = new TrafficDriver(graph, line, driver, new RideBody { Wheelbase = car.Wheelbase, FrontAhead = car.FrontAhead, RearBehind = car.RearBehind, HalfWidth = car.HalfWidth });
        const float dt = 0.04f, rideMs = 8f;
        // The line starts at z = -40: metres along it are z + 40. Car A far enough that the ride car commits to
        // crossing ahead of it (about 6 s to spare); car B arriving at lane B as the ride car would.
        float z = -20f, ax = -100f, bx = 60f;
        var d = new TrafficDecision();
        var bad = new List<string>();
        for (var step = 0; step < 150; step++)
        {
            var t = step * dt;
            ax += 10f * dt;
            var a = new OtherCar { Id = 1, X = ax, Z = 12f, Heading = (float)(Math.PI / 2), Vx = 10f, Length = 4.5f, Width = 1.9f, TriggerLength = 3f,
                Target = laneA.Where(w => graph.Position(w).X > ax + 2.25f).DefaultIfEmpty(-1).First() };
            var cars = new List<OtherCar> { a };
            // Car B shows up once the ride car is close to lane A, coming fast enough that the ride car must give way.
            if (z + car.FrontAhead > 6f)
            {
                bx -= 11f * dt;
                var bc = new OtherCar { Id = 2, X = bx, Z = 17f, Heading = -(float)(Math.PI / 2), Vx = -11f, Length = 4.5f, Width = 1.9f, TriggerLength = 3f,
                    Target = laneB.Where(w => graph.Position(w).X < bx - 2.25f).DefaultIfEmpty(-1).First() };
                if (bFirst) cars.Insert(0, bc); else cars.Add(bc);
            }
            else bx = 35f;
            // The driver keeps up with the car, as on the road (the traffic driver looks for the car near it).
            driver.StartAt(0f, z);
            traffic.Decide(new TrafficSnapshot { Time = t, RearX = 0f, RearZ = z, Heading = 0f, Speed = rideMs, Marks = Array.Empty<PathMark>(), Cars = cars, Flags = new NoFlags() }, d);
            var front = z + car.FrontAhead;
            var frontAlong = front + 40f;
            if (step % 5 == 0) output.WriteLine($"{t:0.00} front {front:0.0} A {ax:0.0} B {(cars.Count > 1 ? bx : float.NaN):0.0} yield {d.YieldAlong - 40f:0.0} marginal {d.MarginalCommit} forced {d.ForcedCommit} {d.Why} {d.YieldNote}");
            // Going on (no stop at or short of the front) while car A is about to be where the ride car's body would
            // stand: waved on into A's way.
            var stops = d.YieldAlong is float y && y <= frontAlong + 0.5f || d.StopAlong is float st && st <= frontAlong + 0.5f;
            var aComing = ax + 2.25f > -3f && ax - 2.25f < 3f + 10f * 1f;
            // In A's lane (z 11 to 13, the body 4.6 m long), going on, with A within 2 s.
            aComing = ax + 2.25f > -20f && ax - 2.25f < 2f;
            if (cars.Count > 1 && front > 11f && front - car.FrontAhead - car.RearBehind < 13f && aComing && !stops && !d.ForcedCommit)
                bad.Add($"{t:0.00} s: front {front:0.0}, car A at {ax:0.0}: {d.Why}");
            // The ride car moves on as the decision lets it: stopping where it must, else at its speed.
            var target = Math.Min(d.YieldAlong ?? float.MaxValue, d.StopAlong ?? float.MaxValue);
            var v = target - frontAlong < 0.5f ? 0f : rideMs;
            z += v * dt;
        }
        foreach (var l in bad.Take(5)) output.WriteLine(l);
        Assert.Empty(bad);
    }
}
