using System;
using System.Collections.Generic;
using System.Linq;
using DrivenRides.Core.Drive;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;
using Xunit;

namespace DrivenRides.Core.Tests;

// The planner's fixes from review round 3 (build/review-nwh/fixes-r3.md, R1 to R7), with a world of its own for the
// way-on planner (the game's is NwhPlanner) and a clock of its own for the jobs.
public sealed class ReviewRound3Tests
{
    private static CarSpec Small() => new("small", 2.5f, _ => 40f, new Vec3(0, 0.7f, 1.25f), new Vec3(0.9f, 0.7f, 2f), 50f, 1.5f);

    private static Func<BodyBox, string?> Post(float x, float z) => box =>
    {
        float s = (float)Math.Sin(box.Heading), c = (float)Math.Cos(box.Heading);
        float dx = x - box.Centre.X, dz = z - box.Centre.Z;
        return Math.Abs(dx * c - dz * s) <= box.Half.X && Math.Abs(dx * s + dz * c) <= box.Half.Z ? "post" : null;
    };

    // One lane: each route from a waypoint runs along it to the end, unless a step of it is blocked.
    private sealed class World : IWayOnWorld
    {
        public LaneGraph Graph { get; }
        public Feasibility Check { get; }
        public readonly int[] Lane;
        public readonly Dictionary<(int, int), string> Parked = new Dictionary<(int, int), string>();
        public readonly Dictionary<(int, int), (float From, float To, float Offset)> Shifts = new Dictionary<(int, int), (float, float, float)>();
        public readonly Dictionary<(int, int), BlockCause> Blocks = new Dictionary<(int, int), BlockCause>();
        public readonly List<string> Notes = new List<string>();
        public bool NoRoutes;
        public int Routes;
        // What each piece of work costs on the jobs' clock, ms.
        public double Clock, PerLane = 0.1, PerRoute = 0.2;

        public World(Func<BodyBox, string?>? world = null, int count = 13)
        {
            var b = new TurnTestGraph();
            Lane = Enumerable.Range(0, count).Select(i => b.Add(0, i * 10)).ToArray();
            b.Link(Lane);
            Graph = b.Build();
            Check = new Feasibility(Graph, Small(), world);
        }

        public DrivePlan PlanOf(IReadOnlyList<int> route) => DrivePlan.Build(Graph, route, Small(), Check.Arc, Check.Clear, null, ShiftFor);

        public IEnumerable<object?> PlanRoute(int start, IReadOnlyList<Goal> goals, int vehicleType, WayOnJob? job, PlannedRoute into)
        {
            Routes++;
            Clock += PerRoute;
            yield return null;
            var at = Array.IndexOf(Lane, start);
            if (NoRoutes || at < 0) { into.Summary = "no route"; yield break; }
            var route = Lane.Skip(at).ToList();
            for (var k = 0; k + 1 < route.Count; k++) if (IsBlocked(route[k], route[k + 1])) yield break;
            var plan = PlanOf(route);
            if (Check.ValidateNow(plan).Count > 0) yield break;
            into.Route = Route.Fixed(Graph, route);
            into.Plan = plan;
            into.Summary = "along the lane";
        }

        public IEnumerable<object?> LanesAhead(IReadOnlyList<int> path, int position, float metres, Vec3 near, List<(int From, int To, string What)> found)
        {
            for (var k = Math.Max(0, position); k + 1 < path.Count; k++)
            {
                Clock += PerLane;
                if (Parked.TryGetValue((path[k], path[k + 1]), out var what)) found.Add((path[k], path[k + 1], what));
                yield return null;
            }
        }

        public (float From, float To, float Offset)? ShiftFor(int from, int to) => Shifts.TryGetValue((from, to), out var s) ? s : ((float, float, float)?)null;
        public void Block(int from, int to, BlockCause cause) => Blocks[(from, to)] = cause;
        public bool IsBlocked(int from, int to) => Blocks.ContainsKey((from, to));
        public void Note(string text) => Notes.Add(text);
    }

    private static readonly Goal[] NoGoals = Array.Empty<Goal>();

    // The car on the lane at 15 m, heading north, its front at path position 1.
    private static Func<CarPose> At15 => () => new CarPose(0f, 15f, 0f, 8f);

    private static WayOnPlanner PlannerFor(World w) => new WayOnPlanner(w) { ClockMs = () => w.Clock };

    private static WayOnJob Run(WayOnJob job, double budget = 1000)
    {
        for (var k = 0; k < 10000 && !job.Step(budget); k++) { }
        Assert.True(job.Done);
        return job;
    }

    [Fact]
    public void R1ABlockedLaneIsHeldShortOfTheMomentItIsFound()
    {
        var w = new World { NoRoutes = true };
        w.Parked[(w.Lane[6], w.Lane[7])] = "a parked van";
        var watch = new WayOnPlanner.Watch();
        var route = w.Lane.ToList();
        watch.Installed(w.PlanOf(route));
        var job = PlannerFor(w).LookAhead(route, route, 1, At15, NoGoals, 0, watch);
        // A slice at a time (0.1 ms a lane): the hold is out before the sweep has finished, long before the job ends.
        var slices = 0;
        while (job.HoldPosition < 0 && !job.Step(0.05)) slices++;
        Assert.Equal(6, job.HoldPosition);
        Assert.False(job.Done);
        Run(job);
        Assert.True(job.Changed);
        Assert.Null(job.Found);
        Assert.Equal(6, job.HoldPosition);
        Assert.True(w.IsBlocked(w.Lane[6], w.Lane[7]));
    }

    [Fact]
    public void R1AChangeWithNoWayOnIsPlannedRoundAtMostMaxTriesTimes()
    {
        var w = new World { NoRoutes = true };
        w.Parked[(w.Lane[6], w.Lane[7])] = "a parked van";
        var watch = new WayOnPlanner.Watch();
        var route = w.Lane.ToList();
        watch.Installed(w.PlanOf(route));
        var planner = PlannerFor(w);
        var routes = new List<int>();
        for (var look = 0; look < 5; look++)
        {
            var job = Run(planner.LookAhead(route, route, 1, At15, NoGoals, 0, watch));
            Assert.Equal(6, job.HoldPosition);
            routes.Add(w.Routes);
        }
        // Each try plans from a few starts; after MaxTries looks, none.
        Assert.True(routes[WayOnPlanner.MaxTries - 1] > 0);
        Assert.Equal(routes[WayOnPlanner.MaxTries - 1], routes[4]);
    }

    [Fact]
    public void R1ANewSidewaysMoveWithNoWayOnKeepsAPlanThatStillPasses()
    {
        var w = new World { NoRoutes = true };
        var route = w.Lane.ToList();
        var watch = new WayOnPlanner.Watch();
        watch.Installed(w.PlanOf(route));
        // The lane 60 -> 70 m now wants a small move; nothing stands on the line driven now.
        w.Shifts[(w.Lane[6], w.Lane[7])] = (2f, 8f, 0.3f);
        var planner = PlannerFor(w);
        var job = planner.LookAhead(route, route, 1, At15, NoGoals, 0, watch);
        var heldEarly = false;
        while (!job.Step(0.05)) heldEarly |= job.HoldPosition == 6;
        Assert.True(heldEarly);
        Assert.True(job.Changed);
        Assert.Equal(-1, job.HoldPosition);
        // The next look: the same change is known to pass, so no hold at all, not even for a moment.
        var next = planner.LookAhead(route, route, 1, At15, NoGoals, 0, watch);
        while (!next.Step(0.05)) Assert.Equal(-1, next.HoldPosition);
        Assert.False(next.Changed);
    }

    [Fact]
    public void R1ANewSidewaysMoveWithNoWayOnHoldsWhereThePlanNowFails()
    {
        // A van parked so the line driven now (unshifted) touches it, 65 m along.
        var w = new World(Post(0.5f, 65f)) { NoRoutes = true };
        var route = w.Lane.ToList();
        var watch = new WayOnPlanner.Watch();
        var clean = new World();
        watch.Installed(clean.PlanOf(route));
        w.Shifts[(w.Lane[6], w.Lane[7])] = (3f, 7f, -1.3f);
        var job = Run(PlannerFor(w).LookAhead(route, route, 1, At15, NoGoals, 0, watch));
        Assert.Equal(6, job.HoldPosition);
        Assert.True(watch.Holds.ContainsKey((w.Lane[6], w.Lane[7])));
        Assert.Contains(w.Notes, n => n.Contains("fails there"));
    }

    [Fact]
    public void R3AMoveOfTheSameOffsetElsewhereAlongTheStepIsAChange()
    {
        var w = new World();
        w.Shifts[(w.Lane[6], w.Lane[7])] = (2f, 4f, 0.4f);
        var route = w.Lane.ToList();
        var watch = new WayOnPlanner.Watch();
        watch.Installed(w.PlanOf(route));
        var planner = PlannerFor(w);
        Assert.False(Run(planner.LookAhead(route, route, 1, At15, NoGoals, 0, watch)).Changed);
        w.Shifts[(w.Lane[6], w.Lane[7])] = (5f, 8f, 0.4f);
        var job = Run(planner.LookAhead(route, route, 1, At15, NoGoals, 0, watch));
        Assert.True(job.Changed);
        // A way on was found (the lane with the move where it is now).
        Assert.NotNull(job.Found);
        Assert.Contains(job.Found!.Value.Plan.Shifts, s => Math.Abs(s.From - 5f) < 0.01f);
    }

    [Fact]
    public void R2TheHoldIsAFixedPointAndOneTooLateToStopForGivesTheStepUp()
    {
        var alongOf = Enumerable.Range(0, 13).Select(k => k * 10f).ToArray();
        IEnumerable<object?> Holding(WayOnJob job)
        {
            job.HoldPosition = 5;
            job.HoldWhat = "lane 50 -> 60 m blocked";
            yield return null;
            yield return null;
        }
        // Comfortable: 50 m - HoldShort, whatever the speed.
        var control = new WayOnControl { LookAhead = (p, pose) => new WayOnJob("ahead", p, Holding) };
        control.Ahead(0f, 0.02f, 1, alongOf, 12f, 10f, At15);
        Assert.Equal(48f, control.HoldAlong);
        // Standing short of it: held; after HoldSeconds with no way on, the drive ends.
        var t = 0f;
        WayOnControl.Result r = default;
        for (; t < WayOnControl.HoldSeconds + 2f && r.Act != WayOnControl.Act.End; t += 0.02f) r = control.Ahead(t, 0.02f, 4, alongOf, 47.5f, 0f, At15);
        Assert.Equal(WayOnControl.Act.End, r.Act);
        Assert.StartsWith("blocked ahead", r.Why);
        // Too late: 15 m/s with 3 m to go needs 37 m/s²; the hold stays at 48 m (not moved on past the step), and
        // once the car stands, the step is given up as blocked at its position.
        var late = new WayOnControl { LookAhead = (p, pose) => new WayOnJob("ahead", p, Holding) };
        late.Ahead(0f, 0.02f, 4, alongOf, 45f, 15f, At15);
        Assert.Equal(48f, late.HoldAlong);
        Assert.Equal(WayOnControl.Act.Drive, late.Ahead(0.02f, 0.02f, 4, alongOf, 50f, 6f, At15).Act);
        var given = late.Ahead(0.04f, 0.02f, 5, alongOf, 51f, 0f, At15);
        Assert.Equal(WayOnControl.Act.Block, given.Act);
        Assert.Equal(5, given.Position);
        Assert.Null(late.HoldAlong);
    }

    [Fact]
    public void R6AWayOnIsTakenOnceAndNothingOfAJobOutlivesARestart()
    {
        var plan = DrivePlan.Fixed(new List<int> { 0, 1, 2 }, null, 50f);
        var taken = 0;
        IEnumerable<object?> Finding(WayOnJob job)
        {
            job.HoldPosition = 5;
            yield return null;
            job.Found = (plan, new List<int> { 0, 1, 2 }, "a way on");
        }
        var alongOf = Enumerable.Range(0, 13).Select(k => k * 10f).ToArray();
        var now = 0.0;
        var control = new WayOnControl
        {
            BudgetMs = 1,
            LookAhead = (p, pose) => new WayOnJob("ahead", p, job => Steps(job), () => now += 0.6),
            Take = job => { taken++; return job.Found?.Plan; },
        };
        IEnumerable<object?> Steps(WayOnJob job) => Finding(job);
        // The first step holds; a restart of the drive meanwhile (a blocked step's way on) drops the job.
        control.Ahead(0f, 0.02f, 1, alongOf, 12f, 8f, At15);
        Assert.NotNull(control.Job);
        Assert.Equal(48f, control.HoldAlong);
        control.Reset();
        Assert.Null(control.Job);
        Assert.Null(control.HoldAlong);
        Assert.Equal(0, taken);
        // A look that finds a way on: taken once, the hold gone with it.
        WayOnControl.Result r = default;
        for (var t = 2f; t < 3f && r.Act != WayOnControl.Act.Restart; t += 0.02f) r = control.Ahead(t, 0.02f, 1, alongOf, 12f, 8f, At15);
        Assert.Equal(WayOnControl.Act.Restart, r.Act);
        Assert.Same(plan, r.Plan);
        Assert.Equal(1, taken);
        Assert.Null(control.HoldAlong);
        Assert.Null(control.Job);
    }

    [Fact]
    public void R6ABlockedStepsWayOnIsPlannedWhileTheCarWaits()
    {
        var w = new World();
        var route = w.Lane.ToList();
        var planner = PlannerFor(w);
        var control = new WayOnControl
        {
            BudgetMs = 0.1,
            Replan = (p, what, pose) => planner.RoundBlock(route, route, p, what, pose, NoGoals, 0),
            Take = job => job.Found?.Plan,
        };
        // Blocked on the step 10 -> 20 m: that step is given up for the ride, and no way on joins the car standing at
        // 15 m (every route along the one lane runs through it, or starts ahead of the car).
        WayOnControl.Result r;
        var waits = 0;
        while ((r = control.Blocked(1, "a parked van", At15)).Act == WayOnControl.Act.Wait) waits++;
        Assert.True(waits > 0);
        Assert.Equal(WayOnControl.Act.End, r.Act);
        Assert.Equal(BlockCause.OnTheWay, w.Blocks[(w.Lane[1], w.Lane[2])]);
        Assert.Contains(w.Notes, n => n.Contains("no way on joins the car"));
    }

    [Fact]
    public void R4ASimulationIsSlicedWithinItsBudget()
    {
        // The turn rechecked by the look-ahead: each look at the world costs 0.25 ms on the jobs' clock, each ground
        // ray 0.02 ms. A 1 ms budget is overrun by at most one piece of work, not by a whole simulation.
        var clock = 0.0;
        var (graph, entry, exit) = TurnTestGraph.Turn();
        var check = new Feasibility(graph, Small(), _ => { clock += 0.25; return null; }) { GroundAt = _ => { clock += 0.02; return 0f; } };
        var job = new WayOnJob("ahead", 0, j => check.StepWork(entry, exit), () => clock);
        var whole = 0.0;
        while (!job.Step(1.0)) { }
        whole = job.Milliseconds;
        Assert.True(check.Step(entry, exit).Feasible);
        Assert.True(whole > 10.0, $"the check cost {whole:0.0} ms");
        Assert.True(job.Slices > 10, $"{job.Slices} slices");
        Assert.True(job.LongestSlice <= 1.0 + 0.25 + 8 * 0.02 + 0.01, $"longest slice {job.LongestSlice:0.00} ms");
    }

    [Fact]
    public void R5TheRollIsMeasuredWhereTheWheelsAre()
    {
        var w = new World();
        // The rear axle 0.9 m left of the lane's middle: the real left wheels at 1.65 m, the margin-wider ones at 2.15.
        var pose = new Pose(-0.9f, 0f, 20f, 0f);
        var nearKerb = new Feasibility(w.Graph, Small()) { GroundAt = p => p.X < -2.0f ? 0.15f : 0f };
        Assert.Null(nearKerb.Leaning(pose));
        var onKerb = new Feasibility(w.Graph, Small()) { GroundAt = p => p.X < -1.6f ? 0.15f : 0f };
        Assert.Contains("left wheels", onKerb.Leaning(pose) ?? "");
    }

    [Fact]
    public void R7ThePlannersHoldGoesIntoTheTrafficSnapshot()
    {
        var snapshot = new TrafficSnapshot { PlannerHoldAlong = 48f };
        Assert.Equal(48f, snapshot.PlannerHoldAlong);
    }
}
