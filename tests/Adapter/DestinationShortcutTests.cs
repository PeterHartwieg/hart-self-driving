using System;
using System.Reflection;
using Helpers;
using UnityEngine.InputSystem;
using Xunit;

namespace DrivenRides.Adapter.Tests;

[Collection("Game adapters")]
public sealed class DestinationShortcutTests
{
    private readonly CityManager city = new();
    private readonly TaxiSystem taxi = new();
    private readonly DestinationShortcut shortcut = new();
    private readonly Address first = new("First residence"), second = new("Second residence"), shop = new("Shop");

    public DestinationShortcutTests()
    {
        SaveGameManager.Current = new();
        InstanceBehavior<CityManager>.Instance = city;
        InstanceBehavior<TaxiSystem>.Instance = taxi;
        InstanceBehavior<GameManager>.Instance = new();
        InstanceBehavior<UI.UIs>.Instance = new();
        city.Buildings[first] = new();
        city.Buildings[second] = new();
        city.Buildings[shop] = new() { building = new() { BuildingType = "ba:buildingtype_retail" } };
        DestinationTargets.Reset();
        Keyboard.current = new();
        VehicleHelper.Current = null;
        Options.PinnedKey = 2;
        Options.HomeKey = 3;
        GameManager.BlockKeys = false;
        CityMap.IsOpen = true;
        CityMap.CanOpen = true;
        city.cityMap.Taxi = new object();
        TaxiSystem.IsTraveling = false;
        Ride.Active = null;
        TurnTests.Running = false;
        PlayerActivity.PlayerActivityUI.IsPanelOpen = false;
        UI.Load.LoadScene.isLoading = false;
        UI.MiniMenu.MiniMenu.IsOpen = false;
        UI.Smartphone.FullMenu.IsOpen = false;
        SubwaySystem.IsRiding = false;
        EnergyHelper.goingToHospital = false;
        OwnCarTaxi.Refuse = false;
        OwnCarTaxi.Opens = 0;
        Notice.Last = null;
    }

    private void Press(Key key)
    {
        Keyboard.current![key].wasPressedThisFrame = true;
        typeof(DestinationShortcut).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(shortcut, null);
        Keyboard.current[key].wasPressedThisFrame = false;
    }

    [Fact]
    public void PinnedUsesCurrentMarkerAndHomeUsesLatestResidentialVisit()
    {
        DestinationTargets.Entered(first);
        DestinationTargets.Entered(shop);
        Assert.Same(city.Buildings[first], DestinationTargets.Resolve(true));
        DestinationTargets.Entered(second);
        SaveGameManager.Current.customDestination = shop;
        Press(Key.J);
        Press(Key.K);
        Assert.Equal(new[] { city.Buildings[shop], city.Buildings[second] }, taxi.Trips);
        Assert.Equal(shop, SaveGameManager.Current.customDestination);
        SaveGameManager.Current.customDestination = first;
        Press(Key.J);
        Assert.Same(city.Buildings[first], taxi.Trips[2]);
    }

    [Fact]
    public void ResetAndReplacementSaveDiscardHomeWithoutDiscardingVanillaPin()
    {
        DestinationTargets.Entered(first);
        DestinationTargets.Reset();
        Assert.Null(DestinationTargets.Resolve(true));
        DestinationTargets.Entered(first);
        SaveGameManager.Current = new() { customDestination = shop };
        Assert.Null(DestinationTargets.Resolve(true));
        Assert.Same(city.Buildings[shop], DestinationTargets.Resolve(false));
        DestinationTargets.Entered(second);
        Assert.Same(city.Buildings[second], DestinationTargets.Resolve(true));
    }

    [Theory]
    [InlineData(Key.J, "drivenrides:notice_no_pin")]
    [InlineData(Key.K, "drivenrides:notice_no_home")]
    public void UnknownDestinationDoesNotOpenMapOrStartTrip(Key key, string notice)
    {
        CityMap.IsOpen = false;
        VehicleHelper.Current = new();
        Press(key);
        Assert.Equal(notice, Notice.Last);
        Assert.Equal(0, OwnCarTaxi.Opens);
        Assert.Empty(taxi.Trips);
    }

    [Fact]
    public void RemovedPinAndMissingOrNonResidentialHomeDoNotResolve()
    {
        SaveGameManager.Current.customDestination = new Address("Missing");
        Assert.Null(DestinationTargets.Resolve(false));
        DestinationTargets.Entered(first);
        city.Buildings.Remove(first);
        Assert.Null(DestinationTargets.Resolve(true));
        DestinationTargets.Entered(second);
        city.Buildings[second].building.BuildingType = "ba:buildingtype_retail";
        Assert.Null(DestinationTargets.Resolve(true));
    }

    [Fact]
    public void RebindingDisablingAndDuplicateBindingsAreHandled()
    {
        SaveGameManager.Current.customDestination = shop;
        Options.PinnedKey = 4; // U
        Press(Key.J);
        Assert.Empty(taxi.Trips);
        Press(Key.U);
        Assert.Single(taxi.Trips);
        Options.PinnedKey = 0;
        Press(Key.U);
        Assert.Single(taxi.Trips);
        Options.PinnedKey = Options.HomeKey;
        Press(Key.K);
        Assert.Single(taxi.Trips);
        Assert.Equal("drivenrides:notice_destination_key_conflict", Notice.Last);
        Assert.Equal(2, DestinationShortcut.ValidKey(-1, 2));
        Assert.Equal(3, DestinationShortcut.ValidKey(99, 3));
    }

    [Theory]
    [InlineData("typing")]
    [InlineData("menu")]
    [InlineData("phone")]
    [InlineData("loading")]
    [InlineData("ride")]
    [InlineData("taxi")]
    [InlineData("activity")]
    [InlineData("subway")]
    [InlineData("hospital")]
    [InlineData("repositioning")]
    [InlineData("time machine")]
    [InlineData("map refused")]
    [InlineData("normal map")]
    [InlineData("on foot")]
    [InlineData("modifier")]
    public void UnsafeOrUnrelatedContextsDoNotDispatch(string context)
    {
        SaveGameManager.Current.customDestination = shop;
        switch (context)
        {
            case "typing": GameManager.BlockKeys = true; break;
            case "menu": UI.MiniMenu.MiniMenu.IsOpen = true; break;
            case "phone": UI.Smartphone.FullMenu.IsOpen = true; break;
            case "loading": UI.Load.LoadScene.isLoading = true; break;
            case "ride": Ride.Active = new(); break;
            case "taxi": TaxiSystem.IsTraveling = true; break;
            case "activity": PlayerActivity.PlayerActivityUI.IsPanelOpen = true; break;
            case "subway": SubwaySystem.IsRiding = true; break;
            case "hospital": EnergyHelper.goingToHospital = true; break;
            case "repositioning": InstanceBehavior<GameManager>.Instance.playerController.awaitingRepositioning = true; break;
            case "time machine": InstanceBehavior<UI.UIs>.Instance.timeMachine.isBlockingUi = true; break;
            case "map refused": CityMap.CanOpen = false; break;
            case "normal map": city.cityMap.Taxi = null; break;
            case "on foot": CityMap.IsOpen = false; break;
            case "modifier": Keyboard.current!.ctrlKey.isPressed = true; break;
        }
        Press(Key.J);
        Assert.Empty(taxi.Trips);
        Assert.Equal(0, OwnCarTaxi.Opens);
    }

    [Theory]
    [InlineData("none", 1)]
    [InlineData("save", 0)]
    [InlineData("close", 0)]
    [InlineData("taxi", 0)]
    [InlineData("exit", 0)]
    [InlineData("blocked", 0)]
    [InlineData("refused", 0)]
    public void OwnCarDispatchesOnceAndCancelsWhenContextChanges(string change, int trips)
    {
        SaveGameManager.Current.customDestination = shop;
        CityMap.IsOpen = false;
        city.cityMap.Taxi = null;
        VehicleHelper.Current = new();
        OwnCarTaxi.Refuse = change == "refused";
        Press(Key.J);
        if (change != "refused") Press(Key.J); // Pending request cannot queue another trip.
        Assert.Empty(taxi.Trips);
        switch (change)
        {
            case "save": SaveGameManager.Current = new(); break;
            case "close": CityMap.IsOpen = false; break;
            case "taxi": city.cityMap.Taxi = new(); break;
            case "exit": VehicleHelper.Current = null; break;
            case "blocked": GameManager.BlockKeys = true; break;
        }
        shortcut.NextFrame();
        Assert.Equal(trips, taxi.Trips.Count);
        Assert.Equal(1, OwnCarTaxi.Opens);
    }
}
