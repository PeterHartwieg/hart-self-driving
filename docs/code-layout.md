# Current code layout

The Workshop name is **HART. Self Driving**. `DrivenRides` remains the mod ID, assembly name and
namespace. Runtime and manifest version remain 0.4.1 during the behavior-preserving cleanup.
The dated implementation plans explain how the design evolved; README and CHECKS describe current
behavior and the limits of the available evidence.

## Core

`Core/` targets netstandard2.1 and has no Unity or game references.

| Area | Responsibility |
| --- | --- |
| `Routing/` | Lane graph, route search, destination and parking-strip goals |
| `Ride/` | Route expansion/progress, turn geometry, watchdog and restoration rules |
| `Drive/` | Feasibility, steering, speed, traffic decisions and replanning |
| `Parking/` | Local manoeuvre search/control, departure entries and traffic reservation geometry |
| `Testing/` | Offline catalogue, fixtures and judgments shared with the game rig |

`TrafficDriver` remains one class with one decision state. Its partial files separate the operations:

| File | Responsibility |
| --- | --- |
| `TrafficDriver.cs` | State, tuning constants, constructor, `Decide` orchestration and immediate body/gap checks |
| `TrafficDriver.Sweep.cs` | Predicted poses, bounded model stepping and timing |
| `TrafficDriver.Conflicts.cs` | Corridors, clearance, following, commitments and keep-clear decisions |
| `TrafficDriver.Approach.cs` | Cautious approach, held/released stops and verdict diagnostics |
| `TrafficDriver.Recovery.cs` | Backing off, stand-offs and blocked-car detection |

All field/property initializers remain in the original file and original order. Splitting methods does
not introduce another controller, another decision pass or a new lifetime. Traffic margins and physics
thresholds are unchanged.

## Unity adapter

`unity/DrivenRides/Scripts/` is compiled by the official SDK against the game DLLs.

| File or class | Responsibility |
| --- | --- |
| `DrivenRidesMod`, `DrivenRidesCity` | Mod and city lifecycle |
| `Log`, `Notice` | Logging sinks/test-mode filtering and player-facing notifications |
| `Patches` | Harmony hook registration, status logging and thin interception handlers |
| `Ride.cs` | Captured owners/state, lifecycle, NWH handoff, completion/cancellation and presentation |
| `Ride.Planning.cs` | Ride acceptance/refusal, route planning, Gley geometry checks and vehicle preparation |
| `Ride.GleyDriving.cs` | Gley movement, arc steering, route expansion/indexing and Gley replanning |
| `Ride.Bridge.cs` | Bridge detection, fade/time-machine crossing and NWH landing |
| `NwhDrive`, `NwhPlanner`, `NwhCar`, `NwhRides` | Physics-controller adapter, world planning, car setup and ride-car seam |
| `OwnCar`, `OwnCarParking`, `NwhManoeuvre`, `ParkingTraffic` | Own-car input ownership and local parking/departure integration |
| `DestinationShortcut`, `DestinationTargets`, `DriverShortcut` | Input, transient destination memory and private-driver summon |
| `RideRadio`, `RidePanel` | Passenger radio ownership and ride controls |
| `TurnTests`, `TestCar`, `RideCommand`, `ParkingTests`, `DestinationTests` | Test-flag-only commands and observations |

`Ride` also retains all field initializers in `Ride.cs`, preserving initialization order. Its partial
files share the same object and static state. The older Gley path remains supported by the car-physics
option, so it is not dead code. Unity callbacks and Harmony handlers can be invoked without an ordinary
C# call site; absence of a direct caller is not enough to delete them.

Driving/planning diagnostics call `Log.Trace`; startup, ride summary and Harmony status messages use
`Log.Info`, and failures use `Log.Error`. Existing diagnostic text and the legacy prefix filter in
`Info` are preserved. Errors remain visible without a test flag, including errors whose text starts
with a diagnostic prefix. `tests/Adapter/LogTests.cs` checks both test-mode states and both logger sinks.

## Validation and evidence

Run the complete Release test suite, then the Mac SDK build, then both editor checks sequentially:

```sh
DR_TRAFFIC_SEEDS=500 dotnet test tests -c Release
tools/build-mod.sh
tools/check-drive.sh
tools/check-paint.sh
tools/package-mod.sh --no-build
```

The build uses an isolated SDK clone under `build/sdk` unless `BA_SDK` overrides it. A new worktree
must have its own writable SDK project and local game references (`build/game-dlls` or `BA_GAME_DLLS`).
Do not point it at another session's writable SDK. These commands do not need a running game, a test
rig or a save. Packaging checks names, versions, locales, file inventory and hashes; it does not publish.

The optional lane-graph tests use `build/sol-tests/lane-graph.json`. Reuse an allowed local fixture when
available; do not collect it from an unavailable or off-limits rig. `TrafficTests.PendingScenarios` is
an existing explicit skip. The full command above runs 500 seeds per traffic template (3,000 scenarios),
enables the regression budgets and expands the related test sweeps; the default command uses 100 seeds
per template and coarser sweeps. The suite's wall-clock performance assertion is unchanged; record timing
failures and rerun without overlapping your own build/test jobs before attributing them to code.

The separate Python suite checks the rig clients offline:

```sh
BA_BRIDGE_TOOLS=/path/to/bridge/tools python3 -m unittest discover -s tools/agent-tests -p 'test_*.py'
```

It imports the shared `ftlib` dependency at module load. An unavailable or off-limits dependency is a
blocked check, not a pass. Do not stub out that package just to report a green suite. Building or
controlling the private game rig is a separate task with its own ownership rules.

Automated checks do not establish in-game acceptance. Preserve the dated evidence in `CHECKS.md` and
record any new runtime result explicitly, including the tested binary and platform.
