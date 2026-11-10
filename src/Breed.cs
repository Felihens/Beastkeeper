using System.Collections.Generic;
using UnityEngine;

namespace Beastkeeper
{
    // Hare farming. The game's hares don't breed, so this mod does it:
    // two tamed grown hares close together, both fed by hand in the last FedMinutes, now and then make a baby.
    // A baby is born small, in a colour of its own (picked at random, kept for life), and grows up over GrowMinutes.
    // Only grown hares breed, and no more babies come once MaxHares hares live around there.
    // The colour and the size are only seen by players with the mod; for the others it is a plain hare.
    internal static class Breed
    {
        private const float FedMinutes = 10f, PregnantSeconds = 90f, GrowMinutes = 30f, CheckEvery = 10f, Chance = 0.3f;
        private const int MaxHares = 12;
        internal static readonly int ColorKey = "Beastkeeper_hue".GetStableHashCode();
        internal static readonly int SatKey = "Beastkeeper_sat".GetStableHashCode();   // how strong the colour is (0 = white, grey or black)
        internal static readonly int ValKey = "Beastkeeper_val".GetStableHashCode();   // how light the fur is (1 = as light as a wild hare)
        internal static readonly int GlowKey = "Beastkeeper_glow".GetStableHashCode(); // 1 = a rare one whose fur glows
        private const float GlowChance = 0.08f;
        internal static readonly int BornKey = "Beastkeeper_born".GetStableHashCode();
        private static readonly int DueKey = "Beastkeeper_due".GetStableHashCode();

        internal static bool IsHare(Character c) { return c != null && string.Equals(Mount.Kind(c.gameObject), "Hare", System.StringComparison.OrdinalIgnoreCase); }

        internal static void Attach(Character c)
        {
            if (IsHare(c) && c.GetComponent<HareLook>() == null) c.gameObject.AddComponent<HareLook>();
        }

        internal static float Now() { return ZNet.instance != null ? (float)ZNet.instance.GetTimeSeconds() : Time.time; }

        // 0..1: how grown it is (1 for every hare not born on a farm)
        internal static float Grown(ZDO z)
        {
            float born = z.GetFloat(BornKey, 0f);
            if (born <= 0f) return 1f;
            return Mathf.Clamp01((Now() - born) / (GrowMinutes * 60f));
        }

        private static bool Ready(Character c)
        {
            ZNetView nv = Leash.View(c);
            if (c == null || c.IsDead() || !c.IsTamed() || nv == null || !nv.IsValid()) return false;
            return Grown(nv.GetZDO()) >= 1f && Feed.FedWithin(c, FedMinutes * 60f);
        }

        // runs on the game that owns the hare
        internal static void Tick(Character c, ZNetView nv)
        {
            ZDO z = nv.GetZDO();
            float due = z.GetFloat(DueKey, 0f);
            if (due > 0f)
            {
                if (Now() >= due) { z.Set(DueKey, 0f); Birth(c); }
                return;
            }
            if (!Ready(c) || Random.value > Chance) return;
            Character partner = null;
            int around = 0;
            foreach (Character o in Character.GetAllCharacters())
            {
                if (o == null || o == c || !IsHare(o) || o.IsDead()) continue;
                float d = Vector3.Distance(o.transform.position, c.transform.position);
                if (d < 30f) around++;
                if (d < 4f && partner == null && Ready(o)) partner = o;
            }
            if (partner == null || around + 1 >= MaxHares) return;
            z.Set(DueKey, Now() + PregnantSeconds);
            Effect("m_loveEffects", c.transform.position);
            Plugin.Debug("[breed] " + c.name + " and a partner: a baby on the way (" + (around + 1) + " hares around)");
        }

        private static void Birth(Character mother)
        {
            GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab("Hare") : null;
            if (prefab == null) return;
            Vector2 r = Random.insideUnitCircle.normalized * 0.8f;
            Vector3 at = mother.transform.position + new Vector3(r.x, 0.2f, r.y);
            ZNetView mv = Leash.View(mother);
            Vector3 home = mv != null && mv.IsValid() ? mv.GetZDO().GetVec3(Home.HomeKey, Vector3.zero) : Vector3.zero;
            SpawnBaby(at, home != Vector3.zero ? home : at);
        }

        // a tamed baby hare in a colour of its own
        internal static void SpawnBaby(Vector3 at, Vector3 home, bool forceGlow = false)
        {
            GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab("Hare") : null;
            if (prefab == null) return;
            GameObject go = Object.Instantiate(prefab, at, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f));
            Character baby = go.GetComponent<Character>();
            ZNetView nv = go.GetComponent<ZNetView>();
            if (baby == null || nv == null || !nv.IsValid()) return;
            ZDO z = nv.GetZDO();
            // any colour: some come out white, grey or black, the rest any hue from pale to strong, dark to light
            z.Set(ColorKey, Random.value);
            z.Set(SatKey, Random.value < 0.3f ? Random.Range(0f, 0.12f) : Random.Range(0.25f, 1f));
            z.Set(ValKey, Random.Range(0.25f, 1.45f));
            if (forceGlow || Random.value < GlowChance) z.Set(GlowKey, 1);
            z.Set(BornKey, Now());
            z.Set(Halter.Key, 1);
            baby.SetTamed(true);
            Home.Set(baby, home);
            Effect("m_birthEffects", at);
            Plugin.Debug("[breed] a baby hare is born, colour " + z.GetFloat(ColorKey, -1f).ToString("0.00") + (z.GetInt(GlowKey, 0) == 1 ? ", and it glows" : ""));
        }

        // the boar's own love hearts and birth puff
        private static void Effect(string field, Vector3 at)
        {
            GameObject boar = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab("Boar") : null;
            Procreation pr = boar != null ? boar.GetComponent<Procreation>() : null;
            if (pr == null) return;
            EffectList fx = field == "m_loveEffects" ? pr.m_loveEffects : pr.m_birthEffects;
            if (fx != null) fx.Create(at, Quaternion.identity);
        }
    }

    // On every hare: its own colour and its size while it grows, and (on the owner's game) the breeding check.
    internal class HareLook : MonoBehaviour
    {
        private Character _c;
        private ZNetView _nv;
        private Vector3 _baseScale;
        private float _hue = -2f, _sat, _val, _next, _nextBreed;
        private MaterialPropertyBlock _block;
        private bool _glowSet;
        private Light _light;
        private float _pulse;
        private readonly System.Collections.Generic.List<Material> _own = new System.Collections.Generic.List<Material>();

        // the rare glowing ones: the fur shines in its colour and a soft light around it breathes slowly
        private void Glow(float hue, float sat)
        {
            Color glow = sat < 0.15f ? Color.white : Color.HSVToRGB(hue, Mathf.Max(0.5f, sat), 1f);
            foreach (Renderer r in GetComponentsInChildren<Renderer>(true))
            {
                if (!(r is SkinnedMeshRenderer) && !(r is MeshRenderer)) continue;
                Material[] mats = r.materials;   // its own copies: the glow is switched on in the material itself
                foreach (Material m in mats)
                {
                    _own.Add(m);
                    if (!m.HasProperty("_EmissionColor")) { Plugin.Debug("[breed] hare shader " + m.shader.name + " has no glow: light only"); continue; }
                    Texture2D map = HareSkin.GlowFor(glow, m.mainTexture);
                    m.EnableKeyword("_EMISSION");
                    if (map != null && m.HasProperty("_EmissionMap")) { m.SetTexture("_EmissionMap", map); m.SetColor("_EmissionColor", Color.white * 2f); }
                    else m.SetColor("_EmissionColor", glow * 0.6f);
                    m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                }
                r.materials = mats;
            }
            GameObject lg = new GameObject("Beastkeeper_HareGlow");
            lg.transform.SetParent(transform, false);
            lg.transform.localPosition = Vector3.up * 0.4f;
            _light = lg.AddComponent<Light>();
            _light.type = LightType.Point;
            _light.color = glow;
            _light.range = 3f;
            _light.intensity = 0.8f;
            _light.shadows = LightShadows.None;
            _pulse = Random.Range(0f, 10f);
        }

        private void LateUpdate()
        {
            if (_light == null) return;
            _pulse += Time.deltaTime;
            _light.intensity = 0.55f + 0.35f * Mathf.Sin(_pulse * 1.6f);
        }

        private void OnDestroy()
        {
            foreach (Material m in _own) if (m != null) Destroy(m);
        }

        private void Awake()
        {
            _c = GetComponent<Character>();
            _nv = GetComponent<ZNetView>();
            _baseScale = transform.localScale;
            _nextBreed = Time.time + Random.Range(2f, 10f);
        }

        private void Update()
        {
            if (Time.time < _next || _nv == null || !_nv.IsValid() || !Plugin.Enabled.Value) return;
            _next = Time.time + 1f;
            ZDO z = _nv.GetZDO();
            float hue = z.GetFloat(Breed.ColorKey, -1f);
            float sat = z.GetFloat(Breed.SatKey, 0.55f), val = z.GetFloat(Breed.ValKey, 1f);   // older babies: as they were
            if (hue != _hue || sat != _sat || val != _val) { _hue = hue; _sat = sat; _val = val; Paint(hue, sat, val); }
            if (!_glowSet && hue >= 0f && z.GetInt(Breed.GlowKey, 0) == 1) { _glowSet = true; Glow(hue, sat); }
            transform.localScale = _baseScale * Mathf.Lerp(0.5f, 1f, Breed.Grown(z));
            if (_nv.IsOwner() && Time.time >= _nextBreed)
            {
                _nextBreed = Time.time + 10f;
                Breed.Tick(_c, _nv);
            }
        }

        // its own colour on its fur (the fur mask); without the mask, a soft colour over all of it
        private void Paint(float hue, float sat, float val)
        {
            if (hue < 0f) return;
            if (_block == null) _block = new MaterialPropertyBlock();
            foreach (Renderer r in GetComponentsInChildren<Renderer>(true))
            {
                if (!(r is SkinnedMeshRenderer) && !(r is MeshRenderer)) continue;
                Material m = r.sharedMaterial;
                Texture2D skin = HareSkin.For(hue, sat, val, m != null ? m.mainTexture : null);
                r.GetPropertyBlock(_block);
                if (skin != null) _block.SetTexture("_MainTex", skin);
                else _block.SetColor("_Color", Color.HSVToRGB(hue, 0.5f, 1f));
                r.SetPropertyBlock(_block);
            }
        }
    }
}
