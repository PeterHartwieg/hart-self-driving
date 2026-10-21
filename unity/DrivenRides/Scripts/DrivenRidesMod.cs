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
        public const string Version = "0.2.0-rc8";
        // The name players see; tools/rename-mod.sh changes it here and in the manifest, locales and README.
        public const string DisplayName = "HART. Self Driving";

        public string[] RelativeAssetBundlePaths => Array.Empty<string>();

        public Task OnLoadAsync(ModContext context)
        {
            Log.Init(context.Logger);
            Log.Info($"Version {Version} loading.");
            if (TestMode.On) Log.Info("Test mode: debug keys, the turn-test rig, traces and dumps are on.");
            try { Options.Register(context.ModId); }
            catch (Exception ex) { Log.Error("Options: " + ex); }
            Patches.Apply();
            // The traffic code's first calls compiled now, not in the first ride's physics steps (review round 11:
            // each T3 session's first ride had one decision of 13 to 18 ms).
            try
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var decisions = DrivenRides.Core.Drive.TrafficWarmUp.Run();
                Log.Trace($"Traffic warm-up: {decisions} decisions in {watch.Elapsed.TotalMilliseconds:0} ms.");
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

        // The game's mod logger adds its own "[Mod:<title>]" prefix.
        public static void Info(string message)
        {
            if (!TestMode.On && IsTrace(message)) return;
            if (logger != null) logger.Info(message);
            else Debug.Log("[" + DrivenRidesMod.DisplayName + "] " + message);
        }

        // Per-step and diagnostic lines: only in test mode (TestMode), so a normal game logs the startup, one line
        // as a ride starts and one as it ends, and errors.
        public static void Trace(string message)
        {
            if (TestMode.On) Info(message);
        }

        // The driving and planning code (NwhDrive, NwhPlanner) logs its per-step lines through Info with these
        // prefixes; outside test mode they are dropped here, so those files stay as their owners left them.
        private static readonly string[] TracePrefixes = { "NWH drive: ", "NWH planner: ", "NWH shape for " };

        public static void Error(string message)
        {
            if (logger != null) logger.Error(message);
            else Debug.LogError("[" + DrivenRidesMod.DisplayName + "] " + message);
        }

        public static void Error(Exception exception) => Error(exception.ToString());

        private static bool IsTrace(string message)
        {
            foreach (var prefix in TracePrefixes)
                if (message.StartsWith(prefix, StringComparison.Ordinal)) return true;
            return false;
        }
    }

    // Player-facing notices. Text shows a line from Locales/en.json; Show shows a raw English message and is for
    // test-mode texts only (debug keys, the rig, detailed reasons), which a normal game never meets.
    internal static class Notice
    {
        public static void Text(string key, params (string Key, string Value)[] values)
        {
            try
            {
                var data = new Dictionary<string, string>();
                foreach (var (k, v) in values) data[k] = v;
                Notifications.Show(NotificationType.Info, key, data, 5f, trackOnSaveGame: false);
            }
            catch (Exception ex) { Log.Error("Notice: " + ex.Message); }
        }

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
