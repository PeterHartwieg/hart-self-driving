#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BigAmbitions.Mods;
using Helpers;
using Newtonsoft.Json.Linq;
using UI;
using UI.Guiders;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using Object = UnityEngine.Object;

namespace DrivenRides
{
    // Disposable-world probe: real Input System events reach DestinationShortcut.Update. Only dispatched
    // by the opt-in rig. This does not call the shortcut or TaxiSystem on behalf of an injected key.
    internal static class DestinationTests
    {
        internal static bool Busy { get; private set; }

        internal static IEnumerator Press(string keyName, string? modifier = null)
        {
            if (!Enum.TryParse(keyName, true, out Key key) || key == Key.None) throw new ArgumentException("Unknown key " + keyName);
            var previous = Keyboard.current;
            var keyboard = InputSystem.AddDevice<Keyboard>("DrivenRidesTestKeyboard");
            try
            {
                keyboard.MakeCurrent();
                var keys = new List<Key> { key };
                if (modifier != null && Enum.TryParse(modifier, true, out Key mod)) keys.Add(mod);
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(keys.ToArray()));
                yield return null;
                yield return null;
                InputSystem.QueueStateEvent(keyboard, new KeyboardState());
                yield return null;
                yield return null;
            }
            finally
            {
                InputSystem.RemoveDevice(keyboard);
                if (previous != null && previous.added) previous.MakeCurrent();
            }
        }

        internal static IEnumerator Run(Dictionary<string, string> args, string folder)
        {
            if (Busy || !TestMode.On || SaveGameManager.Current?.SaveGameName?.StartsWith("FT", StringComparison.Ordinal) != true) yield break;
            Busy = true;
            var result = new JObject { ["op"] = args.TryGetValue("op", out var op) ? op : "status", ["before"] = Snapshot() };
            try
            {
                var steps = Steps(args, result);
                while (true)
                {
                    object? current;
                    try { if (!steps.MoveNext()) break; current = steps.Current; }
                    catch (Exception ex) { result["error"] = ex.ToString(); break; }
                    yield return current;
                }
            }
            finally
            {
                result["after"] = Snapshot();
                result["at"] = DateTime.UtcNow.ToString("O");
                File.AppendAllText(Path.Combine(folder, "destination-results.jsonl"), result.ToString(Newtonsoft.Json.Formatting.None) + "\n");
                Busy = false;
            }
        }

        private static IEnumerator Steps(Dictionary<string, string> args, JObject result)
        {
            var op = (string)result["op"]!;
            var city = InstanceBehavior<CityManager>.Instance;
            var buildings = Object.FindObjectsOfType<CityBuildingController>(true).Where(b => b != null && b.building != null).ToArray();
            var target = args.TryGetValue("address", out var address) ? buildings.FirstOrDefault(b => Id(b) == address) : null;
            switch (op)
            {
                case "status":
                    var player = InstanceBehavior<GameManager>.Instance.playerController.transform.position;
                    result["residences"] = new JArray(buildings.Where(b => b.building.BuildingType == "ba:buildingtype_residential")
                        .OrderBy(b => Vector3.Distance(player, b.transform.position)).Take(10).Select(b => new JObject { ["address"] = Id(b), ["at"] = Position(b.transform.position) }));
                    result["stands"] = new JArray(Object.FindObjectsOfType<PermanentTaxiController>(true).Take(8).Select(b => Position(b.transform.position)));
                    break;
                case "pin":
                    if (target == null) throw new ArgumentException("Building not found");
                    GuidersManager.SetGuiderTarget(target.building.Address, DirectionGuiderType.Destination);
                    break;
                case "clear-pin": SaveGameManager.Current.customDestination = null; GuidersManager.ResetGuider(DirectionGuiderType.Destination); break;
                case "key":
                    result["key"] = args["key"];
                    var press = Press(args["key"], args.TryGetValue("modifier", out var modifier) ? modifier : null);
                    while (press.MoveNext()) yield return press.Current;
                    break;
                case "enter":
                    if (target == null || VehicleHelper.IsInsideVehicle() || BuildingManager.IsInsideBuilding || Ride.Active != null) throw new InvalidOperationException("Enter requires a building and the player on foot outdoors");
                    result["accepted"] = InstanceBehavior<BuildingManager>.Instance.EnterBuilding(target.building);
                    break;
                case "exit":
                    if (VehicleHelper.GetCurrentVehicleBase() is VehicleController vehicle) vehicle.ExitVehicle();
                    else if (BuildingManager.IsInsideBuilding) InstanceBehavior<BuildingManager>.Instance.ExitFromBuilding(0);
                    break;
                case "bind":
                    foreach (var pair in OptionsService.RegisteredEntries)
                        foreach (var option in pair.Value.Options.OfType<DropdownOption>())
                            if (option.Label == "drivenrides:options_" + args["which"] + "_key")
                            {
                                var index = int.Parse(args["index"]);
                                if (index < 0 || index >= option.ChoiceKeys.Length) throw new ArgumentOutOfRangeException("index");
                                UnityEngine.PlayerPrefs.SetInt("m:" + pair.Key + ":" + option.Id, index);
                                UnityEngine.PlayerPrefs.Save();
                                option.OnValueChanged(index);
                                result["choice"] = option.ChoiceKeys[index];
                            }
                    break;
                case "driver-fixture":
                    // Test-owned vehicle registration only; the normal phone still summons/drives it.
                    if (SaveGameManager.Current.privateDriverVehicleInstances.Count == 0)
                        SaveGameManager.Current.privateDriverVehicleInstances.Add(new VehicleInstance(args["vehicle"]));
                    break;
                case "map": city.cityMap.Toggle(); break;
                default: throw new ArgumentException("Unknown destination probe op " + op);
            }
            yield return null;
        }

        private static string? Id(CityBuildingController? target) => target == null ? null : target.building.StreetName + "#" + target.building.StreetNumber;
        private static JArray Position(Vector3 p) => new JArray(p.x, p.y, p.z);
        private static JObject Snapshot() => new JObject
        {
            ["save"] = SaveGameManager.Current?.SaveGameName,
            ["home"] = Id(DestinationTargets.Resolve(true)),
            ["pin"] = Id(DestinationTargets.Resolve(false)),
            ["pinnedKey"] = Options.PinnedKey, ["homeKey"] = Options.HomeKey,
            ["map"] = CityMap.IsOpen,
            ["taxi"] = InstanceBehavior<CityManager>.Instance?.cityMap.Taxi?.GetType().Name,
            ["blocked"] = GameManager.ShouldBlockKeyboardShortcuts(),
            ["traveling"] = TaxiSystem.IsTraveling, ["driven"] = Ride.Active != null,
            ["inside"] = BuildingManager.IsInsideBuilding,
            ["vehicle"] = VehicleHelper.GetCurrentVehicleBase()?.vehicleType?.vehicleTypeName,
            ["money"] = SaveGameManager.Current?.Money,
        };
    }
}
