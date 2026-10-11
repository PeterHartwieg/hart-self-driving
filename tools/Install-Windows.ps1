# Installs a staged package folder into the game's ModsLocal folder and deletes the staged copy.
# The copy is checked against the build's SHA-256 list before it replaces anything. The previous
# package goes to DrivenRidesPackageBackups and comes back if the swap fails. Nothing is staged inside
# ModsLocal, because the game loads every folder there and rejects two copies of the same mod.
# Run by tools/install-remote.sh over ssh, or by hand on the game PC.
param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$Manifest,
    [string]$ModsLocal = (Join-Path $env:USERPROFILE 'AppData\LocalLow\Hovgaard Games\Big Ambitions\ModsLocal')
)
$ErrorActionPreference = 'Stop'
$Source = (Resolve-Path $Source).Path
$Manifest = (Resolve-Path $Manifest).Path
if (Get-Process 'Big Ambitions' -ErrorAction SilentlyContinue) { throw 'Big Ambitions is running. Close the game before installing.' }

$gameRoot = Split-Path $ModsLocal -Parent
$target = Join-Path $ModsLocal 'DrivenRides'
$incoming = Join-Path $gameRoot 'DrivenRides.incoming'
$backup = $null
try {
    if (Test-Path $incoming) { Remove-Item $incoming -Recurse -Force }
    Copy-Item $Source $incoming -Recurse

    $expected = Get-Content $Manifest | Where-Object { $_.Trim() } | ForEach-Object {
        $hash, $path = $_ -split '\s+', 2
        [pscustomobject]@{ Hash = $hash.ToLower(); Path = $path.Trim().TrimStart('.', '/').Replace('/', '\') }
    }
    if (-not ($expected | Where-Object Path -eq 'DrivenRides.dll')) { throw 'Manifest does not list DrivenRides.dll.' }
    foreach ($entry in $expected) {
        $file = Join-Path $incoming $entry.Path
        if (!(Test-Path $file)) { throw "Package is missing $($entry.Path)." }
        if ((Get-FileHash $file -Algorithm SHA256).Hash.ToLower() -ne $entry.Hash) { throw "Hash mismatch for $($entry.Path)." }
    }

    New-Item -ItemType Directory -Force $ModsLocal | Out-Null
    if (Test-Path $target) {
        $backups = Join-Path $gameRoot 'DrivenRidesPackageBackups'
        New-Item -ItemType Directory -Force $backups | Out-Null
        $backup = Join-Path $backups ('DrivenRides-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
        Move-Item $target $backup
    }
    try {
        Move-Item $incoming $target
    } catch {
        if ($backup -and !(Test-Path $target)) { Move-Item $backup $target }
        throw
    }
} finally {
    if (Test-Path $incoming) { Remove-Item $incoming -Recurse -Force }
}
Remove-Item $Source -Recurse -Force
Remove-Item $Manifest -Force
"Installed and verified $($expected.Count) files in $target"
