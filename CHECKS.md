# In-game checks: planner after T3 on 0.1.80 (nwh-feasible, 0.1.81-planner1)

Disposable save, Food Trucks parked, other gameplay mods off. T3 as before: `tools/turn-tests.sh homepc send run driver=nwh kinds=ride rides=1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18 cars=limo traffic=on speed=auto`, then `tools/turn-tests.sh homepc fetch`.

1. **Routes.** No leg is `avoided` for "no route" on an ordinary street. Expected: ride-1-0, ride-2-0, ride-5-0 with a wide U-turn as a u-curve (17684 -> 99 or a sibling, as on 0.1.70), ride-4-0 with 7408 -> 15092, ride-16-361 with "no added turns".
2. **Every rejection in the log.** One `NWH planner: blocked <kind> <from> -> <to> for <car>: <why>.` line per blocked step, with the numbers: the collider and where, the model's offset from its line, its wheel and whether the margin was on; for pavement the corner, its distance from the nearest lane, and that it lies outside any junction's box. Copy these lines into the results notes for any leg still avoided.
3. **Corners left sharp.** `NWH planner: corner at N left sharp, as the lane runs (...)` lines are notes, not blocks: the leg still drives. Watch one Hamptons guardrail stretch (ride 16): no contact, no kerb.
4. **Junction boxes.** Each result's `judge.JunctionPavement`, if set, names the farthest a body corner went over pedestrian ground inside a junction's box (carriageway by the rule). Over about 4 m, look at that leg's picture or replay it: if the corner is on a real pavement corner or island, the box rule is too wide there.

# In-game checks: traffic for NWH rides (T3, nwh-traffic branch, 0.1.52-traffic1)

Disposable save, Food Trucks parked, other gameplay mods off. Rig only: `tools/turn-tests.sh homepc send run driver=nwh kinds=ride rides=1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20 cars=limo,mersaididash traffic=on speed=1`, then `tools/turn-tests.sh homepc fetch`.

1. **Startup.** One `Traffic reading:` line with `trafficVehicles`, `obstacleList` and `associatedIntersection` all `found`.
2. **Lights.** At a red the car stops with its front about a metre short of the line and goes on green. In the box, `NWH drive: in intersection N` and later `out of intersection N`; while it waits inside for oncoming cars the light stays amber, and the oncoming cars stop for it.
3. **Oncoming cars.** Turning left or U-turning across oncoming lanes it waits short of where its body would enter their lane (log: `giving way to <car> (pose ..., head-on)`), and goes within a few seconds of the last one passing.
4. **Cars that wait for it.** A traffic car stopped across its way with the ride car in its trigger: the ride car creeps past, or backs off (`backing off from traffic car N`) and goes after it. No standstill for good.
5. **Results.** Each results line has `contacts`, `contactsMoving`, `stalls` and `redRuns`. Every failure leaves `<run>-traffic-<n>-<kind>.json` beside the `-nwh.csv`: copy it into `tests/traffic-scenarios/` in the repo and `dotnet test` runs it.
6. **Frame rate** with traffic on stays as it was with the spike (the traffic read is every car within 150 m each physics step).

## Dressed taxi (nwh-taxi branch, 0.1.52-taxi1)

Disposable save, outdoors on a street, Food Trucks parked. The taxi is the drivable car closest in size with the taxi's traffic meshes on it.

1. **Look.** **Ctrl+Alt+Shift+L**: a taxi stands beside you on its handbrake. Log: `Dress donor for ...` (the chosen car and every candidate's score), `Dress parts of ...` (the taxi's renderers and wheels), `Dressed ... as ...` (sizes, wheel mapping, lights, where the look sticks out of the physics box, mismatches). Walk round it: body level and on its wheels, wheels in the arches and touching the ground, nothing of the donor showing, no part floating. Screenshots from the side, front and back, please. Press the key again for a fresh one.
2. **Drive.** From the Mac: `tools/turn-tests.sh homepc send run driver=nwh cars=taxi kinds=turn limit=5 speed=1`. Watch one or two: wheels turn and steer with the car, brake lights at stops. Each result line has `"donor"`.
3. **Remove.** `tools/turn-tests.sh homepc send dress remove` takes the parked one away.

# In-game checks: own car spike (self-driving branch)

Disposable save only. You need a car of your own (one with autopark and one without, if you have both), fuel, and some money. Every line in the log starts with `[Mod:Driven Rides]`; at startup three new patch lines (`TaxiSystem.GetPrice`, `ParkingSimulator.RunHourly`, `CarController.EnterVehicle`) must say `Active`. Press **Ctrl+Alt+Shift+G** once outdoors: the `Traffic pool:` line lists the models a stand-in can come from.

1. **Start.** Sit in your car, stopped on a street. **L** opens the city map; pick a place 1 to 2 km away. The map closes, you ride along as with the driver, the clock follows the clock slider. Log: `Own car: the stand-in is ...` (its own twin, or which car is dressed), `Stand-in dressed: ...` if dressed.
2. **Look.** If dressed: does the car on the street look like yours (body, wheels in place, wheels turning and steering)? Screenshots from close by, please. Lights are expected to be missing in this spike, and a car's own traffic twin keeps its random traffic colour.
3. **Arrival.** You end in the driver's seat, engine on, vehicle camera. Fuel went down a little. Log: `Own car placed where the stand-in stopped`.
4. **Autopark.** Options, Driven Rides, "At the end of a self-driven trip": with *Autopark cars that have autopark*, a car with autopark parks itself (fade, then parked, still in the seat), one without stays on the street. With *Autopark every car*, both park. With *Park the car yourself*, neither. Log: `Own car: autoparking ...` or `no autopark (...)`.
5. **Skip** mid-trip: fade, the car is on the lane nearest the destination with you in it, the clock moved on.
6. **Save** mid-trip and load: you stand beside your car where you started.
7. **Hospital** mid-trip (**Ctrl+Alt+Shift+V**): the car is visible where you started; the hospital takes over.
8. **Parking:** a trip across a full hour mark leaves no new parking fee or ticket for that hour on the car.
9. **Refusals:** L while driving fast says "Stop the car first"; L in a parking garage says "Drive out onto the street first"; a destination too far for the fuel left says the car can't drive there; with the own car option off, L in the car does nothing.
10. A second trip straight after, and a private driver ride after that, both work.

# In-game checks: Milestone 0 (spike, 0.1.0)

Disposable save only, never the series save. Food Trucks parked (`tools/park-remote.sh park` in the Food Trucks repo), other gameplay mods off, so the log is clean. You need a private driver contract with at least one vehicle and some money for taxis. After the session, copy the log with `tools/fetch-log-remote.sh spike` before starting the game again.

Every line in the log starts with `[Mod:Driven Rides]`. At startup it should list six `Patch ...: Active` lines and the options; at city load, `City ready` and `Debug keys ready`.

## 1. Lifecycle (private driver)

Call the driver with **L** (outdoors), then **L** again: you walk to the car and the map opens. Pick a destination 1 to 2 km away.

1. A normal ride. You ride hidden in the car, the camera follows it, a panel at the top shows the distance left and **Skip**. At the end the screen fades and you stand at the destination's door, on foot, with controls. The driver leaves the phone, as after a vanilla ride.
2. Skip in the middle of a ride: fade, you're at the door, the clock has moved to the vanilla arrival time.
3. Pause (Esc) and unpause during a ride: the car stops and goes on; the speed returns to 3x after unpausing.
4. Save during a ride, then load that save: you stand where you got in, on foot.
5. **Ctrl+Alt+Shift+K** during a ride (removes the car): the ride finishes the vanilla way.
6. **Ctrl+Alt+Shift+V** during a ride (hospital): the ride is cancelled and the hospital takes over as usual.
7. Click a cargo item's Read during a ride: refused with "Not during a ride."
8. **Ctrl+Alt+Shift+U** during a ride (an exception in the ride's frame update): you get out where the car is, on foot, and taxis still work.
9. Quit to the main menu during a ride and load again: you're on foot; the log says `Ride cancelled: game unloaded`.
10. **Ctrl+Alt+Shift+J** during a ride (fake save change): the log says `Ride cancelled: the save changed`; you get out on foot with controls.
11. Skip, then **Ctrl+Alt+Shift+X** while the screen is fading: the screen comes back (no black screen), you're on foot.
12. A ride that waits at a red light or in a queue for a whole light cycle: the car waits and then drives on (the log shows no `Watchdog: restart` while it waits).
13. A second ride straight after any of the above works.

After each: you can walk, the clock runs at normal speed, the game speed isn't stuck at 3x, and the log shows `Ride finished` or `Ride cancelled`.

## 2. Routes

Press **Ctrl+Alt+Shift+G** once outdoors (graph stats and ten planning times into the log). Then ten driver rides of 1 to 4 km across Manhattan. For each, note whether it arrived on its own or ended through the watchdog (`Ride ending: Vanilla (watchdog...)`), and anything odd (car turning off the route, stopping in the middle of a junction, driving into things).

## 3. Speed

Three of the rides at 1x and 2x as well (Mods options, Driven Rides, Ride speed). Note the frame rate at 3x compared with 1x.

## 4. Camera

Which camera follows (the log's `Camera: vehicle camera follows ...` line), and whether it's smooth at 3x.

## 5. Shortcut

L calls the driver outdoors and inside a building; L again (outdoors) walks to it; L does nothing while typing in a text field and during a ride.

Pass: lifecycle all clean, at least 9 of 10 routes arrive, every failed route ends through the vanilla finish.
