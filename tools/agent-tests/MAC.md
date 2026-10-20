# Local Mac fallback for agent tests

This is the third instance, `agentmac`, running the licensed native Apple Silicon game locally. It is an on-demand backup when the Windows PC is off. It never contacts or starts that PC. There is no nightly schedule, automatic failover, or automatic save transfer.

| Instance | Endpoint | Launcher |
| --- | --- | --- |
| Normal Windows game | `http://127.0.0.1:8330` through its tunnel | Normal game tools |
| Windows test rig, `agent1` | `http://127.0.0.1:18330` through its tunnel | `tools/agent-tests/agent` |
| Local Mac test rig, `agentmac` | `http://127.0.0.1:28330`, no tunnel | `tools/agent-tests/mac-agent` |

## Agent workflow

Run in the Driven Rides checkout. Use a unique owner name and retain it throughout the test. Read status before acquiring; never take over another agent's reservation automatically.

```sh
export BA_LOCK_OWNER="my-project-my-session"
python3 tools/agent-tests/mac-agent status
python3 tools/agent-tests/mac-agent lock acquire --purpose "Local fallback test"
python3 tools/agent-tests/mac-agent start
python3 tools/agent-tests/mac-agent health
python3 tools/agent-tests/mac-agent call load '{"name":"FT Mac Agent Checkpoint"}'
# Poll health until load.status is done. Create a separate world if appropriate:
# python3 tools/agent-tests/mac-agent call new_game '{"name":"FT My Mac Test"}'
python3 tools/agent-tests/mac-agent call state
python3 tools/agent-tests/mac-agent call time '{"pause":true}'
python3 tools/agent-tests/mac-agent call save '{"name":"FT My Mac Checkpoint"}'
python3 tools/agent-tests/mac-agent stop
python3 tools/agent-tests/mac-agent lock release
```

The same bridge commands, FT-only save protection and ownership protocol apply. The Mac has its own durable lock under `~/.big-ambitions-bridge/agentmac`. Changing commands, screenshots and lifecycle operations require its token; reads remain open. A lock does not expire automatically. Pending work, loading and running simulation prevent release. The offline .NET controller and in-game Mono runtime coordinate through the same OS file lock. Tokens remain local and never appear in process arguments.

Existing Food Trucks CLI/MCP clients can use `BA_BRIDGE_URL=http://127.0.0.1:28330` and the same `BA_LOCK_OWNER`. Configure that environment before starting the MCP server. There is no SSH tunnel and local screenshots need no PC fetch. Windows launch/install/park commands are not Mac rig commands.

`start` waits up to five minutes for bridge readiness. If it times out, the game may still be initializing: inspect `status` and `log` before retrying. A fresh shader cache can take several minutes. `stop --force` is available to the owner when startup or shutdown is stuck; it verifies PID, start time and executable, and can discard unsaved test changes. Normal stop is preferred.

## Graphics and resource use

Default `headless` mode uses Unity's null graphics device for text-based tests. Both modes are muted continuously, capped at 30 FPS, use two job workers and lower process priority. To take screenshots, start with `start --mode background` instead; this initializes Metal graphics without an interactive window. Independent camera screenshots work in that mode:

```sh
python3 tools/agent-tests/mac-agent call shoot '{"name":"mac-camera","x":215,"y":18,"z":-18,"look_at":{"x":215,"y":1,"z":3},"width":960,"height":540,"frames":16,"timeout_s":60}'
```

Headless mode cannot take screenshots; stop and restart in background mode when a test needs them. Headless logs contain unsupported-shader errors because there is no graphics device. The smoke test passed despite them. Broader NPC, traffic, physics and rendering parity remain unverified.

This remains a full city simulation. On this 16 GB M1 Pro, the loaded headless city measured about **3.6 GB physical footprint**, versus **8.8 GB** in background mode (9.1 GB peak). These are single smoke-test observations, not guaranteed budgets. Startup refuses less than 12 GiB free disk or 20% reported free memory. These are headroom checks, not a memory budget. Close other heavy workloads when testing. `resources` reports current headroom.

The Mac must be awake and logged in. A per-process `caffeinate` assertion prevents idle sleep only while this rig runs; closing the lid, shutting down or manual sleep can still interrupt work. System sleep settings are unchanged. Stop after testing; no game process is retained between tests.

## Installation and maintenance

- Private app: `~/Library/Application Support/BigAmbitionsAgentTestsMac/Big Ambitions Agent Tests Mac.app`
- Separate profile, local mods and saves: `~/Library/Application Support/com.hovgaardgames.bigambitions.agenttests.mac`
- Runtime log and installation/process receipts: `~/Library/Application Support/BigAmbitionsAgentTestsMac/`
- Local generated build and evidence: `build/agent-tests-mac/` (ignored).

The original Steam app and normal Mac profile are untouched. APFS clone copies share unchanged disk blocks, not writable files. The private app has its own product and bundle identity and is ad-hoc signed. Steam ownership and installed DLC are verified once during installation; the player itself never initializes Steam, writes achievements or discovers Workshop mods. Starting the rig does not require Steam or the PC.

Build requirements: installed Mac game, .NET 10, Python, UnityPy 1.24.2 in `build/agent-tests/venv`, and the sibling Food Trucks checkout with a built Mac-compatible mod package and shared bridge-lock tooling. `BA_BRIDGE_TOOLS` overrides its tools location. For a fresh installation:

```sh
python3 tools/agent-tests/build-mac.py
export BA_LOCK_OWNER="mac-install-session"
python3 tools/agent-tests/mac-agent lock acquire --purpose "Install local fallback"
# Steam must be running under the owning Mac account for this one-time check.
python3 tools/agent-tests/install-mac.py
python3 tools/agent-tests/mac-agent lock release
```

The installed Food Trucks and Driven Rides mods are snapshots, not live repository links. Startup validates their hashes. The installer deliberately refuses an existing app/profile; an in-place update workflow is not implemented. To test newer code, prepare a reviewed update of the private mod package and installation manifest while the rig is stopped and owned. Do not run a normal-game deploy script against it, delete its profile blindly, or claim an uninstalled change was tested. A Mac pass does not establish Windows-specific compatibility.

## Evidence

17 October 2026, Big Ambitions build 3682, runtime 6:

- **Checked:** native ARM64 process, separate product/profile/cache and port 28330, muted audio, offline Steam runtime, bridge identity and FT world creation.
- **Checked:** missing/wrong ownership tokens rejected, owner commands accepted, reads open, offline release refused during in-game loading.
- **Checked:** clock advance to 08:05, save checkpoint, advance again and reload restoring 08:05, independent 960×540 camera screenshot visually showing the city.
- **Checked:** graceful game exit and restart into headless mode with a new bridge session and the same ownership reservation.
- **Checked, headless:** loaded the background checkpoint at 08:05, advanced to 08:10 and saved a new checkpoint. Screenshot requests were explicitly refused. Physical footprint was 3.6 GB in the loaded city.
- **Pending:** long scenarios, representative driving/traffic/NPC parity, and controlled CPU/GPU savings measurements.

Evidence files are local under `build/agent-tests-mac/`. Nine Python client/isolation tests and the shared lock tests cover tooling separately from gameplay. Both Mac and Windows runtime configurations compile; this Mac work did not deploy or change the PC.
