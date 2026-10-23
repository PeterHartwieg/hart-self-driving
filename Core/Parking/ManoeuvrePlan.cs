using System;
using System.Collections.Generic;
using DrivenRides.Core.Drive;

namespace DrivenRides.Core.Parking
{
    // Local, signed travel; deliberately independent of DrivePlan, lane offsets and road progress.
    public readonly struct ManoeuvrePoint
    {
        public readonly CarPose Pose;
        public readonly int Direction;
        public ManoeuvrePoint(CarPose pose, int direction) { Pose = pose; Direction = direction; }
    }

    public sealed class ManoeuvrePlan
    {
        public IReadOnlyList<ManoeuvrePoint> Points { get; }
        public CarPose Goal { get; }
        public float Length { get; }
        public bool RoadEntry { get; }
        public ManoeuvrePlan(IReadOnlyList<ManoeuvrePoint> points, CarPose goal, bool roadEntry = false)
        {
            Points = new List<ManoeuvrePoint>(points).AsReadOnly();
            Goal = goal;
            RoadEntry = roadEntry;
            for (var i = 1; i < points.Count; i++) Length += Distance(points[i - 1].Pose, points[i].Pose);
        }
        public static float Distance(CarPose a, CarPose b) => (float)Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
        public static float Angle(float a) => (float)Math.Atan2(Math.Sin(a), Math.Cos(a));
        public static Box Body(CarSpec car, CarPose p, float margin = 0f)
        {
            var s = (float)Math.Sin(p.Heading); var c = (float)Math.Cos(p.Heading);
            return new Box(p.X + car.BodyCentre.X * c + car.BodyCentre.Z * s,
                p.Z - car.BodyCentre.X * s + car.BodyCentre.Z * c, p.Heading, car.BodyHalf.X + margin, car.BodyHalf.Z + margin);
        }
    }

    // A snapshot of local obstacles, with optional road/parking-area constraints. No Unity types or saved state.
    public sealed class ManoeuvreSpace
    {
        public readonly List<Box> Obstacles = new List<Box>();
        public Func<CarPose, bool>? Allowed;
        public Func<CarPose, float>? Estimate;
        public float MinX, MaxX, MinZ, MaxZ;
        public float Margin = 0.2f;
        private Dictionary<(int X,int Z),List<Box>>? cells;
        // Freeze the broad phase for this search snapshot; rebuild after changing Obstacles.
        public void PrepareObstacles()
        {
            cells = new Dictionary<(int,int),List<Box>>();
            foreach(var box in Obstacles)
                for(var x=Cell(box.X-box.ExtentX); x<=Cell(box.X+box.ExtentX); x++)
                    for(var z=Cell(box.Z-box.ExtentZ); z<=Cell(box.Z+box.ExtentZ); z++)
                    {
                        if(!cells.TryGetValue((x,z),out var list)) cells[(x,z)]=list=new List<Box>();
                        list.Add(box);
                    }
        }
        private static int Cell(float value) => (int)Math.Floor(value/6f);
        public bool Clear(CarSpec car, CarPose pose)
        {
            return Allowed?.Invoke(pose) != false && ClearBody(car, pose, Margin);
        }
        // Execution may use the planning buffer to absorb tracking error while retaining the actual body check.
        public bool ClearBody(CarSpec car, CarPose pose, float margin = 0f)
        {
            if (pose.X < MinX || pose.X > MaxX || pose.Z < MinZ || pose.Z > MaxZ) return false;
            var body = ManoeuvrePlan.Body(car, pose, margin);
            if (cells == null) { foreach (var obstacle in Obstacles) if (body.Overlaps(obstacle)) return false; }
            else for(var x=Cell(body.X-body.ExtentX); x<=Cell(body.X+body.ExtentX); x++)
                for(var z=Cell(body.Z-body.ExtentZ); z<=Cell(body.Z+body.ExtentZ); z++)
                    if(cells.TryGetValue((x,z),out var nearby)) foreach(var obstacle in nearby)
                        if(body.Overlaps(obstacle)) return false;
            return true;
        }
    }
}
