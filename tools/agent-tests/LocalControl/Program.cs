using BridgeControl;
using Newtonsoft.Json.Linq;
try
{
    var request=JObject.Parse(Console.ReadLine() ?? "{}");
    var store=LeaseStore.ForInstance("agentmac");
    string token=(string)request["token"];
    switch ((string)request["action"])
    {
        case "status": Console.WriteLine(store.StatusJson()); break;
        case "acquire": Console.WriteLine(store.AcquireJson((string)request["owner"],(string)request["purpose"],token)); break;
        case "release": Console.WriteLine(store.ReleaseJson(token)); break;
        case "assert": store.AssertOwner(token); Console.WriteLine("{}"); break;
        case "hold":
            var id=store.Begin(token,(string)request["kind"] ?? "lifecycle");
            try { Console.WriteLine("{\"ready\":true}"); Console.Out.Flush(); Console.ReadLine(); }
            finally { store.End(id); }
            break;
        default: throw new InvalidOperationException("Unknown local lock action");
    }
}
catch(Exception error) { Console.Error.WriteLine(error.Message); Environment.ExitCode=1; }
