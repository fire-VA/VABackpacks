// Shared by RemoteAccess, Hopper and Refill: FiresStorageNetwork.StorageRemote, bound by soft reflection so VABackpacks never needs the
// storage mod. Every call is a no-op (and says so once in the log) when it isn't installed.

using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;

namespace VerdantsAscent.Managers
{
    internal static class RemoteAccessBridge
    {
        private const string TypeName = "FiresStorageNetwork.StorageRemote";
        private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("VABackpacks.RemoteAccess");
        private static bool s_looked, s_missingLogged;
        private static MethodInfo s_isAvailable, s_distance, s_open, s_deposit, s_withdraw;

        private static bool Bind()
        {
            if (!s_looked)
            {
                s_looked = true;
                Type t = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) { t = asm.GetType(TypeName, false); if (t != null) break; }
                if (t != null)
                {
                    s_isAvailable = t.GetMethod("IsAvailable", new[] { typeof(Player) });
                    s_distance = t.GetMethod("DistanceToHome", new[] { typeof(Player) });
                    s_open = t.GetMethod("OpenTerminal", new[] { typeof(Player), typeof(float) });
                    s_deposit = t.GetMethod("Deposit", new[] { typeof(Player), typeof(List<ItemDrop.ItemData>), typeof(float), typeof(Action<int>) });
                    s_withdraw = t.GetMethod("Withdraw", new[] { typeof(Player), typeof(string), typeof(int), typeof(float), typeof(Action<int>) });
                }
            }
            bool ok = s_isAvailable != null && s_distance != null && s_deposit != null && s_withdraw != null;
            if (!ok && !s_missingLogged) { s_missingLogged = true; Log.LogDebug("[RemoteAccess] FiresStorageNetwork's StorageRemote is not installed (0.0.27+): remote storage is off"); }
            return ok;
        }

        internal static bool Installed => Bind();

        internal static bool HasHome(Player p) => Bind() && (bool)s_isAvailable.Invoke(null, new object[] { p });

        /// <summary>Horizontal metres to the home terminal, -1 when there is none (or the storage mod is missing).</summary>
        internal static float DistanceToHome(Player p) => Bind() ? (float)s_distance.Invoke(null, new object[] { p }) : -1f;

        internal static void OpenTerminal(Player p, float range) { if (Bind() && s_open != null) s_open.Invoke(null, new object[] { p, range }); }

        internal static void Deposit(Player p, List<ItemDrop.ItemData> items, float range, Action<int> committed)
        {
            if (!Bind()) { committed?.Invoke(0); return; }
            s_deposit.Invoke(null, new object[] { p, items, range, committed });
        }

        internal static void Withdraw(Player p, string prefab, int amount, float range, Action<int> received)
        {
            if (!Bind()) { received?.Invoke(0); return; }
            s_withdraw.Invoke(null, new object[] { p, prefab, amount, range, received });
        }

        /// <summary>Why the home network can't be reached right now with this reach, or null when it can.</summary>
        internal static string Refusal(Player p, float range)
        {
            if (!Bind()) return "the storage mod isn't installed";
            if (!HasHome(p)) return "no home terminal (open a Storage Terminal with the Remote Rune once)";
            float d = DistanceToHome(p);
            return d > range ? $"out of range ({d:0} m, this tier reaches {range:0} m)" : null;
        }
    }
}
