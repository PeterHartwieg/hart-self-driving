# Planned route line

The city owns one reusable `RouteLineView` and one HDRP unlit `LineRenderer`. `Ride.TryRouteLine` exposes
only an active road-driving phase. Physics rides expose `NwhDrive.PlannedLine`, the actual current
`DriveLine` after arcs, fillets and obstacle shifts, plus its existing progress. Every accepted reroute
replaces that line reference, including the bridge landing. Legacy Gley rides cache a display curve from
their expanded path and steering arcs; projection advances locally so a nearby future loop is not consumed.
No steering, routing, speed, destination selection or save data is changed.

`RouteLinePath` retains the steering vertices and subdivides spans to at most 2 m. Its horizontal distances
match the driver's progress; a binary search identifies the unconsumed suffix. The adapter starts that
suffix at the car and projects it onto the road using the same nonallocating nearest-height probe as the
bridge landing. The ribbon is 0.4 m wide, #2F80FF at 82% opacity, lifted 0.06 m. HDRP's material validator
sets transparent blending and double-sided rendering, with depth testing enabled and depth writes/shadows
disabled. It does not show through buildings or vehicles. The material owns its colour because HDRP/Unlit
does not use LineRenderer vertex colours.

Initial height probes are limited to 128 samples per frame. The previous line is hidden immediately on a
reroute; the new complete line appears after sampling finishes. Nearby samples are refreshed twice a
second (at most 33 probes, covering up to 64 m) for streamed road colliders. One additional head probe follows
the car. Where no collider is available within 1.5 m of the planned height, the planned height is retained.
The Hamptons slopes/bridge and streamed geometry need rendered acceptance; this is not a guarantee that
every collider matches the visible asphalt.

One renderer/material and capacity-grown arrays are reused between rides. During ordinary frames only the
head vertex moves; when a sample is consumed, the cached suffix is uploaded in one call. Geometry buffers
may grow on a larger plan, never per ordinary frame. Display errors hide the overlay and log once for that
city, leaving the ride running. Cancel, completion and Skip hide synchronously; phase checks hide it during
boarding, manoeuvres and bridge fades. City destruction releases its objects/material, and mod unload disables
the component. Option `routeline` uses the existing ModOptions/PlayerPrefs mechanism, default on.

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
