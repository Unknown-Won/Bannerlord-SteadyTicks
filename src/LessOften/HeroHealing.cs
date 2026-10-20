using System.Collections.Generic;

using HarmonyLib;

using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace SteadyTicks.LessOften
{
    /// <summary>
    /// Heroes outside a party heal every 4th hourly clan tick by 4 hours' worth, instead of every hour by 1 hour's worth.
    ///
    /// PartyHealCampaignBehavior.OnClanHourlyTick gives each hero who is not in a party (or held prisoner) 1/24 of the daily
    /// healing every hour, rounded at random and capped at full health. This runs the same body on every 4th call per clan
    /// with 4/24, so healing per day is unchanged. Clans are staggered so a quarter of them heal each hour.
    /// </summary>
    internal static class HeroHealing
    {
        private const int Every = 4;

        private static readonly Dictionary<Clan, int> Calls = new();

        public static void Apply(Harmony harmony)
        {
            Patching.Apply(harmony, "LessOften.HeroHealing", AccessTools.Method(typeof(PartyHealCampaignBehavior), "OnClanHourlyTick", new[] { typeof(Clan) }),
                prefix: Patching.Own(typeof(HeroHealing), nameof(Prefix)), prefixPriority: Priority.Low);
        }

        public static void Clear() => Calls.Clear();

        private static bool Prefix(Clan clan, bool __runOriginal)
        {
            if (!__runOriginal || !Settings.Current.HealHeroesEvery4Hours || clan.IsBanditFaction)
            {
                return __runOriginal;
            }

            if (!Calls.TryGetValue(clan, out int calls))
            {
                calls = (clan.StringId?.GetHashCode() ?? 0) & (Every - 1);
            }

            calls++;
            Calls[clan] = calls;
            if (calls % Every != 0)
            {
                return false;
            }

            // Same as the game's OnClanHourlyTick, with Every hours of healing.
            foreach (Hero hero in clan.Heroes)
            {
                float hp = 0f;
                bool notInParty = hero.PartyBelongedTo == null && hero.PartyBelongedToAsPrisoner == null;
                bool gone = hero.HeroState == Hero.CharacterStates.Dead || hero.HeroState == Hero.CharacterStates.NotSpawned
                            || hero.HeroState == Hero.CharacterStates.Disabled;
                if (notInParty && !gone)
                {
                    hp = Campaign.Current.Models.PartyHealingModel.GetDailyHealingHpForHeroes(null, isPrisoners: false).ResultNumber
                         * Every / CampaignTime.HoursInDay;
                }

                int heal = MBRandom.RoundRandomized(hp);
                if (!hero.IsHealthFull())
                {
                    hero.HitPoints += MathF.Min(heal, hero.MaxHitPoints - hero.HitPoints);
                }
            }

            return false;
        }
    }
}
