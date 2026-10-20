using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AgentTests
{
    // This assembly is injected only into the private test copy. Never install it as a Workshop mod.
    public static class Host
    {
#if AGENT_MAC
        public const string Product = "Big Ambitions Agent Tests Mac";
        public const int Port = 28330;
        private const string InstanceId = "agentmac";
        private const string BundleId = "com.hovgaardgames.bigambitions.agenttests.mac";
#else
        public const string Product = "Big Ambitions Agent Tests";
        public const int Port = 18330;
        private const string InstanceId = "agent1";
#endif
        private static bool initialized;
        private static string dataPath, cachePath, executable, instance, mode;
        private static int pid;
        private static volatile float audioVolume;
        private static volatile int pumpFrame;
        private static long lastFrame;
        private static HashSet<int> ownedDlc = new HashSet<int>();
        public static bool IsReady => initialized;
        public static bool IsHeadless => mode == "headless";

        public static void Initialize()
        {
            if (initialized) return;
            var args = Environment.GetCommandLineArgs();
            instance = InstanceId;
            dataPath = Path.GetFullPath(Application.persistentDataPath);
            cachePath = Path.GetFullPath(Application.temporaryCachePath);
            executable = Process.GetCurrentProcess().MainModule.FileName;
            pid = Process.GetCurrentProcess().Id;
            // headless: -nographics; background: batch mode with graphics, no window; windowed: the full game in a window.
            mode = args.Contains("-nographics") ? "headless" : args.Contains("-batchmode") ? "background" : "windowed";
#if AGENT_MAC
            var expectedProfile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/Application Support", BundleId);
            bool isolated = dataPath == expectedProfile && cachePath.Contains(BundleId);
#else
            bool isolated = string.Equals(new DirectoryInfo(dataPath).Name, Product, StringComparison.Ordinal) && cachePath.Replace('\\', '/').Contains("/" + Product);
#endif
            if (!args.Contains("-agent-tests") || Application.productName != Product || !isolated ||
                !string.Equals(Path.GetFileNameWithoutExtension(executable), Product, StringComparison.Ordinal))
                Environment.FailFast("Agent test isolation check failed: " + dataPath + "; cache=" + cachePath + "; executable=" + executable);
            Directory.CreateDirectory(dataPath);
            Directory.CreateDirectory(cachePath);
            var entitlementFile = Path.Combine(Path.GetDirectoryName(executable), "steam-entitlements.json");
            var entitlements = JObject.Parse(File.ReadAllText(entitlementFile));
            if ((int?)entitlements["app_id"] != 1331550 || (bool?)entitlements["base_owned"] != true)
                Environment.FailFast("Missing verified Steam ownership snapshot for the private test copy.");
            ownedDlc = new HashSet<int>(((JArray)entitlements["dlc"]).Values<int>());
            BridgeControl.ControlGate.ConfigureProfile(dataPath);
            initialized = true;
            Application.runInBackground = true;
            MuteAudio();
            File.WriteAllText(Path.Combine(dataPath, "agent-runtime.json"), Describe().ToString());
            UnityEngine.Debug.Log("[AgentTests] Isolated test runtime: " + Describe().ToString(Newtonsoft.Json.Formatting.None));
        }

        private static JObject Describe() => new JObject
        {
            ["id"] = instance, ["pid"] = pid, ["mode"] = mode, ["port"] = Port,
            ["product"] = Product, ["data_path"] = dataPath, ["cache_path"] = cachePath,
            ["executable"] = executable, ["test_only"] = true, ["workshop"] = false,
            ["steam_stats_writes"] = false, ["audio_muted"] = audioVolume == 0f,
            ["audio_listener_volume"] = audioVolume, ["pump_frame"] = pumpFrame, ["runtime_version"] = 6,
            ["steam_connected"] = false, ["owned_dlc_snapshot"] = new JArray(ownedDlc.OrderBy(x => x))
        };

        private static void MuteAudio()
        {
            AudioListener.volume = 0f;
            audioVolume = AudioListener.volume;
        }

        // Runs in the existing bridge pump, whose lifetime already follows scene transitions.
        public static void Tick()
        {
            if (IsHeadless)
            {
                var now = Stopwatch.GetTimestamp();
                if (lastFrame != 0)
                {
                    var remaining = 1000.0 / 30 - (now - lastFrame) * 1000.0 / Stopwatch.Frequency;
                    if (remaining >= 1) System.Threading.Thread.Sleep((int)remaining);
                }
                lastFrame = Stopwatch.GetTimestamp();
            }
            MuteAudio();
            // The windowed mode is for rendered-game checks (performance mods among them): the game's own frame settings.
            if (mode == "windowed") { pumpFrame = Time.frameCount; return; }
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 30;
            pumpFrame = Time.frameCount;
        }

        // Called by the bridge's HTTP thread. All Unity-derived values were cached on the main thread.
        public static JObject Health(JObject result)
        {
            result["agent_instance"] = Describe();
            return BridgeControl.ControlGate.Health(result);
        }

        private static System.Type commands;
        private static bool quitting;
        private static object Field(string name) => commands.GetField(name, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic).GetValue(null);
        private static object Method(string name) => commands.GetMethod(name, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic).Invoke(null, null);
        public static void RefreshBusy()
        {
            if (commands == null) commands = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "FoodTrucks").GetType("FoodTrucks.TestBridgeCommands", true);
            BridgeControl.ControlGate.ObserveWork(quitting || (string)Method("get_LoadStatus") == "pending" || Field("runUntil") != null ||
                (bool)Method("get_Driving") || (bool?)Method("TimeSkipRunning") == true ||
                ((string)Method("Scene") == "city" && (bool?)Method("Paused") != true));
        }
        public static object AfterCommand(object result) { RefreshBusy(); return result; }
        public static void MarkQuitting() { quitting = true; }
        public static JObject AfterCall(JObject reply, JObject args) { BridgeControl.ControlGate.EndUnqueued(args); return reply; }
        public static JObject PrepareBody(JObject body) => BridgeControl.ControlGate.PrepareBody(body);
        public static JObject Intercept(string cmd, JObject args) => BridgeControl.ControlGate.Intercept(cmd, args);
        public static Task<JToken> Track(Task<JToken> task, JObject args) => BridgeControl.ControlGate.Track(task, args);

        public static Task<List<T>> NoWorkshopMods<T>() => Task.FromResult(new List<T>());
        public static bool OwnsDlc(int id) => id == 0 || ownedDlc.Contains(id);

        public static void BeforeCommand(string cmd, JObject args)
        {
            BridgeControl.ControlGate.BeforeRun(cmd, args);
            if (cmd == "shoot" && mode == "headless")
                throw new InvalidOperationException("Screenshots require the agent instance's background mode.");
        }
    }

}
