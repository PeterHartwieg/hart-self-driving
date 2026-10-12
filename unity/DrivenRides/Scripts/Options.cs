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
        // A new id: the first spike builds defaulted to 3x under "speed".
        private const string SpeedId = "startspeed";
        private const string ClockId = "clock";
        private const string ShortcutId = "shortcut";
        private const string LightsId = "lights";
        private static readonly float[] Speeds = { 1f, 2f, 3f };
        private const int DefaultSpeedIndex = 0;
        // The clock's rate during a ride: a third, a half, normal.
        private static readonly float[] Clocks = { 1f / 3f, 0.5f, 1f };
        public static readonly string[] ClockLabels = { "1/3", "1/2", "1x" };
        private const int DefaultClockIndex = 0;
        private static string? modId;

        public static bool Taxis { get; private set; } = true;
        public static bool PrivateDriver { get; private set; } = true;
        public static float RideSpeed { get; private set; } = 1f;
        public static int ClockIndex { get; private set; } = DefaultClockIndex;
        public static float ClockFactor => Clocks[ClockIndex];
        public static bool Shortcut { get; private set; } = true;
        public static bool TrafficLights { get => LightsOverride ?? lights; private set => lights = value; }
        private static bool lights = true;
        // Set by a test session (lights obeyed), never written to PlayerPrefs, so the options page can't undo it.
        public static bool? LightsOverride;

        public static void Register(string id)
        {
            modId = id;
            Taxis = UnityEngine.PlayerPrefs.GetInt(Key(TaxisId), 1) != 0;
            PrivateDriver = UnityEngine.PlayerPrefs.GetInt(Key(DriverId), 1) != 0;
            RideSpeed = SpeedAt(UnityEngine.PlayerPrefs.GetInt(Key(SpeedId), DefaultSpeedIndex));
            ClockIndex = ClockAt(UnityEngine.PlayerPrefs.GetInt(Key(ClockId), DefaultClockIndex));
            Shortcut = UnityEngine.PlayerPrefs.GetInt(Key(ShortcutId), 1) != 0;
            TrafficLights = UnityEngine.PlayerPrefs.GetInt(Key(LightsId), 1) != 0;

            var options = new ModOptions()
                .AddHeader("drivenrides:options_header")
                .AddToggle(TaxisId, "drivenrides:options_taxis", true, value => { Taxis = value; Log.Info($"Option: driven taxis {(value ? "on" : "off")}."); })
                .AddToggle(DriverId, "drivenrides:options_driver", true, value => { PrivateDriver = value; Log.Info($"Option: driven private driver {(value ? "on" : "off")}."); })
                .AddDropdown(SpeedId, "drivenrides:options_speed",
                    new[] { "drivenrides:options_speed_1", "drivenrides:options_speed_2", "drivenrides:options_speed_3" },
                    DefaultSpeedIndex, index => { RideSpeed = SpeedAt(index); Log.Info($"Option: ride speed {RideSpeed}x."); })
                .AddDropdown(ClockId, "drivenrides:options_clock",
                    new[] { "drivenrides:options_clock_third", "drivenrides:options_clock_half", "drivenrides:options_clock_normal" },
                    DefaultClockIndex, index => { ClockIndex = ClockAt(index); Log.Info($"Option: clock during rides {ClockLabels[ClockIndex]}."); })
                .AddToggle(LightsId, "drivenrides:options_lights", true, value => { TrafficLights = value; Log.Info($"Option: stop at red lights {(value ? "on" : "off")}."); })
                .AddToggle(ShortcutId, "drivenrides:options_shortcut", true, value => { Shortcut = value; Log.Info($"Option: driver shortcut {(value ? "on" : "off")}."); });
            OptionsService.Register(id, options);
            Log.Info($"Options: driven taxis {(Taxis ? "on" : "off")}, private driver {(PrivateDriver ? "on" : "off")}, ride speed {RideSpeed}x, clock during rides {ClockLabels[ClockIndex]}, driver shortcut {(Shortcut ? "on" : "off")}, stop at red lights {(TrafficLights ? "on" : "off")}.");
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

        public static void RememberClock(int index)
        {
            if (index < 0 || index >= Clocks.Length || modId == null) return;
            ClockIndex = index;
            Save(ClockId, index);
        }

        private static void Save(string optionId, int value)
        {
            try { UnityEngine.PlayerPrefs.SetInt(Key(optionId), value); UnityEngine.PlayerPrefs.Save(); }
            catch (Exception e) { Log.Error($"Saving option {optionId}: {e.Message}"); }
        }

        private static string Key(string optionId) => "m:" + modId + ":" + optionId;

        private static float SpeedAt(int index) => Speeds[index >= 0 && index < Speeds.Length ? index : DefaultSpeedIndex];

        private static int ClockAt(int index) => index >= 0 && index < Clocks.Length ? index : DefaultClockIndex;
    }
}
