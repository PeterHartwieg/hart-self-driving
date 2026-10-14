# In-game checks: real rides on NWH (nwh-rides, 0.1.90-rides5)

Disposable save, Food Trucks parked, other gameplay mods off, some money for fares. Taxi, stand and private driver rides now drive with the car's own physics, the same planner and driver as `run driver=nwh traffic=on`: the taxi dressed on the drivable car closest in size, the driver in the drivable version of its vehicle.

1. **By hand.** Hail a taxi in traffic and pick a place 1 to 2 km away. When the map closes you ride in a taxi that looks like the one you hailed. Log: `NWH ride planned for <car> in N ms: ...`, `NWH ride: <donor> dressed as <taxi> where the traffic car ... stands`, `NWH ride: traffic car N handed back to the pool`, `Departed (NWH): ...`. At the end you get out beside the car; `NWH ride: traffic car N takes the ride car's place` and the taxi drives off. Same with a taxi stand.
2. **Private driver.** **L** calls the driver, **L** again walks you to it and opens the map. When the map closes you ride in the drivable version of the driver's car (log: `NWH ride: <type> where the traffic car ... stands`). The driver leaves the phone at boarding, not at the end as before; at the end a traffic car of the same model takes the ride car's place and drives off. Called from indoors (a parked driver): the same, from the lane beside the parked car. A limo ride still gives the limo bonus.
3. **Unattended.** `tools/turn-tests.sh homepc send ride kind=taxi min=800 max=1500`, or `kind=stand`, or `kind=driver [vehicle=limo]`. It hails the nearest taxi within 200 m (a stand within 400 m; the driver is called and waited for, up to 3 minutes), picks a random building at that distance (`seed=N` repeats one, `x=.. z=..` names one) and goes through `TaxiSystem.TravelTo`, so the fare is charged. One line per ride in `ride-results.jsonl` (fetch it with `tools/turn-tests.sh homepc fetch`): `driven`, `end` (`Complete: arrived`, `Vanilla: ...`, `Cancelled: ...`), `seconds`, `contacts`, `contactsCaused`, `redRuns`, `stalls`, `arrivedMetres`.
4. **Look for:** the swap at boarding hidden by the map, no jump or bounce when the taxi starts, no contact with traffic, the car stopping at reds, the player beside the car at the end, a traffic taxi in its place that drives on.
5. **Skip** mid-ride (the panel's button): fade, the clock moves on by vanilla's time for the rest of the way, you are at the door, the car is gone (a traffic car of its model drives on where it stopped).
6. **Watchdog.** Hard to force; if a ride stalls, the log shows `Watchdog: Progress stalled ...; giving up the step ahead and planning round it.` after 20 s without 5 m of progress (waiting at a light or behind a car doesn't count), then either `NWH drive: driving on along a new path` or, 30 s later still stuck, `Ride ending: Vanilla (watchdog: No progress after replan)` and the vanilla finish. The drive also gives up a step once by itself after 30 s of game time stuck (`blocked (stuck: no progress for 30 s)`). **Ctrl+Alt+Shift+K** removes the NWH car: `Ride ending: Vanilla (the car is no longer the ride's)`, the vanilla finish.
7. **Bridge.** A ride to the Hamptons (`tools/turn-tests.sh homepc send ride kind=taxi hamptons=on min=3000 max=6000`, or by hand): at the bridge the screen fades, `Bridge: the car landed on waypoint N; M route waypoints on.`, the clock runs the game's bridge time, and the car drives on from the far side. No `bridge: ...` vanilla end.
8. **2x and 3x.** The panel's 2x and 3x buttons, or `send ride kind=taxi speed=3`. The car should drive as at 1x (same stops at reds, same yields, no wider turns); only more physics steps run per frame. Each ride logs `NWH drive: traffic cost over N steps: ... per frame at 2.9x on average (2 physics steps at the median, ...) p50 .. µs, p99 .. µs, max .. µs`: please copy those lines for a 1x, a 2x and a 3x ride. The Mac simulation's numbers (`dotnet test --filter GameSpeedTests`): per frame at 60 fps, p99 0.85 ms at 1x, 1.6 ms at 2x, 2.3 ms at 3x. Note the frame rate at 3x against 1x.
9. **Back to Gley.** Options, Driven Rides, "Rides drive with real car physics" off: the next taxi and driver rides are the Gley rides as in 0.1.84 (log: no `NWH ride` lines, `Departed: N waypoints for Gley`). From the Mac: `send ridemode gley`, then `send ride kind=taxi`; `send ridemode options` goes back to the page's choice. The startup line `Options: ...` ends with `car physics on|off`.
10. **Ends.** Every ride ends with `Ride finished` or `Ride cancelled`, and `NWH ride tally: ...` and a traffic cost line.

# In-game checks: planner after T3 on 0.1.81 (nwh-feasible, 0.1.84-planner4)

Disposable save, Food Trucks parked, other gameplay mods off. T3 as before: `tools/turn-tests.sh homepc send run driver=nwh kinds=ride rides=1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18 cars=limo traffic=on speed=auto`, then `tools/turn-tests.sh homepc fetch`.

1. **Routes.** Rides 1, 2, 4 and 5 as on 0.1.81-planner1 (their 0.1.70 plans). Ride 13 drives its 22 m U-turn and passes: no rig check reads a "lean" any more (its 0.1.81 failure was the limo standing flat on its line). Ride 16-361: the Hamptons gate, shut for the rig's player (it owns nothing behind it). Expect `Gates: N across lanes, M locked` and a `Gate '...' (HamptonsPrivateFenceDoor): shut for the player, doors and trigger over x ..., z ...` line (check the box is about the gate, a few metres, not a whole street), then `NWH planner: the way to the goal goes through the gate ...; the ride ends at waypoint W before it`, and the leg passes, stopping on the lane before the gate with no 'locked' notification and no contact. With a house bought behind that gate in a save (not the rig's), the gate says `opens`, the ride crawls through at 10 km/h and the doors swing open before it.
2. **Every rejection in the log.** One `NWH planner: blocked <kind> <from> -> <to> for <car>: <why>.` line per blocked step, with the collider or corner, the model's offset from its line, its wheel and the margin (0.50 m for added turns and U-turns, 0.20 m along a lane). A lane corner that fails rounded and sharp is `corner at N, rounded or sharp: ...`. Copy these lines into the results notes for any leg still avoided.
3. **Block lifetime.** Within a rig session, a leg whose summary says `no route (N manoeuvres blocked earlier this session)` names how many earlier blocks kept it out. Lane and traffic blocks don't carry over: no later leg should be avoided with nothing of its own listed and N = 0.
4. **Junction boxes.** Each result's `judge.JunctionPavement`, if set, names the farthest a body corner went over pedestrian ground inside a junction's box. Over about 4 m, look at that leg's picture: if the corner is on a real pavement corner or island, the box rule is too wide there.

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

# In-game checks: own car driving itself (NWH)

Disposable save only. You need a car of your own (one with autopark and one without, if you have both), fuel, and some money. Every line in the log starts with `[Mod:Driven Rides]`; at startup the patch lines `TaxiSystem.GetPrice`, `CarController.Update` and `CarController.ExitVehicle` must say `Active`.

1. **Start.** Sit in your car, stopped on a street. **L** opens the city map; pick a place 1 to 2 km away. The map closes and the car drives off by itself with you in the driver's seat and the car's own camera. Log: `Own car: driving itself (...)`, `NWH ride planned for ...`, `Departed (NWH)`.
2. **Keys.** During the trip WASD and the handbrake do nothing; the steering wheel doesn't fight the drive (steering assist held off). The ride panel's speed buttons, clock slider and Skip work.
3. **Arrival.** The car stops near the destination and you're still in the seat with control. Fuel went down as for a normal drive. Log: `Own car: the keys are the player's again (arrived)`.
4. **Autopark.** Options, Driven Rides, "At the end of a self-driven trip": with *Autopark cars that have autopark*, a car with autopark parks itself (fade, then parked, still in the seat), one without stays on the street. With *Autopark every car*, both park. With *Park the car yourself*, neither. Log: `Own car: autoparked` or `no autopark (...)`.
5. **Skip** mid-trip: fade, the car is on the lane nearest the destination with you in it, the clock moved on, then autopark as above.
6. **Getting out** mid-trip (the exit key): the trip ends, the car stops where it is, you're on foot beside it. Log: `Own car: the player got out during the trip`.
7. **Save** mid-trip and load: you sit in your car where it was when you saved.
8. **Hospital** mid-trip (**Ctrl+Alt+Shift+V**): the car stops where it is; the hospital takes over.
9. **Refusals:** L while driving fast says "Stop the car first"; L in a parking garage says "Drive out onto the street first"; L in a car parked at the kerb, or facing against the lane, says it can't drive itself there ("pull out into the lane first"), and works once the car stands in the lane; an empty tank says so; with the own car option off, L in the car does nothing.
10. **Mid-trip:** the game's autopark button and the bridge's skip button say "Not while the car drives itself". A car that runs out of fuel or is wrecked mid-trip stops, with a notice, and you're in it with control.
11. **Bridge:** a trip to the Hamptons skips the bridge as the driver's rides do, and the tank goes down by what the game's own bridge skip charges.
12. A second trip straight after, and a private driver ride after that, both work.

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
