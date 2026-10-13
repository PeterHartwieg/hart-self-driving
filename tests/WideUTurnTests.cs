using System.Collections.Generic;
using System.Linq;
using DrivenRides.Core.Routing;
using DrivenRides.Core.Testing;
using Xunit;

namespace DrivenRides.Core.Tests;

public sealed class WideUTurnTests
{
    // A divided street, two lanes each way: east along z = 0 (inner) and z = -4 (outer), west along z = 15 (inner) and
    // z = 19 (outer), a junction from x = 0 to 30. Lane changes between the eastbound lanes before the junction.
    private static (LaneGraph Graph, int InnerEntry, int OuterEntry, int[] Goals) Street()
    {
        var b = new TurnTestGraph();
        int[] East(float z)
        {
            var w = new List<int>();
            for (var x = -70; x <= -10; x += 10) w.Add(b.Add(x, z));
            w.Add(b.Add(-5, z));
            w.Add(b.Add(5, z, true));
            w.Add(b.Add(12, z, true));
            w.Add(b.Add(25, z, true));
            w.Add(b.Add(35, z));
            w.Add(b.Add(45, z));
            b.Link(w.ToArray());
            return w.ToArray();
        }
        int[] West(float z)
        {
            var w = new List<int> { b.Add(35, z), b.Add(20, z, true), b.Add(10, z, true), b.Add(0, z, true), b.Add(-10, z, true) };
            for (var x = -15; x >= -75; x -= 10) w.Add(b.Add(x, z));
            b.Link(w.ToArray());
            return w.ToArray();
        }
        var inner = East(0);
        var outer = East(-4);
        for (var k = 0; k < 6; k++) { b.Beside(inner[k], outer[k]); b.Beside(outer[k], inner[k]); }
        var farInner = West(15);
        var farOuter = West(19);
        return (b.Build(true), inner[7], outer[7], new[] { farInner[^1], farOuter[^1] });
    }

    [Fact]
    public void EveryApproachLaneTurnsIntoEveryFarLane()
    {
        var (graph, inner, outer, _) = Street();
        Assert.True(graph.UTurn(inner) >= 0);
        Assert.Equal(-1, graph.UTurn(outer));
        var group = graph.UTurnGroup(inner);
        Assert.Equal(4, group.Count);
        // Widest first: from the outer lane to the far outer lane, 8 m wider than the inner one.
        Assert.Equal(outer, group[0].Entry);
        Assert.InRange(group[0].Width - group.Single(p => p.Entry == inner && p.Target == graph.UTurn(inner)).Width, 7.5f, 8.5f);
        Assert.Equal(0f, graph.UTurnNarrower(group[0].Entry, group[0].Target));
        Assert.True(graph.IsWideUTurn(group[0].Entry, group[0].Target));
        Assert.False(graph.IsWideUTurn(inner, graph.UTurn(inner)));
        Assert.All(group, p => Assert.True(graph.IsUTurn(p.Entry, p.Target)));
        Assert.All(group.Where(p => p.Entry != inner || p.Target != graph.UTurn(inner)), p => Assert.NotEmpty(graph.TurnVias(p.Entry, p.Target)));
        // The catalogue keeps the inner one's id and names the wider ones by their target.
        var cases = Catalogue.Build(graph).Where(f => f.Kind == CaseKind.UTurn).Select(f => f.Id).ToList();
        Assert.Single(cases, id => id.StartsWith("uturn-" + inner + "-"));
        Assert.Contains("uturnw-" + group[0].Entry + "-" + group[0].Target, cases);
        Assert.Equal(cases.Count, cases.Distinct().Count());
    }

    [Fact]
    public void TheWidestSwingWinsWhenTheCarMayTakeIt()
    {
        var (graph, inner, outer, goals) = Street();
        var request = new RouteRequest { Start = 0, Goals = goals.Select(g => new Goal(g, 0)).ToArray() };
        var gley = RoutePlanner.Plan(graph, request, int.MaxValue).Result!;
        Assert.Contains(inner, gley.Waypoints);
        Assert.Contains(graph.UTurn(inner), gley.Waypoints);
        request.WideUTurns = true;
        var wide = RoutePlanner.Plan(graph, request, int.MaxValue).Result!;
        var widest = graph.UTurnGroup(inner)[0];
        var at = wide.Waypoints.ToList().IndexOf(widest.Entry);
        Assert.True(at >= 0 && wide.Waypoints[at + 1] == widest.Target, string.Join(" ", wide.Waypoints));
        // Blocked for this car (the feasibility check): the next widest it may take.
        request.IsBlockedStep = (a, b) => a == widest.Entry && b == widest.Target;
        var next = RoutePlanner.Plan(graph, request, int.MaxValue).Result!;
        var steps = next.Waypoints.Zip(next.Waypoints.Skip(1)).ToList();
        Assert.DoesNotContain((widest.Entry, widest.Target), steps);
        var uTurn = Assert.Single(steps, s => graph.IsUTurn(s.First, s.Second));
        Assert.True(graph.IsWideUTurn(uTurn.First, uTurn.Second));
    }

    [Fact]
    public void BlockedWiderPairsCostTheInnerOneNothing()
    {
        // All the wider pairs blocked for this car: the inner U-turn is the widest one open and costs what it costs
        // on the Gley path, so blocked wide pairs never push a route round the block.
        var (graph, inner, _, goals) = Street();
        var group = graph.UTurnGroup(inner);
        var request = new RouteRequest { Start = 0, Goals = goals.Select(g => new Goal(g, 0)).ToArray() };
        var gley = RoutePlanner.Plan(graph, request, int.MaxValue).Result!;
        request.WideUTurns = true;
        request.IsBlockedStep = (a, b) => graph.IsWideUTurn(a, b);
        var wide = RoutePlanner.Plan(graph, request, int.MaxValue).Result!;
        Assert.Equal(gley.Waypoints, wide.Waypoints);
        Assert.Equal(gley.DriveSeconds, wide.DriveSeconds, 3);
        // With only the widest blocked, the next widest open pair costs nothing extra either; a narrower one does.
        var widest = group[0];
        request.IsBlockedStep = (a, b) => a == widest.Entry && b == widest.Target;
        var next = RoutePlanner.Plan(graph, request, int.MaxValue).Result!;
        var uTurn = next.Waypoints.Zip(next.Waypoints.Skip(1)).Single(s => graph.IsUTurn(s.First, s.Second));
        Assert.Equal(group[1].Width, graph.UTurnWidth(uTurn.First, uTurn.Second), 3);
    }
}
