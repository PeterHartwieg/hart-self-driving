using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;
using Xunit;

namespace DrivenRides.Core.Tests;

public sealed class TaxiStartTests
{
    [Fact]
    public void ATaxiGleyDrivesLeavesFromItsTargetWaypoint()
    {
        Assert.Equal(TaxiStartKind.GleyTarget, TaxiStart.Choose(ignoredByGley: false, carPhysics: true));
        Assert.Equal(TaxiStartKind.GleyTarget, TaxiStart.Choose(ignoredByGley: false, carPhysics: false));
    }

    [Fact]
    public void ACabAnotherModHoldsStartsAheadOfItWithCarPhysics()
    {
        Assert.Equal(TaxiStartKind.AheadOfCab, TaxiStart.Choose(ignoredByGley: true, carPhysics: true));
    }

    [Fact]
    public void ACabAnotherModHoldsIsLeftToTheGameForAGleyRide()
    {
        Assert.Equal(TaxiStartKind.Vanilla, TaxiStart.Choose(ignoredByGley: true, carPhysics: false));
    }

    [Fact]
    public void OnlyAWaypointInFrontOnALaneRunningTheCabsWayIsAStart()
    {
        var cab = new Vec3(10f, 2f, 10f);
        var north = new Vec3(0f, 0f, 1f);
        Assert.True(TaxiStart.IsAhead(cab, north, new Vec3(11.5f, 1.8f, 16f), north));
        Assert.False(TaxiStart.IsAhead(cab, north, new Vec3(10f, 2f, 6f), north));
        Assert.False(TaxiStart.IsAhead(cab, north, new Vec3(7f, 2f, 16f), new Vec3(0f, 0f, -1f)));
        Assert.False(TaxiStart.IsAhead(cab, north, new Vec3(10f, 2f, 20f), new Vec3(1f, 0f, 0f)));
        Assert.True(TaxiStart.IsAhead(cab, new Vec3(0f, 0.3f, 2f), new Vec3(12f, 2f, 18f), new Vec3(1f, 0f, 1f)));
    }
}
