using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;
using Xunit;

namespace DrivenRides.Core.Tests;

// Real Life Overhaul 1.0.5's City Cabs (on by default) drive a called taxi to the caller with Gley ignoring it; Gley's
// target waypoint for the cab is still the one it was loaded at (docs/rlo-1.0.5-compatibility.md).
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
        // In front, same way, a little to the side and lower (the cab pulled in to the kerb).
        Assert.True(TaxiStart.IsAhead(cab, north, new Vec3(11.5f, 1.8f, 16f), north));
        // Behind the cab.
        Assert.False(TaxiStart.IsAhead(cab, north, new Vec3(10f, 2f, 6f), north));
        // In front, but the oncoming lane.
        Assert.False(TaxiStart.IsAhead(cab, north, new Vec3(7f, 2f, 16f), new Vec3(0f, 0f, -1f)));
        // In front, a cross street's lane (90 degrees off).
        Assert.False(TaxiStart.IsAhead(cab, north, new Vec3(10f, 2f, 20f), new Vec3(1f, 0f, 0f)));
        // A lane 45 degrees off still counts; the pitch of a ramp doesn't change the flat heading.
        Assert.True(TaxiStart.IsAhead(cab, new Vec3(0f, 0.3f, 2f), new Vec3(12f, 2f, 18f), new Vec3(1f, 0f, 1f)));
    }
}
