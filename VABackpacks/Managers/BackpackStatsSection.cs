using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using FiresCore.UI.Shell;
using UnityEngine;
using VerdantsAscent.Managers.Camp;

namespace VerdantsAscent.Managers
{
    /// <summary>
    /// VABackpacks' card on
    /// Core 0.2.511's Stats tab (FiresShell.RegisterStatsSection, Tools\CORE_STATS_SECTIONS_API.md; order 410, right after RPGClasses).
    /// Data only (the worn bag, its slots and weight reduction, the active camp, the staff in hand); Core draws the card and reads it on
    /// the main thread only while the tab shows. The Core call sits alone in a NoInlining method inside try/catch, so an older Core
    /// (no RegisterStatsSection) just logs and skips. Latin-1 text only.
    /// </summary>
    internal static class BackpackStatsSection
    {
        private const string Id = "vabackpacks.stats", Title = "Backpacks";
        private const int Order = 410;
        private static readonly List<StatRow> Rows = new List<StatRow>(10);   // reused: Core copies the values out each refresh

        internal static void Register()
        {
            try { RegisterWithCore(); VabLog.Line($"[VABackpacks] stats: '{Id}' registered on the hub's Stats tab (order {Order})"); }
            catch (Exception e) { VabLog.Line($"[VABackpacks] stats: no hub Stats tab here (Core older than 0.2.511, or no Core): {e.GetBaseException().Message}"); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RegisterWithCore() => FiresShell.RegisterStatsSection(Id, Title, Order, BuildRows, 1f);

        private static IList<StatRow> BuildRows()
        {
            Rows.Clear();
            Player me = Player.m_localPlayer;
            if (me == null) { Rows.Add(new StatRow("Backpack", "no character", StatState.Muted)); return Rows; }

            ItemDrop.ItemData bag = BackpackApi.GetEquippedBackpack(me);
            if (bag == null) Rows.Add(new StatRow("Backpack", "none worn", StatState.Muted));
            else
            {
                Rows.Add(new StatRow("Backpack", $"{Localization.instance?.Localize(bag.m_shared.m_name) ?? bag.m_shared.m_name} (q{bag.m_quality})"));
                Rows.Add(new StatRow("Slots", $"{BackpackApi.GetEquippedRows(me)} x {BackpackApi.GetEquippedColumns(me)} = {BackpackApi.GetEquippedSlotCount(me)}"));
                if (BackpackApi.TryGetEquippedConfig(me, out BackpackConfig cfg))
                    Rows.Add(new StatRow("Weight reduction", $"{cfg.WeightReduction * 100f:0}%", cfg.WeightReduction > 0f ? StatState.Good : StatState.Normal, "of the bag's contents"));
            }

            BackpackCampMarker camp = ActiveCamp(me);
            if (camp == null) Rows.Add(new StatRow("Camp", "none deployed", StatState.Muted));
            else
            {
                float d = Vector3.Distance(me.transform.position, camp.CampCenter);
                Rows.Add(new StatRow("Camp", $"{d:0} m away", d <= camp.CampRadius ? StatState.Good : StatState.Normal, d <= camp.CampRadius ? "you are in it" : null));
                Rows.Add(new StatRow("Camp ground restore", string.IsNullOrEmpty(camp.PreCamp) ? "legacy" : "exact",
                    string.IsNullOrEmpty(camp.PreCamp) ? StatState.Warn : StatState.Good, string.IsNullOrEmpty(camp.PreCamp) ? "deployed without FAT 0.2.945's pre-camp capture" : "pack-up restores the ground exactly"));
            }

            ItemDrop.ItemData weapon = me.GetCurrentWeapon();
            string prefab = weapon?.m_dropPrefab != null ? weapon.m_dropPrefab.name : "";
            if (prefab.StartsWith("FiresStaff_", StringComparison.Ordinal))
            {
                Rows.Add(new StatRow("Staff", Localization.instance?.Localize(weapon.m_shared.m_name) ?? prefab));
                Rows.Add(new StatRow("Staff attack styles", StaffAttackStyles.On ? "on" : "off", StaffAttackStyles.On ? StatState.Good : StatState.Muted));
            }
            return Rows;
        }

        // the player's active camp: the ZDOID in their custom data (BackpackCampManager.KEY_PLAYER_CAMP), matched against the registry
        private static BackpackCampMarker ActiveCamp(Player me)
        {
            if (me.m_customData == null || !me.m_customData.TryGetValue(BackpackCampManager.KEY_PLAYER_CAMP, out string raw) || string.IsNullOrEmpty(raw)) return null;
            foreach (BackpackCampMarker m in BackpackCampRegistry.All)
            {
                if (m == null) continue;
                var nv = m.GetComponent<ZNetView>();
                if (nv != null && nv.IsValid() && nv.GetZDO() != null && $"{nv.GetZDO().m_uid.UserID}:{nv.GetZDO().m_uid.ID}" == raw) return m;   // the form SetActiveCamp writes
            }
            return null;
        }
    }
}
