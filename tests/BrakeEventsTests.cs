using DrivenRides.Core.Drive;
using Xunit;

namespace DrivenRides.Core.Tests;

public sealed class BrakeEventsTests
{
    // A brake for a car in a turn, 9 to 3 m/s and back: one line, with the reason given when it began and the wheel.
    [Fact]
    public void ASlowdownGivesOneLine()
    {
        var e = new BrakeEvents();
        string? line = null;
        var lines = 0;
        var note = new YieldNote { Kind = YieldNoteKind.Conflict, Pose = 4, Car = 7 };
        for (var i = 0; i <= 250; i++)
        {
            var t = i * 0.02f;
            var v = t < 1f ? 9f : t < 2f ? 9f - 6f * (t - 1f) : t < 3f ? 3f : 3f + 3f * (t - 3f);
            var why = t >= 1f && t < 2.5f ? "giving way to 7" : "";
            var l = e.Step(t, t * 8f, v, 30f, why, note, false);
            if (l != null) { line = l; lines++; }
        }
        Assert.Equal(1, lines);
        Assert.Contains("9.0 to 3.0 m/s", line!.Replace(',', '.'));
        Assert.Contains("giving way to 7", line);
        Assert.Contains("wheel up to 30", line);
    }
}
