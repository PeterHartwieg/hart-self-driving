using System;
using System.Linq;
using DrivenRides.Core.Drive;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;
using DrivenRides.Core.Testing;
using Xunit;
using Xunit.Abstractions;

namespace DrivenRides.Core.Tests;

public sealed class FeasibilityTests
{
    private readonly ITestOutputHelper output;
    public FeasibilityTests(ITestOutputHelper output) => this.output = output;

    // A small car and a long one with a wide circle, lengths from the rear axle.
    private static CarSpec Small() => new("small", 2.5f, _ => 40f, new Vec3(0, 0.7f, 1.25f), new Vec3(0.9f, 0.7f, 2f), 50f, 1.5f);
    private static CarSpec Barge() => new("barge", 6f, _ => 18f, new Vec3(0, 1.2f, 3f), new Vec3(1.2f, 1.2f, 5f), 50f, 1.9f);

    [Fact]
    public void ForStepBuildsTheCatalogueCase()
    {
        var (graph, entry, exit) = TurnTestGraph.Turn();
        var options = new CatalogueOptions();
        var f = Catalogue.ForStep(graph, entry, exit, options)!;
        Assert.Equal(Catalogue.Build(graph, options).Single(c => c.Id == f.Id).Route, f.Route);
        var (u, uEntry, _) = TurnTestGraph.UTurn();
        var uCase = Catalogue.ForStep(u, uEntry, u.UTurn(uEntry), options)!;
        Assert.Equal(CaseKind.UTurn, uCase.Kind);
        Assert.Null(Catalogue.ForStep(graph, entry, entry + 1, options));
    }

    [Fact]
    public void ASmallCarMakesTheTurnAndTheUTurn()
    {
        var (graph, entry, exit) = TurnTestGraph.Turn();
        var check = new Feasibility(graph, Small());
        var turn = check.Step(entry, exit);
        output.WriteLine(turn.ToString());
        Assert.True(turn.Feasible, turn.ToString());
        var (u, uEntry) = TwoLaneUTurn();
        var uTurn = new Feasibility(u, Small()).Step(uEntry, u.UTurn(uEntry));
        output.WriteLine(uTurn.ToString());
        Assert.True(uTurn.Feasible, uTurn.ToString());
        Assert.Equal("u-curve", uTurn.Form);
    }

    // TurnTestGraph.UTurn with a second lane on the far carriageway, outside the target lane: room to swing into.
    private static (LaneGraph Graph, int Entry) TwoLaneUTurn()
    {
        var b = new TurnTestGraph();
        int a = b.Add(-30, 0), e = b.Add(-5, 0), j1 = b.Add(5, 0, true), j2 = b.Add(12, 0, true), j3 = b.Add(25, 0, true);
        int straight = b.Add(35, 0), end = b.Add(45, 0);
        b.Link(a, e, j1, j2, j3, straight, end);
        int w0 = b.Add(35, 15), wj1 = b.Add(20, 15, true), wj2 = b.Add(10, 15, true), wj3 = b.Add(0, 15, true), wj4 = b.Add(-10, 15, true);
        int exit = b.Add(-15, 15), w1 = b.Add(-30, 15), w2 = b.Add(-50, 15), w3 = b.Add(-70, 15);
        b.Link(w0, wj1, wj2, wj3, wj4, exit, w1, w2, w3);
        int o0 = b.Add(35, 19), oj1 = b.Add(20, 19, true), oj2 = b.Add(10, 19, true), oj3 = b.Add(0, 19, true), oj4 = b.Add(-10, 19, true);
        int o5 = b.Add(-15, 19), o6 = b.Add(-30, 19), o7 = b.Add(-50, 19), o8 = b.Add(-70, 19);
        b.Link(o0, oj1, oj2, oj3, oj4, o5, o6, o7, o8);
        b.Beside(w1, o6);
        b.Beside(o6, w1);
        return (b.Build(true), e);
    }

    [Fact]
    public void ACarThatEndsUpOffItsLanePastTheExitIsNotFeasible()
    {
        // The turn case driven on a path that swings 7 m to the right of the exit lane after the exit: what a U-turn
        // run wide into the parking lane looked like in T2 (the car nosed into a parked van; the check had passed it).
        var b = new TurnTestGraph();
        var (_, entry, exit) = TurnTestGraph.Turn();
        int a = b.Add(0, -30), run = b.Add(0, -20), e = b.Add(0, -10);
        int j1 = b.Add(0, -5, true), j2 = b.Add(0, 5, true), straight = b.Add(0, 10), end = b.Add(0, 20);
        b.Link(a, run, e, j1, j2, straight, end);
        int west = b.Add(-20, -2), crossEntry = b.Add(-10, -2), cj1 = b.Add(-5, -2, true), cj2 = b.Add(5, -2, true);
        int x = b.Add(10, -2), x2 = b.Add(20, -2), x3 = b.Add(30, -2), x4 = b.Add(40, -2), x5 = b.Add(50, -2);
        b.Link(west, crossEntry, cj1, cj2, x, x2, x3, x4, x5);
        var wide = b.Add(28, -9);
        var graph = b.Build(true);
        Assert.Equal((entry, exit), (e, x));
        var fixture = Catalogue.ForStep(graph, e, x, new CatalogueOptions())!;
        var path = fixture.Route.ToList();
        path.Insert(path.IndexOf(x2) + 1, wide);
        var check = new Feasibility(graph, Small());
        var verdict = check.Drive(fixture, DrivePlan.Fixed(path, null, 50f), "vias");
        output.WriteLine(verdict.ToString());
        Assert.False(verdict.Feasible);
        Assert.True(verdict.Reason.Contains("pavement") || verdict.Reason.Contains("off-lane"), verdict.Reason);
        Assert.True(check.Drive(fixture, DrivePlan.Fixed(fixture.Route.ToList(), null, 50f), "vias").Feasible);
    }

    [Fact]
    public void ALongCarWithAWideCircleGoesRoundTheBlock()
    {
        var (u, uEntry, _) = TurnTestGraph.UTurn();
        var check = new Feasibility(u, Barge());
        var verdict = check.Step(uEntry, u.UTurn(uEntry));
        output.WriteLine(verdict.ToString());
        Assert.False(verdict.Feasible);
        Assert.NotEqual("", verdict.Reason);
        // Kept for the session: the second question costs no simulation.
        var simulations = check.Simulations;
        check.Step(uEntry, u.UTurn(uEntry));
        Assert.Equal(simulations, check.Simulations);
    }

    [Fact]
    public void ABodyThatTouchesTheWorldFailsTheStep()
    {
        var (graph, entry, exit) = TurnTestGraph.Turn();
        // A post where the lanes cross: whatever line the car takes, its body passes over it.
        var post = new Vec3(4f, 0f, -3f);
        string? World(BodyBox box)
        {
            float s = (float)Math.Sin(box.Heading), c = (float)Math.Cos(box.Heading);
            float dx = post.X - box.Centre.X, dz = post.Z - box.Centre.Z;
            float right = dx * c - dz * s, ahead = dx * s + dz * c;
            return Math.Abs(right) <= box.Half.X && Math.Abs(ahead) <= box.Half.Z ? "post" : null;
        }
        var verdict = new Feasibility(graph, Small(), World).Step(entry, exit);
        output.WriteLine(verdict.ToString());
        Assert.False(verdict.Feasible);
        Assert.Contains("contact", verdict.Reason);
        Assert.StartsWith("post [rear axle", verdict.Hit);
        // Without the post, the same car makes it.
        Assert.True(new Feasibility(graph, Small(), _ => null).Step(entry, exit).Feasible);
    }

    [Fact]
    public void AWheelUpOnTheKerbFailsTheStep()
    {
        // NWH's wheels are ray casts: a wheel on the pavement touches nothing, so the ground under each wheel is asked.
        var (graph, entry, exit) = TurnTestGraph.Turn();
        var flat = new Feasibility(graph, Small(), _ => null) { GroundAt = _ => 0f };
        Assert.True(flat.Step(entry, exit).Feasible);
        // A pavement 0.15 m up, starting just left of the approach lane: the left wheels (with the margin) are on it.
        var kerb = new Feasibility(graph, Small(), _ => null) { GroundAt = p => p.X < -1f ? 0.15f : 0f };
        var verdict = kerb.Step(entry, exit);
        output.WriteLine(verdict.ToString());
        Assert.False(verdict.Feasible);
        Assert.Contains("kerb", verdict.Reason);
    }

    [Fact]
    public void AUTurnIsOneSmoothCurveAsWideAsTheLanesAreApart()
    {
        // The two-lane street: inner to inner is 15 m across, outer to outer 23 m. The curve is a semicircle of half
        // that, its heading turning steadily (no straight across between two quarter turns, as through the vias).
        var (u, entry) = TwoLaneUTurn();
        foreach (var (target, width) in u.UTurns(entry))
        {
            var line = TurnArc.FitUTurn(u, entry, target, 3f)!;
            Assert.NotNull(line);
            Assert.InRange(line.Radius, width / 2f - 0.3f, width / 2f + 0.3f);
            var pts = line.Points;
            double? last = null;
            var turned = 0.0;
            for (var k = 1; k < pts.Count; k++)
            {
                var h = Math.Atan2(pts[k].X - pts[k - 1].X, pts[k].Z - pts[k - 1].Z);
                if (last is double l) turned += DriveLine.Wrap(h - l);
                last = h;
            }
            Assert.InRange(Math.Abs(turned), Math.PI - 0.15, Math.PI + 0.15);
        }
        // Too narrow for a car whose tightest followable circle is 9 m: none.
        Assert.Null(TurnArc.FitUTurn(u, entry, u.UTurn(entry), 9f));
    }

    [Fact]
    public void TheArcFitsTheCarsCircleAtCornerSpeed()
    {
        var car = Small();
        var radius = car.ArcMinRadius();
        // The arc it allows is at least as wide as the car's circle at that arc's corner speed, with the slack.
        var arc = Math.Max(radius * TurnArc.RadiusSlack, TurnArc.MinDriveRadius);
        var speed = Math.Max(PursuitDriver.MinCornerMs, (float)Math.Sqrt(PursuitDriver.Lateral * arc));
        Assert.True(arc >= car.RadiusAt(speed) * TurnArc.RadiusSlack - 0.02f);
        // NWH's Ackermann: about 2 t / L as sharply as a bicycle with the same wheel angle (sharper for this short car).
        Assert.InRange(car.RadiusAt(0f) * car.YawGain / (car.Wheelbase / (float)Math.Tan(40 * Math.PI / 180)), 0.9f, 1f);
    }

    [Fact]
    public void TheDriverFollowsAnArcWithNwhsAckermann()
    {
        // As PursuitDriverTests' corner, with the limo's track: steering for the car's real curvature keeps it on the
        // arc; steering for a bicycle ran it wide (tools/calibrate on rig session 20261013-083259).
        var arc = TurnArc.Fit(new Vec3(0, 0, 20), 0, 1, new Vec3(12, 0, 30), 1, 0, 6f)!;
        var points = Enumerable.Range(0, 8).Select(k => new Vec3(0, 0, -40 + k * 60f / 7)).Concat(Enumerable.Range(0, 10).Select(k => new Vec3(12 + k * 68f / 9, 0, 30))).ToList();
        var entry = points.FindIndex(p => Math.Abs(p.Z - 20) < 0.01f && p.X == 0);
        var line = DriveLine.Build(points, new[] { (entry, arc) });
        float Widest(float driverTrack)
        {
            var driver = new PursuitDriver(line, 3.75f, 4.9f, 14f, driverTrack);
            var car = new CarModel(3.75f, 35f, 0, -40, 0) { TrackWidth = 1.42f };
            var widest = 0f;
            for (var i = 0; i < 4000; i++)
            {
                var o = driver.Step(car.Input(0.02f));
                car.Step(o, 0.02f);
                if (i > 100) widest = Math.Max(widest, Math.Abs(o.Lateral));
                if (o.Along >= line.Line.Length - 6f && car.Speed < 0.05) break;
            }
            return widest;
        }
        var aware = Widest(1.42f);
        var bicycle = Widest(0f);
        output.WriteLine($"widest off the line: steering for NWH's Ackermann {aware:0.00} m, for a bicycle {bicycle:0.00} m");
        Assert.True(aware < 0.6f, $"{aware} m");
        Assert.True(aware < bicycle);
    }
}
