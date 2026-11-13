using System;
using System.Linq;
using DrivenRides.Core.Drive;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;
using Xunit;

public class RouteLinePathTests
{
    private static Polyline Line(params Vec3[] points) => new Polyline(points.ToList());

    [Fact]
    public void SamplesLongSpansAndRetainsEveryCornerAndDestination()
    {
        var path = new RouteLinePath();
        path.Reset(Line(new Vec3(0, 0, 0), new Vec3(5, 1, 0), new Vec3(5, 3, 3)));
        Assert.Equal(6, path.Count);
        Assert.Equal(8f, path.Length);
        Assert.Equal(new Vec3(5, 1, 0), path.Point(3));
        Assert.Equal(new Vec3(5, 3, 3), path.Point(5));
        for (int i = 1; i < path.Count; i++)
            Assert.InRange(Vec3.HorizontalDistance(path.Point(i - 1), path.Point(i)), 0.001f, 2.001f);
        Assert.Equal(1f / 3f, path.Point(1).Y, 5);
    }

    [Theory]
    [InlineData(-1f, 0)]
    [InlineData(0f, 1)]
    [InlineData(1f, 1)]
    [InlineData(2f, 2)]
    [InlineData(3.99f, 2)]
    [InlineData(4f, 3)]
    [InlineData(100f, 3)]
    public void TrimsAtAndBetweenSamples(float along, int expected)
    {
        var path = new RouteLinePath();
        path.Reset(Line(new Vec3(0, 0, 0), new Vec3(4, 0, 0)));
        Assert.Equal(expected, path.FirstAfter(along));
    }

    [Fact]
    public void CrossingIsTrimmedByDistanceRatherThanNearestPosition()
    {
        var path = new RouteLinePath();
        path.Reset(Line(new Vec3(0, 0, 0), new Vec3(4, 0, 0), new Vec3(4, 0, 4),
            new Vec3(0, 0, 4), new Vec3(0, 0, 0), new Vec3(-4, 0, 0)));
        Assert.Equal(1, path.FirstAfter(0));
        Assert.Equal(9, path.FirstAfter(16));
        Assert.Equal(new Vec3(-2, 0, 0), path.Point(9));
    }

    [Fact]
    public void RerouteReplacesAllSamplesIncludingShorterAndEmptyPlans()
    {
        var path = new RouteLinePath();
        path.Reset(Line(new Vec3(0, 0, 0), new Vec3(100, 0, 0)));
        path.Reset(Line(new Vec3(5, 2, 8), new Vec3(5, 2, 9)));
        Assert.Equal(2, path.Count);
        Assert.Equal(1f, path.Length);
        Assert.Equal(new Vec3(5, 2, 9), path.Point(1));
        path.Reset(Line());
        Assert.Equal(0, path.Count);
        Assert.Equal(0, path.FirstAfter(0));
    }

    [Fact]
    public void RepeatedPointsAndSinglePointDoNotCreateZeroLengthSegments()
    {
        var path = new RouteLinePath();
        path.Reset(Line(new Vec3(0, 0, 0), new Vec3(0, 0, 0), new Vec3(1, 0, 0)));
        Assert.Equal(2, path.Count);
        path.Reset(Line(new Vec3(0, 0, 0)));
        Assert.Equal(1, path.FirstAfter(0));
    }

    [Fact]
    public void TrimLookupAllocatesNothingAfterWarmup()
    {
        var path = new RouteLinePath();
        path.Reset(Line(new Vec3(0, 0, 0), new Vec3(5000, 0, 0)));
        for (int i = 0; i < 10000; i++) path.FirstAfter(i % 5000);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) path.FirstAfter(i % 5000);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }
}

public class RouteLineFollowTests
{
    private static readonly Vec3[] Loop =
    {
        new Vec3(0, 0, 0), new Vec3(100, 0, 0), new Vec3(100, 0, 70), new Vec3(0, 0, 70), new Vec3(0, 0, 0), new Vec3(0, 0, -50),
    };

    [Fact]
    public void EnablingAfterDrivingRoundABlockStartsAtTheCar()
    {
        var line = DriveLine.Build(Loop, Array.Empty<(int, Polyline)>());
        var progress = new RouteProgress(Loop);
        var car = new Vec3(0, 0, 0);
        for (var along = 1f; along <= 345f; along += 1f)
        {
            car = line.Line.At(along);
            progress.Update(car);
        }
        Assert.Equal(345.0, progress.AlongMetres, 1);
        var stale = 0f;
        for (var i = 0; i < 1000; i++) stale = line.Line.Project(car.X, car.Z, stale);
        Assert.True(stale < 10f, $"projection unexpectedly recovered to {stale}");
        Assert.Equal(345f, line.Follow(car.X, car.Z, 0f, progress.NextIndex), 2);
        var head = RouteLinePath.Head(line.Line, line.Follow(car.X, car.Z, 0f, progress.NextIndex), car);
        Assert.Equal(0f, Vec3.HorizontalDistance(car, head));
        var shown = line.Follow(car.X, car.Z, 0f, progress.NextIndex);
        car = line.Line.At(350f);
        progress.Update(car);
        Assert.Equal(350f, line.Follow(car.X, car.Z, shown, progress.NextIndex), 2);
    }

    [Fact]
    public void ANewPlanFollowsFromItsStart()
    {
        var line = DriveLine.Build(Loop, Array.Empty<(int, Polyline)>());
        var progress = new RouteProgress(Loop);
        var car = new Vec3(3, 0, 0.5f);
        progress.Update(car);
        Assert.Equal(3f, line.Follow(car.X, car.Z, 0f, progress.NextIndex), 2);
    }

    [Fact]
    public void NativeUTurnStartsAtTheCarEvenWhenItRunsWideOfTheOutgoingLane()
    {
        var points = new[]
        {
            new Vec3(20, 0, 12), new Vec3(0, 0, 12), new Vec3(-4, 0, 10),
            new Vec3(-6, 0, 6), new Vec3(-4, 0, 2), new Vec3(0, 0, 0),
            new Vec3(20, 0, 0), new Vec3(60, 0, 0),
        };
        var line = DriveLine.Build(points, Array.Empty<(int, Polyline)>());
        var progress = new RouteProgress(points);
        for (var a = 0f; a <= line.AlongOf[5] + 8f; a += 1f) progress.Update(line.Line.At(a));
        var car = new Vec3(8, 1, -7.792f);
        progress.Update(car);
        var along = line.Follow(car.X, car.Z, 0f, progress.NextIndex);
        Assert.Equal(line.AlongOf[5] + 8f, along, 3);
        Assert.InRange(Vec3.HorizontalDistance(car, line.Line.At(along)), 7.791f, 7.793f);
        var samples = new RouteLinePath();
        samples.Reset(line.Line);
        var next = samples.Point(samples.FirstAfter(along));
        Assert.True(next.X > car.X);
        Assert.Equal(0f, next.Z);
        var head = RouteLinePath.Head(line.Line, along, car);
        Assert.Equal(0f, Vec3.HorizontalDistance(car, head));
        Assert.Equal(0f, head.Y);
    }

    [Fact]
    public void PhysicsHeadStaysOnTheRearAxlePlanWithAForwardFirstSegment()
    {
        var line = new Polyline(new[] { new Vec3(0, 0, 0), new Vec3(20, 0, 0) }.ToList());
        var body = new Vec3(10.3f, 1, 0);
        var head = RouteLinePath.Head(line, 9f);
        var samples = new RouteLinePath();
        samples.Reset(line);
        var next = samples.Point(samples.FirstAfter(9f));
        Assert.Equal(new Vec3(9, 0, 0), head);
        Assert.InRange(Vec3.HorizontalDistance(body, head), 1.29f, 1.31f);
        Assert.True(next.X - head.X > 0f);
    }
}
