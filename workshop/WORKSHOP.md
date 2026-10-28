# HART. Self Driving: Workshop upload

Paste-ready fields for the game's Mod Creator (Main menu > Mods > Mod Creator), the images for the Steam page and the launch posts. This folder is not part of the package.

Design canvas with every card: https://claude.ai/artifact/98zz4yeE3u5CBE9L5oJhvK (private until shared). Exported files go in `workshop/export/`.

## Fields

**Mod folder:** `DrivenRides` (the Mod Creator lists the folders in `ModsLocal`).

**Title:** `HART. Self Driving`

**Workshop item:** https://steamcommunity.com/sharedfiles/filedetails/?id=3819853552 (uploaded 14 October 2026; gallery images added the same evening).

**Target build:** `3682`, verified from the Windows game’s `Player.log` on 22 October 2026. Use this internal game build in the Mod Creator; `25482473` is the separate Steam build ID.

**Version:** `0.4.2`. Prepared package: `build/package/DrivenRides/`; archive: `build/package/DrivenRides-0.4.2.zip`. Update the existing Workshop item above. Publication is a separate step.

**PC installation:** 0.4.1 installed in the normal game's `ModsLocal/DrivenRides` on 22 October 2026 at 18:40 CEST, with all nine files verified against the release manifest. The previous 0.4.0 package with passenger radio is backed up in `DrivenRidesPackageBackups/DrivenRides-20261022-184033`. The normal game was closed and was not launched. Shortcut gameplay was checked in the isolated Mac rig; Windows-specific behavior, rendered UI appearance and listening through speakers remain manual checks.

**Thumbnail:** the package includes the existing `preview.jpg` (947,401 bytes, below the 1 MB limit). The Mod Creator takes the first image in the mod folder’s root.

**Bug reports:** `#self-driving-bugs` in the Peter Hartwieg Discord (created by Peter on 14 October 2026). The invite https://discord.gg/jhdYKgkyPn opens that channel.

**Donations:** PayPal hosted button `Q8KVURCRBFLQN`, the same as Big Copilot's; Peter renamed it on 14 October 2026 to cover all his Big Ambitions projects.

**Description** (Steam formatting):

```
Taxis, taxi stands, your private driver and your own car now drive you through the city instead of fading to black. You stay with the car for the whole trip.

The car uses the game's own driving physics, the same your car has when you drive it. It finds its way through traffic, stops at red lights, gives way and turns at junctions. The camera follows it, and you get out at the door.

[h2]What it does[/h2]
[list]
[*][b]Taxis and taxi stands.[/b] Hail a taxi or walk up to a stand and pick a place on the map as usual. The fare is the game's. Instead of the fade, the taxi drives you there.
[*][b]Your private driver.[/b] Press [b]L[/b] to call your driver. Once it waits, press [b]L[/b] again: you walk to the car and the map opens.
[*][b]Your own car.[/b] Stop in a driving lane or a legal parking space, press [b]L[/b] in the driver's seat and pick a place on the map. The car can pull out through a parking aisle or driveway, drive there and, with autopark enabled, park in a parallel space. If a departure is too tight or obstructed, it returns control where it stands. The trip costs fuel, no fare.
[*][b]Quick destinations.[/b] Press J for your pinned building or K for the last residence visited. Use them in a stopped own car or on the taxi/private-driver destination map. Both keys are configurable.
[*][b]Quicker driving.[/b] Closer following, smaller junction gaps and faster corners. Tight turns still slow down when the car needs more steering room.
[*][b]Radio during rides.[/b] Taxis and private-driver rides play the normal car radio with your selected station, volume and phone controls. Headphones can keep playing when you get out.
[*][b]The ride panel.[/b] A small card shows the distance left, 1x, 2x and 3x speed, a slider for the clock and [b]Skip[/b]. Skip ends the ride the base game's way.
[/list]

[h2]The clock[/h2]
A drive takes longer than the base game's instant ride, so during a ride the clock runs at a third of its speed by default. The slider sets anything from a stopped clock to normal speed. Use the time: the phone works during a ride.

[h2]Options[/h2]
Under Settings > Mods > HART. Self Driving you can switch taxis, the driver and your own car on or off, set the ride speed and clock, choose what happens at the end of a self-driven trip, and turn on "Stop for pedestrians" or "Ignore speed limits" (both off by default).

[h2]Good to know[/h2]
[list]
[*]If there is no route the car can drive, or it gets stuck for good, the ride ends the base game's way: a fade and you are at the door.
[*]Now and then the car slows down for a car that isn't really in its way, then drives on.
[*]One larger-car test overran a stop line at 3x speed; this remains under investigation. Quicker corners do not guarantee shorter trips through traffic and lights.
[*]Visible arrival parking is limited to parallel spaces. Larger cars, tight spaces and interruption cases still need broader testing; garages are not supported.
[*]The bridge to the Hamptons keeps the game's own fade, then the car drives on from the far side.
[*]Gated communities open only when you own a building behind the gate.
[*]L calls your driver or opens your own car's map. J drives to the pinned building; K drives to the last residence visited in the loaded game. Change or disable J/K in the mod options. Home resets on load; loading inside a residence sets it as home.
[*]The mod adds nothing to your save. Unsubscribe and your saves load without it.
[/list]

[h2]Bug reports[/h2]
Post bugs in [url=https://discord.gg/jhdYKgkyPn]#self-driving-bugs on my Discord[/url], one post per problem. Include the mod version, the game build and what happened. The lines in Player.log that start with [Mod:DrivenRides] help most; the file is in %USERPROFILE%\AppData\LocalLow\Hovgaard Games\Big Ambitions.

[h2]Support[/h2]
The mod is free and stays free. If you'd like to say thanks, you can [url=https://www.paypal.com/donate/?hosted_button_id=Q8KVURCRBFLQN]support my Big Ambitions projects via PayPal[/url]. It helps me keep this mod working after game updates.

Made for the HART. Group series. The full README ships with the mod. MIT licence for the code; the HART name and artwork are not included in that licence.
```

**Change log** (0.4.1 update):

```
0.4.1 — quick destinations

- Press J to drive to your pinned building, or K to drive to the last residence you visited.
- Use either shortcut in your stopped own car or on a taxi/private-driver destination map.
- Change or disable each key in the mod options.
- Home resets when loading a save; loading inside a residence sets that as home.

Restart the game after updating. No save migration is needed.
```

**Previous change log** (0.4.0):

```
0.4.0 — parking and more assertive driving

- Start self-driven trips from legal parking bays and parallel spaces, including forward or reverse exits through an aisle or driveway.
- Visible parallel parking, with traffic held outside the manoeuvre area and the existing fade-and-park fallback.
- Closer following, smaller junction gaps and quicker passing of waiting cars.
- Firmer braking for stationary cars and queues.
- Faster corners while retaining slower, tighter turns where the car needs steering room.
- Normal car radio in taxis and private-driver rides, with your current station, volume and phone controls.

Known issues: one larger-car test overran a stop line at 3x speed; wider traffic, vehicle and tight-parking coverage remains pending. Corner timing improved in the sampled tests, but a general full-trip speedup is unverified.

Restart the game after updating. No save migration is needed.
```

## Images

The Mod Creator uploads one thumbnail. The rest go on the item's Steam page after the upload (Add/edit images & videos), in this order:

1. Wide hero (`Banner`), the first image people see.
2. Feature cards: taxis, private driver, your own car, ride panel.
3. Two or three real screenshots from a ride without UI cards over them: the limo at a red light at dusk, a taxi turning onto an avenue seen from behind, the Hamptons drive. Steam readers trust real shots more than cards.
4. The announcement video once it is cut (`docs/announcement-video.md`).

The cards use generated artwork (GPT 6.1 Sol image generation, prompts and checks in `workshop/art/sources/`; the banner and ride panel card use the first wide hero, `art/hero-wide-v1.png`, whose left side is calmer than the retry) with the real outlined HART lettering on top, as the brand kit asks. They show what the mod does; the screenshots show how it looks.

## Launch posts

Post only once the Steam page has its images, because the upload is public as soon as it finishes. `https://steamcommunity.com/sharedfiles/filedetails/?id=3819853552` is the item's link.

**Reddit**, r/BigAmbitions, as a video or image post:

```
I made a mod so taxis, your driver and your own car actually drive you through the city instead of fading to black

Once I had money in Big Ambitions I took the driver everywhere, and the city turned into a list of loading screens. So I built a mod that keeps you with the car for the whole trip.

- Taxis, taxi stands and your private driver drive you there with the game's own car physics: traffic, red lights, junctions.
- Your own car can drive itself: stop in a lane, press L, pick a place on the map. It autoparks if the car has it.
- A small ride panel gives you 1x/2x/3x, a clock slider (the clock runs at a third of its speed during rides by default) and Skip for the old fade.
- Fares stay the game's. Nothing is added to your save.

It's free on the Workshop: https://steamcommunity.com/sharedfiles/filedetails/?id=3819853552

Bug reports are welcome. If a ride can't be driven, it falls back to the normal fade, so you always arrive.
```

**X**, with a 15 to 30 second clip:

```
Taxis in Big Ambitions now drive you through the city instead of fading to black. Your driver and your own car too.

Free on the Steam Workshop: https://steamcommunity.com/sharedfiles/filedetails/?id=3819853552

#BigAmbitions
```

**YouTube community post**, with the `Social` square:

```
New mod: HART. Self Driving. Taxis, your private driver and your own car drive you through the city instead of fading to black. You'll see it in the next episodes.

Get it here: https://steamcommunity.com/sharedfiles/filedetails/?id=3819853552

Which trip would you watch the whole way?
```
