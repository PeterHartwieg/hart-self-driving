#nullable enable
using System;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using NwhController = NWH.VehiclePhysics2.VehicleController;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DrivenRides.BuildEditor
{
    // Exercises the packaged physics callback with a controlled failure, without a city, player or save.
    public static class DriveChecks
    {
        public static void Run()
        {
            var code = 0;
            GameObject? root = null;
            try
            {
                var args = Environment.GetCommandLineArgs();
                var path = Path.GetFullPath(args[Array.IndexOf(args, "-driveAssembly") + 1]);
                var dependencies = Path.Combine(Path.GetDirectoryName(path)!, "Dependencies");
                Assembly.LoadFrom(Path.Combine(dependencies, "0Harmony.dll"));
                Assembly.LoadFrom(Path.Combine(dependencies, "DrivenRides.Core.dll"));
                var assembly = Assembly.LoadFile(path);
                var driveType = assembly.GetType("DrivenRides.NwhDrive", true)!;
                var carType = assembly.GetType("DrivenRides.NwhCar", true)!;
                root = new GameObject("Drive failure check");
                root.SetActive(false);
                var vehicle = root.AddComponent<NwhController>();
                // AddComponent resolves the SDK's editor copy of the same script name. Use the packaged managed
                // instance here; the callback under test needs only its fields and the real car input component.
                var drive = FormatterServices.GetUninitializedObject(driveType);
                var car = Activator.CreateInstance(carType)!;
                carType.GetField("Root")!.SetValue(car, root);
                carType.GetField("Vehicle")!.SetValue(car, vehicle);
                // Deliberately omit Body: the physics step must fail and end once, with the car held.
                driveType.GetField("Car")!.SetValue(drive, car);
                // Only a non-null driver is needed to reach the missing-body failure, before any driver is used.
                var driverField = driveType.GetField("driver", BindingFlags.Instance | BindingFlags.NonPublic)!;
                driverField.SetValue(drive, FormatterServices.GetUninitializedObject(driverField.FieldType));
                driveType.GetField("started", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(drive, true);
                vehicle.input.Throttle = 1f;
                var step = driveType.GetMethod("FixedUpdate", BindingFlags.Instance | BindingFlags.NonPublic)!;
                // The expected error is exercised here, not a failure of this check.
                var logging = Debug.unityLogger.logEnabled;
                try { Debug.unityLogger.logEnabled = false; step.Invoke(drive, null); }
                finally { Debug.unityLogger.logEnabled = logging; }
                Require((bool)driveType.GetProperty("Ended")!.GetValue(drive)!, "The exceptional step did not end the drive.");
                Require(!(bool)driveType.GetProperty("Arrived")!.GetValue(drive)!, "A failed drive must use the vanilla finish, not claim arrival.");
                Require(((string)driveType.GetProperty("EndReason")!.GetValue(drive)!).StartsWith("drive failed:"), "The drive did not record its failure.");
                Require(vehicle.input.Throttle == 0f && vehicle.input.Handbrake == 1f, "The failed drive did not stop the car.");
                step.Invoke(drive, null); // A later physics step must stay ended without throwing again.
                Debug.Log("[DrivenRidesDriveChecks] PASS: packaged physics failure ends once, stops the car, and selects the vanilla completion path.");
            }
            catch (Exception ex) { code = 1; Debug.LogError("[DrivenRidesDriveChecks] FAIL: " + ex); }
            finally { if (root != null) Object.DestroyImmediate(root); }
            EditorApplication.Exit(code);
        }

        private static void Require(bool value, string why) { if (!value) throw new InvalidOperationException(why); }
    }
}
