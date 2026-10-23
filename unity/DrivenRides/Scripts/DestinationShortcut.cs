#nullable enable
using System;
using System.Collections;
using Helpers;
using PlayerActivity;
using UI;
using UI.Load;
using UI.MiniMenu;
using UI.Smartphone;
using UnityEngine;
using UnityEngine.InputSystem;

namespace DrivenRides
{
    internal sealed class DestinationShortcut : MonoBehaviour
    {
        // Only vanilla's unused letters, excluding L (call driver / open own-car map).
        private static readonly Key[] Keys = { Key.None, Key.G, Key.J, Key.K, Key.U, Key.V, Key.X };
        internal static readonly string[] Labels = { "drivenrides:key_disabled", "drivenrides:key_g", "drivenrides:key_j", "drivenrides:key_k", "drivenrides:key_u", "drivenrides:key_v", "drivenrides:key_x" };
        internal const int DefaultPinnedKey = 2;
        internal const int DefaultHomeKey = 3;
        private bool pending;

        internal static int ValidKey(int index, int fallback) => index >= 0 && index < Keys.Length ? index : fallback;

        private static bool Pressed(Keyboard keyboard, int index) => index > 0 && index < Keys.Length && keyboard[Keys[index]].wasPressedThisFrame;

        private void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null || pending) return;
            if (keyboard.ctrlKey.isPressed || keyboard.altKey.isPressed || keyboard.shiftKey.isPressed ||
                keyboard.leftMetaKey.isPressed || keyboard.rightMetaKey.isPressed) return;
            var pinned = Pressed(keyboard, Options.PinnedKey);
            var home = Pressed(keyboard, Options.HomeKey);
            if (!pinned && !home) return;
            try
            {
                if (Blocked()) return;
                var map = InstanceBehavior<CityManager>.Instance?.cityMap;
                if (map == null) return;
                // A normal browsing/subway map never becomes a taxi map through these keys.
                if (CityMap.IsOpen ? !map.IsTaxiMode : !VehicleHelper.IsInsideVehicle()) return;
                if (pinned && home) { Notice.Text("drivenrides:notice_destination_key_conflict"); return; }
                var target = DestinationTargets.Resolve(home);
                if (target == null)
                {
                    Notice.Text(home ? "drivenrides:notice_no_home" : "drivenrides:notice_no_pin");
                    return;
                }
                if (CityMap.IsOpen)
                {
                    Travel(target);
                    return;
                }
                OwnCarTaxi.Open(); // Existing stopped-car, damage, location and enabled checks.
                if (!CityMap.IsOpen || !(map.Taxi is OwnCarTaxi own) || own.Car != VehicleHelper.GetCurrentVehicleBase()) return;
                // Let the map initialise its camera before selecting the destination. Closing it, changing
                // saves, leaving the car or an interruption cancels this request instead of starting it later.
                pending = true;
                StartCoroutine(AfterOpening(map, own, target, SaveGameManager.Current));
            }
            catch (Exception ex) { pending = false; Log.Error("Destination shortcut: " + ex); }
        }

        private IEnumerator AfterOpening(CityMap map, OwnCarTaxi own, CityBuildingController target, GameInstance save)
        {
            yield return null;
            try
            {
                if (!ReferenceEquals(save, SaveGameManager.Current) || map == null || own == null || target == null ||
                    !CityMap.IsOpen || !ReferenceEquals(map.Taxi, own) || own.Car != VehicleHelper.GetCurrentVehicleBase() || Blocked()) yield break;
                Travel(target);
            }
            catch (Exception ex) { Log.Error("Destination shortcut: " + ex); }
            finally { pending = false; }
        }

        private static bool Blocked() => SaveGameManager.Current == null || LoadScene.isLoading ||
            GameManager.ShouldBlockKeyboardShortcuts() || MiniMenu.IsOpen || FullMenu.IsOpen ||
            TurnTests.Running || TaxiSystem.IsTraveling || Ride.Active != null || PlayerActivityUI.IsPanelOpen ||
            SubwaySystem.IsRiding || EnergyHelper.goingToHospital || InstanceBehavior<GameManager>.Instance.playerController.awaitingRepositioning ||
            InstanceBehavior<UIs>.Instance.timeMachine.isBlockingUi || !CityMap.CanOpenMap();

        private static void Travel(CityBuildingController target)
        {
            // Exactly the map's Go action: vanilla keeps its fare, access checks and existing ride patches.
            InstanceBehavior<TaxiSystem>.Instance.TravelTo(target);
        }
    }
}
