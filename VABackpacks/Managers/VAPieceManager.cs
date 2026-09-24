using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    public static class VAPieceManager
    {
        public static readonly List<string> PieceNames = new List<string>
        {
            



        };

        public static void LoadPieces(string mainPath, string modInstance)
        {
            if (MyMod.assetBundle == null)
            {
                Debug.LogError($"{MyMod.PluginName}: AssetBundle is null.");
                return;
            }

            MyMod.piecePrefabs.Clear();
            var allAssets = MyMod.assetBundle.GetAllAssetNames();
            Debug.Log($"{MyMod.PluginName}: AssetBundle contains {allAssets.Length} assets");
            if (ConfigManager.Instance?.configVerboseLogging?.Value ?? false)
                Debug.Log($"{MyMod.PluginName}: bundle assets: {string.Join(", ", allAssets)}");

            int failed = 0;
            foreach (var pieceName in PieceNames)
            {
                string prefabPath = mainPath + "pieces/" + pieceName + ".prefab";
                string normalizedPath = prefabPath.Replace('\\', '/').ToLower();
                GameObject piecePrefab = MyMod.assetBundle.LoadAsset<GameObject>(normalizedPath);
                if (piecePrefab == null)
                {
                    Debug.LogError($"{MyMod.PluginName}: Failed to load piece prefab: {normalizedPath}");
                    failed++;
                    continue;
                }

                var piece = piecePrefab.GetComponent<Piece>();
                if (piece == null)
                {
                    Debug.LogError($"{MyMod.PluginName}: {pieceName} - Piece component missing!");
                    UnityEngine.Object.Destroy(piecePrefab);
                    failed++;
                    continue;
                }

                if (piece.m_category == Piece.PieceCategory.Max)
                    piece.m_category = Piece.PieceCategory.Furniture;

                if (piece.m_craftingStation == null)
                {
                    var workbench = ZNetScene.instance?.GetPrefab("piece_workbench")?.GetComponent<CraftingStation>();
                    piece.m_craftingStation = workbench;
                }

                piecePrefab.SetActive(true);

                MyMod.piecePrefabs.Add(piecePrefab);
                Debug.Log($"{MyMod.PluginName}: Loaded piece prefab: {pieceName}");
            }

            Debug.Log($"{MyMod.PluginName}: Loaded {MyMod.piecePrefabs.Count} pieces. Failed: {failed}");
        }

        public static void AddPiecesToHammer()
        {
            if (!MyMod.IsObjectDBValid() || ObjectDB.instance == null)
            {
                Debug.LogWarning($"{MyMod.PluginName}: ObjectDB not valid, cannot add pieces to Hammer");
                return;
            }

            var hammer = ObjectDB.instance.GetItemPrefab("Hammer");
            if (hammer == null)
            {
                Debug.LogWarning($"{MyMod.PluginName}: Hammer prefab not found in ObjectDB");
                return;
            }

            var itemDrop = hammer.GetComponent<ItemDrop>();
            if (itemDrop == null)
            {
                Debug.LogWarning($"{MyMod.PluginName}: Hammer ItemDrop is null");
                return;
            }

            var pieceTable = itemDrop.m_itemData?.m_shared?.m_buildPieces;
            if (pieceTable == null)
            {
                Debug.LogWarning($"{MyMod.PluginName}: Hammer PieceTable is null");
                return;
            }

            int added = 0;
            foreach (var piece in MyMod.piecePrefabs)
            {
                if (piece == null)
                {
                    Debug.LogWarning($"{MyMod.PluginName}: Null piece prefab detected");
                    continue;
                }

                if (!pieceTable.m_pieces.Contains(piece))
                {
                    pieceTable.m_pieces.Add(piece);
                    added++;
                }
            }
            if (added > 0) Debug.Log($"{MyMod.PluginName}: added {added} piece(s) to the Hammer");
        }
    }
}