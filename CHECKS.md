## Route line candidate, 23 October 2026

**In-game acceptance pending.** This branch adds the default-on “Show the planned route on the road” option.
Offline results and implementation constraints are in `docs/route-line.md`. No normal installation,
Windows/Home PC access or native game run is part of this change.

Peter's checks on a disposable save, with only the reviewed candidate enabled:

1. Start an own-car, hailed-taxi, taxi-stand and private-driver ride. Check blue/transparent/readable colour,
   sensible width, no pink/missing shader, and a road-facing line from the vehicle to the road arrival point.
2. Drive ordinary bends, a U-turn, lane changes and an obstacle-offset route. Check the curve stays on the
   road, shortens continuously at 1x/2x/3x, and does not jump to a later crossing/loop or leave a trail behind.
3. Trigger an accepted obstruction reroute. The old route must disappear immediately and the replacement
   must appear after its bounded height preparation, with no line to the old destination. Check backing recovery.
4. Toggle the option off/on while moving and paused. Off hides immediately; on restores the current remainder.
   Restart the game to check both persisted values; fresh/reset options should default on.
5. Complete, Skip and cancel trips; get out of an own car; load/unload a save; test hospital/foreign travel
   interruption where practical. No line should remain. The next ride should have just one line.
6. Depart parking bays/parallel spaces and arrive with autopark. The road line hides during the separate local
   manoeuvres and returns after departure. Check the Hamptons bridge in both directions: hidden during fades,
   correct far-side remainder, no old cross-map segment after landing.
7. Check slopes/ramps, streamed road sections, near/far cameras, daylight/night and rain. Watch for clipping,
   flicker or hovering. Geometry without a nearby collider uses planned height and needs particular attention.
8. Disable real car physics and repeat a curved taxi/driver trip and reroute for the legacy Gley route. Compare
   frame times on a long route with the option on/off; no continuing managed allocation or growing renderer count.

## Quick destinations, 22 October 2026 (0.4.1)

J selects vanilla's pinned building; K selects the most recent residential building entered in the loaded
game. Each has a persisted mod-option dropdown (Disabled / G / J / K / U / V / X). They work from a stopped
own car, opening its normal destination map, or from an already-open taxi/stand/private-driver map. Both
dispatch through vanilla `TaxiSystem.TravelTo`, retaining fares, private-fence checks and the existing ride
implementation. Home is session-only, seeded when loading indoors; businesses do not replace it. Unload or
a replacement save object clears it, with no custom save writes. Missing destinations and conflicting keys
show notices without dispatch. Normal/subway maps, typing, modifiers, menus, activities and other travel
do not dispatch; a queued own-car request also rechecks its save/map/car after opening the map.

- **Automated:** the final Debug suite passed 393 tests and skipped one existing pending traffic scenario;
  the existing frame-cost benchmark failed (1× p99 10.002 ms against 8 ms), then failed at 3× on its separate
  retry (11.974 ms). No limit was relaxed. The shipped Release Core is byte-identical to 0.4.0; Release-mode
  validation is recorded below. All 28 destination adapter cases passed. These link the production input,
  dispatch and destination-tracking source against narrow game API doubles. They cover residential/business
  visits, current/removed pins, unload/reset and replacement saves, rebinding/disable/conflicts, blocked input,
  own-car refusal and cancellation after closing the map, changing cars/saves or opening another UI.
- **Release validation:** `dotnet test tests -c Release` passed all 394 tests, including the unchanged
  frame-cost benchmark, with one existing skipped scenario in 46.95 seconds. The tested Release Core DLL
  exactly matches the packaged DLL. Debug timing failures above remain recorded; they are not functional
  shortcut failures. Log: `build/destination-shortcuts/final-release-tests.log`.
- **Build:** final official SDK build passed against the real game DLLs; no warnings in the new destination
  sources. Existing unrelated build warnings remain. Local package: `build/DrivenRides`; build log:
  `build/destination-shortcuts/final-build.log`. JSON parsing and `git diff --check` passed.
- **Checked in the isolated headless Mac game:** real Unity keyboard events started three own-car trips
  (J for pin, K for home, then U after rebinding), a hailed taxi, taxi stand and private-driver trip. All six
  selected the intended building, physically drove, and completed with Skip and clean control/ride cleanup.
  A further K trip drove its full 638 m route and arrived normally in 118.3 seconds; pressing U during it
  did not change the destination. No contacts, red-light violations or stalls were recorded in these trips.
- **Checked in game:** home A → business → home B; indoor load seeds home, outdoor load clears it; no pin,
  no home, disabled and duplicate bindings; Ctrl-key, ordinary map, pause menu and options suppression.
  The game options dropdown showed the localized key choices and changing U back to J through that UI
  updated the binding. Rebinding also persisted across save loads. Fixture setup failures are retained
  separately and do not count as passes. Evidence: ignored `build/destination-shortcuts/` JSON and player logs.
- **Final 0.4.1 binary:** installed package hashes verified before launch. The restart retained J/K chosen
  through the options UI, and J physically drove to the expected pin with Skip and clean cleanup. The load
  outlasted the helper timeout but completed normally; testing resumed without reloading. Results:
  `final-restart.json`, `final-j-smoke.json`, `final-smoke-resume.log` and `final-install.json`.
- **Rig cleanup:** the game exited after the graceful-stop helper timed out. Its original mods, preferences,
  settings and installation receipt were restored; all 263 original tracked hashes verified. Ownership released,
  pending zero. Receipts: `restored.json`, `released.json`. Only disposable FT saves were used.
- **Limits:** these checks used the native Mac game with null graphics, so rendered UI/camera appearance
  and interactive Windows behavior remain unverified. Moving-car and typing suppression, Hamptons trips
  and fare/access refusal UI were not exercised live in this run. Existing driving and
  parking limitations below still apply. Normal Windows installation is verified below; it adds no Windows
  gameplay evidence.

**Release package:** `build/package/DrivenRides-0.4.1.zip`, nine files, 2,088,971 bytes. Package validation
passed. ZIP SHA-256: `6ad1bb70b6540ac8ce1ff9778a7f82059570d3aee90eb910a77c9b7e23c8fea9`.
Adapter SHA-256: `b1de9fa64c5af80de3be8ef75eb835f99224e6b05d905570d178867a00374bde`.
Core SHA-256: `45c6a24c20bf49ca1c6d5e5b87f389aa58ab512540ab8dd2718b771d94edf74d`.
**Installed on Peter's normal PC at his request, 22 October 2026, 18:40 CEST:** all nine installed files
match the 0.4.1 release manifest. The previous radio-enabled 0.4.0 is backed up at
`DrivenRidesPackageBackups/DrivenRides-20261022-184033`. The normal game was closed and was not launched.
The pre-install 500-seed-per-template traffic soak passed all six templates (3,000 scenarios).

The Desktop folder `HART Self Driving 0.4.1 Release` contains the same verified ZIP, extracted package,
manifest, changelog, Workshop guide and paste-ready description/update notes. Its previous 0.4.0 folder
is preserved in `Documents/HART Self Driving Release Backups/0.4.0-before-0.4.1-20261022-184153`.
Seven handoff files and nine extracted package files verified. Receipts are under
`build/release-0.4.1/`: `normal-install.json`, `pc-release-handoff.json`, `final-pc-state.json` and
`normal-released.json`. Nothing published to the Workshop.

## Passenger radio, 22 October 2026 (0.4.0 release update)

Taxi, taxi-stand and private-driver boarding now enables the existing game radio and phone controls. It keeps
the station, playback position and volume. Arrival, Skip, cancellation and unload release the temporary radio
session; headphones or a real car entered meanwhile keep their audio. Save/manager/radio identity protects a
replacement world from stale cleanup. The radio's two headphone-change handlers are scoped to this session,
without raising unrelated vehicle-entry events. Own-car rides retain vanilla behavior.

- **Automated:** 366 tests passed, one existing pending traffic scenario skipped, including the 3,000-scenario
  traffic soak and 13 adapter lifecycle tests. Those tests link the actual `RideRadio` source against narrow game
  API doubles; the official SDK build also passed against the real game APIs. Cases cover preexisting playback,
  exit, headphones added/removed, replacement save/manager/radio, repeated cleanup, a subsequent ride,
  a real vehicle radio and missing phone controls.
- **Runtime evidence:** retained in ignored `build/radio-0.4.0/`. Initial taxi checks confirmed activation and
  Skip cleanup, but one checkpoint still had the player in their own car and another sampled before the map's
  pause was released. These are retained as setup limitations. The test snapshot now waits for one second of
  actual simulation after departure.
- **Checked in the isolated rendered Windows player, final adapter:** two taxi rides at 1× reported an
  unmuted, playing AudioSource, the existing Jazz station/clip, enabled phone controls and owned radio state.
  Skip (12 seconds) and cancellation (10 seconds) both muted the radio, disabled controls and cleared
  ownership. No contact/red-run/stall was recorded. A short arrival attempt used vanilla fallback before
  boarding and correctly left the radio off; it is not a driven-arrival check. The taxi-stand attempt had no
  nearby stand, and the private-driver attempt had no registered vehicle. Neither refusal counts as a pass.
  Results: `final-taxi-skip.json`, `final-taxi-cancel.json`, `final-taxi-arrival.json`,
  `final-stand-arrival.json`, `driver-cancel.json`, with corresponding game logs.
- **Release updated on the normal PC:** all nine files match the new package manifest; normal game closed.
  Earlier 0.4.0 is backed up at `DrivenRidesPackageBackups/DrivenRides-20261022-115723`. The Desktop folder
  `HART Self Driving 0.4.0 Release` now contains the new ZIP, extracted package and upload text; the previous
  handoff remains in `Documents/HART Self Driving Release Backups/0.4.0-before-radio-20261022-115835`.
  Adapter SHA-256: `f733303f5ec63ca04166d8a67792a747a841db41ae3fb140932bd2dd32d78489`.
  ZIP SHA-256: `9f9b88056213ec286f8a563772480e78d99ba2e2e4343cdaa74f9a5c26a9f089`.
  Core remains byte-identical to the preceding 0.4.0 package. Nothing published to the Workshop.
- **Rig cleanup:** isolated player stopped; original mod package, all 17 temporarily parked mods, preferences
  and installation receipt restored. All 105 original tracked file hashes passed. Normal and agent ownership
  released, with no pending work. Receipts: `normal-install.json`, `pc-release-handoff.json`, `agent-restored.json`.
- **Pending:** speaker-level audibility (the isolated background player deliberately mutes its audio listener),
  interactive station/volume and headphone changes, driven arrival, taxi-stand and private-driver gameplay
  coverage. The existing
  disposable checkpoint has no private-driver vehicle; its refusal is retained in `driver-cancel.json`.

## Release preparation, 22 October 2026 (0.4.0)

Promotes the parking/departure work and the more assertive driving / faster-corner tuning below to 0.4.0.
The release Core is byte-identical to the final `driving2` Core used in the limited Mac comparison;
the adapter version, package metadata and release documentation are updated. Existing game evidence
and failures remain applicable; this preparation does not constitute a new gameplay acceptance run.

- **Automated:** 353 tests passed, one existing pending traffic-scenario test skipped, with 500 seeds for
  each of six traffic templates (3,000 scenarios). Official SDK build and package validation passed.
- **Package checked:** `build/package/DrivenRides-0.4.0.zip`, 2,080,575 bytes, nine files. Every ZIP entry's
  size and SHA-256 matches `build/package/DrivenRides-0.4.0.manifest.json`. Includes the changelog and real
  Workshop thumbnail. Internal game build **3682** was verified from the Windows `Player.log`; the separate
  Steam build ID is 25482473. The package now uses the internal build number expected by the Mod Creator.
- **Installed in the normal Windows game, 22 October 2026, 11:23 CEST:** all nine installed file hashes and
  sizes match the release manifest. The adapter reports 0.4.0. The game was closed and was not launched.
  Previous parking13 files remain in
  `DrivenRidesPackageBackups/DrivenRides-20261022-112315`. Normal ownership was released, held false and
  pending zero. Installation receipt: `build/release-0.4.0/normal-install.json`.
- **Prepared, not published:** `CHANGELOG.md` ships with the package; `workshop/WORKSHOP.md` contains the
  description and update notes for existing Workshop item 3819853552. No Workshop publication, Git commit
  or release tag was made. The package was built from the existing working tree; its release source/input
  hashes and tracked diff are retained in `build/release-0.4.0/source-manifest.json` and `working-tree.patch`.
- **Pending:** Windows gameplay acceptance of the new driving tuning, representative moving-traffic trips
  at 1×, stop-line overrun reproduction/fix, and the wider parking/vehicle coverage described below.
  The observed 3× stop-line overrun remains a known issue in the player-facing release notes.

Adapter SHA-256: `f46757e218f68bf7e30c09f5c040bc18d3f66f8efdecdde6a21953646e220d3c`.
Core SHA-256: `45c6a24c20bf49ca1c6d5e5b87f389aa58ab512540ab8dd2718b771d94edf74d`.
Fresh test, build and installation logs are retained under ignored `build/release-0.4.0/`.

## Faster corners, 21 October 2026 (0.3.0-driving2)

The preferred sideways acceleration rises from 4 to 6 m/s²: an identical 6.5 m radius curve permits
18.4 → 22.5 km/h, and a 20 m curve 32.2 → 39.4 km/h, before other caps. This deliberately accepts some
extra tracking error to reduce corner time. Candidate arc sizing retains its established 4 m/s² reference
pace; the existing per-arc speed cap preserves 15% radius slack against speed-sensitive steering lock.
The actual faster trajectory still goes through the body's, tyres' and lane's feasibility checks.

**Automated:** 353 Core tests pass, one existing pending traffic-scenario test skipped, including the full
500-seed soak for each of six traffic templates. No traffic soak budget was relaxed for this change.
The new timing regressions drive the approach, bend and 20 m of exit with steering lag/lock enabled, require
at least 5% time saved, at most 0.9 m transient tracking error and at most 0.15 m error on the exit lane.
A measured Dash regression keeps a 12 m arc available and checks that its actual cap leaves steering room.

| Isolated model, radius | Previous time | New time | Time saved | Maximum tracking error, before → after |
| --- | ---: | ---: | ---: | ---: |
| 6.5 m | 11.14 s | 10.44 s | 6.3% | 0.776 → 0.872 m |
| 12 m | 11.46 s | 10.62 s | 7.3% | 0.527 → 0.660 m |
| 20 m | 11.88 s | 10.98 s | 7.6% | 0.425 → 0.552 m |

Two older precision bounds explicitly change from 0.6 m to 0.8 m / 0.75 m to record this tradeoff; their
exit-lane and Ackermann-improvement checks remain. The kerb fixture now puts the raised ground under the
left tyre itself instead of depending on drift past x = -1. The post fixture moves 0.5 m into the corner
to continue blocking both rounded and sharp paths; at the new speed the rounded body clears its old point.

**Checked in game, limited Mac comparison:** four ordinary turns and two U-turns were chosen by sorting
the catalogue IDs by SHA-256, before observing outcomes, then driven in the limo, Mersai Di Dash and Vord
Tiaravic. Same disposable FT checkpoint, same command, 3× world speed, moving traffic/pedestrians off,
traffic lights obeyed. Parked vehicles and light phase can differ across reloads. Timing uses recorded
simulation seconds and interpolated front-axle crossings of the catalogue's entry/exit gates.

- Baseline (`20261021-222153`): of 18 planned cases, 14 passed and four long-car U-turns were avoided.
- Final Core (`20261021-223244`): 13 passed, one failed for a stop-line overrun, and the same four U-turns
  were avoided. All 14 driven cases reached their last gate. No judged contact, intrusion or stall was recorded
  in these planned cases. The red-run failure remains a failure: Dash on `turn-5188-11975`.
- Including that failed case in the measurements, the 12 ordinary turns averaged **5.34 → 4.68 s** between
  gates (12.3% shorter) and **26.86 → 31.50 km/h** (17.3% faster). The two driven Vord U-turns averaged
  **8.28 → 7.47 s** (9.8% shorter). Largest recorded rear-axle tracking error across all 14: **0.43 → 0.46 m**.
- Whole-case time, including approach/exit and waiting, averaged **15.19 → 17.46 s** across all 14 driven
  cases. Thus this run demonstrates quicker bends, **not** shorter complete journeys. Light waits and parked
  vehicle variability prevent a controlled full-route timing claim. The stop-line trace records slow braking
  before the bend; whether this reproduces at 1× or was caused by the corner change is unverified.
- Four additional control cases ran each time. The nominally good U-turn hit a mailbox under both policies,
  and the bad turn failed by contact rather than its expected intrusion. The rig therefore reports itself
  **uncalibrated**. Its categorical pass count is not a clean acceptance run; raw traces and all failures are retained.

**Rejected first experiment:** coupling both corner speed and arc sizing to 6 m/s² increased the Dash's
minimum circle from 9.48 to 11.95 m. A previously accepted arc became vias, with tracking error reaching
1.87 m. That experiment (`20261021-222718`) had 12 planned passes and six avoided cases, including two blocked
by parked cars; its positive turn control also failed intrusion. Keeping the established arc-sizing reference
restored the arc in the final rerun. No failed/avoided cases were dropped from the evidence.

**Pending:** representative journeys with moving traffic at 1× on Windows, stop-line overrun reproduction,
contact/recovery costs, heavy vehicles, and subjective driving feel. The normal game installation is unchanged.
The Mac rig's original mod snapshot/settings are restored after testing and its ownership released.

**Build:** official SDK build passed. Package: `build/DrivenRides/`, version `0.3.0-driving2`.
Adapter SHA-256: `9342ef0dd18824bfec677f88a0920faba835132abc4a7de483cab25a4b185349`.
The Mac experiment used the `driving1` adapter with
only the Core DLL swapped; installed hashes were verified before each launch. Final Core SHA-256:
`45c6a24c20bf49ca1c6d5e5b87f389aa58ab512540ab8dd2718b771d94edf74d`.
Evidence is local and ignored in `build/corner-speed/`: source/package snapshots, installation receipts,
all three sessions' traces/results/logs, the graph/catalogue and selected cases, `compare.py`,
`comparison-final.json`, the isolated timing probe, and `final-core500.trx`.

## More assertive driving, 21 October 2026 (0.3.0-driving1)

Peter's priority is shorter trips, with occasional contact acceptable when it earns a useful time saving.
This first pass changes the shared road-driving margins: moving following uses 0.30 s reaction / 3.5 m/s² braking,
junction headway is 0.60 s plus 0.30 s from rest, and merging can ask the following car to brake at 4 m/s² with
a 1.2 m gap. Passing a waiting car rises from 3.5 to 4.5 m/s; squeezing beside a stationary car rises from 2 to 3 m/s.

**Automated:** 349 Core tests pass, with the existing pending traffic-scenario test skipped. New pedal-response
cases cover 4.8, 5.8 and 9 m bodies following at 7, 10 and 13 m/s, then braking with the leader. Moving headway is
0.66–0.70 s; minimum bumper clearance during that braking is 0.90–1.31 m. The existing stopped-truck regression
still passes. Tests now distinguish a short crossing gap the car can take from a nearer conflict it must stop for.

**Simulation comparison:** 500 identical seeds per template, before/after Core snapshots. Means below use only
the seeds that completed under both policies (475 for lights, 500 for each other template), so dropping a slow
failed run cannot manufacture the timing gain. These are short synthetic scenarios, not in-game journey times.

| Scenario | Before | After | Time saved |
| --- | ---: | ---: | ---: |
| Lights | 28.52 s | 28.04 s | 1.69% |
| Priority junction | 15.86 s | 15.76 s | 0.61% |
| Merge | 13.26 s | 13.26 s | 0% |
| U-turn | 17.17 s | 17.12 s | 0.28% |
| Head-on narrow section | 14.69 s | 14.69 s | 0% |
| Pulling away | 13.92 s | 13.83 s | 0.61% |

**Tradeoff:** 2,979 of 3,000 scenarios complete without a judged failure, versus 2,977 before. All 21 failures
are in the light template: traffic touching the stationary ride car, jams or timeout, within the existing 6%
simulation allowance (23 before). No moving-ride contact or red-run failure appeared. Runs losing the option to
stop before a conflict rise from 37 to 40 at lights, 8 to 9 at priority junctions and 31 to 32 when pulling away;
repeated stops rise from 47 to 64 at lights and 114 to 136 at priority junctions. These are measured costs of the
new policy, not improvements. The soak budgets explicitly record them and now also cap mean arrival times at the
previous policy's means, rounded up. Collision, red-light and stall assertions retain their existing thresholds.

**Pending in game:** measured trip times and subjective feel, contact frequency and recovery time, including
the long limo and truck. Compare the same routes and settings at 1× and include failures, jams and fallback time
in the result. The measured synthetic gains are modest; this pass does not establish a large real-game speedup.
The normal Windows installation remains the previously installed parking13 build.

**Build:** official SDK build passed. The local package is `build/DrivenRides/`, version `0.3.0-driving1`.
Its adapter SHA-256 is `82b4a8b40b45bd7236cb117a2381a9d65b1b54354ab2f6324ce2cfaca72e6471`; Core is
`1ebf71e8daed9aabd43da339c32655fae4cc5ee0d86fce6a7d168413c83fe10f`. It was subsequently used as the
isolated Mac baseline for the corner comparison above; its moving-traffic tuning remains unverified in game.

Evidence: ignored `build/assertive-driving/` contains before-source snapshots, a reproducible benchmark,
both 3,000-seed JSONL records, `comparison.json`, and unit/soak TRX results. No moving-traffic game validation
is claimed for this tuning.

## Parking-lot departures, 21 October 2026 (0.3.0-parking13)

Legal parking now uses a local exit search through the aisle/driveway, followed by a directed road handoff.
The parked heading and distance from a road waypoint no longer reject a bay before local planning.
Nearby ordinary parallel strips retain the established curbside pull-out; remote parallel areas use the lot search.
The implementation also reads rendered curb geometry, preserves only the original parked bumper overhang,
and validates a simulated driven trajectory before moving. A failed candidate may try another entry while
stationary; a failure after movement returns control in place.

- **Automated:** 345 Core tests passed, one existing pending traffic scenario skipped. Departure regressions
  cover long road segments, both parked orientations, an offset driveway, disconnected surfaces, thin curbs,
  body/tyre clearance, obstacle-index equivalence, tracking-validation cancellation and a road handoff that
  stops inside its accepted pose tolerance instead of chasing a final centimetre-scale tracking error.
- **Build:** official SDK build passed. The final driving build's adapter SHA-256 is
  `4825d921a2cb06a9b9a87cadcd5907c3e9a49a1f216e79bee19efd898ad027d1`; Core is
  `400390c4651e852e071d1df1d18965fea893ca7a41328a3d673a09aee1ae669c`.
- **Checked in the isolated rendered Windows game during development:** a Vord left a waterfront bay via
  a 71.3 m aisle/driveway manoeuvre, yielded to pedestrians, held up to four approaching cars and drove on.
  A factory lot departure also completed. The factory rerun measured 20.08 m of physical road driving,
  excluding one existing bridge skip. Both returned controls with no drive/manoeuvre component or reservation
  left and no recorded road contacts or red runs. These initial passes preceded the final orientation fallback
  and tracking-validation additions; final-build results are listed below.
- **Regression discoveries retained in the evidence:** the pedestrian NavMesh also covers parking aisles;
  centre-only rasterization missed a thin raised curb; the original search could exhaust its budget for the
  reverse orientation; execution could consume the planning margin and then stop unnecessarily; counting a
  bridge teleport as road mileage could produce a false test pass. Each led to a targeted correction.
- **Coverage harness:** `tools/agent-tests/parking-cases.py` verifies the installed package, reloads a disposable
  FT checkpoint for each seed, samples actual free legal spaces and records the actual starting pose. It tests
  real exit movement plus 20 m of physical road driving and cleanup. Bays, parallel spots, reversed orientation,
  location filtering and unrestricted selection are supported. Failed/refused cases remain failed results.
- **Checked on the final manoeuvre/Core revision, before restoring curbside routing:** factory bay, seed 3, reversed orientation at (-528.64,-310.02), completed
  a 38.8 m local manoeuvre and 20.67 m of physical road driving. The result excluded one bridge jump, recorded
  no contacts/red runs/stalls and confirmed returned controls with no controllers or reservation left.
  Evidence: `verified-reverse-cases.jsonl` (all installed hashes recorded) and `verified-reverse-Player.log`.
- **Checked on the final manoeuvre/Core revision, before restoring curbside routing:** waterfront seed 1 selected another bay at (-230.20,-403.97), completed the
  aisle/driveway exit and 20.39 m of road driving with no position jumps. No contacts/red runs/stalls; controls
  restored and all controllers/reservations cleared. Evidence: `verified-waterfront-1.json` and its Player log.
- **Checked on the final package:** curbside seed 6 repeated the endpoint-timeout pose at (-86.80,455.88).
  It exited and drove 20.33 m on the road in 20 seconds, with zero contacts/red runs/stalls, returned controls
  and complete component/reservation cleanup. `final-roadside-cases.jsonl` records all seven installed hashes;
  `final-roadside-Player.log` records the manoeuvre. The earlier tight poses at (-7.77,172.55) and (-111.80,455.88)
  were not replayed verbatim after the routing change and remain wider-coverage cases.
- **Checked on the final package:** the opposite-facing curbside strip, seed 7 at (-182.30,473.25), also
  completed its exit and 20.04 m of road driving with zero contacts/red runs/stalls and complete cleanup.
  Evidence: `roadside-parallel-7.json` and its Player log.
- **Checked on the final package:** factory seed 2 at (-546.84,-309.88) repeated the bay exit and 20.00 m of
  physical road driving, excluding one bridge jump. Zero contacts/red runs/stalls and complete cleanup.
  Evidence: `roadside-factory-2.json` and its Player log. All three final-package regression cases passed;
  `final-summary.json` combines their records, package hashes and the cleanup receipt.
- **Checked cancellation:** `cancellation-22.json` gets out during factory departure planning, before movement.
  The player is outside, controls return and all temporary controllers/reservations are gone. This used the same
  cancellation code before the final Core handoff-tolerance correction.
- **Curbside coverage:** the lot search completed seed 7 at (-182.30,473.25), then 20.29 m of physical road
  driving with zero contacts and complete cleanup (`verified-parallel-7.json`). It refused tight parallel samples
  at (-7.77,172.55) and (-111.80,455.88) without moving (`final-parallel-city-5.json`, `verified-parallel-6.json`).
  This prompted restoring the established curbside path for nearby parallel strips instead of extending the new
  surface policy to every parked car. A subsequent curbside run reached the road but timed out 13 cm from its
  endpoint; the shared road-handoff stopping rule now applies to curbside exits too (`roadside-before-handoff-Player.log`). These failures remain in the evidence; a free legal spot does not imply
  a verified departure. The earlier parallel fixture near the factory found no free legal space and is also
  recorded as a refusal.
- **Pending beyond the sampled cases:** the full city/vehicle matrix, larger cars, every traffic-reservation
  scenario and interactive visual acceptance. Garages remain unsupported. Headless results cannot validate
  the rendered-surface access used here.
- Evidence is retained under ignored `build/parking-departure-study/`: build/unit logs, package hashes, seeded
  case results, game logs, screenshots and geometry replays. No game assets or disposable saves are added to Git.
  The isolated player is stopped, its temporarily parked extra mods restored, and `agent1` ownership released
  (held false, pending zero; `cleanup.json`).
- **Installed in the normal Windows game at Peter's request, 21 October 2026, 21:42 CEST:** the exact tested
  parking13 package is active in `ModsLocal/DrivenRides`; all seven installed SHA-256 hashes match the tested
  manifest. The pre-install traffic soak passed all six templates at 500 seeds each. The previous local copy
  remains in `ModsLocal-disabled`; both that package and the subscribed Workshop package, plus the original
  enabled-mod list, were copied to `DrivenRidesPackageBackups/before-parking13-20261021-214223`.
  Workshop item `3819853552` was removed from the enabled-mod list to prevent loading both copies; its
  subscription/files and all other enabled mod IDs were preserved. The normal game was closed throughout
  installation and was not launched; normal-game runtime acceptance is pending. Normal ownership was
  released (held false, pending zero). Receipt: `build/parking-departure-study/normal-install.json`.

## Stopped-car braking, 18 October 2026

The reported Food Truck ride detected the leader but hit it at 1.6–2.1 m/s as it stopped at red lights.
The normal-game log measured the truck at 5.80 m long, front bumper 4.45 m ahead of the rear axle;
the problem is reproducible offline using those dimensions and actual pedal outputs. Following a stationary
car now applies the same distance-based brake demand as a stop line, keeping the existing following gaps.

- **Automated:** five new pedal-response cases failed before the fix and pass after it, including the
  measured truck at 7.2, 10 and 13.9 m/s, a 4.8 m car and a 9 m vehicle. Each now stops with at least
  1.5 m clearance. These are synthetic regressions based on the report and logged measurements, not a
  recorded-frame replay. The full Core suite passes: 331 passed, one existing pending scenario skipped.
- **Build:** official SDK build passed. The isolated Windows agent uses its parking12 adapter with the
  updated Core DLL, verified SHA-256 `4ca8664ab777af5868487bea8739ac23c87a469a7576cba51f4864b2baa5b51f`.
- **Checked in the isolated headless game:** the patched truck completed the 1,313 m outbound road route at
  3x with zero contacts, red runs or stalls. The return drive also had zero contacts/red runs/stalls, then
  finished through vanilla after the planner refused a passage beside a parked delivery truck. Both returned
  controls and cleared the drive/reservation state. This checks simulation, not rendered appearance.
- **Pending:** repeat the exact stopped-leader queue in game. Neither these drives nor the unmodified short
  baseline encountered that situation; they do not establish that the original queue collision is gone.
- Evidence: `build/foodtruck-following/` (ignored), including the original game log, before/after regression
  results, full test/build logs, baseline game log and the isolated installation receipt. The initial validation did not change the normal game.
  The patched Core remains in the isolated test snapshot; its prior DLL and
  installation manifest are backed up under `C:\BigAmbitions-AgentTests\foodtruck-braking-before-20261018`.
  The agent player is stopped and its ownership lock released.
- **Installed in the normal PC game, 18 October 10:46 Europe/Berlin, at Peter's request:** replaced only
  `Dependencies/DrivenRides.Core.dll` with the same tested SHA-256 above. The installed game adapter's 88 Core
  type references and 484 member references resolve against it; all six other package files were preserved
  and hash-verified. The 500-seed traffic soak passed (six cases). The game was closed during installation;
  the normal ownership lock was released. Prior package backup: `DrivenRidesPackageBackups/DrivenRides-before-braking-20261018-104650`.
  Receipt: `build/foodtruck-following/normal-install.json`. The fix loads at the next normal-game launch.

## In-game checks: unparking and visible parallel parking (0.3.0-parking12)

Pending unless a result below explicitly says checked.

**17 October, isolated headless rig:** parking10 completed a physical parallel park while holding one approaching
Gley car. Final rear axle was (-179.45, 455.82), target (-179.53, 455.87): about 0.09 m and 0.045 rad error.
All wheels were legal; the player stayed seated; controls returned; both drive components and the reservation
were gone. This checks simulation/physics, not rendered appearance. Evidence:
`build/agent-tests/parking10/Player-agent-first.log` and `ride-results-first.jsonl`.

An immediate departure was safely refused. Parking11 geometry logging reproduced the cause in another tight
space: the search exhausted its root because each 0.96 m primitive hit a parked car after 0.56–0.80 m of clear
travel. Parking12 retains the collision-checked prefix of such a step. All four captured departure goals now
solve offline, and mirrored regression cases track the result without contact.

**Checked, parking12 headless:** reloaded `FT Driven Rides tight departure`, planned an 8.7 m manoeuvre in
3.45 seconds, completed the pull-out with a reverse correction, and handed over to the road controller. Final
rear axle (-127.99, 426.13), target (-127.81, 426.26): about 0.22 m and 0.017 rad error. Reservation released; no
approaching car needed holding in this run. Reloading regenerates surrounding traffic, so the exact original
obstacle geometry is covered by the offline replay, not this run. The subsequent road trip was intentionally
paused and the player closed at Peter’s request for the night; no completed-trip result is claimed.
Evidence: `build/agent-tests/parking12/Player-agent.log` and `Player-agent-stopped.log`. The arrival rerun was
refused before driving because the rig placed the car off the usable lane. Skip/exit tests remain pending.

**Night handoff:** agent player stopped, `agent1` lock released (held false, pending zero). Parking12 is installed
only in the isolated agent profile. Resume from the retained disposable checkpoints; fix the arrival setup and
repeat parking followed by departure, then interruption cleanup. No normal-game install was changed.

Use a disposable save. The local manoeuvre planner lives in
`Core/Parking`, separate from the road planner and `DrivePlan.Shifts`.

**Reservation checks still pending beyond the successful headless arrival above:** the local manoeuvre reserves its swept area before moving. Check a following car,
an oncoming car in an unaffected lane, an existing occupant clearing the area, and a car turning/lane-changing
into it. Approaching cars should brake outside, overlapping Gley spawns should be refused, and traffic should
resume after success, timeout, Skip, exit, hospital, unload or planning cancellation. A blocked occupant still
causes bounded fallback. Repeat pause/resume. Look for `Parking traffic: reserved`, `released`, and step-log
`clearing` / `held` counts. Results must have `parkingReservationsLeft: 0`. Check both new Harmony hooks are Active.

**Parking9, pending in game:** with autopark enabled, the initial road route now targets the lane beside an ordinary
parallel parking strip. Check a destination whose strip is on the other side of a one-way street: the car should
approach from the strip's direction before it stops, going around the block when needed. Check left-side parking
and a street with multiple lanes: it must finish in the lane adjacent to the strip. Look for `Parking navigation:`
and `Goal: parallel parking strips` in the test log. Disabled autopark retains the ordinary drop-off route; no
reachable strip also falls back to that search. At arrival, newly occupied spaces must be passed over and any
further road approach must follow lane direction. Physical parking passed once in headless mode; rendered appearance remains pending.

Offline routing tests cover both sides, an intervening lane, opposite-direction routing around a block, other
levels, spaces too small for the car, vehicle restrictions and a handoff near a junction.
Parking12 full suite: 326 passed, one existing skipped scenario. The 16 new reservation cases cover braking
without entry (including late acquisition and fast traffic), existing occupants, adjacent/opposing lanes, road
height, turning/lane changes, spawn overlap, pooled IDs and the whole rotated body sweep. SDK build and all seven
package hashes passed. Logs: `build/logs/parking12-tests.log`, `build/logs/parking12-build.log`.
The isolated agent package was installed with all seven hashes verified.

17 October evidence (disposable saves; Food Trucks bridge and other installed mods present):

- **Checked, parking3:** the Vord pulled out from 31 First Avenue, waited for passing traffic, aligned within 0.21 m
  and 0.03 rad of the lane target, then drove to 1 Fifth Street. Road tally: no contacts, red runs or stalls.
- **Checked, parking6:** a following traffic car blocked the parking path at 31 First Avenue. The car held still,
  including through pause/resume, then timed out and used the fade-and-park fallback. Legal parking, player seated,
  controls returned, neither `NwhDrive` nor `NwhManoeuvre` left. This was not a visible-parking success.
- **Pending, parking8:** the revised staging path pulls farther forward before reversing, and the search accepts
  an aligned target reached partway through a rollout. The measured following-car case now passes offline.
  Parking7 was installed, then the game was closed and the PC lock released for the Food Trucks session before
  this arrival could be repeated. Parking10 includes these fixes; its later headless physical arrival passed as recorded above. Rendered inspection remains pending.

Local evidence: `build/logs/game/parking3-195839/Player.log`, `build/logs/game/parking6-201952/Player.log`,
`build/logs/parking6-paused.json` and `build/logs/parking6-paused-later.json` (not committed).

1. **Departure:** L from a normal parallel space on either side of a street. It pulls out, straightens, and continues
   the planned road route. Repeat between two parked cars, with room to reverse first. No contact or kerb crossing.
2. **Refusal:** a boxed-in car or too little straight road before a junction stays put and returns the controls.
3. **Traffic:** existing occupants and pedestrians hold the manoeuvre; it continues when clear. Approaching Gley
   cars wait outside the reservation. A persistent block returns control within the bounded timeout. Neither
   adjacent parked cars nor traffic are removed to make room.
4. **Arrival:** drive to a destination with autopark enabled. Observe the approach and reverse into the space with no
   fade. All wheels end in a legal parking zone, the player remains seated and controls return. Repeat in a long car.
5. **Fallback:** no drivable approach, a space occupied after selection, or a failed manoeuvre uses the existing
   checked fade-and-park or leaves the car under player control. It never teleports into a newly occupied space.
6. **Lifecycle:** pause/resume, Skip during departure and parking, exit mid-manoeuvre, empty fuel, hospital and unload.
   No drive component, reservation, ignored-traffic flag, input override or altered time scale remains. Another ride can start immediately afterward.
7. **Settings/saves:** all three autopark settings; 2x/3x road speed (manoeuvres run at 1x); save/load during a manoeuvre
   using the game's ordinary vehicle state. No parking plan or ride state is written to the save.

Offline: `dotnet test tests --filter FullyQualifiedName~ManoeuvreTests` covers both departure sides, reverse tracking,
two-car clearance, occupied targets, bounded incremental search and holding for a new obstacle. Parking8: the full
suite passed 301 tests with one existing skipped scenario; the final 21-case manoeuvre suite also passed after adding
the exact recorded arrival coordinates. The SDK build and all seven package hashes passed. These tests cover model
tracking, not a successful physical parking run.

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
