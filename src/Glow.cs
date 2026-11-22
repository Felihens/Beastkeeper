using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Beastkeeper
{
    // How an animal goes into a Beast Totem and comes out of it (user, 2026-11-22), on this game only:
    // leaving: its skin turns to white light, a ball of light appears at its centre, the animal fades away (under a
    //          second) leaving only the ball, a narrow pillar of light rises from the ball to the sky, the ball goes
    //          out and then the pillar.
    // arriving: the same backwards: a pillar comes down to a ball, the animal appears as white light around it and
    //          its own colours come back.
    // Made only of plain shapes with an unlit see-through material: nothing from other mods.
    // Everyone nearby with the mod sees it (user, 2026-11-22): the game that stores or takes out the animal tells
    // the others, and each plays it on its own screen. Only the first game removes the animal; the others keep it
    // hidden until that removal reaches them.
    internal static class Glow
    {
        private const float GlowIn = 0.35f, GlowHold = 0.25f, FadeOut = 0.7f, PillarUp = 0.3f, BallIn = 0.25f, BallOut = 0.25f, PillarOut = 0.6f;
        private const float Height = 60f, PillarWidth = 0.3f, BallSize = 0.45f;   // user, 2026-11-22: smaller
        // brighter than white, so the skin really shines (user, 2026-11-22: "full brightness before it fades")
        private static readonly Color White = new Color(3f, 3f, 3f, 1f);
        private static readonly Color LightColor = new Color(1f, 0.95f, 0.8f, 1f);
        private const float LightIntensity = 2.4f;

        private static Material NewMat(Color c)
        {
            Shader sh = Shader.Find("Sprites/Default");
            if (sh == null) return null;
            Material m = new Material(sh);
            m.color = c;
            return m;
        }

        private static void SetAlpha(Material m, Color c, float a) { if (m != null) m.color = new Color(c.r, c.g, c.b, a); }

        private static GameObject Shape(PrimitiveType type, string name, Material m)
        {
            GameObject g = GameObject.CreatePrimitive(type);
            g.name = name;
            Collider col = g.GetComponent<Collider>();
            if (col != null) Object.Destroy(col);
            Renderer r = g.GetComponent<Renderer>();
            if (m != null) r.sharedMaterial = m;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return g;
        }

        // the animal's own look (not its effects or ropes)
        private static List<Renderer> Skin(Character c)
        {
            List<Renderer> list = new List<Renderer>();
            foreach (Renderer r in c.GetComponentsInChildren<Renderer>())
                if (r != null && r.enabled && (r is SkinnedMeshRenderer || r is MeshRenderer) && !r.name.StartsWith("Beastkeeper_Rope")) list.Add(r);
            return list;
        }

        private static void Overlay(List<Renderer> skin, Dictionary<Renderer, Material[]> own, Material white)
        {
            foreach (Renderer r in skin)
            {
                if (r == null) continue;
                Material[] o = own[r];
                Material[] m = new Material[o.Length + 1];
                o.CopyTo(m, 0);
                m[o.Length] = white;
                r.sharedMaterials = m;
            }
        }

        private static void AllWhite(List<Renderer> skin, Dictionary<Renderer, Material[]> own, Material white)
        {
            foreach (Renderer r in skin)
            {
                if (r == null) continue;
                Material[] m = new Material[Mathf.Max(1, own[r].Length)];
                for (int i = 0; i < m.Length; i++) m[i] = white;
                r.sharedMaterials = m;
            }
        }

        private static void Restore(List<Renderer> skin, Dictionary<Renderer, Material[]> own)
        {
            foreach (Renderer r in skin) if (r != null) r.sharedMaterials = own[r];
        }

        // ------------------------------------------------------------ sound (user, 2026-11-22: the effect should be heard)
        // the game's own sounds: a shimmer when the skin shines, a rush when the pillar goes up or comes down
        private const string ShineSound = "sfx_dverger_heal_start", ShownSound = "sfx_dverger_heal_finish", PillarSound = "sfx_offering";
        private static readonly Dictionary<string, GameObject> _sounds = new Dictionary<string, GameObject>();
        private const float SoundVolume = 0.7f;   // user, 2026-11-22: 30% quieter than the game plays them
        private static GameObject _soundHold;

        private static GameObject FindSound(string name)
        {
            GameObject g;
            if (_sounds.TryGetValue(name, out g)) return g;
            g = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(name) : null;
            if (g == null)
                foreach (GameObject o in Resources.FindObjectsOfTypeAll<GameObject>())
                    if (o != null && o.name == name && o.transform.parent == null && o.GetComponent<ZNetView>() == null) { g = o; break; }
            _sounds[name] = g;
            Plugin.Debug("[glow] sound " + name + (g != null ? " found" : " not found"));
            return g;
        }

        private static void Sound(string name, Vector3 at)
        {
            try
            {
                GameObject g = FindSound(name);
                if (g == null) return;
                // made under a switched-off holder, so its volume is lowered before it starts playing
                if (_soundHold == null) { _soundHold = new GameObject("Beastkeeper_SoundHold"); _soundHold.SetActive(false); Object.DontDestroyOnLoad(_soundHold); }
                GameObject s = Object.Instantiate(g, at, Quaternion.identity, _soundHold.transform);
                foreach (ZSFX z in s.GetComponentsInChildren<ZSFX>(true)) { z.m_minVol *= SoundVolume; z.m_maxVol *= SoundVolume; }
                foreach (AudioSource a in s.GetComponentsInChildren<AudioSource>(true)) a.volume *= SoundVolume;
                s.transform.SetParent(null, true);
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("[glow] sound " + name + ": " + e.Message); }
        }

        // ------------------------------------------------------------ where the light sits on each animal
        // Set by the user in the totem light editor (2026-11-22): per animal, a point in its own frame (metres, at
        // scale 1) and the ball's size. Shipped as res/glow.txt; a file Beastkeeper_models/glow.txt is used instead
        // when there is one, read again when it changes. Lines: "<prefab> x y z size". Animals not listed fall back to
        // the middle of their body.
        private class Spot { public Vector3 at; public float size; }
        private static Dictionary<string, Spot> _spots;
        private static System.DateTime _spotsTime;

        private static string SpotsFile { get { return Path.Combine(Path.Combine(BepInEx.Paths.ConfigPath, "Beastkeeper_models"), "glow.txt"); } }

        private static Dictionary<string, Spot> ReadSpots(string text)
        {
            Dictionary<string, Spot> d = new Dictionary<string, Spot>();
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                string[] v = line.Split(new char[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
                if (v.Length < 4) continue;
                float x, y, z, size = BallSize, probe;
                System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
                System.Globalization.NumberStyles num = System.Globalization.NumberStyles.Float;
                int i = 1;
                string pose = Idle;
                if (!float.TryParse(v[1], num, inv, out probe)) { pose = v[1]; i = 2; }
                if (v.Length < i + 3) continue;
                if (!float.TryParse(v[i], num, inv, out x) || !float.TryParse(v[i + 1], num, inv, out y) || !float.TryParse(v[i + 2], num, inv, out z)) continue;
                if (v.Length > i + 3) float.TryParse(v[i + 3], num, inv, out size);
                Spot sp = new Spot();
                sp.at = new Vector3(x, y, z);
                sp.size = Mathf.Clamp(size, 0.1f, 3f);
                d[v[0] + "|" + pose] = sp;
            }
            return d;
        }

        // the editor's poses: standing (drake: hovering), walking (drake: on the ground), running (drake: flying forward)
        private const string Idle = "idle", Walk = "walk", Run = "run";

        // what it is doing as the light starts; it is held in that pose until the end
        private static string PoseOf(Character c)
        {
            if (c == null) return Idle;
            Vector3 v = c.GetVelocity();
            v.y = 0f;
            float speed = v.magnitude;
            if (Drake.Is(c)) return !c.IsFlying() ? Walk : speed > 3f ? Run : Idle;
            if (speed < 0.5f) return Idle;
            return c.IsRunning() || speed > 5f ? Run : Walk;
        }

        private static Spot SpotOf(Character c, string pose)
        {
            try
            {
                string f = SpotsFile;
                if (File.Exists(f))
                {
                    System.DateTime t = File.GetLastWriteTimeUtc(f);
                    if (_spots == null || t != _spotsTime) { _spots = ReadSpots(File.ReadAllText(f)); _spotsTime = t; Plugin.Debug("[glow] light spots from " + f + ": " + _spots.Count); }
                }
                else if (_spots == null)
                {
                    _spots = new Dictionary<string, Spot>();
                    using (Stream st = typeof(Glow).Assembly.GetManifestResourceStream("Beastkeeper.glow.txt"))
                        if (st != null) using (StreamReader r = new StreamReader(st)) _spots = ReadSpots(r.ReadToEnd());
                }
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("[glow] light spots: " + e.Message); if (_spots == null) _spots = new Dictionary<string, Spot>(); }
            if (c == null) return null;
            string name = Utils.GetPrefabName(c.gameObject);
            Spot sp;
            // that pose, else another one of this animal's
            if (_spots.TryGetValue(name + "|" + pose, out sp)) return sp;
            foreach (string q in new string[] { Idle, Walk, Run })
                if (_spots.TryGetValue(name + "|" + q, out sp)) return sp;
            return null;
        }

        private static float SizeOf(Character c, string pose)
        {
            Spot sp = SpotOf(c, pose);
            float k = c != null ? Mathf.Max(0.1f, c.transform.lossyScale.y) : 1f;
            return (sp != null ? sp.size : BallSize) * k;
        }

        // the editor's spot, else the middle of its body (its skinned mesh)
        private static Vector3 Centre(Character c, List<Renderer> skin, string pose)
        {
            Spot spot = SpotOf(c, pose);
            if (spot != null) return c.transform.TransformPoint(spot.at);
            Renderer best = null;
            float size = 0f;
            foreach (Renderer r in skin)
            {
                if (r == null || !(r is SkinnedMeshRenderer)) continue;
                float v = r.bounds.size.sqrMagnitude;
                if (v > size) { size = v; best = r; }
            }
            if (best != null) return best.bounds.center;
            return c.GetCenterPoint();
        }

        // it stays exactly where it is until the effect ends (user, 2026-11-22)
        private static void Hold(Character c)
        {
            if (c == null) return;
            BaseAI ai = c.GetComponent<BaseAI>();
            if (ai != null) ai.enabled = false;
            c.SetMoveDir(Vector3.zero);
            if (c.GetComponent<Pinned>() == null) c.gameObject.AddComponent<Pinned>();
        }

        private static void Release(Character c)
        {
            if (c == null) return;
            Pinned pin = c.GetComponent<Pinned>();
            if (pin != null) Object.Destroy(pin);
            BaseAI ai = c.GetComponent<BaseAI>();
            if (ai != null) ai.enabled = true;
        }

        internal class Pinned : MonoBehaviour
        {
            private Vector3 _pos;
            private Quaternion _rot;
            private Rigidbody _body;
            private Character _c;
            private Animator _anim;
            private float _animSpeed = 1f;
            private int _frames;

            private void Awake()
            {
                _pos = transform.position;
                _rot = transform.rotation;
                _body = GetComponent<Rigidbody>();
                _c = GetComponent<Character>();
            }

            private void Keep()
            {
                if (_c != null) _c.SetMoveDir(Vector3.zero);
                if (_body != null)
                {
                    _body.velocity = Vector3.zero;
                    _body.angularVelocity = Vector3.zero;
                    _body.position = _pos;
                    _body.rotation = _rot;
                }
                transform.position = _pos;
                transform.rotation = _rot;
            }

            private void FixedUpdate() { Keep(); }

            private void LateUpdate()
            {
                Keep();
                if (++_frames == 2)
                {
                    _anim = GetComponentInChildren<Animator>();
                    if (_anim != null) { _animSpeed = _anim.speed; _anim.speed = 0f; }
                }
                else if (_anim != null && _frames > 2) _anim.speed = 0f;
            }

            private void OnDestroy() { if (_anim != null) _anim.speed = _animSpeed > 0f ? _animSpeed : 1f; }
        }

        // ------------------------------------------------------------ leaving

        // gone = remove it from the world at the end of its fade (it was already written down)
        internal static void Leave(Character c, bool gone)
        {
            if (c == null || Plugin.Instance == null) { if (gone) Remove(c); return; }
            Tell(c, 0);
            Plugin.Instance.StartCoroutine(LeaveNow(c, gone, false));
        }

        // ------------------------------------------------------------ telling the other players

        private const string Rpc = "Beastkeeper_Glow";
        private static ZRoutedRpc _registeredOn;

        internal static void Register()
        {
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (rpc == null || rpc == _registeredOn) return;
            rpc.Register<ZDOID, int>(Rpc, new System.Action<long, ZDOID, int>(OnGlow));
            _registeredOn = rpc;
        }

        // 0 = leaving, 1 = arriving
        private static void Tell(Character c, int kind)
        {
            ZNetView nv = Leash.View(c);
            if (ZRoutedRpc.instance == null || nv == null || !nv.IsValid()) return;
            Register();
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, Rpc, new object[] { nv.GetZDO().m_uid, kind });
        }

        private static void OnGlow(long sender, ZDOID id, int kind)
        {
            if (sender == ZNet.GetUID() || Plugin.Instance == null || Player.m_localPlayer == null) return;   // ours: already playing
            Plugin.Instance.StartCoroutine(Remote(id, kind));
        }

        // the animal may reach this game a moment after the message (a new one coming out of a totem)
        private static IEnumerator Remote(ZDOID id, int kind)
        {
            float wait = kind == 1 ? 2f : 0.3f;
            for (float t = 0f; t < wait; t += 0.1f)
            {
                GameObject g = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(id) : null;
                Character c = g != null ? g.GetComponent<Character>() : null;
                if (c != null)
                {
                    if (Vector3.Distance(c.transform.position, Player.m_localPlayer.transform.position) > 150f) yield break;
                    if (kind == 0) yield return LeaveNow(c, false, true);
                    else yield return ArriveNow(c);
                    yield break;
                }
                yield return new WaitForSeconds(0.1f);
            }
        }

        // only the light: a ball and a pillar at a spot (when the animal can't be shown)
        internal static void Pillar(Vector3 at)
        {
            if (Plugin.Instance != null) Plugin.Instance.StartCoroutine(PillarNow(at, null, null, null));
        }

        private static void Remove(Character c)
        {
            if (c == null) return;
            ZNetView nv = Leash.View(c);
            if (nv == null || !nv.IsValid()) return;
            if (!nv.IsOwner()) nv.ClaimOwnership();
            ZNetScene.instance.Destroy(c.gameObject);
        }

        // hidden = (another player's game) the animal stays invisible after its fade, until its removal reaches us
        private static IEnumerator LeaveNow(Character c, bool gone, bool hidden)
        {
            Material white = NewMat(White), ballMat = NewMat(LightColor);
            GameObject ball = null;
            Light lamp = null;
            List<Renderer> skin = Skin(c);
            Dictionary<Renderer, Material[]> own = new Dictionary<Renderer, Material[]>();
            foreach (Renderer r in skin) own[r] = r.sharedMaterials;
            bool removed = false;
            string pose = PoseOf(c);
            Vector3 centre = Centre(c, skin, pose);
            float size = SizeOf(c, pose);
            Plugin.Debug("[glow] " + Utils.GetPrefabName(c.gameObject) + " (" + pose + "): light " + (SpotOf(c, pose) != null ? "where the editor put it" : "at the middle of its body") + ", ball " + size.ToString("0.00") + " m");
            try
            {
                Hold(c);
                if (white != null) Overlay(skin, own, white);
                ball = Shape(PrimitiveType.Sphere, "Beastkeeper_LightBall", ballMat);
                ball.transform.localScale = Vector3.zero;
                lamp = ball.AddComponent<Light>();
                lamp.type = LightType.Point;
                lamp.color = LightColor;
                lamp.range = 10f;
                lamp.intensity = 0f;

                // 1. its skin turns to light, a ball grows at its centre
                Sound(ShineSound, centre);
                float t = 0f;
                while (t < GlowIn && c != null)
                {
                    t += Time.deltaTime;
                    float k = Mathf.Clamp01(t / GlowIn);
                    centre = Centre(c, skin, pose);
                    SetAlpha(white, White, k);
                    ball.transform.position = centre;
                    ball.transform.localScale = Vector3.one * size * k;
                    lamp.intensity = LightIntensity * k;
                    yield return null;
                }
                // all of it shines at full for a moment
                if (c != null && white != null) AllWhite(skin, own, white);
                SetAlpha(white, White, 1f);
                t = 0f;
                while (t < GlowHold && c != null)
                {
                    t += Time.deltaTime;
                    centre = Centre(c, skin, pose);
                    ball.transform.position = centre;
                    yield return null;
                }
                // 2. the animal fades away, only the ball is left
                t = 0f;
                while (t < FadeOut && c != null)
                {
                    t += Time.deltaTime;
                    SetAlpha(white, White, 1f - Mathf.Clamp01(t / FadeOut));
                    yield return null;
                }
                if (gone) { Remove(c); removed = true; }
                else if (hidden && c != null) { Plugin.Instance.StartCoroutine(ShowAgainLater(c, skin, own, white)); white = null; }
                else if (c != null) { Restore(skin, own); Release(c); }
            }
            finally
            {
                if (gone && !removed) Remove(c);
                if (white != null) Object.Destroy(white);
            }
            // 3. the pillar rises from the ball, the ball goes out, then the pillar
            yield return PillarNow(centre, ball, lamp, ballMat);
        }

        private static IEnumerator PillarNow(Vector3 at, GameObject ball, Light lamp, Material ballMat)
        {
            if (ball == null)
            {
                ballMat = NewMat(LightColor);
                ball = Shape(PrimitiveType.Sphere, "Beastkeeper_LightBall", ballMat);
                ball.transform.position = at;
                ball.transform.localScale = Vector3.one * BallSize;
                lamp = ball.AddComponent<Light>();
                lamp.type = LightType.Point;
                lamp.color = LightColor;
                lamp.range = 10f;
                lamp.intensity = LightIntensity;
            }
            Material pillarMat = NewMat(LightColor);
            GameObject pillar = Shape(PrimitiveType.Cylinder, "Beastkeeper_LightPillar", pillarMat);
            Sound(PillarSound, at);
            try
            {
                float t = 0f;
                while (t < PillarUp)
                {
                    t += Time.deltaTime;
                    float h = Height * Mathf.Clamp01(t / PillarUp);
                    pillar.transform.localScale = new Vector3(PillarWidth, h * 0.5f, PillarWidth);
                    pillar.transform.position = at + Vector3.up * (h * 0.5f);
                    yield return null;
                }
                t = 0f;
                Vector3 full = ball.transform.localScale;
                while (t < BallOut)
                {
                    t += Time.deltaTime;
                    float k = 1f - Mathf.Clamp01(t / BallOut);
                    ball.transform.localScale = full * k;
                    if (lamp != null) lamp.intensity = LightIntensity * k;
                    yield return null;
                }
                ball.SetActive(false);
                t = 0f;
                while (t < PillarOut)
                {
                    t += Time.deltaTime;
                    float k = 1f - Mathf.Clamp01(t / PillarOut);
                    SetAlpha(pillarMat, LightColor, k);
                    pillar.transform.localScale = new Vector3(PillarWidth * (0.4f + 0.6f * k), Height * 0.5f, PillarWidth * (0.4f + 0.6f * k));
                    yield return null;
                }
            }
            finally
            {
                Object.Destroy(pillar);
                if (ball != null) Object.Destroy(ball);
                if (pillarMat != null) Object.Destroy(pillarMat);
                if (ballMat != null) Object.Destroy(ballMat);
            }
        }

        // should the removal never come (the other game kept it), it shows again
        private static IEnumerator ShowAgainLater(Character c, List<Renderer> skin, Dictionary<Renderer, Material[]> own, Material white)
        {
            yield return new WaitForSeconds(5f);
            if (c != null) { Restore(skin, own); Release(c); }
            if (white != null) Object.Destroy(white);
        }

        // ------------------------------------------------------------ arriving

        internal static void Arrive(Character c)
        {
            if (c == null || Plugin.Instance == null) return;
            Tell(c, 1);
            Plugin.Instance.StartCoroutine(ArriveNow(c));
        }

        private static IEnumerator ArriveNow(Character c)
        {
            Hold(c);
            yield return null;   // let it set itself up (size, saddle) for a frame
            if (c == null) yield break;
            Material white = NewMat(White), ballMat = NewMat(LightColor), pillarMat = NewMat(LightColor);
            List<Renderer> skin = Skin(c);
            Dictionary<Renderer, Material[]> own = new Dictionary<Renderer, Material[]>();
            foreach (Renderer r in skin) own[r] = r.sharedMaterials;
            string pose = PoseOf(c);
            Vector3 centre = Centre(c, skin, pose);
            float size = SizeOf(c, pose);
            Plugin.Debug("[glow] " + Utils.GetPrefabName(c.gameObject) + " arrives (" + pose + "): light " + (SpotOf(c, pose) != null ? "where the editor put it" : "at the middle of its body"));
            GameObject ball = Shape(PrimitiveType.Sphere, "Beastkeeper_LightBall", ballMat);
            ball.transform.position = centre;
            ball.transform.localScale = Vector3.zero;
            Light lamp = ball.AddComponent<Light>();
            lamp.type = LightType.Point;
            lamp.color = LightColor;
            lamp.range = 10f;
            GameObject pillar = Shape(PrimitiveType.Cylinder, "Beastkeeper_LightPillar", pillarMat);
            try
            {
                // hidden at first: only light where it will stand
                if (white != null) { AllWhite(skin, own, white); SetAlpha(white, White, 0f); }
                // the leaving in reverse (user, 2026-11-22): first the pillar, then the ball, then the animal
                // 1. a pillar comes down from the sky to where it will stand
                Sound(PillarSound, centre);
                float t = 0f;
                while (t < PillarUp)
                {
                    t += Time.deltaTime;
                    float h = Height * Mathf.Clamp01(t / PillarUp);
                    pillar.transform.localScale = new Vector3(PillarWidth, h * 0.5f, PillarWidth);
                    pillar.transform.position = centre + Vector3.up * (Height - h * 0.5f);
                    yield return null;
                }
                // 2. a ball lights up at its foot, while the pillar narrows and goes out
                t = 0f;
                while (t < PillarOut)
                {
                    t += Time.deltaTime;
                    float b = Mathf.Clamp01(t / BallIn);
                    float k = 1f - Mathf.Clamp01(t / PillarOut);
                    ball.transform.localScale = Vector3.one * size * b;
                    lamp.intensity = LightIntensity * b;
                    SetAlpha(pillarMat, LightColor, k);
                    pillar.transform.localScale = new Vector3(PillarWidth * (0.4f + 0.6f * k), Height * 0.5f, PillarWidth * (0.4f + 0.6f * k));
                    yield return null;
                }
                pillar.SetActive(false);
                // 3. the animal appears around the ball, all shining
                Sound(ShownSound, centre);
                t = 0f;
                while (t < FadeOut)
                {
                    t += Time.deltaTime;
                    SetAlpha(white, White, Mathf.Clamp01(t / FadeOut));
                    if (c == null) yield break;
                    yield return null;
                }
                SetAlpha(white, White, 1f);
                t = 0f;
                while (t < GlowHold) { t += Time.deltaTime; if (c == null) yield break; yield return null; }
                // 4. its own colours come back and the ball goes out
                if (c != null && white != null) Overlay(skin, own, white);
                t = 0f;
                while (t < GlowIn + BallOut)
                {
                    t += Time.deltaTime;
                    float k = 1f - Mathf.Clamp01(t / (GlowIn + BallOut));
                    SetAlpha(white, White, k);
                    ball.transform.localScale = Vector3.one * size * k;
                    lamp.intensity = LightIntensity * k;
                    if (c == null) yield break;
                    yield return null;
                }
            }
            finally
            {
                if (c != null) { Restore(skin, own); Release(c); }
                Object.Destroy(pillar);
                Object.Destroy(ball);
                if (white != null) Object.Destroy(white);
                if (ballMat != null) Object.Destroy(ballMat);
                if (pillarMat != null) Object.Destroy(pillarMat);
            }
        }
    }
}
