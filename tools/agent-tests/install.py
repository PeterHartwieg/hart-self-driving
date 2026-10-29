"""Install one private copy on Windows; never writes to the normal game or its profile."""
import base64
import gzip
import importlib.machinery
import importlib.util
import pathlib
import subprocess
import argparse

here = pathlib.Path(__file__).resolve().parent
loader = importlib.machinery.SourceFileLoader('agent_client', str(here/'agent'))
spec = importlib.util.spec_from_loader(loader.name, loader)
agent = importlib.util.module_from_spec(spec)
loader.exec_module(agent)
overlay = here.parents[1]/'build/agent-tests/output'

if __name__ == '__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--update', action='store_true', help='Update only the verified, stopped agent copy; keep its saves')
    opts=parser.parse_args()
    names=['BridgeControl.dll','AgentTestRuntime.dll','AgentRigInput.dll','HGPlugins.dll','BigAmbitions.dll','BigAmbitions.ModsInternal.dll','FoodTrucks.dll',
           'globalgamemanagers','patch-manifest.json','player-manifest.json','rig-input-manifest.json']
    for name in names:
        if not (overlay/name).is_file():
            raise SystemExit('Missing build artifact: '+name+'; run build.sh first')
    script = (here/'Agent-Windows.ps1').read_bytes()
    packed = base64.b64encode(gzip.compress(script)).decode()
    # A short script payload avoids SSH stdin and shell quoting issues on Windows.
    print(agent.remote(r'''
$ErrorActionPreference='Stop'
$stage=Join-Path $env:USERPROFILE 'AgentTests-Staging'
New-Item -ItemType Directory -Force (Join-Path $stage 'overlay') | Out-Null
$bytes=[Convert]::FromBase64String('PAYLOAD')
$memory=New-Object IO.MemoryStream(,$bytes)
$gzip=New-Object IO.Compression.GZipStream($memory,[IO.Compression.CompressionMode]::Decompress)
$target=[IO.File]::Create((Join-Path $stage 'Agent-Windows.ps1'))
try {$gzip.CopyTo($target)} finally {$target.Dispose();$gzip.Dispose();$memory.Dispose()}
'Installer staged'
'''.replace('PAYLOAD', packed)))
    for name in names:
        subprocess.run(['scp','-q',str(overlay/name),agent.HOST+':AgentTests-Staging/overlay/'+name],check=True,timeout=60)
    subprocess.run(['scp','-q',str(here/'Read-SteamEntitlements.ps1'),agent.HOST+':AgentTests-Staging/Read-SteamEntitlements.ps1'],check=True,timeout=60)
    action='update' if opts.update else 'install'
    agent.locks.provision()
    print(agent.locks.invoke('AgentTests-Staging/Agent-Windows.ps1', {'Action':action}, 'agent1', timeout=600))
