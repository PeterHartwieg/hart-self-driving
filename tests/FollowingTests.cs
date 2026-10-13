using System;
using System.Linq;
using DrivenRides.Core.Drive;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;
using Xunit;
using Xunit.Abstractions;

namespace DrivenRides.Core.Tests;

// Following a traffic car on a straight (T3 20261013-191420: half the moving time was spent following, 2.3 to 2.6 s
// behind, because every car ahead was treated as standing).
public sealed class FollowingTests
{
    private readonly ITestOutputHelper output;
    public FollowingTests(ITestOutputHelper output) => this.output = output;

    private sealed class NoFlags : ILiveFlags
    {
        public bool Stop(int waypoint) => false;
        public bool GiveWayEntry(int waypoint, out int junction, out int road) { junction = road = -1; return false; }
    }

    [Theory]
    [InlineData(10f)]
    [InlineData(7f)]
    [InlineData(13f)]
    public void FollowsAtItsSpeedAboutASecondAndAHalfBehindThenStopsBehindIt(float leaderMs)
    {
        var points = Enumerable.Range(0, 201).Select(k => new Vec3(0, 0, k * 8f)).ToList();
        var line = DriveLine.Build(points, Array.Empty<(int, Polyline)>());
        var car = RideCar.Dash;
        var driver = new PursuitDriver(line, car.Wheelbase, car.FrontAhead, 13.9f);
        var graph = new TurnTestGraph();
        graph.Add(0, 0);
        var traffic = new TrafficDriver(graph.Build(), line, driver, new RideBody { Wheelbase = car.Wheelbase, FrontAhead = car.FrontAhead, RearBehind = car.RearBehind, HalfWidth = car.HalfWidth });
        var ride = new CarModel(car.Wheelbase, car.Lock, 0, 0, 0, leaderMs);
        float leaderZ = 40f, v = leaderMs;
        const float length = 4.5f, dt = 0.02f;
        var decision = new TrafficDecision();
        var minGap = float.MaxValue;
        float speedSum = 0, headwaySum = 0;
        var samples = 0;
        for (var step = 0; step < 3500; step++)
        {
            var t = step * dt;
            // The traffic car keeps its speed for 45 s, then stops at Gley's braking.
            if (t > 45f) v = Math.Max(0f, v - PursuitDriver.LeaderBrake * dt);
            leaderZ += v * dt;
            var other = new OtherCar { Id = 1, X = 0, Z = leaderZ, Heading = 0, Vz = v, Length = length, Width = 1.9f, TriggerLength = 3f, Target = -1 };
            traffic.Decide(new TrafficSnapshot { Time = t, RearX = (float)ride.X, RearZ = (float)ride.Z, Heading = (float)ride.Heading, Speed = (float)ride.Speed, Marks = Array.Empty<PathMark>(), Cars = new[] { other }, Flags = new NoFlags() }, decision);
            var input = ride.Input(dt);
            input.GapAhead = decision.GapAhead;
            input.GapSpeed = decision.GapSpeed;
            ride.Step(driver.Step(input), dt);
            var gap = leaderZ - length / 2f - ((float)ride.Z + car.FrontAhead);
            minGap = Math.Min(minGap, gap);
            if (t > 25f && t < 45f)
            {
                speedSum += (float)ride.Speed;
                headwaySum += gap / Math.Max(0.1f, (float)ride.Speed);
                samples++;
            }
        }
        var speed = speedSum / samples;
        var headway = headwaySum / samples;
        output.WriteLine($"leader {leaderMs} m/s: following at {speed:0.00} m/s, {headway:0.00} s behind; stopped {minGap:0.0} m behind at the closest");
        Assert.InRange(speed, leaderMs - 0.3f, leaderMs + 0.3f);
        Assert.InRange(headway, 1.0f, 1.5f);
        Assert.InRange(minGap, 1.5f, 4f);
        Assert.True(Math.Abs(ride.Speed) < 0.1);
    }
}
