# Turn tests: an automated in-game test harness

The mod drives hard manoeuvres by itself, with the car models that ride, and reports what went wrong with enough detail to fix it without a human test drive. Started and read over ssh while the game runs a disposable test save. Reviewed in six rounds by Fable 5.1 and GPT 6 Astra until both said "build it" (`build/review-tests/`, not committed).

Everything here is debug tooling: it does nothing unless a command file appears.

## Session

A test session is owned by `TurnTests` (game side). It holds one anchor, the player's attachment to it, the player's start pose, and every override; each test case runs a `Ride` in test mode that borrows the anchor.

- **Start requires** a loaded city, the player outdoors, no activity panel, no time machine, no ride, not traveling, not in a vehicle, and energy at least 20 above the hospital threshold.
- **The player** is attached to the anchor for the whole session (`AttachToObject`, as a ride's `Board`): hidden, capsule and agent off. The anchor follows the test car, so Gley's active camera (the player transform) travels with it: traffic spawns, lights cycle and nothing is removed around the car. The pedestrian camera shows the test. Between cases the anchor moves to the next start.
- **The clock** is held: the `CapTick` prefix sets the tick's minutes to 0 while no time machine runs (the mouse and placement code in the tick still run). A time machine that starts anyway (the `StartTimeMachine` prefix) stops the session first.
- **Blocked during a session:** activities (`ActivityPrefix`), the L shortcut, and taxi or driver rides (a `TaxiSystem.TravelTo` prefix, before the fare, with a notice).
- **Traffic lights** are obeyed in tests: an in-memory override that `Options.TrafficLights` prefers, never written to PlayerPrefs, cleared at session end.
- **Teardown** (stop command, exception, unload, hospital, foreign time machine) runs synchronously: remove the test car if still owned, release overrides, detach at the start pose with a NavMesh sample, show the player, `Character.Reset()`, restore the camera, the ride's pause-aware `RestoreScale`. On an ordinary stop a follow-up on the next frame checks the agent is on the NavMesh and warps again if not, only if the session id and the save are unchanged. On hospital or unload no follow-up.
- Test rides never touch the production blocked-step set (`GleyGraph.BlockStep`); the plan-time preflight uses a session-local set.

## Traffic and pedestrians

Two passes.

- **Pass 1, geometry.** The game's own switch, as its `ToggleTraffic` console command: `GameManager.spawnTraffic = false`, `SetTrafficDensity(0)`, `ClearTraffic()` before the first test car exists. Pedestrians: read the spawner's requested population (`_lastSetAmount`) and switch, then `spawningActive = false` and `SetActivePedestrians(0)`. Each case runs `ClearTrafficOnArea(start, 40)`; before departure it counts other active Gley cars within 150 m (path-owned ones that `ClearTraffic` skips) and pedestrians within 50 m; a car found marks the run `contaminated`.
- **Pass 2, traffic.** Restore traffic (`spawnTraffic = true`; the time-of-day controller re-applies the density) and pedestrians: wait until `activePedestrians` is empty (20 s bound), then `Release()` any left (iterating `activePedestrians.ToArray()`), `SetActivePedestrians(captured)`, restore the switch. Wait 10 s for traffic to fill. Record the active car and pedestrian counts within 150 m at each departure.
- Every exit restores both. On hospital or unload the pedestrian restore runs at once without the wait.

## Catalogue

Built in game from the live `LaneGraph` (Core `Catalogue`), written to `catalogue.jsonl` with stable ids.

| Kind | Cases |
|---|---|
| `turn` | each junction entry `i`, each target in `MissingTurns(i)` |
| `uturn` | `UTurn(i)` |
| `lanechange` | every diagonal step the planner can take (Gley's lateral neighbours and the inferred ones, to the first or second waypoint ahead, as `RoutePlanner` relaxes them), kept if the side gap is over 4.5 m or the step lies within 40 m of a junction, a height change over 1 m, or a fence-like street prop |
| `sharp` | Gley's own successor chains that turn over 80 degrees within 30 m |
| `ramp` | directed stretches with a height change over 3 m within 60 m, one case per stretch from its foot |
| `crest` | stretches where the grade changes by more than 6 percent within 30 m |
| `ride` | a recorded route from `rides.jsonl` by line number, split at bridges into sections |
| `control` | negative-control pairs (below) |

**Fixtures.** A case is a fixed route, built directly, not planned: a run-up of up to 120 m back along straight predecessors and through the previous junction's straight-on waypoints, the manoeuvre, and a run-out of 60 m along the exit lane. A fixture is either a `route` (expanded by the ride's `Expand`, now Core `PathExpander`) or a `path` (pre-expanded, with its slow-path set, driven as given). U-turns and added left turns run in two variants: `rolling`, and `fromstop` (held at the entry waypoint, released 2 s after the car is stationary under 0.3 m/s).

**Exclusions.** A case whose route passes within 20 m of any `BridgeTriggerController` collider's bounds is excluded (the ride's bridge skip needs a time machine). `ride` cases are split there and the gap is reported as not covered.

**Ranking.** Sharpness, and the count of `StreetProps` colliders within 15 m of the manoeuvre; hardest first.

## Cars

The models that ride: every private-driver vehicle type the game ships (`PrivateDriverHelpers.GetAiVehiclePrefab`) and the taxi prefab. The `cars` command logs each model's body collider bounds, `length`, `colliderWidth`, `wheelDistance`, `maxSteer`, power and the vehicle type's `maxSpeed`. A fixture carries the vehicle type, so its `maxSpeed` reaches the test ride as `driverTopSpeed` does in a real ride, and the ride speed from the command.

Default set: the largest turning radius (`wheelDistance / tan(maxSteer)`), the longest, the widest and the taxi, deduplicated. The smallest runs only on cases that failed or came close.

**Loading.** `ClearTrafficOnArea(start, 40)` and `LoadVehicle(prefab, start, true)` in the same frame; verify the returned car's prefab, face it down the route, hold it; an overlap check of its body against street props and buildings must be clear, else `setup-error` (retried twice).

## Preparation

After moving the anchor, wait at least 3 s. Hamptons houses: a Harmony postfix wraps `HamptonsHouse.LoadHamptonsItems`'s enumerator; the wrapper clears the house's completion record on its first `MoveNext` and sets it after the last (items and `SetUpItemControllersParents` done). Preparation waits (10 s bound, then `setup-error`) for houses whose `IsHouseLoaded` is true at the anchor. During the run, a physics step with a house within 30 m of the car that is not loaded, or loaded without a completion record, is marked "house loading"; clearance and kerb results of those steps count as incomplete, not clear.

## Gates and verdicts

Gates come from the route's own lane waypoints, not the expanded path:

- the entry waypoint;
- the manoeuvre's first plain exit waypoint (for a U-turn the first non-junction waypoint after `UTurn(i)`);
- exit-lane waypoints about 10 m and 30 m past it (the last gate sits before the run-out's stopping tail);
- for U-turns, a turn gate at the end of the straight chain into the junction (`InferUTurns`, at least 14 m in).

A gate is a directed segment through the waypoint, perpendicular to the lane, 4 m each side, within 2 m of height; it is passed when the front axle moves across it between two consecutive physics poses, in the lane's direction. A from-stop run that starts on its entry gate counts it as passed. At the last gate the car's centre must be within 1 m of the exit lane's centreline and its heading within 10 degrees. The verdict never depends on the ride's own arrival.

Per run, two separate records:

- **Geometry outcome:** `pass`, `pass-slow` (a rolling run whose approach speed was under 70 percent of the expected speed), `fail` with reasons, `incomplete` (time limit).
- **Traffic interference:** `contaminated` (pass 1: another car found before departure; any pass: Gley's state `StopInDistance` or `Follow` during the manoeuvre), and every incident with a vehicle or pedestrian.

An observed geometry failure always wins. A contaminated run is repeated up to twice; still contaminated, it is `unresolved`. Unexecuted coverage: `avoided` (the plan-time preflight rejected the route; controls skip the preflight), `replanned` (the live `StepBlockedAhead` or the watchdog asked for a replan; the test ride never re-routes), `setup-error`.

## Measurements

Per physics step (`FixedUpdate` on a component on the car), with a step counter; collision callbacks carry the step counter; crossings and overlaps are evaluated between consecutive stored poses.

- **Contacts:** every `OnCollisionEnter`/`Stay` per collider (no quiet time), with point, normal, impulse and layer. Street props, buildings and fences fail the run. Vehicles, pedestrians and the player are incidents.
- **Clearance:** the car's envelope from its body colliders' local geometry (`BoxCollider` center and size; a `MeshCollider`'s `sharedMesh.bounds` in local space) with the full rotation and scale. Boolean overlaps at 0.3, 0.2 and 0.1 m inflation (`OverlapBoxNonAlloc`, buffer 64) against street props and buildings; the smallest that hits is recorded. A full buffer invalidates the step and the run is repeated.
- **Kerb:** a raycast down from each wheel's hub; the contact height against the road surface interpolated along the route at that wheel's along-position, with the crossfall from the two nearest lanes. The first run is a calibration (flat road, ramp, driveway): it logs the layer and height difference per contact, and the kerb check is reported, not used for the verdict, until thresholds are set from it. No ground within 1 m is recorded separately.
- **Opposing lanes, outside junctions:** the boundary is the midline between the nearest opposing-direction lane and the nearest same-direction lane on that side (plain waypoints within 12 m, level within 1 m); depth is how far the footprint (corners and edge midpoints) is past it. Fails above 0.3 m.
- **Junctions, by phase and region:** forbidden regions are built before the run: the exit road's opposing lanes as polygons widened to the midline with their neighbours, 25 m back from the first plain exit waypoint, plus their straight continuation into the junction. Permitted: the entry road's opposing lanes for left turns. U-turns: the approach carriageway is permitted up to the turn gate; the far carriageway is permitted only after the front axle crosses the turn gate; the median and junction area between the carriageways is forbidden outside the U-turn's own swept corridor (the car's half width plus 0.3 m around its expanded path) from the turn gate on. From the entry gate to the last gate every step measures the footprint's depth into forbidden regions whatever the heading (heading recorded); depth over 0.3 m fails. Footprint parts outside every built region count as unmeasured; the share is reported.
- **Exit lane:** from the first plain exit waypoint on, the centre's lateral distance from the exit lane's polyline, and the time spent over 1.5 m.
- **Path deviation:** on plain stretches, the centre's distance from the route's lane polyline; fails over 1.5 m. Inside junctions, informational.
- **Red lights:** the front bumper crossing the plane through a `stop` waypoint while its light is red.
- **Speed:** an approach sensor two path positions before the manoeuvre's first slow-path position (or 30 m before the entry for manoeuvres without one); speed at the sensor, at the entry gate, and the minimum between them. Expected speed: min(the car's top speed, the lane's `maxSpeed`). A car that drops under 0.5 m/s between sensor and entry gate turns a rolling run into from-stop coverage.
- **Stuck:** waiting with Gley's state `StopInPoint`, `StopInDistance` or `Follow` is waiting; a watchdog `Restart` during the manoeuvre is a finding.
- **Time:** wall-clock and simulated time; pitch, roll and height in the trace.

## Negative controls

Each control is a pair. The correct fixture must pass; a mutation of only its driven path must fail for its stated reason. Gates, the expected exit lane and the forbidden regions are computed once from the correct fixture and frozen for both. A control counts as calibrated only if both happen; otherwise the run is `uncalibrated` and its passes are not counted.

| Control | Mutation | Expected failure |
|---|---|---|
| U-turn turning in early | `path` with the U-turn's vias removed | far carriageway entered before the turn gate |
| Left turn through the exit road's oncoming lanes | `path` with the via moved into the exit road's nearest opposing lane | forbidden intrusion over 0.3 m |
| Wrong exit lane | `path` through the correct via, ending in another same-direction exit lane | the last gate's lane check |
| Corner cut | `path` of an added right turn with its via removed | a contact, a near miss at 0.1 m, the kerb report, or a missed gate |

## Failure capture

The first failure latches the car's pose, contacts and the last 5 s of trace in `FixedUpdate`. The Rigidbody is never touched. In that frame's `LateUpdate` a camera frames the incident (orthographic from above, 40 m; plus an oblique view for ramps and failures more than 2 m off the ground) and renders once to a RenderTexture: the planned path (green), the car's track (red), the latched footprint (a wire box, yellow), the forbidden regions (blue) and contact points. The line layer is outside the main camera's culling mask. PNG to the attempt's folder. The car is removed on the next frame.

## Commands and files

All in `<persistentDataPath>/DrivenRides/tests/`.

- **Commands:** the Mac script writes `commands-<id>.tmp` and renames it to `commands-<id>.txt`. The mod claims a file by renaming it to `.claimed`, then reads it, and writes `ack-<id>.json`; a duplicate id is acknowledged as ignored; a `.claimed` file older than the mod's start is renamed to `.abandoned`. A `stop` flag file is checked every frame.
- **Status:** `status.json`, written to a temp file and renamed, with a heartbeat, the session id, the build version, the graph hash, the current case, counts and a time estimate.
- **Results:** `results-<session>.jsonl`, one line per attempt: a unique attempt id (also used by every artifact name), the case id, the fixture, car, variant and pass, options, version and graph hash, both outcome records, measurements and the trace file name.
- **Commands:** `cars`; `catalogue`; `run [kinds=turn,uturn,lanechange,sharp,ramp,crest,ride,control] [cars=default|all|<names>] [pass=geometry|traffic] [speed=3] [mode=screening|exhaustive] [limit=N] [ids=...] [rides=<lines>]`; `rerun <results file> verdict=fail,contaminated,unresolved`; `stop`.

**Screening.** The default run is labelled a screening run: pass 1 over the whole catalogue for the default set; the smallest car only on fails and near misses; pass 2 on fails, near misses, the regression set and a random 10 percent of the clean cases. The summary lists, per car and pass, how many cases ran and how many were left out. `mode=exhaustive` runs every chosen car over the whole catalogue in both passes.

**Order of building.** First the controls, the regression spots from Peter's drives and the 20 sharpest cases with one car, checked against what Peter saw; then the full catalogue.

## Code layout

- `Core/Ride/PathExpander.cs`: the ride's `Expand`, moved out of `Ride.cs` unchanged in behaviour, used by rides and tests.
- `Core/Testing/`: `Fixture` (and its JSON form), `Catalogue`, `Gate`, `Regions`, `RunJudge` (samples in, outcome records and measurements out), `Controls`. Pure C#, unit-tested, and runnable offline against a dumped lane graph.
- `unity/DrivenRides/Scripts/Testing/`: `TurnTests` (session, commands, status, results), `TestCar` (the physics-step sampler, contacts, overlaps, kerb rays), `TestPicture`, and the test mode in `Ride.cs`.
- `tools/turn-tests.sh` (send a command, poll the status, fetch results and pictures) and `tools/summarise-tests.py`.
