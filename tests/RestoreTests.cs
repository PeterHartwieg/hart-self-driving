using System;
using System.Collections.Generic;
using System.Linq;
using DrivenRides.Core.Drive;
using DrivenRides.Core.Testing;
using Xunit;
using Xunit.Abstractions;

namespace DrivenRides.Core.Tests;

public sealed class RestoreTests
{
    private readonly ITestOutputHelper output;
    public RestoreTests(ITestOutputHelper output) => this.output = output;

    private const float Dt = 0.02f;

    private static (CarModel Car, PursuitDriver Driver) Restore(ScenarioRide at, DrivenRides.Core.Routing.LaneGraph graph)
    {
        var line = at.BuildLine(graph, out _);
        return (at.BuildCar(), at.BuildDriver(line));
    }

    private static ScenarioRide Copy(ScenarioRide r) => new ScenarioRide
    {
        Path = r.Path, Arcs = r.Arcs, Wheelbase = r.Wheelbase, Lock = r.Lock, FrontAhead = r.FrontAhead, RearBehind = r.RearBehind, HalfWidth = r.HalfWidth,
        TopMs = r.TopMs, Slow = r.Slow, LockCurve = r.LockCurve, TrackWidth = r.TrackWidth, YawFactor = r.YawFactor, WheelRate = r.WheelRate, WheelSmooth = r.WheelSmooth,
    };

    [Theory]
    [InlineData("t3c-211205-ride9-contact-1.json", 1.0f)]
    [InlineData("t3c-211205-ride9-contact-1.json", 6.0f)]
    [InlineData("t3c-211205-ride9-contact-1.json", 9.0f)]
    [InlineData("t3c-211205-ride9-contact-2.json", 2.0f)]
    public void ARestoredDriveGoesOnAsTheOriginal(string file, float at)
    {
        var s = TrafficScenarioJson.Load(System.IO.File.ReadAllText(System.IO.Path.Combine(TrafficScenarioJson.Folder()!, file)));
        var template = file;
        var graph = s.BuildGraph();
        var line = s.Ride.BuildLine(graph, out var pathAlong);
        Assert.NotEmpty(line.Curves);
        var driver = s.Ride.BuildDriver(line);
        var car = s.Ride.BuildCar();
        var steps = (int)(at / Dt);
        for (var k = 0; k < steps; k++) car.Step(driver.Step(car.Input(Dt)), Dt);

        var saved = Copy(s.Ride);
        saved.X = (float)car.X; saved.Z = (float)car.Z; saved.Heading = (float)car.Heading; saved.Speed = (float)car.Speed;
        saved.Steer = (float)car.Steer; saved.Integral = driver.Integral;
        saved.Line = line.Line.Points.ToList();
        saved.LineAlong = pathAlong.ToArray();
        saved.Curves = line.Curves.ToList();
        var restored = new TrafficScenario { Name = "restore", Ride = saved, Seconds = 10f };
        restored.Waypoints.AddRange(s.Waypoints);
        var json = restored.ToJson();
        var (car2, driver2) = Restore(TrafficScenarioJson.Load(json).Ride, graph);

        var worst = 0.0;
        for (var k = 0; k < (int)(6f / Dt); k++)
        {
            car.Step(driver.Step(car.Input(Dt)), Dt);
            car2.Step(driver2.Step(car2.Input(Dt)), Dt);
            worst = Math.Max(worst, Math.Sqrt((car.X - car2.X) * (car.X - car2.X) + (car.Z - car2.Z) * (car.Z - car2.Z)));
        }
        output.WriteLine($"{template}: restored at {at} s (wheel {saved.Steer:0.0}°, {saved.Speed:0.0} m/s, {driver.Along:0} m along, curves {string.Join(" ", line.Curves.Select(c => $"{c.From:0}-{c.To:0}"))}), worst {worst:0.000} m apart over 6 s");
        Assert.True(worst < 0.05, $"{worst:0.000} m apart");
    }

    [Fact]
    public void TheWheelHoldsItsRestoredAngle()
    {
        var car = new ScenarioRide { Steer = -12f, Speed = 5f }.BuildCar();
        car.Step(new DriveOutput { SteerDegrees = -12f, Throttle = 0f, Brake = 0f }, Dt);
        Assert.Equal(-12.0, car.Steer, 2);
    }

    [Fact]
    public void TheCurveSpansComeBack()
    {
        var s = TrafficScenarioJson.Load(System.IO.File.ReadAllText(System.IO.Path.Combine(TrafficScenarioJson.Folder()!, "t3c-211205-ride9-contact-1.json")));
        var graph = s.BuildGraph();
        var line = s.Ride.BuildLine(graph, out var pathAlong);
        s.Ride.Line = line.Line.Points.ToList();
        s.Ride.LineAlong = pathAlong.ToArray();
        s.Ride.Curves = line.Curves.ToList();
        var again = TrafficScenarioJson.Load(s.ToJson()).Ride.BuildLine(graph, out _);
        Assert.Equal(line.Curves.Count, again.Curves.Count);
        for (var i = 0; i < line.Curves.Count; i++) Assert.InRange(again.Curves[i].From - line.Curves[i].From, -0.01f, 0.01f);
    }
}
