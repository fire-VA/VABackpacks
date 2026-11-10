// Deposit (bypass slot): one "Deposit" button on the bag panel moves every stack in the bag into the nearby chests that already hold that
// item (within 5 / 10 / 20 m by tier), like "stack to nearby chests". Core's StorageNetwork.DepositMatching does the moving: only chests
// this player may open (access, ward, a reachable owner; Locked chests, carts, ships, companion bags and hoppers are skipped), each put
// owner-applied and ledgered so nothing is ever doubled; what no chest takes stays in the bag. Bound by soft reflection (Core 0.2.756+).
// Local player only; guarded by WornTier.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    public sealed class DepositOption : IBagUpgradeOption
    {
        public const string UpgradeId = "Deposit";
        private const string NeedsCore = "Deposit needs Fires Unified Core 0.2.756 or newer";
        private static readonly float[] DefaultRadius = { 5f, 10f, 20f };
        private const float DefaultCooldown = 1f;

        private static readonly ConfigEntry<float>[] Radius = new ConfigEntry<float>[BagUpgrades.TierCount];
        private static ConfigEntry<float> s_cooldown;
        private static readonly BepInEx.Logging.ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("VABackpacks." + UpgradeId);
        private static float s_nextAllowed;
        private static bool s_running;
        private static MethodInfo s_depositMatching;
        private static bool s_looked;

        private static bool s_hooked;

        public DepositOption()
        {
            if (s_hooked) return;
            s_hooked = true;
            new Terminal.ConsoleCommand("bagup_deposit", "- deposits your worn bag into nearby chests that already hold its items (the Deposit upgrade's button)", args => DoDeposit(Player.m_localPlayer));
        }

        public string Id => UpgradeId;
        public string Name => "Deposit";
        public string Slot => BackpackSpecialSlots.SlotBypass;
        public string Donor => "Chain";
        public string[] Recipes => new[]
        {
            "forge,1,Bronze:5,FineWood:10,LeatherScraps:5",
            "forge,3,Iron:10,FineWood:15,Chain:2",
            "forge,5,BlackMetal:10,FineWood:20,LinenThread:5",
        };

        public string Describe(int tier) => $"A Deposit button on the bag moves its stacks into chests within {RadiusFor(tier):0.#} m that already hold them.";

        public void Bind(ConfigFile cfg, ConfigSync sync)
        {
            BackpackApi.RegisterBagHeaderButton("deposit", 20, bag => BagUpgrades.SlottedTier(bag, UpgradeId) > 0, bag => "Deposit",
                bag => "Move this bag's stacks into nearby chests that already hold them", bag => DoDeposit(Player.m_localPlayer));
            for (int t = 0; t < BagUpgrades.TierCount; t++)
            {
                Radius[t] = cfg.Bind(BagUpgrades.Section, $"Deposit {BagUpgrades.TierNumerals[t]} radius", DefaultRadius[t],
                    new ConfigDescription($"Chests within this many metres take the bag's stacks with Deposit {BagUpgrades.TierNumerals[t]}. Server-synced.",
                        new AcceptableValueRange<float>(1f, 50f)));
                sync?.AddConfigEntry(Radius[t]);
            }
            s_cooldown = cfg.Bind(BagUpgrades.Section, "Deposit cooldown", DefaultCooldown,
                new ConfigDescription("Seconds between two deposits (the button can't be spammed). Server-synced.", new AcceptableValueRange<float>(0f, 30f)));
            sync?.AddConfigEntry(s_cooldown);
        }

        internal static float RadiusFor(int tier) => tier < 1 ? 0f : Radius[tier - 1]?.Value ?? DefaultRadius[tier - 1];

        /// <summary>True when the worn bag has Deposit (the bag panel shows the button only then).</summary>
        public static bool IsAvailable(Player player) => player != null && BagUpgrades.WornTier(player, UpgradeId) > 0;

        private static MethodInfo DepositMatching()
        {
            if (s_looked) return s_depositMatching;
            s_looked = true;
            var t = Type.GetType("FiresCore.Storage.StorageNetwork, FiresUnifiedCore", false);
            s_depositMatching = t?.GetMethod("DepositMatching", BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(Player), typeof(Inventory), typeof(Vector3), typeof(float), typeof(IList<ItemDrop.ItemData>), typeof(Action<int, int, string>) }, null);
            if (s_depositMatching == null) Log.LogWarning($"[{UpgradeId}] {NeedsCore}: StorageNetwork.DepositMatching not found");
            return s_depositMatching;
        }

        /// <summary>The Deposit button: the worn bag's stacks into nearby chests that already hold them. Local player only.</summary>
        public static void DoDeposit(Player player)
        {
            if (player == null || player != Player.m_localPlayer) return;
            int tier = BagUpgrades.WornTier(player, UpgradeId);
            if (tier < 1) { player.Message(MessageHud.MessageType.Center, "No worn bag has a Deposit upgrade"); return; }
            if (s_running || Time.time < s_nextAllowed) { Log.LogDebug($"[{UpgradeId}] pressed again too soon; ignored"); return; }
            MethodInfo deposit = DepositMatching();
            if (deposit == null) { player.Message(MessageHud.MessageType.Center, NeedsCore); return; }
            ItemDrop.ItemData bag = BagUpgrades.WornBagWith(player, UpgradeId);
            Inventory source = BagUpgrades.BagInventory(player, bag);
            List<ItemDrop.ItemData> items = BagUpgrades.ItemsInBag(player, bag)
                .Where(i => i != null && !i.m_equipped && BagUpgrades.SlotOf(i) == null && !BackpackApi.IsBackpack(i))
                .ToList();
            items = BagUpgrades.SplitForSend(player, bag, items);   // big stacks leave the bag only as vanilla-size pieces
            float radius = RadiusFor(tier);
            if (source == null || items.Count == 0) { player.Message(MessageHud.MessageType.Center, "The bag is empty"); Log.LogDebug($"[{UpgradeId}] nothing to deposit (bag inventory {(source != null ? "found" : "missing")}, {items.Count} stack(s))"); return; }
            s_running = true;
            s_nextAllowed = Time.time + (s_cooldown?.Value ?? DefaultCooldown);
            Log.LogDebug($"[{UpgradeId}] depositing {items.Count} stack(s) within {radius:0.#} m (tier {tier})");
            try
            {
                deposit.Invoke(null, new object[] { player, source, player.transform.position, radius, (IList<ItemDrop.ItemData>)items,
                    new Action<int, int, string>((placed, left, why) => Done(player, placed, left, why, radius)) });
            }
            catch (Exception e) { s_running = false; Log.LogWarning($"[{UpgradeId}] deposit failed: {(e.InnerException ?? e).Message}"); }
        }

        private static void Done(Player player, int placed, int left, string why, float radius)
        {
            s_running = false;
            Log.LogInfo($"[{UpgradeId}] done: {placed} stack(s) placed, {left} left within {radius:0.#} m{(why != null ? $" ({why})" : "")}");
            string msg = placed == 0 ? $"Nothing deposited{(why != null ? $": {why}" : "")}"
                : left == 0 ? $"Deposited {placed} stack(s)" : $"Deposited {placed} stack(s), {left} stayed in the bag";
            player?.Message(MessageHud.MessageType.Center, Localization.instance != null ? Localization.instance.Localize(msg) : msg);
        }
    }
}
