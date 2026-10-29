"""Install a separate APFS-cloned Mac test player. Never modifies the Steam app or normal profile."""
import hashlib
import json
import plistlib
import shutil
import subprocess
import sys
from mac_common import *

def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
if __name__=='__main__':
    if sys.platform!='darwin':raise SystemExit('This installer requires macOS')
    plain(ROOT);plain(PROFILE)
    if ROOT.exists() or PROFILE.exists():raise SystemExit('Mac agent installation/profile already exists; refusing to overwrite it')
    overlay=OUT/'overlay'
    manifest=json.loads((overlay/'patch-manifest.json').read_text())
    if sha(overlay/'AgentRigInput.dll')!=json.loads((overlay/'rig-input-manifest.json').read_text())['sha256']:raise SystemExit('AgentRigInput.dll does not match its build manifest')
    ft=BRIDGE_TOOLS.parent/'build/FoodTrucks';dr=REPO/'build/DrivenRides'
    for row in manifest:
        source=ft/row['file'] if row['file']=='FoodTrucks.dll' else SOURCE/'Contents/Resources/Data/Managed'/row['file']
        if sha(source)!=row['source_sha256'] or sha(overlay/row['file'])!=row['patched_sha256']:raise SystemExit('Build input/output changed: '+str(source))
    with lifecycle():
        subprocess.run([sys.executable,str(HERE/'read-steam-mac.py'),str(SOURCE/'Contents/PlugIns/libsteam_api.dylib'),str(OUT/'steam-entitlements.json')],check=True,timeout=30,stdout=subprocess.DEVNULL)
        ROOT.mkdir(parents=True)
        # clonefile copies share only unmodified APFS blocks; writes are isolated, unlike hard links.
        subprocess.run(['/bin/cp','-cR',str(SOURCE),str(APP)],check=True)
        contents=APP/'Contents';data=contents/'Resources/Data'
        (contents/'MacOS/Big Ambitions').rename(EXE)
        info=plistlib.loads((contents/'Info.plist').read_bytes())
        info.update(CFBundleExecutable=PRODUCT,CFBundleName=PRODUCT,CFBundleIdentifier=BUNDLE,LSUIElement=True,NSAppSleepDisabled=True)
        (contents/'Info.plist').write_bytes(plistlib.dumps(info))
        shutil.copy2(overlay/'globalgamemanagers',data/'globalgamemanagers')
        if (data/'app.info').exists():(data/'app.info').write_text('Hovgaard Games\n'+PRODUCT+'\n')
        for name in ('HGPlugins.dll','BigAmbitions.dll','BigAmbitions.ModsInternal.dll','AgentTestRuntime.dll','BridgeControl.dll','AgentRigInput.dll'):
            shutil.copy2(overlay/name,data/'Managed'/name)
        shutil.copy2(OUT/'steam-entitlements.json',EXE.parent/'steam-entitlements.json')
        mods=PROFILE/'ModsLocal';mods.mkdir(parents=True)
        shutil.copytree(ft,mods/'FoodTrucks')
        shutil.copy2(overlay/'FoodTrucks.dll',mods/'FoodTrucks/FoodTrucks.dll')
        shutil.copy2(overlay/'BridgeControl.dll',mods/'FoodTrucks/Dependencies/BridgeControl.dll')
        if (dr/'DrivenRides.dll').is_file():shutil.copytree(dr,mods/'DrivenRides')
        subprocess.run(['/usr/bin/codesign','--force','--deep','--sign','-',str(APP)],check=True)
        critical=[EXE,contents/'Info.plist',data/'globalgamemanagers',EXE.parent/'steam-entitlements.json']
        critical.extend(data/'Managed'/n for n in ('HGPlugins.dll','BigAmbitions.dll','BigAmbitions.ModsInternal.dll','AgentTestRuntime.dll','BridgeControl.dll','AgentRigInput.dll'))
        critical.extend(p for p in mods.rglob('*') if p.is_file())
        (ROOT/'install.json').write_text(json.dumps({'instance':'agentmac','app':str(APP),'profile':str(PROFILE),'source':str(SOURCE),'files':{str(p):sha(p) for p in critical}},indent=2))
        print('Installed isolated Mac rig: '+str(APP))
