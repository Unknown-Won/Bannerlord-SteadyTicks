using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

using TaleWorlds.CampaignSystem;

namespace SteadyTicks.Profiling
{
    internal enum Section
    {
        MapState,
        RealTick,
        AiWait,
        Movement,
        CampaignTick,
        TickEvent,
        MapEvents,
        Encounters,
        SiegeEvents,
        PeriodicEvents,
        HourlyEvent,
        DailyEvent,
        WeeklyEvent,
        EntityTicks,
        PartyHourly,
        HourlyAi,
        SpreadQueue,
        QuarterHourAi,
    }

    internal enum Counter
    {
        ListenersSpread,
        ListenersOnTheHour,
        RethinksSkipped,
        RethinksForced,
    }

    /// <summary>
    /// Measures campaign map frame times and the time spent in the main campaign systems.
    /// Results go to SteadyTicks.log (readable summary) and SteadyTicks.profile.csv (one row per report).
    /// </summary>
    internal static class FrameProfiler
    {
        private static readonly string[] SectionNames =
        {
            "map state", "campaign real tick", "AI wait", "movement", "campaign tick", "per-frame tick event",
            "map events", "encounters", "siege events", "periodic events",
            "hourly event", "daily event", "weekly event", "entity ticks", "party hourly", "hourly AI",
            "spread queue", "quarter-hour AI (bg)",
        };

        private static readonly string[] SectionKeys =
        {
            "map_state", "real_tick", "ai_wait", "movement", "campaign_tick", "tick_event",
            "map_events", "encounters", "siege_events", "periodic_events",
            "hourly_event", "daily_event", "weekly_event", "entity_ticks", "party_hourly", "hourly_ai",
            "spread_queue", "quarter_hour_ai_bg",
        };

        private static readonly string[] CounterKeys = { "listeners_spread", "listeners_on_hour", "rethinks_skipped", "rethinks_forced" };

        private const double HitchMs = 1000.0;

        private static readonly int SectionCount = SectionNames.Length;
        private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;

        private static readonly long[] FrameTicks = new long[SectionCount];
        private static readonly long[] WindowTicks = new long[SectionCount];
        private static readonly long[] WindowMaxTicks = new long[SectionCount];
        private static readonly int[] Counters = new int[CounterKeys.Length];
        private static readonly List<double> FrameMs = new(4096);
        private static readonly List<bool> FrameGc = new(4096);
        private static int _gcAtFrameStart;

        private static long _backgroundTicks;
        private static long _lastFrameStamp;
        private static double _windowMs;
        private static int _hitches;
        private static int _paused, _play, _fastForward;
        private static double _windowStartHours = -1;

        public static bool Enabled { get; private set; }

        public static int SessionFrames { get; private set; }
        public static long SessionMapTicks { get; private set; }
        private static bool _patchReportWritten;

        public static long Begin() => Enabled ? Stopwatch.GetTimestamp() : 0;

        public static void End(Section section, long start)
        {
            if (start != 0)
            {
                FrameTicks[(int)section] += Stopwatch.GetTimestamp() - start;
            }
        }

        /// <summary>For work on the campaign's background AI task.</summary>
        public static void EndBackground(long start)
        {
            if (start != 0)
            {
                Interlocked.Add(ref _backgroundTicks, Stopwatch.GetTimestamp() - start);
            }
        }

        public static void Count(Counter counter, int amount = 1)
        {
            if (Enabled)
            {
                Counters[(int)counter] += amount;
            }
        }

        /// <summary>Called at the start of every campaign map frame.</summary>
        public static long OnMapFrame()
        {
            bool enabled = Settings.Current.EnableProfiler;
            if (enabled != Enabled)
            {
                if (!enabled)
                {
                    ListenerProfiler.WriteSessionSummary(MsPerTick);
                    ListenerProfiler.ResetSession();
                }

                ClearState();
                Enabled = enabled;
                if (enabled)
                {
                    Log.Activate();
                    ListenerProfiler.WrapAll();
                    if (!_patchReportWritten)
                    {
                        _patchReportWritten = true;
                        ListenerProfiler.WritePatchReport();
                    }
                }
            }

            if (!Enabled)
            {
                return 0;
            }

            ListenerProfiler.SetMainThread();
            long now = Stopwatch.GetTimestamp();
            if (_lastFrameStamp != 0)
            {
                EndFrame((now - _lastFrameStamp) * MsPerTick);
            }

            _lastFrameStamp = now;
            _gcAtFrameStart = GC.CollectionCount(2);
            return now;
        }

        /// <summary>New or ended campaign: put listeners back and start over (timing is set up again on the next map frame).</summary>
        public static void Reset()
        {
            ListenerProfiler.ResetSession();
            Enabled = false;
            _patchReportWritten = false;
            SessionFrames = 0;
            SessionMapTicks = 0;
            ClearState();
        }

        private static void ClearState()
        {
            Array.Clear(FrameTicks, 0, SectionCount);
            Interlocked.Exchange(ref _backgroundTicks, 0);
            _lastFrameStamp = 0;
            ResetWindow();
        }

        private static void ResetWindow()
        {
            Array.Clear(WindowTicks, 0, SectionCount);
            Array.Clear(WindowMaxTicks, 0, SectionCount);
            Array.Clear(Counters, 0, Counters.Length);
            FrameMs.Clear();
            FrameGc.Clear();
            ListenerProfiler.ResetWindow();
            _windowMs = 0;
            _hitches = 0;
            _paused = _play = _fastForward = 0;
            _windowStartHours = -1;
            for (int g = 0; g < 3; g++)
            {
                GcAtWindowStart[g] = GC.CollectionCount(g);
            }
        }

        private static void EndFrame(double frameMs)
        {
            FrameTicks[(int)Section.QuarterHourAi] += Interlocked.Exchange(ref _backgroundTicks, 0);

            if (frameMs > HitchMs)
            {
                // Loading screens, missions and alt-tab gaps are not campaign map frames.
                _hitches++;
                Array.Clear(FrameTicks, 0, SectionCount);
                return;
            }

            for (int i = 0; i < SectionCount; i++)
            {
                long t = FrameTicks[i];
                WindowTicks[i] += t;
                if (t > WindowMaxTicks[i])
                {
                    WindowMaxTicks[i] = t;
                }

                FrameTicks[i] = 0;
            }

            FrameMs.Add(frameMs);
            SessionFrames++;
            SessionMapTicks += (long)(frameMs / MsPerTick);
            FrameGc.Add(GC.CollectionCount(2) != _gcAtFrameStart);
            _windowMs += frameMs;

            var campaign = Campaign.Current;
            if (campaign is not null)
            {
                switch (campaign.GetSimplifiedTimeControlMode())
                {
                    case CampaignTimeControlMode.StoppablePlay:
                    case CampaignTimeControlMode.UnstoppablePlay:
                        _play++;
                        break;
                    case CampaignTimeControlMode.StoppableFastForward:
                    case CampaignTimeControlMode.UnstoppableFastForward:
                        _fastForward++;
                        break;
                    default:
                        _paused++;
                        break;
                }

                if (_windowStartHours < 0)
                {
                    _windowStartHours = CampaignTime.Now.ToHours;
                }
            }

            if (_windowMs >= Settings.Current.ProfilerWindowSeconds * 1000.0)
            {
                Report();
                ResetWindow();
            }
        }

        private static void Report()
        {
            try
            {
                int frames = FrameMs.Count;
                if (frames == 0)
                {
                    return;
                }

                double seconds = _windowMs / 1000.0;
                double fpsAvg = frames / seconds;
                var sorted = FrameMs.OrderByDescending(x => x).ToList();
                int fullGcs = FrameGc.Count(x => x);
                int worstWithGc = Enumerable.Range(0, frames).OrderByDescending(i => FrameMs[i]).Take(Math.Max(1, frames / 100)).Count(i => FrameGc[i]);
                int worstCount = Math.Max(1, frames / 100);
                double worstAvg = sorted.Take(worstCount).Average();
                double fpsLow = 1000.0 / worstAvg;
                double worstMs = sorted[0];
                double hours = _windowStartHours < 0 ? 0 : CampaignTime.Now.ToHours - _windowStartHours;
                int parties = Campaign.Current?.MobileParties.Count ?? 0;
                var s = Settings.Current;

                string Pct(int n) => (100.0 * n / frames).ToString("0", CultureInfo.InvariantCulture);
                string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

                var sb = new StringBuilder();
                sb.Append($"Profile {F(seconds)}s: {frames} frames, FPS {F(fpsAvg)} avg, {F(fpsLow)} 1% low, worst frame {F(worstMs)} ms");
                if (_hitches > 0)
                {
                    sb.Append($" ({_hitches} gaps over 1 s ignored)");
                }

                sb.Append($"\n  time: paused {Pct(_paused)}%, play {Pct(_play)}%, fast forward {Pct(_fastForward)}%, {F(hours)} in-game hours, {parties} parties, date {CampaignTime.Now}");
                sb.Append($"\n  full garbage collections: {fullGcs} frames had one, {worstWithGc} of the {Math.Max(1, frames / 100)} slowest frames");
                sb.Append($"\n  features: spread ticks {(s.SpreadTicks ? "on" : "off")}, distant AI {(s.ThrottleDistantAi ? "on x" + s.RethinkSlowdown : "off")}");
                AppendWorld(sb);
                long[] world = WorldNow();
                AppendWorldCounts(sb, world);
                sb.Append("\n  ms per frame (avg / worst):");
                for (int i = 0; i < SectionCount; i++)
                {
                    sb.Append($"\n    {SectionNames[i],-22} {F(WindowTicks[i] * MsPerTick / frames),7} / {F(WindowMaxTicks[i] * MsPerTick),7}");
                }

                sb.Append($"\n  hourly/daily listeners: {Counters[(int)Counter.ListenersSpread]} spread, {Counters[(int)Counter.ListenersOnTheHour]} on the hour;" +
                          $" distant AI: {Counters[(int)Counter.RethinksSkipped]} rethinks skipped, {Counters[(int)Counter.RethinksForced]} forced on approach");
                ListenerProfiler.WrapAll();
                ListenerProfiler.AppendReport(sb, frames, MsPerTick);
                Log.Write(sb.ToString());

                WriteCsv(seconds, frames, fpsAvg, fpsLow, worstMs, hours, parties, s, world);
            }
            catch (Exception e)
            {
                Log.Error("Profiler report", e);
            }
        }

        // Counts that grow over a long campaign, plus memory and garbage collections, for spotting what slows a save down.
        private static readonly string[] WorldKeys =
        {
            "heroes_alive", "heroes_dead", "clans", "kingdoms", "armies", "map_events", "log_entries", "issues", "quests",
            "managed_mb", "working_set_mb", "gc0", "gc1", "gc2",
        };

        private static readonly int[] GcAtWindowStart = new int[3];

        private static long[] WorldNow()
        {
            var values = new long[WorldKeys.Length];
            try
            {
                var campaign = Campaign.Current;
                if (campaign is not null)
                {
                    values[0] = Hero.AllAliveHeroes.Count;
                    values[1] = Hero.DeadOrDisabledHeroes.Count;
                    values[2] = Clan.All.Count;
                    values[3] = Kingdom.All.Count;
                    values[4] = Kingdom.All.Sum(k => k.Armies.Count);
                    values[5] = campaign.MapEventManager.MapEvents.Count;
                    values[6] = campaign.LogEntryHistory.GameActionLogs.Count;
                    values[7] = campaign.IssueManager?.Issues.Count ?? 0;
                    values[8] = campaign.QuestManager?.Quests.Count ?? 0;
                }

                values[9] = GC.GetTotalMemory(false) / (1024 * 1024);
                values[10] = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024);
                for (int g = 0; g < 3; g++)
                {
                    values[11 + g] = GC.CollectionCount(g) - GcAtWindowStart[g];
                }
            }
            catch (Exception e)
            {
                Log.Error("Profiler: world counts", e);
            }

            return values;
        }

        private static void AppendWorldCounts(StringBuilder sb, long[] w)
        {
            sb.Append($"\n  world: heroes {w[0]} alive / {w[1]} dead or disabled, clans {w[2]}, kingdoms {w[3]}, armies {w[4]}, map events {w[5]}, " +
                      $"log entries {w[6]}, issues {w[7]}, quests {w[8]}");
            sb.Append($"\n  memory: managed {w[9]} MB, process {w[10]} MB; garbage collections this report: gen0 {w[11]}, gen1 {w[12]}, gen2 {w[13]}");
        }

        /// <summary>What the world holds right now: parties by kind and by party component type, and map events by type.</summary>
        private static void AppendWorld(StringBuilder sb)
        {
            var campaign = Campaign.Current;
            if (campaign is null)
            {
                return;
            }

            var kinds = new int[9];
            var components = new Dictionary<string, int>();
            foreach (var party in campaign.MobileParties)
            {
                if (!party.IsActive)
                {
                    continue;
                }

                kinds[ListenerProfiler.PartyKind(party)]++;
                var c = party.PartyComponent;
                string name = c is null ? "(none)" : c.GetType().Assembly.GetName().Name + ": " + c.GetType().Name;
                components[name] = components.TryGetValue(name, out int n) ? n + 1 : 1;
            }

            sb.Append("\n  active parties: " + string.Join(", ", Enumerable.Range(0, 9).Where(i => kinds[i] > 0).Select(i => $"{ListenerProfiler.KindName(i)} {kinds[i]}")));
            sb.Append("\n  party types: " + string.Join(", ", components.OrderByDescending(kv => kv.Value).Take(10).Select(kv => $"{kv.Key} {kv.Value}")));

            var events = campaign.MapEventManager.MapEvents;
            sb.Append($"\n  map events now: {events.Count}" + (events.Count == 0 ? "" : " (" + string.Join(", ", events.GroupBy(e => e.EventType).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}")) + ")"));
        }

        private static void WriteCsv(double seconds, int frames, double fpsAvg, double fpsLow, double worstMs, double hours, int parties, Settings s, long[] world)
        {
            string path = Path.Combine(Log.Directory, "SteadyTicks.profile.csv");
            var inv = CultureInfo.InvariantCulture;
            var row = new List<string>
            {
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", inv),
                seconds.ToString("0.00", inv), frames.ToString(inv), fpsAvg.ToString("0.00", inv), fpsLow.ToString("0.00", inv),
                worstMs.ToString("0.00", inv), _hitches.ToString(inv),
                (100.0 * _paused / frames).ToString("0", inv), (100.0 * _play / frames).ToString("0", inv), (100.0 * _fastForward / frames).ToString("0", inv),
                hours.ToString("0.00", inv), parties.ToString(inv),
                s.SpreadTicks ? "1" : "0", s.ThrottleDistantAi ? s.RethinkSlowdown.ToString(inv) : "0",
            };

            for (int i = 0; i < SectionCount; i++)
            {
                row.Add((WindowTicks[i] * MsPerTick / frames).ToString("0.000", inv));
                row.Add((WindowMaxTicks[i] * MsPerTick).ToString("0.000", inv));
            }

            row.AddRange(Counters.Select(c => c.ToString(inv)));
            row.AddRange(world.Select(v => v.ToString(inv)));
            row.Add("\"" + CampaignTime.Now + "\"");

            var header = new List<string>
            {
                "time", "window_s", "frames", "fps_avg", "fps_1pct_low", "worst_frame_ms", "gaps", "paused_pct", "play_pct", "ff_pct",
                "ingame_hours", "parties", "spread_ticks", "distant_ai_slowdown",
            };
            foreach (var key in SectionKeys)
            {
                header.Add(key + "_avg_ms");
                header.Add(key + "_max_ms");
            }

            header.AddRange(CounterKeys);
            header.AddRange(WorldKeys);
            header.Add("ingame_date");
            string headerLine = string.Join(",", header);

            // A file from an older version with other columns is kept under a new name.
            if (File.Exists(path) && File.ReadLines(path).FirstOrDefault() != headerLine)
            {
                File.Move(path, Path.Combine(Log.Directory, $"SteadyTicks.profile.{DateTime.Now:yyyyMMdd-HHmmss}.csv"));
            }

            bool newFile = !File.Exists(path);
            using var w = new StreamWriter(path, true, new UTF8Encoding(false)) { NewLine = "\n" };
            if (newFile)
            {
                w.WriteLine(headerLine);
            }

            w.WriteLine(string.Join(",", row));
        }
    }
}
