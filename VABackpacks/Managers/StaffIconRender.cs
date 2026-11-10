using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    /// <summary>
    /// Renders a FiresStaff_* icon from the staff's own model, once per staff at ObjectDB setup on a client (cached; no per-frame cost).
    /// The staff's meshes are copied (mesh + materials only, no components that wake) onto a free layer, posed head up-right at a 3/4 turn
    /// like the vanilla weapon icons, lit by one directional light that sees only that layer, and drawn twice by a disabled ortho camera:
    /// on black and on white. Alpha = 1 - (white - black) - Valheim's shaders do not write a usable alpha, so this gives a clean
    /// transparent background whatever they write. Everything made for the render is destroyed before returning.
    /// </summary>
    internal static class StaffIconRender
    {
        private const int Size = 128;
        private const float HeadShare = 0.3f;
        private static readonly BepInEx.Logging.ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("VABackpacks.StaffIcons");

        internal static Sprite Render(GameObject prefab, string id)
        {
            if (prefab == null) return null;
            int layer = FreeLayer();
            if (layer < 0) { Debug.LogWarning($"{MyMod.PluginName}: staff icon render: FiresStaff_{id}: no free layer"); return null; }

            var made = new List<UnityEngine.Object>();
            Color ambient = RenderSettings.ambientLight; var ambientMode = RenderSettings.ambientMode;
            var prevActive = RenderTexture.active;
            try
            {
                var root = new GameObject("FiresStaffIconRig_" + id);
                made.Add(root);
                root.transform.position = new Vector3(0f, -4000f, 0f);
                var pose = new GameObject("pose"); pose.transform.SetParent(root.transform, false);

                // The held copy is the "attach" child when the prefab has one; the dropped root otherwise
                Transform src = prefab.transform.Find("attach") ?? prefab.transform;
                int meshes = 0;
                foreach (var r in src.GetComponentsInChildren<Renderer>(false))
                {
                    if (r is ParticleSystemRenderer || !r.enabled) continue;
                    Mesh mesh = r is SkinnedMeshRenderer sk ? sk.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh;
                    if (mesh == null) continue;
                    var go = new GameObject(r.name); go.layer = layer;
                    go.transform.SetParent(pose.transform, false);
                    Matrix4x4 m = src.worldToLocalMatrix * r.transform.localToWorldMatrix;
                    go.transform.localPosition = m.GetColumn(3);
                    go.transform.localRotation = Quaternion.LookRotation(m.GetColumn(2), m.GetColumn(1));
                    go.transform.localScale = new Vector3(m.GetColumn(0).magnitude, m.GetColumn(1).magnitude, m.GetColumn(2).magnitude);
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var mr = go.AddComponent<MeshRenderer>();
                    mr.sharedMaterials = r.sharedMaterials;
                    mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    mr.receiveShadows = false;
                    meshes++;
                }
                if (meshes == 0) { Debug.LogWarning($"{MyMod.PluginName}: staff icon render: FiresStaff_{id}: no mesh on the prefab"); return null; }

                // Long axis of the model in its own space; the head is the end the glow (particles / light) sits at
                Bounds lb = LocalBounds(pose.transform);
                Vector3 e = lb.extents;
                Vector3 axis = e.x >= e.y && e.x >= e.z ? Vector3.right : e.y >= e.z ? Vector3.up : Vector3.forward;
                Vector3 glow = Vector3.zero; int g = 0;
                foreach (var c in src.GetComponentsInChildren<Component>(true))
                    if (c is ParticleSystem || c is Light) { glow += src.InverseTransformPoint(c.transform.position); g++; }
                float side = g > 0 ? Vector3.Dot(glow / g - lb.center, axis) : 1f;
                if (side < 0f) axis = -axis;
                pose.transform.localPosition = Vector3.zero;
                // head up, a 35 degree turn for the 3/4 look, then laid on the diagonal (head at the top right)
                pose.transform.localRotation = Quaternion.Euler(0f, 0f, -45f) * Quaternion.Euler(15f, 35f, 0f) * Quaternion.FromToRotation(axis, Vector3.up);

                Bounds wb = WorldBounds(pose.transform);
                var camGo = new GameObject("cam"); camGo.transform.SetParent(root.transform, false);
                var cam = camGo.AddComponent<Camera>();
                cam.enabled = false;
                cam.orthographic = true;
                // Framed on the head: the whole staff on the diagonal read as a thin stick at slot size (round 10-29 06:22). The view keeps
                // the top-right corner (the head) and shows HeadShare of the length; the shaft runs off the bottom left, like vanilla staffs.
                float half = Mathf.Max(wb.extents.x, wb.extents.y);
                cam.orthographicSize = half * HeadShare * 1.06f;
                Vector3 focus = wb.center + new Vector3(wb.extents.x, wb.extents.y, 0f) * (1f - HeadShare);
                cam.transform.position = focus - Vector3.forward * (wb.extents.z + 2f);
                cam.transform.rotation = Quaternion.identity;
                cam.nearClipPlane = 0.01f; cam.farClipPlane = wb.size.z + 4f;
                cam.cullingMask = 1 << layer;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.allowHDR = false; cam.allowMSAA = true;

                var lightGo = new GameObject("key"); lightGo.transform.SetParent(root.transform, false);
                lightGo.transform.rotation = Quaternion.Euler(35f, -30f, 0f);
                var key = lightGo.AddComponent<Light>();
                key.type = LightType.Directional; key.intensity = 1.25f; key.color = new Color(1f, 0.97f, 0.92f);
                key.cullingMask = 1 << layer; key.shadows = LightShadows.None;
                // The gem: a point light in the staff's own (held-tint) light colour at each light on the prefab, seen only by this layer
                int gems = 0; Color gemCol = Color.clear;
                foreach (var pl in src.GetComponentsInChildren<Light>(true))
                {
                    var lg = new GameObject("gem"); lg.transform.SetParent(pose.transform, false);
                    lg.transform.localPosition = src.InverseTransformPoint(pl.transform.position);
                    var gl = lg.AddComponent<Light>();
                    gl.type = LightType.Point; gl.color = pl.color; gl.intensity = 3f; gl.range = half * 0.35f;
                    gl.cullingMask = 1 << layer; gl.shadows = LightShadows.None;
                    gemCol = pl.color; gems++;
                }
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.55f, 0.55f, 0.6f);

                var rt = RenderTexture.GetTemporary(Size, Size, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default, 8);
                cam.targetTexture = rt;
                Color32[] black = Grab(cam, rt, Color.black), white = Grab(cam, rt, Color.white);
                cam.targetTexture = null;
                RenderTexture.ReleaseTemporary(rt);

                var px = new Color32[black.Length];
                int solid = 0;
                for (int i = 0; i < px.Length; i++)
                {
                    float a = 1f - Mathf.Max(white[i].r - black[i].r, Mathf.Max(white[i].g - black[i].g, white[i].b - black[i].b)) / 255f;
                    a = Mathf.Clamp01(a);
                    if (a < 0.02f) { px[i] = new Color32(0, 0, 0, 0); continue; }
                    // un-premultiply the black render by the alpha
                    px[i] = new Color32((byte)Mathf.Min(255f, black[i].r / a), (byte)Mathf.Min(255f, black[i].g / a), (byte)Mathf.Min(255f, black[i].b / a), (byte)(a * 255f));
                    if (a > 0.5f) solid++;
                }
                var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false) { name = "FiresStaff_" + id + "_icon", wrapMode = TextureWrapMode.Clamp };
                tex.SetPixels32(px); tex.Apply(false, false);
                var s = Sprite.Create(tex, new Rect(0, 0, Size, Size), new Vector2(0.5f, 0.5f));
                s.name = tex.name;
                Log.LogDebug($"FiresStaff_{id} icon RENDERED {Size}x{Size} from the model ({meshes} mesh(es), layer {layer}, head by {(g > 0 ? g + " glow part(s)" : "default +axis")}, {solid * 100 / px.Length}% solid, head framing {HeadShare:0.00}, {gems} gem light(s) #{ColorUtility.ToHtmlStringRGB(gemCol)})");
                Dump(tex, id);
                return s;
            }
            catch (Exception ex) { Debug.LogWarning($"{MyMod.PluginName}: staff icon render: FiresStaff_{id} failed: {ex.Message}"); return null; }
            finally
            {
                RenderSettings.ambientLight = ambient; RenderSettings.ambientMode = ambientMode;
                RenderTexture.active = prevActive;
                foreach (var o in made) if (o != null) UnityEngine.Object.DestroyImmediate(o);
            }
        }

        private static Color32[] Grab(Camera cam, RenderTexture rt, Color bg)
        {
            cam.backgroundColor = new Color(bg.r, bg.g, bg.b, 0f);
            cam.Render();
            RenderTexture.active = rt;
            var t = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            t.ReadPixels(new Rect(0, 0, Size, Size), 0, 0, false);
            t.Apply(false);
            var px = t.GetPixels32();
            UnityEngine.Object.DestroyImmediate(t);
            return px;
        }

        // A copy of each icon in BepInEx\cache\VABackpacks\staff_icons (the proof of what was rendered); a failed write only logs
        private static void Dump(Texture2D tex, string id)
        {
            try
            {
                var enc = Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule")?.GetMethod("EncodeToPNG", new[] { typeof(Texture2D) });
                if (enc == null) return;
                string dir = System.IO.Path.Combine(BepInEx.Paths.CachePath, "VABackpacks", "staff_icons");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "FiresStaff_" + id + ".png"), (byte[])enc.Invoke(null, new object[] { tex }));
            }
            catch (Exception e) { VabLog.Line($"{MyMod.PluginName}: staff icon render: FiresStaff_{id} cache copy not written: {e.Message}"); }
        }

        private static int FreeLayer()
        {
            for (int i = 31; i >= 8; i--) if (string.IsNullOrEmpty(LayerMask.LayerToName(i))) return i;
            return -1;
        }

        private static Bounds LocalBounds(Transform pose)
        {
            bool any = false; var b = new Bounds();
            foreach (var mf in pose.GetComponentsInChildren<MeshFilter>())
            {
                var mb = mf.sharedMesh.bounds;
                Matrix4x4 m = pose.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                foreach (var c in Corners(mb)) { var p = m.MultiplyPoint3x4(c); if (!any) { b = new Bounds(p, Vector3.zero); any = true; } else b.Encapsulate(p); }
            }
            return b;
        }

        private static Bounds WorldBounds(Transform pose)
        {
            bool any = false; var b = new Bounds();
            foreach (var mf in pose.GetComponentsInChildren<MeshFilter>())
            {
                var mb = mf.sharedMesh.bounds;
                foreach (var c in Corners(mb)) { var p = mf.transform.TransformPoint(c); if (!any) { b = new Bounds(p, Vector3.zero); any = true; } else b.Encapsulate(p); }
            }
            return b;
        }

        private static IEnumerable<Vector3> Corners(Bounds b)
        {
            for (int i = 0; i < 8; i++)
                yield return b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
        }
    }
}
