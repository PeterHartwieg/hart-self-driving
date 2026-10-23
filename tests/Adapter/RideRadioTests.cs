using System;
using Helpers;
using UI;
using Xunit;

namespace DrivenRides.Adapter.Tests;

[Collection("Game adapters")]
public sealed class RideRadioTests : IDisposable
{
    private readonly GameManager manager = new();
    private readonly GameInstance save = new();
    private readonly UIs ui = new();
    private RideRadio? session;
    public RideRadioTests()
    {
        InstanceBehavior<GameManager>.Instance = manager;
        InstanceBehavior<UIs>.Instance = ui;
        SaveGameManager.Current = save;
        PlayerHelper.Headset = false;
        VehicleHelper.Current = null;
        UnityEngine.Debug.Errors.Clear();
    }

    private RideRadio Start() => session = RideRadio.Begin(manager, save)!;
    public void Dispose() => session?.End();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BoardingEnablesTheRadioEvenWhenItWasAlreadyPlaying(bool muted)
    {
        manager.radioPlayer.IsMuted = muted;
        Start();
        Assert.False(manager.radioPlayer.IsMuted);
        Assert.True(ui.smartphoneUI.radioControls.Enabled);
        Assert.True(RideRadio.Owns(manager.radioPlayer));
        Assert.Equal(new[] { true }, manager.radioPlayer.onRadioToggle.Values);
    }

    [Fact]
    public void LeavingOnFootStopsTheRadioAndCleanupIsIdempotent()
    {
        var ride = Start();
        ride.End();
        ride.End();
        Assert.True(manager.radioPlayer.IsMuted);
        Assert.False(ui.smartphoneUI.radioControls.Enabled);
        Assert.False(RideRadio.Owns(manager.radioPlayer));
        Assert.Equal(2, manager.radioPlayer.MuteCalls);
        Assert.Equal(new[] { true, false }, manager.radioPlayer.onRadioToggle.Values);
    }

    [Fact]
    public void HeadphonesEquippedDuringTheRideKeepPlayingAfterExit()
    {
        Start();
        PlayerHelper.Headset = true;
        session!.End();
        Assert.False(manager.radioPlayer.IsMuted);
        Assert.True(ui.smartphoneUI.radioControls.Enabled);
        Assert.False(RideRadio.Owns(manager.radioPlayer));
    }

    [Fact]
    public void HeadphonesRemovedDuringTheRideDoNotKeepRadioAliveAfterExit()
    {
        PlayerHelper.Headset = true;
        Start();
        PlayerHelper.Headset = false;
        Assert.True(RideRadio.Owns(manager.radioPlayer));
        session!.End();
        Assert.True(manager.radioPlayer.IsMuted);
        Assert.False(ui.smartphoneUI.radioControls.Enabled);
    }

    [Theory]
    [InlineData("save")]
    [InlineData("manager")]
    [InlineData("radio")]
    public void StaleRideCannotChangeTheNewWorldsAudio(string replacement)
    {
        Start();
        var oldRadio = manager.radioPlayer;
        if (replacement == "save") SaveGameManager.Current = new();
        if (replacement == "manager") InstanceBehavior<GameManager>.Instance = new();
        if (replacement == "radio") manager.radioPlayer = new();
        var currentRadio = InstanceBehavior<GameManager>.Instance.radioPlayer;
        currentRadio.IsMuted = false;
        Assert.False(RideRadio.Owns(oldRadio));
        session!.End();
        Assert.False(currentRadio.IsMuted);
        Assert.False(oldRadio.IsMuted);
        Assert.True(ui.smartphoneUI.radioControls.Enabled);
    }

    [Fact]
    public void OldCleanupCannotSilenceASubsequentRide()
    {
        var oldRide = Start();
        Start();
        oldRide.End();
        Assert.False(manager.radioPlayer.IsMuted);
        Assert.True(RideRadio.Owns(manager.radioPlayer));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public void ExitRespectsARealVehicleRadio(bool hasRadio, bool portable, bool shouldMute)
    {
        Start();
        VehicleHelper.Current = new() { vehicleType = new() { hasRadio = hasRadio, spawnInPlayerObject = portable } };
        session!.End();
        Assert.Equal(shouldMute, manager.radioPlayer.IsMuted);
    }

    [Fact]
    public void MissingPhoneUiDoesNotBreakAudioCleanup()
    {
        ui.smartphoneUI.radioControls.ThrowOnEnable = true;
        var ride = Start();
        Assert.False(manager.radioPlayer.IsMuted);
        Assert.Single(UnityEngine.Debug.Errors);
        ride.End();
        Assert.True(manager.radioPlayer.IsMuted);
        Assert.False(RideRadio.Owns(manager.radioPlayer));
    }
}
