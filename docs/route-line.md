# Planned route line

The city owns one reusable `RouteLineView` and one HDRP unlit `LineRenderer`. `Ride.TryRouteLine` exposes
only an active road-driving phase. Physics rides expose `NwhDrive.PlannedLine`, the actual current
`DriveLine` after arcs, fillets and obstacle shifts, plus its existing progress. Every accepted reroute
replaces that line reference, including the bridge landing. Legacy Gley rides cache a display curve from
their expanded path and steering arcs; projection advances locally so a nearby future loop is not consumed,
and is never more than a waypoint behind the ride's own progress, so switching the option on after a block or
a loop selects the current suffix. Physics lines start at the plan's point for the driver's progress (the
rear axle). Gley lines start at the car's horizontal position and connect to that unconsumed suffix, because
native Gley turns can run wide of their waypoint line. Both use the plan's road height as the probe reference.
No steering, routing, speed, destination selection or save data is changed.

`RouteLinePath` retains the steering vertices and subdivides spans to at most 2 m. Its horizontal distances
match the driver's progress; a binary search identifies the unconsumed suffix. The adapter starts that
suffix at the car and projects it onto the road using the same nonallocating nearest-height probe as the
bridge landing. The ribbon is 0.4 m wide, #2F80FF at 82% opacity, lifted 0.06 m. HDRP's material validator
sets transparent blending and double-sided rendering, with depth testing enabled and depth writes/shadows
disabled. It does not show through buildings or vehicles. The material owns its colour because HDRP/Unlit
does not use LineRenderer vertex colours.

Initial height probes are limited to 128 samples per frame, beginning with the visible suffix and its preceding
height sample. The previous line is hidden immediately on a reroute; the prepared near portion of the new line
appears on the first update and extends as probing finishes. Unprepared points are never uploaded.
Nearby samples are refreshed twice a second (at most 33 probes, covering up to 64 m) for streamed road
colliders. One additional head probe follows
the car. Where no collider is available within 1.5 m of the planned height, the planned height is retained.
The Hamptons slopes/bridge and streamed geometry need rendered acceptance; this is not a guarantee that
every collider matches the visible asphalt.

One renderer/material and capacity-grown arrays are reused between rides. During ordinary frames the managed
side moves only the head vertex; when a sample is consumed or preparation extends the line, the cached suffix
is uploaded in one call. Unity still rebuilds the line mesh for the whole remaining route on any position
change, so the native cost per
frame grows with the route's remaining length. Geometry buffers may grow on a larger plan, never per ordinary
frame. A plan with a non-finite point hides the overlay for that plan only; other display errors hide it and
log once for that city, leaving the ride running. The first show in a city logs the shader and render queue
the material ended up with. Cancel, completion and Skip hide synchronously; phase checks hide it during
boarding, manoeuvres and bridge fades. City destruction and mod unload destroy the component, which releases
its objects/material. Option `routeline` uses the existing ModOptions/PlayerPrefs mechanism, default on, and
logs its changes like the other options.

## 0.4.2 rig follow-up, 1 November 2026

**Offline regression checked; in-game acceptance pending the batched release rig.** The
[rig re-run](https://github.com/PeterHartwieg/hart-self-driving/pull/3#issuecomment-6556866222)
recorded S1's first Gley U-turn head at (-127.096, 458.642), beside a car at (-127.090, 450.850).
The outgoing suffix was selected, but placing the head at its plan projection left a 7.792 m lateral gap.
There was no fitted steering arc for this native U-turn. Changing progress projection cannot put an
off-plan car onto that plan. `RouteLinePath.Head` now uses the Gley car's X/Z while retaining planned height;
physics keeps the rear-axle plan point. Waypoint progress and suffix selection are unchanged.

S4 is fixed with incremental display rather than accepting the 29-frame delay. Waiting for every height
probe made 3,716 samples take 30 preparation updates at 128 per frame. The same buffers and probe budget can
show the prepared near portion on the first update, then extend it, without a new renderer or asynchronous
job. Already-consumed samples are skipped. Full-route preparation can still take those updates, but no
longer gates first visibility. This was a small change to the existing preparation/upload loop.

The focused Core U-turn test failed with a 7.79199982 m head gap using the old plan-point behavior and passed
after the fix. It also checks the outgoing suffix; the block case now checks the head, and a new physics
case checks the rear-axle point and forward first segment. The required foreground Release suite passed
412 tests, with three existing skips (two unavailable graph fixtures and the pending traffic scenario).
`Ride.cs`, `RouteLineView.cs` and `RouteLineChecks.cs` were checked by hand for types, call sites, nullable
head propagation, delegate signature and prepared-buffer bounds. The updated Unity checks cover Gley/physics
heads, immediate near display, bounded extension and late switch-on; they were not run here.

No game or rig was run for this fix. Before merge/upload, the required `rig_test` session (Codex Sol, high)
must add verifiable before/red and after/green in-game proof to the PR from the owner's batched release rig.

## Offline validation

Run sequentially from this worktree (set `BA_GAME_DLLS` to an existing local game DLL cache if necessary):

```sh
DR_TRAFFIC_SEEDS=500 dotnet test tests -c Release
PYTHONDONTWRITEBYTECODE=1 BA_BRIDGE_TOOLS="$HOME/Coding_Projects/big-ambitions/big-ambitions-food-trucks/tools" python3 -m unittest discover -s tools/agent-tests -p 'test_*.py'
tools/build-mod.sh
tools/check-drive.sh
tools/check-paint.sh
tools/check-route-line.sh
tools/package-mod.sh
```

The Mac-hosted official SDK build targets Win64, as before; it does not launch a game. The new Unity check
exercises the production component's road projection, orientation/material state, consuming suffix,
replacement plan, bounded preparation, renderer reuse, disabling and warm managed allocations. Batch editor
objects do not receive the normal player-loop lifecycle, so the disable callback is explicitly invoked.
It is not a rendered game screenshot or native ride acceptance. See `CHECKS.md` for Peter's in-game matrix.

## Candidate evidence, 23 October 2026

The full Release suite passed 406 tests (including 12 new sampling/trim cases), with one existing pending
traffic-scenario skip. `DR_TRAFFIC_SEEDS=500` exercised all six 500-seed templates; the cached offline graph
fixtures also ran. All nine Python rig-client tests passed with read-only bridge tools and bytecode writing
disabled. The official SDK build and all three Unity checks passed, including zero managed bytes allocated
by 1,000 warmed route updates covering both head movement and suffix uploads. No game was launched.

The first route editor check incorrectly expected automatic `OnDisable` dispatch in non-playing editor mode;
it now checks hierarchy invisibility and explicitly exercises that callback. Initial and final logs are retained
under ignored `build/route-line/`. The first package wrapper invocation lacked `BA_GAME_DLLS`; it stopped
before building or fetching anything. The final validation exports the local cache path for every build step.
No traffic budget or performance threshold was relaxed. Existing unrelated SDK/analyzer warnings remain.
