using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading;

using HarmonyLib;

using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace SteadyTicks.Profiling
{
    /// <summary>
    /// Times every listener of every campaign event (the game's and every mod's) while the profiler is on.
    ///
    /// When the profiler is switched on, each listener delegate in each CampaignEvents event list is replaced by a small
    /// compiled wrapper that calls the original and records how long it took, under the event and the listener's owner type.
    /// Lists are checked again every report, so listeners added later (quests, new behaviors) are timed too. When the
    /// profiler is switched off, or the campaign ends, the original delegates are put back. Times are inclusive: an event
    /// fired from inside another listener counts in both. Only the main thread is timed.
    ///
    /// A few game methods that are not listeners (per-party ticks, battles) are timed with Harmony as "selected game methods".
    /// </summary>
    internal static class ListenerProfiler
    {
        internal sealed class Stat
        {
            public long Total, Max;
            public int Calls;

            public void Add(long ticks)
            {
                Total += ticks;
                Calls++;
                if (ticks > Max)
                {
                    Max = ticks;
                }
            }

            public void Clear()
            {
                Total = Max = 0;
                Calls = 0;
            }
        }

        /// <summary>One timed listener (or one party kind of it). Holds its own stats, so recording needs no lookup.</summary>
        internal sealed class Key
        {
            public string Event = "";
            public Type? Owner;
            public string? OwnerlessMethod; // set when the listener was registered without an owner: named by its method instead
            public string? Kind;
            public readonly Stat Window = new();
            public readonly Stat Session = new();

            public string Name => $"{Event}{(Kind is null ? "" : $" ({Kind})")}: " +
                                  (OwnerlessMethod is null ? Owner?.FullName ?? "(no owner)" : $"{Owner?.FullName}.{OwnerlessMethod} (no owner)");
            public string Mod => Owner is null ? "(unknown)" : ModName(Owner.Assembly);
        }

        private sealed class RecAccess
        {
            public Func<object, object?> List = null!, Next = null!, Owner = null!;
            public Func<object, Delegate> Action = null!;
            public FieldInfo ActionField = null!;
        }

        private static readonly List<Key> Keys = new();
        private static readonly Dictionary<object, (Delegate Original, FieldInfo Field)> Wrapped = new(ReferenceEqualityComparer.Instance);
        private static readonly Dictionary<Type, RecAccess?> Access = new();
        private static readonly Dictionary<Assembly, string> ModNames = new();
        private static readonly Dictionary<MethodBase, (Stat Window, Stat Session)> Details = new();

        private static readonly string[] Kinds = { "lord", "bandit", "caravan", "villager", "patrol", "garrison", "militia", "custom", "other" };
        private static int _mainThread;
        private static int _windows;

        private static readonly MethodInfo BeginMethod = AccessTools.Method(typeof(ListenerProfiler), nameof(Begin));
        private static readonly MethodInfo EndMethod = AccessTools.Method(typeof(ListenerProfiler), nameof(End));
        private static readonly MethodInfo EndPartyMethod = AccessTools.Method(typeof(ListenerProfiler), nameof(EndParty));

        /// <summary>The game loop's thread (module loading can run on another one). Set from the map frame.</summary>
        public static void SetMainThread() => _mainThread = Thread.CurrentThread.ManagedThreadId;

        public static void Apply(Harmony harmony)
        {
            _mainThread = Thread.CurrentThread.ManagedThreadId;

            var self = typeof(ListenerProfiler);
            var detailPrefix = Patching.Own(self, nameof(DetailPrefix));
            var detailPostfix = Patching.Own(self, nameof(DetailPostfix));
            void Detail(Type type, string method, Type[]? args = null) =>
                Patching.Apply(harmony, $"Detail.{type.Name}.{method}", AccessTools.Method(type, method, args), detailPrefix, detailPostfix, Priority.First, Priority.Last);

            // The game's own per-entity work that is not an event listener.
            Detail(typeof(MobileParty), "HourlyTick", Type.EmptyTypes);
            Detail(typeof(MobileParty), "DailyTick", Type.EmptyTypes);
            Detail(typeof(Town), "DailyTick", Type.EmptyTypes);
            Detail(typeof(Village), nameof(Village.DailyTick), Type.EmptyTypes);
            // Battles, encounters and hero life cycle.
            Detail(typeof(MapEvent), "Update", Type.EmptyTypes);
            Detail(typeof(MapEvent), "SimulateBattleSessionForMapEvent", Type.EmptyTypes);
            Detail(typeof(MapEvent), "CheckRunAway", Type.EmptyTypes);
            Detail(typeof(MapEvent), "FinalizeEventAux", Type.EmptyTypes);
            Detail(typeof(RaidEventComponent), "Update");
            Detail(typeof(EncounterManager), nameof(EncounterManager.StartPartyEncounter));
            Detail(typeof(HeroCreator), nameof(HeroCreator.CreateSpecialHero));
            Detail(typeof(KillCharacterAction), "ApplyInternal");
            Detail(typeof(CompanionsCampaignBehavior), "TrySpawnNewCompanion");
            Detail(typeof(CompanionsCampaignBehavior), "CreateCompanionAndAddToSettlement");
        }

        // ---- Wrapping event listeners ----

        private static readonly Func<Campaign, CampaignEvents>? CampaignEventsOf =
            AccessTools.PropertyGetter(typeof(Campaign), "CampaignEvents") is { } getter
                ? (Func<Campaign, CampaignEvents>)Delegate.CreateDelegate(typeof(Func<Campaign, CampaignEvents>), getter)
                : null;

        /// <summary>Wraps every listener not wrapped yet. Called when the profiler turns on and at every report.</summary>
        public static void WrapAll()
        {
            var campaign = Campaign.Current;
            if (campaign is null || CampaignEventsOf is null)
            {
                return;
            }

            WrapEvents(CampaignEventsOf(campaign));
        }

        /// <summary>Wraps every not yet wrapped listener of these events (also used by the offline PatchCheck tool).</summary>
        internal static void WrapEvents(CampaignEvents events)
        {
            try
            {
                int added = 0;
                foreach (var field in typeof(CampaignEvents).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                {
                    if (!IsMbEvent(field.FieldType) || field.GetValue(events) is not { } mbEvent)
                    {
                        continue;
                    }

                    var access = AccessFor(mbEvent.GetType());
                    if (access is null)
                    {
                        continue;
                    }

                    string eventName = EventName(field.Name);
                    for (object? rec = access.List(mbEvent); rec is not null; rec = access.Next(rec))
                    {
                        if (Wrapped.ContainsKey(rec))
                        {
                            continue;
                        }

                        var original = access.Action(rec);
                        Type? ownerType = access.Owner(rec)?.GetType();
                        if (ownerType is null)
                        {
                            Log.Write($"Profiler: {eventName} listener without an owner: {original.Method.DeclaringType?.FullName}.{original.Method.Name} ({original.Method.DeclaringType?.Assembly.GetName().Name})");
                        }

                        var wrapper = BuildWrapper(original, eventName, ownerType);
                        if (wrapper is null)
                        {
                            continue;
                        }

                        access.ActionField.SetValue(rec, wrapper);
                        Wrapped[rec] = (original, access.ActionField);
                        added++;
                    }
                }

                if (added > 0)
                {
                    Log.Write($"Profiler: timing {added} more event listeners ({Wrapped.Count} in total)");
                }
            }
            catch (Exception e)
            {
                Log.Error("Profiler: wrapping listeners", e);
            }
        }

        /// <summary>Puts every original listener back.</summary>
        public static void UnwrapAll()
        {
            foreach (var kv in Wrapped)
            {
                try
                {
                    kv.Value.Field.SetValue(kv.Key, kv.Value.Original);
                }
                catch (Exception e)
                {
                    Log.Error("Profiler: restoring a listener", e);
                }
            }

            Wrapped.Clear();
        }

        private static bool IsMbEvent(Type t) =>
            t == typeof(MbEvent) || (t.IsGenericType && t.GetGenericTypeDefinition().Name.StartsWith("MbEvent`", StringComparison.Ordinal)
                                     && t.Namespace == typeof(MbEvent).Namespace);

        private static string EventName(string field)
        {
            string name = field.TrimStart('_');
            if (name.EndsWith("Event", StringComparison.Ordinal) && name.Length > 5)
            {
                name = name.Substring(0, name.Length - 5);
            }

            return name.Length > 0 ? char.ToUpperInvariant(name[0]) + name.Substring(1) : field;
        }

        private static Delegate? BuildWrapper(Delegate original, string eventName, Type? owner)
        {
            try
            {
                var type = original.GetType();
                var parameters = type.GetMethod("Invoke")!.GetParameters().Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
                var start = Expression.Variable(typeof(long), "start");
                var call = Expression.Invoke(Expression.Constant(original, type), parameters);

                Expression record;
                if (parameters.Length > 0 && typeof(MobileParty).IsAssignableFrom(parameters[0].Type))
                {
                    // Listeners that take a party are split by party kind (lords, bandits, ...).
                    var keys = Kinds.Select(k => NewKey(eventName, owner, k, original)).ToArray();
                    record = Expression.Call(EndPartyMethod, Expression.Constant(keys), start, Expression.Convert(parameters[0], typeof(MobileParty)));
                }
                else
                {
                    record = Expression.Call(EndMethod, Expression.Constant(NewKey(eventName, owner, null, original)), start);
                }

                var body = Expression.Block(type.GetMethod("Invoke")!.ReturnType, new[] { start },
                    Expression.Assign(start, Expression.Call(BeginMethod)),
                    Expression.TryFinally(call, record));
                return Expression.Lambda(type, body, parameters).Compile();
            }
            catch (Exception e)
            {
                Log.Error($"Profiler: wrapping {eventName} listener of {owner?.FullName}", e);
                return null;
            }
        }

        private static Key NewKey(string eventName, Type? owner, string? kind, Delegate original)
        {
            var key = owner is null
                ? new Key { Event = eventName, Owner = original.Method.DeclaringType, OwnerlessMethod = original.Method.Name, Kind = kind }
                : new Key { Event = eventName, Owner = owner, Kind = kind };
            Keys.Add(key);
            return key;
        }

        private static long Begin() =>
            FrameProfiler.Enabled && Thread.CurrentThread.ManagedThreadId == _mainThread ? Stopwatch.GetTimestamp() : 0;

        private static void End(Key key, long start)
        {
            if (start != 0)
            {
                long ticks = Stopwatch.GetTimestamp() - start;
                key.Window.Add(ticks);
                key.Session.Add(ticks);
            }
        }

        private static void EndParty(Key[] keys, long start, MobileParty? party)
        {
            if (start != 0)
            {
                End(keys[party is null ? 8 : PartyKind(party)], start);
            }
        }

        public static int PartyKind(MobileParty p) =>
            p.IsLordParty ? 0 : p.IsBandit ? 1 : p.IsCaravan ? 2 : p.IsVillager ? 3 : p.IsPatrolParty ? 4 : p.IsGarrison ? 5 : p.IsMilitia ? 6 : p.IsCustomParty ? 7 : 8;

        public static string KindName(int kind) => Kinds[kind];

        private static RecAccess? AccessFor(Type eventType)
        {
            if (Access.TryGetValue(eventType, out var access))
            {
                return access;
            }

            try
            {
                var listField = AccessTools.Field(eventType, "_nonSerializedListenerList");
                var recType = listField.FieldType;
                var actionField = AccessTools.Field(recType, "<Action>k__BackingField")
                                  ?? throw new MissingFieldException(recType.FullName, "Action backing field");
                access = new RecAccess
                {
                    List = Getter<object?>(listField.DeclaringType!, Expression.Field(Expression.Convert(Obj, listField.DeclaringType!), listField)),
                    Next = Getter<object?>(recType, Expression.Field(Expression.Convert(Obj, recType), AccessTools.Field(recType, "Next"))),
                    Owner = Getter<object?>(recType, Expression.Property(Expression.Convert(Obj, recType), AccessTools.Property(recType, "Owner"))),
                    Action = Getter<Delegate>(recType, Expression.Field(Expression.Convert(Obj, recType), actionField)),
                    ActionField = actionField,
                };
            }
            catch (Exception e)
            {
                Log.Error($"Profiler: event type {eventType}", e);
                access = null;
            }

            Access[eventType] = access;
            return access;
        }

        private static readonly ParameterExpression Obj = Expression.Parameter(typeof(object), "o");

        private static Func<object, TResult> Getter<TResult>(Type _, Expression member) =>
            Expression.Lambda<Func<object, TResult>>(Expression.Convert(member, typeof(TResult)), Obj).Compile();

        // ---- Selected game methods ----

        private struct DetailState
        {
            public long Start;
        }

        private static void DetailPrefix(out DetailState __state)
        {
            __state = new DetailState { Start = Thread.CurrentThread.ManagedThreadId == _mainThread ? FrameProfiler.Begin() : 0 };
        }

        private static void DetailPostfix(DetailState __state, MethodBase __originalMethod)
        {
            if (__state.Start == 0)
            {
                return;
            }

            long ticks = Stopwatch.GetTimestamp() - __state.Start;
            if (!Details.TryGetValue(__originalMethod, out var stats))
            {
                stats = (new Stat(), new Stat());
                Details[__originalMethod] = stats;
            }

            stats.Window.Add(ticks);
            stats.Session.Add(ticks);
        }

        // ---- Reports ----

        public static void ResetWindow()
        {
            foreach (var key in Keys)
            {
                key.Window.Clear();
            }

            foreach (var d in Details.Values)
            {
                d.Window.Clear();
            }
        }

        /// <summary>Called when the campaign ends or the profiler turns off.</summary>
        public static void ResetSession()
        {
            UnwrapAll();
            Keys.Clear();
            Details.Clear();
            _windows = 0;
        }

        private static string ModName(Assembly assembly)
        {
            if (!ModNames.TryGetValue(assembly, out string? name))
            {
                name = assembly.GetName().Name ?? "?";
                string an = name;
                if (an.StartsWith("TaleWorlds.", StringComparison.Ordinal) || an.StartsWith("SandBox", StringComparison.Ordinal)
                    || an.StartsWith("StoryMode", StringComparison.Ordinal) || an.StartsWith("NavalDLC", StringComparison.Ordinal))
                {
                    name = "Game (" + an + ")";
                }

                ModNames[assembly] = name;
            }

            return name;
        }

        public static void AppendReport(StringBuilder sb, int frames, double msPerTick)
        {
            var inv = CultureInfo.InvariantCulture;
            string Line(Stat s, string name) =>
                $"\n    {(s.Total * msPerTick / frames).ToString("0.000", inv),7} avg / {(s.Max * msPerTick).ToString("0.00", inv),7} worst, {s.Calls,6} calls  {name}";

            var active = Keys.Where(k => k.Window.Calls > 0).ToList();
            if (active.Count > 0)
            {
                var byTotal = active.OrderByDescending(k => k.Window.Total).Take(15).ToList();
                sb.Append("\n  top listeners by total time (ms per frame avg / worst single call, inclusive):");
                foreach (var k in byTotal)
                {
                    sb.Append(Line(k.Window, k.Name));
                }

                var byWorst = active.Where(k => !byTotal.Contains(k)).OrderByDescending(k => k.Window.Max).Take(10).ToList();
                if (byWorst.Count > 0)
                {
                    sb.Append("\n  other listeners with the worst single calls:");
                    foreach (var k in byWorst)
                    {
                        sb.Append(Line(k.Window, k.Name));
                    }
                }

                sb.Append("\n  listener time by mod (ms per frame avg / worst single call):");
                foreach (var g in active.GroupBy(k => k.Mod).Select(g => (Mod: g.Key, Total: g.Sum(k => k.Window.Total), Max: g.Max(k => k.Window.Max), Calls: g.Sum(k => k.Window.Calls)))
                             .OrderByDescending(g => g.Total).Take(15))
                {
                    sb.Append($"\n    {(g.Total * msPerTick / frames).ToString("0.000", inv),7} avg / {(g.Max * msPerTick).ToString("0.00", inv),7} worst, {g.Calls,6} calls  {g.Mod}");
                }
            }

            var details = Details.Where(d => d.Value.Window.Calls > 0).OrderByDescending(d => d.Value.Window.Total).ToList();
            if (details.Count > 0)
            {
                sb.Append("\n  selected game methods (ms per frame avg / worst single call):");
                foreach (var d in details)
                {
                    sb.Append(Line(d.Value.Window, $"{d.Key.DeclaringType?.Name}.{d.Key.Name}"));
                }
            }

            WriteListenerRows(active, details, frames, msPerTick);

            // A running summary of the whole session, rewritten every minute or so of map time.
            if (++_windows % 6 == 0)
            {
                WriteSessionSummary(msPerTick);
            }
        }

        /// <summary>
        /// SteadyTicks.listeners.csv: one row per listener (and selected game method) costing at least 0.005 ms per frame in
        /// this report, with the in-game date, so a long run shows which code gets more expensive over time.
        /// </summary>
        private static void WriteListenerRows(List<Key> active, List<KeyValuePair<MethodBase, (Stat Window, Stat Session)>> details, int frames, double msPerTick)
        {
            try
            {
                string path = Path.Combine(Log.Directory, "SteadyTicks.listeners.csv");
                bool newFile = !File.Exists(path);
                var inv = CultureInfo.InvariantCulture;
                string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", inv);
                string date = "\"" + CampaignTime.Now + "\"";
                string Q(string s) => "\"" + s.Replace("\"", "'") + "\"";
                string Row(Stat s, string evt, string kind, string owner, string mod) =>
                    string.Join(",", now, date, Q(evt), Q(kind), Q(owner), Q(mod), (s.Total * msPerTick / frames).ToString("0.0000", inv),
                        (s.Max * msPerTick).ToString("0.000", inv), s.Calls.ToString(inv));

                using var w = new StreamWriter(path, true, new UTF8Encoding(false)) { NewLine = "\n" };
                if (newFile)
                {
                    w.WriteLine("time,ingame_date,event,party_kind,owner,mod,avg_ms_per_frame,worst_ms,calls");
                }

                double min = 0.005 / msPerTick * frames;
                foreach (var k in active.Where(k => k.Window.Total >= min))
                {
                    w.WriteLine(Row(k.Window, k.Event, k.Kind ?? "", k.Owner?.FullName ?? "", k.Mod));
                }

                foreach (var d in details.Where(d => d.Value.Window.Total >= min))
                {
                    w.WriteLine(Row(d.Value.Window, "(game method)", "", $"{d.Key.DeclaringType?.FullName}.{d.Key.Name}", "Game"));
                }
            }
            catch (Exception e)
            {
                Log.Error("Profiler: listener rows", e);
            }
        }

        /// <summary>SteadyTicks.summary.log: totals since the campaign was loaded, for long runs.</summary>
        public static void WriteSessionSummary(double msPerTick)
        {
            try
            {
                var inv = CultureInfo.InvariantCulture;
                string Ms(long ticks) => (ticks * msPerTick).ToString("0.0", inv);
                var sb = new StringBuilder();
                sb.Append($"Steady Ticks session summary, written {DateTime.Now:yyyy-MM-dd HH:mm:ss}, {Ms(FrameProfiler.SessionMapTicks)} ms of map time over {FrameProfiler.SessionFrames} frames");
                sb.Append($"\nin-game date {CampaignTime.Now}; times are totals for the session, worst is the slowest single call");

                var keys = Keys.Where(k => k.Session.Calls > 0).ToList();
                sb.Append("\n\nTime by mod (total ms / worst single ms / calls):");
                foreach (var g in keys.GroupBy(k => k.Mod).OrderByDescending(g => g.Sum(k => k.Session.Total)))
                {
                    sb.Append($"\n  {Ms(g.Sum(k => k.Session.Total)),10} / {Ms(g.Max(k => k.Session.Max)),8} / {g.Sum(k => k.Session.Calls),9}  {g.Key}");
                }

                sb.Append("\n\nTop 40 listeners by total time:");
                foreach (var k in keys.OrderByDescending(k => k.Session.Total).Take(40))
                {
                    sb.Append($"\n  {Ms(k.Session.Total),10} / {Ms(k.Session.Max),8} / {k.Session.Calls,9}  {k.Name}");
                }

                sb.Append("\n\nTop 40 listeners by worst single call:");
                foreach (var k in keys.OrderByDescending(k => k.Session.Max).Take(40))
                {
                    sb.Append($"\n  {Ms(k.Session.Total),10} / {Ms(k.Session.Max),8} / {k.Session.Calls,9}  {k.Name}");
                }

                sb.Append("\n\nSelected game methods:");
                foreach (var d in Details.OrderByDescending(d => d.Value.Session.Total))
                {
                    sb.Append($"\n  {Ms(d.Value.Session.Total),10} / {Ms(d.Value.Session.Max),8} / {d.Value.Session.Calls,9}  {d.Key.DeclaringType?.Name}.{d.Key.Name}");
                }

                File.WriteAllText(Path.Combine(Log.Directory, "SteadyTicks.summary.log"), sb.ToString().Replace("\n", Environment.NewLine), new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Log.Error("Profiler: session summary", e);
            }
        }

        /// <summary>SteadyTicks.patches.log: every game method other mods patch with Harmony, by mod. Written once per session.</summary>
        public static void WritePatchReport()
        {
            try
            {
                var byOwner = new Dictionary<string, List<string>>();
                foreach (var method in Harmony.GetAllPatchedMethods())
                {
                    var info = Harmony.GetPatchInfo(method);
                    if (info is null)
                    {
                        continue;
                    }

                    string name = $"{method.DeclaringType?.FullName}.{method.Name}";
                    foreach (var owner in info.Owners.Where(o => o != SubModule.HarmonyId))
                    {
                        if (!byOwner.TryGetValue(owner, out var list))
                        {
                            byOwner[owner] = list = new List<string>();
                        }

                        list.Add(name);
                    }
                }

                var sb = new StringBuilder($"Harmony patches by other mods ({DateTime.Now:yyyy-MM-dd HH:mm:ss}): {byOwner.Sum(kv => kv.Value.Count)} patched methods, {byOwner.Count} Harmony ids");
                foreach (var kv in byOwner.OrderByDescending(kv => kv.Value.Count))
                {
                    sb.Append($"\n\n{kv.Key} ({kv.Value.Count})");
                    foreach (string m in kv.Value.OrderBy(m => m))
                    {
                        sb.Append("\n  " + m);
                    }
                }

                File.WriteAllText(Path.Combine(Log.Directory, "SteadyTicks.patches.log"), sb.ToString().Replace("\n", Environment.NewLine), new UTF8Encoding(false));
                Log.Write($"Profiler: other mods patch {byOwner.Sum(kv => kv.Value.Count)} game methods (list in SteadyTicks.patches.log)");
            }
            catch (Exception e)
            {
                Log.Error("Profiler: patch report", e);
            }
        }
    }

    /// <summary>Reference equality for listener records (net472 has no built-in one).</summary>
    internal sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceEqualityComparer Instance = new();
        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
