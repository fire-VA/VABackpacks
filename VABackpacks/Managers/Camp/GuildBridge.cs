// GuildBridge — soft-reference to FiresGuilds' GuildAPI (FiresRPGmaker's
// before 0.0.528). Lets the camp system grant guildmates access to a camp
// chest without VABackpacks taking a hard load-order dependency on
// FiresNPCs.
//
// When FiresNPCs isn't loaded the bridge degrades cleanly: every
// query returns "not in a guild together", which leaves vanilla's
// Private-creator-only access intact.
//
// Same pattern VAInventory uses to talk to VABackpacks (see VAInventory's
// Managers/BackpackBridge.cs).

using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers.Camp
{
    public static class GuildBridge
    {
        public const string FIRES_ASSEMBLY_NAME = "FiresRPGmaker";
        public const string GUILD_API_FQN       = "VerdantsAscent.Modules.Guilds.GuildAPI";

        public const string GUILDS_ASSEMBLY_NAME = "FiresGuilds";
        public const string GUILDS_API_FQN       = "FiresGuilds.Modules.Guilds.GuildAPI";

        private static Type FindType(string assemblyName, string typeName)
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a != null && a.GetName().Name == assemblyName);
            return asm?.GetType(typeName);
        }

        private static MethodInfo _areGuildmates;
        private static bool _initAttempted;

        public static bool IsAvailable { get; private set; }

        /// <summary>
        /// Lazy-init: runs once on first query. Re-runs are cheap (returns
        /// cached IsAvailable). We can't initialize at startup because
        /// FiresNPCs may load in any order relative to VABackpacks.
        /// </summary>
        private static void EnsureInit()
        {
            if (_initAttempted) return;
            _initAttempted = true;

            try
            {
                // Guilds live in FiresGuilds now; FiresRPGmaker (before 0.0.528) is the fallback.
                Type apiType = FindType(GUILDS_ASSEMBLY_NAME, GUILDS_API_FQN) ?? FindType(FIRES_ASSEMBLY_NAME, GUILD_API_FQN);
                if (apiType == null) { IsAvailable = false; return; }

                _areGuildmates = AccessTools.Method(apiType, "AreGuildmates",
                    new[] { typeof(long), typeof(long) });
                IsAvailable = _areGuildmates != null;

                if (IsAvailable)
                    Debug.Log($"[VABackpacks] GuildBridge initialized via {apiType.Assembly.GetName().Name} — guildmates can access each other's camp chests.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VABackpacks] GuildBridge init failed: {ex.Message}");
                IsAvailable = false;
            }
        }

        /// <summary>
        /// True when both players are members of the same guild. Returns
        /// false if FiresNPCs isn't loaded, if either player has no
        /// guild, or if the API call throws.
        /// </summary>
        public static bool AreGuildmates(long playerA, long playerB)
        {
            EnsureInit();
            if (!IsAvailable || _areGuildmates == null) return false;
            if (playerA == 0L || playerB == 0L) return false;
            if (playerA == playerB) return true;       // self trivially passes

            try
            {
                return (bool)_areGuildmates.Invoke(null, new object[] { playerA, playerB });
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VABackpacks] GuildBridge.AreGuildmates threw: {ex.Message}");
                return false;
            }
        }
    }
}
