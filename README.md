# Driven Rides

A mod for Big Ambitions. Taxi and private-driver rides drive through Manhattan instead of fading to black: you ride along, the camera follows the car, and you arrive at the door. The clock runs at a third of its speed while you ride, the fare is the base game's, and buttons on the ride panel speed the ride up (2x, 3x) or skip it (the base game's fade).

Press **L** to call your private driver. Press **L** again once it waits, and you walk to it and choose where to go.

In the driver's seat of your own car, stopped on the street, press **L** to choose a destination: the car drives itself there and you stay in the driver's seat. Depending on the options it then autoparks or leaves the parking to you. The trip costs fuel, no fare.

Status: in development (Milestone 0, the spike). Not on the Workshop yet.

## Options

In the game's Mods options: driven taxi rides, private-driver rides and your own car driving itself, each on or off; what happens at the end of a self-driven trip (park yourself, autopark cars that have autopark, or autopark every car; default the second); the ride speed (1×, 2× or 3×; default 1×); the clock's speed during a ride (a slider from 0 %, a stopped clock, to 100 %, normal; default 33 %); stopping at red lights; and the driver shortcut. The ride panel's speed buttons and clock slider change the same settings, so the next ride starts with the last choice.

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
