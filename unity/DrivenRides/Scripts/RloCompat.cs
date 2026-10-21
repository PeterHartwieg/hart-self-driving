#nullable enable
using System;
using System.Reflection;
using GleyTrafficSystem;
using HarmonyLib;
using NwhController = NWH.VehiclePhysics2.VehicleController;

namespace DrivenRides
{
    // Real Life Overhaul, when it is loaded (docs/rlo-compat.md). RLO is found by its assembly's name and every part of
    // it is reached by reflection; without it nothing here patches, and every question below answers as if it weren't
    // there, so the mod plays exactly as it does alone.
    //
    // - Car tuning: RLO re-tunes the car the player sits in (Driving/CarTuning: power, gearing, no speed limiter,
    //   slower-building brakes, weight, springs, tyres, ABS). The own car's drive is measured and tuned on the car as the
    //   game made it, so RLO's tuning comes off for the trip (its own Restore) and its OnUpdate is skipped; at the end RLO
    //   is told it hasn't seen the car and puts the tuning back on the next frame.
    // - The police: a trip ends when RLO's police pull the player over or chase them, so the car stops instead of
    //   fleeing; no trip starts while the police, a driving test or prison hold the player.
    // - A broken-down car (RLO cuts its throttle through the keys, which a car driving itself doesn't use) doesn't drive
    //   itself.
    // - A passenger riding down the road isn't booked for walking in it (PlayerOffences.OnFoot).
    // - RLO's mouse look leaves the cursor free while the ride panel shows (ThirdPersonCamera.IsUiOpen).
    // - L does nothing in RLO's photo mode, which rolls the camera on it.
    // - A taxi RLO holds out of traffic (City Cabs) starts its ride from the lane beside it (Ride.TryCreate).
    internal static class RloCompat
    {
        private const string AssemblyName = "RealLifeOverhaul";

        private static bool tried;
        private static bool reported;
        private static bool tuningPatched;

        // Car tuning.
        private static PropertyInfo? tuningInstance;
        private static MethodInfo? tuningRestore;
        private static FieldInfo? tuningSeen;
        private static NwhController? held;

        // What RLO says of the player and their car.
        private static PropertyInfo? policeBusy;
        private static PropertyInfo? licenceActive;
        private static PropertyInfo? inPrison;
        private static PropertyInfo? photoMode;
        private static PropertyInfo? ownershipInstance;
        private static FieldInfo? ownershipCar;
        private static FieldInfo? ownershipCutting;

        // True once RLO has been found.
        public static bool Loaded { get; private set; }

        // At startup and again when a city is ready, since RLO may load after this mod. Each patch logs one Active or Not
        // active line: at startup only when RLO is already there, else at the first city.
        public static void TryPatch(Harmony harmony, bool final)
        {
            if (tried) return;
            var assembly = FindAssembly();
            if (assembly == null)
            {
                if (final && !reported) Log.Info("Real Life Overhaul: not loaded; its patches are not active.");
                reported |= final;
                return;
            }
            tried = true;
            reported = true;
            Loaded = true;
            Log.Info($"Real Life Overhaul {assembly.GetName().Version} loaded.");

            var tuning = assembly.GetType("RealLifeOverhaul.CarTuning", false);
            tuningInstance = tuning != null ? AccessTools.Property(tuning, "Instance") : null;
            tuningRestore = tuning != null ? AccessTools.Method(tuning, "Restore", Type.EmptyTypes) : null;
            tuningSeen = tuning != null ? AccessTools.Field(tuning, "_seen") : null;
            if (tuningInstance == null || tuningRestore == null || tuningSeen == null)
                Log.Error($"Patch RealLifeOverhaul.CarTuning.OnUpdate: Not active, CarTuning has changed (Instance {tuningInstance != null}, Restore {tuningRestore != null}, _seen {tuningSeen != null}).");
            else tuningPatched = Patch(harmony, tuning, "OnUpdate", Type.EmptyTypes, prefix: nameof(TuningPrefix));
            if (!tuningPatched) tuningRestore = null;

            Patch(harmony, assembly.GetType("RealLifeOverhaul.PlayerOffences", false), "OnFoot", new[] { typeof(UnityEngine.Vector3), typeof(float) }, prefix: nameof(OnFootPrefix));
            Patch(harmony, assembly.GetType("RealLifeOverhaul.ThirdPersonCamera", false), "IsUiOpen", Type.EmptyTypes, postfix: nameof(UiOpenPostfix));

            policeBusy = Static(assembly, "RealLifeOverhaul.PoliceAttention", "IsBusy");
            licenceActive = Static(assembly, "RealLifeOverhaul.LicenceDirector", "IsActive");
            inPrison = Static(assembly, "RealLifeOverhaul.Justice", "InPrison");
            photoMode = Static(assembly, "RealLifeOverhaul.Leisure.PhotoMode", "Active");
            var ownership = assembly.GetType("RealLifeOverhaul.CarOwnership", false);
            ownershipInstance = Static(assembly, "RealLifeOverhaul.CarOwnership", "Instance");
            ownershipCar = ownership != null ? AccessTools.Field(ownership, "_car") : null;
            ownershipCutting = ownership != null ? AccessTools.Field(ownership, "_cutting") : null;
            Log.Info($"Real Life Overhaul: police {Found(policeBusy)}, driving test {Found(licenceActive)}, prison {Found(inPrison)}, photo mode {Found(photoMode)}, breakdowns {Found(ownershipInstance != null && ownershipCar != null && ownershipCutting != null)}.");
        }

        // Patches.Remove unpatches everything; a later Apply starts again.
        public static void Forget()
        {
            tried = reported = tuningPatched = false;
            Loaded = false;
            held = null;
            tuningInstance = null;
            tuningRestore = null;
            tuningSeen = null;
            policeBusy = licenceActive = inPrison = photoMode = ownershipInstance = null;
            ownershipCar = ownershipCutting = null;
        }

        // ---------------------------------------------------------------- car tuning

        // The own car starts driving itself: RLO's tuning comes off now, before the car is measured.
        public static void Hold(NwhController vehicle)
        {
            if (tuningRestore == null) return;
            held = vehicle;
            var tuning = Get(tuningInstance);
            if (tuning == null) return;
            try
            {
                tuningRestore.Invoke(tuning, null);
                tuningSeen!.SetValue(tuning, null);
                Log.Info("Own car: Real Life Overhaul's car tuning taken off for the trip.");
            }
            catch (Exception ex) { Log.Error("Own car: taking Real Life Overhaul's car tuning off: " + (ex.InnerException ?? ex).Message); }
        }

        // The keys are the player's again: RLO puts its tuning back on its next frame.
        public static void Release()
        {
            if (held == null) return;
            held = null;
            var tuning = Get(tuningInstance);
            if (tuning == null) return;
            try
            {
                tuningSeen!.SetValue(tuning, null);
                Log.Info("Own car: Real Life Overhaul's car tuning handed back.");
            }
            catch (Exception ex) { Log.Error("Own car: handing Real Life Overhaul's car tuning back: " + ex.Message); }
        }

        // The city goes: the car with it.
        public static void Drop() => held = null;

        // CarTuning.OnUpdate, each frame: skipped while the own car drives itself.
        private static bool TuningPrefix() => held == null;

        // ---------------------------------------------------------------- the player

        // The police pulling the player over, chasing them after a crash, or holding them.
        public static bool PoliceBusy => Is(policeBusy);

        // A scene of RLO's holds the player: the police, a driving test, prison (RLO's own test for its taxis,
        // Wiring/TaxiLinks).
        public static bool PlayerHeld => Is(policeBusy) || Is(licenceActive) || Is(inPrison);

        public static bool PhotoMode => Is(photoMode);

        // RLO has this car broken down: it cuts the throttle of the player's keys (CarOwnership._cutting).
        public static bool BrokenDown(CarController car)
        {
            if (ownershipCutting == null) return false;
            try
            {
                var ownership = Get(ownershipInstance);
                return ownership != null && ReferenceEquals(ownershipCar!.GetValue(ownership), car) && ownershipCutting.GetValue(ownership) is true;
            }
            catch { return false; }
        }

        // PlayerOffences.OnFoot: a passenger carried down the road by a ride isn't walking in it. A time before the
        // next look makes RLO answer "no offence" by its own first test.
        private static void OnFootPrefix(ref float now)
        {
            if (Ride.OwnsPlayer) now = float.NegativeInfinity;
        }

        // ThirdPersonCamera.IsUiOpen: the ride panel needs the cursor, as RLO's own screens do.
        private static void UiOpenPostfix(ref bool __result)
        {
            if (!__result && Ride.Active?.ShowPanel == true) __result = true;
        }

        // ---------------------------------------------------------------- taxis

        // A traffic taxi another mod has taken out of Gley's hands (RLO's City Cabs: SetVehicleIgnored while it drives
        // the cab to the caller). Its target waypoint is still where it was loaded, out of sight.
        public static bool HoldsTaxi(TrafficManager manager, VehicleComponent car)
        {
            if (!Loaded) return false;
            var index = car.GetIndex();
            return index >= 0 && manager.IsVehicleIgnored(index);
        }

        // ---------------------------------------------------------------- reflection

        private static bool Patch(Harmony harmony, Type? type, string method, Type[] args, string? prefix = null, string? postfix = null)
        {
            var name = $"RealLifeOverhaul.{type?.Name ?? "?"}.{method}";
            try
            {
                var original = type != null ? AccessTools.Method(type, method, args) : null;
                if (original == null)
                {
                    Log.Error($"Patch {name}: Not active, method not found.");
                    return false;
                }
                harmony.Patch(original, prefix: prefix != null ? new HarmonyMethod(typeof(RloCompat), prefix) : null,
                    postfix: postfix != null ? new HarmonyMethod(typeof(RloCompat), postfix) : null);
                Log.Info($"Patch {name}: Active.");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"Patch {name}: Not active, {ex.Message}");
                return false;
            }
        }

        private static PropertyInfo? Static(Assembly assembly, string typeName, string property)
        {
            var type = assembly.GetType(typeName, false);
            var info = type != null ? AccessTools.Property(type, property) : null;
            return info != null && info.GetMethod != null && info.GetMethod.IsStatic ? info : null;
        }

        private static object? Get(PropertyInfo? property)
        {
            try { return property?.GetValue(null); }
            catch { return null; }
        }

        private static bool Is(PropertyInfo? property) => Get(property) is true;

        private static string Found(object? info) => info != null && !(info is false) ? "found" : "NOT FOUND";

        private static Assembly? FindAssembly()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                if (assembly.GetName().Name == AssemblyName) return assembly;
            return null;
        }
    }
}
