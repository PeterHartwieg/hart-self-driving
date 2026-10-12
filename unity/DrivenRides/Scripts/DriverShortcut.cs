#nullable enable
using System;
using Helpers;
using Player.HUD.SmartphoneUI;
using PlayerActivity;
using UI;
using UnityEngine;
using UnityEngine.InputSystem;

namespace DrivenRides
{
    // L calls the private driver; once it waits, L walks the player to it and opens the destination map. In the
    // driver's seat of the player's own car, L opens the destination map for the car to drive itself.
    // Vanilla needs the phone, the driver app and a vehicle each time. The game's bindings ignore modifiers and
    // leave L unbound; with any modifier held the key does nothing, so Ctrl+L and the like stay free.
    internal sealed class DriverShortcut : MonoBehaviour
    {
        private static VehicleInstance? lastCalled;

        public static void Remember(VehicleInstance vehicle) => lastCalled = vehicle;

        private void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null || !keyboard.lKey.wasPressedThisFrame) return;
            if (keyboard.ctrlKey.isPressed || keyboard.altKey.isPressed || keyboard.shiftKey.isPressed) return;
            if (!Options.Shortcut || SaveGameManager.Current == null || GameManager.ShouldBlockKeyboardShortcuts()) return;
            try { Press(); }
            catch (Exception ex) { Log.Error("Driver shortcut: " + ex); }
        }

        private static void Press()
        {
            if (TurnTests.Running || TaxiSystem.IsTraveling || Ride.Active != null || CityMap.IsOpen || PlayerActivityUI.IsPanelOpen) return;
            if (VehicleHelper.IsInsideVehicle()) { OwnCarTaxi.Open(); return; }
            var waiting = SmartphonePrivateDriverUI.CurrentVehicle;
            if (waiting != null)
            {
                if (BuildingManager.IsInsideBuilding) { Notice.Show("Your driver is waiting outside."); return; }
                Log.Info("Driver shortcut: walking to the waiting driver.");
                waiting.OnClickToSetDestination();
                return;
            }
            var vehicles = SaveGameManager.Current.privateDriverVehicleInstances;
            if (vehicles == null || vehicles.Count == 0) { Notice.Show("You have no private driver."); return; }
            var vehicle = lastCalled != null && vehicles.Contains(lastCalled) ? lastCalled : vehicles[0];
            Log.Info($"Driver shortcut: calling {vehicle.vehicleTypeName}.");
            // The phone's own handler: contract payment, the subway check, the indoor summon and its notices.
            InstanceBehavior<UIs>.Instance.smartphoneUI.privateDriverUI.OnClickVehicle(vehicle);
        }
    }
}
