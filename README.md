# Driven Rides

A mod for Big Ambitions. Taxi and private-driver rides drive through Manhattan instead of fading to black: you ride along, the camera follows the car, and you arrive at the door. The clock runs at a third of its speed while you ride, the fare is the base game's, and buttons on the ride panel speed the ride up (2x, 3x) or skip it (the base game's fade).

Press **L** to call your private driver. Press **L** again once it waits, and you walk to it and choose where to go.

Status: in development (Milestone 0, the spike). Not on the Workshop yet.

## Options

In the game's Mods options: driven taxi rides and private-driver rides, each on or off; the ride speed (1×, 2× or 3×; default 1×); the clock's speed during a ride (a slider from 0 %, a stopped clock, to 100 %, normal; default 33 %); stopping at red lights; and the driver shortcut. The ride panel's speed buttons and clock slider change the same settings, so the next ride starts with the last choice.

## Evidence

What has been seen in the game, what is pending and what is only read from code.

| Question | Status |
|---|---|
| Gley follows a long preset path through intersections | Unverified (code reading) |
| A car with a preset path is never despawned | Unverified (code reading) |
| The vehicle camera follows a car the player is attached to | Unverified |
| Ride physics hold at 3× | Unverified |

## Debug keys

Ctrl+Alt+Shift with: **G** graph stats, **X** clear a stuck ride, **K** remove the ride car, **V** hospital mid-ride, **U** throw inside the ride, **J** fake a save change. Spike builds only.

## Licence

MIT, see `LICENSE.txt`. Harmony is MIT-licensed and fetched at build time.
