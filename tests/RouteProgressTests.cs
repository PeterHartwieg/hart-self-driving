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
        Assert.Equal(70, progress.AlongMetres);
        Assert.Equal(8, progress.NextIndex);
        progress.Update(new Vec3(290, 0, 0));
        Assert.Equal(150, progress.AlongMetres);
        Assert.Equal(16, progress.NextIndex);
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
}
