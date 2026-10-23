#nullable enable
using System;
using System.Collections;
using DrivenRides.Core.Parking;
using DrivenRides.Core.Routing;
using UnityEngine;
using UnityEngine.Rendering;

namespace DrivenRides
{
    // Static city meshes discard their CPU copy. Read the rendered position/index buffers without changing
    // the mesh or adding colliders to the physics world. Only the requested submesh is copied.
    internal sealed class ParkingSurface
    {
        private readonly ManoeuvreGrid access, pavement;
        private bool complete = true;
        public ParkingSurface(ManoeuvreGrid grid)
        {
            access = new ManoeuvreGrid(grid.MinX,grid.MinZ,grid.Width,grid.Height,grid.Cell);
            pavement = new ManoeuvreGrid(grid.MinX,grid.MinZ,grid.Width,grid.Height,grid.Cell);
        }
        public bool AisleAt(float x,float z) => complete && access.At(x,z) && !pavement.At(x,z);
        public bool PavementAt(float x,float z) => pavement.At(x,z);
        public IEnumerator Read(float height)
        {
            var bounds = new Bounds(new Vector3(access.MinX+access.Width*access.Cell/2,height,access.MinZ+access.Height*access.Cell/2),
                new Vector3(access.Width*access.Cell,2,access.Height*access.Cell));
            foreach (var lane in UnityEngine.Object.FindObjectsOfType<ParkingLaneGenerator>())
            {
                var box = lane.GetComponent<BoxCollider>();
                if (box == null || !box.enabled || lane.isHandicapParking || !bounds.Intersects(box.bounds)) continue;
                // A local aisle beside a legal parking strip. Rendered pavement is subtracted below;
                // the extension alone never licenses crossing a sidewalk.
                var scale = lane.transform.lossyScale;
                var half = box.size/2 + new Vector3(10/Mathf.Abs(scale.x),0,10/Mathf.Abs(scale.z));
                Vec3 Point(float x,float z) => GleyGraph.ToVec(lane.transform.TransformPoint(box.center + new Vector3(x,0,z)));
                var a=Point(-half.x,-half.z); var b=Point(-half.x,half.z); var c=Point(half.x,half.z); var d=Point(half.x,-half.z);
                access.Triangle(a,b,c,true); access.Triangle(a,c,d,true);
            }
            foreach (var renderer in UnityEngine.Object.FindObjectsOfType<MeshRenderer>())
            {
                if (!bounds.Intersects(renderer.bounds)) continue;
                var layer = LayerMask.LayerToName(renderer.gameObject.layer);
                if (layer != "Sidewalk" && layer != "Roads") continue;
                for (var sub=0; sub<renderer.sharedMaterials.Length; sub++)
                {
                    var name=renderer.sharedMaterials[sub]?.name ?? "";
                    var curb=name.Contains("RoadSideCurb") || name.Contains("Grass") || name.Contains("Cobblestone");
                    var asphalt=name.Contains("Asphalt") || name == "M_Street" || name == "M_PierAvenue";
                    if (!curb && !asphalt) continue;
                    Vector3[]? points=null;
                    try { points=Triangles(renderer,sub); }
                    catch (Exception ex) { Log.Trace("Parking surface: " + ex.Message); }
                    if (points == null) { complete=false; continue; }
                    for (var i=0; i<points.Length; i+=3)
                    {
                        var a=points[i]; var b=points[i+1]; var c=points[i+2];
                        if (Mathf.Abs(a.y-height)>.3f || Mathf.Abs(b.y-height)>.3f || Mathf.Abs(c.y-height)>.3f) continue;
                        if (Mathf.Abs(Vector3.Cross(b-a,c-a).normalized.y)<.7f) continue;
                        (curb ? pavement : access).Triangle(GleyGraph.ToVec(a),GleyGraph.ToVec(b),GleyGraph.ToVec(c),true,conservative:curb);
                    }
                }
                yield return null;
            }
            if (!complete) Log.Trace("Parking surface: incomplete geometry; retaining pedestrian restrictions.");
        }

        public static Vector3[]? Triangles(MeshRenderer renderer, int material)
        {
            var mesh = renderer.GetComponent<MeshFilter>()?.sharedMesh;
            if (mesh == null) return null;
            var sub = material + renderer.subMeshStartIndex;
            if (sub >= mesh.subMeshCount || mesh.GetTopology(sub) != MeshTopology.Triangles) return null;
            var descriptor = mesh.GetSubMesh(sub);
            if (descriptor.indexCount > 1500000 || descriptor.vertexCount > 500000) return null;
            var matrix = renderer.localToWorldMatrix;
            if (mesh.isReadable)
            {
                var vertices = mesh.vertices; var indices = mesh.GetTriangles(sub);
                var result = new Vector3[indices.Length];
                for (var i = 0; i < result.Length; i++) result[i] = matrix.MultiplyPoint3x4(vertices[indices[i]]);
                return result;
            }
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null ||
                mesh.GetVertexAttributeFormat(VertexAttribute.Position) != VertexAttributeFormat.Float32 ||
                mesh.GetVertexAttributeDimension(VertexAttribute.Position) != 3) return null;
            var stream = mesh.GetVertexAttributeStream(VertexAttribute.Position);
            var stride = mesh.GetVertexBufferStride(stream);
            var offset = mesh.GetVertexAttributeOffset(VertexAttribute.Position);
            using var vertexBuffer = mesh.GetVertexBuffer(stream);
            using var indexBuffer = mesh.GetIndexBuffer();
            var bytes = new byte[descriptor.vertexCount * stride];
            vertexBuffer.GetData(bytes, 0, descriptor.firstVertex * stride, bytes.Length);
            var wide = mesh.indexFormat == IndexFormat.UInt32;
            var indexBytes = new byte[descriptor.indexCount * (wide ? 4 : 2)];
            indexBuffer.GetData(indexBytes, 0, descriptor.indexStart * (wide ? 4 : 2), indexBytes.Length);
            var points = new Vector3[descriptor.indexCount];
            for (var i = 0; i < points.Length; i++)
            {
                var index = wide ? (int)BitConverter.ToUInt32(indexBytes, i * 4) : BitConverter.ToUInt16(indexBytes, i * 2);
                var p = (index + descriptor.baseVertex - descriptor.firstVertex) * stride + offset;
                if (p < 0 || p + 12 > bytes.Length) return null;
                points[i] = matrix.MultiplyPoint3x4(new Vector3(BitConverter.ToSingle(bytes,p),
                    BitConverter.ToSingle(bytes,p+4), BitConverter.ToSingle(bytes,p+8)));
            }
            return points;
        }
    }
}
