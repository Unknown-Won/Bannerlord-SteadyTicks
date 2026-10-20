# Profiling Bannerlord with Visual Studio 2022

Use this to find out why one slow thing is slow (for example the daily spike), after Steady Ticks' own profiler has named the listener.

## Before you start

- Launch the game through MO2 as usual. Visual Studio attaches to the running game, so MO2's virtual file system stays in place.
- Load the save and wait on the campaign map with time paused.
- In MCM, turn on Steady Ticks' **Enable Profiler**. Its log then names the listener behind each spike, which you'll search for in Visual Studio.
- Keep the capture short (a few minutes). A long capture gets large and slow to open.

## Capture

1. In Visual Studio 2022, open **Debug > Performance Profiler** (Alt+F2). No project needs to be open.
2. Under **Target**, choose **Change Target > Running Process...** and pick **Bannerlord.exe**.
3. Tick **CPU Usage** only, then click **Start**.
4. Switch to the game and fast forward for 2 or 3 in-game days, so the capture includes several daily ticks.
5. Go back to Visual Studio and click **Stop collection**. The report opens when processing finishes.
6. Save it with **File > Save** (a `.diagsession` file) if you want to keep it or send it to me.

## Reading the report

1. **Find the spike.** The CPU graph at the top shows a tall peak for each daily tick. Drag across one peak to select it. Every view below then shows only that moment.
2. **Open the details.** Click **Open details** and pick **Call Tree**, or **Flame Graph** for a picture.
3. **Look at the main thread.** In the thread filter, keep only the busiest thread (the game's main thread). The campaign's background AI runs on another thread.
4. **Follow the hot path.** In Call Tree, the flame icon marks the hot path. Expand it until you reach a game or mod method with a large **Total CPU** share.
5. **Search for the listener.** Use the search box with a name from the Steady Ticks log (for example a behavior class name), to see what that listener spends its time on.

## Things that look odd but are normal

- Methods changed by Harmony appear as `DMD<...>` (for example `DMD<TaleWorlds.CampaignSystem.Campaign::Tick>`). That is the patched game method, including every mod's patches on it.
- `[External Code]` is the native engine (rendering, physics) and .NET itself. Toggle **Show External Code** to see inside it, but the campaign logic is in the managed (C#) methods.
- The game ships without symbol files (PDBs), but C# method names still show. Only line numbers are missing.

## Sending me the result

Easiest: in **Call Tree** or **Flame Graph**, with one spike selected, take a screenshot of the expanded hot path. You can also send the `.diagsession` file; I'll try to read it, but screenshots are the reliable way.
