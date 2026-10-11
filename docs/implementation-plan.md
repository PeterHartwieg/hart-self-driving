# Driven rides: implementation plan

A separate Workshop mod for Big Ambitions, in its own repo (`~/Coding_Projects/big-ambitions-driven-rides`). Taxi and private-driver rides drive through the city instead of fading to black. You sit in the car, the camera follows it, and you get out at the destination. Written 11 October 2026, revised the same day over five rounds of reviews (Fable 5.1, GPT 6 Astra) until both said "ready to build the spike". The reviews are in the Food Trucks checkout's `build/review-rides/` (not committed). Name: **Driven Rides** (Peter, 11 October 2026).

**Peter's decisions, 11 October 2026:** own repo; ride speed 3× by default; the private driver is dismissed after a ride as in vanilla, so it has to be called again, and the mod adds a shortcut for calling it (see [Driver shortcut](#driver-shortcut)).

## Goal and scope

Version 1:

- Rides with the **private driver** and the **taxis** (a taxi hailed from traffic and a taxi stand) drive the real route when both ends are in Manhattan.
- The ride costs the same game time as a vanilla ride, never more.
- A **Skip** button finishes a ride the vanilla way at any moment.
- When the mod can't drive a ride (no route, a parked private driver, the Hamptons), the ride is vanilla from the start. When a driven ride fails midway (stuck car, car removed), it finishes the vanilla way. When something else takes over (the hospital, a game load), the ride is cancelled cleanly without a completion.
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
4. **Drive.** Each frame: the clock budget, the watchdog, ride speed, and a small panel (destination, distance left, **Skip**). The car is **ours** while its `VehicleComponent` is active, its index is the one captured, and its `presetPath` is the very list the mod gave it. Every release, removal or path change first checks that. If it stops being ours, give up.
5. **Arrive.** When one route waypoint is left, `SetVehicleAction(car, StopInPoint, true)` (the vanilla summon's stop) and clear the owned list in place, so `presetPath` stays the same non-null list and the car stays protected from removal. If the list ran out first, Gley has already picked an ordinary next waypoint and the car stops one waypoint further on; the warp to the door covers that.
6. **Finish.** See `Complete` below.

**Shared bookkeeping**, in vanilla's order (`TaxiSystem.cs:86-105`), used by both `Complete` and `FinishVanilla`:

1. Spend any budget left with the time machine, then wait for `GlobalEvents.onTimeMachineEnded` (not just `isRunning == false`, since teardown takes another second).
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

## Clock

Core's `ClockBudget`. The ride must cost vanilla's game time and never more.

- The ride **owns the clock** from the moment the prefix accepts it until `Complete` or `FinishVanilla` has unfaded, and until `Cancel` releases it. Boarding, fades, driving and the closing time machine all count.
- At the start, capture `start = TimeHelper.Now()` and `deadline = start + minutes` (vanilla's own `minutes`).
- Pacing: each frame, `left = deadline − now` in game minutes, and `R` = route length left ÷ the car's observed average speed, both in **scaled** seconds (`Time.deltaTime`), the unit `RunMainGameTick` uses. The pace is `m = clamp(left ÷ R, 0, GameSpeed)`.
- The hard cap is the `RunMainGameTick` prefix: while the ride owns the clock, it replaces the argument with `max(0, min(Time.deltaTime × m, left))` for the game's own tick, and with `max(0, min(argument, left + 0.001))` for the ride's time machine. The floor matters: after the time machine's overshoot `left` is slightly negative, and the game keeps ticking during the time machine's one-second teardown and the unfade, so without it a tick would turn the clock back. The 0.001 is vanilla's own overshoot; without it, float rounding near the target leaves a residue the time machine never clears, and it spins forever. So the ride overshoots vanilla's cost by at most 0.001 game minutes. `GameManager.MinutesMultiplier` is never changed, so there's nothing global to restore.
- When `left` reaches zero the clock stands still for the rest of the ride. The watchdog's deadline bounds how long that lasts. Time is never taken back.
- After the ride, the cap is released and the clock runs normally.
- Expected pace at 1×: about 0.33 (vanilla 40 game min per km over about 120 s per km). The spike log confirms it.

## Ride speed

At 1× a 3 km ride takes about 6 real minutes. **Ride speed** shortens that: 1×, 2× or 3× via `Time.timeScale` during the ride. The default is **3×** (Peter). It is a dropdown on the mod's options page (see [Options](#options)).

- Applied each frame only when the game is unpaused (`UIs.Instance.gameSpeed.Paused` false), no time machine runs, and `GameSpeedController`'s own time-scale tween is finished (read through AccessTools). While paused or tweening, the mod doesn't touch the scale.
- `fixedDeltaTime` is never changed. At 3× physics runs three FixedUpdates per frame: the cost is CPU, which the spike measures.
- On exit, set `Time.timeScale` back to 1 only if the game is unpaused; if paused, leave it to the game.
- If the spike shows trouble at 3×, the default drops to what works and Peter decides. Skip covers impatience.

## Camera

- The spike first logs `vehicleCamera.Follow` and `LookAt`. With the player attached to the anchor, the vehicle camera may follow the car with no change, as it does when the player drives.
- If it does: `SetCameraRoutine(vehicleCamera)`. If not: the pedestrian camera, which follows the carried player.
- Every exit switches back to the pedestrian camera (`Cancel` does it at once, inside the handler).
- Both the anchor copy and the Cinemachine brain run in `LateUpdate` in no fixed order, which can show as jitter at 2× and 3×. The anchor also copies in `Update`; if the spike still sees jitter, point `Follow` at the car for the ride.

## Options

The ModAPI has an options page per mod (`BigAmbitions.Mods.OptionsService.Register(modId, new ModOptions()...)`, the SDK's `Example-Options`). The game stores each value in `PlayerPrefs` under `m:<modId>:<optionId>` (`ModOptionPrefs.cs`) and calls the option's callback only when the options page builds its controls or the player changes one. So the mod reads `PlayerPrefs` with the same keys at load, and takes callbacks for changes after that. Options are player preferences, not save data.

- **Driven rides** (toggle, `rides`, default on): off makes every ride vanilla.
- **Ride speed** (dropdown, `speed`, 1× / 2× / 3×, default 3×).
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
  Core/      LaneGraph, RoutePlanner, GoalPicker, ClockBudget, RideWatch (netstandard2.1, no Unity)
  tests/     xUnit
  unity/DrivenRides/
    Scripts/ DrivenRidesMod, Options, Patches, GleyGraph adapter, RideRunner, RideAnchor,
             RideCamera, RidePanel, DriverShortcut, DebugKeys
  tools/     build-mod.sh, fetch-game-dlls.sh, install-remote.sh, park-remote.sh (from Food Trucks)
  CHECKS.md  in-game checklist for the current milestone
```

`LaneGraph` and `RoutePlanner` are written so Food Trucks can copy them later for its crew trucks.

## Milestones

Each ends in one in-game session on homepc with a disposable save, Food Trucks parked and other gameplay mods off.

### Milestone 0: spike (one session)

The real ride path for the **private driver** only, at 1×, with the minimum around it: the `TravelCoroutine` prefix, the `Update` prefix, anchor and attachment, the route planner, `presetPath` handoff, `StopInPoint` arrival, vanilla placement, the clock budget, Skip, the watchdog, the three exits, and log lines for every step. Plus debug keys:

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
| A tick overshoots the deadline | Ride costs more than vanilla | Cap in the `RunMainGameTick` prefix |
| Time scale fights the game's tween | Unpaused by accident, stuck at 3× | Apply only when unpaused; restore only when unpaused |
| A bug leaves `_travelCoroutine` set | No more taxis until reload | try/finally, unload handler, debug key |
| Graph dead ends or one-way traps | No route | Vanilla ride, logged with waypoint indices |
| A game update changes `TaxiSystem` | Prefix fails | Logged `Not active`; vanilla rides keep working |

## Open questions for Peter

None right now. The decisions so far are at the top.
