// VARecipeManager.cs
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    public static class VARecipeManager
    {
        private static readonly Dictionary<string, GameObject> _prefabCache = new Dictionary<string, GameObject>();

        /// <summary>
        /// Call this after pieces are loaded and config is ready
        /// Applies recipe requirements to every enabled piece based on config
        /// </summary>
        public static void UpdatePieceRequirements()
        {
            if (ZNetScene.instance == null || MyMod.piecePrefabs.Count == 0)
            {
                Debug.Log($"{MyMod.PluginName}: Cannot update piece requirements - ZNetScene or piece list not ready.");
                return;
            }

            _prefabCache.Clear();

            // Cache all loaded piece prefabs by name (lowercase for reliable lookup)
            foreach (var prefab in MyMod.piecePrefabs)
            {
                if (prefab != null)
                {
                    _prefabCache[prefab.name.ToLowerInvariant()] = prefab;
                }
            }

            bool anyApplied = false;

            foreach (var kvp in ConfigManager.Instance.configPerPieceEnabled)
            {
                string pieceName = kvp.Key;

                // Skip if disabled or global recipe switch is off
                if (!kvp.Value.Value || !ConfigManager.Instance.configEnableRecipes.Value)
                    continue;

                if (!_prefabCache.TryGetValue(pieceName.ToLowerInvariant(), out GameObject piecePrefab))
                {
                    Debug.LogWarning($"{MyMod.PluginName}: Piece '{pieceName}' not found in loaded prefabs. Skipping recipe.");
                    continue;
                }

                var piece = piecePrefab.GetComponent<Piece>();
                if (piece == null)
                {
                    Debug.LogWarning($"{MyMod.PluginName}: Piece prefab '{pieceName}' has no Piece component.");
                    continue;
                }

                string configValue = ConfigManager.Instance.configPerPieceRecipes[pieceName].Value;
                var requirements = ParseRequirements(configValue);

                if (requirements.Length == 0)
                {
                    Debug.LogWarning($"{MyMod.PluginName}: No valid requirements parsed for '{pieceName}' from config: '{configValue}'");
                    continue;
                }

                piece.m_resources = requirements;
                anyApplied = true;

                if (ConfigManager.Instance.configVerboseLogging.Value)
                {
                    string reqList = string.Join(" | ", requirements.Select(r => $"{r.m_resItem.m_itemData.m_shared.m_name} x{r.m_amount}"));
                    Debug.Log($"{MyMod.PluginName}: Applied recipe to {pieceName}: {reqList}");
                }
            }

            if (anyApplied && ConfigManager.Instance.configVerboseLogging.Value)
                Debug.Log($"{MyMod.PluginName}: Piece recipe update complete.");
        }

        /// <summary>
        /// Parses a config string like "Wood,5" or "Wood,10,2,Stone,5,1" into Piece.Requirement[]
        /// </summary>
        private static Piece.Requirement[] ParseRequirements(string configValue)
        {
            var requirements = new List<Piece.Requirement>();
            var parts = configValue.Split(',').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToArray();

            for (int i = 0; i < parts.Length; i += 3)
            {
                if (i + 1 >= parts.Length) break;

                string itemName = parts[i];
                if (!int.TryParse(parts[i + 1], out int amount)) amount = 1;
                int perLevel = (i + 2 < parts.Length && int.TryParse(parts[i + 2], out int pl)) ? pl : 0;

                // Try to find the item prefab
                GameObject itemPrefab = null;
                string lowerName = itemName.ToLowerInvariant();

                if (_prefabCache.TryGetValue(lowerName, out itemPrefab))
                {
                    // Found in mod prefabs
                }
                else
                {
                    itemPrefab = ZNetScene.instance.GetPrefab(itemName) ??
                                 ObjectDB.instance.GetItemPrefab(itemName);

                    if (itemPrefab != null)
                        _prefabCache[lowerName] = itemPrefab; // Cache vanilla ones too
                }

                if (itemPrefab == null)
                {
                    Debug.LogWarning($"{MyMod.PluginName}: Could not find item prefab for '{itemName}' in recipe.");
                    continue;
                }

                var itemDrop = itemPrefab.GetComponent<ItemDrop>();
                if (itemDrop == null)
                {
                    Debug.LogWarning($"{MyMod.PluginName}: Prefab '{itemName}' has no ItemDrop component.");
                    continue;
                }

                requirements.Add(new Piece.Requirement
                {
                    m_resItem = itemDrop,
                    m_amount = amount,
                    m_amountPerLevel = perLevel,
                    m_recover = true
                });
            }

            return requirements.ToArray();
        }
    }
}