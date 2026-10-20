param([Parameter(Mandatory=$true)][string]$OutputPath)
$ErrorActionPreference='Stop'
# This short-lived installer helper is the only process that connects to Steam.
# The test player never initializes Steam or claims to own unverified DLC.
$library='C:\Program Files (x86)\Steam\steamapps\common\Big Ambitions\Big Ambitions_Data\Plugins\x86_64\steam_api64.dll'
$env:SteamAppId='1331550'
$env:SteamGameId='1331550'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class AgentSteamReader {
    [DllImport("kernel32", CharSet=CharSet.Unicode, SetLastError=true)]
    public static extern IntPtr LoadLibrary(string path);
    [DllImport("steam_api64", CallingConvention=CallingConvention.Cdecl)]
    private static extern int SteamInternal_SteamAPI_Init(IntPtr versions, IntPtr error);
    public static void Initialize() {
        IntPtr versions=Marshal.StringToHGlobalAnsi("STEAMAPPS_INTERFACE_VERSION008\0\0");
        IntPtr error=Marshal.AllocHGlobal(1024);
        try {
            Marshal.WriteByte(error,0);
            int result=SteamInternal_SteamAPI_Init(versions,error);
            if (result!=0) throw new InvalidOperationException("Steam initialization: "+Marshal.PtrToStringAnsi(error));
        } finally { Marshal.FreeHGlobal(versions); Marshal.FreeHGlobal(error); }
    }
    [DllImport("steam_api64", CallingConvention=CallingConvention.Cdecl)]
    public static extern void SteamAPI_Shutdown();
    [DllImport("steam_api64", CallingConvention=CallingConvention.Cdecl)]
    public static extern IntPtr SteamAPI_SteamApps_v008();
    [DllImport("steam_api64", CallingConvention=CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] public static extern bool SteamAPI_ISteamApps_BIsSubscribed(IntPtr apps);
    [DllImport("steam_api64", CallingConvention=CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] public static extern bool SteamAPI_ISteamApps_BIsDlcInstalled(IntPtr apps, uint appId);
}
'@
if ([AgentSteamReader]::LoadLibrary($library) -eq [IntPtr]::Zero) { throw 'Cannot load the installed Steam library' }
[AgentSteamReader]::Initialize()
try {
    $apps=[AgentSteamReader]::SteamAPI_SteamApps_v008()
    if ($apps -eq [IntPtr]::Zero -or ![AgentSteamReader]::SteamAPI_ISteamApps_BIsSubscribed($apps)) { throw 'Steam did not confirm ownership of Big Ambitions' }
    $dlc=@()
    foreach ($id in @(2254190,2254191)) {
        if ([AgentSteamReader]::SteamAPI_ISteamApps_BIsDlcInstalled($apps,$id)) { $dlc+=$id }
    }
    @{app_id=1331550;base_owned=$true;dlc=@($dlc);verified_utc=[DateTime]::UtcNow.ToString('o')} | ConvertTo-Json | Set-Content -Encoding UTF8 $OutputPath
} finally { [AgentSteamReader]::SteamAPI_Shutdown() }
