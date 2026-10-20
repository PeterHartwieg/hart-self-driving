param(
    [Parameter(Mandatory=$true)][ValidateSet('install','update','start','launch','status','stop','log')][string]$Action,
    [ValidateSet('background','headless','windowed')][string]$Mode='background',
    [string]$RequestPath,
    [switch]$Force
)
$ErrorActionPreference='Stop'
$root='C:\BigAmbitions-AgentTests'
$source='C:\Program Files (x86)\Steam\steamapps\common\Big Ambitions'
$product='Big Ambitions Agent Tests'
$data=Join-Path $root ($product+'_Data')
$profile=Join-Path $env:USERPROFILE ('AppData\LocalLow\Hovgaard Games\'+$product)
$originalProfile=Join-Path $env:USERPROFILE 'AppData\LocalLow\Hovgaard Games\Big Ambitions'
$exe=Join-Path $root ($product+'.exe')
$receiptPath=Join-Path $root 'run.json'
$markerPath=Join-Path $root 'agent-install.json'
$staging=Join-Path $env:USERPROFILE 'AgentTests-Staging'

function Assert-PlainPath([string]$path) {
    $part=$path
    while ($part) {
        if ((Test-Path -LiteralPath $part) -and ((Get-Item -Force -LiteralPath $part).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Reparse point refused: $part" }
        $parent=Split-Path $part -Parent
        if ($parent -eq $part) { break }; $part=$parent
    }
}
function Assert-Installation {
    Assert-PlainPath $root; Assert-PlainPath $profile
    if (!(Test-Path $markerPath)) { throw 'Agent test installation is missing' }
    $install=Get-Content -Raw $markerPath | ConvertFrom-Json
    if ($install.product -ne $product -or $install.root -ne $root -or $install.status -ne 'ready') { throw 'Agent test installation is not ready' }
    foreach ($file in $install.files) {
        if ((Get-FileHash -Algorithm SHA256 -LiteralPath $file.path).Hash.ToLowerInvariant() -ne $file.sha256) { throw "Test artifact changed: $($file.path). Rebuild before launching." }
    }
}
function Get-Agent {
    if (!(Test-Path $receiptPath)) { return $null }
    $r=Get-Content -Raw $receiptPath | ConvertFrom-Json
    $p=Get-Process -Id $r.pid -ErrorAction SilentlyContinue
    if (!$p) { return $null }
    if ($p.Path -ne $exe -or $p.StartTime.ToUniversalTime().Ticks.ToString() -ne $r.started_ticks) { throw 'PID identity mismatch; refusing to control this process' }
    return $p
}
function Assert-NoAgent {
    if (Get-Process -Name $product -ErrorAction SilentlyContinue) { throw 'An agent test player is already running. Stop it with this launcher first.' }
}
function Copy-Tree([string]$from,[string]$to) {
    if (!(Test-Path -LiteralPath $from -PathType Container)) { throw "Missing source $from" }
    Assert-PlainPath $from; Assert-PlainPath $to
    $null=robocopy $from $to /E /COPY:DAT /DCOPY:DAT /R:1 /W:1 /NFL /NDL /NJH /NJS /NP /XJ
    if ($LASTEXITCODE -ge 8) { throw "Copy failed ($LASTEXITCODE): $from" }
}
function Check-Hash([string]$path,[string]$hash) {
    if ((Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant() -ne $hash) { throw "Source changed since patch preparation: $path" }
}
function Read-Entitlements([string]$output) {
    # Steam IPC is scoped to the interactive Windows session, not the SSH session.
    $task='BigAmbitionsAgentEntitlements'
    if (Test-Path $output) { Remove-Item -LiteralPath $output }
    $user=[Security.Principal.WindowsIdentity]::GetCurrent().Name
    $helper=Join-Path $staging 'Read-SteamEntitlements.ps1'
    $launch=New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$helper`" -OutputPath `"$output`""
    $principal=New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
    Register-ScheduledTask -TaskName $task -Action $launch -Principal $principal -Force | Out-Null
    try {
        $started=(Get-Date).AddSeconds(-2)
        Start-ScheduledTask $task
        $deadline=(Get-Date).AddSeconds(30)
        do {
            Start-Sleep -Milliseconds 300
            $info=Get-ScheduledTaskInfo $task
            $running=(Get-ScheduledTask $task).State -eq 'Running'
            $finished=$info.LastRunTime -ge $started -and !$running
        } while (!$finished -and (Get-Date) -lt $deadline)
        if (!$finished -or $info.LastTaskResult -ne 0 -or !(Test-Path $output)) {
            throw 'Steam ownership snapshot failed. Steam must be running in the logged-in desktop session; the private installation was not changed.'
        }
    } finally {
        if ((Get-ScheduledTask $task).State -eq 'Running') { Stop-ScheduledTask $task }
        Unregister-ScheduledTask $task -Confirm:$false
    }
}

$lease=$null; $permit=$null
if ($Action -notin @('status','log')) {
    . (Join-Path $env:USERPROFILE '.big-ambitions-bridge\Control.ps1')
    $lease=[BridgeControl.LeaseStore]::ForInstance('agent1')
    if ($Action -eq 'launch') {
        $request=[IO.File]::ReadAllText($RequestPath) | ConvertFrom-Json
        $env:BA_LOCK_TOKEN=$request.token; $Mode=$request.mode
        $lease.AssertPermit($env:BA_LOCK_TOKEN,$request.permit)
    } elseif ($Action -eq 'stop' -and !$Force) { $lease.AssertOwner($env:BA_LOCK_TOKEN) }
    else {
        $kind=if ($Action -eq 'stop' -and $Force) { 'terminate' } else { 'lifecycle' }
        $permit=$lease.Begin($env:BA_LOCK_TOKEN,$kind)
    }
}
try {
switch ($Action) {
 'update' {
    Assert-NoAgent; Assert-Installation
    $install=Get-Content -Raw $markerPath | ConvertFrom-Json
    $overlay=Join-Path $staging 'overlay'
    $manifest=Get-Content -Raw (Join-Path $overlay 'patch-manifest.json') | ConvertFrom-Json
    $player=Get-Content -Raw (Join-Path $overlay 'player-manifest.json') | ConvertFrom-Json
    Check-Hash (Join-Path $source 'Big Ambitions_Data\globalgamemanagers') $player.source_sha256
    foreach ($name in @('UnityPlayer.dll','Big Ambitions_Data\Managed\BigAmbitions.dll')) {
        $copy=if($name -eq 'UnityPlayer.dll'){Join-Path $root $name}else{Join-Path $data 'Managed\BigAmbitions.dll'}
        $known=@($install.sources | Where-Object file -eq 'BigAmbitions.dll')
        if ($name -ne 'UnityPlayer.dll' -and $known.Count -eq 1) {
            Check-Hash (Join-Path $source $name) $known[0].source_sha256
        } else {
            Check-Hash $copy (Get-FileHash -Algorithm SHA256 (Join-Path $source $name)).Hash.ToLowerInvariant()
        }
    }
    foreach ($entry in $manifest) {
        if ($entry.file -eq 'FoodTrucks.dll') {
            # The private mod snapshot is independent of later normal-game deployments.
            $known=@($install.sources | Where-Object file -eq $entry.file)
            if ($known.Count -eq 1) {
                if ($known[0].source_sha256 -ne $entry.source_sha256) { throw 'Different Food Trucks source: update the complete private mod package separately.' }
            } else {
                # Migrate an older receipt only when reproducing its exact installed bridge.
                Check-Hash (Join-Path $profile 'ModsLocal\FoodTrucks\FoodTrucks.dll') $entry.patched_sha256
            }
        } else {
            Check-Hash (Join-Path $source ('Big Ambitions_Data\Managed\'+$entry.file)) $entry.source_sha256
        }
        Check-Hash (Join-Path $overlay $entry.file) $entry.patched_sha256
    }
    Check-Hash (Join-Path $overlay 'globalgamemanagers') $player.patched_sha256
    Read-Entitlements (Join-Path $overlay 'steam-entitlements.json')
    # Include the newly patched game adapter and ownership snapshot in older installations.
    $gameDll=Join-Path $data 'Managed\BigAmbitions.dll'
    if (!($install.files | Where-Object path -eq $gameDll)) {
        $install.files+=@{path=$gameDll;sha256=(Get-FileHash -Algorithm SHA256 $gameDll).Hash.ToLowerInvariant()}
    }
    $backup=Join-Path $root ('revisions\'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff'))
    New-Item -ItemType Directory -Force $backup | Out-Null
    Copy-Item $markerPath (Join-Path $backup 'agent-install.json')
    foreach ($file in $install.files) { Copy-Item $file.path (Join-Path $backup ([IO.Path]::GetFileName($file.path))) }
    # A failed partial update leaves hashes mismatched and therefore cannot be started.
    foreach ($file in $install.files) {
        $name=[IO.Path]::GetFileName($file.path)
        if ($name -ne 'app.info') { Copy-Item (Join-Path $overlay $name) $file.path }
        $file.sha256=(Get-FileHash -Algorithm SHA256 $file.path).Hash.ToLowerInvariant()
    }
    $entitlements=Join-Path $root 'steam-entitlements.json'
    Copy-Item (Join-Path $overlay 'steam-entitlements.json') $entitlements -Force
    if (!($install.files | Where-Object path -eq $entitlements)) {
        $install.files+=@{path=$entitlements;sha256=(Get-FileHash -Algorithm SHA256 $entitlements).Hash.ToLowerInvariant()}
    }
    $controller=Join-Path $data 'Managed\BridgeControl.dll'
    Copy-Item (Join-Path $overlay 'BridgeControl.dll') $controller -Force
    if (!($install.files | Where-Object path -eq $controller)) {
        $install.files+=@{path=$controller;sha256=(Get-FileHash -Algorithm SHA256 $controller).Hash.ToLowerInvariant()}
    }
    Copy-Item $PSCommandPath (Join-Path $root 'Agent-Windows.ps1') -Force
    $install | Add-Member -NotePropertyName sources -NotePropertyValue @($manifest) -Force
    $install | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 $markerPath
    'Agent overlay updated; previous revision retained.'
 }
 'install' {
    Assert-NoAgent; Assert-PlainPath $root; Assert-PlainPath $profile
    if (Test-Path $root) { throw 'Installation already exists; refusing to overwrite it. Build into a new, reviewed revision.' }
    if (Test-Path $profile) { throw 'Agent profile already exists; refusing to merge an unknown profile.' }
    $overlay=Join-Path $staging 'overlay'
    $manifest=Get-Content -Raw (Join-Path $overlay 'patch-manifest.json') | ConvertFrom-Json
    $player=Get-Content -Raw (Join-Path $overlay 'player-manifest.json') | ConvertFrom-Json
    Check-Hash (Join-Path $source 'Big Ambitions_Data\globalgamemanagers') $player.source_sha256
    foreach ($entry in $manifest) {
        $input=if ($entry.file -eq 'FoodTrucks.dll') { Join-Path $originalProfile 'ModsLocal\FoodTrucks\FoodTrucks.dll' } else { Join-Path $source ('Big Ambitions_Data\Managed\'+$entry.file) }
        Check-Hash $input $entry.source_sha256
        Check-Hash (Join-Path $overlay $entry.file) $entry.patched_sha256
    }
    Read-Entitlements (Join-Path $overlay 'steam-entitlements.json')
    New-Item -ItemType Directory $root | Out-Null
    @{product=$product;root=$root;status='installing'} | ConvertTo-Json | Set-Content -Encoding UTF8 $markerPath
    Copy-Tree $source $root
    Rename-Item (Join-Path $root 'Big Ambitions.exe') ($product+'.exe')
    Rename-Item (Join-Path $root 'Big Ambitions_Data') ($product+'_Data')
    New-Item -ItemType Directory (Join-Path $profile 'ModsLocal') -Force | Out-Null
    foreach ($mod in @('FoodTrucks','DrivenRides')) {
        $from=Join-Path $originalProfile ('ModsLocal\'+$mod)
        if (Test-Path $from) { Copy-Tree $from (Join-Path $profile ('ModsLocal\'+$mod)) }
    }
    Copy-Item (Join-Path $overlay 'globalgamemanagers') (Join-Path $data 'globalgamemanagers')
    [IO.File]::WriteAllText((Join-Path $data 'app.info'), "Hovgaard Games`n$product`n")
    $files=@()
    foreach ($name in @('HGPlugins.dll','BigAmbitions.dll','BigAmbitions.ModsInternal.dll','AgentTestRuntime.dll','BridgeControl.dll','FoodTrucks.dll')) {
        $dest=if ($name -eq 'FoodTrucks.dll') { Join-Path $profile 'ModsLocal\FoodTrucks\FoodTrucks.dll' } else { Join-Path $data ('Managed\'+$name) }
        Copy-Item (Join-Path $overlay $name) $dest
        $files+=@{path=$dest;sha256=(Get-FileHash -Algorithm SHA256 $dest).Hash.ToLowerInvariant()}
    }
    $entitlements=Join-Path $root 'steam-entitlements.json'
    Copy-Item (Join-Path $overlay 'steam-entitlements.json') $entitlements
    $files+=@{path=$entitlements;sha256=(Get-FileHash -Algorithm SHA256 $entitlements).Hash.ToLowerInvariant()}
    foreach ($name in @('globalgamemanagers','app.info')) {
        $dest=Join-Path $data $name
        $files+=@{path=$dest;sha256=(Get-FileHash -Algorithm SHA256 $dest).Hash.ToLowerInvariant()}
    }
    Check-Hash (Join-Path $data 'globalgamemanagers') $player.patched_sha256
    foreach ($entry in $manifest) {
        $input=if ($entry.file -eq 'FoodTrucks.dll') { Join-Path $originalProfile 'ModsLocal\FoodTrucks\FoodTrucks.dll' } else { Join-Path $source ('Big Ambitions_Data\Managed\'+$entry.file) }
        Check-Hash $input $entry.source_sha256
    }
    $acl=netsh http show urlacl url=http://127.0.0.1:18330/
    if (($acl -join "`n") -notmatch 'http://127\.0\.0\.1:18330/') {
        $user=[Security.Principal.WindowsIdentity]::GetCurrent().Name
        $null=netsh http add urlacl url=http://127.0.0.1:18330/ "user=$user" listen=yes
        if ($LASTEXITCODE -ne 0) { throw 'Could not reserve the loopback agent bridge URL' }
    }
    # Original game assets are copied, never hard-linked. Saves and normal preferences are not copied.
    Copy-Item $PSCommandPath (Join-Path $root 'Agent-Windows.ps1')
    @{product=$product;root=$root;profile=$profile;status='ready';files=$files;sources=@($manifest);created=[DateTime]::UtcNow.ToString('o');port=18330;test_only=$true} | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 $markerPath
    'Agent-only installation ready.'
 }
 'start' {
    Assert-Installation; Assert-NoAgent
    if ([Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners() | Where-Object Port -eq 18330) { throw 'Agent bridge port is occupied' }
    $task='BigAmbitionsAgentTestsLaunch'
    $user=[Security.Principal.WindowsIdentity]::GetCurrent().Name
    $requestPath=Join-Path $env:USERPROFILE ('.big-ambitions-bridge\agent1\launch-'+[Guid]::NewGuid().ToString('N')+'.json')
    @{token=$env:BA_LOCK_TOKEN;permit=$permit;mode=$Mode} | ConvertTo-Json | Set-Content -Encoding UTF8 $requestPath
    $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $null=icacls $requestPath /inheritance:r /grant:r "*${sid}:(F)"
    if ($LASTEXITCODE -ne 0) { throw 'Could not protect the launch request' }
    $launch=New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$root\Agent-Windows.ps1`" -Action launch -RequestPath `"$requestPath`""
    $principal=New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
    Register-ScheduledTask -TaskName $task -Action $launch -Principal $principal -Force | Out-Null
    try {
        Start-ScheduledTask $task
        $deadline=(Get-Date).AddSeconds(12)
        do { Start-Sleep -Milliseconds 300; $agent=Get-Agent } while (!$agent -and (Get-Date) -lt $deadline)
        if (!$agent) { throw 'Agent launch did not produce a verified process; inspect launch-error.txt' }
        Get-Content -Raw $receiptPath
    } finally { Unregister-ScheduledTask $task -Confirm:$false; Remove-Item -LiteralPath $requestPath -ErrorAction SilentlyContinue }
 }
 'launch' {
    try {
        Assert-Installation; Assert-NoAgent
        $arguments=@('-agent-tests','-disable-force-steam','-console','-foodtrucks-test','-drivenrides-test','-batchmode','-screen-fullscreen','0','-screen-width','960','-screen-height','540','-screen-quality','Low','-job-worker-count','4','-logFile',('"'+$root+'\Player-agent.log"'))
        if ($Mode -eq 'headless') { $arguments+='-nographics' }
        if ($Mode -eq 'windowed') {
            # The full game in a 1920x1080 window at Unity's default quality (the normal game's), uncapped, still muted.
            $arguments=@('-agent-tests','-disable-force-steam','-console','-foodtrucks-test','-drivenrides-test','-screen-fullscreen','0','-screen-width','1920','-screen-height','1080','-logFile',('"'+$root+'\Player-agent.log"'))
            $prefs='HKCU:\Software\Hovgaard Games\'+$product
            (Get-Item $prefs -ErrorAction SilentlyContinue).GetValueNames() | Where-Object { $_ -like 'UnityGraphicsQuality_h*' } | ForEach-Object { Remove-ItemProperty -Path $prefs -Name $_ }
        }
        $p=Start-Process -FilePath $exe -ArgumentList $arguments -WorkingDirectory $root -PassThru
        if ($Mode -ne 'windowed') { $p.PriorityClass='BelowNormal' }
        @{pid=$p.Id;started_ticks=$p.StartTime.ToUniversalTime().Ticks.ToString();exe=$exe;mode=$Mode;port=18330} | ConvertTo-Json | Set-Content -Encoding UTF8 $receiptPath
    } catch { $_ | Out-String | Set-Content (Join-Path $root 'launch-error.txt'); throw }
 }
 'status' {
    $p=Get-Agent
    $health=$null
    if ($p) { try { $health=(Invoke-WebRequest -UseBasicParsing -TimeoutSec 3 'http://127.0.0.1:18330/health').Content | ConvertFrom-Json } catch {} }
    @{installed=(Test-Path $markerPath);running=[bool]$p;pid=$(if($p){$p.Id}else{$null});health=$health;root=$root;profile=$profile} | ConvertTo-Json -Depth 8
 }
 'stop' {
    $p=Get-Agent
    if (!$p) { 'Agent is not running'; break }
    if ($Force) { $p | Stop-Process -Force; 'Stopped the verified agent process'; break }
    $health=(Invoke-WebRequest -UseBasicParsing -TimeoutSec 3 'http://127.0.0.1:18330/health').Content | ConvertFrom-Json
    if ($health.agent_instance.pid -ne $p.Id -or $health.agent_instance.product -ne $product) { throw 'Bridge identity mismatch' }
    $body=@{cmd='quit';args=@{};expect_session=$health.session;lock_token=$env:BA_LOCK_TOKEN} | ConvertTo-Json -Compress
    try { $reply=(Invoke-WebRequest -UseBasicParsing -TimeoutSec 5 -Method Post -ContentType 'application/json' -Body $body 'http://127.0.0.1:18330/call').Content | ConvertFrom-Json } catch { throw 'Quit was not confirmed; inspect agent status before retrying.' }
    if (!$reply.ok) { throw $reply.error }
    'Quit requested for the verified agent process'
 }
 'log' { Get-Content (Join-Path $root 'Player-agent.log') -Tail 90 }
}

} finally { if ($permit) { $lease.End($permit) } }
