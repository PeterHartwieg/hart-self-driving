# Real Life Overhaul compatibility

Real Life Overhaul (RLO, Workshop 3816536560, source AdultCudi/real-life-overhaul) changes several things this mod relies on. `unity/DrivenRides/Scripts/RloCompat.cs` handles them. It finds RLO by its assembly's name (`RealLifeOverhaul`) and reaches it only by reflection. Without RLO no patch is made, every question answers as if RLO weren't there, and the only trace is one log line at the first city: `Real Life Overhaul: not loaded; its patches are not active.`

Checked against RLO 0.41.1 (the shipped DLL: every member below exists) and the 0.45.0 source. RLO's driving, police, camera and car ownership code is the same in both.

## What RLO does, and what this mod does about it

| RLO | Effect on a ride | Here |
|---|---|---|
| `Driving/CarTuning` and `CarHandling` re-tune `GameManager.selectedVehicle` every time it changes (from 0.39.0, reworked in 0.40.5): power, taller gearing, speed limiter off, brakes building over 0.25 s, more weight, springs, dampers, tyres, ABS | The own car's drive, its start check and the parking manoeuvres were tuned on the game's car | `OwnCarDrive.Begin` calls `RloCompat.Hold` before the car is measured: RLO's own `Restore()` takes everything off and a prefix on `CarTuning.OnUpdate` skips it during the trip. `End` calls `Release`: `_seen` is cleared, so RLO puts the tuning back on its next frame |
| RLO's eased pedals (`inputModifyCallback`) | None: NWH doesn't call the callback while `autoSetInput` is off | Nothing |
| `Crime/PoliceAttention` watches the car the player is in. A patrol car signals them to stop; a car that keeps going is fleeing (custody, fines, licence points). A crash starts `PolicePursuit` | The car driving itself would flee | `OwnCarDrive.Trouble` ends the trip while `PoliceAttention.IsBusy`, with a notice. The car stops on its handbrake and the player has the wheel |
| RLO's driving offences (red lights, wrong side, speeding 45 km/h over) | Count during a trip in the own car, as for any driver | Nothing. With "stop at red lights" off the car runs reds and RLO may book them |
| `Transport/CarOwnership` breakdowns cut the throttle through `inputModifyCallback` | A broken-down car would still drive itself | `OwnCarTaxi.Open` refuses, and a breakdown mid-trip ends it (`CarOwnership._cutting` for this car) |
| Wear, kilometres, fuel by kind, potholes | Count normally; worn tyres and brakes stay on the car | Nothing. A thirsty car can run dry mid-trip, which `Trouble` already handles |
| `Crime/PlayerOffences.OnFoot` books a player walking in the road | The hidden passenger rides down the carriageway | Prefix: while `Ride.OwnsPlayer`, `now` is set before RLO's next look, so it answers no offence |
| Mouse look and the chase camera (on by default) lock the cursor unless `ThirdPersonCamera.IsUiOpen()` | The ride panel can't be clicked | Postfix: `IsUiOpen` is true while the ride panel shows |
| Photo mode rolls the camera on L/J | L would call the driver or open the map | The shortcut does nothing while `Leisure.PhotoMode.Active` |
| The police, a driving test (`LicenceDirector.IsActive`) or prison hold the player (RLO's own test, `Wiring/TaxiLinks`) | L would self-drive the driving-school car, or call a driver in custody | The shortcut refuses with "Not now." |
| City Cabs (`Transport/Taxi`) takes a pool taxi out of Gley's hands (`SetVehicleIgnored`, an empty `presetPath`) and drives it to the caller | Gley's target waypoint for the cab is still where it was loaded, 250 to 650 m away, so the ride's line would start there | `Ride.TryCreate`: a taxi Gley ignores starts at the lane waypoint just ahead of it. A Gley ride (car physics off) leaves it to the base game, since Gley would have to drive the cab again |

## Checked as harmless

RLO has no Harmony patches of its own. Its calls to `TimeMachine.StartTimeMachine` and `CarController.ExitVehicle` (custody, hospital, impound, transit, outings) go through this mod's prefixes, which end a ride first, as intended. `BridgeHold` keeps only the bridge under the player visible. RLO's traffic tools and accidents skip taxis, private drivers and cars with a `presetPath`, which covers every ride car. City Cabs charges its call-out when `TaxiSystem.IsTraveling` turns on, as for a normal ride. No other RLO key uses L.

## Not handled

- The Garage's parts (RLO's custom shop: springs, brakes, tyres, weight) stay on the car during a trip. They are part of the car the player built.
- RLO's driving offences during an own-car trip are the player's, as above.
