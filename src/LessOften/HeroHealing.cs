using System.Collections.Generic;

using HarmonyLib;

using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace SteadyTicks.LessOften
{
    /// <summary>
    /// Heroes outside a party heal every 4th hourly clan tick by the hours they spent outside a party since the last one,
    /// instead of every hour by 1 hour's worth.
    ///
    /// PartyHealCampaignBehavior.OnClanHourlyTick gives each hero who is not in a party (or held prisoner) 1/24 of the daily
    /// healing every hour, rounded at random and capped at full health. This runs the same body on every 4th call per clan.
    /// Clans are staggered so a quarter of them heal each hour.
    ///
    /// A hero who joins or leaves a party between two batches is paid by time: the hours outside a party are banked when the
    /// hero joins one, and counted up to the batch when the hero is outside then. Only those heroes are tracked, until their
    /// next batch; the others get the full interval if they are outside a party, as before.
    /// </summary>
    internal static class HeroHealing
    {
        private const int Every = 4;
        private const double MaxHours = 24;

        private sealed class Account
        {
            // Start of the current stretch outside a party (meaningful only while the hero is outside one).
            public double Since;

            // Hours outside a party since the last batch, from stretches that already ended.
            public double Hours;
        }

        private static readonly Dictionary<Clan, int> Calls = new();
        private static readonly Dictionary<Hero, Account> Accounts = new();

        public static void Apply(Harmony harmony)
        {
            Patching.Apply(harmony, "LessOften.HeroHealing", AccessTools.Method(typeof(PartyHealCampaignBehavior), "OnClanHourlyTick", new[] { typeof(Clan) }),
                prefix: Patching.Own(typeof(HeroHealing), nameof(Prefix)), prefixPriority: Priority.Low);

            // Every hero added to or removed from a party roster (members and prisoners) goes through these two.
            var change = Patching.Own(typeof(HeroHealing), nameof(BeforeChange));
            var changed = Patching.Own(typeof(HeroHealing), nameof(AfterChange));
            Patching.Apply(harmony, "LessOften.HeroHealing.Joined", AccessTools.Method(typeof(PartyBase), "OnHeroAdded", new[] { typeof(Hero), typeof(TroopRoster) }),
                prefix: change, postfix: changed);
            Patching.Apply(harmony, "LessOften.HeroHealing.Left", AccessTools.Method(typeof(PartyBase), "OnHeroRemoved", new[] { typeof(Hero), typeof(TroopRoster) }),
                prefix: change, postfix: changed);
        }

        public static void Clear()
        {
            Calls.Clear();
            Accounts.Clear();
        }

        private static bool IsOutside(Hero hero) => hero.PartyBelongedTo == null && hero.PartyBelongedToAsPrisoner == null;

        private static int CallsOf(Clan clan) =>
            Calls.TryGetValue(clan, out int calls) ? calls : (clan.StringId?.GetHashCode() ?? 0) & (Every - 1);

        private static void BeforeChange(Hero heroObject, out bool __state) => __state = IsOutside(heroObject);

        private static void AfterChange(Hero heroObject, bool __state)
        {
            bool outside = IsOutside(heroObject);
            if (outside == __state || !Settings.Current.HealHeroesEvery4Hours || Campaign.Current is not { GameStarted: true }
                || heroObject.Clan is not { IsBanditFaction: false } clan)
            {
                return;
            }

            double now = CampaignTime.Now.ToHours;
            if (!Accounts.TryGetValue(heroObject, out var account))
            {
                // Untracked so far: the hero has been in the previous state since the clan's last batch.
                account = new Account { Since = now - CallsOf(clan) % Every };
                Accounts[heroObject] = account;
            }

            if (outside)
            {
                account.Since = now;
            }
            else
            {
                account.Hours += now - account.Since;
            }
        }

        private static bool Prefix(Clan clan, bool __runOriginal)
        {
            if (!__runOriginal || !Settings.Current.HealHeroesEvery4Hours)
            {
                if (Accounts.Count > 0)
                {
                    Accounts.Clear();
                }

                return __runOriginal;
            }

            if (clan.IsBanditFaction)
            {
                return true;
            }

            int calls = CallsOf(clan) + 1;
            Calls[clan] = calls;
            if (calls % Every != 0)
            {
                return false;
            }

            // Same as the game's OnClanHourlyTick, with the hours spent outside a party since the last batch.
            double now = CampaignTime.Now.ToHours;
            float daily = -1f;
            foreach (Hero hero in clan.Heroes)
            {
                bool notInParty = IsOutside(hero);
                double hours = notInParty ? Every : 0;
                if (Accounts.TryGetValue(hero, out var account))
                {
                    hours = account.Hours + (notInParty ? now - account.Since : 0);
                    Accounts.Remove(hero);
                }

                float hp = 0f;
                bool gone = hero.HeroState == Hero.CharacterStates.Dead || hero.HeroState == Hero.CharacterStates.NotSpawned
                            || hero.HeroState == Hero.CharacterStates.Disabled;
                if (hours > 0 && !gone)
                {
                    if (daily < 0f)
                    {
                        daily = Campaign.Current.Models.PartyHealingModel.GetDailyHealingHpForHeroes(null, isPrisoners: false).ResultNumber;
                    }

                    hp = daily * (float)System.Math.Min(hours, MaxHours) / CampaignTime.HoursInDay;
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
