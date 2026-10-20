"""Local-only paths, ownership and identity checks for the Mac fallback rig."""
import contextlib
import json
import os
from pathlib import Path
import subprocess
import sys

HERE=Path(__file__).resolve().parent
REPO=HERE.parents[1]
OUT=REPO/'build/agent-tests-mac'
BRIDGE_TOOLS=Path(os.environ.get('BA_BRIDGE_TOOLS',REPO.parent/'big-ambitions-food-trucks/tools'))
sys.path.insert(0,str(BRIDGE_TOOLS))
from ftlib import locks
PRODUCT='Big Ambitions Agent Tests Mac'
BUNDLE='com.hovgaardgames.bigambitions.agenttests.mac'
ROOT=Path.home()/'Library/Application Support/BigAmbitionsAgentTestsMac'
APP=ROOT/(PRODUCT+'.app')
EXE=APP/'Contents/MacOS'/PRODUCT
PROFILE=Path.home()/'Library/Application Support'/BUNDLE
SOURCE=Path.home()/'Library/Application Support/Steam/steamapps/common/Big Ambitions/Big Ambitions.app'
CONTROL=OUT/'control/LocalControl.dll'
URL='http://127.0.0.1:28330'

def credential(create=False):return locks.token('agentmac',create=create)
def control(action,**fields):
    request={'action':action,'token':credential(create=action=='acquire'),'owner':os.environ.get('BA_LOCK_OWNER'),**fields}
    result=subprocess.run(['dotnet',str(CONTROL)],input=json.dumps(request)+'\n',capture_output=True,text=True,timeout=15)
    if result.returncode:raise RuntimeError(result.stderr.strip())
    return json.loads(result.stdout)

@contextlib.contextmanager
def lifecycle(kind='lifecycle'):
    p=subprocess.Popen(['dotnet',str(CONTROL)],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
    try:
        p.stdin.write(json.dumps({'action':'hold','kind':kind,'token':credential()})+'\n');p.stdin.flush()
        if p.stdout.readline().strip()!='{"ready":true}':raise RuntimeError(p.stderr.read().strip() or 'Could not reserve the rig')
        yield
    finally:
        active_error=sys.exc_info()[0] is not None
        if p.poll() is None:
            try:p.stdin.write('\n');p.stdin.flush()
            except BrokenPipeError:pass
        _,error=p.communicate(timeout=15)
        if p.returncode and not active_error:raise RuntimeError(error.strip() or 'Local ownership controller failed')

def plain(path):
    for part in (path,*path.parents):
        if part.is_symlink():raise RuntimeError('Symlinked install path refused: '+str(part))

def identity(health):
    i=health.get('agent_instance',{})
    if not (i.get('id')=='agentmac' and i.get('port')==28330 and i.get('product')==PRODUCT and i.get('executable')==str(EXE) and i.get('data_path')==str(PROFILE) and i.get('test_only') is True):
        raise RuntimeError('Bridge is not the isolated local Mac rig')
