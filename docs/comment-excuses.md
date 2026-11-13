# Comment excuses

Code comments are banned. The comments below stayed because each one admits a workaround, a known gap or an imperfect choice. Each line is a seed for a future issue: fix the code or file the issue, then delete the comment and its line here.

`tests/CommentBan.cs` reads every line that starts with `` - `path:line` `` in this file. A comment that starts on a listed line passes. Any other comment fails `CommentBanTests`, and so does a listed line where no comment starts. Line numbers move when code above them changes, so update the entry in the same commit.

Directives need no entry: C# preprocessor lines (`#if`, `#pragma`, `#region`), a shebang on line 1, Python `# noqa`, `# type:`, `# pylint:` and coding lines, `# shellcheck` and PowerShell `#Requires`. A trailing `//` on a C# directive line is still a comment.

The scanner covers git-tracked and untracked, non-ignored `.cs`, `.py`, `.sh`, `.ps1`, `.csproj`, `.slnx`, `.gitignore`, `tools/mod.conf` and the extensionless `agent` and `mac-agent` scripts. It skips `docs/` and `workshop/`. Python docstrings are string literals and the scanner does not read them. Four module docstrings stay because argparse prints them as help. One more docstring is an excuse, and since the scanner cannot check it, it is listed here in prose: `tools/package-mod.py`, the `placeholder_png` docstring (temporary).

Categories: workaround, known-gap, todo, temporary, skipped, imperfect-choice.

## Core

- `Core/Drive/CarModel.cs:5` known-gap
- `Core/Drive/CarSpec.cs:47` imperfect-choice
- `Core/Drive/Corridor.cs:25` imperfect-choice
- `Core/Drive/Corridor.cs:62` imperfect-choice
- `Core/Drive/Corridor.cs:168` imperfect-choice
- `Core/Drive/Feasibility.cs:58` known-gap
- `Core/Drive/Feasibility.cs:82` workaround
- `Core/Drive/Feasibility.cs:85` temporary
- `Core/Drive/Feasibility.cs:88` workaround
- `Core/Drive/Feasibility.cs:464` workaround
- `Core/Drive/Follower.cs:23` workaround
- `Core/Drive/JunctionRules.cs:10` imperfect-choice
- `Core/Drive/JunctionRules.cs:46` workaround
- `Core/Drive/PursuitDriver.cs:235` imperfect-choice
- `Core/Drive/PursuitDriver.cs:249` workaround
- `Core/Drive/Traffic.cs:23` workaround
- `Core/Drive/Traffic.cs:72` imperfect-choice
- `Core/Drive/TrafficDriver.Approach.cs:7` imperfect-choice
- `Core/Drive/TrafficDriver.Approach.cs:189` imperfect-choice
- `Core/Drive/TrafficDriver.Conflicts.cs:7` imperfect-choice
- `Core/Drive/TrafficDriver.Recovery.cs:7` imperfect-choice
- `Core/Drive/TrafficDriver.Recovery.cs:10` imperfect-choice
- `Core/Drive/TrafficDriver.Sweep.cs:7` imperfect-choice
- `Core/Drive/TrafficDriver.Sweep.cs:222` imperfect-choice
- `Core/Drive/TrafficDriver.cs:17` known-gap
- `Core/Drive/TrafficDriver.cs:179` workaround
- `Core/Drive/TrafficDriver.cs:420` known-gap
- `Core/Drive/TrafficDriver.cs:532` workaround
- `Core/Drive/TrafficMargins.cs:3` imperfect-choice
- `Core/Drive/TrafficWarmUp.cs:8` workaround
- `Core/Drive/WheelPlane.cs:7` known-gap
- `Core/Parking/ManoeuvreDriver.cs:76` imperfect-choice
- `Core/Parking/ManoeuvreDriver.cs:119` workaround
- `Core/Ride/RouteLinePath.cs:16` workaround
- `Core/Ride/TurnArc.cs:74` workaround
- `Core/Ride/TurnArc.cs:81` workaround
- `Core/Routing/JunctionBoxes.cs:6` workaround
- `Core/Routing/JunctionBoxes.cs:26` workaround
- `Core/Routing/LaneGraph.cs:82` workaround
- `Core/Routing/LaneGraph.cs:297` workaround
- `Core/Routing/LaneGraph.cs:425` workaround
- `Core/Routing/RoutePlanner.cs:20` imperfect-choice
- `Core/Testing/Catalogue.cs:47` skipped
- `Core/Testing/Catalogue.cs:173` workaround
- `Core/Testing/Judge.cs:17` workaround
- `Core/Testing/Judge.cs:22` workaround
- `Core/Testing/Judge.cs:180` imperfect-choice
- `Core/Testing/LaneStretch.cs:36` workaround
- `Core/Testing/Pavement.cs:7` workaround
- `Core/Testing/Pavement.cs:21` temporary
- `Core/Testing/TrafficVerdict.cs:15` imperfect-choice

## tests

- `tests/FeasibilityTests.cs:214` imperfect-choice
- `tests/GameSpeedTests.cs:16` imperfect-choice
- `tests/PursuitDriverTests.cs:47` imperfect-choice
- `tests/ReviewRound2Tests.cs:157` workaround
- `tests/ReviewRound7Tests.cs:104` known-gap
- `tests/ReviewRound9Tests.cs:41` imperfect-choice
- `tests/TrafficSim.cs:221` imperfect-choice
- `tests/TrafficSim.cs:375` workaround
- `tests/TrafficSim.cs:483` workaround
- `tests/TrafficSim.cs:702` imperfect-choice
- `tests/TrafficSim.cs:743` workaround
- `tests/TrafficSim.cs:746` workaround
- `tests/TrafficTests.cs:89` known-gap
- `tests/TrafficTests.cs:102` imperfect-choice
- `tests/TrafficTests.cs:107` imperfect-choice
- `tests/TrafficTests.cs:203` known-gap
- `tests/TrafficTests.cs:215` known-gap

## tools

- `tools/Park-Windows.ps1:1` workaround
- `tools/agent-tests/Patcher/Program.cs:75` workaround
- `tools/agent-tests/Patcher/Program.cs:109` workaround
- `tools/agent-tests/install.py:28` workaround
- `tools/agent-tests/mac-agent:40` workaround
- `tools/calibrate/Program.cs:141` imperfect-choice
- `tools/calibrate/Program.cs:174` skipped
- `tools/calibrate/Program.cs:292` workaround
- `tools/package-mod.py:178` workaround
- `tools/rejudge/Program.cs:167` imperfect-choice
- `tools/tested-steps.py:16` known-gap

## unity/BuildEditor

- `unity/BuildEditor/DriveChecks.cs:32` workaround
- `unity/BuildEditor/RouteLineChecks.cs:31` workaround
- `unity/BuildEditor/RouteLineChecks.cs:62` imperfect-choice
- `unity/BuildEditor/RouteLineChecks.cs:99` workaround

## unity/DrivenRides

- `unity/DrivenRides/Scripts/DrivableSteering.cs:12` workaround
- `unity/DrivenRides/Scripts/DrivenRidesMod.cs:27` workaround
- `unity/DrivenRides/Scripts/GleyGraph.cs:127` workaround
- `unity/DrivenRides/Scripts/GleyGraph.cs:154` workaround
- `unity/DrivenRides/Scripts/GleyTraffic.cs:59` workaround
- `unity/DrivenRides/Scripts/Log.cs:26` imperfect-choice
- `unity/DrivenRides/Scripts/NwhCar.cs:116` workaround
- `unity/DrivenRides/Scripts/NwhCar.cs:124` workaround
- `unity/DrivenRides/Scripts/NwhDress.cs:274` workaround
- `unity/DrivenRides/Scripts/NwhDrive.cs:210` workaround
- `unity/DrivenRides/Scripts/NwhDrive.cs:656` known-gap
- `unity/DrivenRides/Scripts/NwhManoeuvre.cs:350` workaround
- `unity/DrivenRides/Scripts/NwhPlanner.cs:112` imperfect-choice
- `unity/DrivenRides/Scripts/NwhPlanner.cs:217` workaround
- `unity/DrivenRides/Scripts/NwhPlanner.cs:296` workaround
- `unity/DrivenRides/Scripts/NwhPlanner.cs:366` workaround
- `unity/DrivenRides/Scripts/NwhPlanner.cs:369` workaround
- `unity/DrivenRides/Scripts/NwhPlanner.cs:383` workaround
- `unity/DrivenRides/Scripts/NwhPlanner.cs:514` known-gap
- `unity/DrivenRides/Scripts/NwhPlanner.cs:520` known-gap
- `unity/DrivenRides/Scripts/NwhPlanner.cs:563` skipped
- `unity/DrivenRides/Scripts/Options.cs:16` workaround
- `unity/DrivenRides/Scripts/Options.cs:18` workaround
- `unity/DrivenRides/Scripts/Options.cs:50` known-gap
- `unity/DrivenRides/Scripts/OwnCar.cs:49` workaround
- `unity/DrivenRides/Scripts/OwnCar.cs:122` known-gap
- `unity/DrivenRides/Scripts/OwnCar.cs:332` workaround
- `unity/DrivenRides/Scripts/OwnCar.cs:502` workaround
- `unity/DrivenRides/Scripts/ParkingSurface.cs:11` workaround
- `unity/DrivenRides/Scripts/Patches.cs:33` workaround
- `unity/DrivenRides/Scripts/Patches.cs:35` workaround
- `unity/DrivenRides/Scripts/Ride.Bridge.cs:41` known-gap
- `unity/DrivenRides/Scripts/Ride.GleyDriving.cs:143` workaround
- `unity/DrivenRides/Scripts/Ride.GleyDriving.cs:159` workaround
- `unity/DrivenRides/Scripts/Ride.GleyDriving.cs:348` workaround
- `unity/DrivenRides/Scripts/Ride.GleyDriving.cs:417` workaround
- `unity/DrivenRides/Scripts/Ride.Planning.cs:557` workaround
- `unity/DrivenRides/Scripts/Ride.Planning.cs:570` workaround
- `unity/DrivenRides/Scripts/Ride.Planning.cs:582` workaround
- `unity/DrivenRides/Scripts/Ride.Planning.cs:595` workaround
- `unity/DrivenRides/Scripts/Ride.cs:336` workaround
- `unity/DrivenRides/Scripts/Ride.cs:344` workaround
- `unity/DrivenRides/Scripts/Ride.cs:459` workaround
- `unity/DrivenRides/Scripts/Ride.cs:692` workaround
- `unity/DrivenRides/Scripts/Ride.cs:700` workaround
- `unity/DrivenRides/Scripts/Ride.cs:712` workaround
- `unity/DrivenRides/Scripts/Ride.cs:839` workaround
- `unity/DrivenRides/Scripts/Ride.cs:887` workaround
- `unity/DrivenRides/Scripts/Ride.cs:910` workaround
- `unity/DrivenRides/Scripts/Ride.cs:953` workaround
- `unity/DrivenRides/Scripts/Ride.cs:1087` workaround
- `unity/DrivenRides/Scripts/Ride.cs:1098` workaround
- `unity/DrivenRides/Scripts/RideRadio.cs:42` workaround
- `unity/DrivenRides/Scripts/Sidewalk.cs:24` workaround
- `unity/DrivenRides/Scripts/TestedSteps.cs:6` known-gap
- `unity/DrivenRides/Scripts/TurnTests.cs:202` imperfect-choice
- `unity/DrivenRides/Scripts/TurnTests.cs:954` workaround
- `unity/DrivenRides/Scripts/TurnTests.cs:974` workaround
