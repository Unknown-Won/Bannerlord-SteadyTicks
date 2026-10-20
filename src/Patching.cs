using System;
using System.Collections.Generic;
using System.Reflection;

using HarmonyLib;

namespace SteadyTicks
{
    /// <summary>Applies patches one at a time so one missing target never blocks the others.</summary>
    internal static class Patching
    {
        public static readonly List<string> Failed = new();

        public static bool Apply(Harmony harmony, string name, MethodBase? target, MethodInfo? prefix = null, MethodInfo? postfix = null,
            int prefixPriority = Priority.Normal, int postfixPriority = Priority.Normal)
        {
            if (target is null)
            {
                Failed.Add(name);
                Log.Warn($"Patch {name}: target not found, skipped");
                return false;
            }

            try
            {
                harmony.Patch(target,
                    prefix: prefix is null ? null : new HarmonyMethod(prefix) { priority = prefixPriority },
                    postfix: postfix is null ? null : new HarmonyMethod(postfix) { priority = postfixPriority });
                Log.Write($"Patch {name}: ok ({target.DeclaringType?.FullName}.{target.Name})");
                return true;
            }
            catch (Exception e)
            {
                Failed.Add(name);
                Log.Error($"Patch {name}", e);
                return false;
            }
        }

        public static MethodInfo Own(Type type, string method) =>
            AccessTools.Method(type, method) ?? throw new MissingMethodException(type.FullName, method);
    }
}
