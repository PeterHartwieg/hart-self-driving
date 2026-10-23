using System.Collections;
using System.Collections.Generic;
using Xunit;

[CollectionDefinition("Game adapters")]
public sealed class GameAdapterCollection { }

// Narrow doubles for destination input and dispatch; SDK compilation checks the real game API surface.
public sealed record Address(string Name);
public sealed class Building { public string BuildingType = "ba:buildingtype_residential"; }
public sealed class CityBuildingController { public Building building = new(); }
public sealed class CityManager
{
    public readonly Dictionary<Address, CityBuildingController> Buildings = new();
    public CityMap cityMap = new();
    public CityBuildingController? FindCityBuildingController(Address address) => Buildings.GetValueOrDefault(address);
}
public sealed class CityMap
{
    public static bool IsOpen;
    public static bool CanOpen = true;
    public object? Taxi;
    public bool IsTaxiMode => Taxi != null;
    public static bool CanOpenMap() => CanOpen;
}
public sealed class PlayerController { public bool awaitingRepositioning; }
public sealed class TimeMachine { public bool isBlockingUi; }
public static class SubwaySystem { public static bool IsRiding; }
namespace Helpers { public static class EnergyHelper { public static bool goingToHospital; } }
public sealed class TaxiSystem
{
    public static bool IsTraveling;
    public readonly List<CityBuildingController> Trips = new();
    public void TravelTo(CityBuildingController target) => Trips.Add(target);
}
namespace UnityEngine
{
    public class MonoBehaviour
    {
        private IEnumerator? routine;
        public void StartCoroutine(IEnumerator value) { routine = value; value.MoveNext(); }
        public void NextFrame() { routine?.MoveNext(); }
    }
}
namespace UnityEngine.InputSystem
{
    public enum Key { None, G, J, K, U, V, X }
    public sealed class KeyControl { public bool isPressed; public bool wasPressedThisFrame; }
    public sealed class Keyboard
    {
        public static Keyboard? current;
        public readonly KeyControl ctrlKey = new(), altKey = new(), shiftKey = new(), leftMetaKey = new(), rightMetaKey = new();
        private readonly Dictionary<Key, KeyControl> keys = new();
        public KeyControl this[Key key] => keys.TryGetValue(key, out var value) ? value : keys[key] = new();
    }
}
namespace PlayerActivity { public static class PlayerActivityUI { public static bool IsPanelOpen; } }
namespace UI.Load { public static class LoadScene { public static bool isLoading; } }
namespace UI.MiniMenu { public static class MiniMenu { public static bool IsOpen; } }
namespace UI.Smartphone { public static class FullMenu { public static bool IsOpen; } }
namespace DrivenRides
{
    internal static class Options { public static int PinnedKey = 2, HomeKey = 3; }
    internal static class TurnTests { public static bool Running; }
    internal static class Ride { public static object? Active; }
    internal static class Notice { public static string? Last; public static void Text(string text) => Last = text; }
    internal sealed class OwnCarTaxi
    {
        public VehicleController? Car;
        public static bool Refuse;
        public static int Opens;
        public static void Open()
        {
            Opens++;
            if (Refuse) return;
            CityMap.IsOpen = true;
            InstanceBehavior<CityManager>.Instance.cityMap.Taxi = new OwnCarTaxi { Car = Helpers.VehicleHelper.Current };
        }
    }
}
