#nullable enable
using System;
using BigAmbitions.Mods;
using UnityEngine;

namespace DrivenRides
{
    // The mod's options page. The game keeps each value in PlayerPrefs under "m:<modId>:<optionId>" and calls a
    // callback only when the page builds its controls or the player changes one, so the values are read from
    // PlayerPrefs at load and the callbacks keep them current afterwards. The ride panel's speed and clock buttons
    // write the same keys, so the page shows the panel's last choice (Peter, 12 October 2026).
    internal static class Options
    {
        private const string TaxisId = "taxis";
        private const string DriverId = "driver";
        private const string OwnCarId = "owncar";
        private const string AutoparkId = "autopark";
        private const int DefaultAutopark = (int)AutoparkMode.CarsWithIt;
        // A new id: the first spike builds defaulted to 3x under "speed".
        private const string SpeedId = "startspeed";
        // A new id: the first settings build stored a dropdown index under "clock".
        private const string ClockId = "clockpercent";
        private const string ShortcutId = "shortcut";
        private const string PinnedKeyId = "pinnedkey";
        private const string HomeKeyId = "homekey";
        private const string LightsId = "lights";
        private const string PedestriansId = "pedestrians";
        private const string SpeedLimitsId = "nolimits";
        private const string CarPhysicsId = "carphysics";
        private static readonly float[] Speeds = { 1f, 2f, 3f };
        private const int DefaultSpeedIndex = 0;
        // The clock's rate during a ride in percent: 0 stops it, 100 is normal (Peter, 12 October 2026).
        private const int DefaultClockPercent = 33;
        private static string? modId;

        public static bool Taxis { get; private set; } = true;
        public static bool PrivateDriver { get; private set; } = true;
        public static bool OwnCar { get; private set; } = true;
        public static AutoparkMode Autopark { get => AutoparkOverride ?? autopark; private set => autopark = value; }
        private static AutoparkMode autopark = AutoparkMode.CarsWithIt;
        // Set by the rig's own-car rides (RideCommand autopark=...), never written to PlayerPrefs.
        public static AutoparkMode? AutoparkOverride;
        public static float RideSpeed { get; private set; } = 1f;
        public static int ClockPercent { get; private set; } = DefaultClockPercent;
        public static float ClockFactor => ClockPercent / 100f;
        public static bool Shortcut { get; private set; } = true;
        public static int PinnedKey { get; private set; } = DestinationShortcut.DefaultPinnedKey;
        public static int HomeKey { get; private set; } = DestinationShortcut.DefaultHomeKey;
        // Taxi and private driver rides drive with the car's own physics (NWH, phase 5); off, they are the Gley rides
        // as before. The rig's `ridemode` command overrides it for a session without touching PlayerPrefs.
        public static bool CarPhysics { get => CarPhysicsOverride ?? carPhysics; private set => carPhysics = value; }
        private static bool carPhysics = true;
        public static bool? CarPhysicsOverride;
        public static bool TrafficLights { get => LightsOverride ?? lights; private set => lights = value; }
        private static bool lights = true;
        // The ride car brakes and stops for people in its way (NwhDrive's cast). Off by default: Gley's cars don't,
        // and people cross on red anyway (Peter, 14 October 2026). The car passes through people either way.
        public static bool Pedestrians { get; private set; }
        // The ride car drives up to its own top speed instead of DrivePlan.LimitFactor of the road's limit; corners,
        // traffic, following and lights as before. Off by default. Taken when a ride is planned (NwhPlanner.For).
        public static bool IgnoreSpeedLimits { get; private set; }
        // Set by a test session (lights obeyed), never written to PlayerPrefs, so the options page can't undo it.
        public static bool? LightsOverride;

        public static void Register(string id)
        {
            modId = id;
            Taxis = UnityEngine.PlayerPrefs.GetInt(Key(TaxisId), 1) != 0;
            PrivateDriver = UnityEngine.PlayerPrefs.GetInt(Key(DriverId), 1) != 0;
            OwnCar = UnityEngine.PlayerPrefs.GetInt(Key(OwnCarId), 1) != 0;
            Autopark = AutoparkAt(UnityEngine.PlayerPrefs.GetInt(Key(AutoparkId), DefaultAutopark));
            RideSpeed = SpeedAt(UnityEngine.PlayerPrefs.GetInt(Key(SpeedId), DefaultSpeedIndex));
            ClockPercent = ClampClock(UnityEngine.PlayerPrefs.GetInt(Key(ClockId), DefaultClockPercent));
            Shortcut = UnityEngine.PlayerPrefs.GetInt(Key(ShortcutId), 1) != 0;
            PinnedKey = DestinationShortcut.ValidKey(UnityEngine.PlayerPrefs.GetInt(Key(PinnedKeyId), DestinationShortcut.DefaultPinnedKey), DestinationShortcut.DefaultPinnedKey);
            HomeKey = DestinationShortcut.ValidKey(UnityEngine.PlayerPrefs.GetInt(Key(HomeKeyId), DestinationShortcut.DefaultHomeKey), DestinationShortcut.DefaultHomeKey);
            TrafficLights = UnityEngine.PlayerPrefs.GetInt(Key(LightsId), 1) != 0;
            Pedestrians = UnityEngine.PlayerPrefs.GetInt(Key(PedestriansId), 0) != 0;
            IgnoreSpeedLimits = UnityEngine.PlayerPrefs.GetInt(Key(SpeedLimitsId), 0) != 0;
            CarPhysics = UnityEngine.PlayerPrefs.GetInt(Key(CarPhysicsId), 1) != 0;

            var options = new ModOptions()
                .AddHeader("drivenrides:options_header")
                .AddToggle(TaxisId, "drivenrides:options_taxis", true, value => { Taxis = value; Log.Info($"Option: driven taxis {(value ? "on" : "off")}."); })
                .AddToggle(DriverId, "drivenrides:options_driver", true, value => { PrivateDriver = value; Log.Info($"Option: driven private driver {(value ? "on" : "off")}."); })
                .AddToggle(CarPhysicsId, "drivenrides:options_carphysics", true, value => { CarPhysics = value; Log.Info($"Option: rides with car physics {(value ? "on" : "off")}."); })
                .AddToggle(OwnCarId, "drivenrides:options_owncar", true, value => { OwnCar = value; Log.Info($"Option: own car self driving {(value ? "on" : "off")}."); })
                .AddDropdown(AutoparkId, "drivenrides:options_autopark",
                    new[] { "drivenrides:options_autopark_off", "drivenrides:options_autopark_cars", "drivenrides:options_autopark_every" },
                    DefaultAutopark, index => { Autopark = AutoparkAt(index); Log.Info($"Option: autopark {Autopark}."); })
                .AddDropdown(SpeedId, "drivenrides:options_speed",
                    new[] { "drivenrides:options_speed_1", "drivenrides:options_speed_2", "drivenrides:options_speed_3" },
                    DefaultSpeedIndex, index => { RideSpeed = SpeedAt(index); Log.Info($"Option: ride speed {RideSpeed}x."); })
                .AddSlider(ClockId, "drivenrides:options_clock", 0, 100, DefaultClockPercent,
                    value => { ClockPercent = ClampClock(value); Log.Info($"Option: clock during rides {ClockPercent}%."); },
                    "drivenrides:options_clock_value")
                .AddToggle(LightsId, "drivenrides:options_lights", true, value => { TrafficLights = value; Log.Info($"Option: stop at red lights {(value ? "on" : "off")}."); })
                .AddToggle(PedestriansId, "drivenrides:options_pedestrians", false, value => { Pedestrians = value; Log.Info($"Option: stop for pedestrians {(value ? "on" : "off")}."); })
                .AddToggle(SpeedLimitsId, "drivenrides:options_nolimits", false, value => { IgnoreSpeedLimits = value; Log.Info($"Option: ignore speed limits {(value ? "on" : "off")}."); })
                .AddToggle(ShortcutId, "drivenrides:options_shortcut", true, value => { Shortcut = value; Log.Info($"Option: driver shortcut {(value ? "on" : "off")}."); })
                .AddDropdown(PinnedKeyId, "drivenrides:options_pinned_key", DestinationShortcut.Labels, DestinationShortcut.DefaultPinnedKey,
                    index => PinnedKey = DestinationShortcut.ValidKey(index, DestinationShortcut.DefaultPinnedKey))
                .AddDropdown(HomeKeyId, "drivenrides:options_home_key", DestinationShortcut.Labels, DestinationShortcut.DefaultHomeKey,
                    index => HomeKey = DestinationShortcut.ValidKey(index, DestinationShortcut.DefaultHomeKey));
            OptionsService.Register(id, options);
            Log.Info($"Options: driven taxis {(Taxis ? "on" : "off")}, private driver {(PrivateDriver ? "on" : "off")}, own car {(OwnCar ? "on" : "off")}, autopark {Autopark}, ride speed {RideSpeed}x, clock during rides {ClockPercent}%, driver shortcut {(Shortcut ? "on" : "off")}, stop at red lights {(TrafficLights ? "on" : "off")}, car physics {(CarPhysics ? "on" : "off")}.");
        }

        public static void Unregister()
        {
            if (modId != null) OptionsService.RemoveModOptions(modId);
        }

        // The panel's buttons: the choice becomes the options page's value and the next ride's start.
        public static void RememberSpeed(float speed)
        {
            var index = Array.IndexOf(Speeds, speed);
            if (index < 0 || modId == null) return;
            RideSpeed = speed;
            Save(SpeedId, index);
        }

        // The panel's slider sets the rate while it is dragged and writes it to disk once, when it is let go.
        public static void SetClock(int percent)
        {
            if (modId == null) return;
            ClockPercent = ClampClock(percent);
            Save(ClockId, ClockPercent, flush: false);
        }

        public static void FlushClock()
        {
            if (modId == null) return;
            Save(ClockId, ClockPercent);
            Log.Trace($"Clock during rides {ClockPercent}%.");
        }

        private static void Save(string optionId, int value, bool flush = true)
        {
            try { UnityEngine.PlayerPrefs.SetInt(Key(optionId), value); if (flush) UnityEngine.PlayerPrefs.Save(); }
            catch (Exception e) { Log.Error($"Saving option {optionId}: {e.Message}"); }
        }

        private static string Key(string optionId) => "m:" + modId + ":" + optionId;

        private static float SpeedAt(int index) => Speeds[index >= 0 && index < Speeds.Length ? index : DefaultSpeedIndex];

        private static AutoparkMode AutoparkAt(int index) => index >= 0 && index <= 2 ? (AutoparkMode)index : (AutoparkMode)DefaultAutopark;

        private static int ClampClock(int percent) => Mathf.Clamp(percent, 0, 100);
    }
}
