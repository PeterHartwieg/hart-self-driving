using System.Collections.Generic;
using DrivenRides.Core.Drive;
using Xunit;

namespace DrivenRides.Core.Tests;

public sealed class JunctionRulesTests
{
    private static PathMark Mark(int position, float along, bool stop = false, bool giveWay = false, bool enter = false, bool exit = false, int junction = -1, int road = -1, bool? free = null) =>
        new() { Position = position, Waypoint = position, Along = along, Stop = stop, GiveWay = giveWay, Enter = enter, Exit = exit, Junction = junction, Road = road, Free = free };

    private static TrafficDecision Step(JunctionRules rules, List<PathMark> marks, float front, float rear, float speed, float time)
    {
        var d = new TrafficDecision();
        rules.Step(marks, front, rear, speed, time, d);
        return d;
    }

    [Fact]
    public void StopsShortOfARedAndDrivesThroughOneSeenTooLate()
    {
        var rules = new JunctionRules();
        var marks = new List<PathMark> { Mark(3, 50f, stop: true, enter: true, junction: 0) };
        var d = Step(rules, marks, 20f, 15f, 10f, 0f);
        Assert.Equal(50f - JunctionRules.ShortOfLine, d.StopAlong);
        Assert.True(d.StopIsRed);
        // 14 m/s with 8 m to go needs 12 m/s²: driven through, as on amber, and not stopped for again.
        var late = new JunctionRules();
        Assert.Null(Step(late, marks, 42f, 37f, 14f, 0f).StopAlong);
        Assert.Null(Step(late, marks, 44f, 39f, 13f, 0.1f).StopAlong);
    }

    [Fact]
    public void GiveWayIsAStopLineUntilGleySaysFree()
    {
        var rules = new JunctionRules();
        var far = Mark(5, 40f, giveWay: true, enter: true, junction: 2, road: 1);
        // Far off: not asked yet, so the car slows for the line.
        Assert.False(rules.ShouldAsk(far, 20f));
        Assert.Equal(40f - JunctionRules.ShortOfLine, Step(rules, new() { far }, 20f, 15f, 8f, 0f).StopAlong);
        // Close: asked; not free holds it, free lets it go and holds the road for others' corridors.
        Assert.True(rules.ShouldAsk(far, 36f));
        var no = far;
        no.Free = false;
        Assert.NotNull(Step(rules, new() { no }, 36f, 31f, 2f, 1f).StopAlong);
        Assert.Equal(-1, rules.RoadHeld(2));
        var yes = far;
        yes.Free = true;
        Assert.Null(Step(rules, new() { yes }, 36f, 31f, 2f, 2f).StopAlong);
        Assert.Equal(1, rules.RoadHeld(2));
    }

    [Fact]
    public void CountedInAJunctionFromItsEnterToItsExitAt20SAtMost()
    {
        var rules = new JunctionRules();
        var marks = new List<PathMark> { Mark(2, 30f, stop: false, enter: true, junction: 7, road: 0), Mark(6, 50f, exit: true, junction: 7) };
        Assert.Empty(Step(rules, marks, 29f, 24f, 5f, 0f).Events);
        var entered = Step(rules, marks, 30.5f, 25.5f, 5f, 1f).Events;
        Assert.Equal(new[] { new JunctionEvent { Kind = JunctionEventKind.Enter, Junction = 7 } }, entered);
        // Once only, however long the front is past the enter waypoint.
        Assert.Empty(Step(rules, marks, 40f, 35f, 5f, 2f).Events);
        // Left when the rear passes the exit, not the front.
        Assert.Empty(Step(rules, marks, 52f, 47f, 5f, 3f).Events);
        Assert.Equal(JunctionEventKind.Leave, Assert.Single(Step(rules, marks, 56f, 51f, 5f, 4f).Events).Kind);

        // Never more than InsideLimit seconds, and every one left on LeaveAll.
        var stuck = new JunctionRules();
        Step(stuck, marks, 31f, 26f, 1f, 0f);
        Assert.Empty(Step(stuck, marks, 32f, 27f, 1f, 19f).Events);
        Assert.Single(Step(stuck, marks, 32f, 27f, 1f, 20.5f).Events);
        var again = new JunctionRules();
        Step(again, marks, 31f, 26f, 1f, 0f);
        var events = new List<JunctionEvent>();
        again.LeaveAll(events);
        again.LeaveAll(events);
        Assert.Single(events);
    }
}
