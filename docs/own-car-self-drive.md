# Own car self-drive and autopark: design notes

Not part of the implementation plan yet. Written 12 October 2026 from a read of the 1.0 decompile (23 September 2026) and the Gley decompile in `build/decompiled-external/GleyTrafficSystem/`. Nothing here has been tried in the game. Game paths are relative to the decompile's `BigAmbitions/`, Gley paths to `GleyTrafficSystem/`.

## Peter's decisions, 12 October 2026

- **Name and hook.** The mod goes to the Workshop as **HART. Self Driving**, with the hook "FULL SELF DRIVING" on the thumbnail. The thumbnail shows an empty driver's seat, so the player's own car driving itself has to be in the first Workshop release. This reverses the plan's "Not in version 1: the player's own car driven by AI" ([implementation-plan.md](implementation-plan.md), Goal and scope).
- **Own car.** The player can let their own car drive to a destination. The mod swaps the real car for a traffic car and runs the existing ride (route planner, `presetPath`, anchor, ride panel).
- **Every car works, modded cars included.** The mod must not depend on a model being in Gley's traffic pool.
- **Autopark setting.** An option in the mod settings: on, any car autoparks at the destination (easier early game); off, autopark stays a luxury-car feature and the player parks by hand (vanilla's upgrade path).

Thumbnail work is in the Food Trucks checkout's `build/thumbnail-rides/` (not committed).

## Peter's decisions, 12 October 2026 (second round)

- **Starting an own-car ride.** L in the driver's seat of your own car opens the destination map. L anywhere else keeps calling the private driver.
- **Autopark is a three-way choice** on the options page, replacing the on/off toggle above: *park yourself* (you end in the driver's seat on the street), *autopark cars that have it* (`autoParkSupported`), or *autopark every car*. With no free spot found, every choice ends as *park yourself*. Default: *autopark cars that have it* (Peter, 12 October 2026).
- **After autopark you stay in the car**, parked. The ride doesn't put you at the door.
- **Clock.** The same rule as the private driver, but the clock rate during a ride is a setting, on the options page and on the ride panel. The panel's last choice is remembered, for the clock and for the ride speed, and becomes the options page's value.
- **Parking charges** on the hidden car during the ride are suppressed.

## Scope for the first Workshop release

Taxis, the private driver and the own car are switched on and off separately on the options page.

1. **Settings.** Toggles for taxis, private driver and own car; the clock rate dropdown; the autopark dropdown. Panel choices for speed and clock written back to the option's PlayerPrefs key, so the options page shows them.
2. **Own car, spike.** L in the driver's seat opens the map; the swap to a stand-in (own model if pooled, else the closest pooled car under a visual copy), the ride, and the swap back with you in the driver's seat. Logs which models are in the pool. In game: does the reskin look right at street level?
3. **Own car, finished.** Autopark per the setting, fuel, parking charges suppressed, save during a ride (the car stays at the start), cargo, map marker, every failure ending with the car on the street and you in or beside it.
4. **Taxis in game.** No hailed taxi or taxi stand ride has run in game yet: fare, achievements, the stand car released, a second ride straight after.
5. **Release prep.** Debug keys gated, locale strings under the new name, README evidence, Workshop packaging, a clean-install check, the thumbnail.

## On NWH driving (from 14 October 2026)

Every ride now drives with the game's own car physics (`docs/implementation-plan.md`, "NWH driving"), so the own car drives itself. The stand-in, its dress, hiding the car and the exit and re-entry are gone (removed after the NWH rides branch, 0.1.90); the sections below "What the game does" are kept for the facts about the game they record, not as the design. What stays: L in the driver's seat, the map, no fare, the autopark setting, the clock slider, Skip and the cancel paths.

- **The ride car** is the player's car as it stands, wrapped for `NwhDrive` (`NwhCar.Wrap`); nothing is spawned or stripped, so the car's own fuel use, damage and parking state carry on.
- **Input** (`OwnCarDrive`): NWH's `autoSetInput` off while the ride drives, back on at every end, the car left on its brakes. The game's steering assist adds its own angle each frame (`CarController.Update`, `_angleAssist`), so a postfix holds `externallyAddedAngle` at 0 during the ride.
- **Getting out** during the trip ends the ride first (`CarController.ExitVehicle` prefix), then the game takes the player out where the car stands.
- **Ride.cs** drives it through the NWH rides seam, `NwhRideCar` (`NwhRides.cs`), filled by `OwnCarTaxi.RideCar`: `PlayerInside` (no attach, anchor, hiding, exit or entry; a cancel leaves the player and the car where they are), `Owned = false`, `End` (the keys back, the drive component off the car), `Arrive` (after a Skip or a given-up trip the car goes to the lane nearest the destination, then autopark).
- **Planning** uses the car's drivable prefab's shape (`NwhPlanner.For`, the car's own vehicle type) and, where Gley's questions need a traffic model (lanes allowed, a free waypoint), the car's traffic twin or the pooled car closest in length (`OwnCarTaxi.PlanPrefab`); nothing is loaded.
- **Open:** a modded car without a `Vehicles/PlayerVehicles/<id>` prefab has no shape to plan with and gets "can't drive itself there"; planning from the live car would cover it.
- **No longer needed:** the parking-charge suppression (the car is the active vehicle the whole time), the fuel charge (the car burns its own), the re-entry refusal.

## What the game does

### Autopark is a teleport

- Each vehicle type has an `autoParkSupported` flag (`Vehicles.VehicleTypes/VehicleType.cs:44`). Which models set it is asset data. `AutoParkSettings.cs` only holds gap and padding sizes.
- A key (`PlayerAction 3`, `GameManager.cs:1115-1120`) or the HUD button (`UI.ItemPanel/ItemPanelUI.cs:336`) calls `ClickAutoPark()` (`ItemPanelUI.cs:467-477`). It requires the car to be `selectedVehicle`, no activity panel open and vehicle navigation not blocked, then starts `VehicleParkingHelper.ParkInClosestSpot()`.
- `ParkingLaneGenerator` builds `AutoParkSpot` triggers in the gaps between parked cars (`ParkingLaneGenerator.cs:912-1199`). They are rebuilt near a car on enter, exit and teleport (`VehicleController.cs:418, 475`; `Helpers/VehicleHelper.cs:718-719`).
- `VehicleParkingHelper.Update` searches only while `controlledByPlayer` is true and the speed is under 10 (`VehicleParkingHelper.cs:196-213`), and picks the nearest free spot long enough for the car (`:72-109`, private `FindNearestFreeSpot`).
- `ParkInClosestSpot` (`:158-194`) needs the public field `availableAutoParkSpot` set and the car not fully broken. It fades, calls `TeleportVehicleToGround` (`VehicleHelper.cs:722`), sets `parkingState = Legal` and calls `UpdateParkingZone`. **It never checks `autoParkSupported` or whether the player is in the car.** A mod can run it on any car that has a `VehicleParkingHelper`: set `availableAutoParkSpot` (search through reflection on `FindNearestFreeSpot`, or a copy of it), then `StartCoroutine(helper.ParkInClosestSpot())`.

No vanilla code drives a player-owned car without input. `SkipBridgeHelper.SkipBridge` (`Vehicles/SkipBridgeHelper.cs:69-91`) and autopark both teleport, then call `SavePosition`.

### The player's car

- `CarController` (subclass of `VehicleController`, wrapping the NWH physics car) drives it. `EnterVehicle` parents the character into the car, sets `ActiveVehicleId` and starts the engine (`CarController.cs:555-583`; `VehicleController.cs:343-419`).
- Saved state is all on `VehicleInstance`: id, type, position, rotation, street, cargo, `parkingState`, tickets, fuel, damage (`VehicleInstance.cs:22-56`), in `SaveGameManager.Current.VehicleInstances`.
- Player cars spawn from `Vehicles/PlayerVehicles/<id>` (`GameManager.cs:1748-1803`; `VehicleHelper.cs:250-295`). The AI twin is `Vehicles/<id>` via `PrivateDriverHelpers.GetAiVehiclePrefab` (`Helpers/PrivateDriverHelpers.cs:64-80`); the private driver already uses it for a car dropped off at a gas station (`UI.Overlays/GasStationOverlay.cs:119`). Mod vehicles have no AI twin.
- `TrafficManager.LoadVehicle` only finds prefabs in the Gley pool; otherwise it logs an error and returns null (`TrafficManager.cs:531-540`). The pool's contents are asset data; `DebugKeys` G logs them.

### What the swap must preserve

- **Keep the real car active.** `OnDisable` unregisters it from `AllPlayerVehicles` (`VehicleController.cs:269-275`), and it only registers in `Awake` (`:164`). Hide its renderers and colliders and freeze it (`SetFreeze`, `:587`).
- **Save position.** Every live `VehicleController` writes its transform on `onSaveGame` (`VehicleController.cs:216, 491-508`); `CarController.SavePosition` also writes fuel (`CarController.cs:730-736`). A save mid-ride stores the car at the start.
- **Map marker** follows the car's transform (`GameManager.cs:1790-1800`), so it stays at the start until the real car moves.
- **Parking.** `SetParkingData` writes the state on exit from the HUD's value (`VehicleController.cs:479-489`), which comes from `UpdateParkingZone` (`CarController.cs:696-728`). Hourly tickets and fees hit every car except the active one (`Helpers/ParkingSimulator.cs:124-180`), so the hidden car is charged during the ride unless the mod prevents it.
- **Cargo** stays on `VehicleInstance` as long as the real car object lives.
- **Fuel.** Charge it as the bridge skip does (`SkipBridgeHelper.cs:97-120`).
- `OnExitBuilding` destroys a car whose address matches the building being left (`VehicleController.cs:223-240`); a street-parked car shouldn't trigger it.

## Making every car drivable

### Adding cars to the pool: vanilla cars only

- `CityManager.InitTrafficComponent` counts `trafficComponent.vehiclePool` and passes the count to `Manager.Initialize` (`CityManager.cs:171-172`). `VehiclePool` is a ScriptableObject of `CarType[]` (prefab, count, `canBeAiDriven`; `VehiclePool.cs:9-26`, `CarType.cs:14-23`). `TrafficVehicles.Initialize` instantiates each car once (`TrafficVehicles.cs:40-50, 235-250`).
- `TrafficManager.Initialize` sizes every per-car and per-wheel array once (`TrafficManager.cs:383-435, 443-473`). **Nothing can be added after init.**
- A Harmony prefix on `TrafficManager.Initialize` could swap in a cloned pool with extra entries and raise `nrOfVehicles` (a `ref` argument). Clone the asset, or the additions pile up on every city load. Keeping the extra cars out of random traffic needs an out-of-range `VehicleGroupType` (`TrafficVehicles.cs:54-67, 91-98`), which is a hack. Only types known at city load are covered; a car bought mid-session needs a reload.
- A modded car would need a full AI prefab built at runtime: `rb`, `allWheels` (`wheelTransform`, `wheelGraphics`, radius, suspension; `Wheel.cs:16-30`), `frontTrigger` with a child `BoxCollider`, `visibilityScript` (`VehicleComponent.cs:246`), `length`, `coliderHeight`, `wheelDistance`, `colliderWidth`, the AI layer (`TrafficDespawner.cs:9`), and three private fields only the editor fills (`allColliders`, `engineSound`, `vehicleLights`; `VehicleComponent.cs:196-202`). Brittle, with physics tuning, and likely to break on game updates.

### Reskinning a pooled stand-in: every car

Borrow any pooled car with `LoadVehicle`, as the rides already do, hide its body and show a visual-only copy of the player's car on it.

- **Wheels.** Gley's wheel job sets each `wheelGraphics` transform's position (suspension) and local rotation (spin, and steering on the front wheels) every frame (`UpdateWheelJob.cs`, scheduled at `TrafficManager.cs:1345`). Copies of the player car's wheel meshes, parented under those transforms with zero local offset, spin and steer with them. Offset by the radius difference if the wheels differ in size.
- **Hidden body.** `VisibilityScript` reacts to its own renderer (`GleyUrbanAssets/VisibilityScript.cs:26-35`); with it hidden the car reports "not in view", but `CanBeRemoved` is false while `presetPath` is set (`VehicleComponent.cs:413-419`). Visibility otherwise only gates sound and lights on a sleeping car (`TrafficManager.cs:1274-1277`).
- **Lights.** Brake and turn lights are on the donor's light renderer (`VehicleLightsComponent.cs:10`). Keep that renderer on or copy the lights.
- **Size.** Pick the inactive pooled car closest to the player car: pooled cars have public `length` and `colliderWidth` (`VehicleComponent.cs:63, 105`); the player car has `VariableCenterOfMass.dimensions` (used at `VehicleParkingHelper.cs:91`). The pool is readable through `AccessPool` (`DebugKeys.cs:106`). Collisions and gaps in traffic follow the donor's collider.
- **Cost.** A builder that copies the player car's renderers without colliders or scripts, wheel re-parenting, lights, and a small size mismatch.

**Recommendation.** If the car's own AI twin happens to be pooled, borrow it as is. Otherwise reskin the closest pooled car. This touches no Gley init, covers modded cars and cars bought mid-session, and relies only on `LoadVehicle` and `presetPath`.

## The ride, step by step

1. **Board.** Vanilla `ExitVehicle()` on the real car. Hide and freeze it in place, keeping it active. Pick the stand-in (own twin if pooled, else the closest size), build the visual copy, `LoadVehicle` it at the car, attach the player to the anchor and start the existing ride runner.
2. **Drive.** As a private-driver ride.
3. **Arrive.** Remove the stand-in. `TeleportVehicleToGround` the real car to the stand-in's pose, clearing anything in the way (`DestroyBlockingVehicles`, `VehicleHelper.cs:297`). Show it, `EnterVehicle()`, charge fuel, `SavePosition`.
4. **Park.** Setting on: find a spot and run `ParkInClosestSpot` on any car. Setting off: run it only if `autoParkSupported`, and only when the player presses autopark as in vanilla; otherwise the player is in their car on the street and parks by hand.

## Open questions

- Where the car stops at the destination before autopark: on the street lane nearest the door, then the spot search from there.
- In game: which models are in the Gley pool (`DebugKeys` G), and whether a reskinned stand-in looks right at street level.
