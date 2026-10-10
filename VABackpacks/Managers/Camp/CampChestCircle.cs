// CampChestCircle - 1.0.41 (Fire's camp redesign: "the white build circle shown when the hammer is out at a camp is CENTRED
// ON THE CHEST, with radius = the camp's save radius"). The camp chest is the camp centre; this draws vanilla's own build-area
// circle (a copy of the workbench's area marker) around it, sized to the ring pack-up saves, while the local player has the
// hammer out near the camp. The camp workbench keeps its vanilla circle and range and anchors nothing.

using UnityEngine;

namespace VerdantsAscent.Managers.Camp
{
    internal sealed class CampChestCircle : MonoBehaviour
    {
        const float ShowWithin = 20f;   // beyond the ring, so the ring is seen from outside it too

        BackpackCampMarker marker;
        GameObject circle;
        CircleProjector projector;
        bool logged;

        void Awake() => marker = GetComponent<BackpackCampMarker>();

        // 1.0.41: the one ring definition pack-up uses too (CampRing).
        float Radius => marker == null ? 0f : CampRing.Radius(marker);

        void Update()
        {
            var player = Player.m_localPlayer;
            bool show = player != null && marker != null && Radius > 0f && player.InPlaceMode()
                && (player.transform.position - transform.position).sqrMagnitude <= (Radius + ShowWithin) * (Radius + ShowWithin);
            if (show && circle == null && !Create()) return;
            if (circle == null) return;
            if (circle.activeSelf != show) circle.SetActive(show);
            if (show)
            {
                circle.transform.position = CampRing.Centre(marker);
                if (projector != null && !Mathf.Approximately(projector.m_radius, Radius)) projector.m_radius = Radius;
                Tint();   // 1.0.45: cheap now (a child-count compare until segments change)
            }
        }

        bool Create()
        {
            var bench = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab("piece_workbench") : null;
            var source = bench != null ? bench.GetComponent<CraftingStation>()?.m_areaMarker : null;
            if (source == null)
            {
                if (!logged) { logged = true; Debug.Log("[VABackpacks] camp circle: no workbench area marker to copy; no circle"); }
                return false;
            }
            // Not parented to the chest: the chest's scale must not stretch the ring (vanilla's circle is drawn at scale 1).
            circle = Instantiate(source, CampRing.Centre(marker), Quaternion.identity);
            circle.name = "VACampSaveRing";
            circle.transform.localScale = source.transform.lossyScale;
            projector = circle.GetComponent<CircleProjector>();
            if (projector != null) projector.m_radius = Radius;
            circle.SetActive(false);
            Debug.Log($"[VABackpacks] camp circle: the save ring ({Radius:0.#} m) is drawn around the camp chest at {CampRing.Centre(marker)} (CampRing, the same circle pack-up captures)");
            return true;
        }

        // 1.0.42 (Fire: "make the camp save ring GREEN so it stands out"): vanilla's circle, its segment renderers tinted green.
        // The projector adds segments as the radius grows, so the tint is re-applied when the renderer count changes.
        static readonly Color Green = new Color(0.25f, 1f, 0.35f, 1f);
        int tinted = -1, tintedChildren = -1;
        float nextTint;

        void Tint()
        {
            // 1.0.45 ([lead]): the renderers are looked up again only when the projector's segment count changes.
            int children = circle.transform.childCount;
            if (children == tintedChildren) return;
            tintedChildren = children;
            var renderers = circle.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == tinted) return;
            tinted = renderers.Length;
            foreach (var r in renderers)
            {
                if (r == null) continue;
                foreach (var m in r.materials)
                {
                    if (m == null) continue;
                    if (m.HasProperty("_Color")) m.color = Green;
                    if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", Green);
                }
            }
        }

        void OnDestroy()
        {
            if (circle != null) Destroy(circle);
        }
    }
}

namespace VerdantsAscent.Managers.Camp
{
    // 1.0.42 (Fire: "show ONLY the chest ring"): the camp's own workbench never draws its vanilla build-range circle, so
    // the green save ring around the chest is the only ring at a camp. Its build range is untouched.
    [HarmonyLib.HarmonyPatch(typeof(CraftingStation), nameof(CraftingStation.ShowAreaMarker))]
    internal static class CampWorkbenchMarkerHide
    {
        static bool Prefix(CraftingStation __instance)
        {
            var nview = __instance != null ? __instance.GetComponent<ZNetView>() : null;
            bool campPiece = nview != null && nview.IsValid() && nview.GetZDO().GetInt(CampImmortalPatch.KEY_IMMORTAL, 0) == 1;
            return !campPiece;
        }
    }
}
