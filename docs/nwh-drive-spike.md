# NWH drive spike: ride cars on the game's own car physics

A one-session spike, 13 October 2026, on branch `nwh-drive` (builds 0.1.51-nwh1 to nwh7, on top of 0.1.51; nwh7 was built, not run). The question: should a ride car be driven by the physics the player's own car uses (NWH Vehicle Physics 2) instead of being a Gley traffic car, keeping the mod's route planner? Peter's framing: "decouple the car physics from the pathing".

Why: Gley turns a car about its origin, which on the taxi and the limo is the rear axle, so the car visibly pivots on its back wheels; Gley's numbers are also far from the drivable car's (the limo: 1.71 m between the wheels and a 2 m turning circle in Gley, 3.75 m and about 5 m when the player drives it). 0.1.48 to 0.1.51 patched this inside Gley (`DrivableSteering`, `TurnAboutMiddle`); that stays the fallback.

## What was built

Everything is in the turn test rig, nothing in a real ride.

- **The car.** `NwhCar` spawns the drivable prefab (`Vehicles/PlayerVehicles/<id>`) under an inactive holder, so nothing wakes, and takes off every script from the game's own assemblies before it does: `CarController`, `CarFeatures`, `VehicleBlinker`, `VehicleDeformationController`, `VehicleParkingHelper` (they expect a saved `VehicleInstance` and a player). Left: NWH's `VehicleController` with its modules (fuel off), `DamageHandler`, `VariableCenterOfMass`, the four `WheelController`s, colliders. The root goes on the `Vehicles` layer, as the game puts a car the player enters. Fuel off, engine started, `input.autoSetInput = false`.
- **The driver.** Core's `PursuitDriver` (unit-tested, a kinematic car with NWH's steering lag in the tests) on a `DriveLine`: the ride's expanded path with each added turn's driver's arc (`TurnArc`) in place of the corner.
  - Steering: pure pursuit from the rear axle, looking `max(wheelbase + 1.5, 1.5 + 0.45 v)` metres ahead, steered for where the car will be 0.25 s on (NWH smooths the wheel and turns it at 180°/s at most). The wheel angle is written as NWH input, scaled by the car's lock at its speed (`maximumSteerAngle` × `speedSensitiveSteeringCurve`), exactly as the player's keys go in.
  - Speed: the top speed of the vehicle type and Gley's road limits; corners at 3.5 m/s² sideways from the line's curvature over 10 m, never under 12 km/h; no faster than the wheel's present angle allows; long cars at 15 km/h in U-turns; braking at 2.5 m/s². Throttle and brake by a small PI loop. At a standstill it holds on the handbrake, because NWH's automatic gearbox shifts into reverse on the brake.
  - Red lights: Gley's live `Waypoint.stop` on the path's waypoints, braking to the line; a red first seen when stopping would need more than 5 m/s² is driven through, as on amber.
  - Cars ahead: a box as wide as the car cast along its heading (short while turning), counting other cars, parked cars and people only when they stand on the car's line.
  - Giving way: a moving Gley car that will cross the next 25 m of the line within 3 s is waited for, short of the bend that leads there.
  - Deadlock: 5 s held by a standing traffic car across its way (within 3.5 m), it backs off for 2 s.
- **The rig.** `run driver=nwh` drives each case with the drivable car (the taxi has none and is skipped); `traffic=on` keeps traffic and pedestrians. `TestCar` judges a car without a Gley component from its rigidbody and NWH's wheels. Each run writes a `-nwh.csv` trace (along, lateral offset, speed, wanted speed, wheel angle, pedals, gear, stop line, gap, give way). `tools/compare-drivers.py` compares two results files case by case.

## Results

### Turn rig, traffic off (session 20261013-083259, nwh1, auto speed about 4.7x)

The 30 turn and 33 U-turn cases of `q4`, limo and mersaididash, against the Gley run on 0.1.49 (`t13`, its turn-about-the-middle fix came in 0.1.50 and 0.1.51):

| | NWH | Gley (t13) |
|---|---|---|
| Turns, limo | 27/30 | 29/30 |
| Turns, mersaididash | 26/30 | 28/30 (1 avoided) |
| U-turns Gley drove, limo | 13/14 | 14/14 |
| U-turns Gley avoided (round the block) | limo 6/18, dash 7/32 made it | not driven |
| Mean worst lane deviation, passes | 0.27 m | 0.17 m (limo), 0.06 m (dash) |
| Last gate | ≤ 0.03 m off the lane, ≤ 0.2° | ≤ 0.01 m, 0° |

- The turns fail in two places for both cars. `turn-18207-14353`: the exit lane on Broadway has cars parked partly in it, and the NWH car ran into one (Gley's cars don't collide with that layer; a car on the player's layer does). `turn-4644-8640`: a 6.5 m arc at a tight corner; the limo was near full lock, ran 0.45 m wide and its body touched a fire hydrant.
- The U-turns NWH fails are, with one exception, the ones Gley's world check already sends round the block. The drivable limo turns on about 5 m at walking pace; the U-turn vias are tighter. Peter: "U-turns for larger vehicles is basically impossible, drivers will have to drive around the block which is fine and realistic."
- With nwh2 to nwh6 the speeds and the steering changed; the rig set wasn't rerun on them.

### Traffic on (rides 15, 5, 6 from `rides.jsonl`, limo, 1x, Peter watching)

- nwh3: it followed a car to a stop 1.7 m behind it and pulled away with it, and braked for reds (each turned green before it got there). It ran wide out of corners, by up to 3 m after the U-turn (full throttle with the wheel still at lock), and at the first right turn onto Broadway it pulled into a traffic car that had the right of way (no giving way yet). The first U-turn was "incredibly slow" (Peter): the U-turn's vias read as a 2 to 3 m radius.
- nwh5 (steering for the lag, speed capped by the wheel, giving way, corners never under 12 km/h): at a light that turned green as it arrived, it U-turned across the oncoming lanes, which had the same green, and was hit at 13 m/s; it had seen the oncoming car but waited at the point where its line crosses theirs, which in a U-turn the body reaches much earlier. Later it gave way, pulled out, touched a traffic car at 1 m/s and both stopped for good: the Gley car stops for anything on the player's layer in its trigger, and this car for the Gley car 0.3 m ahead. To Peter it looked like running a red light.
- nwh6 (giving way before the bend, backing off nose to nose): at the same junction it stopped for oncoming traffic, then stood 9 s after the oncoming cars had passed (the give-way check kept firing; the trace doesn't say for which car), until a traffic car pulled up 2 m away at an angle and both waited for good. nwh7 widens the back-off to a traffic car standing across the way within 3.5 m; it wasn't run.

Three traffic runs, three junction situations a Gley car never meets, because Gley's own cars settle them among themselves.

### What Gley does with the NWH car

Gley cars see it as the player's car (its colliders are on `Vehicles`, in Gley's `playerLayers`): they stop when it is in their front trigger. At speed that trigger is too short to stop in time, so a car that cuts in is hit, as it is when the player drives.

## Peter's verdict on the look

Asked how the NWH car looked in the turns that went cleanly, in the rig and in traffic, against the Gley version (0.1.51), Peter answered "like a car I drive" (13 October 2026), over "better, not there yet". During the runs he found the first U-turn "incredibly slow" and asked for larger turns to be faster; nwh2 and nwh4 changed that and were not judged separately.

## What it would take to switch

The steering and the speed are the small part. The work is everything a Gley car got from Gley:

1. **Junction rules.** Giving way to oncoming traffic on left turns and U-turns, merging, Gley's give-way waypoints, and gap acceptance that holds up in dense traffic. Gley's own cars rely on its intersection manager and on each other's triggers; the NWH car has neither and must be defensive.
2. **Deadlocks** with traffic cars that stop for it, beyond the back-off.
3. **Parked cars** in lanes: the route or the line has to go round them.
4. **A world check with the real car.** The planner's U-turn and arc checks (`TurnDrive`, `FindBlockedSteps`) model Gley; they would model the NWH car's turning circle instead, and leave out what it can't make.
5. **Taxis.** The taxi has no drivable prefab. Taxi rides would stay Gley cars, or get a drivable taxi built from the traffic model.
6. **The ride around it.** Boarding (swapping the waiting Gley car for an NWH car, or spawning the NWH car where the driver stops), the watchdog on NWH progress, Skip, cancel, the bridge skip (a teleport of the NWH car), arrival, the anchor and camera, time scale at 2x and 3x (physics stays at its step; tested only at about 4.7x in the rig).
7. **Tuning per car** in the rig: lookahead, lag, corner speed, for the 15 drivable models.

## Consequences for the own car

The own-car design (`docs/own-car-self-drive.md`, `OwnCar.cs`, `StandIn.cs`) swaps the player's car for a Gley stand-in dressed as it, because Gley can only drive pooled models. With NWH driving, none of that is needed: the player's own car can drive itself, with the player in the driver's seat, its own camera, its own fuel use, damage and parking state. Setting `autoSetInput = false` on the player's car and writing its input is all the driving needs; `CarController` stays on (it is the player's car). Every car works, modded ones included, because every drivable car has NWH. Autopark is the game's own at the end, on the real car. The stand-in, the dress and the swap back would go. What stays is the same list as above: junction rules, deadlocks, parked cars.

## Recommendation

**Switch every ride to NWH driving** (Peter, 13 October 2026), keeping the route planner, the arcs and the turn rig. The look is what the mod is for, and in Peter's comparison the NWH car has it where the 0.1.48-0.1.51 Gley patches don't. The cost is that traffic behaviour becomes the mod's own code. In order:

1. **Traffic in the rig first.** A `traffic=on` tier of recorded rides at 1x and auto, judged on contacts with traffic and standstills as well as the lane: today's three runs give the first cases (the U-turn junction of ride 15, the right turn onto Broadway). Log which car a give-way or a gap waits for.
2. **Junction rules** in Core, unit-tested with a kinematic car and moving others: give way to oncoming traffic before turning across it, merge only into a gap, Gley's give-way points, a timeout that lets the car go when nothing moves, backing off (nwh7) as the last resort. Read Gley's intersection state (which road has green) instead of predicting cars where that is simpler.
3. **The world check with the NWH car's circle** (`TurnDrive` with the drivable wheelbase and lock, the rear axle as pivot), so the planner leaves out U-turns and arcs the car can't make, and parked cars in a lane are driven round or the lane is avoided.
4. **The own car**, as above: the player's car drives itself, `StandIn` and the swap go.
5. **The private driver**: the summoned Gley car is swapped for an NWH car of the vehicle type at boarding (the map hides it), or the driver arrives as an NWH car.
6. **Taxis**: an NWH car under the taxi's look, the reverse of `StandIn`'s dress (the drivable car of the nearest size, the taxi's meshes on it), or taxis stay Gley cars until that exists.
7. **The ride around it**: watchdog on NWH progress, Skip, cancel, the bridge skip, arrival, time scale at 2x and 3x, and per-car tuning in the rig for all 15 drivable models.

If traffic can't be made to work, the fallback is the hybrid: NWH for the own car, where the player expects their car's physics most, and Gley with the 0.1.51 patches for the rest.

## Running it again

Build 0.1.51-nwh7 from this branch (`tools/build-mod.sh`), install it, and drive the rig over ssh:

- `tools/turn-tests.sh homepc send run driver=nwh kinds=turn,uturn cars=limo,mersaididash speed=auto ids=<the q4 ids>`: the comparison set.
- `tools/turn-tests.sh homepc send run driver=nwh kinds=ride rides=15,5,6 cars=limo traffic=on speed=1`: traffic, to watch.
- `tools/compare-drivers.py <NWH results> <Gley results> Turn,UTurn`.

Results of this session are in `build/tests/nwh-<session>/` (not committed): 20261013-082837 (smoke), 20261013-083259 (comparison), 20261013-084745, 085721 and 090918 (traffic on nwh3, nwh5, nwh6).
