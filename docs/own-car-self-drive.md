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

### Unparking and visible parallel parking (17 October 2026)

Peter requested implementation of both, superseding the earlier decision to leave kerb departures manual.
`Core/Parking` owns a bounded pose-space search using `CarModel` and a forward/reverse controller. It has no dependency
on lane graphs, `DrivePlan` or lane shifts. Static obstacles and runtime traffic checks use the full vehicle body.
The Unity adapters (`NwhManoeuvre`, `OwnCarParking`) read the world and apply the resulting inputs to the real car.

Parking9 adds Peter's requirement that navigation account for the parking strips themselves. With autopark enabled,
`ParkingNavigation` reads ordinary parallel strip geometry, including inactive street sections, without generating
remote parking spaces. `Core/Routing/ParkingGoalPicker` selects road endpoints beside those strips, aligned with
their direction, on the closest adjacent lane and the same level, with room for the car and a straight handoff
outside junctions. The normal road planner chooses a directed route to those goals; it can go around the block
before arrival. If none is reachable, the ordinary drop-off search remains available.

On arrival the free-space search uses current occupancy, prefers a compatible space beside the current lane, and
otherwise compares directed road travel plus walking distance. The separate manoeuvre planner still owns only
the final local movement. This strip-aware routing is covered offline and is pending in game.

An own-car ride from a parallel space first manoeuvres to a point on its unchanged road route, before a junction,
then repeats the ordinary join check. Failure returns control in place. Parking retains the existing space search
and options, routes legally to an approach using the road planner when needed, then uses the local planner to enter
the space. The target pose is copied because the game's generated spot objects can disappear during the approach.
The final wheel positions must be legal before completion. The prior checked teleport remains the parking fallback.

Parking10 adds a temporary traffic reservation at Peter's request. `Core/Parking/TrafficReservation` encloses the
whole body sweep, including forward staging, and decides which approaching bodies must brake. A preliminary
reservation begins before the incremental search and is replaced by the planned envelope before movement. The
manoeuvre waits for traffic already inside to clear; newly approaching Gley cars brake outside. Adjacent lanes
that do not intersect the envelope and traffic at other heights remain free. Live collision/pedestrian checks
and the existing bounded timeout remain in force; an occupant unable to leave still causes a safe fallback.

`ParkingTraffic` applies the hold through Gley's public `SetVehicleIgnored` API only during its physics call,
braking horizontal velocity while retaining suspension. A Harmony finalizer releases these temporary flags on
success or exception. Previously ignored cars are left to their owner. No Gley drive action, traffic-light state,
waypoint flag, stored velocity or path is restored on release. Each frame reads the current pool, so a reused slot
has no old hold attached. `PositionValidator.IsValid` rejects spawns whose body overlaps the reserved area. Both
hooks must be installed before a manoeuvre can start. The lease ends on completion, cancellation, Skip, timeout,
controller disable or mod teardown, including cancellation during search. Road planning remains separate.

Unparking and parking are explicit ride phases, with the panel and Skip active. Manoeuvres use 1x world speed while
retaining the road-speed preference and ride clock setting. Input ownership, cancellation and cleanup cover both
controllers. Plans are transient; vehicle position, fuel and parking state use vanilla fields and methods.

Parking12 addresses a headless-test departure failure: a gap can permit a short forward/reverse shunt while
rejecting every full 0.96 m search primitive. The search now retains the checked prefix (at least two samples)
before a collision; clearance and controller safety checks are unchanged. Four captured goals that previously
exhausted the root now solve offline, with mirrored tracking regressions. In test mode, failed searches write
`manoeuvre-worlds.jsonl` beside the rig logs so geometry can be replayed without saves or game types.

Status: Vord unparking checked in game on parking3, including waiting for traffic and continuing the road route.
Parking6 held for a following car, paused/resumed without movement, then fell back to legal parking and returned the
controls with neither drive component left. Parking8 stages farther ahead before reversing and accepts a target
reached within a search rollout; its measured following-car regression passes offline. Parking10 completed a physical parallel park in the isolated headless rig while holding one approaching Gley
car, ending legal with controls restored and no reservation left. Parking12 completed departure after reloading
the tight-gap checkpoint and handed over to road driving. The trip was stopped for the night at Peter’s request;
agent1 was closed and unlocked. Headless checks establish simulation behavior, not rendered appearance. Broader
traffic, strip-routing and interruption tests remain pending. See the current checklist.

### Parking-lot departures (parking13, 21 October 2026)

Nearby legal parallel strips retain the established curbside pull-out. Bays and parking areas without a
same-direction lane within 6 m use the new lot departure. `DepartureEntryPicker` projects
onto directed road segments within 90 m, independent of the parked heading. The local planner connects the car
to a reachable entry, then the normal road join check validates the measured pose before driving begins.
A disconnected exit, exhausted search, obstruction or failed handoff returns control in place.

The local surface snapshot covers a bounded 192 m square. It combines legal parking/aisle regions, ground height,
rendered asphalt and pavement triangles, junction exclusions and physical obstacles. The pedestrian NavMesh alone
cannot distinguish parking aisles from sidewalks. Static city meshes have no readable CPU copy, so the adapter
reads their vertex/index buffers without modifying the scene. Missing geometry retains the pedestrian restriction;
headless mode cannot establish the same aisle access. Curbs constrain both the tyre rectangle and the full body, with only the original legal parking footprint
permitted to overhang. Planning keeps a 20 cm surface buffer; execution checks the actual body so tracking
error can use that buffer without licensing a curb crossing. Body checks also clear vehicles, walls and props
throughout the manoeuvre.

A coarse surface distance field filters and orders lane entries before the bounded pose search. The same snapshot
is reused until the car moves. The departure search runs on a worker using only Core data and a steering limit
sampled on the main thread; cancellation never requires a Unity call from that worker. A second bounded search
can defer the final road heading until near the entry, letting a nose-in car reverse into the aisle first. Every
candidate must also pass a closed-loop Core tracking simulation before movement; a failed candidate leaves the
car stationary while another entry is considered. Obstacle cells accelerate full-body checks; live collision
checks, traffic reservation and the final road join remain separate checks. There is no relocation during departure
and no new save data. Both curbside and lot exits stop inside the road-handoff tolerance, followed by the
measured road join check; arrival parking keeps its precise stopping rule and existing parallel implementation.

The seeded legal-space fixture and coverage runner are described in `turn-tests.md`. Current in-game evidence and
remaining cases are recorded in `CHECKS.md`; passing a geometry replay is not an in-game pass.

### Earlier implementation notes

Every ride now drives with the game's own car physics (`docs/implementation-plan.md`, "NWH driving"), so the own car drives itself. The stand-in, its dress, hiding the car and the exit and re-entry are gone (removed after the NWH rides branch, 0.1.90); the sections below "What the game does" are kept for the facts about the game they record, not as the design. What stays: L in the driver's seat, the map, no fare, the autopark setting, the clock slider, Skip and the cancel paths.

- **The ride car** is the player's car as it stands, wrapped for `NwhDrive` (`NwhCar.Wrap`); nothing is spawned or stripped, so the car's own fuel use, damage and parking state carry on.
- **Input** (`OwnCarDrive`): NWH's `autoSetInput` off while the ride drives, back on at every end, the car left on its brakes. The game's steering assist adds its own angle each frame (`CarController.Update`, `_angleAssist`), so a postfix holds `externallyAddedAngle` at 0 during the ride.
- **Getting out** during the trip ends the ride first (`CarController.ExitVehicle` prefix), then the game takes the player out where the car stands.
- **Ride.cs** drives it through the NWH rides seam, `NwhRideCar` (`NwhRides.cs`), filled by `OwnCarTaxi.RideCar`: `PlayerInside` (no attach, anchor, hiding, exit or entry; a cancel leaves the player and the car where they are), `Owned = false`, `End` (the keys back, the drive component off the car), `Arrive` (after a Skip or a given-up trip the car goes to the lane nearest the destination, then autopark).
- **Planning** uses the car's drivable prefab's shape (`NwhPlanner.For`, the car's own vehicle type) and, where Gley's questions need a traffic model (lanes allowed, a free waypoint), the car's traffic twin or the pooled car closest in length (`OwnCarTaxi.PlanPrefab`); nothing is loaded.
- **Kerb starts** stay the player's (Peter, 14 October 2026): a car beside its lane gets "pull out into the lane first". A pull-out (the line's start moved sideways) was built and driven once in game, then taken out: it shared `DrivePlan.Shifts` with the planner's obstacle shifts, which the look-ahead and the shift blend don't tell apart.
- **Autopark hunts** (0.1.93): the game makes spots only within 6 m of a car the player gets into, out of or teleports, and looks only around the car; the mod makes them on every parking lane within 20, 50, 100 and 150 m in turn and takes the nearest free one long enough for the car (`OwnCarTaxi.Hunt`).
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
