using System;
using System.Collections.Generic;
using DrivenRides.Core.Drive;
using DrivenRides.Core.Routing;

namespace DrivenRides.Core.Parking
{
    // A local surface snapshot, also used as a coarse distance field to guide pose-space search through an aisle.
    // It never supplies car steering: every accepted primitive is still swept with the actual car model.
    public sealed class ManoeuvreGrid
    {
        public readonly float MinX, MinZ, Cell;
        public readonly int Width, Height;
        private readonly bool[] clear;
        private readonly float[] distance;
        public ManoeuvreGrid(float minX, float minZ, int width, int height, float cell = 0.5f)
        {
            MinX = minX; MinZ = minZ; Width = width; Height = height; Cell = cell;
            clear = new bool[width * height]; distance = new float[clear.Length];
        }
        public void Set(int x, int z, bool value) => clear[z * Width + x] = value;
        // Paint surface geometry into the local snapshot. Blocking wins when the caller applies exclusions last.
        public void Triangle(Vec3 a, Vec3 b, Vec3 c, bool value, bool conservative = false)
        {
            var minX = Math.Max(0, (int)Math.Floor((Math.Min(a.X, Math.Min(b.X,c.X))-MinX)/Cell));
            var maxX = Math.Min(Width-1, (int)Math.Floor((Math.Max(a.X, Math.Max(b.X,c.X))-MinX)/Cell));
            var minZ = Math.Max(0, (int)Math.Floor((Math.Min(a.Z, Math.Min(b.Z,c.Z))-MinZ)/Cell));
            var maxZ = Math.Min(Height-1, (int)Math.Floor((Math.Max(a.Z, Math.Max(b.Z,c.Z))-MinZ)/Cell));
            var area = Cross(b.X-a.X,b.Z-a.Z,c.X-a.X,c.Z-a.Z);
            if (Math.Abs(area) < 0.0001f) return;
            // Cell/triangle separating axes: a curb narrower than a cell must still block every cell it touches.
            var abReach=conservative ? Cell/2*(Math.Abs(b.X-a.X)+Math.Abs(b.Z-a.Z)) : 0;
            var bcReach=conservative ? Cell/2*(Math.Abs(c.X-b.X)+Math.Abs(c.Z-b.Z)) : 0;
            var caReach=conservative ? Cell/2*(Math.Abs(a.X-c.X)+Math.Abs(a.Z-c.Z)) : 0;
            for (var z=minZ; z<=maxZ; z++) for (var x=minX; x<=maxX; x++)
            {
                var px=X(x); var pz=Z(z);
                var ab=Cross(b.X-a.X,b.Z-a.Z,px-a.X,pz-a.Z);
                var bc=Cross(c.X-b.X,c.Z-b.Z,px-b.X,pz-b.Z);
                var ca=Cross(a.X-c.X,a.Z-c.Z,px-c.X,pz-c.Z);
                if (area > 0 ? ab>=-abReach-.0001f && bc>=-bcReach-.0001f && ca>=-caReach-.0001f
                    : ab<=abReach+.0001f && bc<=bcReach+.0001f && ca<=caReach+.0001f)
                    Set(x,z,value);
            }
        }
        private static float Cross(float ax,float az,float bx,float bz) => ax*bz-az*bx;
        public float X(int x) => MinX + (x + 0.5f) * Cell;
        public float Z(int z) => MinZ + (z + 0.5f) * Cell;
        public bool At(float x, float z) => Index(x, z, out var i) && clear[i];
        public string[] Rows()
        {
            var rows = new string[Height];
            for (var z = 0; z < Height; z++)
            {
                var row = new char[Width]; for (var x = 0; x < Width; x++) row[x] = clear[z * Width + x] ? '.' : '#';
                rows[z] = new string(row);
            }
            return rows;
        }
        private bool Index(float x, float z, out int index)
        {
            var ix = (int)Math.Floor((x - MinX) / Cell); var iz = (int)Math.Floor((z - MinZ) / Cell);
            index = iz * Width + ix; return ix >= 0 && iz >= 0 && ix < Width && iz < Height;
        }
        public bool Contains(CarSpec car, CarPose pose, bool tyresOnly = false, Box? permittedOverhang = null, float bodyMargin = 0)
        {
            var b = tyresOnly ? new Box(pose.X + (float)Math.Sin(pose.Heading)*car.Wheelbase/2,
                pose.Z + (float)Math.Cos(pose.Heading)*car.Wheelbase/2,pose.Heading,
                car.TrackWidth>0 ? car.TrackWidth/2+.1f : car.BodyHalf.X,car.Wheelbase/2) : ManoeuvrePlan.Body(car, pose,bodyMargin);
            // Sample across the whole footprint so a thin pavement barrier cannot lie between the four wheels.
            var across = (int)Math.Ceiling(b.HalfWidth * 2 / (Cell * 0.75f));
            var along = (int)Math.Ceiling(b.HalfLength * 2 / (Cell * 0.75f));
            for (var ix = 0; ix <= across; ix++)
                for (var iz = 0; iz <= along; iz++)
                {
                    var x = -b.HalfWidth + 2 * b.HalfWidth * ix / across;
                    var z = -b.HalfLength + 2 * b.HalfLength * iz / along;
                    var px=b.X+x*b.Cos+z*b.Sin; var pz=b.Z-x*b.Sin+z*b.Cos;
                    if (At(px,pz)) continue;
                    if(permittedOverhang is Box allowed)
                    {
                        var dx=px-allowed.X; var dz=pz-allowed.Z;
                        if(Math.Abs(dx*allowed.Cos-dz*allowed.Sin)<=allowed.HalfWidth
                            && Math.Abs(dx*allowed.Sin+dz*allowed.Cos)<=allowed.HalfLength) continue;
                    }
                    return false;
                }
            return true;
        }
        public void Guide(CarPose goal, IReadOnlyList<Box> obstacles)
        {
            for (var i = 0; i < distance.Length; i++) distance[i] = float.PositiveInfinity;
            if (!Index(goal.X, goal.Z, out var target) || !clear[target]) return;
            var open = new SortedSet<(float Cost, int Index)> { (0, target) }; distance[target] = 0;
            var walkable = (bool[])clear.Clone();
            foreach (var box in obstacles)
            {
                var hx=Math.Abs(box.Cos)*box.HalfWidth+Math.Abs(box.Sin)*box.HalfLength+Cell/2;
                var hz=Math.Abs(box.Sin)*box.HalfWidth+Math.Abs(box.Cos)*box.HalfLength+Cell/2;
                var minX=Math.Max(0,(int)Math.Floor((box.X-hx-MinX)/Cell));
                var maxX=Math.Min(Width-1,(int)Math.Floor((box.X+hx-MinX)/Cell));
                var minZ=Math.Max(0,(int)Math.Floor((box.Z-hz-MinZ)/Cell));
                var maxZ=Math.Min(Height-1,(int)Math.Floor((box.Z+hz-MinZ)/Cell));
                for (var z=minZ; z<=maxZ; z++) for(var x=minX; x<=maxX; x++)
                    if (walkable[z*Width+x] && box.Overlaps(new Box(X(x),Z(z),0,Cell/2,Cell/2))) walkable[z*Width+x]=false;
            }
            while (open.Count > 0)
            {
                var current = open.Min; open.Remove(current);
                var x = current.Index % Width; var z = current.Index / Width;
                for (var dz = -1; dz <= 1; dz++) for (var dx = -1; dx <= 1; dx++)
                {
                    var nx = x + dx; var nz = z + dz;
                    if (nx < 0 || nz < 0 || nx >= Width || nz >= Height || (dx == 0 && dz == 0)) continue;
                    var next = nz * Width + nx;
                    if (!walkable[next] || !walkable[z * Width + nx] || !walkable[nz * Width + x]) continue;
                    var cost = current.Cost + Cell * (dx == 0 || dz == 0 ? 1 : 1.414214f);
                    if (cost >= distance[next]) continue;
                    open.Remove((distance[next], next)); distance[next] = cost; open.Add((cost, next));
                }
            }
        }
        public float Estimate(CarPose pose) => Index(pose.X, pose.Z, out var index) ? distance[index] : float.PositiveInfinity;
    }
}
