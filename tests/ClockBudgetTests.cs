using System;
using DrivenRides.Core.Ride;
using Xunit;

namespace DrivenRides.Core.Tests;

public sealed class ClockBudgetTests
{
    [Fact]
    public void ExpectedPaceIsOneThirdAndObservationUsesScaledSeconds()
    {
        var clock = new ClockBudget(100, 40, 1, 8.33, 10);
        Assert.Equal(100, clock.StartMinute);
        Assert.Equal(140, clock.DeadlineMinute);
        Assert.InRange(clock.Pace(100, 1000), 0.332, 0.334);
        clock.Observe(10, 40);
        Assert.Equal(6.165, clock.AverageSpeedMs, 6);
        clock.Observe(0, 100);
        Assert.Equal(6.165, clock.AverageSpeedMs, 6);
        clock.Observe(10, 0);
        Assert.Equal(123.3 / 30, clock.AverageSpeedMs, 6);
    }

    [Theory]
    [InlineData(2.0)]
    [InlineData(8.33)]
    [InlineData(25.0)]
    public void SimulatedRideAndClosingTicksNeverExceedBudget(double actualSpeed)
    {
        var clock = new ClockBudget(720, 40, 1, 8.33);
        double now = clock.StartMinute, distance = 1000;
        const double delta = 0.3;
        while (distance > 0)
        {
            double tick = clock.GameTick(now, delta, distance);
            Assert.True(tick >= 0);
            now += tick;
            Assert.True(now <= clock.DeadlineMinute);
            double driven = Math.Min(distance, actualSpeed * delta);
            clock.Observe(delta, driven);
            distance -= driven;
        }
        for (int i = 0; i < 100; i++)
        {
            now += clock.CapTimeMachineTick(now, 10);
            Assert.InRange(now - clock.StartMinute, 0, 40 + ClockBudget.TimeMachineTolerance + 1e-10);
        }
        Assert.Equal(clock.DeadlineMinute + ClockBudget.TimeMachineTolerance, now, 9);
        Assert.Equal(0, clock.GameTick(now, delta, 0));
    }

    [Fact]
    public void SlowerRideStopsClockAtDeadlineEvenWithDistanceLeft()
    {
        var clock = new ClockBudget(0, 40, 1, 8.33);
        // A long scaled frame spends the allowance while the car makes little progress.
        double now = clock.GameTick(0, 300, 1000);
        clock.Observe(300, 100);
        Assert.Equal(40, now);
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(0, clock.GameTick(now, 1, 900 - i));
            Assert.Equal(0, clock.CapGameTick(now, 1));
            clock.Observe(1, 1);
        }
    }

    [Fact]
    public void OvershootAndNegativeRequestsNeverTurnClockBack()
    {
        var clock = new ClockBudget(0, 5, 1);
        double now = 4.9;
        now += clock.CapTimeMachineTick(now, 100);
        Assert.Equal(5.001, now, 9);
        Assert.True(clock.Left(now) < 0);
        Assert.Equal(0, clock.GameTick(now, 1, 10));
        Assert.Equal(0, clock.GameTick(now, 1, 0));
        Assert.Equal(0, clock.CapGameTick(now, 10));
        Assert.InRange(clock.CapTimeMachineTick(now, 10), 0, 1e-12);
        Assert.Equal(0, clock.CapGameTick(0, -10));
        Assert.Equal(0, clock.CapTimeMachineTick(0, -10));
        Assert.Equal(0, clock.GameTick(0, -1, 100));
        Assert.Equal(0, clock.CapTimeMachineTick(6, 10));
    }

    [Fact]
    public void PaceClampsAndTinyRemainingDistanceStillCapsTick()
    {
        var clock = new ClockBudget(0, 40, 0.5);
        Assert.Equal(0.5, clock.Pace(0, 1));
        Assert.Equal(0.5, clock.Pace(41, 0));
        Assert.Equal(0, clock.Pace(41, 100));
        Assert.Equal(0.5, clock.Pace(0, -100));
        Assert.Equal(0.001, clock.GameTick(39.999, 1, 0.001), 9);
        var zero = new ClockBudget(0, 0, 0);
        Assert.Equal(0, zero.GameTick(0, 1, 0));
        var noSeed = new ClockBudget(0, 40, 1, seedSeconds: 0);
        Assert.Equal(8, noSeed.AverageSpeedMs);
        noSeed.Observe(10, 0);
        Assert.Equal(0, noSeed.Pace(0, 100));
    }

    [Fact]
    public void InvalidClockInputsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClockBudget(0, -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClockBudget(0, 1, 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClockBudget(double.NaN, 1, 1));
        var clock = new ClockBudget(0, 1, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Observe(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.CapGameTick(0, double.NaN));
    }
}
