using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Beastkeeper
{
    // The drake as a flying mount (agreed with the user, 2026-11-11):
    // - wild drakes can't be caught: tamed ones hatch from Moder's eggs (DrakeEgg.cs), as babies that grow up;
    // - it only takes the Drake Saddle, made with Moder's dragon tears, so flying starts after Moder;
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

        private static float Max { get { return Mathf.Max(1f, Plugin.DrakeFlightSeconds.Value); } }

        // ------------------------------------------------------------ size

        // tamed drakes grow, so a rider fits; wild ones keep the game's size; a hatched baby starts small
        internal static void KeepSize(Character c)
        {
            if (!Is(c)) return;
            float want = c.IsTamed() && Plugin.Enabled.Value ? Plugin.DrakeSize.Value * Mathf.Lerp(BabyShare, 1f, Grown(c)) : 1f;
            if (Mathf.Abs(c.transform.localScale.x - want) > 0.001f) c.transform.localScale = Vector3.one * want;
        }

        // ------------------------------------------------------------ getting on and off

        internal static void OnRide(Character c)
        {
            KeepSafe(1f);
            KeepSize(c);
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

        internal static float OffAt = -100f;       // when you last got off a drake (to explain a hit just after)

        internal static void OnGetOff(Character c)
        {
            OffAt = Time.time;
            Save(c);
            if (_speedOf == c && _slow >= 0f) { c.m_flySlowSpeed = _slow; c.m_flyFastSpeed = _fast; }
            if (_jog0 >= 0f) { c.m_speed = _jog0; c.m_runSpeed = _run0; }
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
            if (jump) up += 1f;
            if (crouch) up -= 1f;

            // landed: it walks where you steer (the walk loop is DrakeWalk's), rests, and takes off again when you jump
            if (_onFoot)
            {
                if (jump && !_tired) { _onFoot = false; c.TakeOff(); Say("Your drake takes off."); }
                else
                {
                    if (c.IsFlying()) c.Land();
                    _air = Mathf.Min(Max, _air + dt * Max / Mathf.Max(1f, Plugin.DrakeRestSeconds.Value));
                    if (_tired && _air >= Max * 0.3f) { _tired = false; Say("Your drake has rested: it can fly again."); }
                    c.m_speed = Plugin.DrakeGroundSpeed.Value;
                    c.m_runSpeed = Plugin.DrakeGroundSprint.Value;
                    Vector3 onFoot = Quaternion.LookRotation(fwd) * new Vector3(move.x, 0f, move.z);
                    if (onFoot.sqrMagnitude > 1f) onFoot.Normalize();
                    c.SetLookDir(fwd, 0f);
                    c.SetMoveDir(onFoot);
                    c.SetRun(run && onFoot.sqrMagnitude > 0.01f);
                    if (Time.time > _nextSave) { _nextSave = Time.time + 2f; Save(c); }
                    return;
                }
            }
            if (!c.IsFlying()) c.TakeOff();
            c.m_flySlowSpeed = Plugin.DrakeSpeed.Value;
            c.m_flyFastSpeed = Plugin.DrakeSprintSpeed.Value;

            float alt = Altitude(c);
            bool high = alt > LowAltitude;
            // down, close to the ground: it lands
            if (alt < LandAt && crouch)
            {
                _onFoot = true;
                c.Land();
                Say("Your drake lands. It walks now: jump to take off.");
                return;
            }

            // the flight bar: empties up high (faster when sprinting), fills near the ground
            if (high) _air -= dt * (run ? 1.5f : 1f);
            else if (up <= 0f) _air += dt * Max / Mathf.Max(1f, Plugin.DrakeRestSeconds.Value);
            _air = Mathf.Clamp(_air, 0f, Max);
            if (_air <= 0f && !_tired) { _tired = true; Say("Your drake is tired: it glides down to rest."); }
            if (_tired && _air >= Max * 0.3f) { _tired = false; Say("Your drake has rested: it can climb again."); }

            if (_tired)
            {
                if (high) up = -0.6f;              // it glides down on its own
                else up = Mathf.Min(up, 0f);       // and stays low until rested
            }
            float ceiling = Plugin.DrakeMaxAltitude.Value;
            if (alt > ceiling + 4f) up = Mathf.Min(up, -0.4f);   // over a cliff edge: sink back under the ceiling
            else if (alt > ceiling) up = Mathf.Min(up, 0f);

            Vector3 flat = Quaternion.LookRotation(fwd) * new Vector3(move.x, 0f, move.z);
            if (flat.sqrMagnitude > 1f) flat.Normalize();
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

        internal static void UpdateBar()
        {
            Character c = Mount.RiddenCharacter();
            bool show = c != null && Is(c) && Hud.instance != null;
            if (!show) { if (_bar != null && _bar.activeSelf) _bar.SetActive(false); return; }
            if (_bar == null && !MakeBar()) return;
            if (!_bar.activeSelf) _bar.SetActive(true);
            float f = _air / Max;
            Color col = _tired ? new Color(0.85f, 0.35f, 0.3f) : new Color(0.55f, 0.85f, 1f);
            foreach (GuiBar g in _fills) if (g != null) { g.SetValue(f); g.SetColor(col); }
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
            Plugin.Debug("[drake] flight bar made (" + _fills.Length + " fills)");
            return true;
        }
    }
}
