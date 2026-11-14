using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Beastkeeper
{
    // Draws the lasso rope from the holder's left hand to the animal's neck, sagging when slack and straight
    // when taut. Lives on every animal; it only shows while the animal's ZDO says someone holds it.
    // The rope uses the game's own ship rope material when it can find it.
    internal class Rope : MonoBehaviour
    {
        internal const int Points = 18;
        private static readonly FieldInfo HeadField = AccessTools.Field(typeof(Character), "m_head");
        private static Material _mat;
        private static bool _gameMat, _looked;

        private ZNetView _nview;
        private Character _char;
        private LineRenderer _line;
        private Transform _neck;
        private long _holderId;
        private Player _holder;
        private float _nextFind;

        // ------------------------------------------------------------ material

        internal static Material Material()
        {
            if (_mat != null && (_gameMat || _looked)) return _mat;
            if (!_looked && ZNetScene.instance != null)
            {
                _looked = true;
                foreach (string ship in new[] { "Karve", "VikingShip", "Raft" })
                {
                    GameObject p = ZNetScene.instance.GetPrefab(ship);
                    if (p == null) continue;
                    foreach (LineRenderer lr in p.GetComponentsInChildren<LineRenderer>(true))
                        if (lr.sharedMaterial != null)
                        {
                            _mat = lr.sharedMaterial;
                            _gameMat = true;
                            Plugin.Debug("[rope] using the rope material '" + _mat.name + "' (shader " + _mat.shader.name + ") from " + ship + "/" + lr.name);
                            return _mat;
                        }
                }
                Plugin.Debug("[rope] no game rope material found, using a plain one");
            }
            if (_mat == null) _mat = new Material(Shader.Find("Sprites/Default"));
            return _mat;
        }

        internal static void Style(LineRenderer line)
        {
            line.sharedMaterial = Material();
            line.textureMode = LineTextureMode.Tile;
            line.numCapVertices = 2;
            line.startWidth = line.endWidth = Plugin.RopeWidth.Value;
            Color c = _gameMat ? Color.white : new Color(0.42f, 0.31f, 0.19f);
            line.startColor = line.endColor = c;
        }

        internal static LineRenderer NewLine(Transform parent, string name)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            LineRenderer line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.positionCount = Points;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            line.receiveShadows = true;
            Style(line);
            return line;
        }

        internal static void Lay(LineRenderer line, Vector3 a, Vector3 b, float slack)
        {
            float sag = Mathf.Clamp(slack * 0.35f, 0.02f, 0.9f);
            for (int i = 0; i < Points; i++)
            {
                float t = i / (float)(Points - 1);
                line.SetPosition(i, Vector3.Lerp(a, b, t) + Vector3.down * (sag * 4f * t * (1f - t)));
            }
        }

        internal static Vector3 RightHand(Player p)
        {
            VisEquipment ve = p.GetComponent<VisEquipment>();
            if (ve != null && ve.m_rightHand != null) return ve.m_rightHand.position;
            return p.transform.position + Vector3.up * 1.2f + p.transform.right * 0.3f;
        }

        internal static Vector3 Hand(Player p)
        {
            VisEquipment ve = p.GetComponent<VisEquipment>();
            if (ve != null && ve.m_leftHand != null) return ve.m_leftHand.position;
            return p.transform.position + Vector3.up * 1.1f;
        }

        // A point between the head and the bone it hangs from: roughly where a collar sits.
        internal static Vector3 Neck(Character c, ref Transform cache)
        {
            if (cache == null && HeadField != null)
            {
                cache = HeadField.GetValue(c) as Transform;
                if (cache != null) Plugin.Debug("[rope] " + c.name + " neck at bone " + cache.name + (cache.parent != null ? " / " + cache.parent.name : ""));
            }
            if (cache == null) return c.transform.position + Vector3.up * 0.8f;
            if (cache.parent != null) return Vector3.Lerp(cache.position, cache.parent.position, 0.45f);
            return cache.position;
        }

        // ------------------------------------------------------------ the rope on an animal

        private void Awake()
        {
            _nview = GetComponent<ZNetView>();
            _char = GetComponent<Character>();
        }

        private float _idleUntil;   // not held and not tied: look again in a moment, not every frame

        private void LateUpdate()
        {
            if (Time.time < _idleUntil) return;
            long id = Plugin.Enabled.Value ? Leash.Holder(_nview) : 0L;
            if (id != _holderId) { _holderId = id; _holder = null; _nextFind = 0f; }
            if (id != 0L && _holder == null && Time.time >= _nextFind)
            {
                _holder = Leash.FindPlayer(id);
                _nextFind = Time.time + 1f;
            }
            if (id == 0L && _char != null && !_char.IsDead() && Plugin.Enabled.Value && Tie.IsTied(_char) && !Tie.Loose(_char))
            {
                // tied up: the rope runs from the post to its neck
                Vector3 post = Tie.Anchor(_char), neck = Neck(_char, ref _neck);
                float d = Vector3.Distance(post, neck);
                if (d > 30f) { if (_line != null) _line.enabled = false; return; }
                if (_line == null) _line = NewLine(transform, "Beastkeeper_Rope");
                _line.enabled = true;
                _line.startWidth = _line.endWidth = Plugin.RopeWidth.Value;
                Lay(_line, post, neck, Tie.Length + 0.5f - d);
                return;
            }
            if (id == 0L || _holder == null || _char == null || _char.IsDead())
            {
                if (_line != null && _line.enabled) _line.enabled = false;
                if (id == 0L) _idleUntil = Time.time + 0.25f;
                return;
            }

            Vector3 a = Hand(_holder);
            Vector3 b = Neck(_char, ref _neck);
            float dist = Vector3.Distance(a, b);
            if (dist > 60f) { if (_line != null) _line.enabled = false; return; }

            if (_line == null) _line = NewLine(transform, "Beastkeeper_Rope");
            _line.enabled = true;
            _line.startWidth = _line.endWidth = Plugin.RopeWidth.Value;
            Lay(_line, a, b, Plugin.RopeLength.Value + 0.8f - dist);
        }

        private void OnDestroy()
        {
            if (_line != null) Destroy(_line.gameObject);
        }
    }

    // The coil of rope shown in the hand and on the ground: a few loops drawn with the rope material.
    internal class Coil : MonoBehaviour
    {
        public Color Tint = Color.white;   // public: copied when the game clones the item
        private LineRenderer _line;

        private void Start()
        {
            _line = gameObject.AddComponent<LineRenderer>();
            _line.useWorldSpace = false;
            _line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            const int loops = 4, per = 20;
            _line.positionCount = loops * per + 1;
            for (int i = 0; i <= loops * per; i++)
            {
                float a = i / (float)per * Mathf.PI * 2f;
                float r = 0.13f + 0.008f * Mathf.Sin(i * 0.7f);
                float drift = i / (float)(loops * per) * 0.05f;
                _line.SetPosition(i, new Vector3(Mathf.Cos(a) * r + drift, -0.02f + drift * 0.4f, Mathf.Sin(a) * r * 1.15f));
            }
            Rope.Style(_line);
            _line.startWidth = _line.endWidth = 0.028f;
            _line.startColor = new Color(_line.startColor.r * Tint.r, _line.startColor.g * Tint.g, _line.startColor.b * Tint.b, 1f);
            _line.endColor = _line.startColor;
        }
    }
}
