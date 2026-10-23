using System;
using System.Collections.Generic;

// Shared game API surface for the linked adapters. No game binaries are needed by dotnet test.
public static class InstanceBehavior<T> { public static T Instance = default!; }
public sealed class GameInstance { public Address? customDestination; }
public static class SaveGameManager { public static GameInstance Current = new(); }
public sealed class GameManager
{
    public RadioPlayer radioPlayer = new();
    public PlayerController playerController = new();
    public static bool BlockKeys;
    public static bool ShouldBlockKeyboardShortcuts() => BlockKeys;
}
public sealed class RadioPlayer
{
    public bool IsMuted = true;
    public readonly RadioEvent onRadioToggle = new();
    public int MuteCalls;
    public void ToggleMute(bool muted) { IsMuted = muted; MuteCalls++; }
}
public sealed class RadioEvent
{
    public readonly List<bool> Values = new();
    public void Invoke(bool value) => Values.Add(value);
}
public sealed class RadioControls
{
    public bool Enabled;
    public bool ThrowOnEnable;
    public void EnableUI() { if (ThrowOnEnable) throw new InvalidOperationException("UI missing"); Enabled = true; }
    public void DisableUI() => Enabled = false;
}
public sealed class VehicleController { public VehicleType vehicleType = new(); }
public sealed class VehicleType { public bool spawnInPlayerObject; public bool hasRadio; }
namespace UI
{
    public sealed class UIs { public Phone smartphoneUI = new(); public TimeMachine timeMachine = new(); }
    public sealed class Phone { public RadioControls radioControls = new(); }
}
namespace Helpers
{
    public static class PlayerHelper { public static bool Headset; public static bool IsWearingHeadset() => Headset; }
    public static class VehicleHelper
    {
        public static VehicleController? Current;
        public static VehicleController? GetCurrentVehicleBase() => Current;
        public static bool IsInsideVehicle() => Current != null;
    }
}
namespace DrivenRides
{
    internal static class Log
    {
        internal static readonly List<string> Errors = new();
        internal static void Error(string message) => Errors.Add(message);
        internal static void Trace(string message) { }
    }
}
