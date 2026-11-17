using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Beastkeeper
{
    // Ridden frost drake: a beam of ice instead of the ice ball (user, 2026-11-16).
    //  - It lasts BeamSeconds and always goes to the exact point under the crosshair, every frame.
    //  - While it lasts the aim is heavy: the mouse turns the view at AimWeight of its speed, and the view shakes a little.
    //  - It hits the first thing on its line. Damage comes in ticks and grows the longer the beam stays on the same
    //    enemy (the ice builds up), so a beam held on one enemy for the whole second does the most; one attack never
    //    does more than the usual cap (twice AttackDamage, frost a little more: FrostMult).
    //  - The first enemy it touches gets the blizzard (same blizzard and same own wait as before); while the beam
    //    keeps hitting enemies, the blizzard grows from small to full size and slows more.
    // Everybody sees it: the rider's game sends the start, where the beam lands (BeamSendHz times a second) and the
    // end to every player (a message, like the lasso's), and each game with the mod draws it from the drake's head.
    // Only the rider's game deals the damage. The look is made only from the game's own pieces: the ice spit's glow
    // material on two lines (a wide icy glow and a thin white core), the ice spit's hit effect where the beam lands,
    // and two lights.
    internal static partial class Drake
    {
        internal const float BeamSeconds = 1f, AimWeight = 0.3f, BeamReach = 70f, FrostMult = 1.1f;
        private const float BeamTick = 0.1f, BeamRadius = 0.35f, BeamWidth = 0.55f, BeamSendHz = 15f, BeamShake = 0.35f;
        private const string BeamRpc = "Beastkeeper_FrostBeam";

        // one beam on screen (yours, or another rider's)
        private class BeamView
        {
            internal Character Drake;
            internal float Start, Until, NextSplash;
            internal Vector3 End, Target;
            internal bool HasEnd;
            internal GameObject Root;
            internal LineRenderer Glow, Core;
            internal Light Mouth, Hit;
            internal List<GameObject> Sounds = new List<GameObject>();
        }

        // the sound, all from the game (names found in its asset list, 2026-11-16): Moder's cold breath starts it,
        // and while it lasts her breath's rush plus Yagluth's beam loop, quieter
        private static readonly string[] BeamStartSounds = { "sfx_dragon_coldbreath_start" };
        private static readonly string[] BeamLoopSounds = { "sfx_dragon_coldbreath_trailon", "sfx_goblinking_beam" };
        private static readonly float[] BeamLoopVolume = { 1f, 0.5f };
        private static readonly Dictionary<string, GameObject> _sfx = new Dictionary<string, GameObject>();

        private static BeamView _mine;
        private static readonly Dictionary<ZDOID, BeamView> _theirs = new Dictionary<ZDOID, BeamView>();
        private static Character _beamOwner;
        private static Player _beamRider;
        private static float _beamUntil, _beamNextTick, _beamNextSend;
        private static Character _beamFoe;          // the enemy the beam is on now
        private static float _beamOnFoe;            // how long it has been on that one
        private static int _beamMask;
        private static Material _glowMat, _coreMat;
        private static bool _effectsLogged;
        private static ZRoutedRpc _beamRpcOn;

        internal static bool BeamActive { get { return _beamOwner != null && Time.time < _beamUntil; } }

        private static bool UsesBeam(int el) { return el == Frost; }

        // ------------------------------------------------------------ your beam

        private static void BeamStart(Character c, Player rider)
        {
            _beamOwner = c;
            _beamRider = rider;
            _beamUntil = Time.time + BeamSeconds;
            _beamNextTick = Time.time;
            _beamNextSend = 0f;
            _beamFoe = null;
            _beamOnFoe = 0f;
            if (_mine == null) _mine = NewView("mine");
            Open(_mine, c, BeamSeconds);
            Send(c, 0, Vector3.zero, BeamSeconds);
            Plugin.Debug("[beam] on for " + BeamSeconds.ToString("0.0") + " s");
        }

        private static void BeamStop()
        {
            if (_beamOwner != null)
            {
                Plugin.Debug("[beam] off");
                Send(_beamOwner, 2, Vector3.zero, 0f);
            }
            _beamOwner = null;
            _beamFoe = null;
            if (_mine != null) Close(_mine);
        }

        // every frame, after the camera has moved (so the beam lands where the crosshair is now)
        [HarmonyPatch(typeof(GameCamera), "LateUpdate"), HarmonyPostfix, HarmonyPriority(Priority.VeryLow)]
        private static void BeamFrame()
        {
            DrawTheirs();
            if (_beamOwner == null) return;
            Character c = _beamOwner;
            if (c == null || c.IsDead() || Mount.RiddenCharacter() != c || Time.time >= _beamUntil) { BeamStop(); return; }

            Vector3 aim = AimPoint(c, _beamRider);
            Vector3 mouth, dir;
            MouthOf(c, aim, out mouth, out dir);
            Character struck;
            Vector3 end = BeamHit(c, mouth, dir, out struck);
            _mine.End = end; _mine.HasEnd = true;
            Draw(_mine, mouth, end, dir);
            if (Time.time >= _beamNextSend) { _beamNextSend = Time.time + 1f / BeamSendHz; Send(c, 1, end, _beamUntil - Time.time); }
            // a light, steady shake of the view while it lasts (the game's own shake, with the mod's ShakeStrength)
            if (GameCamera.instance != null && _beamRider != null && Plugin.ShakeStrength.Value > 0f)
                GameCamera.instance.AddShake(_beamRider.transform.position, 10f, BeamShake * Plugin.ShakeStrength.Value, true);

            // the enemy under the beam: the ice builds up on it
            Character foe = struck != null && struck != c && struck != _beamRider && !struck.IsDead() && !struck.IsPlayer() && BaseAI.IsEnemy(c, struck) ? struck : null;
            if (foe != _beamFoe) { _beamFoe = foe; _beamOnFoe = 0f; }
            if (foe != null)
            {
                _beamOnFoe += Time.deltaTime;
                Blizzard.Begin(c, foe);
                Blizzard.Feed(Time.deltaTime / BeamSeconds);
            }
            if (Time.time < _beamNextTick) return;
            _beamNextTick = Time.time + BeamTick;
            if (foe == null) return;
            BeamDamage(c, foe, end, dir, _beamOnFoe);
        }

        private static void MouthOf(Character c, Vector3 aim, out Vector3 mouth, out Vector3 dir)
        {
            float scale = Mathf.Max(1f, c.transform.localScale.x);
            Vector3 head = c.GetHeadPoint();
            dir = (aim - head).normalized;
            if (dir.sqrMagnitude < 0.5f) dir = c.transform.forward;
            mouth = head + dir * 1.2f * scale;
        }

        // the first thing on the beam's line (not the drake, not its rider), up to BeamReach
        private static Vector3 BeamHit(Character c, Vector3 from, Vector3 dir, out Character struck)
        {
            struck = null;
            if (_beamMask == 0) _beamMask = LayerMask.GetMask("Default", "static_solid", "terrain", "piece", "character", "character_net", "character_ghost", "character_noenv", "Water");
            RaycastHit[] hits = Physics.SphereCastAll(from, BeamRadius, dir, BeamReach, _beamMask, QueryTriggerInteraction.Ignore);
            float best = BeamReach;
            Vector3 at = from + dir * BeamReach;
            foreach (RaycastHit h in hits)
            {
                if (h.collider == null) continue;
                Character hc = h.collider.GetComponentInParent<Character>();
                if (hc == c || (hc != null && hc == _beamRider)) continue;
                if (h.distance <= 0f) continue;
                if (h.distance < best)
                {
                    best = h.distance;
                    at = h.point != Vector3.zero ? h.point : from + dir * h.distance;
                    struck = hc;
                }
            }
            return at;
        }

        // one tick: the ticks of a whole second on one enemy add up to the attack's full damage, but the first ones
        // are weaker and the last ones stronger (the ice builds up); jumping to another enemy starts over
        private static void BeamDamage(Character c, Character foe, Vector3 at, Vector3 dir, float onFoe)
        {
            float ticks = BeamSeconds / BeamTick;
            float k = Mathf.Clamp01(onFoe / BeamSeconds);
            float build = 0.4f + 1.2f * k;   // 0.4x at the start, 1.6x after a second on it: 1x on average
            float amount = Plugin.DrakeAttackDamage.Value * 2f * FrostMult * DamageOf(c) / ticks * build;
            HitData hd = new HitData();
            hd.m_damage = new HitData.DamageTypes();
            hd.m_damage.m_frost = amount;
            Shot s = ShotOf(Frost);
            hd.m_pushForce = 0f;
            hd.m_toolTier = (short)(s != null && s.Item != null ? s.Item.m_shared.m_toolTier : 0);
            hd.m_dodgeable = false;
            hd.m_ranged = true;   // its own damage setting, not the tamed-animal multiplier
            hd.m_blockable = true;
            hd.m_point = at;
            hd.m_dir = dir;
            hd.SetAttacker(c);
            foe.Damage(hd);
        }

        // ------------------------------------------------------------ everybody sees it

        // what = 0 start (seconds = how long), 1 where it lands now, 2 over
        private static void Send(Character c, int what, Vector3 end, float seconds)
        {
            if (ZRoutedRpc.instance == null || c == null) return;
            ZDOID id = c.GetZDOID();
            if (id.IsNone()) return;
            ZPackage p = new ZPackage();
            p.Write(id);
            p.Write(what);
            p.Write(end);
            p.Write(seconds);
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, BeamRpc, new object[] { p });
        }

        internal static void RegisterBeam()
        {
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (rpc == null || rpc == _beamRpcOn) return;
            rpc.Register<ZPackage>(BeamRpc, new Action<long, ZPackage>(OnBeam));
            _beamRpcOn = rpc;
        }

        private static void OnBeam(long sender, ZPackage p)
        {
            try
            {
                ZDOID id = p.ReadZDOID();
                int what = p.ReadInt();
                Vector3 end = p.ReadVector3();
                float seconds = p.ReadSingle();
                if (sender == ZDOMan.GetSessionID()) return;   // our own: already on screen
                BeamView v;
                _theirs.TryGetValue(id, out v);
                if (what == 2) { if (v != null) Close(v); return; }
                if (v == null || v.Root == null)
                {
                    GameObject g = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(id) : null;
                    Character c = g != null ? g.GetComponent<Character>() : null;
                    if (c == null) return;   // too far away to be loaded: nothing to draw
                    v = NewView("theirs");
                    v.Drake = c;
                    _theirs[id] = v;
                }
                if (what == 0) { Open(v, v.Drake, seconds); return; }
                if (v.Until < Time.time) Open(v, v.Drake, seconds);   // the start message got lost
                v.Until = Time.time + Mathf.Max(0.15f, seconds);
                v.Target = end;
                if (!v.HasEnd) { v.End = end; v.HasEnd = true; }
            }
            catch (Exception e) { Plugin.Debug("[beam] bad message: " + e.Message); }
        }

        // another rider's beam: from their drake's head to where it lands, gliding between the messages
        private static void DrawTheirs()
        {
            if (_theirs.Count == 0) return;
            List<ZDOID> gone = null;
            foreach (KeyValuePair<ZDOID, BeamView> kv in _theirs)
            {
                BeamView v = kv.Value;
                if (v.Drake == null || v.Root == null) { if (gone == null) gone = new List<ZDOID>(); gone.Add(kv.Key); if (v.Root != null) UnityEngine.Object.Destroy(v.Root); continue; }
                if (!v.Root.activeSelf) continue;
                if (Time.time > v.Until || v.Drake.IsDead()) { Close(v); continue; }
                if (!v.HasEnd) continue;
                v.End = Vector3.Lerp(v.End, v.Target, 1f - Mathf.Exp(-20f * Time.deltaTime));
                Vector3 mouth, dir;
                MouthOf(v.Drake, v.End, out mouth, out dir);
                Draw(v, mouth, v.End, dir);
            }
            if (gone != null) foreach (ZDOID id in gone) _theirs.Remove(id);
        }

        // ------------------------------------------------------------ the look

        private static void Open(BeamView v, Character c, float seconds)
        {
            v.Drake = c;
            v.Start = Time.time;
            v.Until = Time.time + seconds;
            v.HasEnd = false;
            v.NextSplash = 0f;
            v.Root.SetActive(true);
            StopSounds(v);
            if (c == null) return;
            Vector3 head = c.GetHeadPoint();
            for (int i = 0; i < BeamStartSounds.Length; i++) PlaySfx(v, BeamStartSounds[i], head, null, 1f);
            // the loops ride along with the mouth light, so they move with the drake
            for (int i = 0; i < BeamLoopSounds.Length; i++) PlaySfx(v, BeamLoopSounds[i], head, v.Mouth != null ? v.Mouth.transform : null, BeamLoopVolume[i]);
        }

        private static void PlaySfx(BeamView v, string name, Vector3 at, Transform follow, float volume)
        {
            GameObject prefab = Sfx(name);
            if (prefab == null) return;
            GameObject g = UnityEngine.Object.Instantiate(prefab, at, Quaternion.identity);
            foreach (ZNetView nv in g.GetComponentsInChildren<ZNetView>(true)) UnityEngine.Object.Destroy(nv);
            if (follow != null) g.transform.SetParent(follow, true);
            if (volume < 1f) foreach (AudioSource a in g.GetComponentsInChildren<AudioSource>(true)) a.volume *= volume;
            v.Sounds.Add(g);
        }

        private static void StopSounds(BeamView v)
        {
            foreach (GameObject g in v.Sounds)
            {
                if (g == null) continue;
                if (g.transform.parent != null) g.transform.SetParent(null, true);
                foreach (AudioSource a in g.GetComponentsInChildren<AudioSource>(true)) a.loop = false;
                UnityEngine.Object.Destroy(g, 0.6f);   // what is still sounding fades out on its own, a loop is cut
            }
            v.Sounds.Clear();
        }

        // a sound prefab by name: from the game's prefab list, else from the effects of the creatures that use it
        private static GameObject Sfx(string name)
        {
            GameObject g;
            if (_sfx.TryGetValue(name, out g)) return g;
            g = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(name) : null;
            if (g == null) g = FromEffects(name, "Dragon", "GoblinKing", "Hatchling");
            if (g == null)
                foreach (GameObject o in Resources.FindObjectsOfTypeAll<GameObject>())
                    if (o != null && o.name == name && o.transform.parent == null) { g = o; break; }
            if (ZNetScene.instance != null) _sfx[name] = g;
            Plugin.Debug("[beam] sound " + name + ": " + (g != null ? "found" : "NOT found"));
            return g;
        }

        // looks through every effect list of these creatures, their attacks and what their attacks throw
        private static GameObject FromEffects(string name, params string[] creatures)
        {
            if (ZNetScene.instance == null) return null;
            foreach (string cn in creatures)
            {
                GameObject cp = ZNetScene.instance.GetPrefab(cn);
                if (cp == null) continue;
                List<GameObject> roots = new List<GameObject> { cp };
                Humanoid h = cp.GetComponent<Humanoid>();
                if (h != null && h.m_defaultItems != null)
                    foreach (GameObject it in h.m_defaultItems)
                    {
                        if (it == null) continue;
                        roots.Add(it);
                        ItemDrop d = it.GetComponent<ItemDrop>();
                        if (d == null) continue;
                        foreach (Attack a in new[] { d.m_itemData.m_shared.m_attack, d.m_itemData.m_shared.m_secondaryAttack })
                        {
                            if (a == null) continue;
                            foreach (EffectList el in new[] { a.m_startEffect, a.m_triggerEffect, a.m_hitEffect, a.m_trailStartEffect })
                            { GameObject f = InList(el, name); if (f != null) return f; }
                            if (a.m_attackProjectile != null) roots.Add(a.m_attackProjectile);
                        }
                    }
                foreach (GameObject r in roots)
                    foreach (MonoBehaviour mb in r.GetComponentsInChildren<MonoBehaviour>(true))
                    {
                        if (mb == null) continue;
                        foreach (System.Reflection.FieldInfo f in mb.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                        {
                            if (f.FieldType != typeof(EffectList)) continue;
                            GameObject found = InList(f.GetValue(mb) as EffectList, name);
                            if (found != null) return found;
                        }
                    }
            }
            return null;
        }

        private static GameObject InList(EffectList el, string name)
        {
            if (el == null || el.m_effectPrefabs == null) return null;
            foreach (EffectList.EffectData e in el.m_effectPrefabs)
                if (e != null && e.m_prefab != null && e.m_prefab.name == name) return e.m_prefab;
            return null;
        }

        private static void Close(BeamView v)
        {
            StopSounds(v);
            if (v.Root != null) v.Root.SetActive(false);
        }

        private static void Draw(BeamView v, Vector3 mouth, Vector3 end, Vector3 dir)
        {
            float scale = v.Drake != null ? Mathf.Max(1f, v.Drake.transform.localScale.x) : 1f;
            // flickers a little, thins out in its last moment
            float left = Mathf.Clamp01((v.Until - Time.time) / 0.15f);
            float grow = Mathf.Clamp01((Time.time - v.Start) / 0.08f);
            float w = BeamWidth * scale * 0.6f * grow * left * (0.85f + 0.15f * Mathf.Sin(Time.time * 60f));
            SetLine(v.Glow, mouth, end, w * 2.2f, w * 1.4f);
            SetLine(v.Core, mouth, end, w * 0.55f, w * 0.35f);
            if (v.Mouth != null) { v.Mouth.transform.position = mouth; v.Mouth.intensity = 2.5f * left; }
            if (v.Hit != null) { v.Hit.transform.position = end - dir * 0.5f; v.Hit.intensity = 4f * left * (0.8f + 0.2f * UnityEngine.Random.value); }
            // ice bursts where it lands (the ice spit's own hit: its particles and sound)
            if (Time.time >= v.NextSplash)
            {
                v.NextSplash = Time.time + 0.2f;
                Shot s = ShotOf(Frost);
                Projectile p = s != null && s.Prefab != null ? s.Prefab.GetComponent<Projectile>() : null;
                if (p != null) LocalEffects(p.m_hitEffects, end, Quaternion.LookRotation(-dir), "hit");
            }
        }

        // an effect list made only here (each game makes its own), never a networked object; what it holds goes to the
        // log once, so we know which has particles and which has sound
        private static void LocalEffects(EffectList list, Vector3 at, Quaternion rot, string what)
        {
            if (list == null || list.m_effectPrefabs == null) return;
            List<string> seen = !_effectsLogged ? new List<string>() : null;
            foreach (EffectList.EffectData e in list.m_effectPrefabs)
            {
                if (e == null || e.m_prefab == null || !e.m_enabled) continue;
                bool net = e.m_prefab.GetComponent<ZNetView>() != null;
                if (seen != null) seen.Add(e.m_prefab.name + (net ? " (networked, skipped)" : "") + (e.m_prefab.GetComponentInChildren<ParticleSystem>(true) != null ? " particles" : "") + (e.m_prefab.GetComponentInChildren<AudioSource>(true) != null ? " sound" : "") + (e.m_prefab.GetComponentInChildren<Light>(true) != null ? " light" : ""));
                if (net) continue;
                UnityEngine.Object.Instantiate(e.m_prefab, at, e.m_inheritParentRotation || e.m_randomRotation ? rot : rot);
            }
            if (seen != null)
            {
                Plugin.Debug("[beam] " + what + " effects: " + (seen.Count > 0 ? string.Join(", ", seen.ToArray()) : "none"));
                if (what == "hit") _effectsLogged = true;
            }
        }

        private static BeamView NewView(string name)
        {
            BeamView v = new BeamView();
            v.Root = new GameObject("Beastkeeper_FrostBeam_" + name);
            UnityEngine.Object.DontDestroyOnLoad(v.Root);
            if (_glowMat == null) _glowMat = GlowMaterial(true);
            if (_coreMat == null) _coreMat = GlowMaterial(false);
            v.Glow = NewLine(v.Root, "glow", _glowMat, new Color(0.45f, 0.75f, 1f, 0.55f));
            v.Core = NewLine(v.Root, "core", _coreMat, new Color(0.95f, 0.98f, 1f, 0.95f));
            v.Mouth = NewLight(v.Root, "mouth", 6f);
            v.Hit = NewLight(v.Root, "hit", 9f);
            v.Root.SetActive(false);
            return v;
        }

        private static LineRenderer NewLine(GameObject root, string name, Material m, Color col)
        {
            GameObject g = new GameObject(name);
            g.transform.SetParent(root.transform, false);
            LineRenderer lr = g.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.positionCount = 2;
            lr.numCapVertices = 4;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.textureMode = LineTextureMode.Stretch;
            if (m != null) lr.material = m;
            lr.startColor = col;
            lr.endColor = new Color(col.r, col.g, col.b, col.a * 0.8f);
            return lr;
        }

        private static Light NewLight(GameObject root, string name, float range)
        {
            GameObject g = new GameObject(name);
            g.transform.SetParent(root.transform, false);
            Light l = g.AddComponent<Light>();
            l.type = LightType.Point;
            l.color = new Color(0.6f, 0.85f, 1f);
            l.range = range;
            l.shadows = LightShadows.None;
            return l;
        }

        private static void SetLine(LineRenderer lr, Vector3 a, Vector3 b, float wa, float wb)
        {
            if (lr == null) return;
            lr.SetPosition(0, a);
            lr.SetPosition(1, b);
            lr.startWidth = wa;
            lr.endWidth = wb;
        }

        // the glow of the game's ice spit (a particle material that adds light), so the beam shines like it. The wide
        // glow keeps the puff's soft picture (soft edges); the core is smooth
        private static Material GlowMaterial(bool soft)
        {
            Shot s = ShotOf(Frost);
            Material found = null;
            if (s != null && s.Prefab != null)
                foreach (ParticleSystemRenderer r in s.Prefab.GetComponentsInChildren<ParticleSystemRenderer>(true))
                    if (r != null && r.sharedMaterial != null) { found = r.sharedMaterial; break; }
            Plugin.Debug("[beam] material " + (found != null ? found.name + " (" + found.shader.name + ")" : "none, plain line"));
            if (found == null) return null;
            Material m = new Material(found);
            if (!soft) m.mainTexture = null;
            if (m.HasProperty("_TintColor")) m.SetColor("_TintColor", new Color(0.6f, 0.8f, 1f, 0.6f));
            if (m.HasProperty("_Color")) m.SetColor("_Color", Color.white);
            return m;
        }

        // ------------------------------------------------------------ the heavy aim

        [HarmonyPatch(typeof(Player), "SetMouseLook"), HarmonyPrefix]
        private static void HeavyAim(Player __instance, ref Vector2 mouseLook)
        {
            if (__instance == Player.m_localPlayer && BeamActive) mouseLook *= AimWeight;
        }
    }
}
