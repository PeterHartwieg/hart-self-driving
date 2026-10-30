# Moved: the agent test rig

The isolated agent test rig (Windows `agent1` player, Mac `agentmac` fallback, runtime overlay, installers and the rig virtual mouse) now lives in its own repository, shared by all Big Ambitions mods: [PeterHartwieg/big-ambitions-agent-rig](https://github.com/PeterHartwieg/big-ambitions-agent-rig). Its history up to `1728ac3` was carried over with `git subtree split`.

Clone it beside this checkout (`../big-ambitions-agent-rig`), or set `BA_AGENT_RIG` to a checkout. Old commands map like this:

| Before | Now (in the rig checkout) |
|---|---|
| `python3 tools/agent-tests/agent …` | `python3 scripts/agent …` |
| `python3 tools/agent-tests/mac-agent …` | `python3 scripts/mac-agent …` |
| `bash tools/agent-tests/build.sh` | `bash scripts/build.sh` |
| `python3 tools/agent-tests/install.py` | `python3 scripts/install.py` |
| `tools/agent-tests/README.md`, `MAC.md` | `README.md`, `docs/windows-rig.md`, `docs/MAC.md` |

The Driven Rides parking fixture stays here: `tools/parking-cases.py` (with `tools/Send-RideCommand.ps1`) drives the rig through `$BA_AGENT_RIG/scripts/agent`. The retained build inputs in `build/agent-tests/` can be copied into the rig checkout's `build/` before its first `--reuse-inputs` build (see its README).
