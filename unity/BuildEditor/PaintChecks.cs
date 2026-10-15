#nullable enable
using System;
using System.IO;
using System.Reflection;
using Data.VehicleColors;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DrivenRides.BuildEditor
{
    // Runs against the packaged DLL and real Unity/game components, without a city, player or save.
    public static class PaintChecks
    {
        public static void Run()
        {
            var code = 0;
            try
            {
                var args = Environment.GetCommandLineArgs();
                var path = Path.GetFullPath(args[Array.IndexOf(args, "-paintAssembly") + 1]);
                var dependencies = Path.Combine(Path.GetDirectoryName(path)!, "Dependencies");
                Assembly.LoadFrom(Path.Combine(dependencies, "0Harmony.dll"));
                Assembly.LoadFrom(Path.Combine(dependencies, "DrivenRides.Core.dll"));
                var assembly = Assembly.LoadFile(path);
                var strip = assembly.GetType("DrivenRides.NwhCar", true)!.GetMethod("Strip", BindingFlags.NonPublic | BindingFlags.Static)!;
                var copy = assembly.GetType("DrivenRides.Ride", true)!.GetMethod("KeepColour", BindingFlags.NonPublic | BindingFlags.Static)!;
                foreach (var tint in new[] { new Color32(20, 60, 220, 255), new Color32(25, 180, 50, 255), new Color32(240, 185, 30, 255) })
                    CheckRide(strip, copy, tint);
                Debug.Log("[DrivenRidesPaintChecks] PASS: three paints, boarding after stripping, all body renderers, inactive children, re-enable, drop-off, and missing-component failure.");
            }
            catch (Exception ex)
            {
                code = 1;
                Debug.LogError("[DrivenRidesPaintChecks] FAIL: " + ex);
            }
            EditorApplication.Exit(code);
        }

        private static void CheckRide(MethodInfo strip, MethodInfo copy, Color32 tint)
        {
            var source = new GameObject("Pickup");
            var ride = new GameObject("Ride");
            var leaving = new GameObject("Leaving");
            source.SetActive(false);
            ride.SetActive(false);
            leaving.SetActive(false);
            var paint = ScriptableObject.CreateInstance<VehicleColor>();
            paint.tint = tint;
            paint.fresnelColor = new Color32(120, 140, 160, 255);
            paint.fresnelPower = 0.37f;
            try
            {
                var pickup = AddBody(source);
                var riding = AddBody(ride);
                var released = AddBody(leaving);
                pickup.SetColor(paint);
                riding.SetDirtiness(0.65f);
                // An inactive child ensures stripping and copying don't overlook dormant paint components.
                riding.gameObject.SetActive(false);
                ride.AddComponent<RandomVehicleColor>();
                strip.Invoke(null, new object?[] { ride, null });
                Require(riding != null, "Stripping removed CarFeatures; boarding would retain the prefab's colour.");
                Require(ride.GetComponentInChildren<RandomVehicleColor>(true) == null, "Random paint survived stripping.");
                copy.Invoke(null, new object[] { source, ride, true });
                CheckPaint(riding!, paint, 0.65f);
                riding!.gameObject.SetActive(true);
                ride.SetActive(true);
                ride.SetActive(false);
                ride.SetActive(true);
                CheckPaint(riding, paint, 0.65f);
                copy.Invoke(null, new object[] { ride, leaving, false });
                CheckPaint(released, paint, 0f);
                Object.DestroyImmediate(released);
                try
                {
                    copy.Invoke(null, new object[] { ride, leaving, false });
                    throw new InvalidOperationException("A missing paint component was silently ignored.");
                }
                catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { }
            }
            finally
            {
                Object.DestroyImmediate(source);
                Object.DestroyImmediate(ride);
                Object.DestroyImmediate(leaving);
                Object.DestroyImmediate(paint);
            }
        }

        private static CarFeatures AddBody(GameObject root)
        {
            var body = new GameObject("Body");
            body.transform.SetParent(root.transform, false);
            var features = body.AddComponent<CarFeatures>();
            var panel = new GameObject("Second body panel");
            panel.transform.SetParent(body.transform, false);
            features.bodyMeshes = new Renderer[] { body.AddComponent<MeshRenderer>(), panel.AddComponent<MeshRenderer>() };
            return features;
        }

        private static void CheckPaint(CarFeatures features, VehicleColor paint, float dirtiness)
        {
            Require(features.VehicleColor == paint, "The car lost its paint reference.");
            var flags = BindingFlags.NonPublic | BindingFlags.Static;
            int Id(string name) => (int)typeof(CarFeatures).GetField(name, flags)!.GetValue(null)!;
            var block = new MaterialPropertyBlock();
            foreach (var renderer in features.bodyMeshes)
            {
                renderer.GetPropertyBlock(block);
                Require(block.GetColor(Id("TintId")) == (Color)paint.tint, "Rendered body tint changed.");
                Require(block.GetColor(Id("Color")) == (Color)paint.fresnelColor, "Rendered fresnel colour changed.");
                Require(Mathf.Approximately(block.GetFloat(Id("Power")), paint.fresnelPower), "Rendered fresnel power changed.");
                Require(Mathf.Approximately(block.GetFloat(Id("Dirtiness")), dirtiness), "Copying paint replaced another renderer property.");
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
