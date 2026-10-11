# Driven Rides mod for Big Ambitions

A Workshop mod that makes taxi and private-driver rides drive through the city instead of fading to black, and adds a one-key shortcut for calling the private driver. Read `docs/implementation-plan.md` first. `CLAUDE.local.md`, if present, has the machine setup and is never committed.

## Layout

- `Core/`: pure C#, netstandard2.1. No Unity and no game types. Builds and tests on any machine with `dotnet test`.
- `tests/`: xUnit tests for Core.
- `unity/DrivenRides/`: the mod folder for the official SDK (`Assets/Mods/DrivenRides` once linked). Game adapters, Harmony patches and the runtime live here, because the SDK compiles this folder against the game DLLs.
- `unity/BuildEditor/`: the batch-mode build entry point, linked into the SDK's `Assets/Editor`.
- `tools/`: build, install, park and log scripts.
- `CHECKS.md`: the in-game checklist for the current milestone.

## Rules

- Never commit game DLLs, decompiled game code, Harmony binaries or save files. The build fetches Harmony from NuGet with a pinned checksum.
- Core decides, the game only shows. Keep game calls in thin adapters.
- Each Harmony patch logs one `Active` or `Not active` line at startup, with the method name.
- The mod writes nothing into saves. A ride is never saved; keep it that way or version a schema first.
- A ride must never cost more game time than the vanilla ride, and every failure must end the ride the vanilla way or cancel it cleanly.
- Mark evidence in the README as checked (seen in game), pending or unverified. Code reading is not a check.
- Neutral names: no references to any YouTube series or personal saves in shipped files.
