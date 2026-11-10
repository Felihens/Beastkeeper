using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Beastkeeper
{
    // Holding the lasso: hold the right mouse button to aim (you raise your arm and swing the loop over your
    // head), left mouse button throws. The loop really flies: it leaves your hand in an arc, and only an animal
    // it touches is caught. A miss falls to the ground (or stops where the rope runs out) and you reel it back.
    // A quick right-click without throwing reels the rope back in: you let go of the animal under the
    // crosshair, or the last one you caught (during a catch: of the animal you fight).
    // The usual attack is switched off while the lasso is in hand.
    [HarmonyPatch]
    internal static class Throw
    {
        private static readonly FieldInfo ZAnim = AccessTools.Field(typeof(Character), "m_zanim");
        private const float Cooldown = 1.2f;
        private const float Windup = 0.22f;      // from the click to the loop leaving the hand
        internal const float LoopRadius = 0.45f;  // size of the flying loop (also how close it must pass)
        internal const float Gravity = 9.81f;
        private const int LoopPoints = 20;

        private const float ClickTime = 0.25f;
        private static bool _aiming, _wasBlock, _threw, _flying;
        private static float _blockDown;
        private static float _ready, _throwPose;
        private static GUIStyle _label;


        internal static bool Holding(Player p)
        {
            if (p == null) return false;
            foreach (ItemDrop.ItemData it in p.GetInventory().GetAllItems())
                if (Lasso.IsLasso(it) && p.IsItemEquiped(it)) return true;
            return false;
        }

        [HarmonyPatch(typeof(Player), "SetControls"), HarmonyPrefix]
        private static void Controls(Player __instance, ref Vector3 movedir, ref bool attack, ref bool attackHold,
            ref bool secondaryAttack, ref bool secondaryAttackHold, ref bool block, ref bool blockHold, ref bool run, ref bool dodge)
        {
            if (!Plugin.Enabled.Value || __instance != Player.m_localPlayer) return;
            bool held = blockHold || block;
            bool clicked = false;
            if (held && !_wasBlock) { _blockDown = Time.time; _threw = false; }
            if (!held && _wasBlock && !_threw && Time.time - _blockDown < ClickTime) clicked = true;
            _wasBlock = held;

            if (Tug.Active)
            {
                // plant your feet and pull: no walking, attacking or dodging during a catch
                movedir = Vector3.zero;
                attack = attackHold = secondaryAttack = secondaryAttackHold = block = blockHold = run = dodge = false;
                _aiming = false;
                if (clicked && !Tug.AteRightClick)
                {
                    Character fought = TugTarget();
                    Tug.LetGo();
                    if (fought != null) Plugin.Instance.StartCoroutine(Reel(__instance, fought));
                }
                return;
            }
            if (!Holding(__instance)) { _aiming = false; return; }
            _aiming = held && Time.time - _blockDown >= ClickTime * 0.5f;
            if (attack && _aiming) TryThrow(__instance, "attack");
            if (clicked)
            {
                string why;
                Character let = Leash.LetGoOne(__instance, Aimed(__instance, out why));
                if (let != null) Plugin.Instance.StartCoroutine(Reel(__instance, let));
            }
            // while aiming, the game's block pose raises your arm (the loop spins above it); not while throwing
            // ...but not while sneaking: the block pose would stand you up
            bool pose = _aiming && Time.time > _throwPose && !__instance.IsCrouching();
            attack = attackHold = secondaryAttack = secondaryAttackHold = false;
            block = false;
            blockHold = pose;
        }

        // The click is also read here, every frame, so a quick click between two physics steps isn't lost.
        internal static void Update()
        {
            Player p = Player.m_localPlayer;
            bool show = p != null && _aiming && !_flying && !Tug.Active && Holding(p) && !InventoryGui.IsVisible() && !Menu.IsVisible();
            if (show && (ZInput.GetButtonDown("Attack") || ZInput.GetButtonDown("JoyAttack"))) TryThrow(p, "click");
            Spin(p, show && !_flying);
        }

        // ------------------------------------------------------------ the loop over your head

        private static void Spin(Player p, bool on)
        {
            Show.SetSpinning(p, on);   // a flag on your character: every game with the mod draws the loop
        }

        internal static LineRenderer NewLoop(Transform parent, string name)
        {
            LineRenderer l = Rope.NewLine(parent, name);
            l.positionCount = LoopPoints;
            l.loop = true;
            return l;
        }

        // a ring of rope around center; wobble makes it look like soft rope, not a hoop
        internal static void LayLoop(LineRenderer l, Vector3 center, Vector3 normal, float radius, float phase)
        {
            Vector3 a = Vector3.Cross(normal, Mathf.Abs(normal.y) < 0.9f ? Vector3.up : Vector3.forward).normalized;
            Vector3 b = Vector3.Cross(normal, a);
            for (int i = 0; i < LoopPoints; i++)
            {
                float t = i / (float)LoopPoints * Mathf.PI * 2f;
                float r = radius * (1f + 0.08f * Mathf.Sin(t * 3f + phase * 2f));
                l.SetPosition(i, center + (a * Mathf.Cos(t) + b * Mathf.Sin(t)) * r);
            }
        }

        // ------------------------------------------------------------ what we aim at

        private static Character Aimed(Player p, out string why)
        {
            why = "";
            GameCamera cam = GameCamera.instance;
            if (cam == null) return null;
            Vector3 from = cam.transform.position, fwd = cam.transform.forward;
            float range = Plugin.ThrowRange.Value;
            float reach = Vector3.Distance(from, p.transform.position) + range;
            Character best = null;
            float bestScore = float.MaxValue;
            foreach (RaycastHit h in Physics.SphereCastAll(from, 0.6f, fwd, reach))
            {
                Character c = h.collider != null ? h.collider.GetComponentInParent<Character>() : null;
                if (c == null || c == p || c.IsPlayer() || c.IsDead()) continue;
                if (h.distance < bestScore) { bestScore = h.distance; best = c; }
            }
            if (best == null) return null;
            float d = Vector3.Distance(p.transform.position, best.transform.position);
            if (d > range) why = "Too far (" + d.ToString("0") + " m)";
            else if (Leash.IsMine(best)) why = "";
            else if (!Leash.Eligible(best)) why = "The lasso won't hold it";
            else if (!best.IsTamed() && !Plugin.CatchWild.Value) why = "Only tamed animals";
            else if (!best.IsTamed() && Leash.Noticed(best)) why = "It has noticed you";
            return best;
        }

        // where the crosshair points: the first thing the camera ray meets (not you), or far away
        private static Vector3 AimPoint(Player p)
        {
            GameCamera cam = GameCamera.instance;
            if (cam == null) return p.transform.position + p.transform.forward * Plugin.ThrowRange.Value;
            Vector3 from = cam.transform.position, fwd = cam.transform.forward;
            RaycastHit[] hits = Physics.RaycastAll(from, fwd, 80f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            float best = 80f;
            foreach (RaycastHit h in hits)
            {
                if (h.collider == null || h.collider.GetComponentInParent<Player>() == p) continue;
                if (h.distance < best) best = h.distance;
            }
            return from + fwd * best;
        }

        private static void TryThrow(Player p, string how)
        {
            if (Time.time < _ready || _flying) return;
            _ready = Time.time + Cooldown;
            _threw = true;
            _throwPose = Time.time + 0.6f;
            ZSyncAnimation zanim = ZAnim != null ? ZAnim.GetValue(p) as ZSyncAnimation : null;
            if (zanim != null) zanim.SetTrigger("throw_bomb");
            Vector3 aim = AimPoint(p);
            Plugin.Debug("[throw] thrown (" + how + ") toward " + aim.ToString("0.0") + ", " + Vector3.Distance(p.transform.position, aim).ToString("0.0") + " m away");
            Plugin.Instance.StartCoroutine(Fly(p, aim));
        }

        // The loop flies on a real arc: aimed so it would come down on the crosshair, pulled down by gravity,
        // and stopped by the rope's length. It catches the first animal it touches; anything else stops it.
        private static IEnumerator Fly(Player p, Vector3 aim)
        {
            _flying = true;
            Spin(p, false);
            yield return new WaitForSeconds(Windup);
            if (p == null) { _flying = false; yield break; }

            float range = Plugin.ThrowRange.Value;
            float speed = Mathf.Max(5f, Plugin.ThrowSpeed.Value);
            Vector3 pos = Rope.RightHand(p) + Vector3.up * 0.3f;
            Vector3 to = aim - pos;
            float dist = Mathf.Min(to.magnitude, range);
            float time = dist / speed;
            Vector3 vel = to.normalized * speed + Vector3.up * (0.5f * Gravity * time);   // lift that the fall takes back

            int id = UnityEngine.Random.Range(1, int.MaxValue);
            Show.SendStart(id, pos, vel, range);
            LineRenderer rope = Rope.NewLine(null, "Beastkeeper_ThrowRope");
            LineRenderer loop = NewLoop(null, "Beastkeeper_ThrowLoop");
            Character caught = null;
            bool down = false;
            float flown = 0f;
            var hits = new List<RaycastHit>();

            while (flown < 3f && p != null)
            {
                float dt = Time.deltaTime;
                flown += dt;
                Vector3 hand = Rope.RightHand(p);
                Vector3 next = pos + vel * dt;
                vel += Vector3.down * Gravity * dt;

                // the rope runs out: the loop stops going away and drops
                Vector3 out_ = next - hand;
                if (out_.magnitude > range)
                {
                    next = hand + out_.normalized * range;
                    vel = new Vector3(0f, Mathf.Min(vel.y, 0f), 0f);
                }

                // what the loop passes through on the way
                Vector3 step = next - pos;
                if (step.sqrMagnitude > 0.000001f)
                {
                    hits.Clear();
                    hits.AddRange(Physics.SphereCastAll(pos, LoopRadius, step.normalized, step.magnitude, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore));
                    hits.Sort((x, y) => x.distance.CompareTo(y.distance));
                    bool stop = false;
                    foreach (RaycastHit h in hits)
                    {
                        if (h.collider == null) continue;
                        Character c = h.collider.GetComponentInParent<Character>();
                        if (c == p) continue;
                        if (c != null && !c.IsPlayer() && !c.IsDead()) { caught = c; stop = true; break; }
                        // ground, a tree, a wall or another player: the loop falls there
                        if (h.distance <= 0f && h.point == Vector3.zero) continue;   // started inside: ignore
                        next = h.point + h.normal * 0.05f;
                        down = true;
                        stop = true;
                        break;
                    }
                    if (stop) { pos = next; if (caught != null) break; if (down) break; }
                }
                pos = next;

                // under the ground or the water: it landed
                float ground = Ground(pos);
                if (pos.y <= ground) { pos.y = ground; down = true; break; }

                LayLoop(loop, pos, (vel.normalized + Vector3.up).normalized, LoopRadius, flown * 6f);
                Rope.Lay(rope, hand, pos, Mathf.Clamp01(flown * 2f) * 0.4f);
                yield return null;
            }

            if (caught != null && p != null)
            {
                // the loop closes around the neck
                Transform neck = null;
                for (float t = 0f; t < 0.15f && caught != null; t += Time.deltaTime)
                {
                    Vector3 n = Rope.Neck(caught, ref neck);
                    LayLoop(loop, n, Vector3.up, Mathf.Lerp(LoopRadius, 0.18f, t / 0.15f), t * 6f);
                    Rope.Lay(rope, Rope.RightHand(p), n, 0.2f);
                    yield return null;
                }
                Plugin.Debug("[throw] the loop caught " + caught.GetHoverName() + " (" + caught.name + ")");
                if (caught != null) Show.SendEnd(id, caught.GetZDOID(), caught.transform.position);
                Destroy(rope, loop);
                _flying = false;
                if (caught != null && !caught.IsDead()) Leash.OnHit(caught, p);
                yield break;
            }

            Plugin.Debug("[throw] missed: the loop " + (down ? "landed" : "stopped") + " at " + pos.ToString("0.0"));
            Show.SendEnd(id, ZDOID.None, pos);
            // the loop lies on the ground a moment, then you pull it back along the ground
            for (float t = 0f; t < 0.5f && p != null; t += Time.deltaTime)
            {
                LayLoop(loop, pos + Vector3.up * 0.03f, Vector3.up, LoopRadius, 0f);
                Rope.Lay(rope, Rope.RightHand(p), pos, 0.6f);
                yield return null;
            }
            Vector3 start = pos;
            for (float t = 0f; t < 0.45f && p != null; t += Time.deltaTime)
            {
                Vector3 hand = Rope.RightHand(p);
                Vector3 q = Vector3.Lerp(start, hand, t / 0.45f);
                q.y = Mathf.Max(Ground(q) + 0.03f, Mathf.Lerp(start.y, hand.y, Mathf.Pow(t / 0.45f, 3f)));
                LayLoop(loop, q, Vector3.up, Mathf.Lerp(LoopRadius, 0.15f, t / 0.45f), 0f);
                Rope.Lay(rope, hand, q, 0.3f);
                yield return null;
            }
            Destroy(rope, loop);
            _flying = false;
        }

        internal static float Ground(Vector3 at)
        {
            float g = ZoneSystem.instance != null ? ZoneSystem.instance.GetGroundHeight(at) : at.y - 100f;
            float water = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : -1000f;
            return Mathf.Max(g, water);
        }

        private static void Destroy(LineRenderer a, LineRenderer b)
        {
            if (a != null) Object.Destroy(a.gameObject);
            if (b != null) Object.Destroy(b.gameObject);
        }

        private static Character TugTarget()
        {
            foreach (Character c in Character.GetAllCharacters())
                if (Tug.Is(c)) return c;
            return null;
        }

        // the rope comes back from the animal to the hand
        private static IEnumerator Reel(Player p, Character from)
        {
            if (p == null || from == null) yield break;
            LineRenderer line = Rope.NewLine(p.transform, "Beastkeeper_Reel");
            Transform neck = null;
            Vector3 start = Rope.Neck(from, ref neck);
            const float time = 0.3f;
            for (float t = 0f; t < time && p != null; t += Time.deltaTime)
            {
                Vector3 a = Rope.Hand(p);
                Rope.Lay(line, a, Vector3.Lerp(start, a, t / time), 0.6f);
                yield return null;
            }
            if (line != null) Object.Destroy(line.gameObject);
        }

        // ------------------------------------------------------------ aiming label under the crosshair

        internal static void OnGUI()
        {
            if (!_aiming || Tug.Active) return;
            Player p = Player.m_localPlayer;
            if (p == null || InventoryGui.IsVisible() || Menu.IsVisible()) { _aiming = false; return; }
            string why;
            Character c = Aimed(p, out why);
            string text;
            Color col;
            if (c == null) { text = "Lasso: aim at an animal"; col = new Color(1f, 1f, 1f, 0.75f); }
            else
            {
                float d = Vector3.Distance(p.transform.position, c.transform.position);
                if (why.Length > 0) { text = c.GetHoverName() + " - " + why; col = new Color(1f, 0.45f, 0.35f); }
                else if (Leash.IsMine(c)) { text = c.GetHoverName() + " " + d.ToString("0") + " m - on your lasso"; col = new Color(1f, 0.85f, 0.4f); }
                else { text = c.GetHoverName() + " " + d.ToString("0") + " m"; col = new Color(0.55f, 1f, 0.5f); }
            }
            if (_label == null)
            {
                _label = new GUIStyle(GUI.skin.label);
                _label.alignment = TextAnchor.UpperCenter;
                _label.fontSize = 18;
                _label.fontStyle = FontStyle.Bold;
            }
            Rect r = new Rect(Screen.width / 2f - 250f, Screen.height / 2f + 28f, 500f, 30f);
            GUI.color = new Color(0f, 0f, 0f, 0.8f);
            GUI.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), text, _label);
            GUI.color = col;
            GUI.Label(r, text, _label);
            GUI.color = Color.white;
        }
    }
}
