# HART. Self Driving changelog

## 0.4.1 — 22 October 2026

- Add configurable shortcuts to drive to the pinned building (J) or last residence visited in the loaded game
  (K). Use them in a stopped own car or on a taxi/private-driver destination map. Each can be changed or disabled.
  Home resets on load, with an indoor residence load counting as a visit.
- Checked with real keyboard events in the isolated Mac rig: own car, taxi, taxi stand and private driver;
  residential/business visits, indoor/outdoor reloads, rebinding, disabling and conflicting keys.
  The Windows SDK build passes; interactive Windows and rendered UI appearance remain unverified.

## 0.4.0 — 22 October 2026

### Parking and departures

- Start a self-driven trip from a legal parallel space or parking bay. The car plans a forward or reverse exit
  through the aisle or driveway, then joins the road. If there is no clear exit, it returns control where it stands.
- With autopark enabled, approach a suitable parallel parking strip from the correct direction and park visibly.
  The existing fade-and-park remains a fallback when the manoeuvre cannot be completed.
- During parking and unparking, let nearby traffic clear the manoeuvre area and hold approaching cars outside it.
- Improve tight-space shunts, reversed bay departures and the handoff from the parking area to road driving.

### Driving

- Play the normal car radio during taxi, taxi-stand and private-driver rides, with the existing station, volume
  and phone controls. Ending or cancelling the ride releases the radio without cutting off headphones.
- Follow moving traffic more closely, take smaller junction gaps and move past waiting cars more quickly.
- Brake more firmly when approaching a stationary car or queue.
- Take corners faster: the preferred corner speed increases by about 22% on an identical curve. Tight turns
  remain available at a lower speed when the car needs more steering room.

### Known issues and verification

- **Partly checked in game:** legal bay/parallel departures and parallel parking, with wider coverage of larger
  vehicles, tight spaces and interruptions still pending. Physical arrival parking is limited to parallel spaces;
  garages are not supported.
- **Partly checked in game:** the sampled ordinary turns took about 12% less time between their entry and exit
  gates in the isolated Mac rig, with slightly more tracking error. Complete journeys were slower in that run
  with varying traffic-light waits, so a general full-trip speedup is unverified.
- One larger-car test overran a stop line at 3x simulation speed. Reproduction at 1x and a fix remain pending.
- **Pending in game:** broader moving-traffic validation of the shorter following and junction gaps. Automated
  braking and traffic simulations pass, but they do not establish collision-free game driving.
- A tight or obstructed departure may still require taking the wheel. A ride that cannot continue uses the
  established fallback or returns control, depending on the ride type and stage.

### Updating

Built for Big Ambitions 1.0, game build 3682. Restart the game after updating. This update adds no custom save
data and requires no save migration. Keep one enabled copy of the mod; avoid duplicate local and Workshop copies.
