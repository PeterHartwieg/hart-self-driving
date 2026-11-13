using System;
using System.Collections.Generic;
using System.Linq;
using DrivenRides.Core.Drive;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;
using Xunit;
using World = DrivenRides.Core.Tests.ReviewRound3Tests.World;

namespace DrivenRides.Core.Tests;

public sealed class ReviewRound4Tests
{
    private static CarSpec Small() => new("small", 2.5f, _ => 40f, new Vec3(0, 0.7f, 1.25f), new Vec3(0.9f, 0.7f, 2f), 50f, 1.5f);
    private static readonly Goal[] NoGoals = Array.Empty<Goal>();
    private static Func<CarPose> Standing(float z) => () => new CarPose(0f, z, 0f, 0f);
    private static WayOnPlanner PlannerFor(World w) => new WayOnPlanner(w) { ClockMs = () => w.Clock };

    private static Func<BodyBox, string?> Post(float x, float z) => box =>
    {
        float s = (float)Math.Sin(box.Heading), c = (float)Math.Cos(box.Heading);
        float dx = x - box.Centre.X, dz = z - box.Centre.Z;
        return Math.Abs(dx * c - dz * s) <= box.Half.X && Math.Abs(dx * s + dz * c) <= box.Half.Z ? "post" : null;
    };

    private static WayOnJob Run(WayOnJob job, double budget = 1000)
    {
        for (var k = 0; k < 100000 && !job.Step(budget); k++) { }
        Assert.True(job.Done);
        return job;
    }

    private sealed class Car
    {
        private readonly CarModel model;
        private readonly PursuitDriver driver;
        public float MaxSpeed;
        public float? StopAlong;
        public float Front => driver.Along + Small().FrontAhead;

        public Car(DrivePlan plan, LaneGraph graph, float z, float speed)
        {
            var line = plan.Line(graph);
            driver = plan.Driver(line, Small().Wheelbase, Small().FrontAhead, Small().TrackWidth);
            model = Small().Model(0, z, 0, speed);
            driver.StartAt(0, z);
            MaxSpeed = speed;
        }

        public CarPose Pose => new((float)model.X, (float)model.Z, (float)model.Heading, (float)model.Speed, (float)model.Steer)
            { MaxSpeed = MaxSpeed, StopAlong = StopAlong };

        public void Step()
        {
            var input = model.Input(0.02f);
            input.MaxSpeed = MaxSpeed;
            input.StopAlong = StopAlong;
            model.Step(driver.Step(input), 0.02f);
        }
    }

    private static WayOnJob Drive(WayOnJob job, Car car, double budget, Action<int>? each = null)
    {
        for (var step = 0; step < 5000; step++)
        {
            each?.Invoke(step);
            if (job.Step(budget)) return job;
            car.Step();
        }
        throw new Exception("the job didn't end");
    }

    [Fact]
    public void W1AWayOnFoundWhileTheCarDrivesIsTakenUpWhereItsJoinWasChecked()
    {
        var w = new World(Post(-0.8f, 85f)) { PerLook = 0.25 };
        var route = w.Lane.ToList();
        var watch = new WayOnPlanner.Watch();
        var plan = new World().PlanOf(route);
        watch.Installed(plan);
        var car = new Car(plan, w.Graph, 15f, 8f);
        var start = car.Pose;
        w.Shifts[(w.Lane[8], w.Lane[9])] = (2f, 8f, 1.0f);
        var job = Drive(PlannerFor(w).LookAhead(route, route, 1, () => car.Pose, NoGoals, 0, watch), car, 1.0);
        Assert.NotNull(job.Found);
        Assert.True(job.Slices > 20, $"{job.Slices} slices");
        Assert.True(car.Pose.Z - start.Z > 2f);
        Assert.True(WayOnPlanner.JoinEnvelope(car.Pose, job.JoinedFrom!.Value), $"{car.Pose.Z} vs {job.JoinedFrom.Value.Z}");
    }

    [Fact]
    public void W1ACarThatBrakesWhileItsJoinIsCheckedIsNotTakenUpFromTheOldCheck()
    {
        var w = new World(Post(-0.8f, 85f)) { PerLook = 0.25 };
        var route = w.Lane.ToList();
        var watch = new WayOnPlanner.Watch();
        var plan = new World().PlanOf(route);
        watch.Installed(plan);
        var car = new Car(plan, w.Graph, 15f, 8f);
        w.Shifts[(w.Lane[8], w.Lane[9])] = (2f, 8f, 1.0f);
        var job = Drive(PlannerFor(w).LookAhead(route, route, 1, () => car.Pose, NoGoals, 0, watch), car, 1.0, step => { if (step == 5) car.StopAlong = car.Front + 12f; });
        Assert.NotNull(job.Found);
        Assert.True(WayOnPlanner.JoinEnvelope(car.Pose, job.JoinedFrom!.Value));
        Assert.True(car.Pose.Speed < 6f);
    }

    [Fact]
    public void W2ASidewaysMoveIsHeldShortOfItsRampAsItsLaneIsSwept()
    {
        var w = new World(Post(0.6f, 35f)) { NoRoutes = true, PerLane = 0.1 };
        var route = w.Lane.ToList();
        var watch = new WayOnPlanner.Watch();
        watch.Installed(new World().PlanOf(route));
        w.Shifts[(w.Lane[3], w.Lane[4])] = (4f, 8f, -1.4f);
        var sweptBeforeHold = -1;
        var offset = 0f;
        var swept = 0;
        w.Swept = k => swept = k;
        var job = PlannerFor(w).LookAhead(route, route, 1, Standing(15f), NoGoals, 0, watch);
        while (!job.Step(0.05))
            if (sweptBeforeHold < 0 && job.HoldPosition == 3) { sweptBeforeHold = swept; offset = job.HoldOffset; }
        Assert.InRange(sweptBeforeHold, 3, 4);
        Assert.Equal(4f - WayOnPlanner.ObstacleClear, offset, 3);
        Assert.Equal(3, job.HoldPosition);
    }

    [Fact]
    public void W3AHoldGoesWhenTheLineDrivenNowPassesAgainOrTheChangeGoes()
    {
        var blocked = true;
        var w = new World(box => blocked && Math.Abs(box.Centre.Z - 65f) < box.Half.Z + 1f && Math.Abs(box.Centre.X - 0.5f) < box.Half.X + 0.3f ? "van" : null) { NoRoutes = true };
        var route = w.Lane.ToList();
        var watch = new WayOnPlanner.Watch();
        watch.Installed(new World().PlanOf(route));
        w.Shifts[(w.Lane[6], w.Lane[7])] = (3f, 7f, -1.3f);
        var planner = PlannerFor(w);
        Assert.Equal(6, Run(planner.LookAhead(route, route, 1, Standing(15f), NoGoals, 0, watch)).HoldPosition);
        blocked = false;
        Assert.Equal(-1, Run(planner.LookAhead(route, route, 1, Standing(15f), NoGoals, 0, watch)).HoldPosition);
        Assert.Empty(watch.Holds);
        blocked = true;
        w.Shifts[(w.Lane[6], w.Lane[7])] = (3f, 7f, -1.6f);
        Assert.Equal(6, Run(planner.LookAhead(route, route, 1, Standing(15f), NoGoals, 0, watch)).HoldPosition);
        w.Shifts.Remove((w.Lane[6], w.Lane[7]));
        Assert.Equal(-1, Run(planner.LookAhead(route, route, 1, Standing(15f), NoGoals, 0, watch)).HoldPosition);
        Assert.Empty(watch.Holds);
    }

    [Fact]
    public void W4APreparedRouteIsBuiltWithoutASimulation()
    {
        var (graph, entry, exit) = TurnTestGraph.Turn();
        var looks = 0;
        var check = new Feasibility(graph, Small(), _ => { looks++; return null; });
        var route = new List<int> { 0, 1, entry, exit, exit + 1, exit + 2, exit + 3 };
        foreach (var _ in check.PrepareWork(route)) { }
        Assert.True(looks > 0);
        var before = looks;
        var plan = DrivePlan.Build(graph, route, Small(), check.Arc, check.Clear);
        Assert.Equal(before, looks);
        Assert.Single(plan.Arcs);
    }

    [Fact]
    public void W4TheWholeLookAheadJobKeepsToItsBudget()
    {
        var (graph, entry, exit) = TurnTestGraph.Turn();
        var route = new[] { 0, 1, entry, exit, exit + 1, exit + 2, exit + 3, exit + 4 };
        var w = new World(graph, route) { PerLook = 0.25, PerLane = 0.05, PerRoute = 0.2 };
        var watch = new WayOnPlanner.Watch();
        var plan = new World(graph, route).PlanOf(route.ToList());
        watch.Installed(plan);
        watch.Baseline[(entry, exit)] = new Verdict { Feasible = true, Form = "vias" };
        var job = Run(PlannerFor(w).LookAhead(route, plan.Path, 0, () => new CarPose(0f, -28f, 0f, 0f), NoGoals, 0, watch), 1.0);
        Assert.True(job.Found != null, job.Note + " | " + string.Join(" | ", w.Notes));
        Assert.True(job.Milliseconds > 20.0, $"{job.Milliseconds:0.0} ms");
        Assert.True(job.LongestSlice <= 1.0 + 0.25 + 0.2 + 0.01, $"longest slice {job.LongestSlice:0.00} ms");
    }

    [Fact]
    public void W5AHoldOnTheStepTheCarIsOnStopsShortOfTheObstacleNotBehindTheCar()
    {
        var w = new World { NoRoutes = true };
        w.Parked[(w.Lane[1], w.Lane[2])] = "a parked van";
        var route = w.Lane.ToList();
        var watch = new WayOnPlanner.Watch();
        watch.Installed(w.PlanOf(route));
        var job = Run(PlannerFor(w).LookAhead(route, route, 1, Standing(12f), NoGoals, 0, watch));
        Assert.Equal(1, job.HoldPosition);
        Assert.Equal(4f - WayOnPlanner.ObstacleClear, job.HoldOffset, 3);

        var alongOf = Enumerable.Range(0, 13).Select(k => k * 10f).ToArray();
        WayOnControl ControlFor(float offset)
        {
            IEnumerable<object?> Holding(WayOnJob j) { j.HoldPosition = 1; j.HoldOffset = offset; j.HoldWhat = "a van"; yield return null; }
            return new WayOnControl { LookAhead = (p, pose) => new WayOnJob("ahead", p, Holding) };
        }
        var easy = ControlFor(30f);
        easy.Ahead(0f, 0.02f, 1, alongOf, 30f, 6f, Standing(25f));
        Assert.Equal(38f, easy.HoldAlong!.Value, 3);
        var nearer = ControlFor(30f);
        nearer.Ahead(0f, 0.02f, 1, alongOf, 32.5f, 6f, Standing(27.5f));
        Assert.InRange(nearer.HoldAlong!.Value, 38.01f, 40f - WayOnControl.PastHold);
        Assert.Equal(WayOnControl.Act.Drive, nearer.Ahead(0.02f, 0.02f, 1, alongOf, 38f, 0f, Standing(33f)).Act);
    }

    [Fact]
    public void W6NoiseInAChangesValuesNeitherMakesANewChangeNorResetsItsTries()
    {
        var w = new World(Post(-0.6f, 55f)) { NoRoutes = true };
        var route = w.Lane.ToList();
        var watch = new WayOnPlanner.Watch();
        w.Shifts[(w.Lane[5], w.Lane[6])] = (2f, 8f, 0.30f);
        watch.Installed(w.PlanOf(route));
        var planner = PlannerFor(w);
        w.Shifts[(w.Lane[5], w.Lane[6])] = (2.2f, 8.1f, 0.33f);
        Assert.False(Run(planner.LookAhead(route, route, 1, Standing(15f), NoGoals, 0, watch)).Changed);
        var routes = new List<int>();
        foreach (var offset in new[] { 0.9f, 0.93f, 0.97f, 1.01f, 1.04f })
        {
            w.Shifts[(w.Lane[5], w.Lane[6])] = (2f, 8f, offset);
            Run(planner.LookAhead(route, route, 1, Standing(15f), NoGoals, 0, watch));
            routes.Add(w.Routes);
        }
        Assert.Equal(WayOnPlanner.MaxTries, watch.Tries[$"shift {w.Lane[5]}->{w.Lane[6]}"]);
        Assert.Equal(WayOnPlanner.MaxTries * routes[0], routes[4]);
    }

    [Fact]
    public void Z1WhereTheCarWillBeIsForeseenWithWhatItBrakesFor()
    {
        var w = new World();
        var plan = w.PlanOf(w.Lane.ToList());
        var line = plan.Line(w.Graph);
        var now = new CarPose(0f, 15f, 0f, 8f);
        var free = w.Check.Predict(plan, line, now, 3f);
        Assert.True(free.Z > 15f + 20f);
        var front = 15f + Small().FrontAhead;
        var red = w.Check.Predict(plan, line, new CarPose(0f, 15f, 0f, 8f) { StopAlong = front + 10f }, 4f);
        Assert.True(red.Speed < 0.5f, $"{red.Speed}");
        Assert.InRange(red.Z + Small().FrontAhead, front + 6f, front + 10.2f);
        var follow = w.Check.Predict(plan, line, new CarPose(0f, 15f, 0f, 8f) { GapAhead = 8f, GapSpeed = 3f }, 3f);
        Assert.True(follow.Z + Small().FrontAhead < front + 8f + 3f * 3f, $"{follow.Z}");
    }

    [Fact]
    public void Z2AMoveTheLineDrivenNowPassesIsNotHeldAndAMoveOnlyRemovedIsNot()
    {
        var w = new World { NoRoutes = true };
        var route = w.Lane.ToList();
        var watch = new WayOnPlanner.Watch();
        w.Shifts[(w.Lane[5], w.Lane[6])] = (2f, 8f, 0.6f);
        watch.Installed(w.PlanOf(route));
        w.Shifts.Remove((w.Lane[5], w.Lane[6]));
        var job = PlannerFor(w).LookAhead(route, route, 1, Standing(15f), NoGoals, 0, watch);
        while (!job.Step(0.05)) Assert.Equal(-1, job.HoldPosition);
        Assert.False(job.Changed);
        Assert.Empty(watch.Holds);
    }

    [Fact]
    public void Z3AMoveThatPassedComesBackAfterOneThatFailedAndIsHeldNoLonger()
    {
        var van = false;
        var w = new World(box => van && Post(0.5f, 65f)(box) is string hit ? hit : null) { NoRoutes = true };
        var route = w.Lane.ToList();
        var watch = new WayOnPlanner.Watch();
        watch.Installed(new World().PlanOf(route));
        var planner = PlannerFor(w);
        WayOnJob Look() => Run(planner.LookAhead(route, route, 1, Standing(15f), NoGoals, 0, watch));
        var key = $"shift {w.Lane[6]}->{w.Lane[7]}";
        w.Shifts[(w.Lane[6], w.Lane[7])] = (3f, 7f, -0.5f);
        Assert.Equal(-1, Look().HoldPosition);
        Assert.True(watch.Passed.ContainsKey(key));
        van = true;
        w.Shifts[(w.Lane[6], w.Lane[7])] = (3f, 7f, -1.4f);
        Assert.Equal(6, Look().HoldPosition);
        Assert.False(watch.Passed.ContainsKey(key));
        van = false;
        w.Shifts[(w.Lane[6], w.Lane[7])] = (3f, 7f, -0.5f);
        Assert.Equal(-1, Look().HoldPosition);
        Assert.Empty(watch.Holds);
    }

    [Fact]
    public void AJoinThatEndsInATurnIsJudgedOnTheStraightAfterIt()
    {
        var (graph, entry, exit) = TurnTestGraph.Turn();
        var route = new List<int> { 0, 1, entry, exit, exit + 1, exit + 2, exit + 3, exit + 4 };
        var plan = DrivePlan.Build(graph, route, Small(), (e, x) => null, (e, x, a) => true);
        var line = plan.Line(graph);
        var pose = new CarPose(0f, -28f, 0f, 0f);
        double Heading(float at) { Vec3 a = line.Line.At(at), b = line.Line.At(at + 0.5f); return Math.Atan2(b.X - a.X, b.Z - a.Z); }
        var start = line.Locate(pose.X, pose.Z, 0f, line.Line.Length).Along;
        Assert.True(Math.Abs(DriveLine.Wrap(Heading(start + 30f) - Heading(start + 22f))) > 5 * Math.PI / 180);
        Assert.Null(new Feasibility(graph, Small()).Join(plan, line, pose));
        var stiff = new CarSpec("stiff", 2.5f, _ => 6f, new Vec3(0, 0.7f, 1.25f), new Vec3(0.9f, 0.7f, 2f), 50f, 1.5f);
        Assert.Contains("off it after", new Feasibility(graph, stiff).Join(plan, line, pose) ?? "");
    }
}
