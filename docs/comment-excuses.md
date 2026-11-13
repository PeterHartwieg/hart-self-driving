# Comment excuses

Code comments are banned. The comments below stayed because each one admits a workaround, a known gap or an imperfect choice. Each line is a seed for a future issue: fix the code or file the issue, then delete the comment and its line here.

`tests/CommentBan.cs` reads every line that starts with `` - `path:line` `` or `` - `path:first-last` `` in this file. Consecutive own-line comments count as one comment, and its entry names every line it covers. A comment passes only when its exact lines are listed, so a line added to a listed comment fails `CommentBanTests` until the entry is changed. Any other comment fails, and so does a listed entry that no comment matches exactly. Line numbers move when code above them changes, so update the entry in the same commit.

Directives need no entry: C# preprocessor lines (`#if`, `#pragma`, `#region`), a shebang on line 1, Python `# noqa`, `# type:`, `# pylint:` and coding lines, `# shellcheck` and PowerShell `#Requires`. A trailing `//` on a C# directive line is still a comment.

The scanner covers git-tracked and untracked, non-ignored `.cs`, `.py`, `.sh`, `.ps1`, `.csproj`, `.slnx`, `.gitignore`, `tools/mod.conf` and the extensionless `agent` and `mac-agent` scripts. It skips `docs/` and `workshop/`. Python docstrings are string literals and the scanner does not read them. Four module docstrings stay because argparse prints them as help. One more docstring is an excuse, and since the scanner cannot check it, it is listed here in prose: `tools/package-mod.py`, the `placeholder_png` docstring (temporary).

Categories: workaround, known-gap, todo, temporary, skipped, imperfect-choice.

## Core

- `Core/Drive/CarModel.cs:5-9` known-gap
- `Core/Drive/CarSpec.cs:47-51` imperfect-choice
- `Core/Drive/Corridor.cs:25-26` imperfect-choice
- `Core/Drive/Corridor.cs:62` imperfect-choice
- `Core/Drive/Corridor.cs:168` imperfect-choice
- `Core/Drive/Feasibility.cs:58-59` known-gap
- `Core/Drive/Feasibility.cs:82-83` workaround
- `Core/Drive/Feasibility.cs:85` temporary
- `Core/Drive/Feasibility.cs:88-90` workaround
- `Core/Drive/Feasibility.cs:464-466` workaround
- `Core/Drive/Follower.cs:23-27` workaround
- `Core/Drive/JunctionRules.cs:10-13` imperfect-choice
- `Core/Drive/JunctionRules.cs:46` workaround
- `Core/Drive/PursuitDriver.cs:235-236` imperfect-choice
- `Core/Drive/PursuitDriver.cs:249` workaround
- `Core/Drive/Traffic.cs:23-25` workaround
- `Core/Drive/Traffic.cs:72-74` imperfect-choice
- `Core/Drive/TrafficDriver.Approach.cs:7` imperfect-choice
- `Core/Drive/TrafficDriver.Approach.cs:189-190` imperfect-choice
- `Core/Drive/TrafficDriver.Conflicts.cs:7` imperfect-choice
- `Core/Drive/TrafficDriver.Recovery.cs:7` imperfect-choice
- `Core/Drive/TrafficDriver.Recovery.cs:10` imperfect-choice
- `Core/Drive/TrafficDriver.Sweep.cs:7` imperfect-choice
- `Core/Drive/TrafficDriver.Sweep.cs:222-224` imperfect-choice
- `Core/Drive/TrafficDriver.cs:17-18` known-gap
- `Core/Drive/TrafficDriver.cs:179-180` workaround
- `Core/Drive/TrafficDriver.cs:420-422` known-gap
- `Core/Drive/TrafficDriver.cs:532-536` workaround
- `Core/Drive/TrafficMargins.cs:3-7` imperfect-choice
- `Core/Drive/TrafficWarmUp.cs:8-11` workaround
- `Core/Drive/WheelPlane.cs:7-12` known-gap
- `Core/Parking/ManoeuvreDriver.cs:76-77` imperfect-choice
- `Core/Parking/ManoeuvreDriver.cs:119-120` workaround
- `Core/Ride/RouteLinePath.cs:16-18` workaround
- `Core/Ride/TurnArc.cs:74-76` workaround
- `Core/Ride/TurnArc.cs:81-83` workaround
- `Core/Routing/JunctionBoxes.cs:6-12` workaround
- `Core/Routing/JunctionBoxes.cs:26-27` workaround
- `Core/Routing/LaneGraph.cs:82-84` workaround
- `Core/Routing/LaneGraph.cs:297-298` workaround
- `Core/Routing/LaneGraph.cs:425-426` workaround
- `Core/Routing/RoutePlanner.cs:20-21` imperfect-choice
- `Core/Testing/Catalogue.cs:47-48` skipped
- `Core/Testing/Catalogue.cs:173` workaround
- `Core/Testing/Judge.cs:17-19` workaround
- `Core/Testing/Judge.cs:22-23` workaround
- `Core/Testing/Judge.cs:180-183` imperfect-choice
- `Core/Testing/LaneStretch.cs:36-37` workaround
- `Core/Testing/Pavement.cs:7-15` workaround
- `Core/Testing/Pavement.cs:21-22` temporary
- `Core/Testing/TrafficVerdict.cs:15-20` imperfect-choice

## tests

- `tests/FeasibilityTests.cs:214` imperfect-choice
- `tests/GameSpeedTests.cs:16-17` imperfect-choice
- `tests/PursuitDriverTests.cs:47-48` imperfect-choice
- `tests/ReviewRound2Tests.cs:157` workaround
- `tests/ReviewRound7Tests.cs:104` known-gap
- `tests/ReviewRound9Tests.cs:41-43` imperfect-choice
- `tests/TrafficSim.cs:221-222` imperfect-choice
- `tests/TrafficSim.cs:375-376` workaround
- `tests/TrafficSim.cs:483-484` workaround
- `tests/TrafficSim.cs:702-705` imperfect-choice
- `tests/TrafficSim.cs:743-744` workaround
- `tests/TrafficSim.cs:746-747` workaround
- `tests/TrafficTests.cs:89-95` known-gap
- `tests/TrafficTests.cs:102-104` imperfect-choice
- `tests/TrafficTests.cs:107-110` imperfect-choice
- `tests/TrafficTests.cs:203-204` known-gap
- `tests/TrafficTests.cs:215-219` known-gap

## tools

- `tools/Park-Windows.ps1:1-2` workaround
- `tools/agent-tests/Patcher/Program.cs:75` workaround
- `tools/agent-tests/Patcher/Program.cs:109-110` workaround
- `tools/agent-tests/install.py:28` workaround
- `tools/agent-tests/mac-agent:40-41` workaround
- `tools/calibrate/Program.cs:141-142` imperfect-choice
- `tools/calibrate/Program.cs:174` skipped
- `tools/calibrate/Program.cs:292` workaround
- `tools/package-mod.py:178-179` workaround
- `tools/rejudge/Program.cs:167-168` imperfect-choice
- `tools/tested-steps.py:16-17` known-gap

## unity/BuildEditor

- `unity/BuildEditor/DriveChecks.cs:32-33` workaround
- `unity/BuildEditor/RouteLineChecks.cs:31` workaround
- `unity/BuildEditor/RouteLineChecks.cs:62` imperfect-choice
- `unity/BuildEditor/RouteLineChecks.cs:99` workaround

## unity/DrivenRides

- `unity/DrivenRides/Scripts/DrivableSteering.cs:12-15` workaround
- `unity/DrivenRides/Scripts/DrivenRidesMod.cs:27-28` workaround
- `unity/DrivenRides/Scripts/GleyGraph.cs:127-128` workaround
- `unity/DrivenRides/Scripts/GleyGraph.cs:154-155` workaround
- `unity/DrivenRides/Scripts/GleyTraffic.cs:59-62` workaround
- `unity/DrivenRides/Scripts/Log.cs:26-27` imperfect-choice
- `unity/DrivenRides/Scripts/NwhCar.cs:116-118` workaround
- `unity/DrivenRides/Scripts/NwhCar.cs:124-127` workaround
- `unity/DrivenRides/Scripts/NwhDress.cs:274-277` workaround
- `unity/DrivenRides/Scripts/NwhDrive.cs:210-211` workaround
- `unity/DrivenRides/Scripts/NwhDrive.cs:656-658` known-gap
- `unity/DrivenRides/Scripts/NwhManoeuvre.cs:350-351` workaround
- `unity/DrivenRides/Scripts/NwhPlanner.cs:112-116` imperfect-choice
- `unity/DrivenRides/Scripts/NwhPlanner.cs:217-220` workaround
- `unity/DrivenRides/Scripts/NwhPlanner.cs:296-305` workaround
- `unity/DrivenRides/Scripts/NwhPlanner.cs:366-367` workaround
- `unity/DrivenRides/Scripts/NwhPlanner.cs:369-371` workaround
- `unity/DrivenRides/Scripts/NwhPlanner.cs:383-387` workaround
- `unity/DrivenRides/Scripts/NwhPlanner.cs:514-515` known-gap
- `unity/DrivenRides/Scripts/NwhPlanner.cs:520-522` known-gap
- `unity/DrivenRides/Scripts/NwhPlanner.cs:563-564` skipped
- `unity/DrivenRides/Scripts/Options.cs:16` workaround
- `unity/DrivenRides/Scripts/Options.cs:18` workaround
- `unity/DrivenRides/Scripts/Options.cs:50-51` known-gap
- `unity/DrivenRides/Scripts/OwnCar.cs:49-52` workaround
- `unity/DrivenRides/Scripts/OwnCar.cs:122-124` known-gap
- `unity/DrivenRides/Scripts/OwnCar.cs:332-333` workaround
- `unity/DrivenRides/Scripts/OwnCar.cs:502-503` workaround
- `unity/DrivenRides/Scripts/ParkingSurface.cs:11-12` workaround
- `unity/DrivenRides/Scripts/Patches.cs:33` workaround
- `unity/DrivenRides/Scripts/Patches.cs:35` workaround
- `unity/DrivenRides/Scripts/Ride.Bridge.cs:41` known-gap
- `unity/DrivenRides/Scripts/Ride.GleyDriving.cs:143-147` workaround
- `unity/DrivenRides/Scripts/Ride.GleyDriving.cs:159-160` workaround
- `unity/DrivenRides/Scripts/Ride.GleyDriving.cs:348` workaround
- `unity/DrivenRides/Scripts/Ride.GleyDriving.cs:417-421` workaround
- `unity/DrivenRides/Scripts/Ride.Planning.cs:579` workaround
- `unity/DrivenRides/Scripts/Ride.Planning.cs:592-594` workaround
- `unity/DrivenRides/Scripts/Ride.Planning.cs:615` workaround
- `unity/DrivenRides/Scripts/Ride.Planning.cs:628-629` workaround
- `unity/DrivenRides/Scripts/Ride.cs:336-337` workaround
- `unity/DrivenRides/Scripts/Ride.cs:344-345` workaround
- `unity/DrivenRides/Scripts/Ride.cs:459-460` workaround
- `unity/DrivenRides/Scripts/Ride.cs:692-697` workaround
- `unity/DrivenRides/Scripts/Ride.cs:700-701` workaround
- `unity/DrivenRides/Scripts/Ride.cs:712-713` workaround
- `unity/DrivenRides/Scripts/Ride.cs:839` workaround
- `unity/DrivenRides/Scripts/Ride.cs:887-890` workaround
- `unity/DrivenRides/Scripts/Ride.cs:910-911` workaround
- `unity/DrivenRides/Scripts/Ride.cs:953-954` workaround
- `unity/DrivenRides/Scripts/Ride.cs:1087` workaround
- `unity/DrivenRides/Scripts/Ride.cs:1098-1100` workaround
- `unity/DrivenRides/Scripts/RideRadio.cs:42-43` workaround
- `unity/DrivenRides/Scripts/Sidewalk.cs:24-25` workaround
- `unity/DrivenRides/Scripts/TestedSteps.cs:6-9` known-gap
- `unity/DrivenRides/Scripts/TurnTests.cs:202` imperfect-choice
- `unity/DrivenRides/Scripts/TurnTests.cs:954-955` workaround
- `unity/DrivenRides/Scripts/TurnTests.cs:974` workaround
