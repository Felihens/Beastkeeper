using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Beastkeeper
{
    // A hatched drake: its element (chosen with the item that woke its egg), its colour, and how it grows.
    // It hatches as a baby and grows to full size over GrowMinutes; only a grown drake takes a saddle.
    internal static partial class Drake
    {
        internal const int Frost = 1, Fire = 2, Poison = 3, Earth = 4, Lightning = 5;
        internal static readonly int ElementKey = "Beastkeeper_element".GetStableHashCode();
        private static readonly int BornKey = "Beastkeeper_drakeBorn".GetStableHashCode();
        internal static readonly int GlowKey = "Beastkeeper_drakeGlow".GetStableHashCode();   // 1 = a rare one with glowing eyes and horns
        private const float BabyShare = 0.4f;   // a newborn's size, compared to a grown tamed drake

        internal static string ElementName(int el)
        {
            switch (el)
            {
                case Fire: return "Fire";
                case Poison: return "Poison";
                case Earth: return "Earth";
                case Lightning: return "Lightning";
                default: return "Frost";
            }
        }

        // the prefab an egg hatches into: the first one in the drake list
        internal static string BabyPrefab
        {
            get
            {
                foreach (string s in (Plugin.DrakeMounts.Value ?? "").Split(','))
                    if (s.Trim().Length > 0) return s.Trim();
                return "Hatchling";
            }
        }

        // its element; drakes not hatched from an egg are frost drakes, like the game's own
        internal static int ElementOf(Character c)
        {
            ZNetView nv = Leash.View(c);
            int el = nv != null && nv.IsValid() ? nv.GetZDO().GetInt(ElementKey, 0) : 0;
            return el >= Frost && el <= Lightning ? el : Frost;
        }

        internal static void MakeBaby(Character baby, ZNetView nv, int el, Vector3 home)
        {
            ZDO z = nv.GetZDO();
            z.Set(ElementKey, el);
            z.Set(BornKey, Breed.Now());
            if (Random.value < Plugin.DrakeGlowChance.Value) z.Set(GlowKey, 1);
            z.Set(Halter.Key, 1);
            baby.SetTamed(true);
            Home.Set(baby, home);
            KeepSize(baby);
            baby.Land();   // it hatches on the ground
        }

        // 0..1: how grown it is (1 for drakes not hatched from an egg)
        internal static float Grown(Character c)
        {
            ZNetView nv = Leash.View(c);
            if (nv == null || !nv.IsValid()) return 1f;
            float born = nv.GetZDO().GetFloat(BornKey, 0f);
            if (born <= 0f) return 1f;
            return Mathf.Clamp01((Breed.Now() - born) / Mathf.Max(60f, Plugin.DrakeGrowMinutes.Value * 60f));
        }

        // minutes left until it's grown (0 when grown)
        internal static int MinutesToGrow(Character c)
        {
            return Mathf.CeilToInt((1f - Grown(c)) * Plugin.DrakeGrowMinutes.Value);
        }

        internal static void Attach(Character c)
        {
            if (!Is(c)) return;
            if (c.GetComponent<DrakeLook>() == null) c.gameObject.AddComponent<DrakeLook>();
            if (c.GetComponent<DrakeGround>() == null) c.gameObject.AddComponent<DrakeGround>();
            if (c.GetComponent<DrakeWalk>() == null) c.gameObject.AddComponent<DrakeWalk>();
        }

        // ------------------------------------------------------------ colours (set in the drake colour editor)

        // the colours of an element's drake; null = the game's own look for that part
        internal class Palette
        {
            internal Color? Scales, Wings, Horns, Eyes;
            internal Color Glow = Color.white;
        }

        internal static int PaletteVersion;
        private static readonly Dictionary<int, Palette> Palettes = new Dictionary<int, Palette>();

        internal static void ColorsChanged() { Palettes.Clear(); PaletteVersion++; }

        // its config line, like "scales:#FF5C1F,wings:#9E3813,horns:#66250C,eyes:original,glow:#FF8040"
        internal static Palette PaletteOf(int el)
        {
            if (el < Frost || el > Lightning) el = Frost;
            Palette p;
            if (Palettes.TryGetValue(el, out p)) return p;
            p = new Palette();
            string line = Plugin.DrakeColors != null && Plugin.DrakeColors[el - 1] != null ? Plugin.DrakeColors[el - 1].Value : "";
            foreach (string item in (line ?? "").Split(','))
            {
                string[] kv = item.Split(':');
                if (kv.Length != 2) continue;
                string part = kv[0].Trim().ToLowerInvariant(), v = kv[1].Trim();
                Color c;
                Color? col = ColorUtility.TryParseHtmlString(v, out c) ? (Color?)c : null;
                switch (part)
                {
                    case "scales": p.Scales = col; break;
                    case "wings": p.Wings = col; break;
                    case "horns": p.Horns = col; break;
                    case "eyes": p.Eyes = col; break;
                    case "glow": if (col.HasValue) p.Glow = col.Value; break;
                }
            }
            Palettes[el] = p;
            return p;
        }
    }

    internal enum DrakePart { Body, Horns, Eyes }

    // A hatched drake's skin, in its element's colours (Palette): the scales (white in the scale mask the user
    // painted), the wings (the rest of the body), the horns and the eyes (model parts of their own). Each colour
    // keeps how light each spot of the game's texture is. The mask's edges are softened, so scales and wings
    // blend into each other instead of meeting at a hard line.
    internal static class DrakeSkin
    {
        private static readonly Dictionary<string, Texture2D> Made = new Dictionary<string, Texture2D>();
        private static float[] _mask;
        private static int _w, _h;
        private static bool _tried, _have;
        private const int SoftRadius = 3, SoftPasses = 3;   // three box blurs of 3 px: about a 9 px soft edge on a 512 px skin

        // the recoloured skin for this part, or null when the part keeps the game's own look
        internal static Texture2D For(int el, Texture source, DrakePart part)
        {
            if (source == null || !Mask()) return null;
            Drake.Palette pal = Drake.PaletteOf(el);
            Color? one = part == DrakePart.Horns ? pal.Horns : part == DrakePart.Eyes ? pal.Eyes : null;
            if (part != DrakePart.Body && !one.HasValue) return null;
            if (part == DrakePart.Body && !pal.Scales.HasValue && !pal.Wings.HasValue) return null;
            string key = source.GetInstanceID() + "_" + el + "_" + part + "_" + Drake.PaletteVersion;
            Texture2D t;
            if (Made.TryGetValue(key, out t) && t != null) return t;
            Color32[] bp = Copy(source);
            Color32[] px = new Color32[bp.Length];
            for (int i = 0; i < px.Length; i++)
            {
                Color32 b = bp[i];
                // keep how light each spot is, give it its colour
                float k = Mathf.Min(1f, (0.3f * b.r + 0.59f * b.g + 0.11f * b.b) / 255f * 1.6f) * 255f;
                Color own = new Color(b.r, b.g, b.b);
                Color o;
                if (part == DrakePart.Body)
                {
                    Color scales = pal.Scales.HasValue ? pal.Scales.Value * k : own;
                    Color wings = pal.Wings.HasValue ? pal.Wings.Value * k : own;
                    o = Color.Lerp(wings, scales, _mask[i]);
                }
                else o = one.Value * k;
                px[i] = new Color32((byte)Mathf.Clamp(o.r, 0f, 255f), (byte)Mathf.Clamp(o.g, 0f, 255f), (byte)Mathf.Clamp(o.b, 0f, 255f), b.a);
            }
            t = new Texture2D(_w, _h, TextureFormat.RGBA32, true);
            t.name = "Beastkeeper_Drake_" + key;
            t.SetPixels32(px);
            t.Apply(true, true);
            Made[key] = t;
            return t;
        }

        // game textures can't be read: copy it on the GPU, at the mask's size
        private static Color32[] Copy(Texture source)
        {
            RenderTexture rt = RenderTexture.GetTemporary(_w, _h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            RenderTexture prev = RenderTexture.active;
            Graphics.Blit(source, rt);
            RenderTexture.active = rt;
            Texture2D copy = new Texture2D(_w, _h, TextureFormat.RGBA32, false);
            copy.ReadPixels(new Rect(0, 0, _w, _h), 0, 0);
            copy.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            Color32[] px = copy.GetPixels32();
            Object.Destroy(copy);
            return px;
        }

        private static bool Mask()
        {
            if (_tried) return _have;
            _tried = true;
            using (Stream s = typeof(DrakeSkin).Assembly.GetManifestResourceStream("Beastkeeper.drake_mask.png"))
            {
                if (s == null) { Plugin.Debug("[drake] no scale mask in the mod yet: drakes are tinted all over"); return false; }
                byte[] data = new byte[s.Length];
                int read = 0;
                while (read < data.Length) { int n = s.Read(data, read, data.Length - read); if (n <= 0) break; read += n; }
                Texture2D t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(t, data)) { Object.Destroy(t); return false; }
                _w = t.width; _h = t.height;
                Color32[] m = t.GetPixels32();
                Object.Destroy(t);
                _mask = new float[m.Length];
                for (int i = 0; i < m.Length; i++) _mask[i] = m[i].r / 255f;
                for (int p = 0; p < SoftPasses; p++) { Blur(_mask, _w, _h, true); Blur(_mask, _w, _h, false); }
            }
            _have = true;
            Plugin.Debug("[drake] scale mask loaded (" + _w + "x" + _h + "), edges softened");
            return true;
        }

        // one box blur along the rows (or the columns): softens the mask's edges
        private static void Blur(float[] m, int w, int h, bool rows)
        {
            int lines = rows ? h : w, len = rows ? w : h;
            float[] line = new float[len];
            for (int l = 0; l < lines; l++)
            {
                for (int i = 0; i < len; i++) line[i] = m[rows ? l * w + i : i * w + l];
                float sum = 0f;
                int n = 0;
                for (int i = -SoftRadius; i <= SoftRadius; i++) if (i >= 0 && i < len) { sum += line[i]; n++; }
                for (int i = 0; i < len; i++)
                {
                    m[rows ? l * w + i : i * w + l] = sum / n;
                    int drop = i - SoftRadius, add = i + SoftRadius + 1;
                    if (drop >= 0) { sum -= line[drop]; n--; }
                    if (add < len) { sum += line[add]; n++; }
                }
            }
        }
    }

    // A tamed drake (hatched from an egg) on its own: the game's drake has no walking animation, so it
    // stays on the ground when it has nowhere to go, and whenever its AI wants to move it takes off, flies there
    // and lands again once it has been still for a moment. It no longer takes off and lands at random.
    // Runs on the game that owns the drake; not while someone rides it (Drake.Drive flies it then).
    internal class DrakeGround : MonoBehaviour
    {
        private Character _c;
        private ZNetView _nv;
        private MonsterAI _ai;
        private float _still, _lastLand, _walking;
        private bool _set;

        private void Awake()
        {
            _c = GetComponent<Character>();
            _nv = GetComponent<ZNetView>();
            _ai = GetComponent<MonsterAI>();
        }

        private void Update()
        {
            if (_c == null || _nv == null || !_nv.IsValid() || !_nv.IsOwner() || _c.IsDead() || !Plugin.Enabled.Value) return;
            if (!_c.IsTamed()) return;   // wild ones fly as in the game
            if (Mount.RiddenCharacter() == _c) { _still = 0f; return; }
            // on a lasso it is led on foot: down on the ground, walking, and it doesn't take off while it is held
            bool led = Leash.Holder(_nv) != 0L;
            if (led && _c.IsFlying() && !_c.InAttack() && Drake.Altitude(_c) < 8f && Time.time - _lastLand > 1f)
            {
                _lastLand = Time.time;
                _c.Land();
                Plugin.Debug("[drake] led on a lasso: landed");
            }
            if (!_set && _ai != null)
            {
                _set = true;
                _ai.m_randomFly = false;       // no taking off and landing at random
                _ai.m_flyAltitudeMin = 1.2f;   // and it flies low
                _ai.m_flyAltitudeMax = 2.5f;
            }
            bool wants = _c.GetMoveDir().sqrMagnitude > 0.04f;
            if (!_c.IsFlying())
            {
                // short ways it walks (DrakeWalk); after WalkSeconds of walking it takes off and flies the rest
                _walking = wants ? _walking + Time.deltaTime : 0f;
                if (wants && !_c.InAttack() && !led && _walking > Plugin.DrakeWalkSeconds.Value) { _c.TakeOff(); _still = 0f; _walking = 0f; }
                return;
            }
            if (led) return;   // held on a lasso: it stays down
            if (wants || _c.InAttack()) { _still = 0f; return; }
            _still += Time.deltaTime;
            if (_still > 1.5f && Drake.Altitude(_c) < 5f && Time.time - _lastLand > 2f)
            {
                _lastLand = Time.time;
                _c.Land();
            }
        }
    }

    // On every drake: its element's colours (seen by every player with the mod), and for the rare ones that
    // hatched glowing (GlowChance), eyes and horns that shine in the glow colour with a soft light breathing slowly.
    internal class DrakeLook : MonoBehaviour
    {
        private Character _c;
        private ZNetView _nv;
        private int _el = -1, _version = -1;
        private float _next;
        private MaterialPropertyBlock _block;
        private bool _glowSet;
        private Light _light;
        private float _pulse;
        private readonly List<Material> _own = new List<Material>();
        private readonly List<Material> _eyeGlow = new List<Material>(), _hornGlow = new List<Material>();

        private void Awake()
        {
            _c = GetComponent<Character>();
            _nv = GetComponent<ZNetView>();
        }

        private void Update()
        {
            if (Time.time < _next || _nv == null || !_nv.IsValid()) return;
            _next = Time.time + 1f;
            int el = Plugin.Enabled.Value ? _nv.GetZDO().GetInt(Drake.ElementKey, 0) : 0;
            bool recolour = _version != Drake.PaletteVersion;
            if (el != _el || recolour) { _el = el; _version = Drake.PaletteVersion; Paint(el); if (_glowSet) SetGlow(el); }
            if (!_glowSet && el >= Drake.Frost && _nv.GetZDO().GetInt(Drake.GlowKey, 0) == 1) { _glowSet = true; Glow(el); }
        }

        private void LateUpdate()
        {
            if (_light == null) return;
            _pulse += Time.deltaTime;
            _light.intensity = 0.7f + 0.4f * Mathf.Sin(_pulse * 1.4f);
        }

        private void OnDestroy()
        {
            foreach (Material m in _own) if (m != null) Destroy(m);
        }

        private static DrakePart PartOf(Renderer r)
        {
            string n = r.name.ToLowerInvariant();
            return n.Contains("eye") ? DrakePart.Eyes : n.Contains("horn") ? DrakePart.Horns : DrakePart.Body;
        }

        private void Glow(int el)
        {
            foreach (Renderer r in GetComponentsInChildren<Renderer>(true))
            {
                if (!(r is SkinnedMeshRenderer) && !(r is MeshRenderer) || Ours(r.transform)) continue;
                DrakePart part = PartOf(r);
                if (part == DrakePart.Body) continue;
                Material[] mats = r.materials;   // its own copies: the glow is switched on in the material itself
                foreach (Material m in mats)
                {
                    _own.Add(m);
                    if (!m.HasProperty("_EmissionColor")) { Plugin.Debug("[drake] shader " + m.shader.name + " has no glow: light only"); continue; }
                    m.EnableKeyword("_EMISSION");
                    // the game's shader glows through an emission map: a white one makes the whole part shine
                    if (m.HasProperty("_EmissionMap")) m.SetTexture("_EmissionMap", Texture2D.whiteTexture);
                    m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                    (part == DrakePart.Eyes ? _eyeGlow : _hornGlow).Add(m);
                }
                r.materials = mats;
            }
            Transform head = null;
            foreach (Transform t in GetComponentsInChildren<Transform>(true))
                if (!Ours(t) && t.name.ToLowerInvariant().Contains("head")) { head = t; break; }
            GameObject lg = new GameObject("Beastkeeper_DrakeGlow");
            lg.transform.SetParent(head != null ? head : transform, false);
            if (head == null) lg.transform.localPosition = Vector3.up * 1f;
            _light = lg.AddComponent<Light>();
            _light.type = LightType.Point;
            _light.range = 4f;
            _light.intensity = 0.8f;
            _light.shadows = LightShadows.None;
            _pulse = Random.Range(0f, 10f);
            SetGlow(el);
            Plugin.Debug("[drake] " + _c.name + " glows (" + _eyeGlow.Count + " eye and " + _hornGlow.Count + " horn materials, light on " + (head != null ? head.name : "its body") + ")");
        }

        private void SetGlow(int el)
        {
            Color glow = Drake.PaletteOf(el).Glow;
            foreach (Material m in _eyeGlow) if (m != null) m.SetColor("_EmissionColor", glow * 2.2f);
            foreach (Material m in _hornGlow) if (m != null) m.SetColor("_EmissionColor", glow * 0.9f);
            if (_light != null) _light.color = glow;
        }

        // something this mod hung on the drake (saddle, bags, seat), not part of its body
        private bool Ours(Transform t)
        {
            for (Transform u = t; u != null && u != transform; u = u.parent)
                if (u.name.StartsWith("Beastkeeper_")) return true;
            return false;
        }

        private void Paint(int el)
        {
            if (_block == null) _block = new MaterialPropertyBlock();
            foreach (Renderer r in GetComponentsInChildren<Renderer>(true))
            {
                if (!(r is SkinnedMeshRenderer) && !(r is MeshRenderer)) continue;
                if (Ours(r.transform)) continue;   // the saddle and bags keep their own look
                r.GetPropertyBlock(_block);
                _block.Clear();
                if (el >= Drake.Frost)   // hatched from an egg (wild drakes keep the game's look)
                {
                    Material m = r.sharedMaterial;
                    Texture2D skin = DrakeSkin.For(el, m != null ? m.mainTexture : null, PartOf(r));
                    if (skin != null) _block.SetTexture("_MainTex", skin);
                }
                r.SetPropertyBlock(_block);
            }
            Plugin.Debug("[drake] " + _c.name + " painted as a " + Drake.ElementName(el) + " drake");
        }
    }
}
