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

    // Review round 2 (U2): judged on the velocities before the impact and the contact's normal (either sign).
    [Fact]
    public void RearEndedWhileStandingIsTheOtherCars()
    {
        // The ride car stands at the origin facing north; a car comes up behind it at 6 m/s and hits its rear.
        Assert.False(TrafficVerdict.CausedByRide(0f, 0f, 0f, 0f, 0f, 6f, 0f, -4.6f, 0f, 1f));
    }

    [Fact]
    public void DrivingIntoACarIsTheRideCarsEvenIfTheImpactStoppedIt()
    {
        // Before the impact the ride car went north at 5 m/s into a car standing across its way.
        Assert.True(TrafficVerdict.CausedByRide(0f, 5f, 0f, 0f, 0f, 0f, 0f, 4f, 0f, -1f));
    }

    [Fact]
    public void BothComingAtThePointIsTheRideCars()
    {
        Assert.True(TrafficVerdict.CausedByRide(0f, 3f, 0f, 0f, -4f, 0f, 3f, 3f, 0.7f, 0.7f));
    }

    [Fact]
    public void CutInOnFromTheSideIsTheOtherCars()
    {
        // The ride car goes north at 4 m/s; a car beside it swerves into its flank.
        Assert.False(TrafficVerdict.CausedByRide(0f, 4f, 0f, 0f, -2f, 4f, 2.2f, 0.5f, -1f, 0f));
    }
}
