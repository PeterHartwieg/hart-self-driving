# Moves the installed mod out of ModsLocal (park) or back (restore), for uninstall tests.
# Local mods cannot be switched off in the game's Mods menu, so removing the folder is the only way.
param([Parameter(Mandatory = $true)][ValidateSet('park', 'restore')][string]$Action)
$ErrorActionPreference = 'Stop'
if (Get-Process 'Big Ambitions' -ErrorAction SilentlyContinue) { throw 'Big Ambitions is running. Close the game first.' }
$root = Join-Path $env:USERPROFILE 'AppData\LocalLow\Hovgaard Games\Big Ambitions'
$installed = Join-Path $root 'ModsLocal\DrivenRides'
$parked = Join-Path $root 'DrivenRidesParked\DrivenRides'
if ($Action -eq 'park') {
    if (!(Test-Path $installed)) { throw 'Driven Rides is not installed in ModsLocal.' }
    if (Test-Path $parked) { throw "$parked already exists." }
    New-Item -ItemType Directory -Force (Split-Path $parked -Parent) | Out-Null
    Move-Item $installed $parked
    'Driven Rides parked: the game will start without it.'
} else {
    if (!(Test-Path $parked)) { throw 'No parked Driven Rides package.' }
    if (Test-Path $installed) { throw "$installed already exists." }
    Move-Item $parked $installed
    'Driven Rides restored to ModsLocal.'
}
