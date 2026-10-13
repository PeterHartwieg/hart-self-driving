using System;
using System.Collections.Generic;
using System.Linq;
using DrivenRides.Core.Drive;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;
using Xunit;
using World = DrivenRides.Core.Tests.ReviewRound3Tests.World;

namespace DrivenRides.Core.Tests;

// The planner's fixes from review round 4 (build/review-nwh/fixes-r4.md, W1 to W6).
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

    // A car driving the plan it was given, as the model drives (so where it goes can be foreseen), at no more than a
    // speed.
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

        // With what it brakes for, as NwhDrive gives it.
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

    // A look-ahead stepped one physics step at a time while the car drives on.
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
        // A van the line driven now touches at 85 m; the lane's new move, 1 m right, passes it.
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
        // The join's check took many physics steps, the car drove on meanwhile, and it was taken up where checked.
        Assert.True(job.Slices > 20, $"{job.Slices} slices");
        Assert.True(car.Pose.Z - start.Z > 2f);
        Assert.True(WayOnPlanner.JoinEnvelope(car.Pose, job.JoinedFrom!.Value), $"{car.Pose.Z} vs {job.JoinedFrom.Value.Z}");
    }

    [Fact]
    public void W1ACarThatBrakesWhileItsJoinIsCheckedIsNotTakenUpFromTheOldCheck()
    {
        // A van the line driven now touches at 85 m; the lane's new move, 1 m right, passes it.
        var w = new World(Post(-0.8f, 85f)) { PerLook = 0.25 };
        var route = w.Lane.ToList();
        var watch = new WayOnPlanner.Watch();
        var plan = new World().PlanOf(route);
        watch.Installed(plan);
        var car = new Car(plan, w.Graph, 15f, 8f);
        w.Shifts[(w.Lane[8], w.Lane[9])] = (2f, 8f, 1.0f);
        // A light 12 m ahead turns red a few physics steps in: the car brakes for it, and is foreseen braking.
        var job = Drive(PlannerFor(w).LookAhead(route, route, 1, () => car.Pose, NoGoals, 0, watch), car, 1.0, step => { if (step == 5) car.StopAlong = car.Front + 12f; });
        // Taken up from where the car then was (review round 5: foreseen with the stop, it is).
        Assert.NotNull(job.Found);
        Assert.True(WayOnPlanner.JoinEnvelope(car.Pose, job.JoinedFrom!.Value));
        Assert.True(car.Pose.Speed < 6f);
    }

    [Fact]
    public void W2ASidewaysMoveIsHeldShortOfItsRampAsItsLaneIsSwept()
    {
        // A van the line driven now touches at 35 m.
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
        // Held once the line driven now was swept there and failed, with the sweep one step past the lane, short of
        // the obstacle the move passes (review round 5); no way on, so still held at the end.
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
        // The van has gone: the line driven now passes, and the hold it left goes.
        blocked = false;
        Assert.Equal(-1, Run(planner.LookAhead(route, route, 1, Standing(15f), NoGoals, 0, watch)).HoldPosition);
        Assert.Empty(watch.Holds);
        // Held again; then the lane needs no move after all: the hold goes with its change.
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
        // The plan took the turn through its vias, and it is now an arc: the turn is checked again, a way on planned
        // (its checks, its line) and its join checked, all within the job.
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
        // The step the car is on (10 -> 20 m), held short of the van 4 m along it, not of the step's start.
        Assert.Equal(1, job.HoldPosition);
        Assert.Equal(4f - WayOnPlanner.ObstacleClear, job.HoldOffset, 3);

        var alongOf = Enumerable.Range(0, 13).Select(k => k * 10f).ToArray();
        WayOnControl ControlFor(float offset)
        {
            IEnumerable<object?> Holding(WayOnJob j) { j.HoldPosition = 1; j.HoldOffset = offset; j.HoldWhat = "a van"; yield return null; }
            return new WayOnControl { LookAhead = (p, pose) => new WayOnJob("ahead", p, Holding) };
        }
        // Room to stop HoldShort before the van at Decel: there.
        var easy = ControlFor(30f);
        easy.Ahead(0f, 0.02f, 1, alongOf, 30f, 6f, Standing(25f));
        Assert.Equal(38f, easy.HoldAlong!.Value, 3);
        // Not quite: where Decel braking from now ends, still short of the van, and no emergency.
        var nearer = ControlFor(30f);
        nearer.Ahead(0f, 0.02f, 1, alongOf, 31f, 6f, Standing(26f));
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
        // Within the tolerances: no change.
        w.Shifts[(w.Lane[5], w.Lane[6])] = (2.2f, 8.1f, 0.33f);
        Assert.False(Run(planner.LookAhead(route, route, 1, Standing(15f), NoGoals, 0, watch)).Changed);
        // A real change whose value wobbles from look to look: planned round MaxTries times in all.
        var routes = new List<int>();
        foreach (var offset in new[] { 0.9f, 0.93f, 0.97f, 1.01f, 1.04f })
        {
            w.Shifts[(w.Lane[5], w.Lane[6])] = (2f, 8f, offset);
            Run(planner.LookAhead(route, route, 1, Standing(15f), NoGoals, 0, watch));
            routes.Add(w.Routes);
        }
        // Each try plans from the same starts: MaxTries tries' worth of routes, whatever the wobble.
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
        // A red with its line 10 m ahead of the front: foreseen stopping short of it.
        var front = 15f + Small().FrontAhead;
        var red = w.Check.Predict(plan, line, new CarPose(0f, 15f, 0f, 8f) { StopAlong = front + 10f }, 4f);
        Assert.True(red.Speed < 0.5f, $"{red.Speed}");
        Assert.InRange(red.Z + Small().FrontAhead, front + 6f, front + 10.2f);
        // A car 8 m ahead going on at 3 m/s: foreseen following it, not overtaking it.
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
        // The van has gone: the move is only removed, and the line driven now (moved over) passes.
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
        // A: passes.
        w.Shifts[(w.Lane[6], w.Lane[7])] = (3f, 7f, -0.5f);
        Assert.Equal(-1, Look().HoldPosition);
        Assert.True(watch.Passed.ContainsKey(key));
        // B: fails, held, no longer counted as passed.
        van = true;
        w.Shifts[(w.Lane[6], w.Lane[7])] = (3f, 7f, -1.4f);
        Assert.Equal(6, Look().HoldPosition);
        Assert.False(watch.Passed.ContainsKey(key));
        // A again, the van gone: swept again since a hold is out, passes, and the hold goes.
        van = false;
        w.Shifts[(w.Lane[6], w.Lane[7])] = (3f, 7f, -0.5f);
        Assert.Equal(-1, Look().HoldPosition);
        Assert.Empty(watch.Holds);
    }
}
