# Decisions in force

Peter's decisions that still hold, the game facts they rest on, and the approaches that were tried and dropped.
Supported behaviour is in [../README.md](../README.md); in-game evidence is in [../CHECKS.md](../CHECKS.md).
The repository is `hart-self-driving`, the player-facing name **HART. Self Driving**, and the mod ID, assembly
and namespace `DrivenRides`.

## Decisions

| Date | Decision |
| --- | --- |
| 11 Oct 2026 | Own repository and Workshop mod. Taxi (hailed and stand), private-driver rides drive through the city, Manhattan and the Hamptons (one lane network across the bridge). |
| 11 Oct 2026 | World at normal speed during a ride, clock at a third of its speed. Ride panel buttons 1×, 2×, 3× and Skip. Skip and every vanilla finish charge vanilla's time (0.04 game minutes per metre of straight line from the car to the destination); an arrival charges nothing more. |
| 11 Oct 2026 | Prices, achievements, events, happiness modifiers and the limo's energy and food bonus stay vanilla. Nothing is charged twice. |
| 11 Oct 2026 | When the mod can't drive a ride it is vanilla from the start. A driven ride that fails midway finishes the vanilla way. When something else takes over (hospital, game load) the ride is cancelled cleanly, with no completion. |
| 11 Oct 2026 | The private driver is dismissed after a ride, as in vanilla. Key **L** calls the driver (last vehicle this session, else the first owned) through the phone's own code; with a driver waiting, L walks the player to it and opens the map. |
| 11 Oct 2026 | Rides are never saved; the mod adds nothing to the save. A save during a ride stores the pickup position. |
| 12 Oct 2026 | Workshop name **HART. Self Driving**; the player's own car driving itself is in the first release. Every car works, modded cars included. |
| 12 Oct 2026 | L in the driver's seat of the own car opens the destination map; elsewhere L calls the driver. |
| 12 Oct 2026 | Autopark is three-way: park yourself, autopark cars that have it (`autoParkSupported`, default), autopark every car. No free spot means park yourself. After autopark the player stays in the parked car. |
| 12 Oct 2026 | Clock rate and ride speed are options; the panel's last choice is written back and becomes the next ride's start value. |
| 13 Oct 2026 | Every ride drives with the player car's physics (NWH Vehicle Physics 2), not as a Gley traffic car. Peter judged NWH "like a car I drive". The route planner and turn rig stay. |
| 13 Oct 2026 | No case lists: what the car can't drive comes from the feasibility check, what it must wait for from the conflict test. `TestedSteps` is a regression record, not a mechanism. One kinematic car model is shared by planner, tests and rig judge; when it disagrees with the game, fix the model. Every in-game failure becomes an offline test. |
| 13 Oct 2026 | U-turns a car can't make cleanly go round the block (few drivers would U-turn a big car). |
| 14 Oct 2026 | Fix only what rides show; this is a quality-of-life mod. Traffic margins stay low (headway about 1.0 s). |
| 17 Oct 2026 | The own car unparks and parks visibly (parallel parking). The local manoeuvre planner stays separate from the road planner. Navigation accounts for parking strips before arrival. A manoeuvre reserves its swept area: traffic inside clears, approaching Gley cars wait outside, overlapping spawns are refused. The checked teleport stays the parking fallback. |
| 21 Oct 2026 | Lot and bay departures use a separate local departure to a directed road entry, then the normal road join check. A failure returns control in place. |
| 21 Oct 2026 | Prioritise shorter trips and assertive driving; an occasional crash is acceptable. Judge tuning by completion, journey time and unnecessary waiting, and record contacts and recovery cost alongside. Preferred sideways acceleration 6 m/s²; arc sizing stays at 4 m/s². |
| 22 Oct 2026 | Taxi and private-driver passengers hear the existing radio; own-car rides keep vanilla radio. No vehicle-entry events are faked. |
| 22 Oct 2026 | Quick destinations: **J** for the pinned building, **K** for the last residential building entered; configurable among Disabled / G / J / K / U / V / X, duplicates refused. They never summon a driver. Home is not saved. |

**Why L, J and K.** the game's bindings ignore modifiers and use W A S D F Q E P O T I N M B C H R (plus Z, Y and digits in the interior designer). G, J, K, L, U, V and X are unbound and sit in the same place on QWERTY and QWERTZ (Food Trucks' key research, its `docs/implementation-plan.md`). Food Trucks uses Ctrl and Ctrl+Alt with those letters, and Peter's other mods use Ctrl+Shift+G, J and K. Plain L clashes with none of them.

**Options.**
The ModAPI has an options page per mod (`BigAmbitions.Mods.OptionsService.Register(modId, new ModOptions()...)`, the SDK's `Example-Options`). The game stores each value in `PlayerPrefs` under `m:<modId>:<optionId>` (`ModOptionPrefs.cs`) and calls the option's callback only when the options page builds its controls or the player changes one. So the mod reads `PlayerPrefs` with the same keys at load, and takes callbacks for changes after that. Options are player preferences, not save data.


## Rejected approaches

- **Rides as Gley traffic cars** (to 0.1.51): about fifteen builds of per-case fixes (turn lines, U-turns, pivots, wiggle), and three traffic runs hit three different junction failures.
- **A hidden Gley "ghost" car the NWH car follows**: a preset-path car skips Gley's junction logic, its jam escapes leave the line, and the bodies drift apart in turns.
- **The NWH car inside Gley's arrays**: Gley sizes every array once at `TrafficManager.Initialize`, and its jobs move the bodies.
- **Every road car on NWH**: feasible but about 100 times Gley's cost per car, and it needs reservations and a rebuilt spawn system. 4 to 6 NWH cars near the camera stays possible.
- **Own car as a Gley stand-in** (hidden real car, pooled or reskinned stand-in, swap back): replaced once every ride drove on NWH. The player's car now drives itself.
- **Adding cars to Gley's pool**: nothing can be added after init; a pool-clone prefix covers only types known at city load, and modded cars would need a hand-built AI prefab.
- **Holding the ride to vanilla's time with a clock budget**: in play the clock looked stopped.
- **Kerb pull-out through `DrivePlan.Shifts`**: obstacle shifts shared that state; replaced by the local manoeuvre planner.
- **The overnight review loop** (0.1.75 to 0.1.80): patched cases nobody had seen; T3 fell from 37 of 38 to 18. Overnight planner checks were the main regression.

## Game facts

From the 1.0 decompile (23 September 2026) and Gley in `ExternalPlugins.dll`; re-check after a game update. Paths are relative to the decompile's `BigAmbitions/` and to `GleyTrafficSystem/`.

**The vanilla ride.** `TaxiSystem.TravelTo(target)` returns at once if `_travelCoroutine != null`, checks the Hamptons private fence, charges `distance × 0.15` for a taxi (free for the private driver) and starts `_travelCoroutine = StartCoroutine(TravelCoroutine(target, minutes))`, where `distance` is a straight line to `target.GetNavMeshTargetPosition()` and `minutes = distance × 0.04 × GameSpeed × taxi.GetTimeMultiplier()` (0.85 for the private driver) (`TaxiSystem.cs:38-68`). The only caller is `BuildingResume.cs:583`, with the city map open in taxi mode.

`TravelCoroutine` (`TaxiSystem.cs:70-118`): hide the character, `ResetNavigation`, set `NavigationBlocker.Taxi`, read `cityMap.Taxi`, call `taxi.DriveAway()`, close the map (which nulls `cityMap.Taxi`, `CityMap.cs:314`), fade, time machine for `minutes`, achievement counter, `GameEvent.Invoke("")`, `WarpSafely(target)`, show, unset the blocker, `Character.Reset()`, `ba:gameevent_completedtaxiride`, `taxi.OnTravelFinished()`, happiness modifier, then `GetClosestWaypoint(player, 50, IsValidWaypointForLeaving, taxi.GetVehiclePrefab())` and either `taxi.InstantiateVehicle(waypoint)` or `DismissPrivateDriver(force: true, null, instantRemove: true)`, unfade, `_travelCoroutine = null`.

**The three taxis** implement `ITaxi`:

- `TaxiController`: a traffic car. Clicking it records its driving state and hard-stops it (`StopNow`, value 60). `DriveAway` restores the state and leaves `presetPath` alone. `InstantiateVehicle` removes this car and loads a new one (`TaxiController.cs:51-81`).
- `PermanentTaxiController`: a taxi stand with a `vehiclePrefab`; `DriveAway` does nothing (`PermanentTaxiController.cs`).
- `PrivateDriverVehicle`: summoned outdoors as a Gley car with a `presetPath` to the player (`PrivateDriverHelpers.cs:90-139`); summoned indoors as a parked `ParkingSimulator` car with no Gley index (`:96-98, 183-193`; `GetVehicleIndex()` returns -1). Its `Update`, while it is the phone's current vehicle, applies `StopInPoint` every frame when it has no target waypoint, and hard-stops the car (clearing `presetPath`) when the player is within 10 m (`PrivateDriverVehicle.cs:175-205, 249-267`). `OnDisable` destroys the component, and `OnDestroy` clears the phone's current vehicle (`:103-117`). It despawns an hour after summoning, on the next hour or time-machine end, if off screen (`:97, 285-302`). Vanilla's `InstantiateVehicle` dismisses the driver and spawns an unregistered traffic car (`:353-363`), so after a vanilla ride the driver is gone from the phone.

**Gley** (modified by the game):

- `VehicleComponent.presetPath`: `WaypointRequested` pops leading entries that are within 5 m or behind the car (dot product < 0), then targets the next one; with the list empty it falls through to normal waypoint choice and drives on (`DrivingAI.cs:447-497`). `PickTurnWaypointIndex` needs the intersection waypoint and its successor in the list (`:643-654`). Overtaking and lane changes are skipped while a path is set.
- With `presetPath != null`, `CanBeRemoved()` is false and `ClearTraffic()` skips the car (`VehicleComponent.cs:413-419`, `TrafficManager.cs:594`). `RemoveVehicle` and `ClearTrafficOnArea` still remove it.
- With `presetPath != null`, a crash's `StopNow` is never cleared when the crash ends (`DrivingAI.cs:289-296`, `TrafficManager.cs:1244-1247`): a bumped car stays stopped until told otherwise.
- `LoadVehicle(prefab, waypoint)` prefers an inactive pooled car of that prefab, else takes any car whose prefab has that name, active or not; `AddVehicleAtPositionNow` removes that car and returns null if the waypoint isn't free (`TrafficManager.cs:524-550`, `DensityManager.cs:328-347`). A loaded car is activated with `Forward` and no path, so it drives off at once unless stopped (`DrivingAI.cs:131-143`). `PermanentTaxiController.InstantiateVehicle` only loads a car; it never releases one.
- API: `GetTargetWaypoint(vehicleIndex)`, `SetVehicleAction(vehicle, action, clearOthers)`, `ForceSetVehicleActiveAction(vehicle, action)`, `VehicleUpdateWaypoint(vehicle)`, `GetWaypoint(int)`, `GetClosestWaypoint(...)` (a linear scan over every waypoint, `DensityManager.cs:234-260`), `GetWaypointOrientation`. `Waypoint.maxSpeed` is km/h (Gley divides by 3.6, `DrivingAI.cs:1137-1139`). `temporaryDisabled` changes at runtime (`WaypointManagerBase.cs:118-124, 208-211`). `otherLanes` has no side.
- A car's front trigger reacts to the player's collider with `StopInDistance` (`VehicleComponent.cs:529-534`).

**The player.** `PlayerController.AttachToObject(transform, blocker)` makes the character and its rigidbody kinematic, disables the capsule, tells Gley the collider is gone, disables the NavMesh agent, parents the player and sets the blocker; `DeattachFromObject(blocker)` undoes it (`PlayerController.cs:940-965`). The scooter and Food Trucks' truck hatch use this pair. Traffic spawns around the player's transform outdoors (`CityManager.cs:64, 193-199`), and culling follows it (`Culling/CullingGroupController.cs:66-68`). `ResetNavigation` drops the pending goal and its callback (`PlayerController.cs:852-871`); `DeattachFromObject` re-enables the agent and calls `ResetPath` at once, so the transform must already stand on the NavMesh (Food Trucks moves it first, `TruckWindow.cs:397-423`). `PlayerHelper.SaveCurrentPosition` writes `LastPlayerPosition` and `LastPlayerRotation` (`PlayerHelper.cs:127-138`); `SaveGameManager.CanSave()` doesn't check rides.

**Time.** `RunMainGameTick(Time.deltaTime × MinutesMultiplier)` adds to `SaveGameManager.Current.Minute` (`GameManager.cs:585, 732`). At 100% one real second is one game minute. `GameManager.SetMinutesMultiplier` is public; `Awake` resets it from `PlayerPrefSettings.GameSpeed`. `GameSpeedController` owns `Time.timeScale`: it tweens it on every pause and unpause (including the city map), and its only speeds are Normal and FastForward (6×), which scales `fixedDeltaTime` and pauses Gley (`GameSpeedController.cs:100-173`); the pause's `ClearTraffic` skips cars with a path, so a ride car would freeze, not vanish (`TrafficManager.cs:586-598, 927-934`). FastForward is only reachable from an activity panel (`PlayerActivityUI.cs:703-709`), and activities can start while riding: `PlayerActivityUI.CanStartActivity` checks vehicle use, not taxi state, and reading cargo starts one (`PlayerActivityUI.cs:174-178, 215-225`; `CargoItemUi.cs:449-462`). `TimeMachine.Update` clamps its tick to `minutesFromNow + 0.001` and stops only at `minutesFromNow <= 0` (`TimeMachine.cs:81-91`). The time machine also ticks through `RunMainGameTick` (`TimeMachine.cs:86`); after it stops, `isRunning` is false, then a second later it resets the game speed and fires `GlobalEvents.onTimeMachineEnded` (`TimeMachine.cs:207-223`). `UIs.Instance.gameSpeed.Paused` is the pause state. `LoadingScreen.cs:118` resets `Time.timeScale`.

**Interruptions.** `EnergyHelper` starts the hospital sequence without checking rides (`EnergyHelper.cs:186-200`). `HospitalRespawn` fires `GlobalEvents.onHospitalRespawnStarts` and in the same frame resets navigation, sets its blocker and plays the faint animation on the character, then fades and moves the player (`GameManager.cs:1853-1886, 1933-1941`). `GlobalEvents.onGameUnloaded` fires on unload; Unity then abandons running coroutines without disposing them, so an iterator's finally block does not run.

**The map.** `CityMap.Close()` starts a coroutine that restarts time, fires `GlobalEvents.onCityMapClosed` and only then restores its own camera state (`CityMap.cs:187-193, 319-324`). Light cycle: `trafficComponent.greenLightTime` (`CityManager.cs:172`).

**Camera.** `CameraHelper.SetCameraRoutine(camera)` switches cameras. `vehicleCamera`'s Follow is set in the scene, and vanilla parents the character into the player's car (`CarController.cs:555-557`), so a carried player may already be what the camera follows. The pedestrian camera follows its configured transform (`PedestrianCam.cs:215-219`). `SubwaySystem` is the vanilla precedent for a visible ride with a hidden player.

**Neighbourhoods.** `Building.Neighbourhood` and `TaxiDestinationData.neighbourhood` (`ba:neighborhood_thehamptons`); the player's side via `ClosestBuildingFromPlayer.Get()`.

**Gley at junctions.**

- Traffic lights are a `stop` flag per waypoint. A light holds yellow while its intersection's `carsInIntersection` is not empty (`TrafficLightsIntersection.cs:102`). `Waypoint.Passed` fills that list at `enter` waypoints and empties it at `exit` ones (`Waypoint.cs:65-75`).
- Priority junctions are `PriorityIntersection.IsPathFree`: round robin by road, at most three cars inside (`PriorityIntersection.cs:95-102`). A car asks through the public `Waypoint.CanChange()` (`Waypoint.cs:55`).
- Everything else is the front trigger: `StopInDistance`, follow, a 5 s temporary stop, `AvoidForward`, reverse, and a 3 to 5 s stop after a crash. Gley has no rule for giving way to oncoming cars before a left turn.
- A car with a `presetPath` skips Gley's junction logic: `WaypointRequested` returns after `SetNextWaypoint` while the path has entries (`DrivingAI.cs:466-484`).
- Gley cars treat a car on the `Vehicles` layer as the player and stop for it when it is in their front trigger (`VehicleComponent.cs:529-534`). That trigger is short, so a Gley car that cuts in at speed hits it, as it hits the player.

**Autopark.**

- Each vehicle type has an `autoParkSupported` flag (`Vehicles.VehicleTypes/VehicleType.cs:44`). Which models set it is asset data. `AutoParkSettings.cs` only holds gap and padding sizes.
- A key (`PlayerAction 3`, `GameManager.cs:1115-1120`) or the HUD button (`UI.ItemPanel/ItemPanelUI.cs:336`) calls `ClickAutoPark()` (`ItemPanelUI.cs:467-477`). It requires the car to be `selectedVehicle`, no activity panel open and vehicle navigation not blocked, then starts `VehicleParkingHelper.ParkInClosestSpot()`.
- `ParkingLaneGenerator` builds `AutoParkSpot` triggers in the gaps between parked cars (`ParkingLaneGenerator.cs:912-1199`). They are rebuilt near a car on enter, exit and teleport (`VehicleController.cs:418, 475`; `Helpers/VehicleHelper.cs:718-719`).
- `VehicleParkingHelper.Update` searches only while `controlledByPlayer` is true and the speed is under 10 (`VehicleParkingHelper.cs:196-213`), and picks the nearest free spot long enough for the car (`:72-109`, private `FindNearestFreeSpot`).
- `ParkInClosestSpot` (`:158-194`) needs the public field `availableAutoParkSpot` set and the car not fully broken. It fades, calls `TeleportVehicleToGround` (`VehicleHelper.cs:722`), sets `parkingState = Legal` and calls `UpdateParkingZone`. **It never checks `autoParkSupported` or whether the player is in the car.** A mod can run it on any car that has a `VehicleParkingHelper`: set `availableAutoParkSpot` (search through reflection on `FindNearestFreeSpot`, or a copy of it), then `StartCoroutine(helper.ParkInClosestSpot())`.

No vanilla code drives a player-owned car without input. `SkipBridgeHelper.SkipBridge` (`Vehicles/SkipBridgeHelper.cs:69-91`) and autopark both teleport, then call `SavePosition`.

**The player's car.**

- `CarController` (subclass of `VehicleController`, wrapping the NWH physics car) drives it. `EnterVehicle` parents the character into the car, sets `ActiveVehicleId` and starts the engine (`CarController.cs:555-583`; `VehicleController.cs:343-419`).
- Saved state is all on `VehicleInstance`: id, type, position, rotation, street, cargo, `parkingState`, tickets, fuel, damage (`VehicleInstance.cs:22-56`), in `SaveGameManager.Current.VehicleInstances`.
- Player cars spawn from `Vehicles/PlayerVehicles/<id>` (`GameManager.cs:1748-1803`; `VehicleHelper.cs:250-295`). The AI twin is `Vehicles/<id>` via `PrivateDriverHelpers.GetAiVehiclePrefab` (`Helpers/PrivateDriverHelpers.cs:64-80`); the private driver already uses it for a car dropped off at a gas station (`UI.Overlays/GasStationOverlay.cs:119`). Mod vehicles have no AI twin.
- `TrafficManager.LoadVehicle` only finds prefabs in the Gley pool; otherwise it logs an error and returns null (`TrafficManager.cs:531-540`). The pool's contents are asset data; `DebugKeys` G logs them.

**Player-car state.** Recorded for the superseded stand-in swap; still true of the game.

- **Keep the real car active.** `OnDisable` unregisters it from `AllPlayerVehicles` (`VehicleController.cs:269-275`), and it only registers in `Awake` (`:164`). Hide its renderers and colliders and freeze it (`SetFreeze`, `:587`).
- **Save position.** Every live `VehicleController` writes its transform on `onSaveGame` (`VehicleController.cs:216, 491-508`); `CarController.SavePosition` also writes fuel (`CarController.cs:730-736`). A save mid-ride stores the car at the start.
- **Map marker** follows the car's transform (`GameManager.cs:1790-1800`), so it stays at the start until the real car moves.
- **Parking.** `SetParkingData` writes the state on exit from the HUD's value (`VehicleController.cs:479-489`), which comes from `UpdateParkingZone` (`CarController.cs:696-728`). Hourly tickets and fees hit every car except the active one (`Helpers/ParkingSimulator.cs:124-180`), so the hidden car is charged during the ride unless the mod prevents it.
- **Cargo** stays on `VehicleInstance` as long as the real car object lives.
- **Fuel.** Charge it as the bridge skip does (`SkipBridgeHelper.cs:97-120`).
- `OnExitBuilding` destroys a car whose address matches the building being left (`VehicleController.cs:223-240`); a street-parked car shouldn't trigger it.
