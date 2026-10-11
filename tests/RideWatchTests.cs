using System;
using DrivenRides.Core.Ride;
using Xunit;

namespace DrivenRides.Core.Tests;

public sealed class RideWatchTests
{
    [Fact]
    public void RestartAtFifteenSecondsAtMostOncePerWindow()
    {
        var watch = new RideWatch(100, 1000);
        Assert.Equal(WatchVerdict.Ok, watch.Update(114.999, 1.9));
        Assert.Equal(WatchVerdict.Restart, watch.Update(115, 1.9));
        Assert.Contains("restart", watch.LastReason);
        Assert.Equal(WatchVerdict.Ok, watch.Update(115, 1.9));
        watch.Restarted(116);
        Assert.Equal(WatchVerdict.Ok, watch.Update(129, 3.8));
        Assert.Equal(WatchVerdict.Replan, watch.Update(130, 3.8));
    }

    [Fact]
    public void TwoMetreStepsResetRestartButFiveMetreStepsResetReplan()
    {
        var watch = new RideWatch(0, 1000);
        Assert.Equal(WatchVerdict.Ok, watch.Update(14, 2));
        Assert.Equal(WatchVerdict.Ok, watch.Update(28, 4));
        Assert.Equal(WatchVerdict.Replan, watch.Update(30, 4));
        watch.Replanned(30);
        Assert.Equal(WatchVerdict.Ok, watch.Update(40, 9));
        Assert.Equal(WatchVerdict.Ok, watch.Update(54.999, 9));
        Assert.Equal(WatchVerdict.Restart, watch.Update(55, 9));
        Assert.Equal(WatchVerdict.Replan, watch.Update(70, 9));
    }

    [Fact]
    public void RestartAcknowledgementResetsProgressBaselineWithoutDelayingReplan()
    {
        var watch = new RideWatch(0, 1000);
        Assert.Equal(WatchVerdict.Restart, watch.Update(15, 1.9));
        watch.Restarted(15);
        Assert.Equal(WatchVerdict.Ok, watch.Update(16, 2));
        Assert.Equal(WatchVerdict.Ok, watch.Update(29.999, 2));
        Assert.Equal(WatchVerdict.Replan, watch.Update(30, 2));
    }

    [Fact]
    public void FailedReplanGivesUpAtThirtySecondsAndStaysTerminal()
    {
        var watch = new RideWatch(0, 1000);
        Assert.Equal(WatchVerdict.Replan, watch.Update(30, 0));
        watch.Replanned(30);
        Assert.Equal(WatchVerdict.Ok, watch.Update(31, 4.9));
        Assert.Equal(WatchVerdict.Restart, watch.Update(46, 4.9));
        watch.Restarted(46);
        Assert.Equal(WatchVerdict.Ok, watch.Update(59.999, 4.9));
        Assert.Equal(WatchVerdict.GiveUp, watch.Update(60, 4.9));
        Assert.Contains("replan", watch.LastReason);
        Assert.Equal(WatchVerdict.GiveUp, watch.Update(61, 100));
    }

    [Fact]
    public void FiveMetresAfterReplanClearsFailedReplanState()
    {
        var watch = new RideWatch(0, 1000);
        watch.Update(30, 4);
        watch.Replanned(30);
        Assert.Equal(WatchVerdict.Ok, watch.Update(59, 9));
        Assert.Equal(WatchVerdict.Ok, watch.Update(60, 9));
        Assert.Equal(WatchVerdict.Replan, watch.Update(89, 9));
    }

    [Fact]
    public void ReplanProgressAtBoundarySucceedsButLateProgressCannotExtendHardLimit()
    {
        var onTime = new RideWatch(0, 1000);
        onTime.Update(30, 0);
        onTime.Replanned(30);
        Assert.Equal(WatchVerdict.Ok, onTime.Update(60, 5));
        var late = new RideWatch(0, 1000);
        late.Update(30, 0);
        late.Replanned(30);
        Assert.Equal(WatchVerdict.GiveUp, late.Update(60.001, 5));
    }

    [Fact]
    public void DeadlineWinsRegardlessOfProgressAndIsStrictlyGreater()
    {
        var watch = new RideWatch(100, 10);
        Assert.Equal(WatchVerdict.Ok, watch.Update(190, 100));
        Assert.Equal(WatchVerdict.GiveUp, watch.Update(190.001, 200));
        Assert.Contains("deadline", watch.LastReason);
    }

    [Fact]
    public void CustomLimitsAreCapturedAndElapsedUsesProvidedPauseExcludedTime()
    {
        var limits = new WatchLimits { RestartAfterSeconds = 2, ReplanAfterSeconds = 4, GiveUpAfterReplanSeconds = 3 };
        var watch = new RideWatch(0, 1000, limits);
        limits.RestartAfterSeconds = 100;
        Assert.Equal(WatchVerdict.Ok, watch.Update(1, 0));
        Assert.Equal(WatchVerdict.Ok, watch.Update(1, 0));
        Assert.Equal(WatchVerdict.Restart, watch.Update(2, 0));
        watch.Restarted(2);
        Assert.Equal(WatchVerdict.Replan, watch.Update(4, 0));
        watch.Replanned(4);
        Assert.Equal(WatchVerdict.GiveUp, watch.Update(7, 0));
    }

    [Fact]
    public void InvalidLimitsAndReversedTimeAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RideWatch(0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RideWatch(0, 1, new WatchLimits { RestartProgressMetres = 0 }));
        var watch = new RideWatch(10, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => watch.Update(9, 0));
    }
}
