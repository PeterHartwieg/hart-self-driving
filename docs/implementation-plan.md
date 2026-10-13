# Driven rides: implementation plan

A separate Workshop mod for Big Ambitions, in its own repo (`~/Coding_Projects/big-ambitions-driven-rides`). Taxi and private-driver rides drive through the city instead of fading to black. You sit in the car, the camera follows it, and you get out at the destination. Written 11 October 2026, revised the same day over five rounds of reviews (Fable 5.1, GPT 6 Astra) until both said "ready to build the spike". The reviews are in the Food Trucks checkout's `build/review-rides/` (not committed). Name: **Driven Rides** (Peter, 11 October 2026).

**Peter's decisions, 11 October 2026:** own repo; normal world speed by default with the clock at a third of its speed during a ride, and buttons on the ride panel for 2×, 3× and Skip (this replaced the first choice, 3× by default with the clock held to vanilla's cost, after the first test rides); the private driver is dismissed after a ride as in vanilla, so it has to be called again, and the mod adds a shortcut for calling it (see [Driver shortcut](#driver-shortcut)).

**Peter's decision, 13 October 2026:** every ride is driven with the player car's physics (NWH) instead of as a Gley traffic car. The plan for that is [NWH driving](#nwh-driving-from-13-october-2026); the sections before it still describe the Gley ride, whose lifecycle carries over.

## Goal and scope

Version 1:

- Rides with the **private driver** and the **taxis** (a taxi hailed from traffic and a taxi stand) drive the real route when both ends are in Manhattan.
- During a ride the clock runs at a third of its speed. Skip charges vanilla's time for the rest of the way.
- A **Skip** button finishes a ride the vanilla way at any moment.
- When the mod can't drive a ride (no route, a parked private driver), the ride is vanilla from the start. The Hamptons are driven too, across the bridge (Peter, 11 October 2026; the lane graph is one network from Manhattan to x -3,380). When a driven ride fails midway (stuck car, car removed), it finishes the vanilla way. When something else takes over (the hospital, a game load), the ride is cancelled cleanly without a completion.
- Prices, achievements, events, happiness modifiers and the limo's energy and food bonus stay as in vanilla.
- A **driver shortcut**: one key calls the private driver, and once it waits, the same key walks you to it and opens the destination map. Vanilla needs the phone, the driver app and a vehicle each time.

Not in version 1: the Hamptons, a passenger-seat camera, getting out at the curb and walking to the door (version 1 places you at the door like vanilla), the player's own car driven by AI, Food Trucks crews driving (a later reuse of the route planner).

## What the game does (code facts)

All from the 1.0 decompile (23 September 2026) and Gley in `ExternalPlugins.dll`. Paths are relative to the decompile's `BigAmbitions/` and to `GleyTrafficSystem/`.

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

## How the mod hooks in

**One Harmony prefix on `TaxiSystem.TravelCoroutine(target, minutes)`**, the method that builds the iterator, not the iterator body. Vanilla's `TravelTo` has already run its busy check, fence check and payment, and the city map is still open and covers the screen. The whole prefix body sits in try/catch. Until a stand's car is loaded it changes nothing; after that, the catch removes that car (`TrafficManager.RemoveVehicle`) before returning true, so vanilla runs with nothing left behind. The ride's static state (context, clock cap, owned car) is committed only as the very last step, so a failed prefix can't leave a cap or a borrowed car. It:

1. Reads `cityMap.Taxi` now, before anything closes the map, and captures the ride context in plain fields: the taxi, target, `minutes`, start time, the owners it belongs to (`SaveGameManager.Current`, the `PlayerController`, the `GameManager`), the happiness modifier name, and for the private driver its vehicle type name (for the limo bonus if the component is gone by the end).
2. Decides **driven** if all hold: the taxi is a `MonoBehaviour` with a `VehicleComponent` whose index is ≥ 0 (so not a parked private driver), no activity panel is open (`PlayerActivityUI.IsPanelOpen`), the player and the destination are in Manhattan, and the route planner returns a route within 100 ms. For a stand, the start is `GetClosestWaypoint(stand, 50, filter, checkFreeFor: prefab)`, which only returns a free waypoint.
3. Stand only, as the last step: `LoadVehicle(vehiclePrefab, spawnWaypoint)` now, with no yield between the free-waypoint check and the load, the car turned to face `path[0]`, and hard-stopped at once (`TriggerChangeDrivingStateEvent(index, StopNow, 0)`, as vanilla's summon does) so it waits for Depart. The prefix first checks that an inactive pooled car of that prefab exists, comparing `vehicle.prefab` with `vehiclePrefab.gameObject` as `LoadVehicle` does (`TrafficManager.cs:531`), so `LoadVehicle` doesn't fall back to an active one. A null result returns true: vanilla, nothing to undo. The map hides the car appearing.
4. If driven: sets `__result` to the mod's ride iterator with the context and returns false. `TravelTo` stores it in `_travelCoroutine`, so `TaxiSystem.IsTraveling` is true for every vanilla check. The mod clears the field through an AccessTools field reference, and only while its own ride is the one running.
5. Otherwise returns true and vanilla runs unchanged.

Nothing is charged twice, nothing calls vanilla's private coroutine, and a failed plan costs nothing extra.

Other patches:

- `PrivateDriverVehicle.Update` prefix: skip while the mod owns that car.
- `GameManager.RunMainGameTick` prefix: while a ride owns the clock, cap the tick (see [Clock](#clock)). It checks `SaveGameManager.Current` against the ride's save on every call, and on a mismatch passes the tick through and drops the cap.
- `PlayerActivityUI.CanStartActivity` prefix: false while a ride owns the player (so reading cargo, and with it FastForward and the activity time machine, can't start). Skipping the method also skips vanilla's notice, so the prefix shows a one-line "Not during a ride" notice when `showNotification` is true; otherwise the click looks dead.
- `TimeMachine.StartTimeMachine` prefix, on the five-argument overload that the one-argument one forwards to (`TimeMachine.cs:170-175`): a time machine the ride didn't start runs `Cancel` first, so the cap never holds back someone else's time machine.
- `PlayerHelper.SaveCurrentPosition` postfix: the save position during a ride (see [Saves and loads](#saves-and-loads)).

No FastForward patch: with activities blocked it can't be reached. If traffic is paused anyway, the watchdog sees no progress and the ride finishes the vanilla way.

Each patch logs one `Active` or `Not active` line at startup with the method name.

## The ride

One iterator (`RideRunner`). Its body sits in try/finally for exceptions and normal exits. A game unload is the case the finally doesn't cover, so the unload handler does its own full cleanup (see the exits below). After every yield the iterator first checks the ride's cancelled flag and returns if set. The context has its own fields; nothing needed for finishing lives only on a pooled component.

1. **Board.** `ResetNavigation()` (drops the goal the player walked with), close the map as vanilla does. Create a mod-owned **anchor** object at the car. `AttachToObject(anchor, NavigationBlocker.Taxi)` (this also sets the blocker), hide the character. The anchor copies the car's pose in `Update` and again in `LateUpdate`. Parenting to the anchor, not the car, means a pooled car that Gley deactivates or reuses never takes the player with it.
2. **Get the car.**
   - `TaxiController`: the clicked car. `taxi.DriveAway()` restores its recorded state.
   - `PrivateDriverVehicle`: the summoned car. The `Update` prefix now leaves it alone.
   - `PermanentTaxiController`: the car the prefix loaded and hard-stopped.
3. **Depart.** Give Gley a mutable copy of the route as `presetPath`; keep the immutable route in the context for progress. `ForceSetVehicleActiveAction(car, Forward)` to clear any `StopNow`, then `VehicleUpdateWaypoint(car)`. Once `GlobalEvents.onCityMapClosed` has fired, switch the camera (see [Camera](#camera)).
4. **Drive.** Each frame: the watchdog, ride speed, and a small panel (distance left, 1×/2×/3× and **Skip**). The car is **ours** while its `VehicleComponent` is active, its index is the one captured, and its `presetPath` is the very list the mod gave it. Every release, removal or path change first checks that. If it stops being ours, give up.
5. **Arrive.** When one route waypoint is left, `SetVehicleAction(car, StopInPoint, true)` (the vanilla summon's stop) and clear the owned list in place, so `presetPath` stays the same non-null list and the car stays protected from removal. If the list ran out first, Gley has already picked an ordinary next waypoint and the car stops one waypoint further on; the warp to the door covers that.
6. **Finish.** See `Complete` below.

**Shared bookkeeping**, in vanilla's order (`TaxiSystem.cs:86-105`), used by both `Complete` and `FinishVanilla`:

1. For Skip and the vanilla finish, run the time machine for vanilla's charge for the rest of the way, then wait for `GlobalEvents.onTimeMachineEnded` (not just `isRunning == false`, since teardown takes another second).
2. Achievement counter for the taxi type, `GameEvent.Invoke("")`.
3. Place the player: move the transform to the target's NavMesh position (`NavMesh.SamplePosition`; fallback, the pickup position), `DeattachFromObject(Taxi)`, `WarpSafely` there, check the agent is on the NavMesh, show, `Character.Reset()`.
4. `ba:gameevent_completedtaxiride`.
5. `taxi.OnTravelFinished()` if the component is alive; for a private driver whose component is gone, the captured limo check (`HappinessHelper.AddModifier("ba:happinessmodifier_limo")`, `EnergyHelper.GenerateEnergy(5)`, `ChangeHunger(10)`) instead. Then the captured happiness modifier.

**Vehicle release**, exactly one operation per exit:

- `Complete`, taxis: if the car is ours, `SetVehicleAction(car, Forward, clearOthers: true)` (clears `StopInPoint`, without which `WaypointRequested` issues nothing), `presetPath = null`, `VehicleUpdateWaypoint`. It rejoins traffic.
- `Complete`, private driver: `DismissPrivateDriver(force: true)`, non-instant, so the car drives off and the driver leaves the phone, as after a vanilla ride (Peter: the driver has to be called again; the shortcut makes that one key).
- `FinishVanilla`: vanilla's own release, with the mod's car accounted for. A copy of `IsValidWaypointForLeaving` (allowed cars not empty, `WithinHeight(5)`, `IsNotIntersection`) picks a waypoint within 50 m of the destination.
  - `TaxiController` or private driver, car still ours: `taxi.InstantiateVehicle(waypoint)`. It removes or dismisses the ride car first, so the load reuses that same, now inactive, car.
  - Stand, car still ours: release the borrowed car first (`SetVehicleAction(Forward, clearOthers: true)`, `presetPath = null`, so it is removable again), then `InstantiateVehicle(waypoint)` only if an inactive pooled car of that prefab exists.
  - Car no longer ours (Gley reused it): no respawn. The passenger part of the ride finishes; another car would only risk taking an active slot.
  - No waypoint: the private driver is dismissed (`DismissPrivateDriver(force: true, null, instantRemove: true)`); a taxi or stand car that is still ours is released as above.

**Three exits**, each written once and idempotent; a finished ride ignores later triggers:

- `Complete` (arrival): fade, shared bookkeeping, release, then restore the camera and the time scale, unfade, and hand the clock back.
- `FinishVanilla` (Skip, watchdog give-up, car no longer ours): fade, shared bookkeeping, vanilla release, restore, unfade, hand the clock back.
- `Cancel` (hospital, unload, another time machine): drop the mod's own state only, synchronously inside the event handler or prefix, before the interrupting system goes on, while the ride's player and managers are still valid. Stop the anchor, move the transform to the nearest NavMesh point, `DeattachFromObject(Taxi)`, show the character, switch to the pedestrian camera, clear the `Update` prefix flag, release the clock cap, set `Time.timeScale` back if the mod changed it and the game is unpaused, clear `presetPath` and the arrival action if the car is still ours, destroy the anchor, clear `_travelCoroutine`. No time machine, no warp to the destination, no completion. The mod never touches the camera again afterwards, so the hospital's own camera change wins. The iterator sees the cancelled flag on its next step and returns.
- `Dispose` (the save changed under the ride, without an unload event): a new save doesn't prove the old objects are gone, and destroying the anchor would destroy any child still parented to it. So: if the captured player still exists and its owners (the captured `PlayerController` and `GameManager`) are alive, run the `Cancel` steps through those captured references only, including putting `Time.timeScale` back if the mod changed it (it is global, and nothing resets it without a scene load). Otherwise, unparent whatever is still under the anchor (`SetParent(null, true)`) before destroying it, and leave the camera, time scale and `_travelCoroutine` alone. Clear the mod's static state either way. The game replaces the save without an unload only at an editor or dev start (`GameManager.cs:422-426`); every in-game load fires `onGameUnloaded` first (`LoadScene.cs:27-31`), so `Dispose` is a guard and the spike expects `Cancel` in the log.

Cancel triggers: `GlobalEvents.onHospitalRespawnStarts`, `GlobalEvents.onGameUnloaded` (it fires before the city scene goes, `LoadScene.cs:27-31`, so the owners still exist), and a time machine the ride didn't start. `Dispose` trigger: `SaveGameManager.Current` is no longer the ride's save, checked each frame and in the tick prefix.

## Route planner

Pure C# in Core, with tests. The adapter builds a **topology snapshot** from Gley once per city load: for each waypoint its position, neighbours, `otherLanes`, allowed vehicle types, intersection flag and `maxSpeed` (converted to m/s). Flat arrays plus a grid index for nearest-waypoint queries. Live state, `temporaryDisabled`, is read through a callback during each search, so a snapshot never goes stale.

- **A\*** over the directed lane graph (`neighbors` only). Edge cost is travel time (length ÷ `maxSpeed`). A penalty per intersection entry makes routes prefer avenues. The heuristic is straight-line distance ÷ the top speed in the graph. No `otherLanes` edges: on a preset path Gley jumps to the next waypoint without its lane-change checks (`DrivingAI.cs:480-484, 506-526`), so a lane change would need its own checked adapter. Not needed while drop-off is at the door.
- **Filter** by the car's Gley vehicle type (`allowedCars`) and by `temporaryDisabled`.
- **Start.** A live car: `GetTargetWaypoint(car index)`, the waypoint Gley already drives to. A stand: a neighbour of the spawn waypoint, with the car rotated to face it.
- **Goals.** Waypoints within 60 m of the destination's NavMesh target, outside intersections, within 3 m height, on a straight stretch of at least 15 m, any lane. Each goal links to one virtual terminal node with its walking time (distance ÷ 1.4 m/s) as cost, so A* minimises driving plus walking in the same unit. The pavement-side lane comes back with curb drop-off, after version 1.
- **Budget.** 100 ms in the prefix, synchronous, because the prefix must decide at once. Over budget, the ride is vanilla. Replans during the ride get the same budget, spread over frames.

## Watchdog

Core's `RideWatch` gets the car's progress along the immutable route, the time (unscaled, excluding pauses) and events; it returns `Ok`, `Restart`, `Replan` or `GiveUp`. Every limit is hard.

- **After a bump or a long stop:** no 2 m of progress for 15 s → `Restart` (`ForceSetVehicleActiveAction(Forward)` and `VehicleUpdateWaypoint`).
- **Still stuck:** no 5 m of progress for 30 s, red lights included → `Replan` from the car's target waypoint. The spike logs the light cycle (`greenLightTime`) and these limits are set from it so two red phases at a busy light don't end a ride.
- **After a replan:** no 5 m of progress in the next 30 s → `GiveUp`.
- **Ride deadline:** three times the expected driving time from the plan, plus 60 s → `GiveUp`.
- **Car gone or reused** → `GiveUp`.

So the player sits in a stuck car for at most about 75 s before the vanilla finish.

## Clock and ride speed

Peter, 11 October 2026, after the first test rides: the world runs at normal speed and the clock at a third of its speed while you ride. Buttons on the ride panel set 1×, 2× or 3× (`Time.timeScale`, applied only while the game is unpaused and its own time-scale tween has finished; `fixedDeltaTime` is never changed), and **Skip** finishes the ride the vanilla way. The options page sets the speed a ride starts at (default 1×).

- The `RunMainGameTick` prefix multiplies each tick by ⅓ while a ride owns the clock. Ticks are game minutes from scaled time, so at 3× the clock runs at its normal rate. The ride's own time machine is left alone.
- A ride's game time is its driving time at a third of the clock's rate, usually more than vanilla's teleport. Vanilla charges 0.04 game minutes per metre of straight line, which the first test showed is very cheap: 6.6 game minutes for a 964 m drive.
- Skip and the vanilla finish run the time machine for vanilla's charge for the straight line from the car to the destination. An arrival charges nothing more.
- The first design held the ride to vanilla's cost with a clock budget; in play the clock looked stopped, so it was dropped.

## Camera

- The spike first logs `vehicleCamera.Follow` and `LookAt`. With the player attached to the anchor, the vehicle camera may follow the car with no change, as it does when the player drives.
- If it does: `SetCameraRoutine(vehicleCamera)`. If not: the pedestrian camera, which follows the carried player.
- Every exit switches back to the pedestrian camera (`Cancel` does it at once, inside the handler).
- Both the anchor copy and the Cinemachine brain run in `LateUpdate` in no fixed order, which can show as jitter at 2× and 3×. The anchor also copies in `Update`; if the spike still sees jitter, point `Follow` at the car for the ride.

## Options

The ModAPI has an options page per mod (`BigAmbitions.Mods.OptionsService.Register(modId, new ModOptions()...)`, the SDK's `Example-Options`). The game stores each value in `PlayerPrefs` under `m:<modId>:<optionId>` (`ModOptionPrefs.cs`) and calls the option's callback only when the options page builds its controls or the player changes one. So the mod reads `PlayerPrefs` with the same keys at load, and takes callbacks for changes after that. Options are player preferences, not save data.

- **Driven taxis** (toggle, `taxis`, default on) and **driven private driver** (toggle, `driver`, default on): off makes those rides vanilla. Hailed taxis and taxi stands share the first.
- **Ride speed** (dropdown, `startspeed`, 1× / 2× / 3×, default 1×). The panel's buttons change it during a ride and write it back, so the next ride starts at the last choice (Peter, 12 October 2026).
- **Clock during rides** (slider, `clockpercent`, 0 to 100 %, default 33 %): 0 stops the clock, 100 is normal speed. The panel has the same slider and writes it back the same way.
- **Stop at red lights** (toggle, `lights`, default on).
- **Driver shortcut** (toggle, `shortcut`, default on).

## Driver shortcut

Vanilla calls the private driver through the phone: open it, open the driver app, pick a vehicle. The mod adds one key, **L**:

- **No driver called:** calls the vehicle called last in this session, else the first in `SaveGameManager.Current.privateDriverVehicleInstances`, through the phone's own `SmartphonePrivateDriverUI.OnClickVehicle(vehicleInstance)`. That keeps vanilla's contract payment, the subway check, the indoor summon and the "unreachable" notice. With no driver vehicles, the key shows vanilla's empty-view text as a notice.
- **Driver waiting:** `PrivateDriverVehicle.OnClickToSetDestination()`, the same as clicking the car: the player walks to it and the destination map opens. Outdoors only.
- **During a ride, with a text field focused, or with any modifier held:** nothing.
- The vehicle called last is remembered for the session only, not in the save. The phone stays the way to pick a different vehicle.

Why L: the game's bindings ignore modifiers and use W A S D F Q E P O T I N M B C H R (plus Z, Y and digits in the interior designer). G, J, K, L, U, V and X are unbound and sit in the same place on QWERTY and QWERTZ (Food Trucks' key research, its `docs/implementation-plan.md`). Food Trucks uses Ctrl and Ctrl+Alt with those letters, and Peter's other mods use Ctrl+Shift+G, J and K. Plain L clashes with none of them. Other Workshop mods may use L; a later version can offer the free letters as a dropdown on the options page.

## Saves and loads

Rides are never saved. Nothing is added to the save file, so there's no schema.

- A save during a ride (the Esc menu stays open to the player) writes the **pickup position**: where the player stood when they got in, a known NavMesh point. It is captured at boarding, with the player's rotation, and the `SaveCurrentPosition` postfix writes both while a ride runs. Loading that save puts the player back on foot where they got in, with the fare spent and the game time so far passed. This is an interrupted ride, not a finished one.
- On unload, `Cancel` runs inside the `onGameUnloaded` handler and does the whole cleanup itself, because Unity drops the iterator without running its finally block. The game resets `Time.timeScale` on load (`LoadingScreen.cs:118`). If the save changes without that event, `Dispose` drops only the mod's own state and never touches the new game.

## Layout

The Food Trucks layout and tools, copied:

```
big-ambitions-driven-rides/
  Core/      LaneGraph, RoutePlanner, GoalPicker, RouteProgress, RideWatch (netstandard2.1, no Unity)
  tests/     xUnit
  unity/DrivenRides/
    Scripts/ DrivenRidesMod, Options, Patches, GleyGraph adapter, RideRunner, RideAnchor,
             RideCamera, RidePanel, DriverShortcut, DebugKeys
  tools/     build-mod.sh, fetch-game-dlls.sh, install-remote.sh, park-remote.sh (from Food Trucks)
  CHECKS.md  in-game checklist for the current milestone
```

`LaneGraph` and `RoutePlanner` are written so Food Trucks can copy them later for its crew trucks.

## NWH driving (from 13 October 2026)

Peter, 13 October 2026, after the spike (`docs/nwh-drive-spike.md`): every ride is driven by the car physics the player's own car uses (NWH Vehicle Physics 2), not as a Gley traffic car. The route planner, the arcs, the ride lifecycle above and the turn rig stay. His goal for this part: "a scalable and robust implementation that does not need a lot of individual fixes for niche situations". The Gley version took about fifteen builds of per-case fixes (turn lines, U-turns, the pivot, the wiggle), and the spike's three traffic runs hit three different junction failures. This plan is built so that traffic doesn't turn into another chain like that. Fable 5.1 gave input on the architecture (13 October 2026); its Gley claims below were checked against the decompile.

### Rules for the work

- **No case lists.** No list of junctions, waypoints or cars that are avoided or treated specially. What a car can't do comes out of the feasibility check; what it must wait for comes out of the conflict test. `TestedSteps` stays a regression record, not a mechanism.
- **One car model.** One kinematic model of the NWH car in Core, calibrated from rig traces, is used by the planner, by the Core tests and by the rig's judge. When the planner and the game disagree, the model is wrong, and the fix goes into the model.
- **Gley's data, not guesses.** Rights of way come from Gley's own waypoints and intersections. The mod predicts other cars only where Gley has no answer.
- **Every in-game failure becomes an offline test.** A failed traffic run dumps the state of every Gley car near the ride car, and that dump becomes a Core scenario. A fix is shown to pass offline before the next session at the game PC.

### What Gley does at junctions

From the Gley decompile in `build/decompiled-external/GleyTrafficSystem/`:

- Traffic lights are a `stop` flag per waypoint. A light holds yellow while its intersection's `carsInIntersection` is not empty (`TrafficLightsIntersection.cs:102`). `Waypoint.Passed` fills that list at `enter` waypoints and empties it at `exit` ones (`Waypoint.cs:65-75`).
- Priority junctions are `PriorityIntersection.IsPathFree`: round robin by road, at most three cars inside (`PriorityIntersection.cs:95-102`). A car asks through the public `Waypoint.CanChange()` (`Waypoint.cs:55`).
- Everything else is the front trigger: `StopInDistance`, follow, a 5 s temporary stop, `AvoidForward`, reverse, and a 3 to 5 s stop after a crash. Gley has no rule for giving way to oncoming cars before a left turn.
- A car with a `presetPath` skips Gley's junction logic: `WaypointRequested` returns after `SetNextWaypoint` while the path has entries (`DrivingAI.cs:466-484`).
- Gley cars treat a car on the `Vehicles` layer as the player and stop for it when it is in their front trigger (`VehicleComponent.cs:529-534`). That trigger is short, so a Gley car that cuts in at speed hits it, as it hits the player.

### Options weighed

- **The mod's own defensive driver** (the spike's way): prediction, gaps, timeouts. Each junction type needs its own rule; that is how the spike got three different failures in three runs.
- **A hidden Gley "ghost" car on the same path that the NWH car follows on a leash.** Rejected. A ghost on a preset path skips Gley's junction logic like any preset-path car. Its own ways out of a jam (`AvoidForward`, reverse) leave the line, so the leash breaks exactly in the jams. In every added turn it pivots on the vias while the NWH car drives the arc, and the two bodies drift metres apart.
- **The NWH car in Gley's arrays.** Rejected. Gley sizes every array once at `TrafficManager.Initialize` and its jobs move the body themselves.
- **Chosen:** rights of way from Gley's data, the ride car counted in Gley's intersections, conflicts from other cars' intent, and one priority rule. Four layers, below.

### The four layers

**Layer 0, physics.** The NWH car stays on the `Vehicles` layer. Gley cars treat it as they treat the player. Nothing to tune.

**Layer 1, rights of way from Gley's data.** On the path's waypoints: `stop` (the spike has this), `giveWay` with `CanChange()` at `enter` waypoints, and membership: `IIntersection.VehicleEnter(-1)` when the car's front passes an `enter` waypoint and `VehicleLeft(-1)` at the `exit` one. `IIntersection` is public, `associatedIntersection` is private (AccessTools), and `carsInIntersection` is only counted and removed from (`GenericIntersection.cs:40-48`), so `-1` as the ride car's index is safe; there is one ride at a time. Effect: lights hold yellow while the ride car is in the box, and priority junctions count it. `VehicleLeft(-1)` also runs in every exit, after any teleport and 20 s after entering at the latest, so a junction can't stick on yellow.

**Layer 2, conflicts from intent.** Each physics step, Core gets every active Gley car within 60 m: position, velocity and its target waypoint (`GetTargetWaypoint`). From the lane graph that gives the car's corridor: the lane to its target and, before it has chosen at a junction, every successor. Core sweeps the ride car's body (not its line) along the next 40 m of its line at the planned speeds and finds the first place where the body enters a corridor that another car will occupy before the ride car has cleared it. That place is the yield point. One test covers oncoming cars on left turns and U-turns, merges, the right turn onto Broadway and cross traffic on a shared green. Because it sweeps the body, a U-turn yields where the body reaches the oncoming lane, well before the line crosses it (the spike's U-turn crash). The box cast ahead stays, for parked cars and people only.

**Layer 3, one priority rule.** A standing Gley car that stands because of the ride car is not a conflict. That is readable: `GetCurrentAction()` is public, and the car's `obstacleList` (private) says whether the ride car's collider is what it stopped for. So: a moving car, the ride car yields; a car stopped for the ride car, the ride car goes at creep speed, with the gap check; both stopped head-on in one lane, the ride car backs off along its line (nwh7). This replaces the spike's timers.

**Parked cars in lanes** belong to the planner, not the driver. The car's footprint is swept along each planned lane step against the static layers, parked cars included; a blocked step is blocked for the session (`GleyGraph.BlockStep`) and the route is planned round it. The first 200 m are checked when the ride is planned, the rest ahead of the car as it drives. Parked cars don't move within a session, so the cache holds.

### What the car can drive

One check: simulate `PursuitDriver` on the ride's `DriveLine` with the Core car model, and judge the track with the rig's own `Core.Testing.Judge` (lane, far side early, contact by sweeping the body box against the world). The planner asks the question the rig asks: would this manoeuvre fail?

- **The model.** The kinematic car in `PursuitDriverTests` becomes `Core.Drive.CarModel`: bicycle model, NWH's steering smoothing and its 180°/s rate, the lock at speed, the planned speed profile. Calibrated per car from the rig's `-nwh.csv` traces; the measured error becomes the margin. `TurnDrive` (the Gley model) goes when the Gley path goes.
- **Arcs.** `TurnArc.Fit` with the car's smallest radius at corner speed, then simulated and judged.
- **U-turns.** No via tuning. A U-turn is feasible if the simulated one at 12 to 15 km/h stays inside the far carriageway and clear of the world; otherwise that step is removed for this car and the route goes round the block. Cached per car and junction for the session. Long cars will rarely pass, as Peter wants.

### Tests

- **T0, Core, milliseconds.** Scenarios: a lane-graph fixture, scripted Gley cars with targets and speeds, the ride car. Templates for a four-way light, a priority T, a merge, a U-turn with oncoming cars, head-on in one lane. 500 seeds per template on every `dotnet test`, as Food Trucks' soak. Invariants: no body overlap, no stall over 30 s except at a red, never past a `stop` while it is set. The spike's three failures are the first three scenarios.
- **T1, Mac, seconds.** Every case in the catalogue simulated and judged against the dumped lane graph (`tools/rejudge`). The diff between two builds is the regression gate.
- **T2, game, traffic off.** The 30 turns and 33 U-turns of `q4`, two cars, `speed=auto`, about 10 minutes. It measures the model against NWH physics, not behaviour.
- **T3, game, traffic on.** 20 recorded rides, plus junction scenarios built by loading Gley cars with a `presetPath` timed to meet the ride car. Judged on: contact with a traffic car (and which one was moving), stalls, red runs (the front passes a `stop` waypoint while it is set), and ride time against free-flow time. About 40 runs, 40 minutes, unattended over ssh. On a failure it writes the T0 scenario dump.

Peter's time goes to T2 and T3 starts and to judging the look; the regressions are caught by T0 and T1.

### Phases

1. **Measure (one rig run).** The layer collision matrix for `Vehicles` against Gley's and the parked cars' layers; `VehicleEnter(-1)` holds a light and `VehicleLeft(-1)` releases it; `CanChange()` callable from outside; `obstacleList` readable; the cost of reading every Gley car's target per physics step. Exit: those five answers in the log.
   Done 13 October 2026 (0.1.54, `measure`, log `build/logs/game/measure2-*`). The ride car's layer, `Vehicles`, collides with Gley's cars (`AiVehicles`), parked cars, `Default` and `Roads`, and ignores `Human`: it drives through people physically, so people stay in the box cast. Gley's cars ignore parked cars. Manhattan has no priority junctions: the 25 give-way waypoints that belong to an intersection are all in the Hamptons; 68 more have none (merges). `CanChange()` answers from outside. Every active Gley car's `obstacleList` is readable. Reading every Gley car within 60 m costs 30 µs a physics step (median, 257 µs at most). `VehicleEnter(-1)` held two lights on yellow for 19 s each, and `VehicleLeft(-1)` let them change 0.3 s later. Lights run 8 s green, 1 s yellow. A newly active intersection is reset by Gley, which clears the count.
2. **Core traffic model and T0.** Layers 1 to 3 in Core, the scenario format, the five templates. Exit: 500 seeds clean on every template, the spike's three scenarios pass.
3. **Feasibility planner.** `CarModel`, the simulate-and-judge check, parked cars in the planner. Exit: T1 plans only what T2 passes, and T2 is at or above the Gley numbers of turn test `t13` on the cases it drives.
4. **Traffic in game (T3).** Exit: 20 rides with no contact with a traffic car the ride car caused, no red run, ride time at most twice free-flow, junction scenarios 95% or better. **Decision point.** If two fix rounds don't get there, fall back to the hybrid: NWH for the own car, Gley with the 0.1.51 patches for the rest. Nothing is wired into real rides before this, so the fallback costs no rework, and phases 2 and 3 improve the Gley path's planner as well.
5. **Rides.** The own car first: the player's real car with `autoSetInput = false`, no `StandIn` and no swap (`docs/own-car-self-drive.md` shrinks to autopark, fuel, parking charges and the map marker). Then the private driver: its summoned Gley car swapped for an NWH car of the same type at boarding, while the map covers the screen. Then the watchdog on NWH progress, Skip, the bridge skip as a teleport of the NWH car, and 2× and 3× (the rig has only run NWH at about 4.7× with no traffic).
6. **Per-car calibration.** The 15 drivable models through T2. A car that can't do a manoeuvre is left out of it by the feasibility check, never by a hand list.

**Taxis** (Peter, 13 October 2026): a drivable taxi, built as a re-skin. The taxi has no drivable prefab, so the mod spawns the drivable car closest in size, hides its renderers and puts the taxi traffic model's meshes on it (body, lights and the wheels on NWH's wheel visuals): the reverse of `StandIn`'s dress. It runs beside phases 1 to 4 and is checked in the rig like any other car (T2, then T3).

### Risks

| Risk | What happens | Answer |
|---|---|---|
| Gley cars cut in at speed (their trigger is short) | Contacts the ride car didn't cause | T3 logs who was moving; yield margins grow if these dominate |
| The `-1` membership sticks | A light stays yellow | `VehicleLeft` on every exit and a 20 s limit; phase 1 checks it |
| Layer assumptions are wrong | Gley cars ignore or hit the ride car | Phase 1's collision matrix |
| The model is off at speed or on slopes | The planner allows what the car can't drive | T2 traces; over 0.5 m, wider margins, not more model |
| Gley's `AvoidForward` swings a car into the ride car's lane | Head-on stops | Counted in T3; the back-off rule covers it |
| Reading every car each physics step is slow | Frame rate drops | Phase 1 measures it; only cars within 60 m |
| NWH at 2× and 3× | Physics at a larger time step | Phase 5 rides at each speed; physics step stays fixed |

### Who does what

One session (this one) takes phases 1 to 4 and the drivable taxi, with subagents in their own worktrees. The own-car session (branch `self-driving`) has paused the stand-in and takes the own-car part of phase 5 once phase 4 passes.

## Milestones

Each ends in one in-game session on homepc with a disposable save, Food Trucks parked and other gameplay mods off.

### Milestone 0: spike (one session)

The real ride path for the **private driver** only, at 1×, with the minimum around it: the `TravelCoroutine` prefix, the `Update` prefix, anchor and attachment, the route planner, `presetPath` handoff, `StopInPoint` arrival, vanilla placement, the ⅓ clock, Skip, the watchdog, the three exits, and log lines for every step. Plus debug keys:

Debug keys on Ctrl+Alt+Shift with the unbound letters, so they clash with nothing (see [Driver shortcut](#driver-shortcut)):

- **G**: graph stats (waypoint count, vehicle types in the pool, connected components, whether the Hamptons side connects, the light cycle) and planning time for ten fixed routes.
- **X**: force-clear the ride state (if a bug leaves `IsTraveling` stuck).
- **K**: remove the ride car mid-ride (tests the give-up path).
- **V**: drain energy to send the player to the hospital mid-ride (tests `Cancel`).
- **U**: throw an exception inside the ride iterator (tests the finally block).
- **J**: make the ride's captured save differ from `SaveGameManager.Current` without an unload, with the player still attached (tests `Dispose`: it must run, and the player must survive, detach and get controls back).

Ride speed is set on the options page.

In order:

1. **Lifecycle.** One short private-driver ride. Then Skip mid-ride, pause and unpause mid-ride, a save mid-ride and its load, the car removed mid-ride, the hospital mid-ride, reading a cargo item mid-ride (must be refused), an exception mid-ride, quitting to the main menu mid-ride and loading again, loading another save from the menu mid-ride (expect `Cancel` in the log, not `Dispose`), and a second ride straight after. Each must end with the player on foot, visible, controllable, the clock and time scale normal, the game time spent no more than vanilla's, and taxis usable again.
2. **Routes.** Ten rides of 1 to 4 km across Manhattan. Log arrival, time, replans, the clock multiplier, and where `StopInPoint` left the car.
3. **Speed.** The ten routes run at the 3× default; three of them again at 1× and 2× to compare frame rate and arrivals.
4. **Camera.** Which camera follows; whether it is smooth.
5. **Shortcut.** L calls the driver outdoors and indoors, L again walks to it and opens the map, L does nothing while typing and during a ride.

Pass: lifecycle all clean, at least 9 of 10 routes arrive, and every failed route ends safely through `FinishVanilla`. Anything less stops the milestone; I report to Peter with the logs before building more.

### Milestone 1: private driver, finished

The panel's polish, locale strings, the README evidence rows.

### Milestone 2: taxis

`TaxiController` and `PermanentTaxiController`, with the per-type boarding and release above. Price, achievements and events checked against vanilla. `FinishVanilla` for a stand ride (the borrowed car released, no second car stolen), and a reused taxi car (a debug key that removes the ride car and spawns traffic until its slot is taken).

### Later (not version 1)

Getting out at the curb and walking to the door (needs the pavement-side lane, a checked lane-change adapter, and Food Trucks' ground probe for a safe pavement point), the Hamptons (the bridge skip expects a player `VehicleController`, so it needs its own handling), a passenger-seat camera, Food Trucks crews driving.

## Risks

| Risk | What happens | Answer |
|---|---|---|
| Gley stalls or wanders on long routes | Rides give up often | Spike target 9/10; restart, replan, `FinishVanilla` |
| Something else touches the ride car (private driver `Update`, crash freeze, pool reuse) | Car stops or vanishes | `Update` prefix, forced `Forward` on depart and restart, reuse check each frame |
| The player rides on a deactivated object | Player vanishes | Mod-owned anchor, never the pooled car |
| Hospital or load mid-ride | Two systems move the player | `Cancel` synchronously in the event handler, save identity check |
| Time scale fights the game's tween | Unpaused by accident, stuck at 3× | Apply only when unpaused; restore only when unpaused |
| A bug leaves `_travelCoroutine` set | No more taxis until reload | try/finally, unload handler, debug key |
| Graph dead ends or one-way traps | No route | Vanilla ride, logged with waypoint indices |
| A game update changes `TaxiSystem` | Prefix fails | Logged `Not active`; vanilla rides keep working |

## Open questions for Peter

None right now. The decisions so far are at the top.
