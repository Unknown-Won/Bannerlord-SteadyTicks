using System;

using HarmonyLib;

using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Siege;

namespace SteadyTicks.Profiling
{
    /// <summary>
    /// Timing patches. Prefixes run first and postfixes last, so the times include other mods' patches on the same methods.
    /// Each patch only reads a timestamp, and does nothing while the profiler is off.
    /// </summary>
    internal static class ProfilerPatches
    {
        public static void Apply(Harmony harmony)
        {
            var self = typeof(ProfilerPatches);

            void Timed(string name, Type type, string method, Type[]? args, string patchName)
            {
                Patching.Apply(harmony, "Profiler." + name, AccessTools.Method(type, method, args),
                    prefix: Patching.Own(self, "P_" + patchName), postfix: Patching.Own(self, "F_" + patchName),
                    prefixPriority: Priority.First, postfixPriority: Priority.Last);
            }

            Timed("MapState", typeof(MapState), "OnTick", new[] { typeof(float) }, nameof(Section.MapState));
            Timed("RealTick", typeof(Campaign), "RealTick", new[] { typeof(float) }, nameof(Section.RealTick));
            Timed("AiWait", typeof(Campaign), nameof(Campaign.WaitAsyncTasks), Type.EmptyTypes, nameof(Section.AiWait));
            Timed("Movement", typeof(CampaignTickCacheDataStore), "RealTick", new[] { typeof(float), typeof(float) }, nameof(Section.Movement));
            Timed("CampaignTick", typeof(Campaign), "Tick", Type.EmptyTypes, nameof(Section.CampaignTick));
            Timed("TickEvent", typeof(CampaignEvents), nameof(CampaignEvents.Tick), new[] { typeof(float) }, nameof(Section.TickEvent));
            Timed("MapEvents", typeof(MapEventManager), "Tick", Type.EmptyTypes, nameof(Section.MapEvents));
            Timed("Encounters", typeof(EncounterManager), nameof(EncounterManager.Tick), new[] { typeof(float) }, nameof(Section.Encounters));
            Timed("SiegeEvents", typeof(SiegeEventManager), nameof(SiegeEventManager.Tick), new[] { typeof(float) }, nameof(Section.SiegeEvents));
            Timed("PeriodicEvents", typeof(CampaignPeriodicEventManager), "OnTick", new[] { typeof(float) }, nameof(Section.PeriodicEvents));
            Timed("HourlyEvent", typeof(CampaignEvents), nameof(CampaignEvents.HourlyTick), Type.EmptyTypes, nameof(Section.HourlyEvent));
            Timed("DailyEvent", typeof(CampaignEvents), nameof(CampaignEvents.DailyTick), Type.EmptyTypes, nameof(Section.DailyEvent));
            Timed("WeeklyEvent", typeof(CampaignEvents), nameof(CampaignEvents.WeeklyTick), Type.EmptyTypes, nameof(Section.WeeklyEvent));
            Timed("EntityTicks", typeof(CampaignPeriodicEventManager), "TickPeriodicEvents", Type.EmptyTypes, nameof(Section.EntityTicks));
            Timed("PartyHourly", typeof(CampaignPeriodicEventManager), "MobilePartyHourlyTick", Type.EmptyTypes, nameof(Section.PartyHourly));
            Timed("HourlyAi", typeof(CampaignPeriodicEventManager), "TickPartialHourlyAi", Type.EmptyTypes, nameof(Section.HourlyAi));
            Timed("QuarterHourAi", typeof(Campaign), "LateAITickAux", Type.EmptyTypes, nameof(Section.QuarterHourAi));
        }

        // The map state prefix also marks the frame boundary.
        private static void P_MapState(out long __state) => __state = FrameProfiler.OnMapFrame();
        private static void F_MapState(long __state) => FrameProfiler.End(Section.MapState, __state);

        private static void P_RealTick(out long __state) => __state = FrameProfiler.Begin();
        private static void F_RealTick(long __state) => FrameProfiler.End(Section.RealTick, __state);

        private static void P_AiWait(out long __state) => __state = FrameProfiler.Begin();
        private static void F_AiWait(long __state) => FrameProfiler.End(Section.AiWait, __state);

        private static void P_Movement(out long __state) => __state = FrameProfiler.Begin();
        private static void F_Movement(long __state) => FrameProfiler.End(Section.Movement, __state);

        private static void P_CampaignTick(out long __state) => __state = FrameProfiler.Begin();
        private static void F_CampaignTick(long __state) => FrameProfiler.End(Section.CampaignTick, __state);

        private static void P_TickEvent(out long __state) => __state = FrameProfiler.Begin();
        private static void F_TickEvent(long __state) => FrameProfiler.End(Section.TickEvent, __state);

        private static void P_MapEvents(out long __state) => __state = FrameProfiler.Begin();
        private static void F_MapEvents(long __state) => FrameProfiler.End(Section.MapEvents, __state);

        private static void P_Encounters(out long __state) => __state = FrameProfiler.Begin();
        private static void F_Encounters(long __state) => FrameProfiler.End(Section.Encounters, __state);

        private static void P_SiegeEvents(out long __state) => __state = FrameProfiler.Begin();
        private static void F_SiegeEvents(long __state) => FrameProfiler.End(Section.SiegeEvents, __state);

        private static void P_PeriodicEvents(out long __state) => __state = FrameProfiler.Begin();
        private static void F_PeriodicEvents(long __state) => FrameProfiler.End(Section.PeriodicEvents, __state);

        private static void P_HourlyEvent(out long __state) => __state = FrameProfiler.Begin();
        private static void F_HourlyEvent(long __state) => FrameProfiler.End(Section.HourlyEvent, __state);

        private static void P_DailyEvent(out long __state) => __state = FrameProfiler.Begin();
        private static void F_DailyEvent(long __state) => FrameProfiler.End(Section.DailyEvent, __state);

        private static void P_WeeklyEvent(out long __state) => __state = FrameProfiler.Begin();
        private static void F_WeeklyEvent(long __state) => FrameProfiler.End(Section.WeeklyEvent, __state);

        private static void P_EntityTicks(out long __state) => __state = FrameProfiler.Begin();
        private static void F_EntityTicks(long __state) => FrameProfiler.End(Section.EntityTicks, __state);

        private static void P_PartyHourly(out long __state) => __state = FrameProfiler.Begin();
        private static void F_PartyHourly(long __state) => FrameProfiler.End(Section.PartyHourly, __state);

        private static void P_HourlyAi(out long __state) => __state = FrameProfiler.Begin();
        private static void F_HourlyAi(long __state) => FrameProfiler.End(Section.HourlyAi, __state);

        // Runs on the campaign's background AI task.
        private static void P_QuarterHourAi(out long __state) => __state = FrameProfiler.Begin();
        private static void F_QuarterHourAi(long __state) => FrameProfiler.EndBackground(__state);
    }
}
