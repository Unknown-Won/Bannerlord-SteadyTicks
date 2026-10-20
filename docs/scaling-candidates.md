# Run less often, scale to match: candidates

Based on the profiler log of 2026-10-19 (fast forward, both options on, about 2,200 parties, about 50 FPS, so about 20 ms per frame). Times are the average cost per frame. "Gain" is the time saved per frame and the rough FPS change it gives at 50 FPS. These are estimates from the measured cost, not measurements of the change itself.

## Summary

| # | What | Cost now | Recommended scale | Est. gain | Risk | Recommendation |
|---|---|---|---|---|---|---|
| 1 | Party healing (4 times a day) | 0.19 ms | once a day, full day's healing | 0.14 ms (~0.7%) | Very low | Do it |
| 2 | Hero healing outside parties (hourly) | 0.15 ms | every 4 hours, 4 hours' healing | 0.11 ms (~0.6%) | Very low | Do it |
| 3 | Hero skill XP while travelling (hourly) | 0.29 ms | every 2 hours, double XP | 0.14 ms (~0.7%) | Low | Do it |
| 4 | Battles on the map | 2.1 to 3.2 ms | simulate every 2nd round, double casualties per round | unknown, maybe 1 to 1.5 ms (5 to 7%) | Medium | Measure first (new build) |
| 5 | Notables (daily) | 0.25 ms | every 2 days, doubled | 0.12 ms (~0.6%) | Medium | Later, if needed |
| 6 | Settlement threat levels (every 3 hours) | 0.16 ms | every 6 hours | 0.07 ms (~0.4%) | Medium (AI uses it) | Skip |

Items 1 to 3 together: about 0.4 ms, roughly 2% FPS. Item 4 is the only scalable candidate big enough to matter, and the new build measures how much of its cost could be saved. Scaling listeners alone will not get near TimeLord's 50%: the largest costs left are lord AI rethinks (1.7 to 2.4 ms, decisions, not scalable), battles, and the sheer number of parties.

## 1. Party healing

- **Code:** `PartyHealCampaignBehavior.OnQuarterDailyPartyTick` calls `TryHealOrWoundParty(party, 4f)` four times a day per party. The second argument is "heals per day", and the method divides a day's healing by it. Settlements already call it once a day with `1f`.
- **Change:** for every party except yours, run it on one of the four calls with `1f`, and skip the other three.
- **Why safe:** it uses the game's own frequency parameter, the same way settlement garrisons heal.
- **What differs:** a party's wounded heal in one daily step instead of four. A party that fights mid-day may have a few more or fewer wounded troops than in vanilla. Your party is untouched.

## 2. Hero healing outside parties

- **Code:** `PartyHealCampaignBehavior.OnClanHourlyTick` gives each hero who is not in a party 1/24 of the daily heal every hour, rounded at random.
- **Change:** every 4 hours per clan, give 4/24 of the daily heal (still rounded at random, still capped at full health).
- **What differs:** heroes in towns heal in 4-hour steps. Over a day the total is the same.

## 3. Hero skill XP while travelling

- **Code:** `MobilePartyTrainingBehavior.HourlyTickParty` grants scouting XP (and sieging or army-leading XP) to party leaders every hour.
- **Change:** for every party except yours, run it every 2nd hour with all skill XP gained during that call doubled.
- **What differs:** the terrain bonus is taken from where the party is at the 2-hour mark, and a skill level-up can come up to an hour later. Totals match on average. Your party is untouched.

## 4. Battles on the map

- **Code:** each frame, `MapEvent.Update` runs every battle. A battle simulates a round only when its next simulation time has passed (`CombatSimulationModel.GetSimulationTickInterval`), and each round runs a number of hits (`GetSimulationTicksForBattleRound`).
- **Possible change:** double the interval and the hits per round, so battles do the same damage per in-game hour in half the rounds.
- **What differs:** battles move in bigger steps, retreat checks happen half as often, and a battle can end up to one round later or earlier. Outcomes stay the same on average, but individual battles can differ.
- **Why measure first:** if most of the cost is finishing battles (loot, prisoners, other mods' battle-ended handlers) rather than the rounds, this change saves little. The new build splits battle time into rounds, retreat checks, raids and finishing.

## 5. Notables

- **Code:** `NotablesCampaignBehavior.DailyTickHero` updates each notable's support, caravan expenses and a chance to disappear.
- **Change:** every 2 days with doubled amounts and doubled-equivalent chances.
- **Why later:** it needs several separate adjustments, and other mods often patch notables, so the compatibility risk is higher for a small gain.

## 6. Settlement threat levels

- **Code:** `PoliticalStagnationAndBorderIncidentCampaignBehavior.HourlyTickSettlement` already updates each settlement only every 3 hours.
- **Why skip:** the AI uses these values to decide when to defend, so stretching them changes AI behavior for a very small gain.
