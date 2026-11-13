using System;
using System.Collections.Generic;
using System.Linq;
using DrivenRides.Core.Drive;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;
using DrivenRides.Core.Testing;
using Xunit;

namespace DrivenRides.Core.Tests;

public sealed class ReviewRound2Tests
{
    private static CarSpec Small() => new("small", 2.5f, _ => 40f, new Vec3(0, 0.7f, 1.25f), new Vec3(0.9f, 0.7f, 2f), 50f, 1.5f);

    private static Func<BodyBox, string?> Post(float x, float z) => box =>
    {
        float s = (float)Math.Sin(box.Heading), c = (float)Math.Cos(box.Heading);
        float dx = x - box.Centre.X, dz = z - box.Centre.Z;
        return Math.Abs(dx * c - dz * s) <= box.Half.X && Math.Abs(dx * s + dz * c) <= box.Half.Z ? "post" : null;
    };

    private static (LaneGraph Graph, int[] Lane) North(int count = 13)
    {
        var b = new TurnTestGraph();
        var lane = Enumerable.Range(0, count).Select(i => b.Add(0, i * 10)).ToArray();
        b.Link(lane);
        return (b.Build(), lane);
    }

    [Fact]
    public void Q1ALineThatStartsAheadOfTheCarDoesNotJoinIt()
    {
        var points = Enumerable.Range(0, 11).Select(k => new Vec3(0, 0, 20 + k * 10)).ToList();
        var line = DriveLine.Build(points, Array.Empty<(int, Polyline)>());
        Assert.False(line.Joins(0f, 0f, 0, out _));
        Assert.True(line.Joins(0.3f, 21f, 0, out _));
    }

    [Fact]
    public void Q1TheDriveOntoANewLineIsCheckedFromTheCarsPose()
    {
        var (graph, lane) = North();
        var clear = new Feasibility(graph, Small(), _ => null);
        var plan = DrivePlan.Build(graph, lane, Small(), clear.Arc, clear.Clear);
        var line = plan.Line(graph);
        var pose = new CarPose(-1.4f, 20f, 0f);
        Assert.Null(clear.Join(plan, line, pose));
        var posted = new Feasibility(graph, Small(), Post(-1.4f, 26f));
        Assert.StartsWith("joining the new line: post", posted.Join(plan, line, pose));
    }

    private static (LaneGraph Graph, int[] Lane) Corner()
    {
        var b = new TurnTestGraph();
        var ids = new List<int>();
        for (var i = 0; i <= 3; i++) ids.Add(b.Add(0, i * 10));
        var a = 40 * Math.PI / 180;
        for (var i = 1; i <= 6; i++) ids.Add(b.Add((float)(Math.Sin(a) * i * 10), (float)(30 + Math.Cos(a) * i * 10)));
        var lane = ids.ToArray();
        b.Link(lane);
        return (b.Build(), lane);
    }

    [Fact]
    public void Q3ACornerThatFailsRoundedAndSharpFailsItsStep()
    {
        var (graph, lane) = Corner();
        var clean = new Feasibility(graph, Small(), _ => null);
        var plan = DrivePlan.Build(graph, lane, Small(), clean.Arc, clean.Clear);
        Assert.Contains(3, plan.Fillets);
        Assert.Empty(clean.ValidateNow(plan));
        Assert.Contains(3, plan.Fillets);
        var posted = new Feasibility(graph, Small(), Post(0.5f, 30f));
        var blocked = DrivePlan.Build(graph, lane, Small(), posted.Arc, posted.Clear);
        var failed = posted.ValidateNow(blocked);
        Assert.DoesNotContain(3, blocked.Fillets);
        Assert.Contains(failed, f => f.From == lane[3] && f.To == lane[4] && f.What.Contains("rounded, sharp or at 10 km/h") && f.What.Contains("post"));
    }

    [Fact]
    public void ACornerThroughANarrowGapIsDrivenSlowly()
    {
        var (graph, lane) = Corner();
        var gap = new Feasibility(graph, Small(), Post(2.0f, 30f));
        var plan = DrivePlan.Build(graph, lane, Small(), gap.Arc, gap.Clear);
        var notes = new List<string>();
        Assert.Empty(gap.ValidateNow(plan, notes));
        Assert.Contains(notes, n => n.Contains($"corner at {lane[3]} driven at 10 km/h"));
        Assert.Contains(plan.Slow, s => s.Position == 3 && s.SpeedKmh == Feasibility.SqueezeKmh);
        var closer = new Feasibility(graph, Small(), Post(1.9f, 31f));
        var blocked = DrivePlan.Build(graph, lane, Small(), closer.Arc, closer.Clear);
        Assert.Contains(closer.ValidateNow(blocked), f => f.From == lane[3] && f.What.Contains("at 10 km/h: post"));
        Assert.DoesNotContain(blocked.Slow, s => s.SpeedKmh == Feasibility.SqueezeKmh);
    }

    [Fact]
    public void WhereTheCarStandsIsNoObstacle()
    {
        var (graph, lane) = North();
        var plan = DrivePlan.Build(graph, lane, Small(), (e, x) => null);
        var line = plan.Line(graph);
        Assert.Null(new Feasibility(graph, Small(), Post(0.5f, 1f)).Sweep(plan, line, 0f, 20f));
        Assert.StartsWith("post", new Feasibility(graph, Small(), Post(0.5f, 15f)).Sweep(plan, line, 0f, 20f));
    }

    [Fact]
    public void AStraightLanesWaypointsAreNoCorners()
    {
        var (graph, lane) = North();
        var check = new Feasibility(graph, Small(), _ => null);
        Assert.Empty(DrivePlan.Build(graph, lane, Small(), check.Arc, check.Clear).Fillets);
        var (bent, bentLane) = Corner();
        Assert.Equal(new[] { 3 }, DrivePlan.Build(bent, bentLane, Small(), check.Arc, check.Clear).Fillets.ToArray());
    }

    [Fact]
    public void Q3AFailedSidewaysMoveFailsItsStepWithNoCollector()
    {
        var (graph, lane) = North();
        (float, float, float)? Shift(int from, int to) => from == lane[2] && to == lane[3] ? (2f, 8f, 1f) : null;
        var check = new Feasibility(graph, Small(), Post(1.0f, 25f));
        var plan = DrivePlan.Build(graph, lane, Small(), check.Arc, check.Clear, null, Shift);
        Assert.Single(plan.Shifts);
        var failed = check.ValidateNow(plan);
        Assert.Empty(plan.Shifts);
        Assert.Contains(failed, f => f.From == lane[2] && f.To == lane[3] && f.What.StartsWith("no way past within the lane"));
    }

    [Fact]
    public void Q4ATurnCheckedAgainIsTheSameShapeThoughANewObject()
    {
        var (graph, entry, exit) = TurnTestGraph.Turn();
        var check = new Feasibility(graph, Small(), _ => null);
        var before = check.Step(entry, exit);
        check.Forget(entry, exit);
        var now = check.Step(entry, exit);
        Assert.True(now.Feasible);
        Assert.NotNull(now.Arc);
        Assert.False(ReferenceEquals(before.Arc, now.Arc));
        Assert.True(now.SameShape(before));
        var vias = new Verdict { Feasible = true, Form = "vias" };
        Assert.False(now.SameShape(vias));
    }

    [Fact]
    public void Q6AnIslandBesideTheLaneIsNoKerbAWheelOnItIs()
    {
        var (graph, lane) = North();
        var check = new Feasibility(graph, Small(), _ => null) { GroundAt = p => p.X < -1.8f ? 0.47f : 0f };
        var plan = DrivePlan.Build(graph, lane, Small(), check.Arc, check.Clear);
        Assert.Null(check.Join(plan, plan.Line(graph), new CarPose(0f, 20f, 0f)));
        Assert.Null(check.Sweep(plan, plan.Line(graph), 10f, 40f));
        var wheelUp = new Feasibility(graph, Small(), _ => null) { GroundAt = p => p.X < -0.6f && p.Z > 30f && p.Z < 31f ? 0.15f : 0f };
        var found = wheelUp.Sweep(plan, plan.Line(graph), 10f, 45f);
        Assert.True(found != null && found.Contains("kerb: a"), found ?? "null");
        // A metre up under one wheel is no kerb: the ground probe met something else (0.1.90, by a bridge).
        var deck = new Feasibility(graph, Small(), _ => null) { GroundAt = p => p.X < -0.6f && p.Z > 30f && p.Z < 31f ? 0.97f : 0f };
        Assert.Null(deck.Sweep(plan, plan.Line(graph), 10f, 45f));
    }

    [Fact]
    public void Q9ThePavementIsAskedAsTheCornersMove()
    {
        var (graph, lane) = North();
        var f = new Fixture { Route = lane.ToList(), Kind = CaseKind.Ride, Manoeuvre = CaseKind.Ride, EntryIndex = 1, ExitIndex = 2 };
        var judge = new Judge(graph, CaseGeometry.Build(graph, f), f);
        var asked = 0;
        judge.PavementAt = _ => { asked++; return false; };
        for (var step = 0; step <= 50; step++)
        {
            var h = step / 50.0 * Math.PI / 2;
            float s = (float)Math.Sin(h), c = (float)Math.Cos(h);
            Vec3 At(float x, float z) => new(x * c + z * s, 0, -x * s + z * c + 20);
            judge.Add(new Sample { Step = step, Time = step * 0.02, Centre = new Vec3(0, 0, 20), FrontAxle = At(0, 1),
                Footprint = new[] { At(-1, 2), At(1, 2), At(1, -2), At(-1, -2) } });
        }
        Assert.InRange(asked, 20, 40);
    }

    [Fact]
    public void Q5APlanningJobRunsInSlicesAndSaysWhereToHoldFirst()
    {
        var now = 0.0;
        IEnumerable<object?> Work(WayOnJob job)
        {
            for (var i = 0; i < 6; i++)
            {
                now += 2;
                if (i == 1) job.HoldPosition = 7;
                yield return null;
            }
            job.Note = "done";
        }
        var job = new WayOnJob("ahead", 3, Work, () => now);
        Assert.False(job.Step(1.0));
        Assert.False(job.Step(1.0));
        Assert.Equal(7, job.HoldPosition);
        Assert.False(job.Done);
        while (!job.Step(1.0)) { }
        Assert.Equal("done", job.Note);
        Assert.True(job.Slices >= 4);
    }
}
