using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

using HarmonyLib;

using SteadyTicks.Profiling;

using TaleWorlds.CampaignSystem;

namespace SteadyTicks.Spreading
{
    /// <summary>
    /// Spreads the listeners of the global hourly, daily and weekly campaign events over the following frames.
    ///
    /// The game already spreads the per-party, per-settlement, per-clan and per-hero ticks and the hourly AI across the hour.
    /// Only these three global events run every listener in one frame. When one fires, the game's listeners and (by default)
    /// other mods' listeners are queued in their original order; mods the player keeps on the hour run right away. Every
    /// listener still runs exactly once per event. The queue runs at the start of the next campaign ticks: each frame takes
    /// listeners until the next one's learned cost would exceed the budget, so a heavy listener gets a frame to itself. The
    /// budget grows when needed to finish before the next in-game hour, and anything left is finished before the next hourly
    /// event and before a save, so no work slips past the hour it belongs to.
    /// </summary>
    internal static class TickSpreader
    {
        private enum Kind { Hourly, Daily, Weekly }

        private readonly struct Item
        {
            public readonly int Frame;
            public readonly Action Action;
            public readonly Type? Owner;
            public readonly string Event;

            public Item(int frame, Action action, Type? owner, string eventName)
            {
                Frame = frame;
                Action = action;
                Owner = owner;
                Event = eventName;
            }
        }

        private static readonly Queue<Item> Pending = new();
        private static readonly Dictionary<Assembly, bool> BaseGameAssemblies = new();
        private static readonly bool[] SplitLogged = new bool[3];

        private static FieldInfo? _listField, _nextField;
        private static PropertyInfo? _actionProperty, _ownerProperty;
        private static AccessTools.FieldRef<CampaignEvents, MbEvent>? _hourly, _daily, _weekly;

        public static int PendingCount => Pending.Count;

        public static void Apply(Harmony harmony)
        {
            if (!ResolveInternals())
            {
                Patching.Failed.Add("Spread ticks");
                Log.Warn("Spread ticks: the game's event internals changed, feature disabled");
                return;
            }

            var self = typeof(TickSpreader);
            Patching.Apply(harmony, "Spread.Hourly", AccessTools.Method(typeof(CampaignEvents), nameof(CampaignEvents.HourlyTick), Type.EmptyTypes),
                prefix: Patching.Own(self, nameof(HourlyPrefix)));
            Patching.Apply(harmony, "Spread.Daily", AccessTools.Method(typeof(CampaignEvents), nameof(CampaignEvents.DailyTick), Type.EmptyTypes),
                prefix: Patching.Own(self, nameof(DailyPrefix)));
            Patching.Apply(harmony, "Spread.Weekly", AccessTools.Method(typeof(CampaignEvents), nameof(CampaignEvents.WeeklyTick), Type.EmptyTypes),
                prefix: Patching.Own(self, nameof(WeeklyPrefix)));
            // Runs just after the profiler's own Campaign.Tick prefix, so the campaign tick time includes the queue.
            Patching.Apply(harmony, "Spread.Drain", AccessTools.Method(typeof(Campaign), "Tick", Type.EmptyTypes),
                prefix: Patching.Own(self, nameof(DrainPrefix)), prefixPriority: Priority.VeryHigh);
            Patching.Apply(harmony, "Spread.FlushBeforeSave", AccessTools.Method(typeof(SaveHandler), "SaveTick", Type.EmptyTypes),
                prefix: Patching.Own(self, nameof(SaveTickPrefix)));
        }

        private static bool ResolveInternals()
        {
            _listField = AccessTools.Field(typeof(MbEvent), "_nonSerializedListenerList");
            var recType = _listField?.FieldType;
            if (recType is null)
            {
                return false;
            }

            _nextField = AccessTools.Field(recType, "Next");
            _actionProperty = AccessTools.Property(recType, "Action");
            _ownerProperty = AccessTools.Property(recType, "Owner");
            if (_nextField is null || _actionProperty is null || _ownerProperty is null)
            {
                return false;
            }

            try
            {
                _hourly = AccessTools.FieldRefAccess<CampaignEvents, MbEvent>("_hourlyTickEvent");
                _daily = AccessTools.FieldRefAccess<CampaignEvents, MbEvent>("_dailyTickEvent");
                _weekly = AccessTools.FieldRefAccess<CampaignEvents, MbEvent>("_weeklyTickEvent");
                return true;
            }
            catch (Exception e)
            {
                Log.Error("Spread ticks", e);
                return false;
            }
        }

        public static void Clear()
        {
            Costs.Clear();
            _hoursPerFrame = 0;
            _lastFrameHours = -1;
            _discovered = false;
            Pending.Clear();
            Array.Clear(SplitLogged, 0, SplitLogged.Length);
        }

        /// <summary>Runs everything still queued, in order.</summary>
        public static void Flush()
        {
            while (Pending.Count > 0)
            {
                RunNext();
            }
        }

        // Learned cost of each listener (Stopwatch ticks, moving average), so the queue can give a heavy listener a frame to
        // itself and pack light ones together. Keyed by the listener method; learned while playing, kept for the session.
        private static readonly Dictionary<MethodInfo, double> Costs = new();
        private const double CostSmoothing = 0.3;

        private static double PredictedCost(in Item item) => Costs.TryGetValue(item.Action.Method, out double c) ? c : 0;

        private static void RunNext()
        {
            var item = Pending.Dequeue();
            long start = Stopwatch.GetTimestamp();
            item.Action();
            double ticks = Stopwatch.GetTimestamp() - start;
            var key = item.Action.Method;
            Costs[key] = Costs.TryGetValue(key, out double old) ? old + (ticks - old) * CostSmoothing : ticks;
        }

        private static readonly string[] EventNames = { "hourly", "daily", "weekly" };
        private static readonly string[] SpreadEventNames = { "hourly (spread)", "daily (spread)", "weekly (spread)" };

        private static int CurrentFrame => Campaign.Current?.CurrentTickCount ?? 0;

        private static bool HourlyPrefix(CampaignEvents __instance) => Dispatch(__instance, Kind.Hourly);
        private static bool DailyPrefix(CampaignEvents __instance) => Dispatch(__instance, Kind.Daily);
        private static bool WeeklyPrefix(CampaignEvents __instance) => Dispatch(__instance, Kind.Weekly);

        private static bool Dispatch(CampaignEvents events, Kind kind)
        {
            bool spreading = Settings.Current.SpreadTicks;
            if (!spreading)
            {
                Flush();
                return true;
            }

            // Work queued in earlier frames belongs to an earlier hour: finish it first. Work from this frame (e.g. the hourly
            // event at midnight, just before the daily one) stays queued behind it, so the order is unchanged.
            int frame = CurrentFrame;
            while (Pending.Count > 0 && Pending.Peek().Frame != frame)
            {
                RunNext();
            }

            var mbEvent = kind switch
            {
                Kind.Hourly => _hourly!(events),
                Kind.Daily => _daily!(events),
                _ => _weekly!(events),
            };

            List<string>? modOwners = !spreading || SplitLogged[(int)kind] ? null : new List<string>();
            int spread = 0, onTheHour = 0;

            // Same walk as MbEvent.Invoke (newest listener first). With spreading off, this only runs while profiling,
            // to time each listener, and runs them all right away as the game does.
            object? rec = _listField!.GetValue(mbEvent);
            while (rec is not null)
            {
                var action = (Action)_actionProperty!.GetValue(rec);
                object? owner = _ownerProperty!.GetValue(rec);
                if (spreading && (IsBaseGame(owner) || ModSpreading.IsSpread(owner)))
                {
                    Pending.Enqueue(new Item(frame, action, owner?.GetType(), SpreadEventNames[(int)kind]));
                    spread++;
                }
                else
                {
                    action();
                    onTheHour++;
                    modOwners?.Add(owner?.GetType().FullName ?? "(no owner)");
                }

                rec = _nextField!.GetValue(rec);
            }

            if (spreading)
            {
                FrameProfiler.Count(Counter.ListenersSpread, spread);
                FrameProfiler.Count(Counter.ListenersOnTheHour, onTheHour);
            }

            if (modOwners is not null)
            {
                SplitLogged[(int)kind] = true;
                Log.Write($"{kind} event: {spread} listeners spread over the next frames, {onTheHour} mod listeners run on the hour" +
                          (modOwners.Count > 0 ? ": " + string.Join(", ", modOwners.Distinct()) : string.Empty));
            }

            return false;
        }

        /// <summary>Every hourly (0), daily (1) and weekly (2) listener owner, for listing the mods in MCM.</summary>
        public static IEnumerable<(int Kind, object? Owner)> ModListeners(CampaignEvents events)
        {
            if (_listField is null)
            {
                yield break;
            }

            var lists = new[] { _hourly!(events), _daily!(events), _weekly!(events) };
            for (int kind = 0; kind < 3; kind++)
            {
                for (object? rec = _listField.GetValue(lists[kind]); rec is not null; rec = _nextField!.GetValue(rec))
                {
                    object? owner = _ownerProperty!.GetValue(rec);
                    if (owner is not null && !IsBaseGame(owner))
                    {
                        yield return (kind, owner);
                    }
                }
            }
        }

        private static bool IsBaseGame(object? owner)
        {
            if (owner is null)
            {
                return false;
            }

            var assembly = owner.GetType().Assembly;
            if (!BaseGameAssemblies.TryGetValue(assembly, out bool isBase))
            {
                string name = assembly.GetName().Name ?? string.Empty;
                isBase = name.StartsWith("TaleWorlds.", StringComparison.Ordinal)
                         || name.StartsWith("SandBox", StringComparison.Ordinal)
                         || name.StartsWith("StoryMode", StringComparison.Ordinal)
                         || name.StartsWith("NavalDLC", StringComparison.Ordinal);
                BaseGameAssemblies[assembly] = isBase;
            }

            return isBase;
        }

        private static bool _discovered;
        private static readonly Func<Campaign, CampaignEvents>? CampaignEventsOf =
            AccessTools.PropertyGetter(typeof(Campaign), "CampaignEvents") is { } getter
                ? (Func<Campaign, CampaignEvents>)Delegate.CreateDelegate(typeof(Func<Campaign, CampaignEvents>), getter)
                : null;

        /// <summary>Once per campaign session, after every behavior has registered: list the mods for the MCM page.</summary>
        private static void DiscoverMods()
        {
            _discovered = true;
            try
            {
                var campaign = Campaign.Current;
                if (campaign is not null && CampaignEventsOf is not null)
                {
                    ModSpreading.Discover(ModListeners(CampaignEventsOf(campaign)).ToList());
                }
            }
            catch (Exception e)
            {
                Log.Error("Listing mods", e);
            }
        }

        // In-game hours per campaign frame (moving average), to know how many frames are left before the next hour.
        private static double _hoursPerFrame;
        private static double _lastFrameHours = -1;

        private static void TrackFrameHours()
        {
            double now = CampaignTime.Now.ToHours;
            if (_lastFrameHours >= 0 && now >= _lastFrameHours)
            {
                _hoursPerFrame += (now - _lastFrameHours - _hoursPerFrame) * 0.1;
            }

            _lastFrameHours = now;
        }

        /// <summary>
        /// Time this frame may spend on the queue: the setting, or more if the predicted work left would not otherwise finish
        /// before the next in-game hour (aiming one frame early), so the hourly flush never has to catch up in one frame.
        /// </summary>
        private static double FrameBudget()
        {
            double budget = Settings.Current.SpreadBudgetMs * Stopwatch.Frequency / 1000.0;
            if (_hoursPerFrame <= 1e-6)
            {
                return budget;
            }

            double now = CampaignTime.Now.ToHours;
            double hoursLeft = Math.Ceiling(now) - now;
            double framesLeft = Math.Max(1.0, hoursLeft / _hoursPerFrame - 1.0);
            double remaining = 0;
            foreach (var item in Pending)
            {
                remaining += PredictedCost(item);
            }

            return Math.Max(budget, remaining / framesLeft);
        }

        private static void DrainPrefix()
        {
            TrackFrameHours();

            if (!_discovered)
            {
                DiscoverMods();
            }

            if (Pending.Count == 0)
            {
                return;
            }

            if (!Settings.Current.SpreadTicks)
            {
                Flush();
                return;
            }

            long start = Stopwatch.GetTimestamp();
            double budget = FrameBudget();
            int ran = 0;
            while (Pending.Count > 0)
            {
                // Stop before a listener that would push this frame over budget, so it starts the next frame instead.
                // A listener that alone exceeds the budget still runs, as the first one of its frame.
                if (ran > 0 && Stopwatch.GetTimestamp() - start + PredictedCost(Pending.Peek()) > budget)
                {
                    break;
                }

                RunNext();
                ran++;
            }

            if (FrameProfiler.Enabled)
            {
                FrameProfiler.End(Section.SpreadQueue, start);
            }
        }

        private static void SaveTickPrefix(SaveHandler __instance)
        {
            if (Pending.Count > 0 && __instance.IsSaving)
            {
                Flush();
            }
        }
    }
}
