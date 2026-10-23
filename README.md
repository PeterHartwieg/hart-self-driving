# HART. Self Driving

A mod for Big Ambitions. Taxi rides, taxi stands, your private driver and your own car drive you through the city instead of fading to black. The car is a real car with the game's own driving physics, the same your car has when you drive it. It finds its way through traffic, stops at red lights, gives way and turns at junctions. You ride along, the camera follows the car, and you get out at the door.

Version 0.4.1. Built for Big Ambitions 1.0 (game build 3682; Steam build 25482473).

This update adds configurable shortcuts for the pinned building and the last residence visited. See [CHANGELOG.md](CHANGELOG.md) for the changes and remaining issues.

## What it does

**Taxis and taxi stands.** Hail a taxi or walk up to a stand and pick a place on the map as usual. The fare is the game's. Instead of the fade, the taxi drives you there.

**Your private driver.** Press **L** to call your driver. Once it waits, press **L** again: you walk to the car and the map opens. The driver is dismissed after the ride, as in the base game. The phone still works for picking a different vehicle.

**Quick destinations — checked in the isolated Mac rig.** Press **J** to drive to the building pinned with the game's **Set destination**, or **K** to drive home: the most recent residential building you entered in the current loaded game. Use either key in your stopped own car, or on the destination map for a taxi, taxi stand or private driver. Each key can be changed to G, J, K, U, V or X, or disabled, in the mod options. Home resets when you load a game; loading inside a residence sets that as home. Visiting a business does not change it. No home visit or pin means a notice and no trip.

**Radio during rides.** Taxis, taxi stands and private-driver rides play the game's normal radio, using your current station and volume with the usual phone controls. Leaving the ride turns the car radio off; headphones can keep playing. Your own car keeps its normal radio behavior. Audio heard through the speakers remains pending verification.

**Your own car.** Sit in the driver's seat of your stopped car and press **L**. Pick a place on the map and the car drives itself there. You stay in the driver's seat. The trip costs fuel and no fare. A legally parked car first plans an exit through its parking aisle or curbside space to a reachable road lane. It can pull forward or reverse, then joins the road in its driving direction. With autopark enabled, the route aims for the lane beside a parallel parking strip, facing the right way, then looks for a free space on arrival. It temporarily reserves the manoeuvre area, lets traffic already inside clear it, and holds approaching traffic outside while pulling out or parking. It tries to park visibly, with the previous fade-and-park as a fallback. If it cannot pull out safely, it returns control where it stands.

**Parking evidence: partly checked in game.** A Vord pulled out of a parallel space, waited for passing traffic and continued its road route. Pause/resume and the blocked-parking fallback also passed. In the isolated headless rig, a physical parallel park completed while the reservation held one approaching Gley car; legal parking, returned controls and reservation cleanup passed. The rig also exposed a tight-gap search failure: parking12 keeps short collision-checked shunts, passes all four captured geometry replays, and completed a departure after checkpoint reload. Rendered appearance and the wider traffic/strip-routing matrix remain pending. Both sides, larger cars and interruption cases remain in `CHECKS.md`. Garages remain outside this implementation; physical arrival parking remains parallel.

**Parking-lot departures: checked in the isolated rendered Windows rig.** A Vord left perpendicular bays in a waterfront lot and a factory lot, including a reversed factory-bay start, and joined the road. The waterfront run yielded to pedestrians and held approaching traffic. Seeded tests select actual free legal spaces, require 20 m of physical road driving after exit, and record the start pose and cleanup result. Tightly boxed parallel spaces still need broader coverage; the wider vehicle/location matrix remains pending. See the source repository’s `CHECKS.md`.

**Stopped-traffic braking: partly checked.** Fixed a braking weakness exposed by the Food Truck: a stationary leader now gets the same distance-based braking as a stop line. Pedal-response regressions pass, and an isolated headless truck drive completed 1.3 km without contact. Repeating the original stopped-car queue in game remains pending; see `CHECKS.md`.

**More assertive driving: pending in game.** The driver follows moving traffic more closely, accepts smaller junction gaps and passes waiting cars faster. Core simulations show modest travel-time gains; real-game journey times, contact frequency and driving feel remain unverified. See the source repository’s `CHECKS.md` for the comparison and braking tradeoff.

**Faster corners: partly checked in the isolated Mac rig.** The preferred corner speed rises by about 22%, while tight turns retain a lower speed when the car needs steering room. The sampled ordinary turns took about 12% less time between their entry and exit gates, with slightly more tracking error and no recorded contact. One larger-car run overran a stop line, and traffic-light waits made the complete test routes slower overall; a reliable full-trip speedup and Windows driving feel remain unverified. See the source repository’s `CHECKS.md` for all outcomes and the rig's control-case limitations.

**Route line — pending in-game verification.** A blue line on the road shows the remaining driving route, including its turns and planned obstacle offsets. It shortens as the car advances and changes when the driver accepts a new route. “Show the planned route on the road” is on by default and can be changed during a ride. The line ends at the selected road arrival point near the destination; it hides during boarding, local parking manoeuvres and bridge fades, and on arrival, Skip or cancellation. Very long routes can take a few frames to appear while their road heights are sampled.

**The ride panel.** While the car drives, a small card under the objectives shows the distance left, buttons for 1x, 2x and 3x, a slider for the clock and a **Skip** button. 2x and 3x run the world faster. The car drives the same way, it just gets there sooner. Skip ends the ride the base game's way: a fade, the time for the rest of the way, and you are at the door.

**The clock.** During a ride the clock runs at a third of its speed by default. A drive takes longer than the base game's instant ride, so the clock is slowed to keep rides from eating your day. The slider on the panel and the options set anything from a stopped clock to normal speed.

**When a ride can't be driven.** If the mod finds no route the car can drive, or the car gets stuck for good, the ride ends the base game's way: a fade and you are at the door. You get a notice when a ride falls back this way from the start.

## Options

In the game's Mods options, under HART. Self Driving:

| Option | Default |
|---|---|
| Show the planned route on the road | On |
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
| Drive to pinned building: key | J |
| Drive home: key | K |

The panel's speed buttons and clock slider change the same settings, so the next ride starts with your last choice. With "Real car physics" off, taxi and driver rides are the mod's first version: the car is a traffic car steered along the route. They look less real and are kept as a way back if the physics rides give you trouble. Your own car always drives with the physics. "Stop for pedestrians" makes the car brake for people in its way; the game's traffic doesn't, and the car passes through people either way. "Ignore speed limits" lets the car reach its own top speed on straight roads, still slowing for corners, traffic and red lights. It applies from the next ride.

## Known limits

- **Odd stops.** Now and then the car slows down or stops for a car that is not really in its way, then drives on. If it stays stuck, the mod tries another way, and after that ends the ride the base game's way.
- **A ride may fall back to the base game's ride.** That happens with no route the car can drive, no free lane beside a taxi stand or a parked driver, or a car stuck for good. You arrive either way.
- **Narrow streets.** Big cars don't make U-turns where the lanes are close together. They go round the block.
- **Gated communities.** The car opens a gate only when you own a building behind it. Otherwise the ride ends on the lane before the gate.
- **The bridge to the Hamptons** is the game's own skip: a fade, then the car drives on from the far side.
- **Your own car** must be stopped in a driving lane facing with traffic, or in a legal parking space with a clear route out. A tight or obstructed departure can require taking the wheel. A modded car without a drivable model can't drive itself.
- **Getting out** of your own car during a trip ends the trip where the car stands.
- **Key conflicts.** L calls the driver or opens your own car's destination map; J and K select quick destinations by default. Change or disable the destination keys if another mod uses them. Assign different keys to pinned building and home; a duplicate binding shows a notice without starting a ride. Turn off "L calls your private driver" if needed; in the driver's seat L stays the self-driving key while "Your car drives itself" is on. The shortcuts ignore modifiers and typing.

## Compatibility

- Big Ambitions 1.0, Windows. A game update can change what the mod hooks into. Each hook logs `Patch <method>: Active` or `Not active` in `Player.log` at startup. A hook that is not active leaves that part to the base game.
- Mods that also change taxi rides, the private driver or the game's taxi system may clash with this one.
- Fares, achievements, events and the limo's energy and food bonus stay as in the base game.

## Removing the mod

HART. Self Driving adds no custom data to your save. Ride and manoeuvre plans are temporary and do not resume after loading; your own car uses the game's ordinary vehicle state. To remove the mod, unsubscribe on the Workshop (or delete the `DrivenRides` folder from `ModsLocal`) and start the game. Your saves load without it.

The options stay in the game's settings (Unity's PlayerPrefs, keys starting with `m:`). They do nothing without the mod.

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
