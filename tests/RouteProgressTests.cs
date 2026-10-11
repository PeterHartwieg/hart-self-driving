using System;
using System.Linq;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;
using Xunit;

namespace DrivenRides.Core.Tests;

public sealed class RouteProgressTests
{
    [Fact]
    public void ProjectsOntoRouteAndNeverLosesProgress()
    {
        var points = new[] { new Vec3(0, 0, 0), new Vec3(10, 0, 0), new Vec3(10, 0, 10) };
        var progress = new RouteProgress(points);
        points[1] = new Vec3(100, 0, 0);
        Assert.Equal(20, progress.LengthMetres);
        Assert.Equal(0, progress.NextIndex);
        progress.Update(new Vec3(-5, 0, 0));
        Assert.Equal(0, progress.NextIndex);
        progress.Update(new Vec3(4, 2, 0));
        Assert.Equal(4, progress.AlongMetres, 6);
        Assert.Equal(1, progress.NextIndex);
        Assert.Equal(2, progress.PointsLeft);
        progress.Update(new Vec3(10, 0, 3));
        Assert.Equal(13, progress.AlongMetres, 6);
        Assert.Equal(7, progress.LeftMetres, 6);
        Assert.Equal(2, progress.NextIndex);
        progress.Update(new Vec3(2, 0, 0));
        Assert.Equal(13, progress.AlongMetres, 6);
        Assert.Equal(2, progress.NextIndex);
        progress.Update(new Vec3(10, 0, 12));
        Assert.Equal(20, progress.AlongMetres, 6);
        Assert.Equal(0, progress.LeftMetres);
        Assert.Equal(0, progress.PointsLeft);
    }

    [Fact]
    public void WindowPreventsJumpToDistantSegments()
    {
        var progress = new RouteProgress(Enumerable.Range(0, 30).Select(i => new Vec3(i * 10, 0, 0)).ToArray());
        progress.Update(new Vec3(290, 0, 0));
        Assert.InRange(progress.AlongMetres, 1, 25);
        var first = progress.AlongMetres;
        progress.Update(new Vec3(290, 0, 0));
        Assert.InRange(progress.AlongMetres, first, first + 25);
        Assert.True(progress.NextIndex < 10);
    }

    [Fact]
    public void LoopNearStartCannotJumpToLaterSection()
    {
        var points = Enumerable.Range(0, 30).Select(i => new Vec3(i * 10, 0, 0)).ToArray();
        points[28] = new Vec3(5, 0, -1);
        points[29] = new Vec3(5, 0, 1);
        var progress = new RouteProgress(points);
        progress.Update(new Vec3(5, 0, 0));
        Assert.Equal(5, progress.AlongMetres);
        Assert.Equal(1, progress.NextIndex);
    }

    [Fact]
    public void SinglePointNeedsFiveMetreProximityAndStaysPassed()
    {
        var progress = new RouteProgress(new[] { new Vec3(0, 0, 0) });
        Assert.Equal(0, progress.LengthMetres);
        progress.Update(new Vec3(0, 6, 0));
        Assert.Equal(1, progress.PointsLeft);
        progress.Update(new Vec3(3, 4, 0));
        Assert.Equal(0, progress.PointsLeft);
        progress.Update(new Vec3(100, 0, 0));
        Assert.Equal(1, progress.NextIndex);
    }

    [Fact]
    public void DuplicatePointsAndVerticalSegmentsHaveFiniteProgress()
    {
        var progress = new RouteProgress(new[] { new Vec3(0, 0, 0), new Vec3(0, 0, 0), new Vec3(0, 10, 0) });
        progress.Update(new Vec3(0, 5, 0));
        Assert.Equal(5, progress.AlongMetres);
        Assert.Equal(2, progress.NextIndex);
        Assert.Throws<ArgumentException>(() => new RouteProgress(Array.Empty<Vec3>()));
    }

    [Fact]
    public void RouteCrossingItselfDoesNotPullProgressForward()
    {
        // The route loops round a block and passes back through its own start (review case).
        var points = new[] { new Vec3(0, 0, 0), new Vec3(40, 0, 0), new Vec3(40, 0, 40), new Vec3(0, 0, 40), new Vec3(0, 0, -40), new Vec3(-40, 0, -40) };
        var progress = new RouteProgress(points);
        progress.Update(new Vec3(0, 0, 0));
        Assert.Equal(0, progress.AlongMetres, 6);
        Assert.Equal(1, progress.NextIndex);
        progress.Update(new Vec3(20, 0, 0.5f));
        Assert.Equal(20, progress.AlongMetres, 6);
        // Driving on round the loop in 10 m steps keeps counting, and the later pass through the start is found in its turn.
        for (var along = 30; along <= 180; along += 10)
            progress.Update(PointAlong(points, along));
        Assert.Equal(180, progress.AlongMetres, 6);
    }

    private static Vec3 PointAlong(Vec3[] points, double along)
    {
        for (var i = 0; i + 1 < points.Length; i++)
        {
            var length = Vec3.Distance(points[i], points[i + 1]);
            if (along <= length)
            {
                var t = (float)(along / length);
                return points[i] + (points[i + 1] - points[i]) * t;
            }
            along -= length;
        }
        return points[points.Length - 1];
    }

    [Fact]
    public void OneUpdateAdvancesAtMostTheLimit()
    {
        var points = new[] { new Vec3(0, 0, 0), new Vec3(100, 0, 0) };
        var progress = new RouteProgress(points, maxAdvanceMetres: 25);
        progress.Update(new Vec3(80, 0, 0));
        Assert.Equal(25, progress.AlongMetres, 6);
        progress.Update(new Vec3(80, 0, 0));
        Assert.Equal(50, progress.AlongMetres, 6);
    }

    [Fact]
    public void CatchesUpAfterAnOversizedJumpOnALongSegment()
    {
        // Review case: a hitch moves the car far along one long segment; progress must recover, not freeze.
        var progress = new RouteProgress(new[] { new Vec3(0, 0, 0), new Vec3(100, 0, 0), new Vec3(200, 0, 0) });
        foreach (var x in new[] { 0f, 30f, 40f, 80f, 100f, 150f, 200f, 200f, 200f, 200f, 200f })
            progress.Update(new Vec3(x, 0, 0));
        Assert.Equal(200, progress.AlongMetres, 6);
        Assert.Equal(0, progress.PointsLeft);
    }
}
