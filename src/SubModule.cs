using System;

using HarmonyLib;

using SteadyTicks.Ai;
using SteadyTicks.LessOften;
using SteadyTicks.Profiling;
using SteadyTicks.Spreading;

using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace SteadyTicks
{
    public sealed class SubModule : MBSubModuleBase
    {
        internal const string HarmonyId = "steadyticks.bannerlord";

        private Harmony? _harmony;

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            Log.Init();

            try
            {
                _harmony = new Harmony(HarmonyId);
                ApplyPatches(_harmony);
                if (Patching.Failed.Count == 0)
                {
                    Log.Write("All patches applied");
                }
                else
                {
                    Log.Warn($"Patches not applied: {string.Join(", ", Patching.Failed)}");
                }
            }
            catch (Exception e)
            {
                Log.Error("Setup", e);
            }
        }

        /// <summary>Every patch of the mod. Also called by tools\PatchCheck to verify the targets offline.</summary>
        internal static void ApplyPatches(Harmony harmony)
        {
            ProfilerPatches.Apply(harmony);
            TickSpreader.Apply(harmony);
            DistantAiThrottle.Apply(harmony);
            HeroHealing.Apply(harmony);
            ListenerProfiler.Apply(harmony);
        }

        protected override void OnBeforeInitialModuleScreenSetAsRoot()
        {
            base.OnBeforeInitialModuleScreenSetAsRoot();
            Settings.Load();
        }

        protected override void OnGameStart(Game game, IGameStarter gameStarter)
        {
            base.OnGameStart(game, gameStarter);
            if (game.GameType is Campaign)
            {
                Settings.Load();
                ResetSession();
            }
        }

        public override void OnGameEnd(Game game)
        {
            base.OnGameEnd(game);
            ResetSession();
        }

        private static void ResetSession()
        {
            FrameProfiler.Reset();
            TickSpreader.Clear();
            DistantAiThrottle.Clear();
            HeroHealing.Clear();
        }
    }
}
