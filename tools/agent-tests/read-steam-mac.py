"""Verify ownership in a short-lived installer process; the test player itself stays offline."""
import ctypes as c
import datetime
import json
import os
from pathlib import Path
import sys

os.environ['SteamAppId']='1331550'
os.environ['SteamGameId']='1331550'
lib=c.CDLL(str(Path(sys.argv[1])))
lib.SteamInternal_SteamAPI_Init.argtypes=[c.c_char_p,c.c_void_p]
lib.SteamInternal_SteamAPI_Init.restype=c.c_int
error=c.create_string_buffer(1024)
result=lib.SteamInternal_SteamAPI_Init(b'STEAMAPPS_INTERFACE_VERSION008\0\0',error)
if result: raise SystemExit('Steam could not verify ownership: '+error.value.decode(errors='replace'))
try:
    lib.SteamAPI_SteamApps_v008.restype=c.c_void_p
    apps=lib.SteamAPI_SteamApps_v008()
    lib.SteamAPI_ISteamApps_BIsSubscribed.argtypes=[c.c_void_p]
    lib.SteamAPI_ISteamApps_BIsSubscribed.restype=c.c_bool
    if not apps or not lib.SteamAPI_ISteamApps_BIsSubscribed(apps): raise SystemExit('Steam did not confirm base-game ownership')
    lib.SteamAPI_ISteamApps_BIsDlcInstalled.argtypes=[c.c_void_p,c.c_uint32]
    lib.SteamAPI_ISteamApps_BIsDlcInstalled.restype=c.c_bool
    dlc=[i for i in (2254190,2254191) if lib.SteamAPI_ISteamApps_BIsDlcInstalled(apps,i)]
    Path(sys.argv[2]).write_text(json.dumps({'app_id':1331550,'base_owned':True,'dlc':dlc,'verified_utc':datetime.datetime.now(datetime.timezone.utc).isoformat()},indent=2))
    print('Steam ownership verified; helper exiting')
finally:lib.SteamAPI_Shutdown()
