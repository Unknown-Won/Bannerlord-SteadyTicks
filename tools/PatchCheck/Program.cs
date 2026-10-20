// Offline check: loads SteadyTicks.dll next to the game assemblies, applies every patch and lists what was hit.
// Usage: PatchCheck.exe [path to SteadyTicks.dll]
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;

using HarmonyLib;

internal static class Program
{
    // Set at build time from SteadyTicks.local.props (see PatchCheck.csproj).
    private static string Setting(string key) =>
        typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value ?? "";

    private static readonly string Game = Setting("GameFolder");

    private static readonly string[] SearchDirs =
    {
        Path.Combine(Game, @"bin\Win64_Shipping_Client"),
        Path.Combine(Game, @"Modules\Native\bin\Win64_Shipping_Client"),
        Path.Combine(Game, @"Modules\SandBoxCore\bin\Win64_Shipping_Client"),
        Path.Combine(Game, @"Modules\SandBox\bin\Win64_Shipping_Client"),
        Path.Combine(Game, @"Modules\StoryMode\bin\Win64_Shipping_Client"),
        Setting("HarmonyFolder"),
        Setting("McmFolder"),
    };

    private static int Main(string[] args)
    {
        // Keep test output out of the game's log folder.
        string logDir = Path.Combine(Path.GetTempPath(), "SteadyTicksPatchCheck");
        Directory.CreateDirectory(logDir);
        Environment.SetEnvironmentVariable("STEADYTICKS_LOGDIR", logDir);

        string dll = args.Length > 0 ? args[0] : Path.GetFullPath(@"..\..\dist\SteadyTicks\bin\Win64_Shipping_Client\SteadyTicks.dll");
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            string name = new AssemblyName(e.Name).Name + ".dll";
            foreach (var dir in SearchDirs.Append(Path.GetDirectoryName(dll)!))
            {
                string path = Path.Combine(dir, name);
                if (File.Exists(path))
                {
                    return Assembly.LoadFrom(path);
                }
            }

            return null;
        };

        var asm = Assembly.LoadFrom(dll);
        var harmony = new Harmony("steadyticks.patchcheck");
        asm.GetType("SteadyTicks.SubModule")!.GetMethod("ApplyPatches", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { harmony });

        var failed = (IList)asm.GetType("SteadyTicks.Patching")!.GetField("Failed", BindingFlags.Static | BindingFlags.Public)!.GetValue(null)!;
        int count = 0;
        foreach (var method in harmony.GetPatchedMethods().OrderBy(m => m.DeclaringType?.FullName + "." + m.Name))
        {
            var info = Harmony.GetPatchInfo(method);
            Console.WriteLine($"  {method.DeclaringType?.FullName}.{method.Name}: {info.Prefixes.Count} prefix, {info.Postfixes.Count} postfix");
            count++;
        }

        Console.WriteLine($"{count} methods patched, {failed.Count} failed{(failed.Count > 0 ? ": " + string.Join(", ", failed.Cast<string>()) : "")}");
        bool spreadOk = SpreadTest(asm);
        var speedField = AccessTools.Field(AccessTools.TypeByName("TaleWorlds.CampaignSystem.Party.MobileParty"), "_lastCalculatedSpeed");
        Console.WriteLine($"Party speed field: {(speedField is null ? "MISSING" : "ok")}");
        bool profiledOk = ProfiledTest(asm);
        bool modOk = ModSpreadTest(asm);
        return failed.Count == 0 && spreadOk && profiledOk && modOk ? 0 : 1;
    }

    /// <summary>Fires a patched hourly event with game and mod listeners and checks what runs when.</summary>
    private static bool SpreadTest(Assembly mod)
    {
        var st = AccessTools.Property(mod.GetType("SteadyTicks.Settings")!, "Current").GetValue(null);
        AccessTools.Property(st.GetType(), "SpreadAllMods").SetValue(st, false);
        var campaignAsm = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "TaleWorlds.CampaignSystem");
        var eventsType = campaignAsm.GetType("TaleWorlds.CampaignSystem.CampaignEvents")!;
        object events = Activator.CreateInstance(eventsType, true)!;
        object hourly = AccessTools.Field(eventsType, "_hourlyTickEvent").GetValue(events);
        var add = AccessTools.Method(hourly.GetType(), "AddNonSerializedListener");

        var ran = new System.Collections.Generic.List<string>();
        add.Invoke(hourly, new object[] { events, (Action)(() => ran.Add("game1")) });
        add.Invoke(hourly, new object[] { new object(), (Action)(() => ran.Add("mod1")) });
        add.Invoke(hourly, new object[] { events, (Action)(() => ran.Add("game2")) });

        AccessTools.Method(eventsType, "HourlyTick").Invoke(events, null);
        string onTheHour = string.Join(",", ran);
        var spreader = mod.GetType("SteadyTicks.Spreading.TickSpreader")!;
        int pending = (int)AccessTools.Property(spreader, "PendingCount").GetValue(null);
        AccessTools.Method(spreader, "Flush").Invoke(null, null);
        string all = string.Join(",", ran);

        // The game runs the newest listener first: game2, mod1, game1.
        bool ok = onTheHour == "mod1" && pending == 2 && all == "mod1,game2,game1";
        Console.WriteLine($"Spread test: on the hour [{onTheHour}], queued {pending}, after flush [{all}] -> {(ok ? "OK" : "FAILED")}");
        return ok;
    }

    /// <summary>
    /// With the profiler on, every listener is wrapped: each still runs once, in the game's order, and is timed under its event
    /// and owner (party listeners split by party kind). Unwrapping puts the original delegates back.
    /// </summary>
    private static bool ProfiledTest(Assembly mod)
    {
        var campaignAsm = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "TaleWorlds.CampaignSystem");
        var eventsType = campaignAsm.GetType("TaleWorlds.CampaignSystem.CampaignEvents")!;
        var settings = AccessTools.Property(mod.GetType("SteadyTicks.Settings")!, "Current").GetValue(null);
        AccessTools.Property(settings.GetType(), "SpreadTicks").SetValue(settings, false);
        var frameProfiler = mod.GetType("SteadyTicks.Profiling.FrameProfiler")!;
        AccessTools.Property(frameProfiler, "Enabled").SetValue(null, true);
        var profiler = mod.GetType("SteadyTicks.Profiling.ListenerProfiler")!;

        object events = Activator.CreateInstance(eventsType, true)!;
        var ran = new System.Collections.Generic.List<string>();
        object EventOf(string field) => AccessTools.Field(eventsType, field).GetValue(events);
        void Add(string field, object owner, Delegate action) =>
            AccessTools.Method(EventOf(field).GetType(), "AddNonSerializedListener").Invoke(EventOf(field), new[] { owner, action });

        Action<float> t1 = _ => ran.Add("t1");
        Add("_tickEvent", events, t1);
        Add("_tickEvent", new object(), (Action<float>)(_ => ran.Add("t2")));
        Add("_hourlyTickEvent", events, (Action)(() => ran.Add("h1")));
        Add("_hourlyTickEvent", new object(), (Action)(() => ran.Add("h2")));
        var party = campaignAsm.GetType("TaleWorlds.CampaignSystem.Party.MobileParty")!;
        var think = campaignAsm.GetType("TaleWorlds.CampaignSystem.PartyThinkParams")!;
        var aiAction = typeof(Action<,>).MakeGenericType(party, think);
        Add("_aiHourlyTickEvent", events, Delegate.CreateDelegate(aiAction, new AiRecorder(ran), typeof(AiRecorder).GetMethod("Run")!.MakeGenericMethod(party, think)));

        AccessTools.Method(profiler, "WrapEvents").Invoke(null, new[] { events });
        // Fire the events directly (CampaignEvents' own methods need a running campaign).
        void Fire(string field, params object?[] args) => AccessTools.Method(EventOf(field).GetType(), "Invoke").Invoke(EventOf(field), args);
        Fire("_tickEvent", 1f);
        Fire("_hourlyTickEvent");
        Fire("_aiHourlyTickEvent", null, null);

        var sb = new System.Text.StringBuilder();
        AccessTools.Method(profiler, "AppendReport").Invoke(null, new object[] { sb, 1, 1000.0 / System.Diagnostics.Stopwatch.Frequency });
        string report = sb.ToString();

        // Unwrap: the first tick listener must be the original delegate again.
        AccessTools.Method(profiler, "UnwrapAll").Invoke(null, null);
        object tickEvent = EventOf("_tickEvent");
        object rec = AccessTools.Field(tickEvent.GetType(), "_nonSerializedListenerList").GetValue(tickEvent);
        while (AccessTools.Field(rec.GetType(), "Next").GetValue(rec) is { } next)
        {
            rec = next;
        }

        bool restored = ReferenceEquals(AccessTools.Property(rec.GetType(), "Action").GetValue(rec), t1);
        AccessTools.Property(frameProfiler, "Enabled").SetValue(null, false);

        string all = string.Join(",", ran);
        bool ok = all == "t2,t1,h2,h1,ai" && report.Contains("Tick: System.Object") && report.Contains("HourlyTick: TaleWorlds")
                  && report.Contains("AiHourlyTick (other)") && report.Contains("listener time by mod") && restored;
        Console.WriteLine($"Profiled wrap test: [{all}], originals restored {restored} -> {(ok ? "OK" : "FAILED")}{report}");
        return ok;
    }

    private sealed class AiRecorder
    {
        private readonly System.Collections.Generic.List<string> _ran;
        public AiRecorder(System.Collections.Generic.List<string> ran) => _ran = ran;
        public void Run<T1, T2>(T1 a, T2 b) => _ran.Add("ai");
    }

    /// <summary>Other mods are queued like the game's listeners; a mod kept on the hour runs right away.</summary>
    private static bool ModSpreadTest(Assembly mod)
    {
        var campaignAsm = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "TaleWorlds.CampaignSystem");
        var eventsType = campaignAsm.GetType("TaleWorlds.CampaignSystem.CampaignEvents")!;
        var settings = AccessTools.Property(mod.GetType("SteadyTicks.Settings")!, "Current").GetValue(null);
        AccessTools.Property(settings.GetType(), "SpreadTicks").SetValue(settings, true);
        AccessTools.Property(settings.GetType(), "SpreadAllMods").SetValue(settings, true);
        AccessTools.Property(settings.GetType(), "EnableProfiler").SetValue(settings, false);

        object events = Activator.CreateInstance(eventsType, true)!;
        object hourly = AccessTools.Field(eventsType, "_hourlyTickEvent").GetValue(events);
        var add = AccessTools.Method(hourly.GetType(), "AddNonSerializedListener");
        var ran = new System.Collections.Generic.List<string>();
        add.Invoke(hourly, new object[] { new Picked(), (Action)(() => ran.Add("picked")) });
        add.Invoke(hourly, new object[] { new object(), (Action)(() => ran.Add("other")) });

        var spreader = mod.GetType("SteadyTicks.Spreading.TickSpreader")!;
        var modSpreading = mod.GetType("SteadyTicks.Spreading.ModSpreading")!;
        var listeners = AccessTools.Method(spreader, "ModListeners").Invoke(null, new[] { events });
        var listType = typeof(System.Collections.Generic.List<>).MakeGenericType(listeners.GetType().GetInterfaces().First(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(System.Collections.Generic.IEnumerable<>)).GetGenericArguments()[0]);
        var list = Activator.CreateInstance(listType, listeners);
        AccessTools.Method(modSpreading, "Discover").Invoke(null, new[] { list });

        // Keep the test assembly on the hour, as the MCM toggle would.
        var mods = (IDictionary)AccessTools.Field(modSpreading, "Mods").GetValue(null);
        object entry = mods[typeof(Program).Assembly.GetName().Name!]!;
        AccessTools.Field(entry.GetType(), "Spread").SetValue(entry, false);
        ((IDictionary)AccessTools.Field(modSpreading, "ByAssembly").GetValue(null)).Clear();

        AccessTools.Method(eventsType, "HourlyTick").Invoke(events, null);
        string onTheHour = string.Join(",", ran);
        AccessTools.Method(spreader, "Flush").Invoke(null, null);
        bool ok = onTheHour == "picked" && string.Join(",", ran) == "picked,other" && mods.Count == 2;
        Console.WriteLine($"Mod spread test: {mods.Count} mods found, on the hour [{onTheHour}], after flush [{string.Join(",", ran)}] -> {(ok ? "OK" : "FAILED")}");
        return ok;
    }

    private sealed class Picked { }
}
