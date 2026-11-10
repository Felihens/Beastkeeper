using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace Beastkeeper
{
    // Saves the rideable animals' models (as they stand in their prefab, in the animal's own frame) for the saddle
    // editor page, once, when you enter a world. One JSON file per animal in BepInEx/config/Beastkeeper_models.
    internal static class Exporter
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static float _ridingSince = -1f, _poseAt;
        private static List<string> _poses;
        private static int _poseIndex;
        private static string _homePose;
        private static readonly FieldInfo AttachAnim = AccessTools.Field(typeof(Player), "m_attachAnimation");

        // true while the rider export is switching poses (the mod's lean stays off so the editor gets the plain pose)
        internal static bool Busy { get { return _poses != null || _prepUntil > 0f; } }
        private static float _prepUntil;

        // your character as it sits on a mount, in the seat's frame, so the editor can show a rider on the seat.
        // It saves you in the pose you ride in (Rider.json, with textures), then switches through every riding pose
        // the game has and saves the shape of each (Rider_<pose>.json), then puts your pose back.
        private static void UpdateRider()
        {
            Mount m = Mount.Riding;
            Player p = Player.m_localPlayer;
            bool ok = m != null && p != null && m.Seat != null && p.IsAttached() && !Sea.IsSeaMount(m.GetComponent<Character>());
            if (_poses != null)
            {
                if (!ok)
                {
                    if (p != null && _poseIndex < _poses.Count) p.GetComponent<ZSyncAnimation>().SetBool(_poses[_poseIndex], false);
                    _poses = null;
                    return;
                }
                if (Time.time < _poseAt) return;
                ZSyncAnimation za = p.GetComponent<ZSyncAnimation>();
                try
                {
                    string pose = _poses[_poseIndex];
                    Save(m, p, "Rider_" + pose, false);
                    za.SetBool(pose, false);
                }
                catch (System.Exception e) { Plugin.Log.LogWarning("[export] rider pose: " + e); }
                _poseIndex++;
                if (_poseIndex < _poses.Count)
                {
                    za.SetBool(_poses[_poseIndex], true);
                    _poseAt = Time.time + 1.5f;
                    return;
                }
                za.SetBool(_homePose, true);
                Plugin.Log.LogInfo("[export] rider saved in " + _poses.Count + " poses: " + string.Join(", ", _poses.ToArray()));
                p.Message(MessageHud.MessageType.Center, "Beastkeeper: rider saved in " + _poses.Count + " poses for the saddle editor");
                _poses = null;
                return;
            }
            if (!Plugin.ExportForEditor.Value || !ok) { _ridingSince = -1f; _prepUntil = 0f; return; }
            if (_ridingSince < 0f) { _ridingSince = Time.time; return; }
            if (Time.time - _ridingSince < 2f) return;
            // first a few frames with the mod's lean off, so the saved pose is the game's own
            if (_prepUntil <= 0f) { _prepUntil = Time.time + 0.3f; return; }
            if (Time.time < _prepUntil) return;
            _prepUntil = 0f;
            Plugin.ExportForEditor.Value = false;
            _modelsDone = false;
            try
            {
                Save(m, p, "Rider", true);
                _homePose = AttachAnim.GetValue(p) as string ?? Mount.DefaultPose;
                // every sitting pose the character's animator knows
                _poses = new List<string>();
                Animator an = p.GetComponentInChildren<Animator>();
                foreach (AnimatorControllerParameter par in an.parameters)
                    if (par.type == AnimatorControllerParameterType.Bool && par.name.StartsWith("attach_")) _poses.Add(par.name);
                Plugin.Log.LogInfo("[export] riding poses the game has: " + string.Join(", ", _poses.ToArray()));
                _poses.Remove(_homePose);
                _poses.Insert(0, _homePose);
                ZSyncAnimation za = p.GetComponent<ZSyncAnimation>();
                za.SetBool(_homePose, true);
                _poseIndex = 0;
                _poseAt = Time.time + 0.2f;
                p.Message(MessageHud.MessageType.Center, "Beastkeeper: saving your riding poses, hold still a few seconds");
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("[export] rider: " + e); _poses = null; }
        }

        // bakes the character in the seat's frame; 'full' adds the uv, triangles and textures (else only the corners)
        private static void Save(Mount m, Player p, string file, bool full)
        {
            Transform seat = m.Seat;
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"name\":\"").Append(file).Append("\"");
            Transform[] joints = Joints(p, seat);
            string[] jointNames = { "spine", "hipL", "hipR" };
            for (int j = 0; j < 3; j++)
            {
                if (joints[j] == null) continue;
                Vector3 sp = seat.InverseTransformPoint(joints[j].position);
                sb.Append(",\"").Append(jointNames[j]).Append("\":[").Append(F(sp.x)).Append(",").Append(F(sp.y)).Append(",").Append(F(sp.z)).Append("]");
            }
            sb.Append(",\"parts\":[");
            bool first = true;
            int count = 0;
            foreach (SkinnedMeshRenderer sk in p.GetComponentsInChildren<SkinnedMeshRenderer>(false))
            {
                if (!sk.enabled || sk.sharedMesh == null) continue;
                Mesh baked = new Mesh();
                sk.BakeMesh(baked, true);
                Matrix4x4 mx = seat.worldToLocalMatrix * sk.transform.localToWorldMatrix;
                if (!first) sb.Append(",");
                first = false;
                if (full) Part(sb, sk.name, baked, mx, sk.sharedMaterial, Weights(sk, baked, joints, p.transform.right));
                else
                {
                    Vector3[] v = baked.vertices;
                    sb.Append("{\"name\":\"").Append(sk.name).Append("\",\"v\":[");
                    for (int i = 0; i < v.Length; i++)
                    {
                        Vector3 q = mx.MultiplyPoint3x4(v[i]);
                        if (i > 0) sb.Append(",");
                        sb.Append(F(q.x)).Append(",").Append(F(q.y)).Append(",").Append(F(q.z));
                    }
                    sb.Append("]}");
                }
                Object.Destroy(baked);
                count++;
            }
            sb.Append("]}");
            string dir = Path.Combine(BepInEx.Paths.ConfigPath, "Beastkeeper_models");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, file + ".json"), sb.ToString());
            Plugin.Log.LogInfo("[export] " + file + " saved: " + count + " meshes, on " + m.name);
        }

        // the back bone and the two thigh bones (left = the one on the seat's -x side)
        private static Transform[] Joints(Player p, Transform seat)
        {
            Transform spine = null, a = null, b = null;
            foreach (Transform t in p.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == "Spine" && spine == null) spine = t;
                else if (t.name.EndsWith("UpLeg", System.StringComparison.OrdinalIgnoreCase)) { if (a == null) a = t; else if (b == null) b = t; }
            }
            if (a != null && b != null && seat.InverseTransformPoint(a.position).x > seat.InverseTransformPoint(b.position).x) { Transform x = a; a = b; b = x; }
            return new[] { spine, a, b };
        }

        // how much each corner follows each joint (0-1): turn the bone a little, bake again, and compare how far
        // each corner moved with how far it would move if it were stuck to the bone
        private static string Weights(SkinnedMeshRenderer sk, Mesh rest, Transform[] joints, Vector3 axis)
        {
            StringBuilder sb = new StringBuilder();
            string[] keys = { "ws", "wl", "wr" };
            Vector3[] v0 = rest.vertices;
            Matrix4x4 toWorld = sk.transform.localToWorldMatrix;
            const float angle = 20f;
            Quaternion q = Quaternion.AngleAxis(angle, axis);
            for (int j = 0; j < 3; j++)
            {
                Transform bone = joints[j];
                if (bone == null) continue;
                Quaternion keep = bone.rotation;
                bone.rotation = q * keep;
                Mesh moved = new Mesh();
                sk.BakeMesh(moved, true);
                bone.rotation = keep;
                Vector3[] v1 = moved.vertices;
                Object.Destroy(moved);
                Vector3 pivot = bone.position;
                sb.Append(",\"").Append(keys[j]).Append("\":[");
                for (int i = 0; i < v0.Length; i++)
                {
                    Vector3 w0 = toWorld.MultiplyPoint3x4(v0[i]), w1 = toWorld.MultiplyPoint3x4(v1[i]);
                    float rigid = (pivot + q * (w0 - pivot) - w0).magnitude;
                    float w = rigid > 0.0005f ? Mathf.Clamp01((w1 - w0).magnitude / rigid) : 0f;
                    if (i > 0) sb.Append(",");
                    sb.Append(w < 0.005f ? "0" : w.ToString("0.##", Inv));
                }
                sb.Append("]");
            }
            return sb.ToString();
        }

        private static void Part(StringBuilder sb, string name, Mesh baked, Matrix4x4 m, Material mat, string extra = "")
        {
            Vector3[] v = baked.vertices;
            Vector2[] uv = baked.uv;
            int[] tri = baked.triangles;
            sb.Append("{\"name\":\"").Append(name).Append("\",\"v\":[");
            for (int i = 0; i < v.Length; i++)
            {
                Vector3 q = m.MultiplyPoint3x4(v[i]);
                if (i > 0) sb.Append(",");
                sb.Append(F(q.x)).Append(",").Append(F(q.y)).Append(",").Append(F(q.z));
            }
            sb.Append("],\"uv\":[");
            for (int i = 0; i < uv.Length; i++) { if (i > 0) sb.Append(","); sb.Append(F(uv[i].x)).Append(",").Append(F(uv[i].y)); }
            sb.Append("],\"t\":[");
            for (int i = 0; i < tri.Length; i++) { if (i > 0) sb.Append(","); sb.Append(tri[i]); }
            sb.Append("],\"tex\":\"").Append(Png(mat)).Append("\"").Append(extra).Append("}");
        }

        private static bool _modelsDone;
        private const string EditorAnimals = "Boar,Wolf,Deer,Bjorn,Lox,Asksvin,Seal,Moose";

        internal static void Update()
        {
            UpdateRider();
            if (!Plugin.ExportForEditor.Value || _modelsDone) return;
            if (Player.m_localPlayer == null || ZNetScene.instance == null || ObjectDB.instance == null) return;
            string dir = Path.Combine(BepInEx.Paths.ConfigPath, "Beastkeeper_models");
            Directory.CreateDirectory(dir);
            int saved = 0;
            foreach (string raw in EditorAnimals.Split(','))
            {
                string name = raw.Trim();
                if (name.Length == 0) continue;
                try { if (Export(name, dir)) saved++; }
                catch (System.Exception e) { Plugin.Log.LogWarning("[export] " + name + ": " + e); }
            }
            _modelsDone = true;
            Plugin.Log.LogInfo("[export] saved " + saved + " animal models to " + dir);
            Player.m_localPlayer.Message(MessageHud.MessageType.Center, "Beastkeeper: " + saved + " animal models saved for the saddle editor (now ride an animal to save your poses)");
        }

        private static bool Export(string name, string dir)
        {
            GameObject prefab = ZNetScene.instance.GetPrefab(name);
            if (prefab == null) { Plugin.Log.LogInfo("[export] no prefab " + name); return false; }
            // a copy that never wakes up (no scripts run while its parent is off)
            GameObject tmp = new GameObject("Beastkeeper_ExportTmp");
            tmp.SetActive(false);
            GameObject g = Object.Instantiate(prefab, tmp.transform, false);
            g.transform.localPosition = Vector3.zero;
            g.transform.localRotation = Quaternion.identity;
            try
            {
                foreach (MonoBehaviour mb in g.GetComponentsInChildren<MonoBehaviour>(true)) Object.DestroyImmediate(mb);
                Transform root = g.transform;
                List<SkinnedMeshRenderer> sks = Lod0(g);
                StringBuilder sb = new StringBuilder();
                sb.Append("{\"name\":\"").Append(name).Append("\"");
                // body measures the mod uses
                Collider body = g.GetComponent<Collider>();
                if (body == null) body = g.GetComponentInChildren<Collider>(true);
                float rootScale = Mathf.Max(0.01f, root.lossyScale.x);
                sb.Append(",\"bodyWidth\":").Append(F(Mount.BodyWidth(body) / rootScale));
                sb.Append(",\"height\":").Append(F(TopOf(body, root)));
                Transform spine = null;
                foreach (Transform t in g.GetComponentsInChildren<Transform>(true)) if (t.name == "Spine1") { spine = t; break; }
                sb.Append(",\"spine\":\"").Append(spine != null ? spine.name : "").Append("\"");
                sb.Append(",\"parts\":[");
                bool first = true;
                foreach (SkinnedMeshRenderer sk in sks)
                {
                    if (sk.sharedMesh == null) continue;
                    Mesh baked = new Mesh();
                    sk.BakeMesh(baked, true);
                    Matrix4x4 m = root.worldToLocalMatrix * sk.transform.localToWorldMatrix;
                    Vector3[] v = baked.vertices;
                    Vector2[] uv = baked.uv;
                    int[] tri = baked.triangles;
                    if (v.Length == 0 || tri.Length == 0) continue;
                    if (!first) sb.Append(",");
                    first = false;
                    sb.Append("{\"name\":\"").Append(sk.name).Append("\",\"v\":[");
                    for (int i = 0; i < v.Length; i++)
                    {
                        Vector3 p = m.MultiplyPoint3x4(v[i]);
                        if (i > 0) sb.Append(",");
                        sb.Append(F(p.x)).Append(",").Append(F(p.y)).Append(",").Append(F(p.z));
                    }
                    sb.Append("],\"uv\":[");
                    for (int i = 0; i < uv.Length; i++)
                    {
                        if (i > 0) sb.Append(",");
                        sb.Append(F(uv[i].x)).Append(",").Append(F(uv[i].y));
                    }
                    sb.Append("],\"t\":[");
                    for (int i = 0; i < tri.Length; i++) { if (i > 0) sb.Append(","); sb.Append(tri[i]); }
                    sb.Append("],\"tex\":\"").Append(Png(sk.sharedMaterial)).Append("\"}");
                    Object.Destroy(baked);
                }
                sb.Append("]}");
                File.WriteAllText(Path.Combine(dir, name + ".json"), sb.ToString());
                Plugin.Log.LogInfo("[export] " + name + ": " + sks.Count + " meshes, spine " + (spine != null ? "Spine1" : "none"));
                return !first;
            }
            finally { Object.Destroy(tmp); }
        }

        // the most detailed level of the model (or every skinned mesh if it has no levels)
        private static List<SkinnedMeshRenderer> Lod0(GameObject g)
        {
            List<SkinnedMeshRenderer> list = new List<SkinnedMeshRenderer>();
            LODGroup lg = g.GetComponentInChildren<LODGroup>(true);
            if (lg != null)
            {
                LOD[] lods = lg.GetLODs();
                if (lods.Length > 0)
                    foreach (Renderer r in lods[0].renderers) { SkinnedMeshRenderer sk = r as SkinnedMeshRenderer; if (sk != null) list.Add(sk); }
            }
            if (list.Count == 0) list.AddRange(g.GetComponentsInChildren<SkinnedMeshRenderer>(true));
            return list;
        }

        private static float TopOf(Collider col, Transform root)
        {
            if (col == null) return 1f;
            float top = 0f;
            CapsuleCollider cap = col as CapsuleCollider;
            BoxCollider box = col as BoxCollider;
            SphereCollider sph = col as SphereCollider;
            if (cap != null) top = cap.center.y + cap.height / 2f;
            else if (box != null) top = box.center.y + box.size.y / 2f;
            else if (sph != null) top = sph.center.y + sph.radius;
            return root.InverseTransformPoint(col.transform.TransformPoint(new Vector3(0f, top, 0f))).y;
        }

        // the material's main texture as PNG (base64), copied on the GPU since game textures can't be read
        private static string Png(Material mat)
        {
            Texture tex = mat != null ? mat.mainTexture : null;
            if (tex == null) return "";
            const int size = 512;
            RenderTexture rt = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            RenderTexture prev = RenderTexture.active;
            Graphics.Blit(tex, rt);
            RenderTexture.active = rt;
            Texture2D t = new Texture2D(size, size, TextureFormat.RGB24, false);
            t.ReadPixels(new Rect(0, 0, size, size), 0, 0);
            t.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            byte[] png = ImageConversion.EncodeToJPG(t, 85);
            Object.Destroy(t);
            return "data:image/jpeg;base64," + System.Convert.ToBase64String(png);
        }

        private static string F(float x) { return x.ToString("0.####", Inv); }
    }
}
