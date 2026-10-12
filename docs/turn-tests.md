# Turn tests: an automated in-game test rig

The mod drives the manoeuvres it adds (and the ramps) by itself, with the car models that ride, and reports failures with a picture, so they can be fixed without a test drive. Started and read over ssh while the game runs a disposable test save. Debug tooling: it does nothing unless a command file appears.

The design went through six review rounds (Fable 5.1, GPT 6 Astra; `build/review-tests/`, not committed) and was then cut back to this lean version on purpose: it is a test rig. Left out for now: a kerb check, crests, a traffic-on pass, from-stop variants, Hamptons house-loading checks.

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
- `contact`: a hit on a building or street prop;
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

Commands: `cars`; `catalogue`; `run [kinds=turn,uturn,lanechange,ramp,ride,control] [cars=default|all|<names>] [speed=3] [limit=N] [ids=...] [rides=<lines>]`; `rerun <results file>` (the failed ones); `stop`.

## Code layout

- `Core/Ride/PathExpander.cs`: the ride's `Expand`, moved to Core unchanged.
- `Core/Testing/`: `Fixture`, `Catalogue`, `Gate`, `Judge`, `Controls`, `Json`. Pure C#, unit-tested, runnable against a dumped lane graph.
- `unity/DrivenRides/Scripts/Testing/`: `TurnTests` (session, commands, files), `TestCar` (sampling, contacts, overlaps, picture), and the test mode in `Ride.cs`.
- `tools/turn-tests.sh`, `tools/summarise-tests.py`.
