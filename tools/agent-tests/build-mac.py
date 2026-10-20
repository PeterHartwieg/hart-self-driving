"""Build the private Mac overlay from the installed Mac player. No PC or network access."""
import os
from pathlib import Path
import shutil
import subprocess
import sys
from mac_common import HERE,REPO,OUT,BRIDGE_TOOLS,PRODUCT,SOURCE

def run(args):subprocess.run([str(x) for x in args],check=True)
if __name__=='__main__':
    if sys.platform!='darwin':raise SystemExit('Mac builder requires macOS')
    managed=SOURCE/'Contents/Resources/Data/Managed'
    if not managed.is_dir():raise SystemExit('Install Big Ambitions for Mac in Steam first')
    ft=BRIDGE_TOOLS.parent/'build/FoodTrucks'
    if not (ft/'FoodTrucks.dll').is_file():raise SystemExit('Build the Food Trucks package first')
    OUT.mkdir(parents=True,exist_ok=True)
    inputs=OUT/'input';output=OUT/'overlay';inputs.mkdir(exist_ok=True);output.mkdir(exist_ok=True)
    for file in managed.glob('*.dll'):
        target=inputs/file.name
        if target.is_symlink():target.unlink()
        elif target.exists():raise SystemExit('Unexpected regular input file: '+str(target))
        target.symlink_to(file)
    shutil.copy2(ft/'FoodTrucks.dll',inputs/'FoodTrucks.dll')
    props=['-c','Release','--nologo','-p:BridgeControlProject='+str(BRIDGE_TOOLS/'bridge-locks/BridgeControl.csproj')]
    run(['dotnet','build',HERE/'LocalControl/LocalControl.csproj',*props,'-o',OUT/'control'])
    run(['dotnet','build',HERE/'AgentRuntime/AgentRuntime.csproj',*props,'-p:AgentMac=true','-p:GameManaged='+str(managed),'-o',OUT/'runtime'])
    run(['dotnet','run','--project',HERE/'Patcher/Patcher.csproj','-c','Release','--',inputs,output,OUT/'runtime/AgentTestRuntime.dll','28330'])
    shutil.copy2(OUT/'control/BridgeControl.dll',output/'BridgeControl.dll')
    python=REPO/'build/agent-tests/venv/bin/python'
    if not python.exists():raise SystemExit('Create the agent build venv with UnityPy 1.24.2 first')
    run([python,HERE/'patch-player.py',SOURCE/'Contents/Resources/Data/globalgamemanagers',output/'globalgamemanagers',PRODUCT])
    print('Mac overlay built; acquire agentmac ownership before install-mac.py')
