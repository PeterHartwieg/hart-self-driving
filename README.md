# HART. Self Driving

A mod for Big Ambitions. Taxi rides, taxi stands, your private driver and your own car drive you through the city instead of fading to black. The car is a real car with the game's own driving physics, the same your car has when you drive it. It finds its way through traffic, stops at red lights, gives way and turns at junctions. You ride along, the camera follows the car, and you get out at the door.

Version 0.2.0-rc6, a release candidate. Built for Big Ambitions 1.0 (Steam build 25482473).

## What it does

**Taxis and taxi stands.** Hail a taxi or walk up to a stand and pick a place on the map as usual. The fare is the game's. Instead of the fade, the taxi drives you there.

**Your private driver.** Press **L** to call your driver. Once it waits, press **L** again: you walk to the car and the map opens. The driver is dismissed after the ride, as in the base game. The phone still works for picking a different vehicle.

**Your own car.** Sit in the driver's seat of your car, stopped in a driving lane, and press **L**. Pick a place on the map and the car drives itself there. You stay in the driver's seat. The trip costs fuel and no fare. At the end the car autoparks or leaves the parking to you, depending on the options. A car parked at the kerb has to be pulled into the lane first.

**The ride panel.** While the car drives, a small card under the objectives shows the distance left, buttons for 1x, 2x and 3x, a slider for the clock and a **Skip** button. 2x and 3x run the world faster. The car drives the same way, it just gets there sooner. Skip ends the ride the base game's way: a fade, the time for the rest of the way, and you are at the door.

**The clock.** During a ride the clock runs at a third of its speed by default. A drive takes longer than the base game's instant ride, so the clock is slowed to keep rides from eating your day. The slider on the panel and the options set anything from a stopped clock to normal speed.

**When a ride can't be driven.** If the mod finds no route the car can drive, or the car gets stuck for good, the ride ends the base game's way: a fade and you are at the door. You get a notice when a ride falls back this way from the start.

## Options

In the game's Mods options, under HART. Self Driving:

| Option | Default |
|---|---|
| Taxis drive you there | On |
| Your private driver drives you there | On |
| Real car physics (off: the older, simpler rides) | On |
| Your car drives itself (L in the driver's seat) | On |
| At the end of a self-driven trip: park yourself, autopark cars that have it, or autopark every car | Autopark cars that have it |
| Ride speed: 1x, 2x or 3x | 1x |
| Clock speed during rides, 0 to 100 % | 33 % |
| Stop at red lights | On |
| Stop for pedestrians | Off |
| Ignore speed limits | Off |
| L calls your private driver | On |

The panel's speed buttons and clock slider change the same settings, so the next ride starts with your last choice. With "Real car physics" off, taxi and driver rides are the mod's first version: the car is a traffic car steered along the route. They look less real and are kept as a way back if the physics rides give you trouble. Your own car always drives with the physics. "Stop for pedestrians" makes the car brake for people in its way; the game's traffic doesn't, and the car passes through people either way. "Ignore speed limits" lets the car reach its own top speed on straight roads, still slowing for corners, traffic and red lights. It applies from the next ride.

## Known limits

- **Odd stops.** Now and then the car slows down or stops for a car that is not really in its way, then drives on. If it stays stuck, the mod tries another way, and after that ends the ride the base game's way.
- **A ride may fall back to the base game's ride.** That happens with no route the car can drive, no free lane beside a taxi stand or a parked driver, or a car stuck for good. You arrive either way.
- **Narrow streets.** Big cars don't make U-turns where the lanes are close together. They go round the block.
- **Gated communities.** The car opens a gate only when you own a building behind it. Otherwise the ride ends on the lane before the gate.
- **The bridge to the Hamptons** is the game's own skip: a fade, then the car drives on from the far side.
- **Your own car** has to start from a driving lane, stopped. A modded car without a drivable model can't drive itself.
- **Getting out** of your own car during a trip ends the trip where the car stands.
- **L** is the mod's only key. Another mod that uses plain L will clash; turn off "L calls your private driver" then. In the driver's seat L stays the self-driving key while "Your car drives itself" is on.

## Compatibility

- Big Ambitions 1.0, Windows. A game update can change what the mod hooks into. Each hook logs `Patch <method>: Active` or `Not active` in `Player.log` at startup. A hook that is not active leaves that part to the base game.
- Mods that also change taxi rides, the private driver or the game's taxi system may clash with this one.
- Fares, achievements, events and the limo's energy and food bonus stay as in the base game.

## Removing the mod

HART. Self Driving adds nothing to your save. A save made during a ride stores the place where you got in, so loading it puts you back there on foot, as if the ride never started. To remove the mod, unsubscribe on the Workshop (or delete the `DrivenRides` folder from `ModsLocal`) and start the game. Your saves load without it.

The options stay in the game's settings (Unity's PlayerPrefs, keys starting with `m:`). They do nothing without the mod.

## Evidence

What has been seen in the game, and what is still waiting for a check. "Checked" means seen in a running game. Reading code doesn't count.

| Question | Status |
|---|---|
| A hailed taxi drives with the game's car physics and arrives at the door | Checked: six rides on 0.1.91, at 1x, 2x and 3x |
| A ride over the bridge to the Hamptons crosses with the game's skip and drives on | Checked: one ride on 0.1.91 |
| A ride in the bridge's rightmost lane lands on the ramp, stays visible and makes the right turn at its foot | Checked: four taxi rides on 0.2.0-rc5 (15 October 2026); the left lane and the own car pending |
| Ocean Crest Road retail destinations get a nearby drop-off instead of the road's entrance | Pending on rc5: rc4 accepted a singleton route and froze; recorded pocket and zero-distance regressions pass offline |
| The private driver drives with the game's car physics | Checked: one ride on 0.1.94, three on 0.2.0-rc1, one on 0.2.0-rc2 |
| The private driver's car keeps its colour from pickup to drop-off | Pending: 0.2.0-rc3 retains the paint component; 0.2.0-rc2's earlier check was contradicted by a car changing colour at boarding |
| A taxi stand ride drives | Pending |
| Your own car drives itself, arrives, autoparks and Skip works | Checked: on 0.1.92 and 0.1.93, 10 of 10 autoparks |
| Getting out of your own car mid-trip ends the trip cleanly | Checked on 0.1.92 |
| Hospital and a save and load during your own car's trip | Pending |
| The ride panel card looks right and its buttons work | Checked: clean install on 0.2.0-rc1 (Skip, 1x to 3x, clock slider) |
| The option "Real car physics" off brings back the older rides | Pending |
| A normal game (no test flag) has no debug keys, no files in `DrivenRides/` and a short log | Checked: clean install on 0.2.0-rc1, 14 October 2026 (one start and one end line per ride, no data folder) |
| A save made with the mod loads without it | Checked: clean install on 0.2.0-rc1, a normal and a mid-ride save; options kept after reinstalling (0.2.0-rc2) |

## Building

For developers. Needs Unity 2022.3.62f2 with Windows Build Support (Mono), the official [Big Ambitions modding SDK](https://github.com/hovgaardgames/bigambitions) with the game's DLLs, and the .NET SDK.

```bash
dotnet test tests
tools/build-mod.sh        # build/DrivenRides/, the folder for ModsLocal
tools/package-mod.sh      # build/package/DrivenRides/, the Workshop folder, checked
```

`tools/mod.conf` holds the mod's name in one place; `tools/rename-mod.sh "<new name>"` writes it everywhere it appears.

Testing features need a launch flag: start the game with `-drivenrides-test` (or `-foodtrucks-test`, which the test PC already passes). Then the debug keys work, the turn-test rig reads its command files (`docs/turn-tests.md`), and rides write traces and dumps to `<LocalLow>/Hovgaard Games/Big Ambitions/DrivenRides/` and a detailed log. Without the flag none of that runs.

Debug keys, Ctrl+Alt+Shift with: **G** lane graph stats, **X** clear a stuck ride, **K** remove the ride car, **V** hospital now, **U** throw inside the ride, **J** fake a save change, **L** park a dressed taxi beside you.

## Licence

MIT, see `LICENSE.txt`. Harmony is MIT, by Andreas Pardeike, and ships in `Dependencies/` with its licence.
