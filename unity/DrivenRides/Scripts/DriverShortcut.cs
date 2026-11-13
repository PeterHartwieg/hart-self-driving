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
    internal sealed class DriverShortcut : MonoBehaviour
    {
        private static VehicleInstance? lastCalled;

        public static void Remember(VehicleInstance vehicle) => lastCalled = vehicle;

        private void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null || !keyboard.lKey.wasPressedThisFrame) return;
            if (keyboard.ctrlKey.isPressed || keyboard.altKey.isPressed || keyboard.shiftKey.isPressed) return;
            if (SaveGameManager.Current == null || GameManager.ShouldBlockKeyboardShortcuts()) return;
            if (OtherMods.PhotoMode) return;
            if (!(VehicleHelper.IsInsideVehicle() ? Options.OwnCar : Options.Shortcut)) return;
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
                if (BuildingManager.IsInsideBuilding) { Notice.Text("drivenrides:notice_driver_outside"); return; }
                Log.Trace("Driver shortcut: walking to the waiting driver.");
                waiting.OnClickToSetDestination();
                return;
            }
            var vehicles = SaveGameManager.Current.privateDriverVehicleInstances;
            if (vehicles == null || vehicles.Count == 0) { Notice.Text("drivenrides:notice_no_driver"); return; }
            var vehicle = lastCalled != null && vehicles.Contains(lastCalled) ? lastCalled : vehicles[0];
            Log.Trace($"Driver shortcut: calling {vehicle.vehicleTypeName}.");
            InstanceBehavior<UIs>.Instance.smartphoneUI.privateDriverUI.OnClickVehicle(vehicle);
        }
    }
}
