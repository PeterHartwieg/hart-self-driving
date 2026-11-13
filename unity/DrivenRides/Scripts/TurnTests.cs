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
    internal sealed class TurnTests : MonoBehaviour
    {
        private static TurnTests? instance;
        private static Session? session;
        private IEnumerator? routine;
        private float nextPoll;
        private float nextStatus;

        public static bool Running => session != null;

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
            public bool Nwh;
            public bool NwhCheck = true;
            public bool TrafficOn;
            public NwhCar? NwhCar;
            public NwhDrive? NwhDrive;
            public float AutoNow = 3f;
            public bool ScaleSet;
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
            if (session != null && session.AutoSpeed && (session.Ride != null || session.NwhDrive != null))
            {
                var auto = Mathf.Clamp(GameSecondsPerFrame / frameSeconds, 1f, MaxSpeed);
                session.Ride?.SetTestSpeed(auto);
                session.AutoNow = auto;
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

        private void LateUpdate()
        {
            var s = session;
            if (s?.Car == null || !s.Car.PictureDue) return;
            s.Car.PictureDue = false;
            try { Picture(s); }
            catch (Exception ex) { Log.Error("Turn tests picture: " + ex.Message); }
        }


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
                case "parking-sites": ParkingTests.Survey(Folder); break;
                case "parking-ground": ParkingTests.GroundSurvey(Folder); break;
                case "run": StartRun(args, null); break;
                case "rerun": if (rest.Count > 0) StartRun(args, rest[0]); break;
                case "stop": if (session != null) session.StopRequested = true; break;
                case "measure":
                    if (rest.Contains("ground"))
                        NwhMeasure.Ground(args.TryGetValue("x", out var gx) && args.TryGetValue("z", out var gz)
                            ? new Vector3(float.Parse(gx, System.Globalization.CultureInfo.InvariantCulture), 0f, float.Parse(gz, System.Globalization.CultureInfo.InvariantCulture))
                            : Camera.main != null ? Camera.main.transform.position : transform.position);
                    else StartCoroutine(NwhMeasure.Run(Camera.main != null ? Camera.main.transform.position : transform.position));
                    break;
                case "dress":
                    if (rest.Contains("remove")) NwhDress.Remove();
                    else NwhDress.SpawnBeside(args.TryGetValue("car", out var which) ? which : "taxi");
                    break;
                case "ride": StartCoroutine(RideCommand.Run(args, Folder)); break;
                case "destinations": StartCoroutine(DestinationTests.Run(args, Folder)); break;
                case "ridemode":
                    Options.CarPhysicsOverride = rest.Contains("nwh") ? true : rest.Contains("gley") ? false : (bool?)null;
                    Log.Info($"Ride mode: {(Options.CarPhysicsOverride == null ? "the options page's" : Options.CarPhysicsOverride.Value ? "car physics (NWH)" : "Gley")}; car physics now {(Options.CarPhysics ? "on" : "off")}.");
                    break;
                default: Log.Error("Turn tests: unknown command " + words[0]); break;
            }
        }

        // Not "Start": Unity takes a method of that name for its message and logs "Start() can not take parameters".
        private void StartRun(Dictionary<string, string> args, string? rerunFile)
        {
            if (session != null || routine != null) { Log.Error("Turn tests: a session is already running."); return; }
            routine = RunSession(args, rerunFile);
        }


        private static readonly FieldInfo? TrafficVehiclesField = AccessTools.Field(typeof(TrafficManager), "trafficVehicles");
        private static readonly FieldInfo? VehicleTypesField = AccessTools.Field(typeof(VehicleTypeHelper), "VehicleTypes");

        private static List<VehicleComponent> Pool() =>
            (TrafficVehiclesField?.GetValue(TrafficManager.Instance) as TrafficVehicles)?.GetVehicleList() ?? new List<VehicleComponent>();

        private static List<CarModel> Models()
        {
            var pool = Pool().Where(v => v != null && v.prefab != null).ToList();
            var models = new List<CarModel>();
            CarModel? Make(string name, GameObject prefab, int top, bool taxi)
            {
                var copy = pool.FirstOrDefault(v => v.prefab == prefab) ?? pool.FirstOrDefault(v => v.prefab.name == prefab.name);
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
                ["donor"] = NwhCar.HasPrefab(m.Name) ? null : NwhDress.DonorFor(m.Prefab, out _) is NwhDress.Donor d ? NwhCar.Id(d.TypeName) : null,
            }.ToString(Newtonsoft.Json.Formatting.None));
            File.WriteAllLines(Path.Combine(Folder, "cars.jsonl"), lines);
            Log.Info("Turn tests: cars written.");
        }

        private static double Round(float v) => Math.Round(v, 2);


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


        private IEnumerator RunSession(Dictionary<string, string> args, string? rerunFile)
        {
            var graph = GleyGraph.Graph;
            if (graph == null || !TrafficManager.HasInstance || !TrafficManager.IsInitialized) { Refuse("traffic is not ready"); yield break; }
            var speed = args.TryGetValue("speed", out var sp) ? sp == "auto" ? 0f : float.TryParse(sp, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? Mathf.Clamp(v, 1f, MaxSpeed) : 3f : 3f;
            var options = CatalogueSettings();
            var catalogue = Catalogue.Build(graph, options);
            var nwh = args.TryGetValue("driver", out var dr) && dr == "nwh";
            var trafficOn = args.TryGetValue("traffic", out var tr) && tr == "on";
            var models = Models();
            if (nwh) models = models.Where(m => NwhCar.HasPrefab(m.Name) || NwhDress.DonorFor(m.Prefab, out _) != null).ToList();
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
            if (!Begin(graph, plan.Count, trafficOn)) yield break;
            var s = session!;
            s.Nwh = nwh;
            s.NwhCheck = !(args.TryGetValue("check", out var ck) && ck == "off");
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
                var run = s.Nwh ? RunNwhCase(s, graph, fixture, car, speed, n, r => result = r) : RunCase(s, graph, fixture, car, speed, n, r => result = r);
                while (run.MoveNext()) yield return null;
                if (fixture.Kind == CaseKind.Control && result != null) controlResults.Add((fixture, result));
                if (controlResults.Count > 0 && fixture.Kind != CaseKind.Control && s.Calibrated == null) s.Calibrated = Calibration(controlResults);
                if (fixture.Kind != CaseKind.Control && result != null && (result.Verdict == "fail" || (result.NearMiss > 0 && result.NearMiss <= 0.2f))) close.Add(fixture);
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

        private const int QuickSample = 30;

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

        private static bool Begin(LaneGraph graph, int total, bool trafficOn)
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
                Started = Time.unscaledTime, TrafficOn = trafficOn,
            };
            s.ResultsFile = Path.Combine(Folder, $"results-{s.Id}.jsonl");
            session = s;
            Options.LightsOverride = true;
            s.VSyncWas = QualitySettings.vSyncCount;
            s.FrameRateWas = Application.targetFrameRate;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;

            if (!trafficOn)
            {
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
            }

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
            if (fixture.Route.Count > fixture.EntryIndex + 1 && TestedSteps.Has(fixture.Route[fixture.EntryIndex], fixture.Route[fixture.EntryIndex + 1])) line["known"] = true;

            s.Anchor!.Target = null;
            s.Anchor.Valid = null;
            s.AnchorObject!.transform.position = start.position + Vector3.up;
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

            var length = Route.Fixed(graph, route).LengthMetres;
            var limit = Time.time + 30f + length / 4f;
            while (!judge.Failed && !judge.Finished && !ride.Ended && Time.time < limit && !s.StopRequested) yield return null;
            var timedOut = Time.time >= limit;
            if (ride.Ended && ride.EndReason.StartsWith("replanned", StringComparison.Ordinal)) judge.Replanned(ride.EndReason);
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
            var verdict = result.Verdict;
            if (ride.EndReason.StartsWith("replanned: step", StringComparison.Ordinal) && result.Reasons.Count == 1 && result.Reasons[0] == "replanned") verdict = "avoided";
            Record(s, line, verdict, null, result, fixture);
            done(result);
        }

        private const float NwhManoeuvreKmh = 10f;

        private IEnumerator RunNwhCase(Session s, LaneGraph graph, Fixture fixture, CarModel model, float speed, int n, Action<JudgeResult> done)
        {
            s.RunId = $"{s.Id}-{n:0000}";
            s.Current = $"{fixture.Id} ({model.Name}, NWH)";
            var all = GleyGraph.Waypoints!;
            var route = fixture.Route;
            var start = all[route[0]];
            var line = new JObject
            {
                ["run"] = s.RunId, ["session"] = s.Id, ["case"] = fixture.Id, ["kind"] = fixture.Kind.ToString(),
                ["car"] = model.Name, ["speed"] = speed, ["version"] = DrivenRidesMod.Version, ["driver"] = "nwh", ["traffic"] = s.TrafficOn,
            };
            if (fixture.RideLine >= 0) line["rideLine"] = fixture.RideLine;

            s.Anchor!.Target = null;
            s.Anchor.Valid = null;
            s.AnchorObject!.transform.position = start.position + Vector3.up;
            var until = Time.unscaledTime + (s.TrafficOn ? 3f : 1f);
            while (Time.unscaledTime < until) { if (s.StopRequested) yield break; yield return null; }

            TrafficManager.Instance.ClearTrafficOnArea(start.position, 40f);
            var planner = s.NwhCheck && fixture.Path == null ? NwhRides.Planner(model.Name, model.Prefab, model.TopSpeed) : null;
            DrivenRides.Core.Drive.DrivePlan? ridePlan = null;
            if (planner != null)
            {
                planner.BeginRide();
                line["minRadius"] = Math.Round(planner.Car.ArcMinRadius(), 2);
                if (fixture.Kind == CaseKind.Ride)
                {
                    var (plannedRoute, plannedPlan, summary) = planner.PlanRoute(graph, route[0], new[] { new Goal(route[route.Count - 1], 0f) }, (int)VehicleTypes.Car);
                    var planned = plannedRoute?.Waypoints.ToList();
                    ridePlan = plannedPlan;
                    line["plan"] = summary;
                    if (planned == null) { Record(s, line, "avoided", "no route this car can drive: " + summary, null, fixture); yield break; }
                    fixture = new Fixture
                    {
                        Id = fixture.Id, Kind = fixture.Kind, Manoeuvre = fixture.Manoeuvre, Route = planned, ExitIndex = planned.Count - 1,
                        RideLine = fixture.RideLine, SharpnessDegrees = fixture.SharpnessDegrees,
                    };
                    route = planned;
                }
                if (fixture.Kind == CaseKind.Turn || fixture.Kind == CaseKind.UTurn)
                {
                    var verdict = planner.Check.Step(route[fixture.EntryIndex], route[fixture.EntryIndex + 1]);
                    line["plan"] = verdict.Form;
                    line["check"] = verdict.ToString();
                    if (!verdict.Feasible) { Record(s, line, "avoided", verdict.ToString(), null, fixture); yield break; }
                }
                var parked = planner.BlockedAhead(graph, route, 0, float.MaxValue);
                if (parked.Count > 0) { Record(s, line, "avoided", $"step {parked[0].From} -> {parked[0].To}: {parked[0].What}", null, fixture); yield break; }
            }
            var firstPath = fixture.Path ?? PathExpander.Expand(graph, route).Path;
            var ahead = graph.PlainHeading(route[0], out var lx, out var lz) ? new Vector3(lx, 0f, lz) : all[firstPath.Count > 1 ? firstPath[1] : route[1]].position - start.position;
            ahead.y = 0f;
            var rotation = ahead.sqrMagnitude > 0.01f ? Quaternion.LookRotation(ahead) : Quaternion.identity;
            var car = NwhRides.Spawn(model.Name, model.Prefab, start.position + Vector3.up * 0.4f, rotation, out var why);
            if (car == null) { Record(s, line, "setup-error", "no NWH car: " + why, null, null); yield break; }
            if (car.Look != null) line["donor"] = NwhCar.Id(car.TypeName);
            s.NwhCar = car;
            NwhRides.PlaceRearAxle(car, start.position, rotation);

            var testCar = car.Root.AddComponent<TestCar>();
            testCar.SetBody(car.BodyCentre, car.BodyHalf);
            testCar.FrontAxleOf = () => car.FrontAxle;
            testCar.Wheels = car.Vehicle.powertrain.wheels.Where(w => w?.wheelUAPI != null).Select(w => w.wheelUAPI).ToArray();
            Collider[] blockers = Array.Empty<Collider>();
            var placed = start.position;
            for (var nudge = 0; nudge <= 10; nudge++)
            {
                placed = start.position + rotation * Vector3.forward * nudge;
                NwhRides.PlaceRearAxle(car, placed, rotation);
                var outline = testCar.Outline();
                var middle = (outline[0] + outline[2]) / 2f;
                middle.y = placed.y + (0.35f + 1.5f) / 2f;
                blockers = Physics.OverlapBox(middle, new Vector3(testCar.HalfWidth + 0.03f, (1.5f - 0.35f) / 2f, car.Length / 2f + 0.03f), car.Root.transform.rotation, TestCar.ObstacleMask, QueryTriggerInteraction.Ignore);
                if (blockers.Length == 0)
                {
                    if (nudge > 0) line["startNudge"] = nudge;
                    break;
                }
            }
            if (blockers.Length > 0)
            {
                var b = blockers[0].bounds;
                EndNwh(s);
                Record(s, line, "setup-error", $"the start is blocked by '{blockers[0].name}' ({LayerMask.LayerToName(blockers[0].gameObject.layer)}, {blockers[0].GetType().Name}, bounds {b.min} to {b.max}; start {start.position}) for 10 m along the lane", null, null);
                yield break;
            }

            var minRadius = car.Wheelbase / Mathf.Tan(Mathf.Max(5f, car.LockAt(NwhManoeuvreKmh / 3.6f)) * Mathf.Deg2Rad);
            List<int> path;
            var arcs = new List<(int Entry, Polyline Arc)>();
            var slow = new List<(int Position, float SpeedKmh)>();
            List<(int, float, float, float)>? shifts = null;
            HashSet<int>? fillets = null;
            DrivenRides.Core.Drive.DrivePlan? firstPlan = null;
            if (fixture.Path != null)
            {
                path = fixture.Path;
                if (fixture.SlowPath != null) foreach (var k in fixture.SlowPath) slow.Add((k, 25f));
            }
            else if (planner != null)
            {
                var plan = ridePlan;
                if (plan == null)
                {
                    plan = planner.Plan(graph, route, out var failed);
                    if (plan == null)
                    {
                        EndNwh(s);
                        Record(s, line, "avoided", $"the line fails as driven: step {failed[0].From} -> {failed[0].To}: {failed[0].What}", null, fixture);
                        yield break;
                    }
                }
                firstPlan = plan;
                path = plan.Path;
                arcs.AddRange(plan.Arcs);
                slow.AddRange(plan.Slow);
                shifts = plan.Shifts;
                fillets = plan.Fillets;
                if (plan.Shifts.Count > 0) line["shifts"] = string.Join(", ", plan.Shifts.Select(x => $"{path[x.Position]}->{path[x.Position + 1]} {x.Offset:+0.00;-0.00} m"));
            }
            else
            {
                var expanded = PathExpander.Expand(graph, route, null, (e, x) => TurnArc.Fit(graph, e, x, minRadius) != null);
                path = expanded.Path;
                foreach (var (entry, exit) in expanded.Arcs)
                {
                    var at = -1;
                    for (var k = 0; k + 1 < path.Count; k++) if (path[k] == entry && path[k + 1] == exit) { at = k; break; }
                    var arc = at >= 0 ? TurnArc.Fit(graph, entry, exit, minRadius) : null;
                    if (arc != null) arcs.Add((at, arc));
                }
            }
            if (planner == null)
                for (var k = 0; k < path.Count; k++) if (all[path[k]].maxSpeed > 0 && all[path[k]].maxSpeed < model.TopSpeed) slow.Add((k, all[path[k]].maxSpeed));
            line["arcs"] = arcs.Count;
            if (planner == null) line["minRadius"] = Math.Round(minRadius, 2);

            var geometry = CaseGeometry.Build(graph, fixture);
            var judge = new Judge(graph, geometry, fixture);
            testCar.Judge = judge;
            s.Car = testCar;
            s.Geometry = geometry;
            s.DrivenPath = path;
            var drive = car.Root.AddComponent<NwhDrive>();
            drive.Recording = true;
            if (planner != null && firstPlan != null) drive.Begin(car, firstPlan, planner.Car, model.TopSpeed > 0 ? model.TopSpeed : 50);
            else
            {
                if (shifts != null) drive.Shifts = shifts;
                drive.Fillets = fillets;
                drive.Begin(car, path, arcs, slow, model.TopSpeed > 0 ? model.TopSpeed : 50);
            }
            var driveRoute = route;
            var driveOffset = 0;
            var rideEnd = route[route.Count - 1];
            var replans = new List<string>();
            var extraSeconds = 0f;
            drive.Blocked += (position, what) =>
            {
                if (drive.WayOn.Replan != null) return;
                var r = NwhPlanner.RouteStepAt(driveRoute, path, position);
                if (r < 0) return;
                planner?.Block(driveRoute[r], driveRoute[r + 1], DrivenRides.Core.Drive.BlockCause.OnTheWay);
                line["blocked"] = $"step {driveRoute[r]} -> {driveRoute[r + 1]}: {what}";
            };
            var goals = new[] { new Goal(rideEnd, 0f) };
            var watch = new DrivenRides.Core.Drive.WayOnPlanner.Watch();
            if (firstPlan != null) watch.Installed(firstPlan);
            DrivenRides.Core.Drive.DrivePlan? TakeUp((DrivenRides.Core.Drive.DrivePlan Plan, List<int> Route, string Note)? found, int position, string kind, string failure)
            {
                var r = NwhPlanner.RouteStepAt(driveRoute, path, position);
                replans.Add(found != null ? kind + ": " + found.Value.Note : kind + ": " + (failure.Length > 0 ? failure : "no way on joins the car"));
                line["replanned"] = string.Join(" | ", replans);
                Log.Info($"Turn test {s.RunId}: {replans[replans.Count - 1]}.");
                if (found == null || r < 0) return null;
                var (plan, next, _) = found.Value;
                var keep = driveOffset + r + (next[0] == driveRoute[r] ? 0 : 1);
                var judged = route.Take(keep).Concat(next).ToList();
                driveOffset = keep;
                driveRoute = next;
                route = judged;
                path = plan.Path;
                    fixture = new Fixture
                    {
                        Id = fixture.Id, Kind = fixture.Kind, Manoeuvre = fixture.Manoeuvre, Route = judged, ExitIndex = judged.Count - 1,
                        RideLine = fixture.RideLine, SharpnessDegrees = fixture.SharpnessDegrees,
                    };
                var rerouted = CaseGeometry.Build(graph, fixture);
                judge.Reroute(rerouted, fixture);
                s.Geometry = rerouted;
                s.DrivenPath = path;
                extraSeconds += 30f + Route.Fixed(graph, next).LengthMetres / 4f;
                watch.Installed(plan);
                return plan;
            }
            if (planner != null && fixture.Kind == CaseKind.Ride)
            {
                drive.WayOn.Replan = (position, what, pose) => planner.PlanRoundBlock(driveRoute, path, position, what, pose, goals, (int)VehicleTypes.Car);
                drive.WayOn.LookAhead = (position, pose) => planner.LookAhead(driveRoute, path, position, pose, goals, (int)VehicleTypes.Car, watch);
                drive.WayOn.Take = job => TakeUp(job.Found, job.Position, job.Kind, job.Note);
            }
            s.NwhDrive = drive;
            testCar.Waiting = () => drive.WaitingNow;
            s.Anchor.Target = car.Root.transform;
            s.Anchor.Valid = () => car.Root != null;

            var length = Route.Fixed(graph, route).LengthMetres;
            var limit = Time.time + 30f + length / 4f + (s.TrafficOn ? length / 4f : 0f);
            while (!judge.Failed && !judge.Finished && !drive.Ended && Time.time < limit + extraSeconds && !s.StopRequested)
            {
                ApplyNwhScale(s, s.AutoSpeed ? s.AutoNow : speed);
                yield return null;
            }
            var timedOut = Time.time >= limit + extraSeconds;
            if (drive.Ended && drive.Arrived) judge.Arrive();
            if (drive.Ended && drive.EndReason.StartsWith("blocked", StringComparison.Ordinal)) judge.Replanned(drive.EndReason);
            yield return null;
            RestoreNwhScale(s);

            var result = judge.Result(timedOut);
            TrafficVerdict.Apply(result, drive.ContactsCaused, drive.Contacts - drive.ContactsCaused, drive.RedRuns, drive.Stalls);
            try
            {
                File.WriteAllText(Path.Combine(Folder, s.RunId + "-samples.csv"), testCar.Samples.ToString());
                line["samples"] = s.RunId + "-samples.csv";
                File.WriteAllText(Path.Combine(Folder, s.RunId + "-nwh.csv"), drive.TraceText());
                line["trace"] = s.RunId + "-nwh.csv";
                line["contacts"] = drive.Contacts;
                line["contactsCaused"] = drive.ContactsCaused;
                line["stalls"] = drive.Stalls;
                line["redRuns"] = drive.RedRuns;
                line["unsettledDecisions"] = drive.UnsettledDecisions;
                var stops = new JArray();
                foreach (var (ms, metres, seconds) in drive.TriggerStops) stops.Add(new JArray(ms, metres, seconds));
                line["triggerStops"] = stops;
                if (drive.DecideCost != null && drive.ReadCost != null && drive.CarsRead != null)
                    line["trafficCost"] = new JObject
                    {
                        ["steps"] = drive.DecideCost.Total,
                        ["readP50"] = drive.ReadCost.Percentile(50), ["readP99"] = drive.ReadCost.Percentile(99),
                        ["decideP50"] = drive.DecideCost.Percentile(50), ["decideP99"] = drive.DecideCost.Percentile(99), ["decideMax"] = drive.DecideCost.Max,
                        ["carsP50"] = drive.CarsRead.Percentile(50), ["carsMax"] = drive.CarsRead.Max,
                    };
                var dumps = new JArray();
                for (var k = 0; k < drive.Dumps.Count; k++)
                {
                    var name = $"{s.RunId}-traffic-{k + 1}-{drive.Dumps[k].Kind}.json";
                    File.WriteAllText(Path.Combine(Folder, name), drive.Dumps[k].Json);
                    dumps.Add(name);
                }
                if (dumps.Count > 0) line["trafficDumps"] = dumps;
            }
            catch (Exception ex) { Log.Error("Turn tests samples: " + ex.Message); }
            line["end"] = drive.Ended ? drive.EndReason : timedOut ? "time limit" : s.StopRequested ? "stopped" : judge.Finished ? "last gate" : "failed";
            if (s.AutoSpeed && s.SpeedFrames > 0) { line["speed"] = Math.Round(s.SpeedSum / s.SpeedFrames, 1); s.SpeedSum = 0; s.SpeedFrames = 0; }
            if (File.Exists(Path.Combine(Folder, s.RunId + ".png"))) line["picture"] = s.RunId + ".png";
            EndNwh(s);
            Record(s, line, result.Verdict, null, result, fixture);
            done(result);
        }

        private static void EndNwh(Session s)
        {
            if (s.Car != null && s.NwhCar != null && s.Car.gameObject == s.NwhCar.Root) { Object.Destroy(s.Car); s.Car = null; }
            s.NwhDrive = null;
            if (s.Anchor != null) { s.Anchor.Target = null; s.Anchor.Valid = null; }
            if (s.NwhCar?.Root != null) Object.Destroy(s.NwhCar.Root);
            s.NwhCar = null;
            RestoreNwhScale(s);
        }

        private static void ApplyNwhScale(Session s, float speed)
        {
            var game = InstanceBehavior<UIs>.Instance.gameSpeed;
            if (game.Paused || InstanceBehavior<UIs>.Instance.timeMachine.isRunning) return;
            if (!Mathf.Approximately(Time.timeScale, speed)) Time.timeScale = speed;
            s.ScaleSet = true;
        }

        private static void RestoreNwhScale(Session s)
        {
            if (!s.ScaleSet) return;
            var ui = InstanceBehavior<UIs>.Instance;
            if (ui != null && ui.gameSpeed != null && ui.gameSpeed.Paused) return;
            Time.timeScale = 1f;
            s.ScaleSet = false;
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


        public static void Abort(string reason) => Teardown(reason, handedOver: true);

        public static void OnForeignTimeMachine()
        {
            if (session != null) Teardown("a time machine started", handedOver: false);
        }

        private static void Teardown(string reason, bool handedOver)
        {
            var s = session;
            if (s == null) return;
            session = null;
            if (instance != null) instance.routine = null;
            Log.Info($"Turn tests {s.Id} ended ({reason}): {s.Done}/{s.Total} runs, {s.Passed} passed, {s.Failed} failed, {s.Other} other.");
            try { if (s.Car != null) Object.Destroy(s.Car); } catch (Exception ex) { Log.Error("Turn tests end, sampler: " + ex.Message); }
            try { s.Ride?.EndTest(); } catch (Exception ex) { Log.Error("Turn tests end, ride: " + ex.Message); }
            try { EndNwh(s); } catch (Exception ex) { Log.Error("Turn tests end, NWH car: " + ex.Message); }
            Options.LightsOverride = null;
            QualitySettings.vSyncCount = s.VSyncWas;
            Application.targetFrameRate = s.FrameRateWas;
            try { if (s.Manager != null && !s.TrafficOn) s.Manager.spawnTraffic = s.TrafficWas; } catch (Exception ex) { Log.Error("Turn tests end, traffic: " + ex.Message); }
            try
            {
                var spawner = InstanceBehavior<CityManager>.Instance != null ? InstanceBehavior<CityManager>.Instance.pedestrianSpawner : null;
                if (spawner != null && !s.TrafficOn)
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
                    // Shown first: the hidden figure's GameObject is inactive, and DeattachFromObject calls ResetPath
                    // on its agent (as Ride.Detach).
                    player.Character.ToggleVisibility(true);
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


        private const int PictureSize = 800;
        private const float PictureMetres = 50f;

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
