using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Beastkeeper
{
    // A tamed drake's collision. The game gives every drake two capsules: a body (radius 0.5, 2.3 tall: what bumps
    // into the world) and a much bigger "Attack collider" (radius 1.0, 4 tall: what attacks and other creatures meet).
    // On a tamed drake, 1.8 times the size, that second one is a block 3.6 m wide and 7 m tall around a thin animal.
    //
    // Here the big block goes. Without a saved file it is shrunk to the body. With one (made in the collision editor:
    // BepInEx/config/Beastkeeper_models/drake_collision.json, or the copy inside the mod) the body takes the saved
    // size and the block is replaced by the saved hit shapes, capsules hung on the bones that follow the animation.
    internal static partial class Drake
    {
        private class HitShape { internal string Bone; internal Vector3 A, B; internal float R; }

        private static bool _colRead, _colBody;
        private static float _bodyR, _bodyH, _bodyUp, _bodyFwd;
        private static readonly List<HitShape> _hitShapes = new List<HitShape>();
        private static readonly HashSet<int> _shaped = new HashSet<int>();
        private const string HitName = "Beastkeeper_Hit";

        private static string CollisionPath { get { return Path.Combine(Path.Combine(BepInEx.Paths.ConfigPath, "Beastkeeper_models"), "drake_collision.json"); } }

        private static void ReadCollision()
        {
            if (_colRead) return;
            _colRead = true;
            try
            {
                string text = null, from = "the mod";
                if (File.Exists(CollisionPath)) { text = File.ReadAllText(CollisionPath); from = "the config folder"; }
                else
                    using (Stream s = typeof(Drake).Assembly.GetManifestResourceStream("Beastkeeper.drake_collision.json"))
                        if (s != null) using (StreamReader r = new StreamReader(s)) text = r.ReadToEnd();
                if (string.IsNullOrEmpty(text)) { Plugin.Debug("[drake] no saved collision: the big attack capsule is shrunk to the body"); return; }
                Dictionary<string, object> root = MiniJson.Obj(MiniJson.Parse(text));
                Dictionary<string, object> body = root != null ? MiniJson.Obj(MiniJson.Get(root, "body")) : null;
                if (body != null)
                {
                    _bodyR = MiniJson.Num(MiniJson.Get(body, "radius"), 0.5f);
                    _bodyH = MiniJson.Num(MiniJson.Get(body, "height"), 2.3f);
                    _bodyUp = MiniJson.Num(MiniJson.Get(body, "up"), 1.18f);
                    _bodyFwd = MiniJson.Num(MiniJson.Get(body, "forward"), 0.26f);
                    _colBody = _bodyR > 0.05f && _bodyH > 0.1f;
                }
                List<object> shapes = root != null ? MiniJson.Arr(MiniJson.Get(root, "shapes")) : null;
                if (shapes != null)
                    foreach (object o in shapes)
                    {
                        Dictionary<string, object> sh = MiniJson.Obj(o);
                        if (sh == null) continue;
                        float[] a = MiniJson.Floats(MiniJson.Get(sh, "a")), b = MiniJson.Floats(MiniJson.Get(sh, "b"));
                        string bone = MiniJson.Str(MiniJson.Get(sh, "bone"));
                        float r = MiniJson.Num(MiniJson.Get(sh, "r"), 0f);
                        if (string.IsNullOrEmpty(bone) || a == null || b == null || a.Length < 3 || b.Length < 3 || r <= 0f) continue;
                        _hitShapes.Add(new HitShape { Bone = bone, A = new Vector3(a[0], a[1], a[2]), B = new Vector3(b[0], b[1], b[2]), R = r });
                    }
                Plugin.Debug("[drake] collision from " + from + ": body " + (_colBody ? _bodyR.ToString("0.00") + " x " + _bodyH.ToString("0.00") : "the game's own") + ", " + _hitShapes.Count + " hit shapes");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[drake] couldn't read drake_collision.json: " + e.Message); _colBody = false; _hitShapes.Clear(); }
        }

        private static Transform Deep(Transform t, string name)
        {
            if (t.name == name) return t;
            for (int i = 0; i < t.childCount; i++)
            {
                Transform f = Deep(t.GetChild(i), name);
                if (f != null) return f;
            }
            return null;
        }

        // once for each tamed drake
        internal static void Shape(Character c)
        {
            CapsuleCollider cap = c != null ? c.GetCollider() : null;
            if (cap == null || !_shaped.Add(cap.GetInstanceID())) return;
            if (_shaped.Count > 400) _shaped.Clear();
            ReadCollision();
            if (_colBody)
            {
                cap.radius = _bodyR;
                cap.height = Mathf.Max(_bodyH, _bodyR * 2f);
                cap.center = new Vector3(0f, _bodyUp, _bodyFwd);
            }
            Transform atk = c.transform.Find("Attack collider");
            CapsuleCollider big = atk != null ? atk.GetComponent<CapsuleCollider>() : null;
            int layer = atk != null ? atk.gameObject.layer : c.gameObject.layer;
            int made = 0;
            float rootScale = Mathf.Max(0.0001f, c.transform.lossyScale.x);
            foreach (HitShape h in _hitShapes)
            {
                Transform bone = Deep(c.transform, h.Bone);
                if (bone == null) continue;
                GameObject g = new GameObject(HitName);
                g.layer = layer;
                g.transform.SetParent(bone, false);
                Vector3 along = h.B - h.A;
                g.transform.localPosition = (h.A + h.B) * 0.5f;
                g.transform.localRotation = along.sqrMagnitude > 1e-12f ? Quaternion.FromToRotation(Vector3.up, along.normalized) : Quaternion.identity;
                CapsuleCollider cc = g.AddComponent<CapsuleCollider>();
                float unit = Mathf.Max(0.0001f, bone.lossyScale.x / rootScale);   // the bones are 100 times smaller inside than they look
                cc.direction = 1;
                cc.radius = h.R / unit;
                cc.height = along.magnitude + 2f * cc.radius;
                made++;
            }
            if (big != null)
            {
                if (made > 0) big.enabled = false;        // the hit shapes take its place
                else
                {
                    // no shapes: the block becomes the body, a little wider so blows still land
                    big.direction = 1;
                    big.radius = cap.radius * 1.15f;
                    big.height = cap.height;
                    big.center = Vector3.zero;
                    atk.localPosition = cap.center;
                }
            }
            Plugin.Debug("[drake] " + c.name + " collision: body " + cap.radius.ToString("0.00") + " x " + cap.height.ToString("0.00")
                + (big == null ? ", no attack capsule found" : made > 0 ? ", " + made + " hit shapes instead of the attack capsule" : ", attack capsule shrunk to the body"));
        }
    }
}
