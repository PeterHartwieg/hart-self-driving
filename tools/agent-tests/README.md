# Dedicated agent test player

For the **local Mac fallback**, see [MAC.md](MAC.md). It is manually launched by an agent when the Windows PC is unavailable; there is no schedule. The commands below manage the Windows rig.

A private Windows copy of the installed, licensed Big Ambitions player, used only for agent tests. It runs beside the normal game. This directory contains the build/management tools, not the game. Do not redistribute the resulting game copy, patched assemblies, or local build output.

## Separation

| Resource | Agent instance |
|---|---|
| Executable | `C:\BigAmbitions-AgentTests\Big Ambitions Agent Tests.exe` |
| Unity product / process name | `Big Ambitions Agent Tests` |
| Data and local mods | `%USERPROFILE%\AppData\LocalLow\Hovgaard Games\Big Ambitions Agent Tests` |
| Unity temporary cache | `%LOCALAPPDATA%\Temp\Hovgaard Games\Big Ambitions Agent Tests` |
| Player preferences | Unity's separate registry key for the agent product |
| Log | `C:\BigAmbitions-AgentTests\Player-agent.log` |
| Bridge | `127.0.0.1:18330` |
| Ownership | `%USERPROFILE%\.big-ambitions-bridge\agent1` (normal game uses `normal`) |

The ordinary game stays on port 8330 with its existing profile. The agent executable has a different process name so the older normal-game launcher cannot select it by name. Agent stop operations check the executable path, PID and creation time. The bridge health response identifies the instance, PID, profile, cache and mode. Client commands bind to the bridge session ID.

Unity PlayerSettings are changed in a private copy before startup. This isolates early mod discovery, saves, cache and preferences; changing only a save filename or a late mod path would not. Original game files are copied, never hard-linked. No ordinary saves or preferences are copied. Test saves remain under the distinct product directory, outside the normal game's save directory used by Steam Auto-Cloud.

The test player never connects to Steam. This keeps it from occupying Steam's normal game launch slot, which a second authenticated client otherwise does. During installation/update only, a short-lived helper uses the installed Steam library to verify base-game ownership and installed DLC, writes a local snapshot, then shuts down and exits. The player reads that verified snapshot for DLC availability; it does not grant unowned DLC. Subsequent player starts do not need Steam. Refresh the snapshot by updating after account/DLC changes. Steam statistics/achievement writes, online Workshop/blueprint features, and subscribed Workshop discovery are unavailable in this private build. Local mod discovery initializes independently of Steam.

The initial mod set is a snapshot of the installed Food Trucks test bridge and Driven Rides, when present. The bridge still depends on Food Trucks and retains its requirement that disposable save names start with `FT`. New versions must be deliberately installed in the agent profile; the normal ModsLocal directory is never the deployment target. The shared bridge lock enforces one owner for mutating operations against this test world. Its ownership is independent of the normal game.

## Use from the Mac

```sh
export BA_LOCK_OWNER="my-project-my-test-session"
python3 tools/agent-tests/agent lock acquire --purpose "My disposable test"
python3 tools/agent-tests/agent start
python3 tools/agent-tests/agent health
python3 tools/agent-tests/agent call new_game '{"name":"FT My disposable test"}'
python3 tools/agent-tests/agent call state
python3 tools/agent-tests/agent call time '{"pause":true}'
python3 tools/agent-tests/agent call save '{"name":"FT My checkpoint"}'
python3 tools/agent-tests/agent stop
python3 tools/agent-tests/agent status
python3 tools/agent-tests/agent log
# After the process exits, or when the game is paused and all work has finished:
python3 tools/agent-tests/agent lock release
```

Set `BA_LOCK_OWNER` to a unique name per session and keep it consistent across commands. Acquisition generates a private token; merely reusing somebody else's displayed owner name does not authorize control. The same owner can recover its token across tool/game restarts. Normal-game ownership is acquired separately using Food Trucks' `tools/bridge-lock --instance normal`.

Reads remain open. Changing commands, screenshots, start/stop and install/update require ownership. Locks do not expire automatically. Release waits for queued/deferred work, background loads/drives/time advancement and owner-started simulation to finish; pause the game first. An HTTP timeout does not release ownership. Status never exposes tokens. Lost owners require an explicit handoff, not automatic takeover. The source and offline Windows controller live in the sibling Food Trucks repository under `tools/bridge-locks`; `BA_BRIDGE_TOOLS` can override that repository's tools directory.

`start` reports the launched PID; allow the game to finish loading before expecting `health`. `health.load.status` tracks world creation/loading. A timed-out command may still be running: inspect its operation status before retrying. `stop --force` targets only the verified agent PID and can lose the agent's unsaved state; use normal stop first.

To reuse the existing CLI/MCP bridge clients, keep the following tunnel open and set their `BA_BRIDGE_URL` to `http://127.0.0.1:18330`:

```sh
python3 tools/agent-tests/agent tunnel
```

Default `background` mode uses Unity batch mode with graphics initialized, low graphics quality, a 960×540 surface, a 30 FPS cap, four job workers and below-normal process priority. It has no interactive game window. Physics and game time still run normally; rendering/culling remains available for compatibility. This is an agent-only player, not an alternative play/recording installation.

Both modes are muted at the Unity audio listener, at startup and every frame thereafter. Health reports the observed listener volume. Audio processing continues normally at zero output volume; the normal game's audio and Windows mixer settings are untouched. Headless mode also has an explicit player-loop cap because Unity's null graphics device can ignore `targetFrameRate`.

Background mode supports the bridge's `shoot` command: a temporary, independently positioned camera renders into an off-screen texture and writes a PNG under the agent profile's `FoodTrucksTest/shots/`. It does not need an active window and leaves the gameplay camera alone. The default capture has no screen-space UI. Example:

```sh
python3 tools/agent-tests/agent call shoot '{"name":"camera-check","x":215,"y":18,"z":-18,"look_at":{"x":215,"y":1,"z":3},"width":960,"height":540,"frames":16}'
```

`start --mode headless` additionally requests `-nographics`. It is experimental until the evidence below establishes compatibility. It cannot take screenshots. Headless success for a smoke test does not establish NPC, traffic, physics or visual parity. Do not use either mode for rendered-game performance comparisons without a controlled baseline.

## Build and install

Requires Python 3.13, .NET 10, SSH access to the game PC, and the installed game and Food Trucks bridge. The sibling `big-ambitions-food-trucks` checkout supplies the shared lock library and client helpers (`BA_BRIDGE_TOOLS` overrides its `tools` path). Steam must be running under the owning Windows user during installation/update to verify the content snapshot. `BA_GAME_HOST` selects the SSH alias (default `homepc`). Build uses UnityPy 1.24.2 and Mono.Cecil 0.11.6. Game references and generated files stay under ignored `build/agent-tests/`.

```sh
bash tools/agent-tests/build.sh
python3 -m unittest discover -s tools/agent-tests -p 'test_*.py'
export BA_LOCK_OWNER="agent-rig-install-session"
python3 tools/agent-tests/agent lock acquire --purpose "Install isolated test player"
python3 tools/agent-tests/install.py
```

The installer refuses an existing installation or profile instead of merging unknown state. The overlay records input/output hashes; installation checks that the game/bridge have not changed since preparation, and startup checks the installed overlay. Unity's original serialized objects other than PlayerSettings must remain byte-identical. The installer reserves only `http://127.0.0.1:18330/` for the current Windows user; no external firewall opening is made.

For a tooling correction on the same game build, stop the agent, rebuild with `bash tools/agent-tests/build.sh --reuse-inputs`, then run `python3 tools/agent-tests/install.py --update`. This uses the retained input snapshot, updates only the overlay, retains a backup of the previous revision and keeps test saves. It refuses a running agent, altered installed overlay, mismatched source inputs, or changed native/game version. Normal-game mod updates do not change the rig's pinned snapshot. Updating Food Trucks also requires keeping its private asset/data/dependency package in step with its DLL; the overlay update alone is not a complete mod-package deployment.

Game updates require rebuilding and reviewing the private copy against the new binaries. Do not drop newer normal-game binaries into it. Do not run the normal game's install/park scripts against the agent instance. Source files here are distinct from all Workshop packages.

## Evidence

Checked on 17 October 2026 with Big Ambitions build 3682 / Unity 2022.3.62f2 and the private Food Trucks 0.10.0 snapshot:

- **Checked:** normal game and isolated background player ran concurrently on ports 8330 and 18330. The normal game's PID and bridge session remained unchanged during the initial smoke tests. Subsequent independent normal-game mod updates were detected and did not replace the rig's snapshot.
- **Checked:** distinct product, executable, persistent data and cache paths reported from the running game. Save files were created only in the agent profile/cache. Graceful stop and restart addressed the isolated player.
- **Regression found and addressed:** the initial Steam-connected rig prevented starting the normal game through Steam while it ran. Runtime version 4 removes the player's Steam initialization and initializes local mods independently. Earlier concurrent-process tests alone did not cover this launch order.
- **Checked, runtime 4:** the running background player had no Steam client/overlay modules and no entry for its PID in Steam's game-process log. The normal game's PID stayed unchanged. The offline player loaded the day-2 test save, answered state queries and saved another checkpoint.
- **Checked, runtime 4 screenshots:** an independent off-screen camera produced a 960×540 PNG in background mode; the downloaded image was visually inspected and showed the city. Evidence: `build/agent-tests/agent-offscreen-camera-check.png`.
- **Checked, runtime 5 locks:** unauthorized commands and forged argument tokens were rejected; owner commands succeeded; reads stayed available. A running simulation and a screenshot still rendering after HTTP timeout prevented release until completion. Independent normal/agent ownership is backed by the same shared controller. Evidence: `build/agent-tests/lock-validation.json`.
- **Checked, mute:** background and headless players reported audio listener volume zero with the runtime's frame counter advancing alongside the bridge across scene loads. The headless player loop measured approximately 30 FPS after initialization.
- **Checked, background:** fresh world creation, money change, save, clock advance, checkpoint reload with restored money/time, and walking to a nearby destination.
- **Checked, headless (before the Steam-disconnection update):** log confirmed the null graphics device; checkpoint load, walking, saving, time advance across midnight and the Food Trucks 04:00 processing boundary, and reloading the resulting day-2 save succeeded.
- **Known headless limitation:** unsupported-shader and asynchronous GPU readback errors occur. The tested saves completed despite these errors; portrait/screenshot rendering is not supported.
- **Checked, runtime 5 parallel launch:** after Peter saved and closed the normal game, Steam launched it with the rig already running. Both bridges answered simultaneously on 8330/18330. The normal bridge rejected missing tokens and the agent token, accepted its own owner's quit, and releasing the normal lock left agent ownership intact. The rig retained ownership across update/restart; both locks were explicitly released after testing.
- **Pending:** representative NPC, traffic, driving and physics parity; a longer multi-day scenario; measured CPU/GPU savings against a controlled baseline. Headless remains experimental.

Local validation: runtime and patcher compile, 11 shared C# lock tests and four Python lock-client tests pass, four agent-client isolation/quoting tests pass, Python and shell syntax checks pass, and serialized player objects outside PlayerSettings remain byte-identical. These checks are separate from the gameplay evidence above. Detailed local logs and JSON responses are retained in ignored `build/agent-tests/`.

The installer ownership reader uses Valve's [ownership and installed-DLC checks](https://partner.steamgames.com/doc/api/ISteamApps), then [shuts down its Steam API session](https://partner.steamgames.com/doc/api/steam_api#SteamAPI_Shutdown) and exits. No Steam API session is created in the player itself.
