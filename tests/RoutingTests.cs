using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using DrivenRides.Core.Routing;
using Xunit;
using Xunit.Abstractions;

namespace DrivenRides.Core.Tests;

public sealed class RoutingTests
{
    private readonly ITestOutputHelper output;
    public RoutingTests(ITestOutputHelper output) => this.output = output;

    internal static LaneGraph Graph(Vec3[] points, int[][] edges, float[]? speeds = null, bool[]? intersections = null, int[]? masks = null, float cellSize = 50)
        => new(points, edges, masks ?? Enumerable.Repeat(1, points.Length).ToArray(),
            intersections ?? new bool[points.Length], speeds ?? Enumerable.Repeat(36f, points.Length).ToArray(), cellSize);

    private static readonly Vec3[] ForkPoints = { new(0, 0, 0), new(10, 0, 0), new(10, 0, 10), new(20, 0, 0) };
    private static readonly int[][] ForkEdges = { new[] { 1, 2 }, new[] { 3 }, new[] { 3 }, Array.Empty<int>() };
    private static RouteRequest Request(int start, params Goal[] goals) => new() { Start = start, Goals = goals };
    private static Route Found(LaneGraph graph, RouteRequest request)
    {
        var job = RoutePlanner.Plan(graph, request, int.MaxValue);
        Assert.Equal(PlanStatus.Found, job.Status);
        return Assert.IsType<Route>(job.Result);
    }

    [Fact]
    public void VectorDistancesAndOperators()
    {
        var a = new Vec3(1, 2, 3);
        var b = new Vec3(4, 6, 3);
        Assert.Equal(5, Vec3.Distance(a, b));
        Assert.Equal(3, Vec3.HorizontalDistance(a, b));
        Assert.Equal(new Vec3(3, 4, 0), b - a);
        Assert.Equal(new Vec3(5, 8, 6), a + b);
        Assert.Equal(new Vec3(2, 4, 6), a * 2);
        Assert.Equal(a * 2, 2 * a);
    }

    [Fact]
    public void GraphCleansEdgesBuildsIncomingAndCopiesInputs()
    {
        var points = (Vec3[])ForkPoints.Clone();
        int[][] edges = { new[] { -1, 4, 0, 1, 1, 2 }, new[] { 3 }, new[] { 3 }, new[] { 3 } };
        int[] masks = { 1, 1, int.MinValue, 0 };
        bool[] intersections = { false, true, false, false };
        float[] speeds = { 0, -10, 72, 36 };
        var graph = Graph(points, edges, speeds, intersections, masks);
        points[0] = new Vec3(999, 0, 0);
        edges[0][3] = 3;
        masks[0] = 0;
        intersections[1] = false;
        speeds[2] = 1;
        Assert.Equal(2, graph.DroppedEdges);
        Assert.Equal(new[] { 1, 2 }, graph.Successors(0));
        Assert.Equal(new[] { 1, 2 }, graph.Predecessors(3));
        Assert.Empty(graph.Successors(3));
        Assert.Equal(ForkPoints[0], graph.Position(0));
        Assert.True(graph.IsIntersection(1));
        Assert.Equal(LaneGraph.DefaultSpeedMs, graph.SpeedMs(0));
        Assert.Equal(LaneGraph.DefaultSpeedMs, graph.SpeedMs(1));
        Assert.Equal(20, graph.TopSpeedMs);
        Assert.True(graph.Allows(0, 0));
        Assert.True(graph.Allows(2, 31));
        Assert.False(graph.Allows(2, 32));
        Assert.False(graph.Allows(0, -1));
    }

    [Fact]
    public void GraphValidatesArrayLengthsCellsAndEmptyGraph()
    {
        Assert.Throws<ArgumentException>(() => new LaneGraph(new Vec3[1], Array.Empty<int[]>(), new int[1], new bool[1], new float[1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => Graph(ForkPoints, ForkEdges, cellSize: 0));
        Assert.Throws<ArgumentException>(() => Graph(ForkPoints, new int[][] { null!, new[] { 3 }, new[] { 3 }, Array.Empty<int>() }));
        var empty = Graph(Array.Empty<Vec3>(), Array.Empty<int[]>());
        Assert.Equal(0, empty.Count);
        Assert.Equal(LaneGraph.DefaultSpeedMs, empty.TopSpeedMs);
        Assert.Equal(-1, empty.Nearest(default, 10));
    }

    [Fact]
    public void GridQueriesUseHorizontalRadiusIncludingCellBoundariesAndNegativeCoordinates()
    {
        Vec3[] points = { new(-50, 100, -50), new(-49, 0, -50), new(0, 0, 0), new(50, 0, 0) };
        var graph = Graph(points, Enumerable.Range(0, 4).Select(_ => Array.Empty<int>()).ToArray());
        Assert.Equal(new[] { 0, 1 }, graph.Near(new Vec3(-50, 0, -50), 1).OrderBy(i => i));
        Assert.Equal(new[] { 2, 3 }, graph.Near(default, 50).OrderBy(i => i));
        Assert.Equal(0, graph.Nearest(new Vec3(-50, 0, -50), 0));
        Assert.Equal(1, graph.Nearest(new Vec3(-50, 0, -50), 2, i => i != 0));
        Assert.Equal(-1, graph.Nearest(new Vec3(100, 0, 0), 10));
        Assert.Equal(4, graph.Near(default, 1_000_000).Count());
        Assert.Throws<ArgumentOutOfRangeException>(() => graph.Near(default, -1).ToList());
    }

    [Fact]
    public void PlannerChoosesShortestAtEqualSpeedsAndFasterWhenSpeedsDiffer()
    {
        var request = Request(0, new Goal(3, 0));
        var shortest = Found(Graph(ForkPoints, ForkEdges), request);
        Assert.Equal(new[] { 0, 1, 3 }, shortest.Waypoints);
        Assert.Equal(20, shortest.LengthMetres);
        Assert.Equal(2, shortest.DriveSeconds, 5);
        var faster = Found(Graph(ForkPoints, ForkEdges, new float[] { 36, 3.6f, 108, 36 }), request);
        Assert.Equal(new[] { 0, 2, 3 }, faster.Waypoints);
        Assert.True(faster.LengthMetres > shortest.LengthMetres);
        Assert.Equal((float)(2 * Math.Sqrt(200) / 20), faster.DriveSeconds, 5);
    }

    [Fact]
    public void IntersectionPenaltyOnlyAppliesOnEntryFromOutside()
    {
        var graph = Graph(ForkPoints, ForkEdges, intersections: new[] { false, true, false, false });
        var request = Request(0, new Goal(3, 0));
        request.IntersectionPenaltySeconds = 0;
        Assert.Equal(new[] { 0, 1, 3 }, Found(graph, request).Waypoints);
        request.IntersectionPenaltySeconds = 4;
        Assert.Equal(new[] { 0, 2, 3 }, Found(graph, request).Waypoints);
        Vec3[] chain = { new(0, 0, 0), new(10, 0, 0), new(20, 0, 0), new(30, 0, 0) };
        var through = Graph(chain, new[] { new[] { 1 }, new[] { 2 }, new[] { 3 }, Array.Empty<int>() }, intersections: new[] { false, true, true, false });
        Assert.Equal(7, Found(through, Request(0, new Goal(3, 0))).DriveSeconds, 5);
    }

    [Fact]
    public void DisabledAndVehicleFiltersDetourAndExemptStart()
    {
        var request = Request(0, new Goal(3, 0));
        var checkedNodes = new List<int>();
        request.IsDisabled = i => { checkedNodes.Add(i); return i == 0 || i == 1; };
        var graph = Graph(ForkPoints, ForkEdges, masks: new[] { 0, 1, 1, 1 });
        Assert.Equal(new[] { 0, 2, 3 }, Found(graph, request).Waypoints);
        Assert.DoesNotContain(0, checkedNodes);
        request.IsDisabled = null;
        graph = Graph(ForkPoints, ForkEdges, masks: new[] { 0, 2, 1, 3 });
        Assert.Equal(new[] { 0, 2, 3 }, Found(graph, request).Waypoints);
        request.VehicleType = 1;
        Assert.Equal(new[] { 0, 1, 3 }, Found(graph, request).Waypoints);
    }

    [Fact]
    public void DisallowedWaypointsAreAPenaltyNotAWall()
    {
        // Only a lane not allowed for the vehicle leads on: Gley would drive it, so the planner must too.
        Vec3[] chain = { new(0, 0, 0), new(10, 0, 0), new(20, 0, 0) };
        var graph = Graph(chain, new[] { new[] { 1 }, new[] { 2 }, Array.Empty<int>() }, masks: new[] { 1, 2, 1 });
        var request = Request(0, new Goal(2, 0));
        var route = Found(graph, request);
        Assert.Equal(new[] { 0, 1, 2 }, route.Waypoints);
        Assert.True(route.DriveSeconds >= 60);
        request.DisallowedPenaltySeconds = float.PositiveInfinity;
        Assert.Equal(PlanStatus.NoPath, RoutePlanner.Plan(graph, request, int.MaxValue).Status);
    }

    [Fact]
    public void LiveDisableCallbackIsReadDuringLaterSteps()
    {
        Vec3[] points = { new(0, 0, 0), new(10, 0, 0), new(20, 0, 0) };
        var graph = Graph(points, new[] { new[] { 1 }, new[] { 2 }, Array.Empty<int>() });
        bool disabled = false;
        var request = Request(0, new Goal(2, 0));
        request.IsDisabled = i => disabled && i == 2;
        var job = RoutePlanner.Plan(graph, request, 1);
        Assert.Equal(PlanStatus.Running, job.Status);
        disabled = true;
        Assert.Equal(PlanStatus.NoPath, job.Step(10));
    }

    [Fact]
    public void DirectedLanesAndBlockedGoalReturnNoPath()
    {
        var graph = Graph(ForkPoints, ForkEdges);
        var job = RoutePlanner.Plan(graph, Request(3, new Goal(0, 0)), 100);
        Assert.Equal(PlanStatus.NoPath, job.Status);
        Assert.Null(job.Result);
        var request = Request(0, new Goal(3, 0));
        request.IsDisabled = i => i == 3;
        Assert.Equal(PlanStatus.NoPath, RoutePlanner.Plan(graph, request, 100).Status);
    }

    [Fact]
    public void VirtualTerminalMinimizesDrivePlusWalkAndDeduplicatesGoals()
    {
        Vec3[] points = { new(0, 0, 0), new(10, 0, 0), new(100, 0, 0) };
        var graph = Graph(points, new[] { new[] { 1, 2 }, Array.Empty<int>(), Array.Empty<int>() });
        var route = Found(graph, Request(0, new Goal(1, 20), new Goal(2, 3), new Goal(2, 0), new Goal(99, 0)));
        Assert.Equal(new[] { 0, 2 }, route.Waypoints);
        Assert.Equal(2, route.GoalIndex);
        Assert.Equal(10, route.DriveSeconds);
        Assert.Equal(0, route.WalkSeconds);
        var startGoal = Found(graph, Request(0, new Goal(0, 100), new Goal(1, 0)));
        Assert.Equal(1, startGoal.GoalIndex);
        var justStart = Found(graph, Request(0, new Goal(0, 2)));
        Assert.Equal(new[] { 0 }, justStart.Waypoints);
        Assert.Equal(0, justStart.DriveSeconds);
        Assert.Equal(0, justStart.LengthMetres);
        Assert.Equal(2, justStart.WalkSeconds);
    }

    [Fact]
    public void StartGoalIsAllowedDespiteVehicleAndDisabledFilters()
    {
        var graph = Graph(new[] { new Vec3(0, 0, 0) }, new[] { Array.Empty<int>() }, masks: new[] { 0 });
        var request = Request(0, new Goal(0, 0));
        request.IsDisabled = _ => throw new InvalidOperationException("Start must not be checked");
        Assert.Equal(new[] { 0 }, Found(graph, request).Waypoints);
    }

    [Fact]
    public void InvalidRequestsAndDiscardedGoalIndices()
    {
        var graph = Graph(ForkPoints, ForkEdges);
        Assert.Equal(PlanStatus.InvalidRequest, RoutePlanner.Begin(graph, Request(-1, new Goal(3, 0))).Status);
        Assert.Equal(PlanStatus.InvalidRequest, RoutePlanner.Begin(graph, Request(4, new Goal(3, 0))).Status);
        Assert.Equal(PlanStatus.InvalidRequest, RoutePlanner.Begin(graph, Request(0)).Status);
        Assert.Equal(PlanStatus.InvalidRequest, RoutePlanner.Begin(graph, Request(0, new Goal(-1, 0), new Goal(4, 0))).Status);
        Assert.Equal(1, Found(graph, Request(0, new Goal(-1, 0), new Goal(3, 0))).GoalIndex);
        Assert.Equal(PlanStatus.InvalidRequest, RoutePlanner.Begin(graph, Request(0, new Goal(3, -1))).Status);
        var invalidPenalty = Request(0, new Goal(3, 0));
        invalidPenalty.IntersectionPenaltySeconds = float.NaN;
        Assert.Equal(PlanStatus.InvalidRequest, RoutePlanner.Begin(graph, invalidPenalty).Status);
    }

    [Fact]
    public void SteppingMatchesOneShotAndPreservesExpansionBudget()
    {
        var graph = Grid(20, 20, 7);
        var request = Request(0, new Goal(399, 1), new Goal(380, 30));
        var whole = RoutePlanner.Plan(graph, request, int.MaxValue);
        var stepped = RoutePlanner.Begin(graph, request);
        Assert.Equal(PlanStatus.Running, stepped.Step(0));
        Assert.Equal(0, stepped.Expanded);
        while (stepped.Status == PlanStatus.Running)
        {
            int before = stepped.Expanded;
            stepped.Step(3);
            Assert.InRange(stepped.Expanded - before, 0, 3);
        }
        Assert.Equal(PlanStatus.Found, stepped.Status);
        Assert.Equal(whole.Result!.Waypoints, stepped.Result!.Waypoints);
        Assert.Equal(whole.Result.GoalIndex, stepped.Result.GoalIndex);
        Assert.Equal(whole.Result.DriveSeconds, stepped.Result.DriveSeconds);
        Assert.Equal(whole.Expanded, stepped.Expanded);
        Assert.Equal(PlanStatus.Found, stepped.Step(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => stepped.Step(-1));
    }

    [Fact]
    public void PlannerMatchesIndependentDijkstraOnRandomDirectedStreets()
    {
        for (int seed = 0; seed < 12; seed++)
        {
            var graph = Grid(10, 10, seed);
            var request = Request(seed, new Goal(99, 0), new Goal(89, 4), new Goal(90, 8));
            var route = Found(graph, request);
            double expected = Dijkstra(graph, request);
            Assert.InRange(Math.Abs(route.DriveSeconds + route.WalkSeconds - expected), 0, 0.0001);
        }
    }

    [Theory]
    [InlineData(100, 100, 9999)]
    [InlineData(316, 317, 31700)]
    public void LargeRandomGridPerformanceSmoke(int width, int height, int goal)
    {
        var graph = Grid(width, height, 42);
        var request = Request(0, new Goal(goal, 0));
        var watch = Stopwatch.StartNew();
        var route = Found(graph, request);
        watch.Stop();
        output.WriteLine($"{graph.Count:N0} waypoints, {route.LengthMetres:N0} m route: {watch.Elapsed.TotalMilliseconds:F2} ms");
        Assert.Equal(0, route.Waypoints[0]);
        Assert.Equal(goal, route.Waypoints[route.Waypoints.Count - 1]);
    }

    private static LaneGraph Grid(int width, int height, int seed)
    {
        int count = width * height;
        var random = new Random(seed);
        var positions = new Vec3[count];
        var edges = new int[count][];
        var speeds = new float[count];
        var intersections = new bool[count];
        for (int z = 0; z < height; z++)
            for (int x = 0; x < width; x++)
            {
                int i = z * width + x;
                positions[i] = new Vec3(x * 20, 0, z * 20);
                speeds[i] = 30 + random.Next(50);
                intersections[i] = x % 5 == 0 && z % 5 == 0;
                var next = new List<int>();
                if (x + 1 < width) next.Add(i + 1);
                if (z + 1 < height) next.Add(i + width);
                if (x > 0 && random.Next(2) == 0) next.Add(i - 1);
                if (z > 0 && random.Next(2) == 0) next.Add(i - width);
                edges[i] = next.ToArray();
            }
        return Graph(positions, edges, speeds, intersections);
    }

    private static double Dijkstra(LaneGraph graph, RouteRequest request)
    {
        var cost = Enumerable.Repeat(double.PositiveInfinity, graph.Count).ToArray();
        var queue = new PriorityQueue<int, double>();
        cost[request.Start] = 0;
        queue.Enqueue(request.Start, 0);
        while (queue.TryDequeue(out int i, out double current))
        {
            if (current != cost[i]) continue;
            foreach (int j in graph.Successors(i))
            {
                double next = current + Vec3.Distance(graph.Position(i), graph.Position(j)) /
                    (((double)graph.SpeedMs(i) + graph.SpeedMs(j)) / 2);
                if (!graph.IsIntersection(i) && graph.IsIntersection(j)) next += request.IntersectionPenaltySeconds;
                if (next >= cost[j]) continue;
                cost[j] = next;
                queue.Enqueue(j, next);
            }
        }
        return request.Goals.Min(goal => cost[goal.Waypoint] + goal.WalkSeconds);
    }

    [Fact]
    public void ShouldStopEndsAStepInsideTheSearchAndTheJobResumes()
    {
        var points = new Vec3[200];
        var edges = new int[200][];
        for (var i = 0; i < 200; i++) { points[i] = new Vec3(i * 10, 0, 0); edges[i] = i == 199 ? Array.Empty<int>() : new[] { i + 1 }; }
        var graph = Graph(points, edges);
        var job = RoutePlanner.Begin(graph, new RouteRequest { Start = 0, Goals = new[] { new Goal(199, 0) } });
        var asked = 0;
        Assert.Equal(PlanStatus.Running, job.Step(int.MaxValue, () => ++asked >= 1));
        Assert.Equal(64, job.Expanded);
        Assert.Equal(PlanStatus.Found, job.Step(int.MaxValue));
        Assert.Equal(200, job.Result!.Waypoints.Count);
    }

    [Fact]
    public void LaneChangeLeavesADeadEndLaneButCostsAPenalty()
    {
        // A pull-over lane (0 -> 1) ends; its only way on is a lane change to the road beside it (2 -> 3). The change
        // goes diagonally from 1 to 3, the waypoint after the one beside 1.
        Vec3[] points = { new(0, 0, 0), new(10, 0, 0), new(10, 0, 3), new(30, 0, 3) };
        int[][] edges = { new[] { 1 }, Array.Empty<int>(), new[] { 3 }, Array.Empty<int>() };
        int[][] lanes = { Array.Empty<int>(), new[] { 2 }, Array.Empty<int>(), Array.Empty<int>() };
        var graph = new LaneGraph(points, edges, new[] { 1, 1, 1, 1 }, new bool[4], new float[4], otherLanes: lanes);
        var route = Found(graph, Request(0, new Goal(3, 0)));
        Assert.Equal(new[] { 0, 1, 3 }, route.Waypoints);
        Assert.True(route.DriveSeconds >= 5);
        var request = Request(0, new Goal(3, 0));
        request.LaneChangePenaltySeconds = float.PositiveInfinity;
        Assert.Equal(PlanStatus.NoPath, RoutePlanner.Plan(graph, request, int.MaxValue).Status);
    }

    [Fact]
    public void ChangesLanesBeforeATurnInsteadOfDetouring()
    {
        // Two parallel lanes; only the left lane (10 -> 11) has the turn to the goal (12). The car starts in the right
        // lane (0 -> 1 -> 2), whose way on is a 1 km loop (2 -> 3 -> 12).
        var points = new Vec3[13];
        for (var i = 0; i < 3; i++) points[i] = new Vec3(i * 20, 0, 0);
        points[3] = new Vec3(500, 0, 500);
        points[10] = new Vec3(20, 0, 4);
        points[11] = new Vec3(40, 0, 4);
        points[12] = new Vec3(40, 0, 30);
        var edges = new int[13][];
        for (var i = 0; i < 13; i++) edges[i] = Array.Empty<int>();
        edges[0] = new[] { 1 }; edges[1] = new[] { 2 }; edges[2] = new[] { 3 }; edges[3] = new[] { 12 };
        edges[10] = new[] { 11 }; edges[11] = new[] { 12 };
        var lanes = new int[13][];
        for (var i = 0; i < 13; i++) lanes[i] = Array.Empty<int>();
        lanes[0] = new[] { 10 };
        var masks = Enumerable.Repeat(1, 13).ToArray();
        var graph = new LaneGraph(points, edges, masks, new bool[13], new float[13], otherLanes: lanes);
        Assert.Equal(new[] { 0, 11, 12 }, Found(graph, Request(0, new Goal(12, 0))).Waypoints);
    }

    [Fact]
    public void InfersParallelLanesWhereGleyHasNone()
    {
        // Two one-way lanes 3.5 m apart heading +z, plus an opposite lane 7 m away that must not count.
        var points = new List<Vec3>();
        var edges = new List<int[]>();
        void Lane(float x, int count, int dir)
        {
            var first = points.Count;
            for (var i = 0; i < count; i++)
            {
                points.Add(new Vec3(x, 0, dir > 0 ? i * 10 : (count - 1 - i) * 10));
                edges.Add(i == count - 1 ? Array.Empty<int>() : new[] { first + i + 1 });
            }
        }
        Lane(0, 5, 1); Lane(3.5f, 5, 1); Lane(-3.5f, 5, -1);
        var n = points.Count;
        var graph = new LaneGraph(points.ToArray(), edges.ToArray(), Enumerable.Repeat(1, n).ToArray(), new bool[n], new float[n], inferLanes: true);
        Assert.Contains(5 + 1, graph.Lateral(1));
        Assert.DoesNotContain(graph.Lateral(1), j => j >= 10);
        Assert.True(graph.SideOf(1, 6) > 0);
        Assert.True(graph.InferredLaneChanges > 0);
        var kerb = GoalPicker.Pick(graph, new Vec3(1.75f, 0, 20), 0, new GoalOptions { KerbLane = true, MinStraightMetres = 10 });
        // The inner +z lane (0..4) has a lane to its right; the outer +z lane and the opposite lane are both at a kerb.
        Assert.NotEmpty(kerb);
        Assert.DoesNotContain(kerb, goal => goal.Waypoint < 5);
        // A destination east of the street (x > 3.5): only the +z lanes have it on their right, so the kerb goal is
        // the outer +z lane, never the opposite lane across the street.
        var sameSide = GoalPicker.Pick(graph, new Vec3(8, 0, 20), 0, new GoalOptions { KerbLane = true, TargetOnRight = true, MinStraightMetres = 10 });
        Assert.NotEmpty(sameSide);
        Assert.All(sameSide, goal => Assert.InRange(goal.Waypoint, 5, 9));
    }

    [Fact]
    public void AddsAMissingRightTurnFromTheOutermostLaneOnly()
    {
        // A junction whose data only goes straight on. Northbound lanes at x = 2 (right) and x = -2 (left); an
        // eastbound street with lanes at z = -2 (right) and z = 2 (left) leaving the junction at x = 10.
        var points = new List<Vec3>();
        var edges = new List<int[]>();
        var junction = new List<bool>();
        int Add(float x, float z, bool inJunction) { points.Add(new Vec3(x, 0, z)); edges.Add(Array.Empty<int>()); junction.Add(inJunction); return points.Count - 1; }
        void Link(params int[] chain) { for (var k = 0; k + 1 < chain.Length; k++) edges[chain[k]] = new[] { chain[k + 1] }; }
        int[] North(float x) { var a = Add(x, -30, false); var b = Add(x, -20, false); var e = Add(x, -10, false); var j1 = Add(x, -5, true); var j2 = Add(x, 5, true); var o = Add(x, 10, false); var o2 = Add(x, 20, false); Link(a, b, e, j1, j2, o, o2); return new[] { a, e, o }; }
        int[] East(float z) { var w = Add(-20, z, false); var e = Add(-10, z, false); var j1 = Add(-5, z, true); var j2 = Add(5, z, true); var x1 = Add(10, z, false); var x2 = Add(20, z, false); Link(w, e, j1, j2, x1, x2); return new[] { w, x1, x2 }; }
        var right = North(2); var left = North(-2); var eastRight = East(-2); var eastLeft = East(2);
        var n = points.Count;
        LaneGraph Make(bool infer) => new LaneGraph(points.ToArray(), edges.ToArray(), Enumerable.Repeat(1, n).ToArray(), junction.ToArray(), new float[n], inferTurns: infer);
        var request = Request(right[0], new Goal(eastRight[2], 0));
        Assert.Equal(PlanStatus.NoPath, RoutePlanner.Plan(Make(false), request, int.MaxValue).Status);
        var graph = Make(true);
        Assert.Equal(new[] { eastRight[1] }, graph.MissingTurns(right[1]));
        Assert.Empty(graph.MissingTurns(left[1]));
        var route = Found(graph, request);
        Assert.Contains(eastRight[1], route.Waypoints);
        Assert.True(route.DriveSeconds >= 8);
    }
}
