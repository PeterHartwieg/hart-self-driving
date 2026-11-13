# Real Life Overhaul 1.0.5 compatibility

Recorded 13 November 2026. Source reading and offline tests only. Nothing here has been seen in game yet; the in-game check is pending a batched release rig run.

## What was checked

| Item | Value |
|---|---|
| RLO release | `v1.0.5`, tag commit `a9f51ac697df7c44759421b438bb30f7113bea34` ("Merge version 1.0.5") |
| Release asset | `RealLifeOverhaul-1.0.5.zip`, SHA-256 `765f2501e268c800ffc375557e9cc0bdd0434e619c45e8b676877c10dd03c1b1` (matches the release notes) |
| RLO DLL in it | `RealLifeOverhaul/RealLifeOverhaul.dll`, SHA-256 `0c4b4aea5c339f9131f46d3a72b54657dd79bb0a8f086069243c34e7f6286ae7` |
| Game | build 3682, `BigAmbitions.dll` SHA-256 `9427561b81aaab2ada09c22a3c98dd9a4017a65225b389513885bf336852e911` (RLO targets the same build) |
| This mod | `main` at `f2e8177` (0.4.2) |

The RLO source was read from a scratch clone at that tag. The DLL was checked for what it references: no `0Harmony` assembly reference, and `HarmonyLib` only as strings for the reflection lookup below. Gley's `TrafficManager` and NWH's `VehicleInputHandler` were read from the game's `ExternalPlugins.dll` to settle two questions; nothing decompiled is kept in the repo.

## RLO's Harmony patches

RLO 1.0.5 has one Harmony use, `Scripts/Core/GoingPublicLink.cs`. When Going Public is loaded it borrows Going Public's Harmony by reflection and adds a prefix to `BigAmbitionsMP.MPServer.OnReceive` and to `MPClient.OnReceive`. That is all. Every other "Harmony" hit in its source is the family stat `FamilyState.Harmony`.

So no game method is patched by both mods, and patch order and composition never come up. RLO does call some of the methods this mod patches, and those calls go through this mod's patches as any caller's do.

## Per-hook verdict

This mod's Harmony patches (`Patches.cs`):

| Patched method | What RLO 1.0.5 does with it | Verdict |
|---|---|---|
| `TaxiSystem.TravelCoroutine`, `TravelTo`, `GetPrice` | Nothing. RLO reads `TaxiSystem.IsTraveling` (never writes it) in City Cabs, the offence check, the subway, bus rides and its private driver screen | No conflict. Rides keep `IsTraveling` true, so RLO's own "a taxi carries the player" checks see every ride |
| `PrivateDriverVehicle.Update` | Never touched. Its scenes leave taxis and private driver cars alone (`TrafficTools.SpokenFor`) | No conflict |
| `GameManager.RunMainGameTick` | Not called, not patched. RLO never writes `Time.timeScale` | No conflict |
| `PlayerHelper.SaveCurrentPosition` | Not used | No conflict |
| `PlayerActivityUI.CanStartActivity` | Read as a busy check by its desk and session scenes | No conflict |
| `TimeMachine.StartTimeMachine` | Called by custody, hospital, prison, bus rides, the subway's extra time, parties, travel, outings and the garage | As intended: the prefix ends a ride before any time machine it didn't start. The subway's extra time only starts when `!TaxiSystem.IsTraveling` |
| `DrivingAI.WaypointRequested`, `HasReachedOrPassedWaypoint` | Only `GetBlinkType` is read, for indicators | No conflict |
| `FullMenu.Toggle`, `GameSpeedController.SetPause` | Called when the player opens RLO apps, photo mode, the showroom and similar | No conflict |
| `SfxManager.SetSoundSnapshotCityMap`, `RadioPlayer.OnAccessoryEquipped/UnEquipped` | Not used | No conflict |
| `TrafficManager.FixedUpdate` | Not patched. RLO uses Gley's API heavily (ignore flags, remove, load, target waypoints) and may replace the density manager after repeated errors (`TrafficMend`) | No conflict. Its braking of traffic cars skips cars Gley ignores; see City Cabs below for the one real overlap |
| `PositionValidator.IsValid` | Not called; read as a component when the density manager is rebuilt | No conflict. The patch is on the method, so a rebuilt validator is covered |
| `CarController.Update` | Not touched | No conflict |
| `CarController.ExitVehicle` | Called on the player's car by custody, impound, hospital, prison, permadeath and warps, most after waiting for the car to slow to 1 m/s | As intended: the prefix ends the own-car trip first |
| `ItemPanelUI.ClickAutoPark`, `SkipBridgeHelper.SkipBridge` | With the RLO interface on, its HUD presses the game's own buttons | No conflict: the press still runs the patched method |
| `CarFeatures.UpdateBridgeHiddenState` | Not called. Its chase camera holds the bridge pieces under the player's car visible | No conflict |
| `SmartphonePrivateDriverUI.OnClickVehicle` | With the RLO interface on, its phone screen calls the game's handler | No conflict: the patch still runs |

Game methods this mod calls by reflection, listed in the brief:

| Method | RLO 1.0.5 | Verdict |
|---|---|---|
| `CameraHelper.SetCamera` | Called only for Empire Motors, the showroom and the driving-test car | See the camera note below |
| `DrivingAI.ApplyAction`, `NewDriveActionArrived`, `setVehMaxSpeed` | Not used | No conflict |
| `FenceDoor.IsLocked` | Only a check that the type exists | No conflict |
| `ParkingLaneGenerator.GenerateAutoParkSpots` | Not called. Its bus stop zones shorten lane boxes next to stops, set `spotCountOverride` and call `RegenerateAutoParkSpotsNear` | No code change. The parking code reads lanes and spots when it plans, so it sees RLO's shorter lanes. Pending in game: arriving with autopark next to an RLO bus stop |
| `SkipBridgeHelper.GetFuelConsumption` | Only quoted in a comment | No conflict. The own car burns its own fuel; a bridge skip charges what the game's skip charges |
| `TaxiController.RequestVehicleStop` | Not used | No conflict |
| `TimeHelper.SetCurrentFormattedTime` | Not used | No conflict |
| `VehicleParkingHelper.IsSpotOccupied` | Not called; its traffic drivers read two other fields | No conflict |

No settings IDs or options captions are shared. Both mods store options under their own mod ID, and this mod's captions are `drivenrides:` locale keys. With the RLO interface on, its Options screen lists this mod's rows from the game's own `OptionsService` entries.

## Fixed in this change

### City Cabs: a called taxi's ride started from the wrong place

RLO's City Cabs contact (`taxi_call`, on by default) takes a taxi from Gley's pool, sets `SetVehicleIgnored(true)` and drives it to the caller with its own driver. Gley skips `WaypointRequested` for an ignored car (`TrafficManager.FixedUpdate`), so the cab's target waypoint stays the one it was loaded at, often hundreds of metres back. A hailed-taxi ride starts from `GetTargetWaypoint`, so its route and line started there and not at the cab.

Now a taxi Gley ignores starts on a free lane waypoint just ahead of the cab, running its way. The NWH ride car is placed there, facing the route, while the map still covers the screen. A Gley ride (car physics off) can't take over a car Gley ignores, so it is left to the game with a notice. A taxi Gley drives is planned as before. This ports the `rlo-compat` fix, keyed on Gley's own flag and not on RLO.

When the ride starts, RLO hands the cab back to traffic within a quarter second (`TaxiDirector.Ride`). By then an NWH ride has already put the cab back in the pool, so RLO only lets go of it. Gley clears the ignore flag whenever it puts a car back on the road (`NewVehicleAdded`), so the pool slot isn't left ignored.

Tests: `tests/TaxiStartTests.cs` (the start rule and the "ahead of the cab" test). The planning change is in `Ride.Planning.cs` and is compiled by the SDK build.

### Photo mode: L, J and K reached this mod

RLO's photo mode (F10, on by default) pauses the game and rolls its camera on L and J and picks the next filter on K (`Core/Keys.cs`, `PhotoKeys`). This mod's L calls the private driver or opens the own car's map, and J and K open the own car's map and start a trip. Rolling the camera would do the same.

Now the driver shortcut and the destination shortcuts do nothing while `RealLifeOverhaul.Leisure.PhotoMode.Active` is true. RLO documents the flag as "the mod's keys and panels stand aside". `OtherMods.PhotoMode` reads it by reflection and answers false without RLO.

Tests: `tests/Adapter/DestinationShortcutTests.cs` (`RealLifeOverhaulPhotoModeKeepsItsKeysInTheCar` and the `rlo photo mode` case). Before the change, 3 failed; after it, all pass.

## Checked and left as they are

- **Pedal smoothing, the G throttle cap and breakdowns.** RLO's car tuning and breakdowns act through NWH's `inputModifyCallback`. NWH calls it only when `autoSetInput` is on, and every car this mod drives has it off. They don't reach a self-driving car. A breakdown rolled during a trip takes effect when the keys go back.
- **Walking in the road.** RLO 1.0.5 checks for a carried player itself (`PlayerRide.Read`). A ride sets two of its signs, `TaxiSystem.IsTraveling` and the navigation blocker, so the hidden passenger is never booked. The old `rlo-compat` prefix on `PlayerOffences.OnFoot(Vector3, float)` is no longer needed, and that signature is gone.
- **The interact key.** RLO's "enter with the interact key" refuses while the player's navigation is disabled, which a ride sets.
- **Taxi and parcel shifts in your own car.** A shift starts at a desk on foot, so it never starts during a trip. A self-driving trip during a shift counts as the player driving: fares, crashes and fuel. Fuel is the car's own, so RLO's pump bill matches what was burnt.
- **Delivery trips** are a timeline with no vehicles on the road. **Single parking spaces** are bookkeeping for the game's underground car parks; nothing changes for street spots. **Subway stations** add taxi destinations the ride drives to like any other. The **Insurance app** and the **driving card** only read the car.
- **The camera.** A passenger ride uses the game's pedestrian camera, which RLO's third-person camera re-tunes and follows as on foot. An own-car trip uses the vehicle camera, where RLO's chase camera runs as it does while the player drives. Neither stops the other.
- **The RLO interface (off by default).** Its HUD, phone and map press the game's own buttons (Travel here, the private driver's vehicle, autopark, skip bridge), so every patch above still runs. Nothing in it replaces the taxi system.

## Open product questions

These are things the player would notice, with RLO's defaults on. Each keeps the current behaviour until Peter decides.

1. **Mouse look over the ride panel.** RLO's mouse look (`camera_mouselook`, on by default) locks the cursor while the pedestrian or chase camera is live, so the ride panel needs Left Alt first. RLO's guide gives Left Alt for every window it doesn't know. Options: (a) leave it; (b) patch RLO's `ThirdPersonCamera.IsUiOpen` to report the panel, as `rlo-compat` did, which also stops mouse look during rides. Kept: (a). The mod pack switches mouse look off.
2. **Police during an own-car trip.** RLO's police can signal the player's car to stop, after an offence or on its routine roadside check, about once per 40 minutes at the wheel. A self-driving car drives on: an offence stop then becomes "fleeing" and a chase, and driving on from a roadside check costs 400 and 3 points at the next stop. Options: (a) leave it; (b) end the trip while `PoliceAttention.IsBusy` and keep the brakes on after the keys go back, as `rlo-compat` did. Kept: (a).
3. **RLO's car tuning on the own car.** Power, gearing, the speed limiter (off), a 0.25 s brake build-up and handling changes stay on the car during a trip. Options: (a) leave it, since it is the car the player has; (b) take RLO's tuning off for the trip and give it back after, as `rlo-compat` did. Kept: (a). How the drive copes is pending in game.
4. **Life events and street crime around a passenger.** RLO's `LifeEvents.IsSafeForEvent` doesn't check `TaxiSystem.IsTraveling`, so an event or dialogue can open during a long ride. Its street crime takes a hidden passenger for someone on foot. Options: (a) leave it; (b) patch RLO's gate, as `rlo-compat` did. Kept: (a).
5. **Rides during RLO's driving test, prison or a crime scene.** Nothing stops a ride from starting then. Options: (a) leave it; (b) refuse rides while `LicenceDirector.IsActive`, `Justice.InPrison` or `CrimeDirector.IsActive`. Kept: (a).

## The old `rlo-compat` branch

`origin/rlo-compat` (`d52f084`, 21 October, written against RLO 0.41 and 0.45) can be archived. Do not delete it. Its parts now stand as follows:

- City Cabs start: ported here.
- Photo mode keys: ported here for L, J and K. The old branch covered L only.
- `PlayerOffences.OnFoot` prefix: no longer needed; RLO handles it and the signature is gone.
- Breakdown and pedal handling: not needed. NWH doesn't call the callback for a car this mod drives.
- Tuning hold, police ending, the mouse look patch, the life event gate and the "player held" refusals: open product questions 1 to 5 above.

Its code targets the old single-file `Ride.cs` and RLO 0.4x members, so it can't be merged as it is.
