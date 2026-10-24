# Changelog

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
