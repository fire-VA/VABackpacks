using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.Rendering;

#nullable disable

namespace VerdantsAscent.Managers
{
    /// <summary>
    /// 1.0.24: a worn shoulder bag shows on the player's back.
    ///
    /// Shoulder bags are kept out of m_shoulderItem so a cape can be worn
    /// with them (see BackpackManager, "Cape + Backpack coexistence"), so
    /// vanilla VisEquipment never attaches them and a worn bag had no
    /// visual at all. This draws the bag's own worn model instead: each
    /// bag's attach_skin child holds a one-bone skinned mesh laid out on a
    /// copy of the player skeleton. The mesh is placed on the player's
    /// Spine2 with the same offset it has from that skeleton's Spine2, as a
    /// plain MeshRenderer, which follows the bone without skinning cost.
    ///
    /// The owner writes the worn bag's prefab hash to the player's ZDO
    /// (va_bp_worn), so every client draws other players' bags too. A cape
    /// keeps its own vanilla visual alongside.
    /// </summary>
    public static class BackpackWornVisual
    {
        private static readonly int ZdoKey = "va_bp_worn".GetStableHashCode();
        private const string NamePrefix = "VA_WornBag_";
        private const string AnchorBone = "Spine2";

        private static readonly AccessTools.FieldRef<VisEquipment, ZNetView> NViewRef = AccessTools.FieldRefAccess<VisEquipment, ZNetView>("m_nview");
        private static readonly AccessTools.FieldRef<VisEquipment, GameObject> VisualRef = AccessTools.FieldRefAccess<VisEquipment, GameObject>("m_visual");
        private static readonly AccessTools.FieldRef<VisEquipment, LODGroup> LodRef = AccessTools.FieldRefAccess<VisEquipment, LODGroup>("m_lodGroup");
        private static readonly MethodInfo UpdateLodgroupMethod = AccessTools.Method(typeof(VisEquipment), "UpdateLodgroup");

        // A dedicated server draws nothing.
        private static readonly bool Headless = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;

        private sealed class State
        {
            public Player Player;
            public int Hash;                       // what is drawn now (0 = nothing)
            public ItemDrop.ItemData HashedItem;   // owner side: the item whose name hash is cached below
            public int ItemHash;
            public readonly List<GameObject> Parts = new List<GameObject>();
        }
        private static readonly ConditionalWeakTable<VisEquipment, State> States = new ConditionalWeakTable<VisEquipment, State>();

        // One worn mesh of a bag, worked out once per prefab from its attach_skin.
        private sealed class Part
        {
            public string Bone;
            public Mesh Mesh;
            public Material[] Materials;
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 Scale;
        }
        private static readonly Dictionary<int, List<Part>> Parts = new Dictionary<int, List<Part>>();

        [HarmonyPatch(typeof(VisEquipment), "UpdateEquipmentVisuals")]
        public static class VisEquipment_UpdateEquipmentVisuals_WornBag_Patch
        {
            public static void Postfix(VisEquipment __instance)
            {
                if (Headless || !__instance.m_isPlayer || __instance.m_isArmorStand) return;
                try { Refresh(__instance); }
                catch (System.Exception ex) { Debug.LogWarning($"{MyMod.PluginName}: worn bag visual failed: {ex.Message}"); }
            }
        }

        private static void Refresh(VisEquipment ve)
        {
            State state = States.GetValue(ve, v => new State { Player = v.GetComponent<Player>() });
            ZNetView nview = NViewRef(ve);
            ZDO zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;

            int want;
            if (state.Player != null && (zdo == null || nview.IsOwner()))
            {
                want = WornHash(state);
                if (zdo != null && zdo.GetInt(ZdoKey) != want) zdo.Set(ZdoKey, want);
            }
            else
            {
                want = zdo != null ? zdo.GetInt(ZdoKey) : 0;
            }

            if (want == state.Hash) return;
            Draw(ve, state, want);
        }

        // Owner side: the shoulder bag in the coexistence dict. Weapon bags
        // (Hobo Stick, Wizard Staff) are wielded through vanilla, which
        // already draws them.
        private static int WornHash(State state)
        {
            if (!BackpackManager.TryGetEquippedBackpack(state.Player, out ItemDrop.ItemData item) || item?.m_dropPrefab == null)
            {
                state.HashedItem = null;
                return 0;
            }
            if (!ReferenceEquals(item, state.HashedItem))
            {
                state.HashedItem = item;
                state.ItemHash = item.m_dropPrefab.name.GetStableHashCode();
            }
            return state.ItemHash;
        }

        private static void Draw(VisEquipment ve, State state, int hash)
        {
            LODGroup lod = LodRef(ve);
            foreach (GameObject old in state.Parts)
            {
                if (old == null) continue;
                if (lod != null) Utils.RemoveFromLodgroup(lod, old);
                Object.Destroy(old);
            }
            state.Parts.Clear();
            state.Hash = hash;
            if (hash == 0) return;

            GameObject visual = VisualRef(ve);
            if (visual == null) return;
            GameObject prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(hash) : null;
            List<Part> parts = PartsOf(hash, prefab);

            var bones = new List<string>();
            foreach (Part part in parts)
            {
                Transform bone = Utils.FindChild(visual.transform, part.Bone);
                if (bone == null)
                {
                    Debug.LogWarning($"{MyMod.PluginName}: worn bag {prefab?.name}: no bone '{part.Bone}' on {ve.name}");
                    continue;
                }
                var go = new GameObject(NamePrefix + (prefab != null ? prefab.name : hash.ToString()));
                go.layer = bone.gameObject.layer;
                go.transform.SetParent(bone, false);
                go.transform.localPosition = part.Position;
                go.transform.localRotation = part.Rotation;
                go.transform.localScale = part.Scale;
                go.AddComponent<MeshFilter>().sharedMesh = part.Mesh;
                go.AddComponent<MeshRenderer>().sharedMaterials = part.Materials;
                state.Parts.Add(go);
                bones.Add(bone.name);
            }
            if (lod != null && state.Parts.Count > 0) UpdateLodgroupMethod?.Invoke(ve, null);

            string who = state.Player != null ? state.Player.GetPlayerName() : ve.name;
            Debug.Log($"{MyMod.PluginName}: worn visual {prefab?.name ?? hash.ToString()} on {who}: " +
                      $"{state.Parts.Count} renderer(s) on {(bones.Count > 0 ? string.Join(",", bones) : "no bone")}");
        }

        private static List<Part> PartsOf(int hash, GameObject prefab)
        {
            if (Parts.TryGetValue(hash, out List<Part> parts)) return parts;
            parts = new List<Part>();
            Transform skin = prefab != null ? prefab.transform.Find("attach_skin") : null;
            if (skin != null)
            {
                foreach (SkinnedMeshRenderer smr in skin.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    Mesh mesh = smr.sharedMesh;
                    // Unused cape placeholders in the bags carry no mesh.
                    if (mesh == null || !smr.enabled || !ActiveUnder(smr.transform, skin)) continue;
                    Transform[] smrBones = smr.bones;
                    Matrix4x4[] bind = mesh.bindposes;
                    if (smr.rootBone == null || smrBones == null || smrBones.Length != 1 || smrBones[0] == null || bind == null || bind.Length != 1)
                    {
                        Debug.LogWarning($"{MyMod.PluginName}: worn bag {prefab.name}: '{smr.name}' is not a one-bone skin; not drawn");
                        continue;
                    }
                    // Where the mesh sits relative to Spine2 of the bag's skeleton copy (the bags sit at Spine2
                    // height, so they ride the upper back; the skin's own root bone if there is no Spine2); the
                    // player's bone of the same name gets the same offset.
                    Transform anchor = Utils.FindChild(skin, AnchorBone) ?? smr.rootBone;
                    Matrix4x4 m = anchor.worldToLocalMatrix * smrBones[0].localToWorldMatrix * bind[0];
                    parts.Add(new Part
                    {
                        Bone = anchor.name,
                        Mesh = mesh,
                        Materials = smr.sharedMaterials,
                        Position = m.GetColumn(3),
                        Rotation = m.rotation,
                        Scale = m.lossyScale,
                    });
                }
            }
            if (prefab == null) return parts;   // ObjectDB not ready: try again next change
            if (parts.Count == 0)
                Debug.LogWarning($"{MyMod.PluginName}: worn bag {prefab.name}: no worn mesh under attach_skin");
            Parts[hash] = parts;
            return parts;
        }

        /// <summary>
        /// 1.0.25: the bag's worn mesh as a standalone object, standing upright as it hangs on a back (the camp's chest
        /// decoration uses it): one child MeshRenderer per worn mesh, posed in the frame of the player's root (+Y up, the
        /// bag's back panel facing +Z, where the player's back would be). Null when the prefab has no one-bone worn mesh
        /// (the Hobo Stick and Wizard Staff).
        /// </summary>
        internal static GameObject BuildStandingCopy(GameObject prefab, string name)
        {
            Transform skin = prefab != null ? prefab.transform.Find("attach_skin") : null;
            if (skin == null) return null;
            GameObject root = null;
            foreach (SkinnedMeshRenderer smr in skin.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                Mesh mesh = smr.sharedMesh;
                if (mesh == null || !smr.enabled || !ActiveUnder(smr.transform, skin)) continue;
                Transform[] smrBones = smr.bones;
                Matrix4x4[] bind = mesh.bindposes;
                if (smrBones == null || smrBones.Length != 1 || smrBones[0] == null || bind == null || bind.Length != 1) continue;
                Matrix4x4 m = skin.worldToLocalMatrix * smrBones[0].localToWorldMatrix * bind[0];
                if (root == null) root = new GameObject(name);
                var go = new GameObject(NamePrefix + prefab.name);
                go.transform.SetParent(root.transform, false);
                go.transform.localPosition = m.GetColumn(3);
                go.transform.localRotation = m.rotation;
                go.transform.localScale = m.lossyScale;
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterials = smr.sharedMaterials;
            }
            return root;
        }

        private static bool ActiveUnder(Transform t, Transform top)
        {
            for (; t != null && t != top; t = t.parent)
                if (!t.gameObject.activeSelf) return false;
            return true;
        }
    }
}
