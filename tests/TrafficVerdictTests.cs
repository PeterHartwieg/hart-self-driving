using System;
using DrivenRides.Core.Testing;
using Xunit;

namespace DrivenRides.Core.Tests;

public sealed class TrafficVerdictTests
{
    private static JudgeResult Pass() => new() { Verdict = "pass" };

    [Fact]
    public void TrafficFailuresFailAPassingRun()
    {
        var caused = Pass();
        TrafficVerdict.Apply(caused, 1, 0, 0, 0);
        Assert.Equal("fail", caused.Verdict);
        Assert.Contains("traffic-contact", caused.Reasons);
        var red = Pass();
        TrafficVerdict.Apply(red, 0, 0, 1, 0);
        Assert.Equal("fail", red.Verdict);
        Assert.Contains("red-run", red.Reasons);
        var stood = Pass();
        TrafficVerdict.Apply(stood, 0, 0, 0, 1);
        Assert.Equal("fail", stood.Verdict);
        Assert.Contains("standstill", stood.Reasons);
    }

    [Fact]
    public void AContactAnotherCarCausedIsAnIncident()
    {
        var taken = Pass();
        TrafficVerdict.Apply(taken, 0, 2, 0, 0);
        Assert.Equal("pass", taken.Verdict);
        Assert.Contains("traffic-contact-taken x2", taken.Incidents);
        // Other verdicts (avoided, setup errors) stay as they are.
        var avoided = new JudgeResult { Verdict = "avoided" };
        TrafficVerdict.Apply(avoided, 1, 0, 1, 1);
        Assert.Equal("avoided", avoided.Verdict);
    }

    // Review round 9: only a car moving sideways into the ride car is the other car's; following and junctions count.
    [Fact]
    public void RearEndedWhileStandingCounts()
    {
        // The ride car stands at the origin facing north; a car comes up behind it at 6 m/s and hits its rear.
        Assert.True(TrafficVerdict.CausedByRide(0f, 0f, 0f, 0f, 0f, 0f, 6f, 0f, 0f, -4.6f));
    }

    [Fact]
    public void DrivingIntoACarIsTheRideCars()
    {
        Assert.True(TrafficVerdict.CausedByRide(0f, 5f, 0f, 0f, 0f, 0f, 0f, (float)(Math.PI / 2), 0f, 4f));
    }

    [Fact]
    public void ACrossingCarAtAJunctionCounts()
    {
        // Standing in a junction; a car comes from the right across the ride car's way into it.
        Assert.True(TrafficVerdict.CausedByRide(0f, 0f, 0f, 0f, 0f, -4f, 0f, -(float)(Math.PI / 2), 3f, 1f));
    }

    [Fact]
    public void AMergeIntoTheStandingRideCarIsTheOtherCars()
    {
        // Standing facing north; a car on its right, heading 10 degrees left of north at 4.5 m/s, comes into its flank.
        var h = -10f * (float)Math.PI / 180f;
        Assert.False(TrafficVerdict.CausedByRide(0f, 0f, 0f, 0f, 0f, 4.5f * (float)Math.Sin(h), 4.5f * (float)Math.Cos(h), h, 3.2f, 0.2f));
    }

    [Fact]
    public void SwervingIntoACarBesideIsTheRideCars()
    {
        // Going north, the ride car moves left into a car beside it going straight.
        Assert.True(TrafficVerdict.CausedByRide(-1f, 4f, -0.25f, 0f, 0f, 0f, 4f, 0f, -2.2f, 0.5f));
    }
}
