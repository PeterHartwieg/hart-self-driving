# NWH drive spike: ride cars on the game's own car physics

A one-session spike, 13 October 2026, on branch `nwh-drive` (builds 0.1.51-nwh1 to nwh6, on top of 0.1.51). The question: should a ride car be driven by the physics the player's own car uses (NWH Vehicle Physics 2) instead of being a Gley traffic car, keeping the mod's route planner? Peter's framing: "decouple the car physics from the pathing".

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
- nwh6 (giving way before the bend, backing off): PENDING.

### What Gley does with the NWH car

Gley cars see it as the player's car (its colliders are on `Vehicles`, in Gley's `playerLayers`): they stop when it is in their front trigger. At speed that trigger is too short to stop in time, so a car that cuts in is hit, as it is when the player drives.

## Peter's verdict on the look

PENDING.

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

PENDING Peter's verdict.
