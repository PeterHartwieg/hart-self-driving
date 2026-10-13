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
        var (u, uEntry, _) = TurnTestGraph.UTurn();
        var uTurn = new Feasibility(u, Small()).Step(uEntry, u.UTurn(uEntry));
        output.WriteLine(uTurn.ToString());
        Assert.True(uTurn.Feasible, uTurn.ToString());
        Assert.Equal("vias", uTurn.Form);
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
        Assert.Equal("post", verdict.Hit);
        // Without the post, the same car makes it.
        Assert.True(new Feasibility(graph, Small(), _ => null).Step(entry, exit).Feasible);
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
