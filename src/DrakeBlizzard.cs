using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Beastkeeper
{
    // Ridden frost drake: an ice ball that hits an enemy wraps it in a small blizzard for Seconds. The blizzard
    // follows that enemy, and it and every enemy within Radius of it move at Slow of their speed. The frost attack
    // keeps its usual wait (AttackCooldown); the blizzard has its own: after one starts, the next ice balls only
    // shatter until Seconds have gone by. If the enemy dies the blizzard ends early and the next hit makes a new one
    // at once. A drake fighting on its own makes no blizzard.
    //
    // Frost beam (2026-11-16): the blizzard starts small and the ice builds up while the beam keeps hitting enemies
    // (Feed): it grows to its full size and slow after a whole second of beam. It keeps what it has built up until
    // it ends.
    //
    // Everybody sees it: the rider's game sends its start, how much ice it has and its end to every player, and each
    // game with the mod makes the same blizzard on the same enemy. The slow works wherever that enemy is run (the game
    // of whoever owns it), so it slows for real even when the rider doesn't own the enemy.
    //
    // The look is the game's own poison cloud (the green mist round a poisoned creature), made bigger and coloured
    // icy white and blue: nothing drawn from scratch.
    internal class Blizzard : MonoBehaviour
    {
        internal const float Seconds = 6f, Radius = 5f, Slow = 0.5f;
        // The look's size: the poison cloud scaled 3x looked smaller than a 3 m blizzard, so it is scaled 2x the
        // radius. Its puffs grow with it, so they are shrunk back to their size at 6x; and as many more particles as
        // the cloud is wider than at 3x (by area), so it stays as thick.
        private const float LookScale = Radius * 2f, PuffSize = 6f / LookScale, MoreParticles = (LookScale / 3f) * (LookScale / 3f);
        private static readonly Color Icy = new Color(0.85f, 0.93f, 1f, 1f);
        private static readonly Dictionary<Character, float> _slowed = new Dictionary<Character, float>();
        private static readonly Dictionary<Character, float> _slowBy = new Dictionary<Character, float>();
        private static Blizzard _current;
        private static float _nextBlizzard;   // the blizzard's own wait
        private static GameObject _lookPrefab;
        private static bool _lookTried, _shapesLogged;

        internal Character Owner, Foe;
        internal float Until;
        private bool _theirs;                   // another rider's blizzard, shown here
        private ZDOID _drakeId;
        private float _nextSend;
        private const string Rpc = "Beastkeeper_Blizzard";
        private static ZRoutedRpc _rpcOn;
        private static readonly Dictionary<ZDOID, Blizzard> _remote = new Dictionary<ZDOID, Blizzard>();
        private float _ice = StartIce;          // 0..1, how much ice has built up
        private GameObject _look;
        private const float StartIce = 0.15f;
        private float _nextTick;
        private bool _ending;
        private readonly List<ParticleSystem> _ps = new List<ParticleSystem>();

        internal static void Begin(Character drake, Character foe)
        {
            if (drake == null || foe == null || foe.IsDead() || foe == drake || Mount.RiddenCharacter() != drake) return;
            if (Time.time < _nextBlizzard) return;   // still waiting: this ice ball only shatters
            if (_current != null) _current.End(false);
            _nextBlizzard = Time.time + Seconds;
            Blizzard b = Make(drake, foe, Seconds, StartIce);
            _current = b;
            b.Send(0, b._ice);
            Plugin.Debug("[blizzard] on " + foe.name + " for " + Seconds.ToString("0") + " s (" + b._ps.Count + " particle systems)");
        }

        private static Blizzard Make(Character drake, Character foe, float seconds, float ice)
        {
            GameObject g = new GameObject("Beastkeeper_Blizzard");
            g.transform.position = foe.transform.position;
            Blizzard b = g.AddComponent<Blizzard>();
            b.Owner = drake;
            b.Foe = foe;
            b.Until = Time.time + seconds;
            b._ice = ice;
            b._drakeId = drake.GetZDOID();
            b.Dress();
            return b;
        }

        // ---- the messages: what = 0 start (foe, seconds, ice), 1 ice, 2 over
        private void Send(int what, float ice)
        {
            if (_theirs || ZRoutedRpc.instance == null || _drakeId.IsNone()) return;
            ZPackage p = new ZPackage();
            p.Write(_drakeId);
            p.Write(what);
            p.Write(Foe != null ? Foe.GetZDOID() : ZDOID.None);
            p.Write(Mathf.Max(0f, Until - Time.time));
            p.Write(ice);
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, Rpc, new object[] { p });
        }

        internal static void Register()
        {
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (rpc == null || rpc == _rpcOn) return;
            rpc.Register<ZPackage>(Rpc, new Action<long, ZPackage>(OnMessage));
            _rpcOn = rpc;
        }

        private static void OnMessage(long sender, ZPackage p)
        {
            try
            {
                ZDOID drakeId = p.ReadZDOID();
                int what = p.ReadInt();
                ZDOID foeId = p.ReadZDOID();
                float seconds = p.ReadSingle();
                float ice = p.ReadSingle();
                if (sender == ZDOMan.GetSessionID()) return;   // our own
                Blizzard b;
                _remote.TryGetValue(drakeId, out b);
                if (b == null || b._ending) { _remote.Remove(drakeId); b = null; }
                if (what == 2) { if (b != null) b.End(false); return; }
                if (what == 0 || b == null)
                {
                    if (b != null) b.End(false);
                    if (ZNetScene.instance == null) return;
                    GameObject dg = ZNetScene.instance.FindInstance(drakeId), fg = ZNetScene.instance.FindInstance(foeId);
                    Character drake = dg != null ? dg.GetComponent<Character>() : null, foe = fg != null ? fg.GetComponent<Character>() : null;
                    if (drake == null || foe == null || foe.IsDead()) return;   // too far away to matter here
                    b = Make(drake, foe, seconds, ice);
                    b._theirs = true;
                    _remote[drakeId] = b;
                    return;
                }
                b.SetIce(ice);
            }
            catch (Exception e) { Plugin.Debug("[blizzard] bad message: " + e.Message); }
        }

        private void SetIce(float ice)
        {
            _ice = Mathf.Clamp01(ice);
            if (_look != null) _look.transform.localScale = Vector3.one * LookScale * Built(_ice);
        }

        // the game's poison look, found on its Poison status effect
        private static GameObject LookPrefab()
        {
            if (_lookTried) return _lookPrefab;
            if (ObjectDB.instance == null) return null;
            _lookTried = true;
            StatusEffect poison = ObjectDB.instance.GetStatusEffect("Poison".GetStableHashCode());
            List<string> names = new List<string>();
            if (poison != null && poison.m_startEffects != null && poison.m_startEffects.m_effectPrefabs != null)
                foreach (EffectList.EffectData e in poison.m_startEffects.m_effectPrefabs)
                {
                    if (e == null || e.m_prefab == null) continue;
                    names.Add(e.m_prefab.name);
                    if (_lookPrefab == null && e.m_prefab.GetComponentInChildren<ParticleSystem>(true) != null) _lookPrefab = e.m_prefab;
                }
            Plugin.Debug("[blizzard] poison effects: " + (names.Count > 0 ? string.Join(", ", names.ToArray()) : "none") + " -> look " + (_lookPrefab != null ? _lookPrefab.name : "none"));
            return _lookPrefab;
        }

        private void Dress()
        {
            GameObject prefab = LookPrefab();
            if (prefab == null) return;
            List<string> shapes = new List<string>();
            GameObject look = Instantiate(prefab, transform);
            look.transform.localPosition = Vector3.zero;
            look.transform.localRotation = Quaternion.identity;
            // only the picture: no sound loops, no timed self-destruction, nothing networked
            foreach (ZNetView nv in look.GetComponentsInChildren<ZNetView>(true)) Destroy(nv);
            foreach (TimedDestruction td in look.GetComponentsInChildren<TimedDestruction>(true)) Destroy(td);
            foreach (ParticleSystem ps in look.GetComponentsInChildren<ParticleSystem>(true))
            {
                ParticleSystem.MainModule main = ps.main;
                main.scalingMode = ParticleSystemScalingMode.Hierarchy;
                main.loop = true;
                main.startColor = Recolour(main.startColor);
                main.maxParticles = Mathf.RoundToInt(main.maxParticles * MoreParticles);
                main.startSizeMultiplier *= PuffSize;
                ParticleSystem.EmissionModule em = ps.emission;
                em.rateOverTimeMultiplier *= MoreParticles;
                em.rateOverDistanceMultiplier *= MoreParticles;
                ParticleSystem.ShapeModule sh = ps.shape;
                shapes.Add(ps.name + " " + (sh.enabled ? sh.shapeType + " r" + sh.radius.ToString("0.00") : "no shape")
                    + " size " + main.startSizeMultiplier.ToString("0.00") + " rate " + em.rateOverTimeMultiplier.ToString("0"));
                ParticleSystem.ColorOverLifetimeModule col = ps.colorOverLifetime;
                if (col.enabled) col.color = Recolour(col.color);
                ParticleSystemRenderer r = ps.GetComponent<ParticleSystemRenderer>();
                if (r != null && r.material != null && r.material.HasProperty("_Color"))
                {
                    Color c = r.material.color;
                    r.material.color = new Color(Icy.r, Icy.g, Icy.b, c.a);
                }
                ps.Play(true);
                _ps.Add(ps);
            }
            // as big as the blizzard: the poison cloud is about a creature wide
            _look = look;
            look.transform.localScale = Vector3.one * LookScale * Built(_ice);
            look.SetActive(true);
            if (!_shapesLogged)
            {
                _shapesLogged = true;
                Plugin.Debug("[blizzard] look x" + LookScale.ToString("0") + ": " + string.Join("; ", shapes.ToArray()));
            }
        }

        private static ParticleSystem.MinMaxGradient Recolour(ParticleSystem.MinMaxGradient g)
        {
            switch (g.mode)
            {
                case ParticleSystemGradientMode.Color: return new ParticleSystem.MinMaxGradient(Tint(g.color));
                case ParticleSystemGradientMode.TwoColors: return new ParticleSystem.MinMaxGradient(Tint(g.colorMin), Tint(g.colorMax));
                case ParticleSystemGradientMode.Gradient: return new ParticleSystem.MinMaxGradient(Tint(g.gradient));
                case ParticleSystemGradientMode.TwoGradients: return new ParticleSystem.MinMaxGradient(Tint(g.gradientMin), Tint(g.gradientMax));
                default: return new ParticleSystem.MinMaxGradient(Tint(g.color));
            }
        }

        // icy, keeping how bright and how see-through each colour was
        private static Color Tint(Color c)
        {
            float v = Mathf.Max(0.55f, c.maxColorComponent);
            return new Color(Icy.r * v, Icy.g * v, Icy.b * v, c.a);
        }

        private static Gradient Tint(Gradient g)
        {
            if (g == null) return null;
            GradientColorKey[] keys = g.colorKeys;
            for (int i = 0; i < keys.Length; i++) keys[i].color = Tint(keys[i].color);
            Gradient n = new Gradient();
            n.SetKeys(keys, g.alphaKeys);
            return n;
        }

        // the size (and the share of the slow) for this much ice: a third at the start, all of it when full
        private static float Built(float ice) { return 0.35f + 0.65f * Mathf.Clamp01(ice); }

        // the beam adds ice to the blizzard in progress
        internal static void Feed(float amount)
        {
            Blizzard b = _current;
            if (b == null || b._ending || amount <= 0f) return;
            float was = b._ice;
            b.SetIce(b._ice + amount);
            if (Time.time >= b._nextSend || b._ice >= 1f && was < 1f) { b._nextSend = Time.time + 0.1f; b.Send(1, b._ice); }
            if (was < 1f && b._ice >= 1f) Plugin.Debug("[blizzard] full of ice");
        }

        private void Update()
        {
            if (_ending) return;
            if (Foe == null || Foe.IsDead()) { End(true); return; }
            if (Time.time >= Until) { End(false); return; }
            transform.position = Foe.transform.position;
            if (Time.time < _nextTick) return;
            _nextTick = Time.time + 0.25f;
            Vector3 at = Foe.transform.position;
            foreach (Character o in Character.GetAllCharacters())
            {
                if (o == null || o.IsDead() || o == Owner || o.IsPlayer()) continue;
                if (o != Foe && (Owner == null || !BaseAI.IsEnemy(Owner, o))) continue;
                Vector3 d = o.transform.position - at;
                d.y = 0f;
                if (d.magnitude > Radius * Built(_ice) + o.GetRadius()) continue;
                _slowed[o] = Time.time + 0.4f;
                _slowBy[o] = 1f - (1f - Slow) * Built(_ice);
            }
            if (_slowed.Count > 100) { _slowed.Clear(); _slowBy.Clear(); }
        }

        private void End(bool died)
        {
            if (_ending) return;
            _ending = true;
            if (_current == this) { _current = null; Send(2, _ice); }
            if (_theirs) { Blizzard r; if (_remote.TryGetValue(_drakeId, out r) && r == this) _remote.Remove(_drakeId); }
            if (died && !_theirs) _nextBlizzard = 0f;
            Plugin.Debug("[blizzard] over" + (died ? ": the enemy died, the next hit makes a new blizzard" : ""));
            foreach (ParticleSystem ps in _ps) if (ps != null) ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            Destroy(gameObject, 2.5f);
        }

        // how long until a hit can make a new blizzard (0: ready)
        internal static float WaitLeft()
        {
            return Mathf.Max(0f, _nextBlizzard - Time.time);
        }

        internal static float SpeedFactor(Character c)
        {
            float t;
            if (c == null || !_slowed.TryGetValue(c, out t) || Time.time >= t) return 1f;
            float by;
            return _slowBy.TryGetValue(c, out by) ? by : Slow;
        }
    }

    // the slow inside a blizzard, where the creature moves (the game that owns it)
    [HarmonyPatch]
    internal static class BlizzardSlow
    {
        private static readonly FieldInfo SemanChar = AccessTools.Field(typeof(SEMan), "m_character");

        [HarmonyPatch(typeof(SEMan), "ApplyStatusEffectSpeedMods"), HarmonyPostfix]
        private static void Slowed(SEMan __instance, ref float speed)
        {
            if (!Plugin.Enabled.Value || SemanChar == null) return;
            Character c = SemanChar.GetValue(__instance) as Character;
            float f = Blizzard.SpeedFactor(c);
            if (f < 1f) speed *= f;
        }
    }
}
