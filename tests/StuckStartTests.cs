using System.Linq;
using DrivenRides.Core.Routing;
using Xunit;

namespace DrivenRides.Core.Tests;

// 0.1.90: a taxi stood on a lane's end (no successor) beside a lane whose next waypoints are a junction's; the search
// allowed no lane change into a junction, and the ride went vanilla with every goal reachable once it did.
public class StuckStartTests
{
    [Fact]
    public void ACarAtALanesEndPullsIntoTheLaneBesideThoughAJunctionIsNext()
    {
        var b = new TurnTestGraph();
        int e0 = b.Add(0, 0), e1 = b.Add(0, 10, true), e2 = b.Add(0, 20, true), e3 = b.Add(0, 30), e4 = b.Add(0, 40);
        b.Link(e0, e1, e2, e3, e4);
        var pocket = b.Add(-4, 0);
        b.Beside(pocket, e0);
        var graph = b.Build();
        var job = RoutePlanner.Plan(graph, new RouteRequest { Start = pocket, Goals = new[] { new Goal(e4, 0) } }, int.MaxValue);
        Assert.Equal(PlanStatus.Found, job.Status);
        Assert.Equal(pocket, job.Result!.Waypoints[0]);
        Assert.Equal(e4, job.Result.Waypoints.Last());
        // A car that can drive on along its own lane is held to the old rule: no change into the junction.
        var c = new TurnTestGraph();
        int f0 = c.Add(0, 0), f1 = c.Add(0, 10, true), f2 = c.Add(0, 20, true), f3 = c.Add(0, 30);
        c.Link(f0, f1, f2, f3);
        int g0 = c.Add(-4, 0), g1 = c.Add(-4, 5);
        c.Link(g0, g1);
        c.Beside(g0, f0);
        Assert.NotEqual(PlanStatus.Found, RoutePlanner.Plan(c.Build(), new RouteRequest { Start = g0, Goals = new[] { new Goal(f3, 0) } }, int.MaxValue).Status);
    }
}
