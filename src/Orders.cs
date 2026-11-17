using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace Beastkeeper
{
    // Orders for your favorite mount, else (not near) for the animal you rode last (user, 2026-11-17): hold OrderKey for a wheel, move the mouse to an order and
    // let go of the key (in the middle: nothing).
    //  - Stay:   it stays where it is now (it still defends itself there), even far from a totem.
    //  - Follow: it follows you everywhere, near a totem too.
    //  - Attack: it follows you and fights: first whatever you are fighting (the last enemy you hit, for a few
    //            seconds), else the nearest enemy near you that is out for blood (alerted). It goes no further than
    //            ChaseLimit from you, and follows you again when there is no one left.
    // No order (the start): as before, it follows you away from totems and stays at a totem near one.
    // The order is kept on the animal.
    [HarmonyPatch]
    internal static class Orders
    {
        internal const int None = 0, Stay = 1, Follow = 2, Attack = 3;
        private static readonly int OrderKey = "Beastkeeper_order".GetStableHashCode();
        private static readonly int StayKey = "Beastkeeper_stayAt".GetStableHashCode();
        private const float FoeMemory = 8f;   // seconds your last hit counts as "what you are fighting"
        private const float StayGap = 2.5f;
        private static readonly FieldInfo TargetTimer = AccessTools.Field(typeof(MonsterAI), "m_updateTargetTimer");
        private static readonly MethodInfo SetTargetM = AccessTools.Method(typeof(MonsterAI), "SetTarget", new[] { typeof(Character) });

        private static Character _myFoe;
        private static float _myFoeAt;

        // Whose order it is: the player who gave it (user, 2026-11-17: the orders go to your favorite, else to the
        // animal you rode last). Your favorite's orders from before this was kept count as yours.
        private static readonly int OrderByKey = "Beastkeeper_orderBy".GetStableHashCode();

        internal static int Of(Character c)
        {
            ZNetView nv = Leash.View(c);
            Player me = Player.m_localPlayer;
            if (nv == null || !nv.IsValid() || me == null) return None;
            ZDO z = nv.GetZDO();
            int o = z.GetInt(OrderKey, None);
            if (o == None) return None;
            long by = z.GetLong(OrderByKey, 0L);
            return by == me.GetPlayerID() || (by == 0L && Favorite.IsFavorite(c)) ? o : None;
        }

        // the animal you got on last (not a sea serpent)
        private static ZDOID _lastRidden = ZDOID.None;
        internal static void Rode(Character c)
        {
            ZNetView nv = Leash.View(c);
            if (nv != null && nv.IsValid() && !Sea.IsSeaMount(c)) _lastRidden = nv.GetZDO().m_uid;
        }

        // who the wheel orders: your favorite if it is near, else the animal you rode last if that one is near
        // 1) the tamed animal you are looking at, 2) your favorite, 3) the one you rode last
        private static Character Whom(Player p)
        {
            GameObject hover = p.GetHoverObject();
            Character look = hover != null ? hover.GetComponentInParent<Character>() : null;
            if (look != null && !look.IsPlayer() && !look.IsDead() && look.IsTamed() && look.GetComponent<Mount>() != null && !Sea.IsSeaMount(look)) return look;
            Character fav = Favorite.Loaded();
            if (fav != null && !fav.IsDead() && Flat(fav.transform.position, p.transform.position) <= 100f) return fav;
            if (_lastRidden.IsNone() || ZNetScene.instance == null) return null;
            GameObject go = ZNetScene.instance.FindInstance(_lastRidden);
            Character c = go != null ? go.GetComponent<Character>() : null;
            if (c == null || c.IsDead() || !c.IsTamed() || Flat(c.transform.position, p.transform.position) > 100f) return null;
            return c;
        }

        internal static string Name(int o) { return o == Stay ? "Stay" : o == Follow ? "Follow" : o == Attack ? "Attack" : ""; }

        // ------------------------------------------------------------ what the animal does (Home.Run, Defend)

        // A Stay spot this far away is dropped (user, 2026-11-17: a drake told to stay, then taken through a portal on
        // the lasso, flew off after a relog back to the spot on the other side of the portal and was lost).
        private const float StayMaxAway = 100f;

        internal static bool StayAt(Character c, out Vector3 at)
        {
            at = Vector3.zero;
            if (Of(c) != Stay) return false;
            at = Leash.View(c).GetZDO().GetVec3(StayKey, c.transform.position);
            Vector3 d = c.transform.position - at;
            d.y = 0f;
            if (d.magnitude > StayMaxAway)
            {
                DropStay(c, "its Stay spot is " + d.magnitude.ToString("0") + " m away");
                return false;
            }
            return true;
        }

        // Takes a Stay order off the animal: it stays where it is now, with no order.
        internal static void DropStay(Character c, string why)
        {
            ZNetView nv = Leash.View(c);
            if (nv == null || !nv.IsValid()) return;
            ZDO z = nv.GetZDO();
            if (z.GetInt(OrderKey, None) != Stay) return;
            if (!nv.IsOwner()) nv.ClaimOwnership();
            z.Set(OrderKey, None);
            z.Set(StayKey, Vector3.zero);
            Plugin.Log.LogInfo("[orders] " + c.GetHoverName() + ": Stay order dropped (" + why + ")");
        }

        internal static void Hold(BaseAI ai, Character c, Vector3 at, float dt)
        {
            Vector3 d = c.transform.position - at;
            d.y = 0f;
            if (d.magnitude > StayGap) Leash.MoveTo(ai, dt, at, d.magnitude > 12f);
            else ai.StopMoving();
        }

        // Attack order: an enemy to fight now? Then the game's own fighting goes for it.
        internal static bool Fight(BaseAI ai, Character c)
        {
            MonsterAI mai = ai as MonsterAI;
            if (mai == null || Of(c) != Attack || !Defend.CanFight(c)) return false;
            Player me = Player.m_localPlayer;
            if (me == null || me.IsDead()) return false;
            Character foe = Pick(c, me);
            if (foe == null) return false;
            Defend.Ready(ai, c);
            if (mai.GetTargetCreature() != foe)
            {
                if (SetTargetM != null) SetTargetM.Invoke(mai, new object[] { foe });
                Plugin.Debug("[orders] " + c.name + " attacks " + foe.name + (foe == _myFoe ? " (the one you fight)" : ""));
            }
            if (TargetTimer != null) TargetTimer.SetValue(mai, 2f);   // the game doesn't pick another one meanwhile
            return true;
        }

        private static Character Pick(Character c, Player me)
        {
            float reach = Plugin.ChaseLimit.Value;
            if (_myFoe != null && !_myFoe.IsDead() && Time.time - _myFoeAt < FoeMemory && Flat(_myFoe.transform.position, me.transform.position) <= reach + 10f)
                return _myFoe;
            Character best = null;
            float bestD = float.MaxValue;
            foreach (Character o in Defend.Threats())
            {
                if (o == null || o == c || o.IsDead() || !BaseAI.IsEnemy(c, o)) continue;
                BaseAI oai = o.GetBaseAI();
                MonsterAI omai = oai as MonsterAI;
                bool angry = oai != null && (oai.IsAlerted() || (omai != null && omai.GetTargetCreature() != null));
                if (!angry) continue;
                float d = Flat(o.transform.position, me.transform.position);
                if (d > Plugin.DefendRange.Value * 2f || d > reach) continue;
                if (d < bestD) { bestD = d; best = o; }
            }
            return best;
        }

        // what you are fighting: the last creature you hit
        [HarmonyPatch(typeof(Character), "Damage"), HarmonyPrefix]
        private static void YouHit(Character __instance, HitData hit)
        {
            if (hit == null || __instance == null || __instance.IsPlayer() || __instance.IsTamed()) return;
            if (Player.m_localPlayer == null || hit.GetAttacker() != Player.m_localPlayer) return;
            _myFoe = __instance;
            _myFoeAt = Time.time;
        }

        private static float Flat(Vector3 a, Vector3 b) { a.y = 0f; b.y = 0f; return Vector3.Distance(a, b); }

        // ------------------------------------------------------------ giving an order

        private static void Give(Player p, Character c, int order)
        {
            ZNetView nv = Leash.View(c);
            if (nv == null || !nv.IsValid()) return;
            if (!nv.IsOwner()) nv.ClaimOwnership();
            ZDO z = nv.GetZDO();
            z.Set(OrderKey, order);
            z.Set(OrderByKey, p.GetPlayerID());
            string name = c.GetHoverName();
            if (order == Stay)
            {
                z.Set(StayKey, c.transform.position);
                MonsterAI mai = c.GetComponent<MonsterAI>();
                if (mai != null && mai.GetFollowTarget() != null) mai.SetFollowTarget(null);
                BaseAI ai = c.GetBaseAI();
                if (ai != null) ai.StopMoving();
                p.Message(MessageHud.MessageType.Center, name + " stays here.");
            }
            else if (order == Follow) p.Message(MessageHud.MessageType.Center, name + " follows you.");
            else if (!Defend.CanFight(c)) p.Message(MessageHud.MessageType.Center, name + " can't fight: it follows you.");
            else p.Message(MessageHud.MessageType.Center, name + " follows you and attacks your enemies.");
            Plugin.Debug("[orders] " + c.name + ": " + Name(order));
        }

        // ------------------------------------------------------------ the wheel

        internal static bool Open;
        private static Character _who;
        private static Vector2 _aim;
        private static int _hover = -1;
        private const float Travel = 13f;   // how far the mouse moves to the edge (the same as our arrow wheel)
        private static readonly int[] Slots = { Follow, Attack, Stay };            // top, right, left
        private static readonly float[] Angles = { 90f, -30f, 210f };

        internal static void Update()
        {
            Player p = Player.m_localPlayer;
            KeyboardShortcut key = Plugin.OrderKey.Value;
            if (p == null || key.MainKey == KeyCode.None) { Open = false; return; }
            if (!Open)
            {
                if (!key.IsDown()) return;
                if (Chat.instance != null && Chat.instance.HasFocus()) return;
                if (Console.IsVisible() || TextInput.IsVisible() || Menu.IsVisible() || InventoryGui.IsVisible() || Minimap.IsOpen() || TotemWindow.Open) return;
                if (p.IsDead() || p.InCutscene() || p.IsTeleporting()) return;
                if (p.InPlaceMode()) return;   // building with the hammer: Q is left to the build keys (user, 2026-11-17)
                _who = Whom(p);
                if (_who == null)
                {
                    p.Message(MessageHud.MessageType.Center, "Neither your favorite mount nor the animal you rode last is near (press " + Plugin.CallKey.Value + " to call your favorite).");
                    return;
                }
                _aim = Vector2.zero;
                _hover = -1;
                Open = true;
                return;
            }
            if (_who == null || p.IsDead() || Menu.IsVisible() || InventoryGui.IsVisible()) { Open = false; return; }
            _aim += ZInput.GetMouseDelta();
            if (_aim.magnitude > Travel) _aim = _aim.normalized * Travel;
            _hover = Sector();
            if (ZInput.GetKeyDown(KeyCode.Escape, false) || ZInput.GetKeyDown(KeyCode.Mouse1, false)) { Open = false; return; }
            if (Input.GetKey(key.MainKey)) return;   // still held
            Open = false;
            if (_hover >= 0) Give(p, _who, Slots[_hover]);
        }

        private static int Sector()
        {
            if (_aim.magnitude < Travel * 0.3f) return -1;
            float a = Mathf.Atan2(_aim.y, _aim.x) * Mathf.Rad2Deg;
            int best = -1;
            float bestD = 999f;
            for (int i = 0; i < Angles.Length; i++)
            {
                float d = Mathf.Abs(Mathf.DeltaAngle(a, Angles[i]));
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        private static Texture2D _white;
        private static GUIStyle _style, _title;

        internal static void OnGUI()
        {
            if (!Open || _who == null) return;
            if (_white == null) { _white = new Texture2D(1, 1); _white.SetPixel(0, 0, Color.white); _white.Apply(); }
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 20, fontStyle = FontStyle.Bold };
                _title = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 18, fontStyle = FontStyle.Bold };
            }
            Vector2 c = new Vector2(Screen.width / 2f, Screen.height / 2f);
            float R = 120f, w = 130f, h = 46f;
            int now = Of(_who);
            Label(new Rect(c.x - 200f, c.y - R - 80f, 400f, 28f), _who.GetHoverName(), _title, new Color(1f, 0.85f, 0.4f));
            for (int i = 0; i < Slots.Length; i++)
            {
                float a = Angles[i] * Mathf.Deg2Rad;
                Vector2 at = c + new Vector2(Mathf.Cos(a), -Mathf.Sin(a)) * R;
                Rect box = new Rect(at.x - w / 2f, at.y - h / 2f, w, h);
                bool hover = i == _hover;
                GUI.color = hover ? new Color(0.55f, 0.4f, 0.12f, 0.92f) : new Color(0f, 0f, 0f, 0.7f);
                GUI.DrawTexture(box, _white);
                if (Slots[i] == now)
                {
                    GUI.color = new Color(1f, 0.85f, 0.35f, 1f);
                    GUI.DrawTexture(new Rect(box.x, box.yMax - 4f, box.width, 4f), _white);
                }
                Label(box, Name(Slots[i]), _style, hover ? Color.white : new Color(0.9f, 0.85f, 0.75f));
            }
            Vector2 dot = c + new Vector2(_aim.x, -_aim.y) / Travel * (R * 0.6f);
            GUI.color = new Color(1f, 0.9f, 0.5f, 0.9f);
            GUI.DrawTexture(new Rect(dot.x - 5f, dot.y - 5f, 10f, 10f), _white);
            GUI.color = Color.white;
        }

        private static void Label(Rect r, string text, GUIStyle s, Color col)
        {
            GUI.color = new Color(0f, 0f, 0f, 0.8f);
            GUI.Label(new Rect(r.x + 2f, r.y + 2f, r.width, r.height), text, s);
            GUI.color = col;
            GUI.Label(r, text, s);
            GUI.color = Color.white;
        }

        // the camera and attacks stay still while the wheel is open
        [HarmonyPatch(typeof(Player), "SetMouseLook"), HarmonyPrefix]
        private static void StillCamera(ref Vector2 __0) { if (Open) __0 = Vector2.zero; }

        [HarmonyPatch(typeof(Player), "SetControls"), HarmonyPrefix, HarmonyPriority(Priority.Last)]
        private static void NoAttack(ref bool attack, ref bool attackHold, ref bool secondaryAttack, ref bool secondaryAttackHold, ref bool block, ref bool blockHold)
        {
            if (!Open) return;
            attack = attackHold = secondaryAttack = secondaryAttackHold = block = blockHold = false;
        }
    }
}
