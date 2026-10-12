#nullable enable
using System;
using System.Collections;
using System.Linq;
using BigAmbitions.DayNightCycle;
using GleyTrafficSystem;
using Helpers;
using HarmonyLib;
using Player.HUD.SmartphoneUI;
using PlayerActivity;
using Timemachine;

namespace DrivenRides
{
    // Harmony patches. Each one logs an Active or Not active line at startup.
    internal static class Patches
    {
        private const string HarmonyId = "drivenrides.mod";
        private static Harmony? harmony;

        public static void Apply()
        {
            if (harmony != null) return;
            harmony = new Harmony(HarmonyId);
            Log.Info($"Harmony {typeof(Harmony).Assembly.GetName().Version}.");

            // The ride itself: replaces the iterator vanilla's TravelTo starts, after its checks and payment.
            Patch(typeof(TaxiSystem), "TravelCoroutine", new[] { typeof(EntityController), typeof(float) }, nameof(TravelPrefix), prefix: true);
            // The private driver's own Update would stop the car every frame while the mod drives it.
            Patch(typeof(PrivateDriverVehicle), "Update", Type.EmptyTypes, nameof(DriverUpdatePrefix), prefix: true);
            // The clock runs at the options' rate during a ride (a third by default).
            Patch(typeof(GameManager), "RunMainGameTick", new[] { typeof(float) }, nameof(TickPrefix), prefix: true);
            // A save during a ride puts the player back where they got in.
            Patch(typeof(PlayerHelper), "SaveCurrentPosition", Type.EmptyTypes, nameof(SavePositionPostfix));
            // No activities (reading, and with it fast-forward and the activity time machine) during a ride.
            Patch(typeof(PlayerActivityUI), "CanStartActivity", new[] { typeof(bool), typeof(bool) }, nameof(ActivityPrefix), prefix: true);
            // A time machine the ride didn't start cancels the ride first. The one-argument overload forwards here.
            Patch(typeof(TimeMachine), "StartTimeMachine", new[] { typeof(Timestamp), typeof(bool), typeof(string), typeof(bool), typeof(bool) }, nameof(TimeMachinePrefix), prefix: true);
            // Red lights: Gley would move the ride car's target past a red stop line once it is within 5 m.
            Patch(typeof(DrivingAI), "WaypointRequested", new[] { typeof(int), typeof(VehicleTypes) }, nameof(WaypointRequestedPrefix), prefix: true);
            // Gley's "passed" test pops every waypoint behind the car's nose; in a sharp turn that emptied the route.
            Patch(typeof(DrivingAI), "HasReachedOrPassedWaypoint", new[] { typeof(int), typeof(int) }, nameof(PassedPrefix), prefix: true);
            // The phone's full-screen apps pause the game; during a ride time goes on, so a passenger can be busy
            // on the phone (Peter).
            Patch(typeof(UI.Smartphone.FullMenu), "Toggle", new[] { typeof(bool) }, nameof(FullMenuTogglePrefix), prefix: true);
            Patch(typeof(UI.Smartphone.FullMenu), "Toggle", new[] { typeof(bool) }, nameof(FullMenuTogglePostfix));
            Patch(typeof(GameSpeedController), "SetPause", new[] { typeof(bool), typeof(bool) }, nameof(SetPausePrefix), prefix: true);
            // ... and the street stays audible: the phone's UI-only sound mix is normally undone by the unpause.
            Patch(typeof(SfxManager), "SetSoundSnapshotCityMap", new[] { typeof(bool), typeof(float) }, nameof(PhoneSoundPrefix), prefix: true);
            // No taxi or driver ride while turn tests run, before the fare is taken.
            Patch(typeof(TaxiSystem), "TravelTo", new[] { typeof(EntityController) }, nameof(TravelToPrefix), prefix: true);
            // An own-car ride costs no fare.
            Patch(typeof(TaxiSystem), "GetPrice", new[] { typeof(float) }, nameof(PricePrefix), prefix: true);
            // No hourly parking ticket or fee for the own car while it is hidden at the start of a ride: the game skips
            // the car the player sits in, so for that hour the hidden car counts as that car.
            Patch(typeof(ParkingSimulator), "RunHourly", Type.EmptyTypes, nameof(HourlyPrefix), prefix: true);
            Patch(typeof(ParkingSimulator), "RunHourly", Type.EmptyTypes, nameof(HourlyPostfix));
            // The driver shortcut remembers the vehicle called from the phone too.
            Patch(typeof(SmartphonePrivateDriverUI), "OnClickVehicle", new[] { typeof(VehicleInstance) }, nameof(DriverCalledPostfix));
        }

        public static void Remove()
        {
            harmony?.UnpatchAll(HarmonyId);
            harmony = null;
        }

        private static void Patch(Type? type, string method, Type[]? args, string handler, bool prefix = false)
        {
            // Overloads need their arguments in the log line to tell them apart.
            var name = $"{type?.Name ?? "(type not found)"}.{method}{(args != null && args.Length > 0 ? "(" + string.Join(", ", args.Select(a => a.Name)) + ")" : "")}";
            if (type == null)
            {
                Log.Error($"Patch {name}: Not active, type not found.");
                return;
            }
            try
            {
                var original = AccessTools.Method(type, method, args);
                if (original == null)
                {
                    Log.Error($"Patch {name}: Not active, method not found.");
                    return;
                }
                var patch = new HarmonyMethod(typeof(Patches), handler);
                harmony!.Patch(original, prefix: prefix ? patch : null, postfix: prefix ? null : patch);
                Log.Info($"Patch {name}: Active.");
            }
            catch (Exception ex)
            {
                Log.Error($"Patch {name}: Not active, {ex.Message}");
            }
        }

        private static bool TravelPrefix(TaxiSystem __instance, EntityController target, float minutes, ref IEnumerator __result)
        {
            var ride = Ride.TryCreate(__instance, target, minutes);
            if (ride == null) return true;
            __result = ride;
            return false;
        }

        private static bool DriverUpdatePrefix(PrivateDriverVehicle __instance)
        {
            try { return !Ride.OwnsCarObject(__instance.gameObject); }
            catch { return true; }
        }

        private static void TickPrefix(ref float deltaTimeWithMultiplier)
        {
            try { Ride.CapTick(ref deltaTimeWithMultiplier); }
            catch (Exception ex) { Log.Error("Tick cap: " + ex.Message); }
        }

        private static void SavePositionPostfix()
        {
            try
            {
                if (!Ride.OwnsPlayer || SaveGameManager.Current == null) return;
                SaveGameManager.Current.LastPlayerPosition = Ride.PickupPosition;
                SaveGameManager.Current.LastPlayerRotation = Ride.PickupRotation;
                Log.Info("Saved during a ride: the save has the pickup point.");
            }
            catch (Exception ex) { Log.Error("Save position: " + ex.Message); }
        }

        private static bool ActivityPrefix(bool showNotification, ref bool __result)
        {
            if (!Ride.OwnsPlayer && !TurnTests.Running) return true;
            // Skipping the method also skips vanilla's notice, so show one, or the click looks dead.
            if (showNotification) Notice.Show("Not during a ride.");
            __result = false;
            return false;
        }

        private static bool PricePrefix(ref float __result)
        {
            if (!(InstanceBehavior<CityManager>.Instance?.cityMap?.Taxi is OwnCarTaxi)) return true;
            __result = 0f;
            return false;
        }

        private static bool hourlySwapped;

        private static void HourlyPrefix()
        {
            hourlySwapped = false;
            try
            {
                var game = SaveGameManager.Current;
                var hidden = OwnCarTaxi.HiddenId();
                if (game == null || hidden == null || game.ActiveVehicleId != null) return;
                game.ActiveVehicleId = hidden;
                hourlySwapped = true;
            }
            catch (Exception ex) { Log.Error("Hourly parking: " + ex.Message); }
        }

        private static void HourlyPostfix()
        {
            if (!hourlySwapped) return;
            hourlySwapped = false;
            if (SaveGameManager.Current != null) SaveGameManager.Current.ActiveVehicleId = null;
        }

        private static bool TravelToPrefix()
        {
            if (!TurnTests.Running) return true;
            Notice.Show("Not while turn tests run.");
            return false;
        }

        private static void TimeMachinePrefix()
        {
            try { Ride.OnTimeMachineStarting(); }
            catch (Exception ex) { Log.Error("Time machine: " + ex.Message); }
        }

        private static bool PassedPrefix(int index, int waypointIndex, ref bool __result)
        {
            try
            {
                // Every other car returns here at once.
                var car = Ride.CarCentre(index);
                if (car == null) return true;
                var waypoint = TrafficManager.Instance.GetWaypoint(waypointIndex);
                if (waypoint == null) return true;
                var passed = Ride.HasPassed(index, waypointIndex, waypoint.position, car.Value);
                if (passed == null) return true;
                __result = passed.Value;
                return false;
            }
            catch { return true; }
        }

        private static bool openingPhone;

        private static void FullMenuTogglePrefix(bool show) => openingPhone = show;

        private static readonly System.Reflection.FieldInfo? MixerField = AccessTools.Field(typeof(SfxManager), "audioMixer");

        // Opening the phone also turns the traffic engines (the street's sound) down to silence; during a ride they
        // are turned back up so the passenger hears the road.
        private static void FullMenuTogglePostfix(bool show)
        {
            openingPhone = false;
            if (!show || !Ride.IsDriving) return;
            try { (MixerField?.GetValue(InstanceBehavior<SfxManager>.Instance) as UnityEngine.Audio.AudioMixer)?.ClearFloat("engine"); }
            catch (Exception ex) { Log.Error("Phone sound: " + ex.Message); }
        }

        private static bool SetPausePrefix(bool newPause)
        {
            if (!openingPhone || !newPause || !Ride.IsDriving) return true;
            Log.Info("Phone opened during a ride: time goes on.");
            return false;
        }

        private static bool PhoneSoundPrefix(bool onlyUISound) => !(openingPhone && onlyUISound && Ride.IsDriving);

        private static bool WaypointRequestedPrefix(int index)
        {
            try { return !Ride.HoldsAtRed(index); }
            catch { return true; }
        }

        private static void DriverCalledPostfix(VehicleInstance vehicleInstance)
        {
            if (SmartphonePrivateDriverUI.CurrentVehicle != null) DriverShortcut.Remember(vehicleInstance);
        }
    }
}
