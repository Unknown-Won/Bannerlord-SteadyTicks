using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SteadyTicks
{
    /// <summary>
    /// SteadyTicks.log in Documents\Mount and Blade II Bannerlord\Configs\ModLogs, created only when it is useful:
    /// when the profiler is on, or when something goes wrong (a patch that no longer fits, an error).
    /// Until then, startup lines are kept in memory, so the file still shows the whole session if it is created later.
    /// </summary>
    internal static class Log
    {
        private const int MaxPending = 500;

        private static readonly object Sync = new();
        private static readonly List<string> Pending = new();
        private static TextWriter? _writer;
        private static bool _failed;

        // STEADYTICKS_LOGDIR lets the offline PatchCheck tool write somewhere else than the game's log folder.
        public static string Directory { get; } = Environment.GetEnvironmentVariable("STEADYTICKS_LOGDIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Mount and Blade II Bannerlord", "Configs", "ModLogs");

        public static void Init() => Write($"Steady Ticks started {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        /// <summary>Routine information: written only once the log file exists.</summary>
        public static void Write(string line)
        {
            lock (Sync)
            {
                if (_writer is not null)
                {
                    WriteLine(line);
                }
                else if (Pending.Count < MaxPending)
                {
                    Pending.Add(line);
                }
            }
        }

        /// <summary>Something the player should be able to see (e.g. a feature turned itself off): creates the log.</summary>
        public static void Warn(string line)
        {
            lock (Sync)
            {
                Open();
                WriteLine(line);
            }
        }

        public static void Error(string context, Exception e) => Warn($"ERROR {context}: {e}");

        /// <summary>Creates the log file (if not yet) and writes what was kept in memory. Called when the profiler turns on.</summary>
        public static void Activate()
        {
            lock (Sync)
            {
                Open();
            }
        }

        private static void Open()
        {
            if (_writer is not null || _failed)
            {
                return;
            }

            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                _writer = new StreamWriter(Path.Combine(Directory, "SteadyTicks.log"), false, new UTF8Encoding(false)) { NewLine = "\n" };
                foreach (string line in Pending)
                {
                    _writer.WriteLine(line);
                }

                Pending.Clear();
                _writer.Flush();
            }
            catch
            {
                _writer = null;
                _failed = true;
            }
        }

        private static void WriteLine(string line)
        {
            if (_writer is null)
            {
                return;
            }

            _writer.WriteLine(line);
            _writer.Flush();
        }
    }
}
