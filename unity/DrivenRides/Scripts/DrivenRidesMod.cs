#nullable enable
using System;
using System.Threading.Tasks;
using BAModAPI;

[assembly: RegisterModClass(typeof(DrivenRides.DrivenRidesMod))]
[assembly: RegisterModClass(typeof(DrivenRides.DrivenRidesCity))]

namespace DrivenRides
{
    // Loads once at game start: the options page and the Harmony patches.
    [ModEntryOnInitializationLoad]
    public sealed class DrivenRidesMod : IModBigAmbitions
    {
        public const string Version = "0.4.1";
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
}
