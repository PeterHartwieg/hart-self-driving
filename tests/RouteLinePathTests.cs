using System;
using System.Linq;
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
