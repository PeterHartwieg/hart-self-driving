#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BAModAPI;
using UI.Notification;
using UnityEngine;

[assembly: RegisterModClass(typeof(DrivenRides.DrivenRidesMod))]
[assembly: RegisterModClass(typeof(DrivenRides.DrivenRidesCity))]

namespace DrivenRides
{
    // Loads once at game start: the options page and the Harmony patches.
    [ModEntryOnInitializationLoad]
    public sealed class DrivenRidesMod : IModBigAmbitions
    {
        public const string Version = "0.1.95-traffic28";

        public string[] RelativeAssetBundlePaths => Array.Empty<string>();

        public Task OnLoadAsync(ModContext context)
        {
            Log.Init(context.Logger);
            Log.Info($"Version {Version} loading.");
            try { Options.Register(context.ModId); }
            catch (Exception ex) { Log.Error("Options: " + ex); }
            Patches.Apply();
            // The traffic code's first calls compiled now, not in the first ride's physics steps (review round 11:
            // each T3 session's first ride had one decision of 13 to 18 ms).
            try
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var decisions = DrivenRides.Core.Drive.TrafficWarmUp.Run();
                Log.Info($"Traffic warm-up: {decisions} decisions in {watch.Elapsed.TotalMilliseconds:0} ms.");
            }
            catch (Exception ex) { Log.Error("Traffic warm-up: " + ex); }
            return Task.CompletedTask;
        }

        public Task OnUnloadAsync()
        {
            Ride.CancelActive("mod unload");
            Patches.Remove();
            Options.Unregister();
            return Task.CompletedTask;
        }
    }

    internal static class Log
    {
        private static IModLogger? logger;

        public static void Init(IModLogger modLogger) => logger = modLogger;

        // The game's mod logger adds its own "[Mod:Driven Rides]" prefix.
        public static void Info(string message)
        {
            if (logger != null) logger.Info(message);
            else Debug.Log("[Driven Rides] " + message);
        }

        public static void Error(string message)
        {
            if (logger != null) logger.Error(message);
            else Debug.LogError("[Driven Rides] " + message);
        }

        public static void Error(Exception exception) => Error(exception.ToString());
    }

    internal static class Notice
    {
        public static void Show(string message)
        {
            try
            {
                Notifications.Show(NotificationType.Info, "drivenrides:notification",
                    new Dictionary<string, string> { { "message", message } }, 5f, trackOnSaveGame: false);
            }
            catch (Exception ex) { Log.Error("Notice: " + ex.Message); }
        }
    }
}
