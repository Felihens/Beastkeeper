using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Beastkeeper
{
    // The drake as a flying mount (agreed with the user, 2026-11-11):
    // - wild drakes can't be caught: tamed ones hatch from Moder's eggs (DrakeEgg.cs), as babies that grow up;
    // - it only takes the Drake Saddle, made with a drake trophy; flying starts after Moder anyway (eggs only hatch once she is dead);
    // - a tamed drake grows bigger, so a viking fits on its back;
    // - ridden it always flies: W/A/S/D steer, jump held climbs, crouch held goes down;
    // - flying high tires it: a flight bar empties in FlightSeconds and refills near the ground; empty, it
    //   glides down and can't climb until it has rested a while; hand feeding fills it at once;
    // - it never climbs more than MaxAltitude above the ground or the sea;
    // - it attacks like a bow (DrakeAttack.cs), only low unless AttackInAir; E only gets you off near the ground.
    [HarmonyPatch]
    internal static partial class Drake
    {
        private static readonly int AirKey = "Beastkeeper_air".GetStableHashCode();     // flight seconds left
        private static readonly int AirAtKey = "Beastkeeper_airAt".GetStableHashCode(); // when that was saved (world time)

        private const float LowAltitude = 3f;      // below this it's resting, may attack, and you may get off
        private const float ClimbShare = 0.7f;     // climbing and diving speed, compared to its flying speed

        private static float _air;                 // flight seconds left, while you ride it
        private static bool _tired;                // ran empty: no climbing until it has rested
        private static float _slow = -1f, _fast = -1f;
        private static bool _onFoot;                   // landed under its rider: it walks where you steer, and takes off when you jump
        private static float _jog0 = -1f, _run0 = -1f; // its own walking speeds, put back when you get off
        private static float _acc0 = -1f;              // its own acceleration: on foot under a rider it is at full speed at once
        private const float FootAccel = 100f;
        private const float DeepWater = 1.2f;          // water deeper than this (metres) is "the sea" for a drake
        private const float LandAt = 1.2f;             // how close to the ground "down" lands it, in metres
        private static Character _speedOf;
        private static float _nextSave;

        internal static bool Is(Character c)
        {
            if (c == null || Plugin.DrakeMounts == null) return false;
            string kind = Mount.Kind(c.gameObject);
            foreach (string s in (Plugin.DrakeMounts.Value ?? "").Split(','))
                if (string.Equals(s.Trim(), kind, System.StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static int _stars;   // of the drake being ridden (0, 1 or 2)
        private static float Max { get { return Mathf.Max(1f, Plugin.DrakeFlightSeconds.Value) + StarFlight * _stars; } }

        // A starred drake (hatched with 1 or 2 stars) is better at everything: +25% attack damage, +10% speed and
        // one more 5 s section of flight for each star. Its health follows the game's own rule for starred creatures.
        internal const float StarDamage = 0.25f, StarSpeed = 0.1f, StarFlight = 5f;
        internal static int Stars(Character c) { return c != null ? Mathf.Clamp(c.GetLevel() - 1, 0, 2) : 0; }
        internal static float DamageOf(Character c) { return 1f + StarDamage * Stars(c); }
        private static float SpeedOf(Character c) { return 1f + StarSpeed * Stars(c); }

        // ------------------------------------------------------------ size

        // tamed drakes grow, so a rider fits; wild ones keep the game's size; a hatched baby starts small
        internal static void KeepSize(Character c)
        {
            if (!Is(c)) return;
            float want = c.IsTamed() && Plugin.Enabled.Value ? Plugin.DrakeSize.Value * Mathf.Lerp(BabyShare, 1f, Grown(c)) : 1f;
            if (Mathf.Abs(c.transform.localScale.x - want) > 0.001f) c.transform.localScale = Vector3.one * want;
            if (c.IsTamed()) Shape(c);   // its collision (DrakeCollision.cs)
            // a tamed drake doesn't roar each time it spots something
            BaseAI ai = c.IsTamed() ? c.GetBaseAI() : null;
            if (ai != null && ai.m_alertedEffects != null && ai.m_alertedEffects.m_effectPrefabs != null && ai.m_alertedEffects.m_effectPrefabs.Length > 0)
                ai.m_alertedEffects = new EffectList();
        }

        // A tamed drake's body is a little smaller than the game's capsule around it (made for the flying pose):
        // narrower and lower, with its foot kept where it was, so it stands as close to things as it looks.
        private static readonly HashSet<int> _slimmed = new HashSet<int>();
        private const float SlimRadius = 0.72f, SlimHeight = 0.85f;

        private static void Slim(Character c)
        {
            CapsuleCollider cap = c.GetCollider();
            if (cap == null || !_slimmed.Add(cap.GetInstanceID())) return;
            float foot = cap.center.y - cap.height / 2f;
            cap.radius *= SlimRadius;
            cap.height = Mathf.Max(cap.height * SlimHeight, cap.radius * 2f);
            Vector3 ce = cap.center;
            ce.y = foot + cap.height / 2f;
            cap.center = ce;
            if (_slimmed.Count > 400) _slimmed.Clear();
        }

        // ------------------------------------------------------------ getting on and off

        internal static void OnRide(Character c)
        {
            KeepSafe(1f);
            KeepSize(c);
            _stars = Stars(c);
            ZNetView nv = Leash.View(c);
            _air = Max;
            if (nv != null && nv.IsValid())
            {
                ZDO z = nv.GetZDO();
                float saved = z.GetFloat(AirKey, Max);
                double at = z.GetLong(AirAtKey, 0L) / 1000.0;
                float rested = ZNet.instance != null && at > 0.0 ? (float)(ZNet.instance.GetTimeSeconds() - at) : 0f;
                _air = Mathf.Clamp(saved + Mathf.Max(0f, rested) * Max / Mathf.Max(1f, Plugin.DrakeRestSeconds.Value), 0f, Max);
            }
            _tired = _air < Max * 0.3f;
            _speedOf = c;
            _slow = c.m_flySlowSpeed;
            _fast = c.m_flyFastSpeed;
            _jog0 = c.m_speed;
            _run0 = c.m_runSpeed;
            _acc0 = c.m_acceleration;
            _onFoot = Altitude(c) < LandAt;
            if (_onFoot) { if (c.IsFlying()) c.Land(); }
            else if (!c.IsFlying()) c.TakeOff();
            Plugin.Debug("[drake] on " + c.name + ": flight " + _air.ToString("0") + "/" + Max.ToString("0") + " s, own speeds " + _slow.ToString("0.0") + "/" + _fast.ToString("0.0"));
        }

        // it has no attack on the ground: a drake standing under its rider takes off first (true = it just did)
        internal static bool TakeOffToAttack(Character c)
        {
            if (!_onFoot && c.IsFlying()) return false;
            if (_tired) return false;
            _onFoot = false;
            c.TakeOff();
            return true;
        }

        internal static bool Tired { get { return _tired; } }

        // Each attack uses AttackCost seconds of the flight bar (30 s in all). The bar is the same for every drake
        // and every saddle: the saddle's tier plays no part in it. False = not enough left for an attack.
        internal static bool CanSpend(float seconds) { return _air >= seconds - 0.001f; }
        internal static void Spend(float seconds) { _air = Mathf.Max(0f, _air - seconds); }

        internal static float OffAt = -100f;       // when you last got off a drake (to explain a hit just after)

        internal static void OnGetOff(Character c)
        {
            OffAt = Time.time;
            Save(c);
            if (_speedOf == c && _slow >= 0f) { c.m_flySlowSpeed = _slow; c.m_flyFastSpeed = _fast; }
            if (_jog0 >= 0f) { c.m_speed = _jog0; c.m_runSpeed = _run0; }
            if (_acc0 >= 0f) c.m_acceleration = _acc0;
            _onFoot = false;
            _speedOf = null;
            if (c.IsFlying() && Altitude(c) < LowAltitude) c.Land();
        }

        private static void Save(Character c)
        {
            ZNetView nv = Leash.View(c);
            if (nv == null || !nv.IsValid() || !nv.IsOwner() || ZNet.instance == null) return;
            nv.GetZDO().Set(AirKey, _air);
            nv.GetZDO().Set(AirAtKey, (long)(ZNet.instance.GetTimeSeconds() * 1000.0));
        }

        // you can't step off high up: E only works near the ground (a dying drake or a teleport still lets go)
        [HarmonyPatch(typeof(Player), "StopDoodadControl"), HarmonyPrefix]
        private static bool StayOn(Player __instance)
        {
            if (!Plugin.Enabled.Value || __instance != Player.m_localPlayer || __instance.IsDead() || __instance.IsTeleporting()) return true;
            Mount m = __instance.GetDoodadController() as Mount;
            Character c = Mount.RiddenCharacter();
            if (m == null || m != Mount.Riding || c == null || c.IsDead() || !Is(c)) return true;
            if (!(ZInput.GetButtonDown("Use") || ZInput.GetButtonDown("JoyUse"))) return true;
            if (_onFoot && !c.IsFlying()) return true;
            __instance.Message(MessageHud.MessageType.Center, "Land first to get off: fly down close to the ground and hold crouch.");
            return false;
        }

        // ------------------------------------------------------------ flying

        // how high it is above the ground, or above the sea
        internal static float Altitude(Character c)
        {
            Vector3 p = c.transform.position;
            float ground = ZoneSystem.instance != null ? ZoneSystem.instance.GetGroundHeight(p) : p.y - 1000f;
            RaycastHit hit;
            int mask = LayerMask.GetMask("terrain", "Default", "static_solid", "piece");
            if (Physics.Raycast(p + Vector3.up * 0.2f, Vector3.down, out hit, 300f, mask, QueryTriggerInteraction.Ignore))
                ground = Mathf.Max(ground, hit.point.y);
            if (ZoneSystem.instance != null) ground = Mathf.Max(ground, ZoneSystem.instance.m_waterLevel);
            return p.y - ground;
        }

        // how deep the water is at p: the sea's level over the ground (or a dock, a floor) under it; 0 on dry land
        internal static float WaterDepth(Vector3 p)
        {
            if (ZoneSystem.instance == null) return 0f;
            float ground = ZoneSystem.instance.GetGroundHeight(p);
            RaycastHit hit;
            int mask = LayerMask.GetMask("terrain", "Default", "static_solid", "piece");
            Vector3 from = new Vector3(p.x, Mathf.Max(p.y, ZoneSystem.instance.m_waterLevel) + 2f, p.z);
            if (Physics.Raycast(from, Vector3.down, out hit, 400f, mask, QueryTriggerInteraction.Ignore))
                ground = Mathf.Max(ground, hit.point.y);
            return Mathf.Max(0f, ZoneSystem.instance.m_waterLevel - ground);
        }

        // A drake doesn't go out over deep water, flying or on foot (the sea is the serpent's): it stops at the shore.
        // Should it be over the sea anyway (mounted there, pushed out), it can't land, rest or attack there (it would
        // sink, and its rider with it) and only flies back towards shallower water.
        internal static bool OverSea(Character c) { return c != null && WaterDepth(c.transform.position) > DeepWater; }

        // walking that way leads into deep water (a step that gets shallower is fine)
        private static bool SeaAhead(Character c, Vector3 dir) { return SeaAhead(c, dir, 2f); }

        private static bool SeaAhead(Character c, Vector3 dir, float reach)
        {
            Vector3 p = c.transform.position;
            float here = WaterDepth(p);
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) return here > DeepWater;
            float ahead = WaterDepth(p + dir.normalized * reach);
            return ahead > DeepWater && ahead >= here - 0.05f;
        }

        private static bool _wasSea;

        private static bool Typing()
        {
            return Console.IsVisible() || Chat.instance != null && Chat.instance.HasFocus() || TextInput.IsVisible() || Menu.IsVisible();
        }

        internal static void Drive(Character c, Vector3 fwd, Vector3 move, bool run, float dt)
        {
            bool keys = !Typing();
            float up = 0f;
            bool jump = keys && (ZInput.GetButton("Jump") || ZInput.GetButton("JoyJump"));
            bool crouch = keys && (ZInput.GetButton("Crouch") || ZInput.GetButton("JoyCrouch"));
            bool heavy = Mount.TooHeavy();
            if (heavy)
            {
                if (jump || move.sqrMagnitude > 0.01f) Say("You carry too much: your drake won't " + (_onFoot ? "walk or fly." : "fly on: it comes down."));
                jump = false;
                if (_onFoot || !OverSea(c)) move = Vector3.zero;   // over the sea it may still go back to the shore
            }
            if (jump) up += 1f;
            if (crouch) up -= 1f;

            Vector3 wish = Quaternion.LookRotation(fwd) * new Vector3(move.x, 0f, move.z);

            // landed: it walks where you steer (the walk loop is DrakeWalk's), rests, and takes off again when you jump
            if (_onFoot)
            {
                if (jump && !_tired) { _onFoot = false; c.TakeOff(); Say("Your drake takes off."); }
                else
                {
                    if (c.IsFlying()) c.Land();
                    _air = Mathf.Min(Max, _air + dt * Max / Mathf.Max(1f, Plugin.DrakeRestSeconds.Value));
                    if (_tired && _air >= Max - 0.01f) { _tired = false; Say("Your drake has rested: it can fly again."); }
                    c.m_speed = Plugin.DrakeGroundSpeed.Value * SpeedOf(c);
                    c.m_runSpeed = c.m_speed;   // on foot it doesn't run: its walk is its top speed
                    c.m_acceleration = FootAccel;   // no slow start: it walks (or runs) at its full pace from the first step
                    // sideways it steps at 70% of its pace forward
                    Vector3 local = new Vector3(move.x, 0f, move.z);
                    if (local.sqrMagnitude > 1f) local.Normalize();
                    local.x *= SideShare;
                    Vector3 onFoot = Quaternion.LookRotation(fwd) * local;
                    if (SeaAhead(c, onFoot)) { onFoot = Vector3.zero; Say("Your drake won't go into deep water: it senses sea serpents watching from below."); }
                    c.SetLookDir(fwd, 0f);
                    c.SetMoveDir(onFoot);
                    c.SetRun(false);
                    if (Time.time > _nextSave) { _nextSave = Time.time + 2f; Save(c); }
                    return;
                }
            }
            if (_acc0 >= 0f) c.m_acceleration = _acc0;   // in the air: its own easing
            if (!c.IsFlying()) c.TakeOff();
            c.m_flySlowSpeed = Plugin.DrakeSpeed.Value * SpeedOf(c);
            c.m_flyFastSpeed = Plugin.DrakeSprintSpeed.Value * SpeedOf(c);

            float alt = Altitude(c);
            bool high = alt > LowAltitude;
            bool sea = OverSea(c);
            if (sea != _wasSea)
            {
                _wasSea = sea;
                if (sea) Say("Sea serpents are watching from below: your drake turns back to the shore.");
            }
            if (sea && alt < LandAt + 1f) up = Mathf.Max(up, 0.5f);   // it keeps clear of the waves
            if (sea && crouch && alt < LandAt + 1.5f) Say("Your drake can't land on deep water: fly to the shore.");
            // down, close to the ground: it lands
            if (alt < LandAt && crouch && !sea)
            {
                _onFoot = true;
                c.Land();
                Say("Your drake lands. It walks now: jump to take off.");
                return;
            }

            // the flight bar: it empties all the time the drake is in the air (same rate walking pace or sprint, high or low)
            // and only fills on the ground. Empty: the drake comes down by itself, lands, and flies again once the bar is full.
            _air = Mathf.Max(0f, _air - dt);
            if (_air <= 0f && !_tired) { _tired = true; Say("Your drake is tired: it comes down to rest."); }
            if (_tired && sea) Say("Your drake is tired: fly to land so it can rest.");
            else if (_tired)
            {
                up = -0.6f;
                if (alt < LandAt + 0.3f)
                {
                    _onFoot = true;
                    c.Land();
                    Say("Your drake lands to rest. It flies again once its bar is full.");
                    return;
                }
            }
            if (heavy && !sea)
            {
                up = -0.6f;
                if (alt < LandAt + 0.3f)
                {
                    _onFoot = true;
                    c.Land();
                    Say("Your drake lands: you carry too much to fly.");
                    return;
                }
            }
            float ceiling = Plugin.DrakeMaxAltitude.Value;
            if (alt > ceiling + 4f) up = Mathf.Min(up, -0.4f);   // over a cliff edge: sink back under the ceiling
            else if (alt > ceiling) up = Mathf.Min(up, 0f);

            Vector3 flat = Quaternion.LookRotation(fwd) * new Vector3(move.x, 0f, move.z);
            if (flat.sqrMagnitude > 1f) flat.Normalize();
            // it stops at the shore: no flying on towards deep water (looking further ahead the faster it goes)
            if (flat.sqrMagnitude > 0.01f && SeaAhead(c, flat, Mathf.Max(3f, c.GetVelocity().magnitude * 0.8f)))
            {
                flat = Vector3.zero;
                Say("Your drake won't fly out over deep water: it senses sea serpents watching from below.");
            }
            Vector3 dir = flat + Vector3.up * up * ClimbShare;
            c.SetLookDir(fwd, 0f);
            c.SetMoveDir(dir);
            c.SetRun(run && flat.sqrMagnitude > 0.01f);

            if (Time.time > _nextSave) { _nextSave = Time.time + 2f; Save(c); }
        }

        // its attack: only near the ground, unless AttackInAir is on
        internal static bool MayAttack(Character c)
        {
            return Plugin.DrakeAttackInAir.Value || Altitude(c) < LowAltitude + 1f;
        }

        // hand feeding a drake rests it at once (on the drake's owner)
        internal static void Fed(Character c, ZNetView nv)
        {
            if (!Is(c) || nv == null || !nv.IsValid() || ZNet.instance == null) return;
            nv.GetZDO().Set(AirKey, Max);
            nv.GetZDO().Set(AirAtKey, (long)(ZNet.instance.GetTimeSeconds() * 1000.0));
            if (Mount.RiddenCharacter() == c) { _air = Max; _tired = false; }
        }

        private static float _nextSay;
        private static void Say(string s)
        {
            Player p = Player.m_localPlayer;
            if (p == null || Time.time < _nextSay) return;
            _nextSay = Time.time + 1f;
            p.Message(MessageHud.MessageType.Center, s);
        }

        // ------------------------------------------------------------ its flying pose
        // The drake's model is built standing up, tail hanging down; only its animation lays it flat. So the
        // saddle editor and the seat use the drake in its flying pose instead: a hidden copy, animated a moment.

        private static readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, Matrix4x4>> Rest =
            new System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, Matrix4x4>>();

        // A copy of only the prefab's model (the part with the animator and the bones, never the creature
        // itself: an awake copy of the whole creature joins the game's creature list and breaks the health
        // bars), under an empty root named and placed like the prefab's, posed in flight.
        // Returns that root (or null); destroy 'holder' when done.
        internal static GameObject PosedCopy(GameObject prefab, out GameObject holder)
        {
            holder = new GameObject("Beastkeeper_PoseTmp");
            holder.SetActive(false);
            holder.transform.position = new Vector3(0f, -5000f, 0f);
            Animator src = prefab.GetComponentInChildren<Animator>(true);
            if (src == null || src.gameObject == prefab) { Plugin.Debug("[drake] " + prefab.name + ": no model part to pose"); return null; }
            GameObject g = new GameObject(prefab.name);
            g.transform.SetParent(holder.transform, false);
            // the empty parents between the creature and its model, same names and places, so bone paths match
            Transform parent = g.transform;
            System.Collections.Generic.List<Transform> chain = new System.Collections.Generic.List<Transform>();
            for (Transform u = src.transform.parent; u != null && u != prefab.transform; u = u.parent) chain.Add(u);
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                GameObject e = new GameObject(chain[i].name);
                e.transform.SetParent(parent, false);
                e.transform.localPosition = chain[i].localPosition;
                e.transform.localRotation = chain[i].localRotation;
                e.transform.localScale = chain[i].localScale;
                parent = e.transform;
            }
            GameObject model = Object.Instantiate(src.gameObject, parent, false);
            model.name = src.gameObject.name;
            model.transform.localPosition = src.transform.localPosition;
            model.transform.localRotation = src.transform.localRotation;
            model.transform.localScale = src.transform.localScale;
            foreach (MonoBehaviour mb in model.GetComponentsInChildren<MonoBehaviour>(true)) Object.DestroyImmediate(mb);
            if (model.GetComponentInChildren<Character>(true) != null) { Plugin.Log.LogWarning("[drake] model part of " + prefab.name + " is a creature: not posed"); return null; }
            foreach (AudioSource a in model.GetComponentsInChildren<AudioSource>(true)) Object.DestroyImmediate(a);
            foreach (Light l in model.GetComponentsInChildren<Light>(true)) Object.DestroyImmediate(l);
            foreach (Collider col in model.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(col);
            foreach (Rigidbody rb in model.GetComponentsInChildren<Rigidbody>(true)) Object.DestroyImmediate(rb);
            holder.SetActive(true);
            Animator an = model.GetComponent<Animator>();
            if (an != null)
            {
                an.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                foreach (AnimatorControllerParameter par in an.parameters)
                    if (par.type == AnimatorControllerParameterType.Bool && par.name == "flying") an.SetBool("flying", true);
                for (int i = 0; i < 30; i++) an.Update(0.1f);
                Plugin.Debug("[drake] posed " + prefab.name + " in its flying pose");
            }
            else Plugin.Debug("[drake] " + prefab.name + " has no animator: built pose");
            return g;
        }

        private static string PathOf(Transform root, Transform t)
        {
            string s = "";
            for (Transform u = t; u != null && u != root; u = u.parent) s = s.Length == 0 ? u.name : u.name + "/" + s;
            return s;
        }

        // where 'bone' sits in the drake's own frame in its flying pose (false: not a drake, or no such bone)
        internal static bool RestOf(Character c, Transform bone, out Matrix4x4 m)
        {
            m = Matrix4x4.identity;
            if (!Is(c) || bone == null || ZNetScene.instance == null) return false;
            string prefab = Utils.GetPrefabName(c.gameObject);
            System.Collections.Generic.Dictionary<string, Matrix4x4> bones;
            if (!Rest.TryGetValue(prefab, out bones))
            {
                bones = new System.Collections.Generic.Dictionary<string, Matrix4x4>();
                GameObject p = ZNetScene.instance.GetPrefab(prefab);
                if (p != null)
                {
                    GameObject holder;
                    GameObject g = PosedCopy(p, out holder);
                    try
                    {
                        if (g != null)
                        {
                        Matrix4x4 toRoot = g.transform.worldToLocalMatrix;
                        foreach (Transform t in g.GetComponentsInChildren<Transform>(true))
                            bones[PathOf(g.transform, t)] = toRoot * t.localToWorldMatrix;
                        }
                    }
                    finally { Object.Destroy(holder); }
                }
                Rest[prefab] = bones;
            }
            return bones.TryGetValue(PathOf(c.transform, bone), out m);
        }

        // ------------------------------------------------------------ the flight bar

        private static GameObject _bar;
        private static GuiBar[] _fills;

        // The flight bar, drawn by the mod itself: one section for every 5 seconds of flight (6 for 30 s), above the
        // stamina bar. Blue; red while the drake is tired (it must fill up before it flies again).
        private const float SectionSeconds = 5f, SideShare = 0.7f;
        private static Texture2D _px;

        internal static void DrawBar()
        {
            Character c = Mount.RiddenCharacter();
            if (c == null || !Is(c) || Hud.instance == null || Event.current == null || Event.current.type != EventType.Repaint) return;
            if (_px == null) { _px = new Texture2D(1, 1); _px.SetPixel(0, 0, Color.white); _px.Apply(); Object.DontDestroyOnLoad(_px); }
            float s = Screen.height / 1080f;
            int n = Mathf.Max(1, Mathf.RoundToInt(Max / SectionSeconds));
            float w = 54f * s, h = 12f * s, gap = 4f * s;
            float total = n * w + (n - 1) * gap;
            float x0 = (Screen.width - total) / 2f;
            float y = Screen.height * 0.845f;
            RectTransform src = Hud.instance.m_staminaBar2Root;
            if (src != null) y = Screen.height - src.position.y - 58f * src.lossyScale.y - h / 2f;   // just above the stamina bar
            Color old = GUI.color;
            Color fill = _tired ? new Color(0.9f, 0.32f, 0.26f) : new Color(0.5f, 0.85f, 1f);
            for (int i = 0; i < n; i++)
            {
                Rect r = new Rect(x0 + i * (w + gap), y, w, h);
                GUI.color = new Color(0f, 0f, 0f, 0.7f);
                GUI.DrawTexture(new Rect(r.x - 2f, r.y - 2f, r.width + 4f, r.height + 4f), _px);
                GUI.color = new Color(0.2f, 0.22f, 0.25f, 0.9f);
                GUI.DrawTexture(r, _px);
                float k = Mathf.Clamp01((_air - i * SectionSeconds) / SectionSeconds);
                if (k > 0f)
                {
                    GUI.color = fill;
                    GUI.DrawTexture(new Rect(r.x, r.y, r.width * k, r.height), _px);
                }
            }
            GUI.color = old;
        }

        internal static void UpdateBar()
        {
            if (_bar != null) { Object.Destroy(_bar); _bar = null; }   // the old copy of the stamina bar is gone
        }

        private static void OldUpdateBar()
        {
            Character c = Mount.RiddenCharacter();
            bool show = c != null && Is(c) && Hud.instance != null;
            if (!show) { if (_bar != null && _bar.activeSelf) _bar.SetActive(false); return; }
            if (_bar == null && !MakeBar()) return;
            if (!_bar.activeSelf) _bar.SetActive(true);
            float f = _air / Max;
            Color col = _tired ? new Color(0.85f, 0.35f, 0.3f) : new Color(0.55f, 0.85f, 1f);
            foreach (GuiBar g in _fills) if (g != null) { g.SetMaxValue(1f); g.SetValue(f); g.SetColor(col); }   // 0..1, not the player's stamina
            RectTransform src = Hud.instance.m_staminaBar2Root;
            RectTransform rt = _bar.GetComponent<RectTransform>();
            if (src != null && rt != null) rt.anchoredPosition = src.anchoredPosition + new Vector2(0f, 46f);
        }

        // a copy of the game's stamina bar, in the drake's colour, just above it
        private static bool MakeBar()
        {
            RectTransform src = Hud.instance.m_staminaBar2Root;
            if (src == null) return false;
            _bar = Object.Instantiate(src.gameObject, src.parent, false);
            _bar.name = "Beastkeeper_DrakeFlight";
            foreach (Animator a in _bar.GetComponentsInChildren<Animator>(true)) Object.Destroy(a);
            foreach (TMPro.TMP_Text tx in _bar.GetComponentsInChildren<TMPro.TMP_Text>(true)) tx.gameObject.SetActive(false);
            foreach (CanvasGroup g in _bar.GetComponentsInChildren<CanvasGroup>(true)) g.alpha = 1f;
            foreach (Transform t in _bar.GetComponentsInChildren<Transform>(true))
                if (t.GetComponent<TMPro.TMP_Text>() == null) t.gameObject.SetActive(true);
            foreach (UnityEngine.UI.Graphic gr in _bar.GetComponentsInChildren<UnityEngine.UI.Graphic>(true))
            {
                if (gr is TMPro.TMP_Text) continue;
                Color col = gr.color; col.a = 1f; gr.color = col;
                gr.enabled = true;
            }
            _fills = _bar.GetComponentsInChildren<GuiBar>(true);
            // the copy was made while the stamina bar was part empty, so it took that width as its full width:
            // give it the real full width, or it never fills up
            GuiBar srcBar = src.GetComponentInChildren<GuiBar>(true);
            System.Reflection.FieldInfo fw = AccessTools.Field(typeof(GuiBar), "m_width");
            System.Reflection.FieldInfo fb = AccessTools.Field(typeof(GuiBar), "m_bar");
            float full = srcBar != null && fw != null ? (float)fw.GetValue(srcBar) : 0f;
            if (full > 1f && fb != null)
                foreach (GuiBar g in _fills)
                {
                    RectTransform br = g != null ? fb.GetValue(g) as RectTransform : null;
                    if (br == null) continue;
                    Vector2 sd = br.sizeDelta;
                    sd.x = full;
                    br.sizeDelta = sd;
                    fw.SetValue(g, full);
                }
            Plugin.Debug("[drake] flight bar made (" + _fills.Length + " fills)");
            return true;
        }
    }
}
