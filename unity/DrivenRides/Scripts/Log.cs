#nullable enable
using System;
using BAModAPI;
using UnityEngine;

namespace DrivenRides
{
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

        // Preserve the legacy Info filtering contract as well as explicit Trace calls. These prefixes have
        // always been diagnostic-only; their wording is also consumed by the offline log tools.
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
}
