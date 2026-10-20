using System.Collections.Generic;

using HarmonyLib;

using SteadyTicks.Profiling;

using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors.AiBehaviors;
using TaleWorlds.CampaignSystem.Party;

namespace SteadyTicks.Ai
{
    /// <summary>
    /// Distant parties rethink their plans less often.
    ///
    /// The game calls AiPartyThinkBehavior.PartyHourlyAiTick once per in-game hour per party. It rethinks when Ai.HourCounter
    /// (hours since the last rethink) reaches a multiple of 6, or every hour for army leaders, parties in transition, parties
    /// flagged with RethinkAtNextHourlyTick and raid/siege-assault leaders. The method does nothing else except HourCounter++.
    ///
    /// For a distant party that would make a routine 6-hour rethink, this skips the call and only advances HourCounter, until
    /// 6 x slowdown hours have passed. A party is distant when it could not reach the player's sight range within the margin
    /// hours at the two parties' current speeds. A party that skipped a rethink and comes within that range rethinks at once.
    /// Forced rethinks, armies, battles, sieges, the player's clan (by default) and quest/custom parties are never skipped,
    /// and the quarter-hour AI checks (chasing, fleeing, short-term targets) are not touched.
    /// </summary>
    internal static class DistantAiThrottle
    {
        private const int GameThinkPeriod = 6;

        private static readonly HashSet<MobileParty> Skipped = new();
        private static AccessTools.FieldRef<MobileParty, float>? _lastSpeed;

        private static int _sightHour = -1;
        private static float _sightRange;
        private static int _cleanupDay = -1;

        public static void Apply(Harmony harmony)
        {
            try
            {
                _lastSpeed = AccessTools.FieldRefAccess<MobileParty, float>("_lastCalculatedSpeed");
            }
            catch
            {
                _lastSpeed = null;
                Log.Warn("Distant AI: party speed field not found, using 5 map units per hour");
            }

            Patching.Apply(harmony, "DistantAi.Rethink", AccessTools.Method(typeof(AiPartyThinkBehavior), "PartyHourlyAiTick", new[] { typeof(MobileParty) }),
                prefix: Patching.Own(typeof(DistantAiThrottle), nameof(Prefix)), prefixPriority: Priority.Low);
        }

        public static void Clear()
        {
            Skipped.Clear();
            _sightHour = -1;
            _cleanupDay = -1;
        }

        private static bool Prefix(MobileParty mobileParty, bool __runOriginal)
        {
            if (!__runOriginal)
            {
                return false;
            }

            var settings = Settings.Current;
            if (!settings.ThrottleDistantAi)
            {
                if (Skipped.Count > 0)
                {
                    Skipped.Clear();
                }

                return true;
            }

            var main = MobileParty.MainParty;
            var ai = mobileParty.Ai;
            if (main is null || !main.IsActive || mobileParty == main || ai.IsDisabled || ai.DoNotMakeNewDecisions || IsExempt(mobileParty, settings))
            {
                Skipped.Remove(mobileParty);
                return true;
            }

            int hours = ai.HourCounter;

            // A party that skipped a rethink rethinks as soon as it comes close.
            if (Skipped.Contains(mobileParty))
            {
                if (!IsDistant(mobileParty, main, settings))
                {
                    Skipped.Remove(mobileParty);
                    ai.RethinkAtNextHourlyTick = true;
                    FrameProfiler.Count(Counter.RethinksForced);
                    return true;
                }
            }

            // Only routine 6-hour rethinks are ever skipped.
            if (hours % GameThinkPeriod != 0 || hours >= GameThinkPeriod * settings.RethinkSlowdown || !IsDistant(mobileParty, main, settings))
            {
                if (hours % GameThinkPeriod == 0)
                {
                    Skipped.Remove(mobileParty);
                }

                return true;
            }

            ai.HourCounter = hours + 1;
            Skipped.Add(mobileParty);
            FrameProfiler.Count(Counter.RethinksSkipped);
            CleanupDaily();
            return false;
        }

        private static bool IsExempt(MobileParty party, Settings settings)
        {
            if (!(party.IsLordParty || party.IsBandit || party.IsCaravan || party.IsVillager || party.IsPatrolParty))
            {
                return true;
            }

            if (party.Army is not null || party.MapEvent is not null || party.SiegeEvent is not null || party.IsTransitionInProgress
                || party.Ai.RethinkAtNextHourlyTick)
            {
                return true;
            }

            return !settings.ThrottlePlayerClan && party.ActualClan is { } clan && clan == Clan.PlayerClan;
        }

        private static bool IsDistant(MobileParty party, MobileParty main, Settings settings)
        {
            float closingSpeed = Speed(party) + Speed(main);
            float range = SightRange(main) + closingSpeed * settings.MarginHours;
            return party.GetPosition2D.DistanceSquared(main.GetPosition2D) > range * range;
        }

        private static float Speed(MobileParty party)
        {
            float speed = _lastSpeed is null ? 5f : _lastSpeed(party);
            return speed > 0f ? speed : 0f;
        }

        /// <summary>The player's sight range, refreshed once per in-game hour.</summary>
        private static float SightRange(MobileParty main)
        {
            int hour = (int)CampaignTime.Now.ToHours;
            if (hour != _sightHour)
            {
                _sightHour = hour;
                _sightRange = main.SeeingRange;
            }

            return _sightRange;
        }

        private static void CleanupDaily()
        {
            int day = (int)CampaignTime.Now.ToDays;
            if (day != _cleanupDay)
            {
                _cleanupDay = day;
                Skipped.RemoveWhere(p => !p.IsActive);
            }
        }
    }
}
