#!/usr/bin/env python3
"""Seeded legal-parking departures in the owned, isolated Windows agent rig.

Reloads a disposable FT checkpoint for every case. The checkpoint must own the test
car. Only fixture setup places the car; the departure uses the real ride controller.
"""
import argparse
import importlib.machinery
import importlib.util
import json
import pathlib
import time

HERE = pathlib.Path(__file__).resolve().parent
loader = importlib.machinery.SourceFileLoader('agent_client', str(HERE / 'agent'))
spec = importlib.util.spec_from_loader(loader.name, loader)
agent = importlib.util.module_from_spec(spec)
loader.exec_module(agent)
PROFILE = r'$env:USERPROFILE\AppData\LocalLow\Hovgaard Games\Big Ambitions Agent Tests'


def call(command, args):
    response = agent.call(command, args)
    if not response.get('ok'):
        raise RuntimeError(str(response))
    return response


def health():
    value = json.loads(agent.remote("& 'C:\\BigAmbitions-AgentTests\\Agent-Windows.ps1' -Action status"))['health']
    if not value:
        raise RuntimeError('Start the isolated agent player before running parking cases')
    agent.require_identity(value)
    return value


def results():
    value = agent.remote('if (Test-Path "' + PROFILE + '/DrivenRides/tests/ride-results.jsonl") { Get-Content -Raw "' +
                         PROFILE + '/DrivenRides/tests/ride-results.jsonl" }')
    return [json.loads(line) for line in value.splitlines() if line.strip()]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--checkpoint', required=True)
    parser.add_argument('--seeds', default='1,2,3,4,5')
    parser.add_argument('--parking', choices=['bays', 'parallel', 'all'], default='bays')
    parser.add_argument('--near', help='x,z: sample among the four closest parking areas')
    parser.add_argument('--reverse', action='store_true')
    parser.add_argument('--vehicle', default='vordtiaravic')
    parser.add_argument('--destination', default='ba:street_fifthstreet#1')
    parser.add_argument('--timeout', type=float, default=300)
    parser.add_argument('--output', type=pathlib.Path, default=pathlib.Path('build/parking-cases.jsonl'))
    parser.add_argument('--package', type=pathlib.Path, default=pathlib.Path('build/DrivenRides.sha256'))
    args = parser.parse_args()
    if not args.checkpoint.startswith('FT'):
        parser.error('Only disposable FT checkpoints are supported')
    seeds = [int(seed) for seed in args.seeds.split(',')]
    agent.locks.token('agent1')  # Requires the caller's acquired ownership, never takes over.
    health()
    # Match the tested package to local build output, including Core and dependencies.
    hashes = {}
    for line in args.package.read_text().splitlines():
        digest, name = line.split(None, 1)
        hashes[name.removeprefix('./')] = digest
    check = '''$ErrorActionPreference='Stop'
$expected=[Console]::In.ReadToEnd() | ConvertFrom-Json
$root="''' + PROFILE + '''/ModsLocal/DrivenRides"
foreach ($item in $expected.psobject.Properties) {
 if ((Get-FileHash -Algorithm SHA256 (Join-Path $root $item.Name)).Hash.ToLower() -ne $item.Value) { throw "Package mismatch: $($item.Name)" }
}
'Installed package hashes verified.'
'''
    print(agent.remote(check, payload=hashes), flush=True)
    remote_script = 'DrivenRides-ParkingCases.ps1'
    agent.remote("[IO.File]::WriteAllText((Join-Path $env:USERPROFILE '" + remote_script +
                 "'), ([Console]::In.ReadToEnd() | ConvertFrom-Json))", payload=(HERE / 'Send-RideCommand.ps1').read_text())
    args.output.parent.mkdir(parents=True, exist_ok=True)
    failures = 0
    try:
        for seed in seeds:
            call('load', {'name': args.checkpoint})
            deadline = time.monotonic() + 150
            while True:
                state = health()
                if state['scene'] == 'city' and (state.get('load') or {}).get('status') == 'done':
                    break
                if time.monotonic() > deadline:
                    raise TimeoutError('Checkpoint load timed out')
                time.sleep(3)
            call('time', {'pause': True})
            before = len(results())
            command = (f'ride kind=own vehicle={args.vehicle} parkseed={seed} parking={args.parking} '
                       f'address={args.destination} speed=3 autopark=off departonly=on')
            if args.near:
                command += ' parknear=' + args.near
            if args.reverse:
                command += ' parkreverse=on'
            if any(character.isspace() for value in (args.vehicle, args.destination, args.near or '') for character in value):
                raise ValueError('Command arguments must not contain whitespace')
            print(agent.locks.invoke(remote_script, {'Command': command}, instance='agent1', guarded=True), flush=True)
            call('time', {'pause': False})
            deadline = time.monotonic() + args.timeout
            while True:
                rows = results()
                if len(rows) > before:
                    row = rows[before]
                    break
                if time.monotonic() > deadline:
                    raise TimeoutError(f'Parking seed {seed} timed out; world will be paused for inspection')
                time.sleep(3)
            call('time', {'pause': True})
            passed = (row.get('departurePassed') is True and row.get('parkingLegal') is True
                      and row.get('contacts') == 0 and row.get('redRuns') == 0 and row.get('keysBack') is True
                      and row.get('driveLeft') is False and row.get('manoeuvreLeft') is False
                      and row.get('parkingReservationsLeft') == 0)
            row.update(casePassed=passed, command=command, packageHashes=hashes, checkpoint=args.checkpoint)
            with args.output.open('a') as output:
                output.write(json.dumps(row) + '\n')
            failures += not passed
            print(json.dumps({'seed': seed, 'passed': passed, 'site': row.get('parkingSite'),
                              'end': row.get('refused') or row.get('end')}), flush=True)
    finally:
        call('time', {'pause': True})
    print(f'{len(seeds)-failures}/{len(seeds)} parking departures passed; {args.output}', flush=True)
    return int(failures > 0)


if __name__ == '__main__':
    raise SystemExit(main())
