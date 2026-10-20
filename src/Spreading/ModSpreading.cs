using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

using MCM.Abstractions.Base.Global;
using MCM.Abstractions.FluentBuilder;
using MCM.Common;

namespace SteadyTicks.Spreading
{
    /// <summary>
    /// Other mods' hourly, daily and weekly listeners are spread like the game's own (setting "Spread Other Mods Too"),
    /// except mods the player keeps on the hour. The list of mods comes from the listeners found when a campaign starts, and appears in MCM
    /// as a second settings page ("Steady Ticks: Mods"), saved by MCM like any other setting.
    /// </summary>
    internal static class ModSpreading
    {
        private sealed class ModEntry
        {
            public string Key = "";
            public string Display = "";
            public int Hourly, Daily, Weekly;
            public bool Spread = true;
        }

        private const string SettingsId = "SteadyTicks_Mods_v3";

        private static readonly Dictionary<string, ModEntry> Mods = new();
        private static readonly Dictionary<Assembly, ModEntry?> ByAssembly = new();
        private static FluentGlobalSettings? _settings;
        private static string _builtFor = "";

        /// <summary>Whether this mod listener is spread: other mods are spread unless the player keeps that mod on the hour.</summary>
        public static bool IsSpread(object? owner)
        {
            if (owner is null || !Settings.Current.SpreadAllMods)
            {
                return false;
            }

            var assembly = owner.GetType().Assembly;
            if (!ByAssembly.TryGetValue(assembly, out var entry))
            {
                Mods.TryGetValue(assembly.GetName().Name ?? "", out entry);
                ByAssembly[assembly] = entry;
            }

            return entry is not { Spread: false };
        }

        /// <summary>Called when a campaign starts: lists the mods with listeners and (re)builds the MCM page if the list changed.</summary>
        public static void Discover(IEnumerable<(int Kind, object? Owner)> listeners)
        {
            var found = new Dictionary<string, ModEntry>();
            foreach (var (kind, owner) in listeners)
            {
                if (owner is null)
                {
                    continue;
                }

                var assembly = owner.GetType().Assembly;
                string key = assembly.GetName().Name ?? "";
                if (!found.TryGetValue(key, out var entry))
                {
                    entry = new ModEntry { Key = key, Display = DisplayName(assembly, key) };
                    found[key] = entry;
                }

                if (kind == 0) entry.Hourly++;
                else if (kind == 1) entry.Daily++;
                else entry.Weekly++;
            }

            // Keep choices already made this session.
            foreach (var entry in found.Values)
            {
                if (Mods.TryGetValue(entry.Key, out var old))
                {
                    entry.Spread = old.Spread;
                }
            }

            Mods.Clear();
            foreach (var kv in found)
            {
                Mods[kv.Key] = kv.Value;
            }

            ByAssembly.Clear();

            string signature = string.Join("|", Mods.Keys.OrderBy(k => k));
            if (signature != _builtFor)
            {
                BuildMenu();
                _builtFor = signature;
            }

            Log.Write($"Mods with hourly/daily/weekly listeners: {Mods.Count}; kept on the hour: " +
                      (Mods.Values.Any(m => !m.Spread) ? string.Join(", ", Mods.Values.Where(m => !m.Spread).Select(m => m.Display)) : "none"));
        }

        /// <summary>Module folder name from the assembly path (…\Modules\&lt;Id&gt;\bin\…), else the assembly name.</summary>
        private static string DisplayName(Assembly assembly, string fallback)
        {
            try
            {
                string[] parts = assembly.Location.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                int i = Array.FindLastIndex(parts, p => p.Equals("Modules", StringComparison.OrdinalIgnoreCase));
                if (i >= 0 && i + 1 < parts.Length)
                {
                    return parts[i + 1] == fallback ? fallback : $"{parts[i + 1]} ({fallback})";
                }
            }
            catch
            {
                // Dynamic or in-memory assemblies have no location.
            }

            return fallback;
        }

        private static void BuildMenu()
        {
            try
            {
                _settings?.Unregister();
                var builder = BaseSettingsBuilder.Create(SettingsId, "Steady Ticks: Mods");
                if (builder is null)
                {
                    return;
                }

                builder.SetFolderName("SteadyTicks").SetFormat("json2").CreateGroup("Spread These Mods", group =>
                {
                    int order = 0;
                    foreach (var entry in Mods.Values.OrderBy(m => m.Display, StringComparer.OrdinalIgnoreCase))
                    {
                        var e = entry;
                        group.AddBool(e.Key, e.Display, new ProxyRef<bool>(() => e.Spread, v =>
                        {
                            e.Spread = v;
                            ByAssembly.Clear();
                        }), b => b.SetOrder(order++).SetRequireRestart(false).SetHintText(
                            $"Spread this mod's {Describe(e)} listeners over the next frames. Untick to run them exactly on the hour, as the game does. [ Default: ON ]"));
                    }
                });

                _settings = builder.BuildAsGlobal();
                _settings.Register();
            }
            catch (Exception e)
            {
                Log.Error("Mod spreading menu", e);
            }
        }

        private static string Describe(ModEntry e) => string.Join(", ", new[] { (e.Hourly, "hourly"), (e.Daily, "daily"), (e.Weekly, "weekly") }.Where(x => x.Item1 > 0).Select(x => $"{x.Item1} {x.Item2}"));
    }
}
