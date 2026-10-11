#nullable enable
using System;
using BigAmbitions.Mods;
using UnityEngine;

namespace DrivenRides
{
    // The mod's options page. The game keeps each value in PlayerPrefs under "m:<modId>:<optionId>" and calls a
    // callback only when the page builds its controls or the player changes one, so the values are read from
    // PlayerPrefs at load and the callbacks keep them current afterwards.
    internal static class Options
    {
        private const string RidesId = "rides";
        // A new id: the first spike builds defaulted to 3x under "speed".
        private const string SpeedId = "startspeed";
        private const string ShortcutId = "shortcut";
        private const string LightsId = "lights";
        private static readonly float[] Speeds = { 1f, 2f, 3f };
        private const int DefaultSpeedIndex = 0;
        private static string? modId;

        public static bool DrivenRides { get; private set; } = true;
        public static float RideSpeed { get; private set; } = 1f;
        public static bool Shortcut { get; private set; } = true;
        public static bool TrafficLights { get; private set; } = true;

        public static void Register(string id)
        {
            modId = id;
            DrivenRides = UnityEngine.PlayerPrefs.GetInt(Key(RidesId), 1) != 0;
            RideSpeed = SpeedAt(UnityEngine.PlayerPrefs.GetInt(Key(SpeedId), DefaultSpeedIndex));
            Shortcut = UnityEngine.PlayerPrefs.GetInt(Key(ShortcutId), 1) != 0;
            TrafficLights = UnityEngine.PlayerPrefs.GetInt(Key(LightsId), 1) != 0;

            var options = new ModOptions()
                .AddHeader("drivenrides:options_header")
                .AddToggle(RidesId, "drivenrides:options_rides", true, value => { DrivenRides = value; Log.Info($"Option: driven rides {(value ? "on" : "off")}."); })
                .AddDropdown(SpeedId, "drivenrides:options_speed",
                    new[] { "drivenrides:options_speed_1", "drivenrides:options_speed_2", "drivenrides:options_speed_3" },
                    DefaultSpeedIndex, index => { RideSpeed = SpeedAt(index); Log.Info($"Option: ride speed {RideSpeed}x."); })
                .AddToggle(LightsId, "drivenrides:options_lights", true, value => { TrafficLights = value; Log.Info($"Option: stop at red lights {(value ? "on" : "off")}."); })
                .AddToggle(ShortcutId, "drivenrides:options_shortcut", true, value => { Shortcut = value; Log.Info($"Option: driver shortcut {(value ? "on" : "off")}."); });
            OptionsService.Register(id, options);
            Log.Info($"Options: driven rides {(DrivenRides ? "on" : "off")}, ride speed {RideSpeed}x, driver shortcut {(Shortcut ? "on" : "off")}, stop at red lights {(TrafficLights ? "on" : "off")}.");
        }

        public static void Unregister()
        {
            if (modId != null) OptionsService.RemoveModOptions(modId);
        }

        private static string Key(string optionId) => "m:" + modId + ":" + optionId;

        private static float SpeedAt(int index) => Speeds[index >= 0 && index < Speeds.Length ? index : DefaultSpeedIndex];
    }
}
