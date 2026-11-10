// Bag Hopper (bypass slot): vacuums loose loot around the wearer into the bag, then sends it home through the storage network after a
// short delay, so there's a moment to open the bag and look at the haul. Sending home needs Remote Access in the same bag and the home
// terminal within this tier's send range; out of range the loot simply waits in the bag. "Hopper send delay" 0 = the Send home button only.
// Each vacuumed item carries its pickup time (game time) in its custom data, so the wait survives a relog; the mark goes when it leaves.

using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    public sealed class HopperOption : IBagUpgradeOption
    {
        public const string UpgradeId = "Hopper";
        private const string PickedAtKey = "BagUp.Hopper.t";
        private const float VacuumInterval = 0.5f, SendCheckInterval = 1f;
        private static readonly float[] DefaultRadius = { 4f, 6f, 8f };
        private static readonly float[] DefaultSendRange = { 1000f, 5000f, 7500f };
        private static readonly ConfigEntry<float>[] Radius = new ConfigEntry<float>[BagUpgrades.TierCount];
        private static readonly ConfigEntry<float>[] SendRange = new ConfigEntry<float>[BagUpgrades.TierCount];
        private static ConfigEntry<float> s_sendDelay;
        private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("VABackpacks.Hopper");
        private static readonly Collider[] s_hits = new Collider[64];
        private static int s_itemMask = -1;
        private static float s_nextVacuum, s_nextSend;
        private static bool s_sending;

        public string Id => UpgradeId;
        public string Name => "Bag Hopper";
        public string Slot => BackpackSpecialSlots.SlotBypass;
        public string Donor => "BronzeNails";
        public string[] Recipes => new[]
        {
            "forge,2,Bronze:5,Resin:10,BronzeNails:10",
            "forge,4,Iron:8,Resin:10,Chain:2",
            "blackforge,1,BlackMetal:6,Resin:15,YggdrasilWood:5",
        };

        public string Describe(int tier) => $"Pulls loose loot within {RadiusFor(tier):0} m into this bag and sends it home (up to {SendRangeFor(tier):0} m away, with Remote Access) after {DelayText()}.";

        public void Bind(ConfigFile cfg, ConfigSync sync)
        {
            BackpackApi.RegisterBagHeaderButton("hopper_send_home", 10, bag => BagUpgrades.SlottedTier(bag, UpgradeId) > 0, bag => "Send home",
                bag => "Send the Hopper's collected loot home now (needs Remote Access in this bag and the tier's range)", bag => SendHomeNow(Player.m_localPlayer));
            for (int t = 0; t < BagUpgrades.TierCount; t++)
            {
                string n = BagUpgrades.TierNumerals[t];
                Radius[t] = cfg.Bind(BagUpgrades.Section, $"Bag Hopper {n} radius", DefaultRadius[t], new ConfigDescription($"Bag Hopper {n}: how far (m) loose loot is pulled in. Server-synced.", new AcceptableValueRange<float>(0f, 20f)));
                SendRange[t] = cfg.Bind(BagUpgrades.Section, $"Bag Hopper {n} send range", DefaultSendRange[t], new ConfigDescription($"Bag Hopper {n}: how far (horizontal m) from the home terminal loot is still sent home. Server-synced.", new AcceptableValueRange<float>(0f, 20000f)));
                sync?.AddConfigEntry(Radius[t]); sync?.AddConfigEntry(SendRange[t]);
            }
            s_sendDelay = cfg.Bind(BagUpgrades.Section, "Bag Hopper send delay", 30f, new ConfigDescription("Seconds vacuumed loot waits in the bag before it is sent home. 0 = never automatically (use the Send home button). Server-synced.", new AcceptableValueRange<float>(0f, 600f)));
            sync?.AddConfigEntry(s_sendDelay);
            BagUpgrades.Tick += OnTick;
            BagUpgrades.UpgradeBadge += Badge;
        }

        private static float RadiusFor(int tier) => tier < 1 ? 0f : Radius[tier - 1]?.Value ?? DefaultRadius[tier - 1];
        private static float SendRangeFor(int tier) => tier < 1 ? 0f : SendRange[tier - 1]?.Value ?? DefaultSendRange[tier - 1];
        private static float SendDelay => s_sendDelay?.Value ?? 30f;
        private static string DelayText() => SendDelay > 0f ? $"{SendDelay:0} s" : "you press Send home";
        private static double Now => ZNet.instance != null ? ZNet.instance.GetTimeSeconds() : Time.time;

        private static float s_nextWorn;
        private static void OnTick(Player p, float dt)
        {
            // 1.0.152 (Fire 11-05 perf pass, hot path Player.Update -> WornTier, every frame): asked at the vacuum's own 0.5 s
            if (ConfigManager.Instance.configPerfHopperWornCheck.Value) { if (Time.time < s_nextWorn) return; s_nextWorn = Time.time + VacuumInterval; }
            int tier = BagUpgrades.WornTier(p, UpgradeId);
            if (tier < 1 || p != Player.m_localPlayer || p.IsDead()) return;
            if (Time.time >= s_nextVacuum) { s_nextVacuum = Time.time + VacuumInterval; Vacuum(p, tier); }
            if (SendDelay > 0f && Time.time >= s_nextSend) { s_nextSend = Time.time + SendCheckInterval; SendWaiting(p, tier, false); }
        }

        private static void Vacuum(Player p, int tier)
        {
            ItemDrop.ItemData bag = BagUpgrades.WornBagWith(p, UpgradeId);
            Inventory inv = bag != null ? BagUpgrades.BagInventory(p, bag) : null;
            if (inv == null) return;
            if (s_itemMask < 0) s_itemMask = LayerMask.GetMask("item");
            int n = Physics.OverlapSphereNonAlloc(p.transform.position, RadiusFor(tier), s_hits, s_itemMask);
            int pulled = 0;
            for (int i = 0; i < n; i++)
            {
                var drop = s_hits[i] != null ? s_hits[i].GetComponentInParent<ItemDrop>() : null;
                if (drop == null || !drop.m_autoPickup || !drop.CanPickup()) continue;
                var nv = drop.GetComponent<ZNetView>();
                if (nv == null || !nv.IsValid() || !inv.CanAddItem(drop.m_itemData)) continue;
                if (!nv.IsOwner()) { nv.ClaimOwnership(); continue; }   // take it next sweep, once this peer owns it
                var item = drop.m_itemData.Clone();
                item.m_customData[PickedAtKey] = Now.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
                if (!inv.AddItem(item)) continue;
                ZNetScene.instance.Destroy(drop.gameObject);
                pulled += item.m_stack;
            }
            if (pulled > 0) Log.LogDebug($"[Hopper] pulled {pulled} item(s) into the bag (radius {RadiusFor(tier):0} m)");
        }

        /// <summary>Sends every vacuumed item home now, ignoring the delay (the bag panel's Send home button).</summary>
        public static void SendHomeNow(Player p)
        {
            int tier = BagUpgrades.WornTier(p, UpgradeId);
            if (tier < 1) return;
            SendWaiting(p, tier, true);
        }

        private static void SendWaiting(Player p, int tier, bool now)
        {
            if (s_sending) return;
            ItemDrop.ItemData bag = BagUpgrades.WornBagWith(p, UpgradeId);
            if (bag == null) return;
            if (BagUpgrades.SlottedTier(bag, RemoteAccessOption.UpgradeId) < 1) { if (now) p.Message(MessageHud.MessageType.Center, "Send home needs Remote Access in this bag"); return; }
            var items = new List<ItemDrop.ItemData>();
            foreach (var it in BagUpgrades.ItemsInBag(p, bag))
            {
                if (it == null || !it.m_customData.TryGetValue(PickedAtKey, out string at)) continue;
                double t; double.TryParse(at, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out t);
                if (now || Now - t >= SendDelay) items.Add(it);
            }
            items = BagUpgrades.SplitForSend(p, bag, items);   // big stacks (Compactor / Trophy Pouch) leave the bag only as vanilla-size pieces
            if (items.Count == 0) { if (now) p.Message(MessageHud.MessageType.Center, "Nothing vacuumed to send home"); return; }
            float range = SendRangeFor(tier);
            string why = RemoteAccessBridge.Refusal(p, range);
            if (why != null) { if (now) p.Message(MessageHud.MessageType.Center, RefusalMessage(why)); return; }
            s_sending = true;
            var sending = new List<ItemDrop.ItemData>(items);   // the hopper-tagged stacks this send covers
            RemoteAccessBridge.Deposit(p, items, range, committed =>
            {
                s_sending = false;
                // the callback reports ITEMS committed; the message counts STACKS: sent = emptied or gone from the bag, left = still holding items
                Inventory inv = BagUpgrades.BagInventory(p, bag);
                int sent = 0, left = 0;
                foreach (var it in sending)
                {
                    if (it == null) continue;
                    if (it.m_stack <= 0 || (inv != null && !inv.ContainsItem(it))) sent++; else left++;
                }
                foreach (var it in items) if (it != null && it.m_stack > 0) it.m_customData.Remove(PickedAtKey);   // what stayed (no room at home) waits no longer
                Log.LogInfo($"[Hopper] send home: {sent} stack(s) sent, {left} left, {committed} item(s) committed by the ledger");
                if (now) p.Message(MessageHud.MessageType.Center, left == 0
                    ? (sent == 1 ? "Sent 1 stack home" : $"Sent {sent} stacks home")
                    : (left == 1 ? "Home chests full: 1 stack left in your bag" : $"Home chests full: {left} stacks left in your bag"));
            });
        }

        // RemoteAccessBridge.Refusal's out-of-range text: "out of range ({d:0} m, this tier reaches {range:0} m)"
        private static readonly System.Text.RegularExpressions.Regex OutOfRange =
            new(@"^out of range \((\d+) m, this tier reaches (\d+) m\)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>The out-of-range refusal as "Out of range: X m from home (this tier reaches Y m)"; any other refusal
        /// (or one that doesn't parse) passes through after "Send home: ".</summary>
        private static string RefusalMessage(string why)
        {
            var m = OutOfRange.Match(why);
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (m.Success
                && long.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.None, inv, out long d)
                && long.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.None, inv, out long r))
                return $"Out of range: {d.ToString("N0", inv)} m from home (this tier reaches {r.ToString("N0", inv)} m)";
            return "Send home: " + why;
        }

        private static (string text, bool warn)? Badge(ItemDrop.ItemData upgrade)
        {
            string prefix = BagUpgrades.PrefabName(UpgradeId, 1).TrimEnd('1');   // "FiresBagUp_Hopper_"
            if (upgrade?.m_dropPrefab == null || !upgrade.m_dropPrefab.name.StartsWith(prefix, System.StringComparison.Ordinal)) return null;
            Player p = Player.m_localPlayer;
            ItemDrop.ItemData bag = p != null ? BagUpgrades.WornBagWith(p, UpgradeId) : null;
            if (bag == null) return null;
            int waiting = 0;
            foreach (var it in BagUpgrades.ItemsInBag(p, bag)) if (it != null && it.m_customData.ContainsKey(PickedAtKey)) waiting += it.m_stack;
            return waiting > 0 ? ($"{waiting}", false) : ((string, bool)?)null;
        }
    }
}
