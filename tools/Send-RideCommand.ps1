param([Parameter(Mandatory=$true)][string]$Command)
$ErrorActionPreference='Stop'
$health=(Invoke-WebRequest -UseBasicParsing 'http://127.0.0.1:18330/health').Content | ConvertFrom-Json
if ($health.agent_instance.id -ne 'agent1' -or !$health.agent_instance.test_only -or
    $health.agent_instance.executable -ne 'C:\BigAmbitions-AgentTests\Big Ambitions Agent Tests.exe' -or
    !$health.paused -or $health.save -notlike 'FT*') { throw 'Expected paused disposable agent world' }
if ($Command -match "[\r\n]" -or !$Command.StartsWith('ride kind=own ')) { throw 'Expected one own-car ride command' }
$dir=Join-Path $env:USERPROFILE 'AppData\LocalLow\Hovgaard Games\Big Ambitions Agent Tests\DrivenRides\tests'
if (!(Test-Path $dir)) { throw 'Driven Rides test rig not initialized' }
if (Test-Path (Join-Path $dir 'command.txt')) { throw 'A command is already queued' }
[IO.File]::WriteAllText((Join-Path $dir 'command.txt'), "$Command`nend`n")
"Queued: $Command"
