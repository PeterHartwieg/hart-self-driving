## Real Life Overhaul alongside (0.2.0-rc8)

Pending in game. `docs/rlo-compat.md` has what each line is for. Use a disposable save with RLO 0.41.1 (or newer) on, and once with RLO off.

- R1. With RLO off: the log has `Real Life Overhaul: not loaded; its patches are not active.` and no other new line. One taxi ride and one own-car trip behave as in rc7.
- R2. With RLO on, startup or first city: `Real Life Overhaul ... loaded.`, `Patch RealLifeOverhaul.CarTuning.OnUpdate: Active.`, `...PlayerOffences.OnFoot: Active.`, `...ThirdPersonCamera.IsUiOpen: Active.`, and `police found, driving test found, prison found, photo mode found, breakdowns found`.
- R3. Own-car trip with RLO's handling on Full: `Own car: Real Life Overhaul's car tuning taken off for the trip.` at the start. The trip drives, stops and autoparks as in rc7. At the end, `...handed back.` and RLO's own `Car tuning: ...` line again. Drive a little: the car feels like RLO's again.
- R4. A pull-out from a parallel space and an autopark with RLO on, as R3.
- R5. Police: switch "stop at red lights" off and take own-car trips until RLO's police signal you. Press no key. The trip ends with "The police are pulling you over. You have the wheel.", the car brakes to a stop by itself (`Own car: braking to a stop after handing back.`), and RLO's stop goes its usual way, with no fleeing. Once more pressing the throttle right after the notice: the car is yours at once. Steering alone (keys, a stick or mouse steering) doesn't end the braking; the wheel stays straight until the car stands.
- R6. Breakdown: with a broken-down car (RLO's test tools), L says "The car has broken down."
- R7. A taxi ride and a private-driver ride: no jaywalking warning or fine, and with RLO's mouse look on, the ride panel's buttons can be clicked.
- R8. Photo mode: L rolls the camera and calls nobody. During a driving test, L says "Not now."
- R9. City Cabs: call a cab, let it pull in to the kerb, get in, pick a place. The log says `Taxi held by another mod: the ride car starts at waypoint ...`. The car drives you there from the lane, and RLO charges only its call-out and the fare.
- R10. Night taxi rides with RLO's life events and street crime on: no event, pickpocket or mugging during a ride. Right after a pickpocket starts approaching, L and a hailed taxi go the base game's way (or "Not now.").

## Own car: a car another mod gave a type of its own (0.2.0-rc7)

Pending in game. Every own-car trip in Be A Taxi Reworked's shift taxi said "Your car can't drive itself there" (a user's Player.log, 16 October 2026: `Own car: choosing a destination for beataxi_vehicle_name.`, `NWH shape for beataxi_vehicle_name: no drivable prefab.`). The mod gives the Vord Tiara Vic a type named `beataxi_vehicle_name`, and the shape was looked up by that name. Now the own car's drivable type is the first of the type name, the car's vehicle instance and its object's name that has a drivable prefab. With Be A Taxi Reworked on, start a shift, stop the taxi in a driving lane and press L: the trip should drive, with `choosing a destination for beataxi_vehicle_name (drives as ba:vehicletype_vordtiaravic)` in the test log. Check that the fare still counts on arrival.

## Own car: start from its own lane (0.2.0-rc6)

Pending in game. A trip in your own car that once said "Your car can't drive itself there" for one destination but worked for a spot nearby (16 October 2026; log: `Vanilla ride: pull out into the lane first.`). When no route ran from the car's own lane to the kerb on the destination's side, the planner started the route from a nearby lane instead, and the start check then refused it as not in the lane. Now the own car tries the wider drop-offs (the other side, any lane, further out) from its own lane, and never starts from another one. Stop in a driving lane and pick the same destination again: the trip should start, maybe with a drop-off on the other side of the street. A refusal now says why: "Pull into the driving lane, facing the way the traffic goes, first." when the car isn't on its lane (the log line gives the distance, angle or line left, the route's first waypoint and the car's offset from its lane), or "Your car can't find a way there from this lane." when no route leaves it.

## Bridge landing in the rightmost lane (0.2.0-rc5)

Pending in game. Take a driven ride from Manhattan to 10 Seventh Avenue in the Hamptons. On the bridge the car keeps to the rightmost lane, since it turns right at the bottom of the ramp. After the bridge fade the car should land on the ramp (waypoint 17753, 87 m before the turn), on the road and pitched to the slope, visible the whole time, then drive down and make the right turn. The lane there runs 0.58 m under the road (lane 5.86, road 6.44), so the car is set on the road found under the waypoint. It also vanished until the bottom of the ramp because the game hides cars on a bridge it hides from the camera (`CarFeatures.UpdateBridgeHiddenState`), and rc5 keeps `CarFeatures` on ride cars for their paint; a prefix now leaves the mod's own ride cars visible. Seen in game on 15 October 2026: four rides landed on waypoint 17753 and arrived, and on the last one the taxi stayed visible (Peter) while the test log said `Bridge: kept the ride car visible on 'BridgeSideElevatedRoad (1)'`. Still to do: the left lane and the player's own car. It must not back up or fade a second time. Repeat once in the left lane (a destination straight on, as before) and once with the player's own car. Before this fix the car landed close to the junction, couldn't make the turn from a standstill, backed off, and the ride ended the vanilla way (`Ride ending: Vanilla (NWH drive: blocked: could not stop short of turn 16001 -> 3757 ...)`).

## Ocean Crest Road drop-offs (0.2.0-rc5)

Pending in game. In a disposable save, take private-driver rides to 4 and 6 Ocean Crest Road from outside the road, then between the two buildings. The car should drive to the nearest reachable part of the road beside each destination, rather than finish before the cinema. Repeat once with a taxi or the player's car. Check the destination and drop-off positions in each `Ride accepted` line. A genuinely blocked nearer lane must still allow a farther reachable stop; a locked gate must still keep the car outside.

0.2.0-rc4 failed in game at 6 Ocean Crest Road: start/drop-off 9314, destination (-3021.61, 1.02, -1049.14), a singleton route and repeated `ArgumentOutOfRangeException` in `TrafficDriver.Sweep`. The offline graph reproduces it: the pocket cannot reach the close goals; connected lane 15533 is 5 m away and reaches a drop-off within 15 s walking. rc5 tries nearby starts before widening goals, handles singleton/coincident-point routes without sweeping, and ends an exceptional physics step through the vanilla finish. Check a ride from the old drop-off and a zero-distance ride too; no repeated exception and no indefinite boarding hold.

`tools/check-drive.sh` exercises the packaged physics callback in Unity with a controlled missing-body failure: it must end once, hold the car, and report a failed drive for the ride's vanilla completion path. Passed on rc5; this does not establish an in-game arrival.

## Car colour regression (0.2.0-rc3)

Run `tools/check-paint.sh` after building: it checks the compiled mod in Unity with the game's real paint component, including removal of game scripts, the rendered paint properties at boarding and drop-off, inactive child objects, and re-enabling the ride car. It must fail on 0.2.0-rc2.

In game, use a disposable save and exactly one enabled copy of the mod. Call a private driver with a distinctive paint colour outdoors and from indoors. Check pickup, boarding, arrival and Skip: the paint must stay the same. Repeat with another car model and colour. Pending until seen in game on 0.2.0-rc3.

# In-game checks: clean install from the package (release-prep, 0.2.0-rc1)

The first run as a player would meet the mod: from the package, with no test flag. Build it with `tools/package-mod.sh` and copy `build/package/DrivenRides/` (or unzip `build/package/DrivenRides-0.2.0-rc1.zip`) to the game PC.

1. **A game without the mod.** No `DrivenRides` folder in `ModsLocal` and no Workshop subscription to it (a parked copy outside `ModsLocal` is fine). Park Food Trucks and switch off other gameplay mods. Delete or rename `<LocalLow>/Hovgaard Games/Big Ambitions/DrivenRides/` (the test sessions' traces and dumps) so you can see whether a normal game writes there.
2. **No test flag.** Steam's launch options for Big Ambitions have neither `-drivenrides-test` nor `-foodtrucks-test` (the test PC's sessions add the second; start the game from Steam, not `run-remote.sh`).
3. **Install.** Copy the package folder into `ModsLocal` as `DrivenRides`. Start the game. The Mods menu lists it and it loads with no error. `Player.log`: `Version 0.2.0-rc3 loading.`, `Harmony 2.4.2...`, one `Patch ...: Active` line per hook and no `Not active`, the `Options: ...` line. No `Test mode` line, no `Debug keys ready`, no `Traffic warm-up`, no `City ready`, no `Lane graph:` lines.
4. **Options page.** Mods options, HART. Self Driving: eleven settings with the texts from `Locales/en.json`, none showing a raw `drivenrides:` key. Note any text that is cut off or reads oddly.
5. **New save.** A new character. Hail a taxi and pick a place 1 to 2 km away: the taxi drives, the panel card shows, you get out at the door. The log has one `Ride accepted: ...` line, one `Ride ending: Complete (arrived) ...` line and nothing per step (no `NWH drive:`, `NWH planner:`, `Departed`, `Light:` or `Watchdog` lines).
6. **The rest of the rides.** A taxi stand ride, Skip mid-ride (fade, door), 2x and 3x on the panel, the clock slider. With money for a private driver: L calls it, L again walks you to it, the ride drives. With a car: L in the driver's seat, a trip, autopark. Each ride adds a start and an end line, no more.
7. **Notices.** Each in plain English, no raw key: L with no private driver ("You have no private driver."), L in a moving car ("Stop the car first."), a book or other activity during a ride ("Not during a ride."). If a ride falls back to the vanilla ride: "No driving route for this ride. You get there the usual way."
8. **Nothing for testing.** Ctrl+Alt+Shift+G, X, K, V, U, J and L do nothing. After the rides, `<LocalLow>/Hovgaard Games/Big Ambitions/DrivenRides/` does not exist (no `ride-trace.csv`, `rides.jsonl`, `lane-graph-*.json`, `tests/`).
9. **Save during a ride.** Start a taxi ride, open the pause menu mid-ride, save as a second slot, then let the ride finish. Save again normally and quit.
10. **Uninstall.** Delete `ModsLocal\DrivenRides`. Start the game: the Mods menu no longer lists it. Load the normal save: it loads, you can walk, and a taxi ride is the base game's fade. Load the mid-ride save: you stand on foot where you got in, can walk and take a taxi. `Player.log` has no exception naming `DrivenRides`.
11. **Reinstall.** Copy the package back, start the game, load the save: rides drive again, and the options kept their values.

Results and the log go to the README's evidence table (rows "A normal game ..." and "A save made with the mod loads without it").

# In-game checks: ignore speed limits (nwh-rides4, 0.1.106-rides1)

Options, Driven Rides, "Rides ignore speed limits", off by default. Off: as before, the car keeps to 1.2 times the lane limit. On: on straight avenues it speeds up to the car's own top speed (for example the limo's or the taxi's, see `Departed (NWH): ... top N km/h`), and still slows for corners, follows cars ahead and stops at reds. The option applies from the next ride on, not the one under way.

# In-game checks: stop for pedestrians (nwh-rides3, 0.1.104-rides1)

Options, Driven Rides, "Rides stop for pedestrians", off by default. Off: a ride car neither brakes nor stops for people crossing in front of it and drives through them, as the traffic cars do; the log has no `held by ... (Human)` lines. On: it stops for them as before. Switching it during a ride takes effect at once. Backing off ignores people too when it is off.

# In-game checks: the ride panel as a card (ride-panel, 0.1.97-panel1)

Disposable save, Food Trucks parked, other gameplay mods off. The IMGUI box at the top centre is gone; the panel is now a uGUI card under the Objectives panel (design Option B). It works the same as before; only the look changed. Compare it against the design canvas, "Option B: small card".

1. **Startup.** At the first ride the log has one line `Ride panel built on canvas '<name>' (2 units per design pixel, sibling N), font <name> from <where>, bold <name or "by style">`. Please copy it. "from the Objectives panel" or "loaded fonts" is what I expect; "GlobalReferences (sign font)" or "(not Rubik)" means the font search missed and the text may look wrong. No `Ride panel:` error lines.
2. **Place and size.** A taxi ride: the card sits at the left edge under the Objectives panel and is as wide as it, with about 16 px of gap at 1920x1080. With a long objectives list, check whether the list runs under the card (the card's position is fixed at y 324; it doesn't follow the list's height). Change UI zoom in the game's options: the card grows and shrinks with the Objectives panel.
3. **Look.** Light grey header with "2.4 KM TO GO" in dark bold capitals, ticking down by 0.1 km. Dark translucent body, rounded corners, a soft shadow. Clock row: "Clock 33%" and a slider filled blue up to the handle. Buttons 1X 2X 3X, the current one light with dark text, and a blue SKIP on the right. Texts in Rubik, the HUD's font, sharp at 1080p and 4K.
4. **Buttons.** Hovering a button brightens it (Skip turns lighter blue) and plays the game's hover sound; pressing plays the click sound. 1x/2x/3x change the ride speed and the light button follows (`Ride speed Nx.` in the log). The choice is the options page's start speed for the next ride, as before.
5. **Clock slider.** Drag it: the label follows each percent and the digits don't shift sideways. Let go: one `Clock during rides N%.` line. Clicking on the track jumps the handle there. Change the slider on the options page between rides: the next ride's panel shows that value. After a click, WASD and the arrow keys don't move the slider or the buttons.
6. **When it shows.** Only while the car drives or stops at the end. Gone during boarding, the bridge fade, after Skip (the fade), and after arrival. Skip works as before. The phone and the pause menu draw over the card, not under it. Hide UI (screenshot mode) hides it.
7. **Clicks don't go through.** Clicking a panel button doesn't also act on the world behind it.
8. **Own car and private driver.** The same card on an own-car trip and a private driver ride. A second ride after a city reload shows the card again (it is rebuilt per city).

# In-game checks: getting out at the end (nwh-rides2, 0.1.96-rides1)

Any ride that arrives, is skipped or is cancelled (Ctrl+Alt+Shift+V), and the end of a rig session: the log has no `"ResetPath" can only be called on an active agent that has been placed on a NavMesh.` line after `Ride ending: ...` or `Ride cancelled: ...`, and you can walk off at once.

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

1. **Start.** Sit in your car, stopped in a driving lane (at the kerb you pull out yourself first). **L** opens the city map; pick a place 1 to 2 km away. The map closes and the car drives off by itself with you in the driver's seat and the car's own camera. Log: `Own car: driving itself (...)`, `NWH ride planned for ...`, `Departed (NWH)`.
2. **Keys.** During the trip WASD and the handbrake do nothing; the steering wheel doesn't fight the drive (steering assist held off). The ride panel's speed buttons, clock slider and Skip work.
3. **Arrival.** The car stops near the destination and you're still in the seat with control. Fuel went down as for a normal drive. Log: `Own car: the keys are the player's again (arrived)`.
4. **Autopark.** Options, Driven Rides, "At the end of a self-driven trip": with *Autopark cars that have autopark*, a car with autopark parks itself (fade, then parked, still in the seat), one without stays on the street. With *Autopark every car*, both park. With *Park the car yourself*, neither. Autopark takes the nearest free spot within 150 m, also across the street or round the corner; only with none that close it says to park yourself. Log: `Own car: autoparking N m away`, `autoparked`, or `no free autopark spot within 150 m (N spots seen)`.
5. **Skip** mid-trip: fade, the car is on the lane nearest the destination with you in it, the clock moved on, then autopark as above.
6. **Getting out** mid-trip (the exit key): the trip ends, the car stops where it is, you're on foot beside it. Log: `Own car: the player got out during the trip`.
7. **Save** mid-trip and load: you sit in your car where it was when you saved.
8. **Hospital** mid-trip (**Ctrl+Alt+Shift+V**): the car stops where it is; the hospital takes over.
9. **Refusals:** L while driving fast says "Stop the car first"; L in a parking garage says "Drive out onto the street first"; L in a car parked at the kerb, or facing against the lane, says it can't drive itself there ("pull out into the lane first"); an empty tank says so; with the own car option off, L in the car does nothing.
10. **Mid-trip:** the game's autopark button and the bridge's skip button say "Not while the car drives itself". A car that runs out of fuel or is wrecked mid-trip stops, with a notice, and you're in it with control.
11. **Bridge:** a trip to the Hamptons skips the bridge as the driver's rides do, and the tank goes down by what the game's own bridge skip charges; with too little fuel for it, the trip ends before the bridge with a notice.
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
