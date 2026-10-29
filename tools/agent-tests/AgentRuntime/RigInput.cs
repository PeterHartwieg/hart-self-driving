using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;

namespace AgentTests
{
    // The optional rig virtual mouse (Food Trucks tools/rig-input, AgentRigInput.dll beside this assembly). Loaded once,
    // after the bridge pump runs, only if its SHA-256 is in this installation's receipt. Reached by reflection, so the
    // runtime builds and runs without it.
    internal static class RigInput
    {
        private const string FileName = "AgentRigInput.dll";
        private static bool tried;
        private static volatile string status = "not started";
        private static string sha;
        private static Func<JObject> health;
        private static Action tick;

        internal static JObject Describe() => new JObject { ["status"] = status, ["sha256"] = sha };

        // Main thread, every pump frame.
        internal static void Tick(string executable)
        {
            if (tried) { tick?.Invoke(); return; }
            tried = true;
            try
            {
                var path = Path.Combine(Path.GetDirectoryName(typeof(Host).Assembly.Location), FileName);
                if (!File.Exists(path)) { status = "absent"; return; }
                sha = Sha256(path);
                var refused = CheckReceipt(executable, path, sha);
                if (refused != null) { status = "refused: " + refused; UnityEngine.Debug.LogError("[AgentTests] Rig input " + status); return; }
                var entry = Assembly.LoadFrom(path).GetType("AgentRigInput.Entry", true);
                status = (string)entry.GetMethod("Start").Invoke(null, new object[] { Host.Product, Host.Port });
                health = (Func<JObject>)Delegate.CreateDelegate(typeof(Func<JObject>), entry.GetMethod("Health"));
                tick = (Action)Delegate.CreateDelegate(typeof(Action), entry.GetMethod("Tick"));
            }
            catch (Exception ex) { status = "failed: " + (ex.InnerException ?? ex).GetType().Name + ": " + (ex.InnerException ?? ex).Message; }
            UnityEngine.Debug.Log("[AgentTests] Rig input: " + status);
        }

        // HTTP thread: the addon's own main-thread snapshot, or null without the addon.
        internal static JObject Health() => health?.Invoke();

        private static string Sha256(string path)
        {
            using (var hash = SHA256.Create())
            using (var file = File.OpenRead(path))
                return string.Concat(hash.ComputeHash(file).Select(b => b.ToString("x2")));
        }

        // The installer records every injected file's hash; an addon it did not record is not loaded.
        private static string CheckReceipt(string executable, string path, string hash)
        {
#if AGENT_MAC
            // ROOT/<Product>.app/Contents/MacOS/<Product>
            var root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(executable))));
            var receipt = Path.Combine(root, "install.json");
#else
            var receipt = Path.Combine(Path.GetDirectoryName(executable), "agent-install.json");
#endif
            if (!File.Exists(receipt)) return "no install receipt";
            var files = JObject.Parse(File.ReadAllText(receipt))["files"];
            string recorded = null;
            var full = Path.GetFullPath(path);
            if (files is JArray list)
                recorded = list.OfType<JObject>().Where(f => Same((string)f["path"], full)).Select(f => (string)f["sha256"]).FirstOrDefault();
            else if (files is JObject map)
                recorded = map.Properties().Where(p => Same(p.Name, full)).Select(p => (string)p.Value).FirstOrDefault();
            if (recorded == null) return FileName + " is not in the install receipt";
            return string.Equals(recorded, hash, StringComparison.OrdinalIgnoreCase) ? null : FileName + " changed since installation";
        }

        private static bool Same(string a, string b) =>
            a != null && string.Equals(Path.GetFullPath(a), b, Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
