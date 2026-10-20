using MCM.Abstractions.Attributes;
using MCM.Abstractions.Attributes.v2;
using MCM.Abstractions.Base.Global;

namespace SteadyTicks
{
    public sealed class Settings : AttributeGlobalSettings<Settings>
    {
        public override string Id => "SteadyTicks_v1";
        public override string DisplayName => "Steady Ticks";
        public override string FolderName => "SteadyTicks";
        public override string FormatType => "json";

        private const string Spread = "Spread Ticks";
        private const string Distant = "Distant AI";
        private const string LessOften = "Less Frequent Work";
        private const string Profiler = "Profiler";

        [SettingPropertyBool("Spread Hourly and Daily Ticks", Order = 0, RequireRestart = false,
            HintText = "Runs the game's own hourly, daily and weekly tick work over the next frames instead of all in one frame. Other mods still run on the hour. [ Default: ON ]")]
        [SettingPropertyGroup(Spread, GroupOrder = 0)]
        public bool SpreadTicks { get; set; } = true;

        [SettingPropertyFloatingInteger("Time Per Frame (ms)", 0.5f, 8f, "0.0", Order = 1, RequireRestart = false,
            HintText = "How much spread-out tick work may run in one frame. More is used when needed to finish before the next in-game hour. A listener that alone takes longer gets a frame to itself. [ Default: 2.0 ]")]
        [SettingPropertyGroup(Spread, GroupOrder = 0)]
        public float SpreadBudgetMs { get; set; } = 2f;

        [SettingPropertyBool("Spread Other Mods Too", Order = 2, RequireRestart = false,
            HintText = "Also spread other mods' hourly, daily and weekly work. It still runs once per tick, within the same in-game hour. To keep a mod exactly on the hour, untick it on the 'Steady Ticks: Mods' page (shown once a campaign is loaded). [ Default: ON ]")]
        [SettingPropertyGroup(Spread, GroupOrder = 0)]
        public bool SpreadAllMods { get; set; } = true;

        [SettingPropertyBool("Heal Heroes Every 4 Hours", Order = 0, RequireRestart = false,
            HintText = "Heroes outside a party heal every 4 hours by 4 hours' worth instead of every hour. Same healing per day. [ Default: ON ]")]
        [SettingPropertyGroup(LessOften, GroupOrder = 2)]
        public bool HealHeroesEvery4Hours { get; set; } = true;

        [SettingPropertyBool("Slow Down Distant AI", Order = 0, RequireRestart = false,
            HintText = "Parties well outside your sight rethink their plans less often. Nearby parties, armies, battles and sieges are never slowed. [ Default: OFF ]")]
        [SettingPropertyGroup(Distant, GroupOrder = 1)]
        public bool ThrottleDistantAi { get; set; } = false;

        [SettingPropertyInteger("Rethink Slowdown", 2, 4, "x0", Order = 1, RequireRestart = false,
            HintText = "How many times less often distant parties rethink. The game rethinks every 6 hours, so x2 means every 12 hours. [ Default: x2 ]")]
        [SettingPropertyGroup(Distant, GroupOrder = 1)]
        public int RethinkSlowdown { get; set; } = 2;

        [SettingPropertyFloatingInteger("Safety Margin (hours)", 0f, 24f, "0", Order = 2, RequireRestart = false,
            HintText = "A party counts as distant only if it could not reach your sight range within this many in-game hours, using both parties' current speeds. [ Default: 12 ]")]
        [SettingPropertyGroup(Distant, GroupOrder = 1)]
        public float MarginHours { get; set; } = 12f;

        [SettingPropertyBool("Include Your Clan", Order = 3, RequireRestart = false,
            HintText = "Also slow down your own clan's distant parties. [ Default: OFF ]")]
        [SettingPropertyGroup(Distant, GroupOrder = 1)]
        public bool ThrottlePlayerClan { get; set; } = false;

        [SettingPropertyBool("Enable Profiler", Order = 0, RequireRestart = false,
            HintText = "Writes FPS and campaign map timings to SteadyTicks.log and SteadyTicks.profile.csv in the game's ModLogs folder. [ Default: OFF ]")]
        [SettingPropertyGroup(Profiler, GroupOrder = 3)]
        public bool EnableProfiler { get; set; } = false;

        [SettingPropertyInteger("Report Every (seconds)", 5, 60, "0", Order = 1, RequireRestart = false,
            HintText = "Real seconds of map time per profiler report. [ Default: 10 ]")]
        [SettingPropertyGroup(Profiler, GroupOrder = 3)]
        public int ProfilerWindowSeconds { get; set; } = 10;

        /// <summary>Cached instance. MCM keeps the same object for the whole session.</summary>
        internal static Settings Current { get; private set; } = new();

        internal static void Load()
        {
            try
            {
                Current = Instance ?? Current;
            }
            catch (System.Exception e)
            {
                Log.Error("Settings", e);
            }
        }
    }
}
