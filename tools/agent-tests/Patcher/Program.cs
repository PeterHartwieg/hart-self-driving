using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Security.Cryptography;
using System.Text.Json;

if (args.Length != 3 && args.Length != 4) throw new ArgumentException("Patcher INPUT OUTPUT AgentTestRuntime.dll [PORT]");
var port = args.Length == 4 ? int.Parse(args[3]) : 18330;
if (port != 18330 && port != 28330) throw new ArgumentException("Unknown dedicated bridge port");
var source = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
if (source == output) throw new ArgumentException("Input and output must be separate");
Directory.CreateDirectory(output);
using var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(source);
using var runtime = AssemblyDefinition.ReadAssembly(args[2]);
var host = runtime.MainModule.GetType("AgentTests.Host");
var records = new List<object>();
MethodDefinition Method(TypeDefinition type, string name, int? count = null) =>
    type.Methods.Single(m => m.Name == name && (!count.HasValue || m.Parameters.Count == count));
MethodReference Import(ModuleDefinition module, string name) => module.ImportReference(Method(host, name));
void Reset(MethodDefinition m)
{
    m.Body = new MethodBody(m); m.DebugInformation.SequencePoints.Clear();
    m.CustomAttributes.Remove(m.CustomAttributes.FirstOrDefault(a => a.AttributeType.Name == "AsyncStateMachineAttribute"));
}
void Void(MethodDefinition m) { Reset(m); m.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); }
void False(MethodDefinition m) { Reset(m); m.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0)); m.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); }
void Prefix(MethodDefinition m, MethodReference call)
{
    m.Body.GetILProcessor().InsertBefore(m.Body.Instructions[0], Instruction.Create(OpCodes.Call, call));
}
void Rewrite(string file, Action<ModuleDefinition> change)
{
    var input = Path.Combine(source, file);
    using var asm = AssemblyDefinition.ReadAssembly(input, new ReaderParameters { AssemblyResolver = resolver });
    if (asm.MainModule.AssemblyReferences.Any(r => r.Name == "AgentTestRuntime"))
        throw new InvalidOperationException("Already patched: " + file);
    change(asm.MainModule);
    var dest = Path.Combine(output, file);
    asm.Write(dest);
    records.Add(new { file, source_sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input))).ToLowerInvariant(),
        patched_sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dest))).ToLowerInvariant() });
}

Rewrite("HGPlugins.dll", module =>
{
    var steam = module.GetType("SteamAPI");
    Prefix(Method(steam, "Awake"), Import(module, "Initialize"));
    // Ownership/DLC are verified by a separate, short-lived installer helper. The player stays offline.
    var init = Method(steam, "InitSteam");
    Reset(init); init.Body.GetILProcessor().Emit(OpCodes.Ldarg_0);
    init.Body.GetILProcessor().Emit(OpCodes.Ldc_I4_0);
    init.Body.GetILProcessor().Emit(OpCodes.Stfld, steam.Fields.Single(f => f.Name == "steamApiEnabled"));
    init.Body.GetILProcessor().Emit(OpCodes.Ret);
    Void(Method(steam, "Update"));
    False(Method(steam, "get_isRunningAndValid"));
    var dlc = Method(steam, "DlcIsOwned");
    Reset(dlc); dlc.Body.GetILProcessor().Emit(OpCodes.Ldarg_0);
    dlc.Body.GetILProcessor().Emit(OpCodes.Call, Import(module, "OwnsDlc"));
    dlc.Body.GetILProcessor().Emit(OpCodes.Ret);
    False(Method(steam, "get_StatsRecieved"));
    foreach (var name in new[] { "IndicateProgress", "StoreStats", "ResetSteamAchievements" }) Void(Method(steam, name));
    var ready = Method(steam, "OnUserStatsReady");
    var invoke = (MethodReference)ready.Body.Instructions.Single(i => i.Operand is MethodReference r && r.Name == "Invoke").Operand;
    Reset(ready);
    var il = ready.Body.GetILProcessor();
    var proceed = Instruction.Create(OpCodes.Ldc_I4_1);
    var received = steam.Fields.Single(f => f.Name == "_statsReceived");
    il.Emit(OpCodes.Ldsfld, received); il.Emit(OpCodes.Brfalse, proceed); il.Emit(OpCodes.Ret);
    il.Append(proceed); il.Emit(OpCodes.Stsfld, received);
    il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, steam.Fields.Single(f => f.Name == "onSteamUserStatsReceived"));
    il.Emit(OpCodes.Callvirt, invoke); il.Emit(OpCodes.Ret);
});
Rewrite("BigAmbitions.dll", module =>
{
    // Local mod loading must not wait for a Steam login that this test player intentionally never makes.
    var start = Method(module.GetType("BigAmbitions.HGModLoader"), "TryInitializeModsForPlaySession");
    var check = start.Body.Instructions.Single(i => i.Operand is MethodReference m && m.DeclaringType.Name == "SteamAPI" && m.Name == "get_isRunningAndValid");
    check.Operand = Import(module, "get_IsReady");
});
Rewrite("BigAmbitions.ModsInternal.dll", module =>
{
    Prefix(Method(module.GetType("BigAmbitions.ModsInternal.ModDiscoveryRegistry"), "ResetStaticState"), Import(module, "Initialize"));
    var get = Method(module.GetType("BigAmbitions.ModsInternal.SteamModLoadingService"), "GetSubscribedMods");
    var listType = (GenericInstanceType)((GenericInstanceType)get.ReturnType).GenericArguments[0];
    var generic = new GenericInstanceMethod(Import(module, "NoWorkshopMods"));
    generic.GenericArguments.Add(listType.GenericArguments[0]);
    Reset(get); get.Body.GetILProcessor().Emit(OpCodes.Call, generic); get.Body.GetILProcessor().Emit(OpCodes.Ret);
});
Rewrite("FoodTrucks.dll", module =>
{
    var type = module.Types.Single(t => t.Name == "TestBridge");
    var start = Method(type, "Start");
    int changed = 0;
    foreach (var i in start.Body.Instructions)
    {
        if (i.OpCode == OpCodes.Ldc_I4 && (int)i.Operand == 8330) { i.Operand = port; changed++; }
        if (i.OpCode == OpCodes.Ldstr) i.Operand = ((string)i.Operand).Replace("127.0.0.1:8330", $"127.0.0.1:{port}");
    }
    if (changed < 1) throw new InvalidOperationException("Bridge port call site changed");
    type.Fields.Single(f => f.Name == "Port").Constant = port;
    Prefix(start, Import(module, "Initialize"));
    Prefix(Method(module.Types.Single(t => t.Name == "TestBridgePump"), "Update"), Import(module, "Tick"));
    var health = Method(type, "HealthJson");
    // Retarget any branch to a return through the annotation call as well.
    foreach (var ret in health.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray())
    {
        ret.OpCode = OpCodes.Call; ret.Operand = Import(module, "Health");
        health.Body.GetILProcessor().InsertAfter(ret, Instruction.Create(OpCodes.Ret));
    }
    // Old pinned bridge builds get the same shared lock implementation as the normal source build.
    // New source builds already call it; avoid double reservations when the snapshot is upgraded.
    if (!module.AssemblyReferences.Any(r => r.Name == "BridgeControl"))
    {
        var http = Method(module.Types.Single(t => t.Name == "TestBridgeHttp"), "Call");
        var parse = http.Body.Instructions.Single(i => i.Operand is MethodReference m && m.DeclaringType.Name == "JObject" && m.Name == "Parse");
        http.Body.GetILProcessor().InsertAfter(parse, Instruction.Create(OpCodes.Call, Import(module, "PrepareBody")));
        var call = Method(type, "Call");
        var il = call.Body.GetILProcessor(); var firstCall = call.Body.Instructions[0];
        foreach (var instruction in new[] {
            Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldarg_1),
            Instruction.Create(OpCodes.Call, Import(module, "Intercept")), Instruction.Create(OpCodes.Dup),
            Instruction.Create(OpCodes.Brfalse, firstCall), Instruction.Create(OpCodes.Ret) }) il.InsertBefore(firstCall, instruction);
        // The null interception result remains on the stack on the original path.
        var pop = Instruction.Create(OpCodes.Pop); il.InsertBefore(firstCall, pop);
        call.Body.Instructions.First(i => i.OpCode == OpCodes.Brfalse && i.Operand == firstCall).Operand = pop;
        var queue = call.Body.Instructions.Single(i => i.Operand is MethodReference m && m.DeclaringType.Name == "TestBridgePump" && m.Name == "Run");
        var arg = Instruction.Create(OpCodes.Ldarg_1); il.InsertAfter(queue, arg);
        il.InsertAfter(arg, Instruction.Create(OpCodes.Call, Import(module, "Track")));
        foreach (var ret in call.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray())
        {
            ret.OpCode = OpCodes.Ldarg_1; ret.Operand = null;
            var after = Instruction.Create(OpCodes.Call, Import(module, "AfterCall"));
            il.InsertAfter(ret, after); il.InsertAfter(after, Instruction.Create(OpCodes.Ret));
        }
        var commands = module.Types.Single(t => t.Name == "TestBridgeCommands");
        var queued = Method(module.Types.Single(t => t.Name == "TestBridgePump"), "RunQueued");
        var work = queued.Body.Instructions.Single(i => i.Operand is MethodReference m && m.Name == "Invoke");
        queued.Body.GetILProcessor().InsertAfter(work, Instruction.Create(OpCodes.Call, Import(module, "AfterCommand")));
        Prefix(Method(commands, "EveryFrame"), Import(module, "RefreshBusy"));
        Prefix(Method(commands, "Quit"), Import(module, "MarkQuitting"));
    }
    var run = Method(module.Types.Single(t => t.Name == "TestBridgeCommands"), "Run");
    var first = run.Body.Instructions[0]; var p = run.Body.GetILProcessor();
    p.InsertBefore(first, Instruction.Create(OpCodes.Ldarg_0));
    p.InsertBefore(first, Instruction.Create(OpCodes.Ldarg_1));
    p.InsertBefore(first, Instruction.Create(OpCodes.Call, Import(module, "BeforeCommand")));
});
File.Copy(args[2], Path.Combine(output, "AgentTestRuntime.dll"), true);
File.WriteAllText(Path.Combine(output, "patch-manifest.json"), JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("Patched private test copies; original inputs unchanged.");
