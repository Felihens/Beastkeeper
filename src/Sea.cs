using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Beastkeeper
{
    // Sea mounts (the serpent): caught with the harpoon and the lasso, tamed with the halter, ridden sitting
    // on its head. It swims just under the surface so your head stays out of the water, faster than a ship.
    // It has a food bar of 4 parts: each raw fish fills one, it empties while you ride (FoodMinutes per part),
    // and when it's empty the serpent swims at half speed. You can only get off near land or a ship.
    [HarmonyPatch]
    internal static class Sea
    {
        internal static readonly int FoodKey = "Beastkeeper_seaFood".GetStableHashCode();
        internal const float MaxFood = 4f;
        private static readonly FieldInfo SwimDepth = AccessTools.Field(typeof(Character), "m_swimDepth");
        private static readonly Dictionary<Character, float> LastHarpooned = new Dictionary<Character, float>();
        private static readonly Dictionary<Character, float> OldDepth = new Dictionary<Character, float>();
        private static readonly Dictionary<Character, RigidbodyInterpolation> OldInterp = new Dictionary<Character, RigidbodyInterpolation>();

        internal static bool IsSeaMount(Character c)
        {
            if (c == null) return false;
            string prefab = Utils.GetPrefabName(c.gameObject);
            foreach (string s in (Plugin.SeaMounts.Value ?? "").Split(','))
                if (string.Equals(s.Trim(), prefab, System.StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ---------------------------------------------------------------- harpoon + lasso

        internal static bool Harpooned(Character c)
        {
            if (c == null) return false;
            SEMan se = c.GetSEMan();
            bool on = false;
            if (se != null)
                foreach (StatusEffect e in se.GetStatusEffects())
                    if (e is SE_Harpooned) { on = true; break; }
            if (on)
            {
                if (LastHarpooned.Count > 100) LastHarpooned.Clear();
                LastHarpooned[c] = Time.time;
                return true;
            }
            float t;
            return LastHarpooned.TryGetValue(c, out t) && Time.time - t < 1.5f;
        }

        // ---------------------------------------------------------------- riding

        internal static void OnRide(Character c)
        {
            if (SwimDepth == null || c == null) return;
            if (!OldDepth.ContainsKey(c)) OldDepth[c] = (float)SwimDepth.GetValue(c);
            // smooth motion: at 12 m/s a body moved only on physics steps jumps ~25 cm per frame
            Rigidbody rb = BodyField != null ? BodyField.GetValue(c) as Rigidbody : null;
            if (rb != null && !OldInterp.ContainsKey(c)) { OldInterp[c] = rb.interpolation; rb.interpolation = RigidbodyInterpolation.Interpolate; }
            SwimDepth.SetValue(c, Plugin.SerpentSwimDepth.Value);   // just under the surface: your head stays out
        }

        internal static void OnGetOff(Character c)
        {
            float d;
            if (SwimDepth != null && c != null && OldDepth.TryGetValue(c, out d)) { SwimDepth.SetValue(c, d); OldDepth.Remove(c); }
            ResetBody(c);
            RigidbodyInterpolation ri;
            Rigidbody rb = c != null && BodyField != null ? BodyField.GetValue(c) as Rigidbody : null;
            if (c != null && OldInterp.TryGetValue(c, out ri)) { if (rb != null) rb.interpolation = ri; OldInterp.Remove(c); }
        }

        internal static float Food(Character c)
        {
            ZNetView nv = Leash.View(c);
            return nv != null && nv.IsValid() ? nv.GetZDO().GetFloat(FoodKey, MaxFood) : MaxFood;
        }

        // called while ridden, on the game that moves it
        internal static void Burn(Character c, float dt)
        {
            ZNetView nv = Leash.View(c);
            if (nv == null || !nv.IsValid() || !nv.IsOwner()) return;
            float f = nv.GetZDO().GetFloat(FoodKey, MaxFood);
            if (f <= 0f) return;
            float before = f;
            f = Mathf.Max(0f, f - dt / (Plugin.SerpentFoodMinutes.Value * 60f));
            nv.GetZDO().Set(FoodKey, f);
            if (before > 0f && f <= 0f && Player.m_localPlayer != null)
                Player.m_localPlayer.Message(MessageHud.MessageType.Center, c.GetHoverName() + " is hungry and slows down. Feed it fish.");
        }

        internal static float SpeedFactor(Character c)
        {
            return Food(c) > 0f ? 1f : Plugin.SerpentHungrySpeed.Value;
        }

        // ---------------------------------------------------------------- getting off

        internal static Ship NearShip(Vector3 at, float range)
        {
            Ship best = null;
            float bd = range;
            foreach (Collider col in Physics.OverlapSphere(at, range))
            {
                Ship s = col.GetComponentInParent<Ship>();
                if (s == null) continue;
                float d = Vector3.Distance(at, s.transform.position);
                if (d < bd) { bd = d; best = s; }
            }
            return best;
        }

        internal static bool NearLand(Vector3 at, float range)
        {
            if (ZoneSystem.instance == null) return true;
            float water = ZoneSystem.instance.m_waterLevel;
            for (float r = 4f; r <= range; r += 4f)
                for (int i = 0; i < 12; i++)
                {
                    float a = i * Mathf.PI / 6f;
                    Vector3 p = at + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * r;
                    float h;
                    if (ZoneSystem.instance.GetGroundHeight(p, out h) && h > water + 0.2f) return true;
                }
            return false;
        }

        private static float _nextAgroundMsg;

        private static float Depth(Vector3 p)
        {
            float h;
            if (ZoneSystem.instance == null || !ZoneSystem.instance.GetGroundHeight(p, out h)) return 100f;
            return ZoneSystem.instance.m_waterLevel - h;
        }

        // true when swimming that way would take it into water too shallow (or onto land).
        // Moving towards deeper water is always allowed, so it can work itself free.
        internal static bool Aground(Character c, Vector3 dir)
        {
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f) return false;
            dir.Normalize();
            Vector3 at = c.transform.position;
            float here = Depth(at);
            float min = Plugin.SerpentMinDepth.Value;
            for (float r = 2f; r <= 5f; r += 1.5f)
            {
                float ahead = Depth(at + dir * r);
                if (ahead < min && ahead <= here + 0.05f)
                {
                    if (Time.time > _nextAgroundMsg && Player.m_localPlayer != null)
                    {
                        _nextAgroundMsg = Time.time + 4f;
                        Player.m_localPlayer.Message(MessageHud.MessageType.Center, "Too shallow: " + c.GetHoverName() + " can't swim there.");
                    }
                    return true;
                }
            }
            return false;
        }

        internal static bool CanGetOff(Character c)
        {
            float range = Plugin.SerpentGetOffRange.Value;
            return NearShip(c.transform.position, range) != null || NearLand(c.transform.position, range);
        }

        // where you land when you get off: on the nearest bench of a ship nearby, else in the water beside it
        internal static bool PlaceOnShip(Player p, Character c)
        {
            Ship s = NearShip(c.transform.position, Plugin.SerpentGetOffRange.Value);
            if (s == null) return false;
            Transform spot = null;
            float bd = float.MaxValue;
            foreach (Chair ch in s.GetComponentsInChildren<Chair>())
            {
                Transform t = ch.m_attachPoint != null ? ch.m_attachPoint : ch.transform;
                float d = Vector3.Distance(t.position, c.transform.position);
                if (d < bd) { bd = d; spot = t; }
            }
            p.transform.position = spot != null ? spot.position + Vector3.up * 0.4f : s.transform.position + Vector3.up * 1.6f;
            Plugin.Debug("[sea] got off onto " + (spot != null ? "a bench of " : "the deck of ") + s.name);
            return true;
        }

        // ---------------------------------------------------------------- feeding fish

        internal static bool IsFish(ItemDrop.ItemData item)
        {
            if (item == null) return false;
            string n = item.m_dropPrefab != null ? item.m_dropPrefab.name : "";
            if (n == "FishRaw" || n == "FishAnglerRaw") return true;
            if (!n.StartsWith("Fish") || n.Length <= 4) return false;
            foreach (char ch in n.Substring(4)) if (!char.IsDigit(ch)) return false;
            return true;
        }

        [HarmonyPatch(typeof(Humanoid), "UseItem"), HarmonyPrefix]
        private static bool UseItem(Humanoid __instance, Inventory inventory, ItemDrop.ItemData item, bool fromInventoryGui)
        {
            if (!Plugin.Enabled.Value || fromInventoryGui || !IsFish(item)) return true;
            Player p = __instance as Player;
            if (p == null || p != Player.m_localPlayer) return true;
            Character c = Mount.RiddenCharacter();
            if (c == null)
            {
                GameObject hover = p.GetHoverObject();
                c = hover != null ? hover.GetComponentInParent<Character>() : null;
            }
            if (c == null || !c.IsTamed() || !IsSeaMount(c)) return true;
            ZNetView nv = Leash.View(c);
            if (nv == null || !nv.IsValid()) return true;
            float f = nv.GetZDO().GetFloat(FoodKey, MaxFood);
            if (f > MaxFood - 1f + 0.001f) { p.Message(MessageHud.MessageType.Center, c.GetHoverName() + " isn't hungry."); return false; }
            if (!nv.IsOwner()) nv.ClaimOwnership();
            nv.GetZDO().Set(FoodKey, Mathf.Min(MaxFood, Mathf.Ceil(f + 0.001f)));
            (inventory ?? p.GetInventory()).RemoveOneItem(item);
            p.Message(MessageHud.MessageType.Center, c.GetHoverName() + " gulps the fish down.");
            return false;
        }

        // ---------------------------------------------------------------- body, head and seat

        private class Body
        {
            public Transform Head, Neck, Visual;
            public Vector3 VisualPos;
            public Quaternion VisualRot;
            public bool Flip;
            public Vector3 Heading, Shown;
            public float Lean;
            public float Speed;
        }
        private static readonly Dictionary<Character, Body> Bodies = new Dictionary<Character, Body>();
        private static readonly FieldInfo BodyField = AccessTools.Field(typeof(Character), "m_body");

        private static Body Of(Character c)
        {
            Body b;
            if (Bodies.TryGetValue(c, out b)) return b;
            b = new Body();
            SkinnedMeshRenderer sk = c.GetComponentInChildren<SkinnedMeshRenderer>();
            if (sk != null && sk.bones != null)
                foreach (Transform t in sk.bones)
                    if (t != null && t.name.ToLowerInvariant().Contains("head") && t.parent != null) { b.Head = t; b.Neck = t.parent; break; }
            b.Visual = c.transform.Find("Visual");
            if (b.Visual == null && sk != null) b.Visual = sk.transform.parent;
            if (b.Visual != null) { b.VisualPos = b.Visual.localPosition; b.VisualRot = b.Visual.localRotation; }
            Vector3 snout = Snout(b, c);
            b.Flip = Vector3.Dot(snout, c.transform.forward) < 0f;   // its model faces backwards from its body
            Plugin.Debug("[sea] " + c.name + ": head bone " + (b.Head != null ? b.Head.name + " (neck " + b.Neck.name + ")" : "not found")
                + ", visual " + (b.Visual != null ? b.Visual.name : "none") + ", model faces " + (b.Flip ? "backwards" : "forwards"));
            if (Bodies.Count > 20) Bodies.Clear();
            Bodies[c] = b;
            return b;
        }

        private static Vector3 Snout(Body b, Character c)
        {
            Vector3 d = b.Head != null ? b.Head.position - b.Neck.position : c.transform.forward;
            d.y = 0f;
            return d.sqrMagnitude > 0.0001f ? d.normalized : c.transform.forward;
        }

        // the way the serpent's head points, flat
        internal static Vector3 Facing(Character c) { return Snout(Of(c), c); }

        internal static void Steering(Character c, Vector3 heading, float speed)
        {
            Body b = Of(c);
            b.Heading = heading;
            b.Speed = speed;
        }

        // the body always points where you steer it (like a boat), whatever the game would turn it to
        [HarmonyPatch(typeof(Character), "UpdateSwimming"), HarmonyPostfix]
        private static void Swim(Character __instance)
        {
            if (!Plugin.Enabled.Value || Mount.RiddenCharacter() != __instance) return;
            Body b;
            if (!Bodies.TryGetValue(__instance, out b) || b.Heading.sqrMagnitude < 0.01f) return;
            Quaternion rot = Quaternion.LookRotation(b.Flip ? -b.Heading : b.Heading, Vector3.up);
            Rigidbody rb = BodyField != null ? BodyField.GetValue(__instance) as Rigidbody : null;
            if (rb != null) { rb.MoveRotation(rot); rb.angularVelocity = Vector3.zero; }   // keeps the interpolation smooth
            else __instance.transform.rotation = rot;
        }

        // every frame while you ride: lift the body a little out of the water, bob it, sway it as it swims,
        // and put your seat on top of its head, facing where the snout points
        internal static void Ridden(Character c, Transform seat)
        {
            Body b = Of(c);
            float t = Time.time;
            if (b.Visual != null)
            {
                float amp = Plugin.SerpentBob.Value;
                float bob = Mathf.Sin(t * 1.4f) * 0.12f * amp;
                float sway = Mathf.Sin(t * 1.1f) * (1.5f + 5f * b.Speed) * amp;
                float roll = Mathf.Sin(t * 1.4f + 0.6f) * (1.5f + 3f * b.Speed) * amp;
                // the model itself turns with your steering every frame (the physics body follows behind it),
                // and leans into the curve like a boat
                float dt = Mathf.Max(Time.deltaTime, 0.0001f);
                Vector3 want = b.Heading.sqrMagnitude > 0.01f ? b.Heading : (b.Shown.sqrMagnitude > 0.01f ? b.Shown : Snout(b, c));
                if (b.Shown.sqrMagnitude < 0.01f) b.Shown = want;
                Vector3 before = b.Shown;
                b.Shown = Vector3.Slerp(b.Shown, want, 1f - Mathf.Exp(-14f * dt)).normalized;
                float yawRate = Vector3.SignedAngle(before, b.Shown, Vector3.up) / dt;   // degrees per second
                float lean = Mathf.Clamp(-yawRate * 0.25f * Mathf.Max(0.3f, b.Speed), -14f, 14f);
                b.Lean = Mathf.Lerp(b.Lean, lean, 1f - Mathf.Exp(-5f * dt));
                Quaternion root = Quaternion.LookRotation(b.Flip ? -b.Shown : b.Shown, Vector3.up);
                float side = b.Flip ? -1f : 1f;
                b.Visual.position = c.transform.TransformPoint(b.VisualPos) + Vector3.up * (Plugin.SerpentLift.Value + bob);
                b.Visual.rotation = root * Quaternion.Euler(0f, sway, roll + b.Lean * side) * b.VisualRot;
            }
            if (b.Head != null)
            {
                Vector3 snout = Snout(b, c);
                seat.position = b.Head.position + Vector3.up * Plugin.SerpentSeatUp.Value - snout * Plugin.SerpentSeatBack.Value;
                seat.rotation = Quaternion.LookRotation(snout, Vector3.up);
            }
        }

        internal static void ResetBody(Character c)
        {
            Body b;
            if (c == null || !Bodies.TryGetValue(c, out b)) return;
            if (b.Visual != null) { b.Visual.localPosition = b.VisualPos; b.Visual.localRotation = b.VisualRot; }
            b.Heading = Vector3.zero;
            b.Shown = Vector3.zero;
            b.Lean = 0f;
            b.Speed = 0f;
        }

        // ---------------------------------------------------------------- the food bar
        // A copy of the game's own stamina bar, just above it, in 4 parts with a fish at the end of each:
        // grey until the bar reaches it, in colour once it does.

        private static GameObject _bar;
        private static GuiBar[] _fills;
        private static UnityEngine.UI.Image[] _fishes;
        private const float BarWidth = 280f;

        internal static void UpdateBar()
        {
            Character c = Mount.RiddenCharacter();
            bool show = c != null && IsSeaMount(c) && Hud.instance != null;
            if (!show) { if (_bar != null && _bar.activeSelf) _bar.SetActive(false); return; }
            if (_bar == null && !MakeBar()) return;
            if (!_bar.activeSelf) _bar.SetActive(true);
            float f = Mathf.Clamp(Food(c), 0f, MaxFood);
            foreach (GuiBar g in _fills) if (g != null) g.SetValue(f / MaxFood);
            for (int i = 0; i < _fishes.Length; i++)
                if (_fishes[i] != null) _fishes[i].color = f >= i + 1 - 0.001f ? Color.white : new Color(0.3f, 0.3f, 0.3f, 0.9f);
            RectTransform src = Hud.instance.m_staminaBar2Root;
            RectTransform rt = _bar.GetComponent<RectTransform>();
            if (src != null && rt != null) rt.anchoredPosition = src.anchoredPosition + new Vector2(0f, 46f);
        }

        private static bool MakeBar()
        {
            RectTransform src = Hud.instance.m_staminaBar2Root;
            if (src == null) return false;
            _bar = Object.Instantiate(src.gameObject, src.parent, false);
            _bar.name = "Beastkeeper_SerpentFood";
            foreach (Animator a in _bar.GetComponentsInChildren<Animator>(true)) Object.Destroy(a);
            foreach (TMPro.TMP_Text tx in _bar.GetComponentsInChildren<TMPro.TMP_Text>(true)) tx.gameObject.SetActive(false);
            CanvasGroup cg = _bar.GetComponent<CanvasGroup>();
            // the stamina bar hides itself when full (fades out): wake every part of the copy
            foreach (CanvasGroup g in _bar.GetComponentsInChildren<CanvasGroup>(true)) g.alpha = 1f;
            foreach (Transform t in _bar.GetComponentsInChildren<Transform>(true))
                if (t.GetComponent<TMPro.TMP_Text>() == null) t.gameObject.SetActive(true);
            foreach (UnityEngine.UI.Graphic gr in _bar.GetComponentsInChildren<UnityEngine.UI.Graphic>(true))
            {
                if (gr is TMPro.TMP_Text) continue;
                Color col = gr.color; col.a = 1f; gr.color = col;
                gr.enabled = true;
                Plugin.Debug("[sea] bar part " + gr.name + " colour " + gr.color);
            }
            _fills = _bar.GetComponentsInChildren<GuiBar>(true);
            RectTransform rt = _bar.GetComponent<RectTransform>();
            FieldInfo bf = AccessTools.Field(typeof(Hud), "m_staminaBarBorderBuffer");
            float border = bf != null ? System.Convert.ToSingle(bf.GetValue(Hud.instance)) : 16f;
            rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, BarWidth + border);
            foreach (GuiBar g in _fills) g.SetWidth(BarWidth);

            Sprite fish = null;
            GameObject fp = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab("Fish1") : null;
            Sprite[] icons = fp != null ? fp.GetComponent<ItemDrop>().m_itemData.m_shared.m_icons : null;
            if (icons != null && icons.Length > 0) fish = icons[0];
            _fishes = new UnityEngine.UI.Image[4];
            for (int i = 1; i <= 4; i++)
            {
                float x = border / 2f + BarWidth * i / 4f;
                if (i < 4) Piece(rt, "divider", x, new Vector2(2f, 14f), null, new Color(0f, 0f, 0f, 0.75f));
                if (fish != null) _fishes[i - 1] = Piece(rt, "fish", x, new Vector2(30f, 30f), fish, Color.white);
            }
            Plugin.Debug("[sea] food bar made from " + src.name + ", " + _fills.Length + " bars");
            return true;
        }

        private static UnityEngine.UI.Image Piece(RectTransform parent, string name, float x, Vector2 size, Sprite sprite, Color color)
        {
            GameObject g = new GameObject("Beastkeeper_" + name, typeof(RectTransform), typeof(UnityEngine.UI.Image));
            RectTransform r = g.GetComponent<RectTransform>();
            r.SetParent(parent, false);
            r.anchorMin = r.anchorMax = new Vector2(0f, 0.5f);
            r.pivot = new Vector2(0.5f, 0.5f);
            r.sizeDelta = size;
            r.anchoredPosition = new Vector2(x, 0f);
            UnityEngine.UI.Image im = g.GetComponent<UnityEngine.UI.Image>();
            im.sprite = sprite;
            im.color = color;
            im.preserveAspect = true;
            im.raycastTarget = false;
            return im;
        }
    }
}
