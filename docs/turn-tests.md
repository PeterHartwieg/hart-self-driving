# Turn tests: an automated in-game test rig

The mod drives the manoeuvres it adds (and the ramps) by itself, with the car models that ride, and reports failures with a picture, so they can be fixed without a test drive. Started and read over ssh while the game runs a disposable test save. Debug tooling: it runs only in a game started with `-drivenrides-test` or `-foodtrucks-test` (the test PC's sessions pass the second), and then does nothing unless a command file appears. The same flag turns on the debug keys, the ride trace, the lane graph and ride dumps, the slowdown dumps, the traffic cost lines and the detailed log; without it the log has the startup lines, one line as each ride starts and one as it ends, and errors.

The design went through six review rounds (Fable 5.1, GPT 6 Astra; `build/review-tests/`, not committed) and was then cut back to this lean version on purpose: it is a test rig. Left out for now: a kerb check, crests, a traffic-on pass, from-stop variants, Hamptons house-loading checks.

## Isolated agent player

For unattended parking tests use the separate PC player documented in `tools/agent-tests/README.md`. Its bridge
is port 18330, its lock is `agent1`, and its test directory is under `Big Ambitions Agent Tests/DrivenRides/tests`.
The normal `tools/turn-tests.sh` still targets the ordinary game's profile; do not use it for this instance.
Acquire the agent lease, deploy only into its stopped profile, and submit command files under that lease while
paused before resuming via its bridge. Keep the normal game untouched. Stop the agent and wait for its process to
exit before releasing the lease between fixes. Headless results establish simulation behaviour, not appearance.
A failed local search records replay geometry in `manoeuvre-worlds.jsonl` when test mode is enabled.

## Session

`TurnTests` (game side) owns the session.

- **Start requires** a loaded city, the player outdoors, no activity panel, no time machine, no ride, not traveling.
- **The player** is attached to a session anchor for the whole session (as a ride's `Board`): hidden, capsule and agent off. The anchor follows the test car, so Gley's active camera (the player transform) travels with it: lights cycle and the area stays live. The pedestrian camera shows the test.
- **The clock** is held: the `CapTick` prefix sets the tick's minutes to 0 while no time machine runs. A foreign time machine stops the session.
- **Traffic and pedestrians off**, the game's own way (its `ToggleTraffic` console command): `GameManager.spawnTraffic = false`, `SetTrafficDensity(0)`, `ClearTraffic()`; pedestrians: remember `_lastSetAmount`, `spawningActive = false`, release the active ones. Restored at the end (`spawnTraffic = true`, the time-of-day controller re-applies the density; `SetActivePedestrians(remembered)`, `spawningActive = true`).
- **Traffic lights** obeyed: an in-memory override that `Options.TrafficLights` prefers.
- **Blocked during a session:** activities, the L shortcut, taxi and driver rides (a `TaxiSystem.TravelTo` prefix).
- **Teardown** (stop, exception, unload, hospital, foreign time machine): remove the test car, restore traffic, pedestrians, lights and time scale, detach the player at the start pose with a NavMesh sample, show them.
- Test rides never add to the production blocked-step set.

## Catalogue

Built in game from the live `LaneGraph` (Core `Catalogue`), stable ids:

- `turn`: each junction entry `i`, each `x` in `MissingTurns(i)`;
- `uturn`: `UTurn(i)`;
- `lanechange`: the planner's diagonal steps (`RoutePlanner`), kept if the side gap is over 4.5 m or the step is within 40 m of a junction;
- `ramp`: directed stretches with a height change over 3 m within 60 m;
- `ride`: a recorded route from `rides.jsonl` by line, split where it nears a bridge.

Each case is a fixed route: up to 120 m of run-up through straight predecessors, the manoeuvre, 60 m of run-out along the exit lane. Cases within 20 m of a bridge trigger are left out (the bridge skip needs a time machine). Ranked by the heading change of the manoeuvre, sharpest first.

**Cars:** from the game's vehicle types with a traffic prefab, plus the taxi prefab: the largest turning radius (`wheelDistance / tan(maxSteer)`), the longest, and the taxi. A test ride uses the vehicle type's `maxSpeed` as a real private-driver ride does.

**One case:** move the anchor to the start, wait 3 s, `ClearTrafficOnArea(start, 40)` and `LoadVehicle` there, face it down the route, then drive it with the real ride code in test mode at the chosen speed (default 3x). The case ends when the last gate is passed, on the first failure, when the ride stops, or after a time limit.

## Pass or fail

Gates are directed lines across the lane (4 m each side) at the entry waypoint, the first plain waypoint after the manoeuvre, and 30 m further down the exit lane; the car's front axle must cross them in order. At the last gate the car's centre must be within 1 m of the lane's centre and its heading within 10 degrees.

A run fails on:
- `contact`: a hit on a building or street prop, or on an upright face of a road collider (a divider or kerb wall built into the road);
- `near-miss`: the car's body box, inflated by 0.1 m, overlapping a building or street prop;
- `missed-gate`, `exit-lane`, `exit-heading`;
- `intrusion`: on plain stretches, the car's footprint more than 0.3 m past the midline between the route's lane side and the nearest oncoming lane; past the mouth of the road being turned into (its first 25 m), more than 0.3 m into its oncoming lanes. Inside the junction a left turn sweeps across them, and a U-turn's approach lanes are that road's oncoming lanes, so neither is checked there;
- `far-side-early`: a U-turn's footprint on the far carriageway before the front axle crosses the turn gate (the end of the straight way into the junction, `LaneGraph.UTurnStart`);
- `stuck`: 20 s without progress while not waiting at a light;
- `replanned`: the ride code wanted to re-route (a blocked step ahead, the watchdog).

Recorded too: entry speed, the closest near-miss level (0.3, 0.2, 0.1 m), the maximum deviation from the lane on plain stretches.

**Negative controls**, run first: a U-turn with its curve waypoints removed (must fail `far-side-early`) and a left turn steered through the oncoming lanes of the road it turns into (must fail `intrusion`), each next to its correct version (must pass). If a control doesn't behave, the run is marked `uncalibrated`.

**Failure picture:** in the frame of the failure, a camera 40 m above renders the planned path (green), the car's track (red) and its outline at the failure (yellow) to a PNG.

## Commands and files

In `<persistentDataPath>/DrivenRides/tests/`. The Mac script `tools/turn-tests.sh` writes `command.tmp` and renames it to `command.txt`; the mod reads and deletes it. A `stop.txt` file stops a run. The mod writes `status.json` (heartbeat, current case, counts), `results-<session>.jsonl` (one line per run) and pictures `<run id>.png`.

Commands: `cars`; `catalogue`; `run tier=quick|full`; `run [kinds=turn,uturn,lanechange,ramp,ride,control] [cars=default|all|<names>] [speed=3] [limit=N] [ids=...] [rides=<lines>]`; `rerun <results file>` (the failed ones); `stop`. `run driver=nwh [traffic=on]` drives each case with the car's own NWH physics; a car without a drivable prefab (the taxi) is the drivable car closest in size dressed as it (`NwhDress`, the result line names the `donor`). `dress [car=taxi|<traffic prefab>]` parks a dressed car beside the player to look at (also Ctrl+Alt+Shift+L); `dress remove` takes it away. With `driver=nwh`, each case first goes through the NWH planner (`NwhPlanner`: Core's feasibility check with the world, the case's lanes swept for parked cars); a case it leaves out is `avoided`, with the reason in `why`, and the results line has `plan` (arc, tight arc, vias) and `check`. `check=off` drives every case as the spike did.

Real rides from the Mac: `ride [kind=taxi|stand|driver] [vehicle=<part of the type name>] [min=500] [max=1500] [x=.. z=..] [seed=N] [speed=1|2|3] [hamptons=on]` hails the nearest taxi (within 200 m), uses the nearest stand (400 m) or calls the private driver and waits for it, picks a destination building (random between `min` and `max` metres, or the one nearest `x`, `z`) and calls `TaxiSystem.TravelTo` as the map's Go button does, fare included (`RideCommand`). It needs no session; it refuses while one runs. One line per ride in `ride-results.jsonl`: `driven`, `carPhysics`, `end`, `seconds`, the NWH tally (`contacts`, `contactsCaused`, `redRuns`, `stalls`) and `arrivedMetres`. `ridemode nwh|gley|options` drives the rides after it with car physics, as Gley rides, or as the options page says, until the game quits.

For a repeatable exact destination, add `address=ba:street_firstavenue#31` (street locale key, `#`, number); it takes
precedence over `x`/`z`, which match building controller positions rather than door positions. `kind=own` uses the
player's car with `autopark=off|cars|every`. `kerb=M` places it M metres right of a lane to test departure;
`kerb=0.01` re-seats it in the lane, while omitting `kerb` preserves a parked car's pose. `skip=N`, `exit=N`
(own car) and `cancel=N` interrupt N seconds into the ride. Results include `radioBefore`, `radioDuring` (once
road driving begins) and `radioAfter`: mute/playback state, station/clip, controls, headset and radio ownership.
The background rig mutes its audio listener, so these observations do not establish speaker-level audibility.
Own-car results also record `keysBack`, `inCar`, `driveLeft`, `manoeuvreLeft`
`parkingReservationsLeft` (must be zero) and `parkingState`. The contact/red/stall tally covers the road drive; manoeuvre outcomes are in the game log.

## Seeded legal-parking departures

`ride kind=own parkseed=N parking=bays|parallel|all departonly=on autopark=off` places the owned test car in an
actual free legal parking spot, enters it, and invokes the normal self-driving ride. The fixture saves/restores
Unity's random state, checks occupancy (cars, buildings and props), and requires all wheels to be legally parked.
It never removes neighbouring cars. `parknear=x,z` restricts selection to the four nearest parking areas;
`parkreverse=on` reverses the parked orientation before checking legality. Without `parkseed`, the parked pose is
preserved. Use a disposable FT checkpoint containing the selected car.

`departonly=on` stops after the local exit and 20 m of road driving. Results include `parkingSeed`, `parkingSite`,
`parkingStart` (x,y,z,yaw), `parkingLegal`, `departurePassed`, `departureRoadStart`, `roadMetres`, `roadJumps` and
final control/reservation state. Large position jumps (including vanilla bridge skips) do not count toward the
20 m of physical road driving; measurement resumes after the jump.
Population can change after reload, so the exact selected pose is recorded in addition to the seed.
A refusal or safe cancellation is recorded as a failed departure, never a successful fixture.

The Windows runner verifies every installed package hash, reloads the checkpoint per seed, and leaves the world
paused. Acquire `agent1` ownership and start its background player first, following `tools/agent-tests/README.md`:

```sh
python3 tools/agent-tests/parking-cases.py --checkpoint 'FT Parking checkpoint' \
  --seeds 1,2,3,4,5 --parking bays --near=-270,-341 \
  --output build/parking-cases.jsonl
```

Repeat with `--reverse`, `--parking parallel`, and without `--near` for wider coverage. A pass requires a legal
start, 20 m of road driving, no contacts/red runs, returned controls and no remaining controllers or reservation.
Keep blocked cases in the results. The runner does not install a mod, create a production save, acquire another
owner's lock or touch the normal game. Stop/release the isolated rig after the sweep. `parking-sites` surveys legal
parking areas; `parking-ground` exports nearby render geometry for diagnosis into the ignored rig-data folder.

## Tiers and the avoid list

- **Quick** (`run tier=quick`, about 10 minutes, after any update): the controls, every manoeuvre on the avoid list, and a fixed sample of 30 cases per kind, with the car with the largest turning circle.
- **Full** (`run tier=full`, about 40 minutes, after changes to the turning code or a game update): the controls and every case with that car, then the other default cars on the cases it failed or passed within 0.2 m.

`speed=auto` uncaps the frame rate for the session and sets the speed every frame so a frame covers about 0.05 s of game time, as a 3x ride at 60 fps does (Gley's waypoint switching and the ride's turn speed run once per frame), up to 8x. A fixed `speed=` goes up to 8 too.

Cases are short (50 m run-up, 40 m run-out: the car reaches its top speed in about 40 m). Lane changes are all the ones across a wide gap (slip lanes, ramps) and a fixed one-in-eight sample of the rest. Traffic is off, so a case waits only 1 s after moving the anchor.

**The avoid list.** `tools/tested-steps.py` turns results into `unity/DrivenRides/Scripts/TestedSteps.cs`: each manoeuvre that failed after its entry gate, with the shortest car that failed it. The ride planner leaves those manoeuvres out for cars at least that long. Re-judge older results first (`tools/rejudge --write`); pass only the kinds a later build hasn't changed (`file:UTurn,LaneChange`). Waypoint indices belong to one game build: regenerate after a game update. Results mark failures already on the list as `known`.

## Code layout

- `Core/Ride/PathExpander.cs`: the ride's `Expand`, moved to Core unchanged.
- `Core/Testing/`: `Fixture`, `Catalogue`, `Gate`, `Judge`, `Controls`, `Json`. Pure C#, unit-tested, runnable against a dumped lane graph.
- `unity/DrivenRides/Scripts/`: `TurnTests` (session, commands, files), `TestCar` (sampling, contacts, overlaps, picture), and the test mode in the `Ride` partial class. There is no `Scripts/Testing` subfolder; see [code-layout.md](code-layout.md).
- `tools/turn-tests.sh`, `tools/summarise-tests.py`.
- `tools/rejudge` also checks the ride's turn model away from the game: `drive <entry> <exit> <aim> [samples.csv]` (a turn driven through its vias, against a recorded track), `arcs [lookahead]` (which catalogue turns get a driver's arc for the 6.7 m car, and how closely the car follows it), `arcline <entry> <exit>` (one arc's points).
- `tools/rejudge <graph> feasible <car> [--ids ...] [--rig results.jsonl] [--out file] [--previous file]` is T1 for NWH cars: Core's `Feasibility` on every catalogue turn and U-turn, one line per case, and the cases whose verdict or form changed against a previous `--out` (exit code 1 if any). No world offline: a kerb estimate (half a lane past the outermost lane on the right) stands in for it, and misses the props at junction corners. `<car>` is `limo`, `mersaididash` (`tools/rejudge/Cars.cs`) or the text of the game log's "NWH shape" line. `--pedestrian` takes every point as the pedestrians' ground, the worst the game's NavMesh can say, so the pavement rule rests on what counts as carriageway alone: within half a lane of a lane, or inside a junction's box (`JunctionBoxes`, the hull of its lanes and their first waypoints out; T3 on 0.1.70 drove the limo's wide U-turns through a median's opening up to 5.4 m from any lane there).
- `tools/calibrate <graph> <run>-nwh.csv...` fits Core's `CarModel` to the rig's NWH traces: the wheel's smoothing and rate, the front track (NWH's Ackermann), the open-loop error over 1 and 2 s, and the closed-loop error on nwh1 rig cases (the feasibility check's margin).

## Turn lines

An added turn is driven along a driver's arc where one fits (Core `TurnArc`): on along the entry lane, one circular arc, out in line with the exit lane. The arc should start no earlier than 3.5 m before the first lane crossing the entry lane (half a lane to the kerb line, and 1.5 m more for a wider curve at a large junction) and end by the far side of the junction; where that is tighter than the car can follow (its turning circle plus 15%, and never under 6.5 m, so a nimble car doesn't pivot round the corner), it starts a little earlier. The ride steers it by moving Gley's target for the car to 1.1 m past its front axle along the arc each physics step, from 12 m before the entry waypoint (Gley lets go of the entry 5 m before it). The world check drives the same pursuit in Core's `TurnDrive` (Gley's steering: front-axle target, 5 m centre-of-mass reach, quickened wheel) and tests the car's body box, 0.2 m larger, against street props, buildings and, from 0.35 m up, the road's own colliders. A turn with no clear arc falls back to its vias and an aim down the exit lane.

A U-turn is checked the same way: `TurnDrive` drives it through its vias at the ride's U-turn speed and 20 m on down the lane (a long car came out at an angle and swung onto the pavement past a shorter check), and the body box must stay clear. A car that can't make one cleanly goes round the block (Peter: few drivers would U-turn a car that size). The model tracks recorded U-turns to about 0.05 m and errs wide, so it blocks the odd U-turn a long car could just make.


## Quick destination key probes

With test mode enabled, `destinations op=status` records the current home/pin, binding indices, travel state,
nearby residences and taxi stands in `destination-results.jsonl`. The command refuses non-FT saves.
`destinations op=key key=J` queues actual Unity Input System keyboard events for the regular shortcut Update;
`modifier=LeftCtrl` checks modifier suppression. It does not directly invoke the shortcut or travel handler.

Fixture operations: `pin address=<street_key>#<number>`, `clear-pin`, `enter address=...`, `exit`, `map`,
and `bind which=pinned|home index=0..6` (Disabled/G/J/K/U/V/X). Enter runs the real building-entry flow;
bind uses the registered option callback and its normal PlayerPrefs key. Use only an owned disposable test
world and restore preferences after the run. `driver-fixture vehicle=<type>` can register a test-only private
vehicle when the fixture has none; the normal phone still summons it.

`ride ... shortcut=J pin=on` prepares the ordinary ride fixture, pins its selected target and injects J instead
of calling TravelTo. For own-car rides it leaves the map closed so the shortcut opens it. For taxi, stand and
driver rides it opens the normal destination map first. `shortcut=K address=...` tests the previously visited
home; it does not seed home memory. Results include the actual ride target and whether it matches the expected
fixture target. Retain refusals and vanilla fallbacks separately from successful driven trips.
