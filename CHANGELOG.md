# Changelog

## 1.0.2

- Spread ticks: when a mod object removes its hourly, daily or weekly listener (for example a quest that just ended), a copy already waiting in the queue no longer runs afterwards. The game skips removed listeners at once, and Steady Ticks now does the same.
- Hero healing: a hero who moves to another clan while outside a party is now healed for exactly the hours since the last heal, instead of up to 4 hours too many or too few.
- Profiler: listeners that their owners removed (ended quests, destroyed parties) are no longer kept in memory while the profiler is on.

## 1.0.1

- Hero healing: a hero who joins or leaves a party between two 4-hour heals now gets exactly the hours spent outside a party, instead of all or nothing.

## 1.0.0

First public release.

- Spread the game's hourly, daily and weekly tick listeners over the following frames, finishing within the same in-game hour and before saves.
- Spread other mods' tick listeners too (on by default), with a per-mod opt-out page in MCM.
- Learned per-listener costs: heavy listeners get their own frame; the budget grows when needed to finish before the next hour.
- Heroes outside a party heal every 4 hours by 4 hours' worth.
- Optional: slower routine rethinks for parties far outside the player's sight.
- Optional profiler: FPS, campaign systems, every event listener by mod, world growth and memory, session summaries.
- No files are written unless the profiler is on or something goes wrong.
