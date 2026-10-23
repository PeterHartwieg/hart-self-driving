using System;
using System.Linq;
using DrivenRides.Core.Drive;
using DrivenRides.Core.Parking;
using DrivenRides.Core.Routing;
using Xunit;

namespace DrivenRides.Core.Tests;

public sealed class DepartureTests
{
    private static CarSpec Car => new("test", 2.7f, _ => 35f, new Vec3(0, .6f, 1.2f), new Vec3(.9f, .6f, 2.2f), 50, 1.6f, 140);
    [Fact]
    public void FindsEntryInMiddleOfLongRoadWithoutAConnectionToTheParkingBay()
    {
        var graph = new LaneGraph(new[] {new Vec3(0,0,-100), new Vec3(0,0,100)}, new[] {new[] {1}, Array.Empty<int>()},
            new[] {1,1}, new[] {false,false}, new[] {50f,50f});
        var entries = DepartureEntryPicker.Pick(graph, new Vec3(20,0,0), Car, 0);
        Assert.NotEmpty(entries);
        Assert.Equal(0, entries[0].From);
        Assert.Equal(0, entries[0].Heading);
        Assert.InRange(entries[0].Distance, 19.9f, 20.1f);
        Assert.Empty(DepartureEntryPicker.Pick(graph, new Vec3(20,4,0), Car, 0));
        Assert.Empty(DepartureEntryPicker.Pick(graph, new Vec3(20,0,0), Car, 0, blocked: (_, _) => true));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void LeavesPerpendicularBayBetweenCarsAndJoinsInRoadDirection(int facing)
    {
        var start = new CarPose(12, 0, facing * MathF.PI / 2);
        var goal = new CarPose(0, 12, 0);
        var world = new ManoeuvreSpace {MinX=-8, MaxX=22, MinZ=-15, MaxZ=30};
        // Model body centres differ for nose-in and backed-in parking; leave the same physical neighbouring bays.
        world.Obstacles.Add(new Box(12 + facing * 1.2f, -3.2f, MathF.PI/2, .9f, 2.2f));
        world.Obstacles.Add(new Box(12 + facing * 1.2f, 3.2f, MathF.PI/2, .9f, 2.2f));
        var planner = new ManoeuvrePlanner(Car, world, start, goal, roadEntry:true);
        while (!planner.Done) planner.Step();
        Assert.True(planner.Result != null, planner.Reason);
        var driver = new ManoeuvreDriver(Car, planner.Result!);
        var model = Car.Model(start.X, start.Z, start.Heading);
        for (var i = 0; i < 15000 && !driver.Done && driver.Failure.Length == 0; i++)
        {
            var p = new CarPose((float)model.X,(float)model.Z,(float)model.Heading,(float)model.Speed,(float)model.Steer);
            Assert.All(world.Obstacles, b => Assert.False(ManoeuvrePlan.Body(Car,p).Overlaps(b)));
            model.Step(driver.Step(p,false), .02f);
        }
        Assert.True(driver.Done,driver.Failure);
        Assert.InRange(Math.Abs(model.X),0,.65);
        Assert.InRange(Math.Abs(ManoeuvrePlan.Angle((float)model.Heading)),0,.18);
    }

    [Fact]
    public void SurfaceGuideUsesDrivewayAndCannotCutAcrossPavement()
    {
        var grid = new ManoeuvreGrid(-15,-15,60,60);
        for (var z=0; z<60; z++) for(var x=0;x<60;x++)
            grid.Set(x,z, Math.Abs(grid.X(x)) > 1 || grid.Z(z) > 7);
        var goal = new CarPose(-8,0,0);
        grid.Guide(goal,Array.Empty<Box>());
        Assert.True(grid.Estimate(new CarPose(8,0,0)) > 20);
        Assert.False(grid.Contains(Car,new CarPose(0,0,0)));
        Assert.True(grid.Contains(Car,new CarPose(0,9,MathF.PI/2)));
    }

    [Fact]
    public void DisconnectedSurfaceHasNoSuggestedExit()
    {
        var grid = new ManoeuvreGrid(-10,-10,40,40);
        for(var z=0;z<40;z++) for(var x=0;x<40;x++) grid.Set(x,z,Math.Abs(grid.X(x))>1);
        grid.Guide(new CarPose(-5,0,0),Array.Empty<Box>());
        Assert.True(float.IsPositiveInfinity(grid.Estimate(new CarPose(5,0,0))));
    }

    [Fact]
    public void SurfaceTrianglesRespectBothWindingsBoundsAndCurbs()
    {
        var grid = new ManoeuvreGrid(-10,-10,40,40);
        grid.Triangle(new Vec3(-20,0,-20),new Vec3(20,0,-20),new Vec3(20,0,20),true);
        grid.Triangle(new Vec3(-20,0,-20),new Vec3(-20,0,20),new Vec3(20,0,20),true);
        Assert.True(grid.At(0,0)); Assert.True(grid.At(-9,9));
        grid.Triangle(new Vec3(-1,0,-20),new Vec3(1,0,-20),new Vec3(1,0,20),false);
        grid.Triangle(new Vec3(-1,0,-20),new Vec3(-1,0,20),new Vec3(1,0,20),false);
        Assert.False(grid.At(0,0)); Assert.True(grid.At(5,0));
        grid.Guide(new CarPose(-5,0,0),Array.Empty<Box>());
        Assert.True(float.IsPositiveInfinity(grid.Estimate(new CarPose(5,0,0))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LeavesBayThroughOffsetDrivewayInsteadOfCrossingTheSidewalk(bool reversed)
    {
        var grid = new ManoeuvreGrid(-12,-6,150,60);
        for(var z=0; z<grid.Height; z++) for(var x=0; x<grid.Width; x++)
        {
            var px=grid.X(x); var pz=grid.Z(z);
            grid.Set(x,z,(pz>=-2 && pz<13) || (px>30 && px<42 && pz>=13 && pz<17) || pz>=17);
        }
        var world = new ManoeuvreSpace {MinX=-12,MaxX=63,MinZ=-6,MaxZ=24};
        world.Obstacles.Add(new Box(-3.2f,1.2f,0,.9f,2.2f));
        world.Obstacles.Add(new Box(3.2f,1.2f,0,.9f,2.2f));
        var start=reversed ? new CarPose(0,2.4f,MathF.PI) : new CarPose(0,0,0); var goal=new CarPose(48,20,MathF.PI/2);
        grid.Guide(goal,world.Obstacles); world.Allowed=p=>grid.Contains(Car,p,bodyMargin:.2f); world.Estimate=grid.Estimate;
        var planner=new ManoeuvrePlanner(Car,world,start,goal,roadEntry:true);
        while(!planner.Done) planner.Step();
        Assert.True(planner.Result!=null,planner.Reason);
        Assert.All(planner.Result!.Points,p=>Assert.True(world.Clear(Car,p.Pose)));
        Assert.Contains(planner.Result.Points,p=>p.Pose.X>30 && p.Pose.X<42 && p.Pose.Z>13 && p.Pose.Z<17);
        var driver=new ManoeuvreDriver(Car,planner.Result); var model=Car.Model(start.X,start.Z,start.Heading);
        for(var i=0;i<15000 && !driver.Done && driver.Failure.Length==0;i++)
        {
            var pose=new CarPose((float)model.X,(float)model.Z,(float)model.Heading,(float)model.Speed,(float)model.Steer);
            // Plan with the runtime clearance buffer; judge the actual body rather than that padded envelope.
            Assert.True(grid.Contains(Car,pose),"The tracked body crossed the driveway boundary");
            Assert.All(world.Obstacles,b=>Assert.False(ManoeuvrePlan.Body(Car,pose).Overlaps(b)));
            model.Step(driver.Step(pose,false),.02f);
        }
        Assert.True(driver.Done,driver.Failure);
    }

    [Fact]
    public void ParkedBumperCanOverhangCurbButTyresCannotCrossIt()
    {
        var grid=new ManoeuvreGrid(-5,-5,40,40);
        for(var z=0;z<grid.Height;z++) for(var x=0;x<grid.Width;x++) grid.Set(x,z,grid.Z(z)>=-.5f);
        var pose=new CarPose(0,0,0);
        Assert.False(grid.Contains(Car,pose));
        Assert.True(grid.Contains(Car,pose,tyresOnly:true));
        Assert.False(grid.Contains(Car,new CarPose(0,-1,0),tyresOnly:true));
        var original=ManoeuvrePlan.Body(Car,pose,.35f);
        Assert.True(grid.Contains(Car,pose,permittedOverhang:original,bodyMargin:.2f));
        Assert.False(grid.Contains(Car,new CarPose(5,0,0),permittedOverhang:original,bodyMargin:.2f));
    }

    [Fact]
    public void IndexedBodyClearanceMatchesAllObstacleChecks()
    {
        var world=new ManoeuvreSpace {MinX=-40,MaxX=40,MinZ=-40,MaxZ=40};
        var random=new Random(8821);
        for(var i=0;i<60;i++) world.Obstacles.Add(new Box(random.Next(-35,35),random.Next(-35,35),
            (float)random.NextDouble()*6.28f, .5f+(float)random.NextDouble()*4,.5f+(float)random.NextDouble()*12));
        var samples=Enumerable.Range(0,1000).Select(_=>new CarPose(random.Next(-40,40),random.Next(-40,40),
            (float)random.NextDouble()*6.28f)).ToArray();
        var expected=samples.Select(p=>world.Clear(Car,p)).ToArray();
        world.PrepareObstacles();
        Assert.Equal(expected,samples.Select(p=>world.Clear(Car,p)).ToArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RoadHandoffStopsInsideToleranceWithoutChasingFinalCentimetres(bool roadEntry)
    {
        var points=Enumerable.Range(0,21).Select(i=>new ManoeuvrePoint(new CarPose(0,8+i*.1f,0),1)).ToList();
        var plan=new ManoeuvrePlan(points,new CarPose(0,10,0),roadEntry);
        var driver=new ManoeuvreDriver(Car,plan);
        driver.Step(new CarPose(.15f,9.8f,.1f),false);
        Assert.Equal(roadEntry,driver.Done);
        var misaligned=new ManoeuvreDriver(Car,plan);
        misaligned.Step(new CarPose(.15f,9.8f,.3f),false);
        Assert.False(misaligned.Done);
    }

    [Fact]
    public void TrackingValidationChecksTheDrivenBodyAndHonoursCancellation()
    {
        var start=new CarPose(0,0,0); var goal=new CarPose(0,10,0);
        var space=new ManoeuvreSpace {MinX=-10,MaxX=10,MinZ=-10,MaxZ=20};
        var planner=new ManoeuvrePlanner(Car,space,start,goal,roadEntry:true);
        while(!planner.Done) planner.Step();
        Assert.NotNull(planner.Result);
        Assert.Equal("",ManoeuvreDriver.Validate(Car,planner.Result!,p=>space.ClearBody(Car,p)));
        // An obstacle discovered on the driven path must reject the candidate before execution.
        Assert.NotEmpty(ManoeuvreDriver.Validate(Car,planner.Result!,p=>p.Z<5));
        Assert.Equal("tracking check cancelled",ManoeuvreDriver.Validate(Car,planner.Result!,_=>true,()=>true));
    }

    [Fact]
    public void NarrowCurbsBlockTouchedCellsEvenWhenNoCellCentreTouchesThem()
    {
        var grid=new ManoeuvreGrid(-2,-2,8,8);
        var a=new Vec3(.05f,0,-2); var b=new Vec3(.15f,0,-2); var c=new Vec3(.15f,0,2); var d=new Vec3(.05f,0,2);
        grid.Triangle(a,b,c,true); grid.Triangle(a,c,d,true);
        Assert.False(grid.At(.25f,0));
        grid.Triangle(a,b,c,true,conservative:true); grid.Triangle(a,d,c,true,conservative:true);
        Assert.True(grid.At(.25f,0)); Assert.False(grid.At(.75f,0)); Assert.False(grid.At(-.25f,0));
    }
}
