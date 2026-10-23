using System;
using DrivenRides.Core.Drive;
using DrivenRides.Core.Parking;
using DrivenRides.Core.Routing;
using Xunit;

namespace DrivenRides.Core.Tests;

public sealed class TrafficReservationTests
{
    private static TrafficReservation Street => new(new Box(0, 0, 0, 1.4f, 9), 0);
    private static OtherCar Car(float x, float z, float heading = 0, float speed = 0) => new()
    {
        X = x, Z = z, Heading = heading, Width = 1.8f, Length = 4.5f,
        Vx = MathF.Sin(heading) * speed, Vz = MathF.Cos(heading) * speed
    };

    [Theory]
    [InlineData(1, 0.02f)]
    [InlineData(8, 0.02f)]
    [InlineData(20, 0.02f)]
    [InlineData(35, 0.1f)]
    public void ApproachingCarBrakesOutsideAndStaysThere(float speed, float dt)
    {
        var car = Car(0, -200, speed: speed);
        var stopped = false;
        for (var i = 0; i < 15000; i++)
        {
            var distance = Street.StopDistance(car, 0);
            var next = TrafficReservation.BrakedSpeed(car.Speed, distance, dt);
            car.Vz = next; car.Z += next * dt;
            Assert.False(Street.Touches(TrafficReservation.Body(car), 0));
            if (next < 0.001f) { stopped = true; break; }
        }
        Assert.True(stopped);
        Assert.InRange(car.Z + car.Length / 2, -13, -9.49f);
        Assert.True(float.IsFinite(Street.StopDistance(car, 0)));
        // A frame with a normal Gley acceleration must still be held on the next check.
        car.Vz = 0.2f;
        Assert.Equal(0, TrafficReservation.BrakedSpeed(car.Speed, Street.StopDistance(car, 0), 0.1f));
    }

    [Fact]
    public void LateAcquisitionCannotCrossTheBoundaryInOneStep()
    {
        var car = Car(0, -11.85f, speed: 20);
        var distance = Street.StopDistance(car, 0);
        car.Z += TrafficReservation.BrakedSpeed(car.Speed, distance, 0.1f) * 0.1f;
        Assert.False(Street.Touches(TrafficReservation.Body(car), 0));
    }

    [Fact]
    public void OccupantsCanLeaveBeforeTheManoeuvreStarts()
    {
        var car = Car(0, 0, speed: 5);
        Assert.True(Street.Touches(TrafficReservation.Body(car), 0));
        Assert.True(float.IsPositiveInfinity(Street.StopDistance(car, 0)));
        car.Z = 13;
        Assert.False(Street.Touches(TrafficReservation.Body(car), 0));
        Assert.True(float.IsPositiveInfinity(Street.StopDistance(car, 0)));
    }

    [Theory]
    [InlineData(3.5f, -15, 0, 0)] // Adjacent lane, same direction.
    [InlineData(-3.5f, 15, MathF.PI, 0)] // Opposite lane.
    [InlineData(0, -15, 0, 5)] // Bridge above.
    [InlineData(0, 15, 0, 0)] // Already leaving.
    public void UnrelatedTrafficContinues(float x, float z, float heading, float height)
    {
        Assert.True(float.IsPositiveInfinity(Street.StopDistance(Car(x, z, heading, 8), height)));
    }

    [Fact]
    public void CrossingAndChangingLaneIntoTheAreaAreHeld()
    {
        Assert.True(float.IsFinite(Street.StopDistance(Car(-10, 0, MathF.PI / 2, 8), 0)));
        var changing = Car(3.5f, -14, speed: 8);
        Assert.True(float.IsPositiveInfinity(Street.StopDistance(changing, 0)));
        Assert.True(float.IsFinite(Street.StopDistance(changing, 0, new Vec3(0, 0, -3))));
    }

    [Fact]
    public void SpawnCheckUsesTheEntireBodyAndRoadLevel()
    {
        Assert.True(Street.Touches(new Box(0, 11, 0, 0.9f, 2.5f), 0));
        Assert.False(Street.Touches(new Box(0, 11, 0, 0.9f, 2.5f), 5));
        Assert.False(Street.Touches(new Box(3.5f, 0, 0, 0.9f, 2.5f), 0));
    }

    [Fact]
    public void ReusedPoolIndexDoesNotCarryAHoldToAnotherLocation()
    {
        var car = Car(0, -15, speed: 8); car.Id = 17;
        Assert.True(float.IsFinite(Street.StopDistance(car, 0)));
        car.X = 30;
        Assert.True(float.IsPositiveInfinity(Street.StopDistance(car, 0)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(MathF.PI / 2)]
    public void RegionCoversRotatedBodiesThroughoutBothLegs(float heading)
    {
        var spec = new CarSpec("long", 3.7f, _ => 35, new Vec3(0.1f, 0.5f, 1.8f), new Vec3(1.1f, 0.7f, 3.1f), 50, 1.6f);
        var poses = new[] { new CarPose(0, 0, heading), new CarPose(4, 6, heading + 0.4f), new CarPose(3, -2, heading) };
        var plan = new ManoeuvrePlan(new[] { new ManoeuvrePoint(poses[0], 1), new ManoeuvrePoint(poses[1], 1),
            new ManoeuvrePoint(poses[2], -1) }, poses[2]);
        var region = TrafficReservation.ForPlan(spec, plan, 0);
        foreach (var p in poses)
        {
            var body = ManoeuvrePlan.Body(spec, p, 0.25f);
            var a = region.Area;
            Assert.True(MathF.Abs((body.X - a.X) * a.Sin + (body.Z - a.Z) * a.Cos) + body.RadiusOn(a.Sin, a.Cos) <= a.HalfLength);
            Assert.True(MathF.Abs((body.X - a.X) * a.Cos - (body.Z - a.Z) * a.Sin) + body.RadiusOn(a.Cos, -a.Sin) <= a.HalfWidth);
        }
    }

    [Fact]
    public void PlanningReservationIncludesTheForwardStagingArea()
    {
        var spec = new CarSpec("car", 2.7f, _ => 35, new Vec3(0, 0.5f, 1.2f), new Vec3(0.9f, 0.7f, 2.2f), 50, 1.6f);
        var area = TrafficReservation.Preparing(spec, new CarPose(0, 2, 0), new CarPose(3, 0, 0), 0, true);
        Assert.True(area.Touches(new Box(0, spec.FrontAhead + 3, 0, 0.9f, 2.2f), 0));
    }
}
