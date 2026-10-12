#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Buildings;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;
using DrivenRides.Core.Testing;
using GleyTrafficSystem;
using HarmonyLib;
using Helpers;
using Newtonsoft.Json.Linq;
using PlayerActivity;
using UI;
using UnityEngine;
using UnityEngine.AI;
using Vehicles.VehicleTypes;
using Object = UnityEngine.Object;

namespace DrivenRides
{
    // The turn test rig (docs/turn-tests.md). Commands arrive as files in <persistentDataPath>/DrivenRides/tests:
    // command.txt (its last line "end", so a half-copied file waits) and stop.txt. The session holds the player on an
    // anchor that follows each test car, holds the clock, switches traffic and pedestrians off, and drives each case
    // with the ride's own code in test mode. Results: status.json, results-<session>.jsonl, <run>.png on failures.
    internal sealed class TurnTests : MonoBehaviour
    {
        private static TurnTests? instance;
        private static Session? session;
        private IEnumerator? routine;
        private float nextPoll;
        private float nextStatus;

        public static bool Running => session != null;

        // The ride's per-frame logic (Gley's waypoint switching, the turn speed) needs frames: a 3x ride at 60 fps has
        // 0.05 s of game time per frame, so an automatic speed keeps to that and runs faster on a faster PC.
        private const float MaxSpeed = 8f;
        private const float GameSecondsPerFrame = 0.05f;
        private float frameSeconds = 1f / 60f;

        private static string Folder => Path.Combine(GleyGraph.DataFolder, "tests");

        private sealed class CarModel
        {
            public string Name = "";
            public GameObject Prefab = null!;
            public int TopSpeed;
            public float Length, Width, TurnRadius;
            public bool Taxi;
        }

        private sealed class Session
        {
            public string Id = "";
            public Vector3 StartPosition;
            public Quaternion StartRotation;
            public GameObject? AnchorObject;
            public RideAnchor? Anchor;
            public PlayerController? Player;
            public GameManager? Manager;
            public bool Attached;
            public bool TrafficWas = true;
            public bool PedestriansSpawningWas = true;
            public int PedestriansWere;
            public bool StopRequested;
            public string ResultsFile = "";
            public int Total, Done, Passed, Failed, Other;
            public string Current = "";
            public string? Calibrated;
            public Ride? Ride;
            public TestCar? Car;
            public CaseGeometry? Geometry;
            public LaneGraph? Graph;
            public IReadOnlyList<int>? DrivenPath;
            public string RunId = "";
            public float Started;
            public bool AutoSpeed;
            public double SpeedSum;
            public int SpeedFrames;
            public int VSyncWas;
            public int FrameRateWas;
        }

        private void OnEnable()
        {
            instance = this;
            try { Directory.CreateDirectory(Folder); }
            catch (Exception ex) { Log.Error("Turn tests folder: " + ex.Message); }
        }

        private void OnDisable()
        {
            if (instance == this) instance = null;
        }

        private void Update()
        {
            if (Time.unscaledTime >= nextPoll)
            {
                nextPoll = Time.unscaledTime + 1f;
                try { Poll(); }
                catch (Exception ex) { Log.Error("Turn tests command: " + ex); }
            }
            frameSeconds = Mathf.Lerp(frameSeconds, Mathf.Clamp(Time.unscaledDeltaTime, 0.002f, 0.2f), 0.05f);
            if (session != null && session.AutoSpeed && session.Ride != null)
            {
                var auto = Mathf.Clamp(GameSecondsPerFrame / frameSeconds, 1f, MaxSpeed);
                session.Ride.SetTestSpeed(auto);
                session.SpeedSum += auto;
                session.SpeedFrames++;
            }
            if (session != null && Time.unscaledTime >= nextStatus)
            {
                nextStatus = Time.unscaledTime + 2f;
                WriteStatus("running");
            }
            if (routine == null) return;
            try
            {
                if (!routine.MoveNext()) routine = null;
            }
            catch (Exception ex)
            {
                Log.Error("Turn tests: " + ex);
                routine = null;
                Teardown("an exception: " + ex.Message, handedOver: false);
            }
        }

        // The failure picture, in the frame the judge latched the failure, before the car moves on.
        private void LateUpdate()
        {
            var s = session;
            if (s?.Car == null || !s.Car.PictureDue) return;
            s.Car.PictureDue = false;
            try { Picture(s); }
            catch (Exception ex) { Log.Error("Turn tests picture: " + ex.Message); }
        }

        // ---- Commands ----

        private void Poll()
        {
            var stop = Path.Combine(Folder, "stop.txt");
            if (File.Exists(stop))
            {
                File.Delete(stop);
                if (session != null) { session.StopRequested = true; Log.Info("Turn tests: stop requested."); }
            }
            var file = Path.Combine(Folder, "command.txt");
            if (!File.Exists(file)) return;
            var lines = File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            if (lines.Count == 0 || lines[lines.Count - 1] != "end") return;
            // A run sent while one is going waits in its file until the session ends.
            if ((session != null || routine != null) && (lines[0].StartsWith("run", StringComparison.Ordinal) || lines[0].StartsWith("rerun", StringComparison.Ordinal))) return;
            File.Delete(file);
            var words = lines[0].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return;
            var args = new Dictionary<string, string>();
            var rest = new List<string>();
            foreach (var w in words.Skip(1))
            {
                var eq = w.IndexOf('=');
                if (eq > 0) args[w.Substring(0, eq)] = w.Substring(eq + 1);
                else rest.Add(w);
            }
            Log.Info("Turn tests command: " + lines[0]);
            switch (words[0])
            {
                case "cars": WriteCars(); break;
                case "catalogue": WriteCatalogue(); break;
                case "run": Start(args, null); break;
                case "rerun": if (rest.Count > 0) Start(args, rest[0]); break;
                case "stop": if (session != null) session.StopRequested = true; break;
                default: Log.Error("Turn tests: unknown command " + words[0]); break;
            }
        }

        private void Start(Dictionary<string, string> args, string? rerunFile)
        {
            if (session != null || routine != null) { Log.Error("Turn tests: a session is already running."); return; }
            routine = RunSession(args, rerunFile);
        }

        // ---- Cars ----

        private static readonly FieldInfo? TrafficVehiclesField = AccessTools.Field(typeof(TrafficManager), "trafficVehicles");
        private static readonly FieldInfo? VehicleTypesField = AccessTools.Field(typeof(VehicleTypeHelper), "VehicleTypes");

        private static List<VehicleComponent> Pool() =>
            (TrafficVehiclesField?.GetValue(TrafficManager.Instance) as TrafficVehicles)?.GetVehicleList() ?? new List<VehicleComponent>();

        // The models that ride: each vehicle type whose traffic prefab is in Gley's pool, and the taxi.
        private static List<CarModel> Models()
        {
            var pool = Pool().Where(v => v != null && v.prefab != null).ToList();
            var models = new List<CarModel>();
            CarModel? Make(string name, GameObject prefab, int top, bool taxi)
            {
                var copy = pool.FirstOrDefault(v => v.prefab == prefab) ?? pool.FirstOrDefault(v => v.prefab.name == prefab.name);
                // Gley's trucks (vans, box trucks) are no ride's car.
                if (copy == null || copy.vehicleType != VehicleTypes.Car) return null;
                var steer = Mathf.Clamp(copy.maxSteer, 5f, 60f);
                return new CarModel
                {
                    Name = name, Prefab = copy.prefab, TopSpeed = top, Taxi = taxi,
                    Length = copy.length, Width = copy.colliderWidth,
                    TurnRadius = copy.wheelDistance / Mathf.Tan(steer * Mathf.Deg2Rad),
                };
            }
            if (VehicleTypesField?.GetValue(null) is Dictionary<string, VehicleType> types)
                foreach (var type in types.Values)
                {
                    if (type == null || string.IsNullOrEmpty(type.vehicleTypeName)) continue;
                    var at = type.vehicleTypeName.IndexOf("vehicletype_", StringComparison.Ordinal);
                    var id = at >= 0 ? type.vehicleTypeName.Substring(at + "vehicletype_".Length) : type.vehicleTypeName;
                    var prefab = PrefabHelper.LoadPrefabAssetByName("Vehicles/" + id);
                    if (prefab == null || prefab.GetComponent<VehicleComponent>() == null) continue;
                    var model = Make(type.vehicleTypeName, prefab, type.maxSpeed, false);
                    if (model != null && models.All(m => m.Prefab != model.Prefab)) models.Add(model);
                }
            var taxiPrefab = pool.Select(v => v.prefab).FirstOrDefault(p => p.GetComponent<TaxiController>() != null);
            if (taxiPrefab != null)
            {
                var taxi = Make("taxi", taxiPrefab, 0, true);
                if (taxi != null) models.Add(taxi);
            }
            return models;
        }

        private static List<CarModel> Choose(List<CarModel> models, string which)
        {
            if (models.Count == 0) return models;
            if (which == "all") return models;
            if (which != "default")
                return models.Where(m => which.Split(',').Any(n => m.Name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            var chosen = new List<CarModel>();
            void Add(CarModel? m) { if (m != null && !chosen.Contains(m)) chosen.Add(m); }
            Add(models.Where(m => !m.Taxi).OrderByDescending(m => m.TurnRadius).FirstOrDefault());
            Add(models.Where(m => !m.Taxi).OrderByDescending(m => m.Length).FirstOrDefault());
            Add(models.FirstOrDefault(m => m.Taxi));
            return chosen;
        }

        private static void WriteCars()
        {
            var lines = Models().Select(m => new JObject
            {
                ["name"] = m.Name, ["prefab"] = m.Prefab.name, ["taxi"] = m.Taxi, ["topSpeed"] = m.TopSpeed,
                ["length"] = Round(m.Length), ["width"] = Round(m.Width), ["turnRadius"] = Round(m.TurnRadius),
            }.ToString(Newtonsoft.Json.Formatting.None));
            File.WriteAllLines(Path.Combine(Folder, "cars.jsonl"), lines);
            Log.Info("Turn tests: cars written.");
        }

        private static double Round(float v) => Math.Round(v, 2);

        // ---- Catalogue ----

        private static CatalogueOptions CatalogueSettings()
        {
            var zones = new List<Bounds>();
            foreach (var bridge in Object.FindObjectsOfType<Streets.BridgeTriggerController>())
            {
                var c = bridge != null ? bridge.GetComponent<Collider>() : null;
                if (c == null) continue;
                var b = c.bounds;
                b.Expand(40f);
                zones.Add(b);
            }
            return new CatalogueOptions { NearBridge = p => zones.Any(z => z.Contains(new Vector3(p.X, p.Y, p.Z))) };
        }

        private static void WriteCatalogue()
        {
            var graph = GleyGraph.Graph;
            if (graph == null) { Log.Error("Turn tests: no lane graph."); return; }
            var all = Catalogue.Build(graph, CatalogueSettings());
            File.WriteAllLines(Path.Combine(Folder, "catalogue.jsonl"), all.Select(f => f.ToJson()));
            Log.Info("Turn tests catalogue: " + string.Join(", ", all.GroupBy(f => f.Kind).Select(g => $"{g.Key} {g.Count()}")) + ".");
        }

        private static List<Fixture> RideCases(LaneGraph graph, string lines, CatalogueOptions options)
        {
            var result = new List<Fixture>();
            var file = Path.Combine(GleyGraph.DataFolder, "rides.jsonl");
            if (!File.Exists(file)) return result;
            var all = File.ReadAllLines(file);
            foreach (var part in lines.Split(','))
            {
                if (!int.TryParse(part, out var line) || line < 1 || line > all.Length) continue;
                var route = JObject.Parse(all[line - 1])["route"]?.Select(t => (int)t).ToList();
                if (route != null) result.AddRange(Catalogue.FromRide(graph, route, line, options));
            }
            return result;
        }

        // ---- The session ----

        private IEnumerator RunSession(Dictionary<string, string> args, string? rerunFile)
        {
            var graph = GleyGraph.Graph;
            if (graph == null || !TrafficManager.HasInstance || !TrafficManager.IsInitialized) { Refuse("traffic is not ready"); yield break; }
            // speed=auto: the speed follows the frame rate (AutoSpeed); a number is a fixed speed.
            var speed = args.TryGetValue("speed", out var sp) ? sp == "auto" ? 0f : float.TryParse(sp, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? Mathf.Clamp(v, 1f, MaxSpeed) : 3f : 3f;
            var options = CatalogueSettings();
            var catalogue = Catalogue.Build(graph, options);
            var models = Models();
            var plan = new List<(Fixture Fixture, CarModel Car)>();
            List<CarModel>? followUp = null;
            static IEnumerable<string> followUpNames(List<(Fixture Fixture, CarModel Car)> p, int from) => p.Skip(from).Select(x => x.Car.Name).Distinct();
            if (rerunFile != null)
            {
                var path = Path.Combine(Folder, Path.GetFileName(rerunFile));
                if (!File.Exists(path)) { Refuse("no file " + rerunFile); yield break; }
                var byId = catalogue.Concat(Controls.Build(graph, catalogue)).GroupBy(f => f.Id).ToDictionary(g => g.Key, g => g.First());
                foreach (var line in File.ReadAllLines(path))
                {
                    var o = JObject.Parse(line);
                    if ((string?)o["verdict"] != "fail") continue;
                    var id = (string?)o["case"];
                    var car = models.FirstOrDefault(m => m.Name == (string?)o["car"]);
                    if (id == null || car == null) continue;
                    if (byId.TryGetValue(id, out var f)) plan.Add((f, car));
                    else if ((string?)o["kind"] == "Ride" && o["rideLine"] != null)
                        foreach (var r in RideCases(graph, ((int)o["rideLine"]!).ToString(), options).Where(r => r.Id == id)) plan.Add((r, car));
                }
            }
            else if (args.TryGetValue("tier", out var tier))
            {
                // The tiers (docs/turn-tests.md): quick after any update, full after changes to the turning code or a
                // game update. Both drive the first default car (the largest turning circle); full then drives the
                // other default cars where it failed or came within 0.2 m.
                var cars = Choose(models, "default");
                if (cars.Count == 0) { Refuse("no car models found"); yield break; }
                var cases = catalogue.ToList();
                if (tier == "quick")
                {
                    var known = cases.Where(f => TestedSteps.Has(f.Route[f.EntryIndex], f.Route[f.EntryIndex + 1]));
                    var sample = cases.GroupBy(f => f.Kind).SelectMany(g => g.OrderBy(f => StableHash(f.Id)).Take(QuickSample));
                    cases = known.Concat(sample).Distinct().ToList();
                }
                foreach (var f in Controls.Build(graph, catalogue)) plan.Add((f, cars[0]));
                foreach (var f in cases) plan.Add((f, cars[0]));
                if (tier == "full") followUp = cars.Skip(1).ToList();
            }
            else
            {
                var cars = Choose(models, args.TryGetValue("cars", out var c) ? c : "default");
                if (cars.Count == 0) { Refuse("no car models found"); yield break; }
                var kinds = (args.TryGetValue("kinds", out var k) ? k : "control,turn,uturn,lanechange,ramp").Split(',').Select(x => x.Trim().ToLowerInvariant()).ToHashSet();
                var cases = catalogue.Where(f => kinds.Contains(f.Kind.ToString().ToLowerInvariant())).ToList();
                if (args.TryGetValue("rides", out var rides)) cases.AddRange(RideCases(graph, rides, options));
                if (args.TryGetValue("ids", out var ids)) { var set = ids.Split(',').ToHashSet(); cases = cases.Where(f => set.Contains(f.Id)).ToList(); }
                if (args.TryGetValue("limit", out var lim) && int.TryParse(lim, out var limit)) cases = cases.Take(limit).ToList();
                if (kinds.Contains("control"))
                    foreach (var f in Controls.Build(graph, catalogue)) plan.Add((f, cars[0]));
                foreach (var car in cars)
                    foreach (var f in cases) plan.Add((f, car));
            }
            if (plan.Count == 0) { Refuse("nothing to run"); yield break; }
            if (!Begin(graph, plan.Count)) yield break;
            var s = session!;
            s.AutoSpeed = speed <= 0f;
            if (s.AutoSpeed) speed = 3f;
            Log.Info($"Turn tests {s.Id}: {plan.Count} runs at {speed:0}x, cars {string.Join(", ", plan.Select(p => p.Car.Name).Distinct())}.");

            var controlResults = new List<(Fixture Fixture, JudgeResult Result)>();
            var close = new List<Fixture>();
            var n = 0;
            for (var index = 0; index < plan.Count; index++)
            {
                var (fixture, car) = plan[index];
                if (s.StopRequested || session != s) break;
                n++;
                JudgeResult? result = null;
                var run = RunCase(s, graph, fixture, car, speed, n, r => result = r);
                while (run.MoveNext()) yield return null;
                if (fixture.Kind == CaseKind.Control && result != null) controlResults.Add((fixture, result));
                if (controlResults.Count > 0 && fixture.Kind != CaseKind.Control && s.Calibrated == null) s.Calibrated = Calibration(controlResults);
                if (fixture.Kind != CaseKind.Control && result != null && (result.Verdict == "fail" || (result.NearMiss > 0 && result.NearMiss <= 0.2f))) close.Add(fixture);
                // The full tier's second pass: the other cars where the first one failed or came close.
                if (index == plan.Count - 1 && followUp != null)
                {
                    foreach (var other in followUp) foreach (var f in close) plan.Add((f, other));
                    s.Total = plan.Count;
                    followUp = null;
                    Log.Info($"Turn tests {s.Id}: {close.Count} cases failed or came within 0.2 m; driving them with {string.Join(", ", followUpNames(plan, n))}.");
                }
            }
            if (s.Calibrated == null && controlResults.Count > 0) s.Calibrated = Calibration(controlResults);
            Teardown(s.StopRequested ? "stopped" : "finished", handedOver: false);
        }

        // Each control pair's bad half must fail for its stated reason: that shows the judge catches the fault. A
        // correct half that fails is a ride fault at that spot (the first runs found two), reported, not counted.
        private const int QuickSample = 30;

        // A stable order by id (string.GetHashCode differs between runs).
        private static uint StableHash(string id)
        {
            uint hash = 2166136261;
            foreach (var c in id) hash = (hash ^ c) * 16777619;
            return hash;
        }

        private static string Calibration(List<(Fixture Fixture, JudgeResult Result)> results)
        {
            var bad = new List<string>();
            var faults = new List<string>();
            foreach (var (f, r) in results)
            {
                if (!f.IsMutation && r.Verdict != "pass") faults.Add($"{f.Id} {r.Verdict} ({string.Join(" ", r.Reasons)})");
                if (f.IsMutation && (r.Verdict != "fail" || f.ExpectedFailure == null || !r.Reasons.Any(x => x.StartsWith(f.ExpectedFailure, StringComparison.Ordinal))))
                    bad.Add($"{f.Id} should fail with {f.ExpectedFailure}, got {r.Verdict} ({string.Join(" ", r.Reasons)})");
            }
            var verdict = (bad.Count == 0 ? "calibrated" : "uncalibrated: " + string.Join("; ", bad)) +
                          (faults.Count > 0 ? "; ride faults at the controls: " + string.Join("; ", faults) : "");
            Log.Info("Turn tests controls: " + verdict);
            return verdict;
        }

        private static void Refuse(string why)
        {
            Log.Error("Turn tests not started: " + why + ".");
            WriteStatusFile(new JObject { ["state"] = "refused", ["why"] = why, ["version"] = DrivenRidesMod.Version, ["heartbeat"] = DateTime.Now.ToString("s") });
        }

        private static bool Begin(LaneGraph graph, int total)
        {
            var manager = InstanceBehavior<GameManager>.Instance;
            var player = manager != null ? manager.playerController : null;
            if (player == null) { Refuse("no player"); return false; }
            if (BuildingManager.IsInsideBuilding) { Refuse("the player is inside a building"); return false; }
            if (PlayerActivityUI.IsPanelOpen) { Refuse("an activity panel is open"); return false; }
            if (InstanceBehavior<UIs>.Instance.timeMachine.isRunning) { Refuse("a time machine is running"); return false; }
            if (Ride.Active != null || TaxiSystem.IsTraveling) { Refuse("a ride is running"); return false; }
            if (VehicleHelper.IsInsideVehicle()) { Refuse("the player is in a vehicle"); return false; }

            var s = new Session
            {
                Id = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture),
                Player = player, Manager = manager, Graph = graph, Total = total,
                StartPosition = player.transform.position, StartRotation = player.transform.rotation,
                Started = Time.unscaledTime,
            };
            s.ResultsFile = Path.Combine(Folder, $"results-{s.Id}.jsonl");
            session = s;
            Options.LightsOverride = true;
            // No frame cap while tests run: more frames let the automatic speed go higher.
            s.VSyncWas = QualitySettings.vSyncCount;
            s.FrameRateWas = Application.targetFrameRate;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;

            // Traffic and pedestrians off, as the game's own ToggleTraffic and TogglePedestrian console commands.
            s.TrafficWas = manager!.spawnTraffic;
            manager.spawnTraffic = false;
            TrafficManager.Instance.SetTrafficDensity(0);
            TrafficManager.Instance.ClearTraffic();
            var spawner = InstanceBehavior<CityManager>.Instance.pedestrianSpawner;
            if (spawner != null)
            {
                s.PedestriansWere = PedestrianAmountField?.GetValue(spawner) is int amount ? amount : 0;
                s.PedestriansSpawningWas = spawner.spawningActive;
                spawner.spawningActive = false;
                foreach (var pedestrian in spawner.activePedestrians.ToArray()) if (pedestrian != null) pedestrian.Release();
                spawner.SetActivePedestrians(0);
            }

            // The player rides along on an anchor that follows each test car, as in a ride.
            player.ResetNavigation();
            s.AnchorObject = new GameObject("DrivenRides.TestAnchor");
            s.AnchorObject.transform.SetPositionAndRotation(player.transform.position, player.transform.rotation);
            s.Anchor = s.AnchorObject.AddComponent<RideAnchor>();
            s.Attached = true;
            player.AttachToObject(s.AnchorObject.transform, NavigationBlocker.Taxi);
            player.Character.ToggleVisibility(false);
            WriteStatus("running");
            return true;
        }

        private static readonly FieldInfo? PedestrianAmountField = AccessTools.Field(typeof(PedestrianSpawner), "_lastSetAmount");

        private IEnumerator RunCase(Session s, LaneGraph graph, Fixture fixture, CarModel model, float speed, int n, Action<JudgeResult> done)
        {
            s.RunId = $"{s.Id}-{n:0000}";
            s.Current = $"{fixture.Id} ({model.Name})";
            var all = GleyGraph.Waypoints!;
            var route = fixture.Route;
            var start = all[route[0]];
            var line = new JObject
            {
                ["run"] = s.RunId, ["session"] = s.Id, ["case"] = fixture.Id, ["kind"] = fixture.Kind.ToString(),
                ["car"] = model.Name, ["speed"] = speed, ["version"] = DrivenRidesMod.Version,
            };
            if (fixture.RideLine >= 0) line["rideLine"] = fixture.RideLine;
            // On the avoid list already: rides don't drive it with cars this long.
            if (fixture.Route.Count > fixture.EntryIndex + 1 && TestedSteps.Has(fixture.Route[fixture.EntryIndex], fixture.Route[fixture.EntryIndex + 1])) line["known"] = true;

            // Over to the case: the anchor (and with it Gley's camera, the player) stands at the start.
            s.Anchor!.Target = null;
            s.Anchor.Valid = null;
            s.AnchorObject!.transform.position = start.position + Vector3.up;
            // Traffic is off, so only the anchor (Gley's camera) has to arrive; a second is plenty.
            var until = Time.unscaledTime + 1f;
            while (Time.unscaledTime < until) { if (s.StopRequested) yield break; yield return null; }

            var driven = fixture.Path ?? PathExpander.Expand(graph, route).Path;
            TrafficManager.Instance.ClearTrafficOnArea(start.position, 40f);
            var car = TrafficManager.Instance.LoadVehicle(model.Prefab, start, true);
            if (car == null || car.prefab == null || car.prefab.name != model.Prefab.name)
            {
                if (car != null) TrafficManager.Instance.RemoveVehicle(car.GetIndex());
                Record(s, line, "setup-error", "the car could not be loaded", null, null);
                yield break;
            }
            var ahead = all[driven.Count > 1 ? driven[1] : route[1]].position - start.position;
            ahead.y = 0f;
            if (ahead.sqrMagnitude > 0.01f)
            {
                var rotation = Quaternion.LookRotation(ahead);
                car.transform.rotation = rotation;
                if (car.rb != null) car.rb.rotation = rotation;
            }
            AIEvents.TriggerChangeDrivingStateEvent(car.GetIndex(), SpecialDriveActionTypes.StopNow, 0f);
            Physics.SyncTransforms();

            var testCar = car.gameObject.AddComponent<TestCar>();
            var outline = testCar.Outline();
            var middle = (outline[0] + outline[2]) / 2f + Vector3.up;
            var blockers = Physics.OverlapBox(middle, new Vector3(testCar.HalfWidth, 0.8f, car.length / 2f), car.transform.rotation, TestCar.ObstacleMask, QueryTriggerInteraction.Ignore);
            if (blockers.Length > 0)
            {
                Object.Destroy(testCar);
                TrafficManager.Instance.RemoveVehicle(car.GetIndex());
                Record(s, line, "setup-error", $"the start is blocked by '{blockers[0].name}'", null, null);
                yield break;
            }

            var geometry = CaseGeometry.Build(graph, fixture);
            var judge = new Judge(graph, geometry, fixture);
            testCar.Judge = judge;
            s.Car = testCar;
            s.Geometry = geometry;
            s.DrivenPath = driven;
            var trace = Path.Combine(Folder, s.RunId + "-trace.csv");
            Ride ride;
            try
            {
                ride = Ride.StartTest(car.GetComponent<VehicleComponent>(), model.Prefab.GetComponent<VehicleComponent>(), Route.Fixed(graph, route),
                    fixture.Path, fixture.SlowPath, model.TopSpeed, speed, s.Anchor, trace);
            }
            catch (Exception ex)
            {
                Object.Destroy(testCar);
                s.Car = null;
                TrafficManager.Instance.RemoveVehicle(car.GetIndex());
                Record(s, line, "setup-error", "the test ride did not start: " + ex.Message, null, null);
                yield break;
            }
            s.Ride = ride;
            testCar.Waiting = () => ride.WaitingNow;

            // Driving. A generous limit: the route at 4 m/s plus 30 s, in game time (the ride speed scales it).
            var length = Route.Fixed(graph, route).LengthMetres;
            var limit = Time.time + 30f + length / 4f;
            while (!judge.Failed && !judge.Finished && !ride.Ended && Time.time < limit && !s.StopRequested) yield return null;
            var timedOut = Time.time >= limit;
            if (ride.Ended && ride.EndReason.StartsWith("replanned", StringComparison.Ordinal)) judge.Replanned(ride.EndReason);
            // One more frame, so a failure latched in the last physics step gets its picture.
            yield return null;

            var result = judge.Result(timedOut);
            try
            {
                if (ride.Ended && ride.EndReason.StartsWith("replanned", StringComparison.Ordinal)) testCar.Samples.Append("R,").Append(ride.EndReason.Replace(',', ';')).Append('\n');
                File.WriteAllText(Path.Combine(Folder, s.RunId + "-samples.csv"), testCar.Samples.ToString());
                line["samples"] = s.RunId + "-samples.csv";
            }
            catch (Exception ex) { Log.Error("Turn tests samples: " + ex.Message); }
            line["end"] = ride.Ended ? ride.EndReason : timedOut ? "time limit" : s.StopRequested ? "stopped" : judge.Finished ? "last gate" : "failed";
            line["restarts"] = ride.Restarts;
            if (s.AutoSpeed && s.SpeedFrames > 0) { line["speed"] = Math.Round(s.SpeedSum / s.SpeedFrames, 1); s.SpeedSum = 0; s.SpeedFrames = 0; }
            line["trace"] = Path.GetFileName(trace);
            if (File.Exists(Path.Combine(Folder, s.RunId + ".png"))) line["picture"] = s.RunId + ".png";
            Object.Destroy(testCar);
            s.Car = null;
            s.Ride = null;
            ride.EndTest();
            // A step the ride's world check blocks is one real rides never drive: avoided, not failed, unless the
            // car had already gone wrong before.
            var verdict = result.Verdict;
            if (ride.EndReason.StartsWith("replanned: step", StringComparison.Ordinal) && result.Reasons.Count == 1 && result.Reasons[0] == "replanned") verdict = "avoided";
            Record(s, line, verdict, null, result, fixture);
            done(result);
        }

        private static void Record(Session s, JObject line, string verdict, string? why, JudgeResult? result, Fixture? fixture)
        {
            line["verdict"] = verdict;
            if (why != null) line["why"] = why;
            if (result != null) line["judge"] = JToken.Parse(result.ToJson());
            if (fixture != null) line["fixture"] = JToken.Parse(fixture.ToJson());
            File.AppendAllText(s.ResultsFile, line.ToString(Newtonsoft.Json.Formatting.None) + "\n");
            s.Done++;
            if (verdict == "pass") s.Passed++;
            else if (verdict == "fail") s.Failed++;
            else s.Other++;
            Log.Info($"Turn test {line["run"]}: {line["case"]} ({line["car"]}) {verdict}{(why != null ? ", " + why : "")}{(result != null && result.Reasons.Count > 0 ? ", " + string.Join(" ", result.Reasons) : "")}.");
        }

        // ---- Ending ----

        public static void Abort(string reason) => Teardown(reason, handedOver: true);

        // A time machine that isn't a test's own (there are none) stops the session before it runs.
        public static void OnForeignTimeMachine()
        {
            if (session != null) Teardown("a time machine started", handedOver: false);
        }

        // Synchronous: the car goes, the game's switches come back, the player is put down where the session began.
        private static void Teardown(string reason, bool handedOver)
        {
            var s = session;
            if (s == null) return;
            session = null;
            if (instance != null) instance.routine = null;
            Log.Info($"Turn tests {s.Id} ended ({reason}): {s.Done}/{s.Total} runs, {s.Passed} passed, {s.Failed} failed, {s.Other} other.");
            try { if (s.Car != null) Object.Destroy(s.Car); } catch (Exception ex) { Log.Error("Turn tests end, sampler: " + ex.Message); }
            try { s.Ride?.EndTest(); } catch (Exception ex) { Log.Error("Turn tests end, ride: " + ex.Message); }
            Options.LightsOverride = null;
            QualitySettings.vSyncCount = s.VSyncWas;
            Application.targetFrameRate = s.FrameRateWas;
            try { if (s.Manager != null) s.Manager.spawnTraffic = s.TrafficWas; } catch (Exception ex) { Log.Error("Turn tests end, traffic: " + ex.Message); }
            try
            {
                var spawner = InstanceBehavior<CityManager>.Instance != null ? InstanceBehavior<CityManager>.Instance.pedestrianSpawner : null;
                if (spawner != null)
                {
                    spawner.spawningActive = s.PedestriansSpawningWas;
                    spawner.SetActivePedestrians(s.PedestriansWere);
                }
            }
            catch (Exception ex) { Log.Error("Turn tests end, pedestrians: " + ex.Message); }
            var player = s.Player;
            if (s.Attached && player != null)
            {
                try
                {
                    var place = NavMesh.SamplePosition(s.StartPosition, out var hit, 5f, NavMesh.AllAreas) ? hit.position : s.StartPosition;
                    player.transform.position = place;
                    player.DeattachFromObject(NavigationBlocker.Taxi);
                    player.Character.WarpSafely(place);
                    player.transform.rotation = s.StartRotation;
                    if (!handedOver && instance != null) instance.StartCoroutine(CheckPlaced(player, place));
                }
                catch (Exception ex) { Log.Error("Turn tests end, detach: " + ex); }
                try { player.Character.ToggleVisibility(true); player.Character.Reset(); }
                catch (Exception ex) { Log.Error("Turn tests end, player: " + ex.Message); }
            }
            if (s.AnchorObject != null)
            {
                for (var i = s.AnchorObject.transform.childCount - 1; i >= 0; i--) s.AnchorObject.transform.GetChild(i).SetParent(null, true);
                Object.Destroy(s.AnchorObject);
            }
            WriteStatusFile(StatusOf(s, "ended: " + reason));
        }

        // The agent counts as on the NavMesh only from the next frame on.
        private static IEnumerator CheckPlaced(PlayerController player, Vector3 place)
        {
            var save = SaveGameManager.Current;
            yield return null;
            if (session != null || player == null || !ReferenceEquals(save, SaveGameManager.Current)) yield break;
            if (!player.Character.navmeshAgent.isOnNavMesh)
            {
                Log.Info($"Turn tests end: player not on the NavMesh at {place}; warping again.");
                player.Character.WarpSafely(place);
            }
        }

        // ---- Status ----

        private static JObject StatusOf(Session s, string state) => new JObject
        {
            ["state"] = state, ["session"] = s.Id, ["version"] = DrivenRidesMod.Version, ["heartbeat"] = DateTime.Now.ToString("s"),
            ["current"] = s.Current, ["done"] = s.Done, ["total"] = s.Total, ["passed"] = s.Passed, ["failed"] = s.Failed, ["other"] = s.Other,
            ["controls"] = s.Calibrated, ["results"] = Path.GetFileName(s.ResultsFile),
            ["minutes"] = Math.Round((Time.unscaledTime - s.Started) / 60f, 1),
        };

        private static void WriteStatus(string state)
        {
            if (session != null) WriteStatusFile(StatusOf(session, state));
        }

        private static void WriteStatusFile(JObject status)
        {
            try
            {
                var path = Path.Combine(Folder, "status.json");
                var temp = path + ".tmp";
                File.WriteAllText(temp, status.ToString());
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
            catch (Exception ex) { Log.Error("Turn tests status: " + ex.Message); }
        }

        // ---- The failure picture ----

        private const int PictureSize = 800;
        private const float PictureMetres = 50f;

        // A top-down camera image of the failure, with the plan drawn over it: lane waypoints (grey), the planned
        // path (green), the path driven if it differs (orange), the car's track (red), its outline at the failure
        // (yellow), the gates (white). The drawing is done on the CPU, so it shows even if the render doesn't.
        private static void Picture(Session s)
        {
            var car = s.Car!;
            var centre = car.FailureCentre;
            var texture = new Texture2D(PictureSize, PictureSize, TextureFormat.RGB24, false);
            var rendered = false;
            GameObject? cameraObject = null;
            RenderTexture? target = null;
            try
            {
                cameraObject = new GameObject("DrivenRides.TestCamera");
                var camera = cameraObject.AddComponent<Camera>();
                camera.orthographic = true;
                camera.orthographicSize = PictureMetres / 2f;
                camera.nearClipPlane = 1f;
                camera.farClipPlane = 300f;
                camera.transform.SetPositionAndRotation(centre + Vector3.up * 120f, Quaternion.Euler(90f, 0f, 0f));
                target = RenderTexture.GetTemporary(PictureSize, PictureSize, 24);
                camera.targetTexture = target;
                camera.Render();
                var previous = RenderTexture.active;
                RenderTexture.active = target;
                texture.ReadPixels(new Rect(0, 0, PictureSize, PictureSize), 0, 0);
                RenderTexture.active = previous;
                camera.targetTexture = null;
                rendered = true;
            }
            catch (Exception ex) { Log.Error("Turn tests picture render: " + ex.Message); }
            finally
            {
                if (target != null) RenderTexture.ReleaseTemporary(target);
                if (cameraObject != null) Object.Destroy(cameraObject);
            }
            if (!rendered)
            {
                var fill = Enumerable.Repeat(new Color32(40, 40, 40, 255), PictureSize * PictureSize).ToArray();
                texture.SetPixels32(fill);
            }

            Vector2 Px(Vector3 p) => new Vector2((p.x - centre.x) / PictureMetres * PictureSize + PictureSize / 2f, (p.z - centre.z) / PictureMetres * PictureSize + PictureSize / 2f);
            Vector2 PxV(Vec3 p) => Px(new Vector3(p.X, p.Y, p.Z));
            var graph = s.Graph;
            if (graph != null)
                foreach (var i in graph.Near(GleyGraph.ToVec(centre), PictureMetres * 0.75f)) Dot(texture, PxV(graph.Position(i)), 2, new Color(0.6f, 0.6f, 0.6f));
            var geometry = s.Geometry;
            if (s.DrivenPath != null)
            {
                var all = GleyGraph.Waypoints!;
                Polyline(texture, s.DrivenPath.Select(i => Px(all[i].position)).ToList(), new Color(1f, 0.55f, 0f));
            }
            if (geometry != null)
            {
                Polyline(texture, geometry.ExpandedPolyline.Select(PxV).ToList(), Color.green);
                foreach (var gate in geometry.Gates.Concat(geometry.TurnGate != null ? new[] { geometry.TurnGate.Value } : Array.Empty<Gate>()))
                {
                    var p = new Vector3(gate.Point.X, gate.Point.Y, gate.Point.Z);
                    var across = new Vector3(gate.DirZ, 0f, -gate.DirX) * 4f;
                    Line(texture, Px(p - across), Px(p + across), Color.white);
                }
            }
            Polyline(texture, car.Track.Concat(new[] { centre }).Select(Px).ToList(), Color.red);
            if (car.FailureOutline != null)
            {
                var o = car.FailureOutline;
                for (var i = 0; i < 4; i++) Line(texture, Px(o[i]), Px(o[(i + 1) % 4]), Color.yellow);
            }
            texture.Apply();
            File.WriteAllBytes(Path.Combine(Folder, s.RunId + ".png"), texture.EncodeToPNG());
            Object.Destroy(texture);
        }

        private static void Polyline(Texture2D t, List<Vector2> points, Color color)
        {
            for (var i = 0; i + 1 < points.Count; i++) Line(t, points[i], points[i + 1], color);
        }

        private static void Line(Texture2D t, Vector2 a, Vector2 b, Color color)
        {
            var steps = Mathf.CeilToInt(Vector2.Distance(a, b));
            if (steps > 4 * PictureSize) return;
            for (var i = 0; i <= steps; i++) Dot(t, Vector2.Lerp(a, b, steps == 0 ? 0f : (float)i / steps), 1, color);
        }

        private static void Dot(Texture2D t, Vector2 p, int radius, Color color)
        {
            for (var x = -radius; x <= radius; x++)
                for (var y = -radius; y <= radius; y++)
                {
                    int px = Mathf.RoundToInt(p.x) + x, py = Mathf.RoundToInt(p.y) + y;
                    if (px >= 0 && py >= 0 && px < PictureSize && py < PictureSize) t.SetPixel(px, py, color);
                }
        }
    }
}
