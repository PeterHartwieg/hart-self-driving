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
}
