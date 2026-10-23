using System;
using System.Linq;
using DrivenRides.Core.Drive;
using DrivenRides.Core.Parking;
using DrivenRides.Core.Routing;
using Xunit;

namespace DrivenRides.Core.Tests;

public sealed class ManoeuvreTests
{
    [Theory]
    [InlineData(1f)]
    [InlineData(-1f)]
    public void PullsOutOfAGapTooShortForOneFullSearchPrimitive(float mirror)
    {
        // Reduced from the headless departure replay: every initial 0.96 m rollout hit a parked car,
        // although 0.56-0.80 m of each rollout was clear. The previous search exhausted its root node.
        var car = new CarSpec("tight gap", 2.665f, _ => 35, new Vec3(0, 0.782f, 1.199f),
            new Vec3(0.897f, 0.578f, 2.461f), 70, 1.504f, 140);
        var start = new CarPose(0, 0, MathF.PI);
        var goal = new CarPose(mirror * 3.10836f, -6.0076f, mirror * -3.139159f);
        var world = new ManoeuvreSpace { MinX = -12, MaxX = 12, MinZ = -15, MaxZ = 10,
            Allowed = p => p.X * mirror >= -0.5f && p.X * mirror <= 3.61f };
        world.Obstacles.Add(new Box(mirror * 0.01247f, 5.61148f, mirror * 3.101197f, 1.128005f, 3.404085f));
        world.Obstacles.Add(new Box(mirror * -0.13734f, -7.10785f, mirror * -3.114016f, 0.921325f, 2.383022f));
        var search = new ManoeuvrePlanner(car, world, start, goal);
        while (!search.Done) search.Step();
        Assert.NotNull(search.Result);
        Assert.Contains(search.Result!.Points, p => p.Direction < 0);
        Assert.All(search.Result.Points, p => Assert.True(world.Clear(car, p.Pose)));
        var model = car.Model(start.X, start.Z, start.Heading);
        var driver = new ManoeuvreDriver(car, search.Result);
        for (var i = 0; i < 15000 && !driver.Done && driver.Failure.Length == 0; i++)
        {
            var pose = new CarPose((float)model.X, (float)model.Z, (float)model.Heading, (float)model.Speed, (float)model.Steer);
            Assert.All(world.Obstacles, b => Assert.False(ManoeuvrePlan.Body(car, pose).Overlaps(b)));
            model.Step(driver.Step(pose, false), 0.02f);
        }
        Assert.True(driver.Done, driver.Failure);
    }

    private static CarSpec Car => new("test", 2.7f, _ => 35f, new Vec3(0, 0.6f, 1.2f), new Vec3(0.9f, 0.6f, 2.2f), 50, 1.6f, 140);
    private static ManoeuvreSpace Space() => new() { MinX = -9, MaxX = 9, MinZ = -20, MaxZ = 25 };
    private static ManoeuvrePlan Plan(CarPose from, CarPose to, ManoeuvreSpace? world = null, bool parking = false)
    {
        var search = new ManoeuvrePlanner(Car, world ?? Space(), from, to, stageForParking: parking);
        while (!search.Done) search.Step();
        Assert.True(search.Result != null, $"{search.Reason}; {search.Expanded} expansions");
        return search.Result!;
    }

    [Theory]
    [InlineData(3f, 0f, 0f, 10f, false)]
    [InlineData(-3f, 0f, 0f, 10f, false)]
    [InlineData(0f, 10f, 3f, 0f, true)]
    [InlineData(0f, 0f, 0f, -5f, false)]
    [InlineData(0f, 0f, 2.7f, 0f, true)]
    [InlineData(0f, -3f, 2.7f, 0f, true)]
    [InlineData(0f, 2.4f, 2.7f, 0f, true)]
    public void TracksForwardAndReverseToTheRequestedPose(float x, float z, float gx, float gz, bool parking)
    {
        var plan = Plan(new CarPose(x, z, 0), new CarPose(gx, gz, 0), parking: parking);
        var model = Car.Model(x, z, 0);
        var driver = new ManoeuvreDriver(Car, plan);
        for (var i = 0; i < 15000 && !driver.Done && driver.Failure.Length == 0; i++)
        {
            var p = new CarPose((float)model.X, (float)model.Z, (float)model.Heading, (float)model.Speed, (float)model.Steer);
            var o = driver.Step(p, false);
            model.Step(o, 0.02f);
        }
        Assert.True(driver.Done, driver.Failure + $" at {model.X:0.00},{model.Z:0.00}, heading {model.Heading:0.000}; goal {plan.Points.Last().Pose.Heading:0.000}; cursor {driver.Cursor}/{plan.Points.Count}");
        Assert.InRange(ManoeuvrePlan.Distance(new CarPose((float)model.X, (float)model.Z, (float)model.Heading), plan.Goal), 0, 0.4f);
    }

    [Fact]
    public void PullsOutBetweenTwoCarsWithoutSweepingThroughEither()
    {
        var world = Space();
        world.Obstacles.Add(new Box(3, 6.5f, 0, 0.9f, 2.2f));
        world.Obstacles.Add(new Box(3, -5, 0, 0.9f, 2.2f));
        var plan = Plan(new CarPose(3, 0, 0), new CarPose(0, 12, 0), world);
        Assert.All(plan.Points, p => Assert.True(world.Clear(Car, p.Pose)));
        FollowWithObstacles(plan, world);
    }

    [Fact]
    public void ParallelParkingFindsAReversePathAndPreservesClearance()
    {
        var world = Space();
        world.Obstacles.Add(new Box(3, 7, 0, 0.9f, 2.2f));
        world.Obstacles.Add(new Box(3, -5, 0, 0.9f, 2.2f));
        var plan = Plan(new CarPose(0, 9, 0), new CarPose(3, 0, 0), world);
        Assert.Contains(plan.Points, p => p.Direction == -1);
        Assert.All(plan.Points, p => Assert.True(world.Clear(Car, p.Pose)));
        FollowWithObstacles(plan, world);
    }

    [Theory]
    [InlineData(2.665f, 35f, 1.504f, 1.199f, 0.897f, 2.461f, 6f)]
    [InlineData(3.748f, 40f, 1.534f, 1.831f, 1.056f, 3.076f, 12f)]
    [InlineData(4.02f, 35f, 1.7f, 1.624f, 1.128f, 3.404f, 15f)]
    public void MeasuredCarShapesCanPullOutAndReverseParkFacingEast(float wheelbase, float steer, float track,
        float centre, float width, float halfLength, float advance)
    {
        var car = new CarSpec("measured", wheelbase, _ => steer, new Vec3(0, 0.8f, centre), new Vec3(width, 0.8f, halfLength), 50, track, 140);
        var parked = new CarPose(-12, -2.74f, MathF.PI / 2);
        var lane = new CarPose(-12 + advance, 0, MathF.PI / 2);
        foreach (var reverse in new[] { false, true })
        {
            var start = reverse ? lane : parked;
            var goal = reverse ? parked : lane;
            var world = new ManoeuvreSpace { MinX = -25, MaxX = 20, MinZ = -4, MaxZ = 0.5f };
            var search = new ManoeuvrePlanner(car, world, start, goal, stageForParking: reverse);
            while (!search.Done) search.Step();
            Assert.NotNull(search.Result);
            var model = car.Model(start.X, start.Z, start.Heading);
            var driver = new ManoeuvreDriver(car, search.Result!);
            for (var i = 0; i < 15000 && !driver.Done && driver.Failure.Length == 0; i++)
            {
                var p = new CarPose((float)model.X, (float)model.Z, (float)model.Heading, (float)model.Speed, (float)model.Steer);
                model.Step(driver.Step(p, false), 0.02f);
            }
            Assert.True(driver.Done, driver.Failure);
        }
    }

    [Fact]
    public void OccupiedTargetIsRefusedWithoutMoving()
    {
        var world = Space(); world.Obstacles.Add(new Box(3, 1, 0, 1, 3));
        var planner = new ManoeuvrePlanner(Car, world, new CarPose(0, 10, 0), new CarPose(3, 0, 0));
        Assert.True(planner.Done); Assert.Null(planner.Result); Assert.Equal(0, planner.Expanded);
    }

    [Fact]
    public void ParkingBesideTheCarRefusesAnObstructedForwardStagingPose()
    {
        var world = Space(); world.Obstacles.Add(new Box(0, 6, 0, 0.9f, 1));
        var planner = new ManoeuvrePlanner(Car, world, new CarPose(0, 0, 0), new CarPose(3, 0, 0), stageForParking: true);
        Assert.True(planner.Done); Assert.Null(planner.Result);
        Assert.Contains("staging", planner.Reason);
    }

    [Theory]
    [InlineData(0f, 2.38f, 0f, 2.72f, 0f)]
    [InlineData(-177.32f, 458.60f, 1.5707963f, -179.69f, 455.87f)]
    public void ParkingStartsByPullingAheadOfAStoppedFollower(float x, float z, float heading, float gx, float gz)
    {
        // In-game arrival: the rear axle stopped 2.38 m beyond the space, with a car close behind.
        // Reversing straight away deadlocked against that follower before entering the space.
        var car = new CarSpec("vord", 2.665f, _ => 35f, new Vec3(0, 0.8f, 1.199f),
            new Vec3(0.897f, 0.8f, 2.461f), 50, 1.504f, 140);
        var start = new CarPose(x, z, heading);
        var world = new ManoeuvreSpace { MinX = x - 20, MaxX = x + 20, MinZ = z - 20, MaxZ = z + 20 };
        var search = new ManoeuvrePlanner(car, world, start, new CarPose(gx, gz, heading), stageForParking: true);
        while (!search.Done) search.Step();
        Assert.True(search.Result != null, search.Reason);
        var plan = search.Result!;
        Assert.Equal(1, plan.Points[1].Direction);
        var firstReverse = plan.Points.First(p => p.Direction < 0);
        var s = MathF.Sin(heading); var c = MathF.Cos(heading);
        Assert.True((firstReverse.Pose.X - gx) * s + (firstReverse.Pose.Z - gz) * c > car.FrontAhead + 2.5f);
        var follower = new OtherCar { X = x - s * 4.46f, Z = z - c * 4.46f, Heading = heading, Width = 1.794f, Length = 4.922f };
        Assert.False(new ManoeuvreDriver(car, plan).Blocked(start, new[] { follower }, _ => true));
        var driver = new ManoeuvreDriver(car, plan);
        var model = car.Model(start.X, start.Z, start.Heading);
        var followerBody = new Box(follower.X, follower.Z, follower.Heading, follower.Width / 2, follower.Length / 2);
        for (var i = 0; i < 15000 && !driver.Done && driver.Failure.Length == 0; i++)
        {
            var pose = new CarPose((float)model.X, (float)model.Z, (float)model.Heading, (float)model.Speed, (float)model.Steer);
            Assert.False(ManoeuvrePlan.Body(car, pose, 0.15f).Overlaps(followerBody));
            model.Step(driver.Step(pose, false), 0.02f);
        }
        Assert.True(driver.Done, driver.Failure);
    }

    [Fact]
    public void SearchHasAHardBudgetAndAnIncrementalStep()
    {
        var world = Space(); world.Obstacles.Add(new Box(0, 5, 0, 10, 1));
        var planner = new ManoeuvrePlanner(Car, world, new CarPose(0, -5, 0), new CarPose(0, 12, 0));
        planner.Step(3); Assert.InRange(planner.Expanded, 1, 3);
        while (!planner.Done) planner.Step();
        Assert.Null(planner.Result); Assert.InRange(planner.Expanded, 1, ManoeuvrePlanner.MaxExpanded);
    }

    [Fact]
    public void ANewObstacleHoldsTheCarWithoutAdvancingThePath()
    {
        var driver = new ManoeuvreDriver(Car, Plan(new CarPose(0, 0, 0), new CarPose(0, -5, 0)));
        var cursor = driver.Cursor;
        var o = driver.Step(new CarPose(0, 0, 0), true);
        Assert.Equal(0, o.Desired); Assert.Equal(1, o.Handbrake); Assert.Equal(cursor, driver.Cursor);
        Assert.False(driver.Done);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void PedalsOvercomeRollingResistanceAndStillStopAtTheEnd(int direction)
    {
        var plan = Plan(new CarPose(0, 0, 0), new CarPose(0, direction * 5, 0));
        var driver = new ManoeuvreDriver(Car, plan);
        var model = Car.Model(0, 0, 0);
        for (var i = 0; i < 6000 && !driver.Done && driver.Failure.Length == 0; i++)
        {
            var pose = new CarPose((float)model.X, (float)model.Z, (float)model.Heading, (float)model.Speed, (float)model.Steer);
            var o = driver.Step(pose, false);
            // Independent longitudinal plant: throttle has a dead zone and cannot be replaced by Desired.
            var speed = Math.Abs(model.Speed);
            speed = Math.Max(0, speed + ((o.Throttle - 0.12f) * 4 - speed * 0.2f - o.Handbrake * 6) * 0.02f);
            Assert.InRange(speed, 0, 1.1f);
            o.Desired = direction * (float)speed;
            model.Step(o, 0.02f);
        }
        Assert.True(driver.Done, driver.Failure);
        Assert.InRange(Math.Abs(model.Speed), 0, 0.06f);
    }

    private static void FollowWithObstacles(ManoeuvrePlan plan, ManoeuvreSpace world)
    {
        var start = plan.Points[0].Pose;
        var model = Car.Model(start.X, start.Z, start.Heading);
        var driver = new ManoeuvreDriver(Car, plan);
        for (var i = 0; i < 15000 && !driver.Done && driver.Failure.Length == 0; i++)
        {
            var pose = new CarPose((float)model.X, (float)model.Z, (float)model.Heading, (float)model.Speed, (float)model.Steer);
            var body = ManoeuvrePlan.Body(Car, pose, 0.1f);
            Assert.DoesNotContain(world.Obstacles, obstacle => body.Overlaps(obstacle));
            var output = driver.Step(pose, false);
            if (Math.Abs(pose.Speed) > 0.06f && output.Desired != 0)
                Assert.Equal(Math.Sign(pose.Speed), Math.Sign(output.Desired));
            model.Step(output, 0.02f);
        }
        Assert.True(driver.Done, driver.Failure + $" at {model.X:0.000},{model.Z:0.000}, heading {model.Heading:0.000}; goal {plan.Points.Last().Pose.Heading:0.000}; cursor {driver.Cursor}/{plan.Points.Count}");
    }

    [Fact]
    public void ApproachingTrafficIsWaitedForBeforeTheCarEntersItsCorridor()
    {
        var pose = new CarPose(0, 0, 0);
        var driver = new ManoeuvreDriver(Car, Plan(pose, new CarPose(0, 8, 0)));
        var crossing = new OtherCar { X = -10, Z = 5, Vx = 5, Heading = MathF.PI / 2, Width = 1.8f, Length = 4.4f };
        Assert.True(driver.Blocked(pose, new[] { crossing }, _ => true));
        crossing.X = 10;
        Assert.False(driver.Blocked(pose, new[] { crossing }, _ => true));
    }
}
