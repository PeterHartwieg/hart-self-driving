"""The rig virtual mouse addon (Food Trucks tools/rig-input) is built, hashed, installed and loaded only through the
isolated rig's own paths. Static checks: the live behaviour is a rig run (docs/test-bridge.md, "Rig virtual mouse")."""
import pathlib
import re
import unittest

HERE = pathlib.Path(__file__).resolve().parent
ADDON = 'AgentRigInput.dll'


def read(name):
    return (HERE / name).read_text(encoding='utf-8')


class RigInputWiringTests(unittest.TestCase):
    def test_builds_compile_the_addon_from_the_shared_tools_against_the_game_inputs(self):
        build = read('build.sh')
        self.assertIn('tools="${BA_BRIDGE_TOOLS:-$repo/../big-ambitions-food-trucks/tools}"', build)
        self.assertIn('python3 "$tools/rig-input/build.py" --game-managed "$out/input" --output "$out/rig-input"', build)
        self.assertIn('rig-input-manifest.json', build)
        mac = read('build-mac.py')
        self.assertIn("BRIDGE_TOOLS/'rig-input/build.py','--game-managed',managed", mac)

    def test_installers_stage_verify_copy_and_hash_the_addon(self):
        self.assertIn(f"'{ADDON}'", read('install.py'))
        self.assertIn("'rig-input-manifest.json'", read('install.py'))
        windows = read('Agent-Windows.ps1')
        install = windows[windows.index(" 'install' {"):windows.index(" 'start' {")]
        update = windows[windows.index(" 'update' {"):windows.index(" 'install' {")]
        for block in (install, update):
            self.assertIn("Check-Hash (Join-Path $overlay 'AgentRigInput.dll') $rigManifest.sha256", block)
        # Install copies it into Managed through the hashed file list; update adds it to an older receipt.
        files = re.search(r"foreach \(\$name in @\(([^)]*)\)\) \{\s*\$dest=", install).group(1)
        self.assertIn(f"'{ADDON}'", files)
        self.assertIn("$rigInput=Join-Path $data 'Managed\\AgentRigInput.dll'", update)
        self.assertIn('$install.files+=@{path=$rigInput;sha256=$rigManifest.sha256}', update)
        # Start checks every receipt file's hash, so a changed or missing addon refuses to launch.
        self.assertIn('Get-FileHash -Algorithm SHA256 -LiteralPath $file.path', windows[:windows.index('function Get-Agent')])
        mac = read('install-mac.py')
        self.assertGreaterEqual(mac.count(f"'{ADDON}'"), 2)
        self.assertIn("rig-input-manifest.json", mac)

    def test_runtime_loads_it_once_after_the_pump_with_a_receipt_hash_and_reports_it(self):
        host = read('AgentRuntime/Host.cs')
        rig = read('AgentRuntime/RigInput.cs')
        tick = host[host.index('public static void Tick()'):host.index('public static JObject Health')]
        self.assertIn('RigInput.Tick(executable);', tick)
        self.assertIn('result["rig_mouse"] = mouse;', host)
        self.assertIn('["rig_input"] = RigInput.Describe()', host)
        self.assertIn('["runtime_version"] = 7', host)
        self.assertIn('if (tried) { tick?.Invoke(); return; }', rig)
        self.assertIn('CheckReceipt(executable, path, sha)', rig)
        self.assertIn('"agent-install.json"', rig)
        self.assertIn('"install.json"', rig)
        self.assertIn('Host.Product, Host.Port', rig)
        # Reflection only: the runtime must build without the addon.
        self.assertNotIn('AgentRigInput', read('AgentRuntime/AgentRuntime.csproj'))


if __name__ == '__main__':
    unittest.main()
