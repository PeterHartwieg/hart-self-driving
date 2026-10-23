#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace DrivenRides.BuildEditor
{
    // Runs the production component with real Unity rendering/physics objects, no city or game process.
    public static class RouteLineChecks
    {
        public static void Run()
        {
            var code = 0;
            GameObject? root = null, road = null, vehicle = null;
            Material? material = null;
            try
            {
                var args = Environment.GetCommandLineArgs();
                var path = Path.GetFullPath(args[Array.IndexOf(args, "-routeAssembly") + 1]);
                Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(path)!, "Dependencies", "0Harmony.dll"));
                var assembly = Assembly.LoadFile(path);
                var type = assembly.GetType("DrivenRides.RouteLineView", true)!;
                root = new GameObject("Route line check");
                var view = root.AddComponent(type);
                // Unity resolves script components to its editor copy, as in the existing paint checks.
                type = view.GetType();
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var show = (Action<Polyline, float, Transform>)Delegate.CreateDelegate(typeof(Action<Polyline, float, Transform>),
                    view, type.GetMethod("Show", flags)!);
                var hide = (Action)Delegate.CreateDelegate(typeof(Action), view, type.GetMethod("Hide", flags)!);
                road = new GameObject("Road collider");
                var box = road.AddComponent<BoxCollider>();
                box.size = new Vector3(120, 1, 120);
                road.transform.position = new Vector3(0, -0.5f, 0);
                vehicle = new GameObject("Car pivot");
                vehicle.transform.position = new Vector3(0, 1, 0);
                Physics.SyncTransforms();
                var line = new Polyline(new List<Vec3> { new Vec3(0, 0.2f, 0), new Vec3(6, 0.2f, 0), new Vec3(6, 0.2f, 6) });
                show(line, 0, vehicle.transform);
                var r = root.GetComponentInChildren<LineRenderer>(true);
                Require(r != null && r.enabled, "No visible route");
                material = r.sharedMaterial;
                Require(r.positionCount == 7, "Incorrect initial sample count");
                Require(r.alignment == LineAlignment.TransformZ && Mathf.Abs(r.transform.forward.y) > 0.99f, "Ribbon is not road-facing");
                Require(r.widthMultiplier == 0.4f && r.shadowCastingMode == ShadowCastingMode.Off, "Width or shadow setup");
                Require(material.shader.name == "HDRP/Unlit" && material.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT"), "HDRP transparent material");
                Require(material.GetFloat("_ZWrite") == 0 && material.GetFloat("_ZTestTransparent") == (float)CompareFunction.LessEqual, "Depth setup");
                var blue = material.GetColor("_UnlitColor");
                Require(Mathf.Abs(blue.r - 47f / 255f) < 0.001f && blue.b == 1 && blue.a > 0.8f && blue.a < 0.9f, "Route colour");
                for (int i = 0; i < r.positionCount; i++) Require(Mathf.Abs(r.GetPosition(i).y - 0.06f) < 0.001f, "Road projection/lift");
                vehicle.transform.position = new Vector3(3, 1, 0);
                show(line, 3, vehicle.transform);
                Require(r.positionCount == 6 && r.GetPosition(0).x == 3 && r.GetPosition(1).x == 4, "Consumed prefix retained");
                Require(r.GetPosition(5).z == 6, "Destination lost");
                hide(); Require(!r.enabled, "Hide did not disable renderer");
                show(line, 3, vehicle.transform);
                Require(r.enabled && root.GetComponentsInChildren<LineRenderer>(true).Length == 1 && r.sharedMaterial == material, "Renderer/material not reused");
                // Profile normal frames and sample-boundary uploads through a typed delegate (reflection itself allocates).
                for (int i = 0; i < 100; i++) show(line, i % 5, vehicle.transform);
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 1000; i++) show(line, i % 5, vehicle.transform);
                var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Require(allocated == 0, "Steady update allocated " + allocated + " bytes");
                var reroute = new Polyline(new List<Vec3> { new Vec3(3, 0, 0), new Vec3(3, 0, -4) });
                show(reroute, 0, vehicle.transform);
                Require(r.positionCount == 3 && r.GetPosition(2).z == -4, "Reroute retained old destination");
                show(reroute, 4, vehicle.transform); Require(!r.enabled, "Completed path still visible");
                var longLine = new Polyline(new List<Vec3> { new Vec3(0, 0, 0), new Vec3(1000, 0, 0) });
                show(longLine, 0, vehicle.transform); Require(!r.enabled, "Unfinished height probes exposed stale route");
                for (int i = 0; i < 4; i++) show(longLine, 0, vehicle.transform);
                Require(r.enabled && r.positionCount == 501, "Bounded probes failed to finish long route");
                root.SetActive(false);
                Require(!r.gameObject.activeInHierarchy, "Inactive host left route visible");
                // Batch editor objects do not receive player-loop callbacks; exercise the callback explicitly.
                type.GetMethod("OnDisable", flags)!.Invoke(view, null);
                Require(!r.enabled, "Disable callback left renderer enabled");
                Debug.Log("[DrivenRidesRouteLineChecks] PASS: road height/lift, ribbon orientation, HDRP colour/depth, trim, reroute, completion, bounded probes, reuse, disable; zero bytes in 1,000 warm updates.");
            }
            catch (Exception ex) { code = 1; Debug.LogError("[DrivenRidesRouteLineChecks] FAIL: " + ex); }
            finally
            {
                if (root != null) Object.DestroyImmediate(root);
                if (material != null) Object.DestroyImmediate(material);
                if (vehicle != null) Object.DestroyImmediate(vehicle);
                if (road != null) Object.DestroyImmediate(road);
            }
            EditorApplication.Exit(code);
        }

        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
