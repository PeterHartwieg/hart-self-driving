#nullable enable
using System;
using System.Threading.Tasks;
using BAModAPI;
using UnityEngine;

[assembly: RegisterModClass(typeof(DrivenRides.DrivenRidesMod))]

namespace DrivenRides
{
    // Loads once at game start.
    [ModEntryOnInitializationLoad]
    public sealed class DrivenRidesMod : IModBigAmbitions
    {
        public const string Version = "0.1.0";

        public string[] RelativeAssetBundlePaths => Array.Empty<string>();

        public Task OnLoadAsync(ModContext context)
        {
            Log.Init(context.Logger);
            Log.Info($"Version {Version} loading.");
            return Task.CompletedTask;
        }

        public Task OnUnloadAsync() => Task.CompletedTask;
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
}
