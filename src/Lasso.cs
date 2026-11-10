using System.Collections.Generic;
using UnityEngine;

namespace Beastkeeper
{
    // The lasso: a copy of the club (a one-handed item, so it is held in the hand) with its model swapped
    // for a coil of rope.
    internal static class Lasso
    {
        internal static readonly ToolSet Set = new ToolSet();
        private static readonly string[] RopeOf = { "", "leather", "troll hide", "wolf hide", "linen and lox hide", "red jute" };

        internal static bool IsLasso(ItemDrop.ItemData item) { return Set.Is(item); }

        internal static void Init()
        {
            for (int t = 1; t <= Tiers.Max; t++)
            {
                int tier = t;
                Set.ByTier[t] = Items.Add(new CustomItem
                {
                    PrefabName = t == 1 ? "Beastkeeper_Lasso" : "Beastkeeper_Lasso" + t,   // tier 1 keeps the old name, so old lassos stay
                    Sources = new[] { "Club", "Torch" },
                    Recipe = Recipes.Lasso[t],
                    Setup = (p, src) => Setup(p, src, tier),
                });
            }
        }

        private static void Setup(GameObject p, GameObject src, int tier)
        {
            ItemDrop.ItemData.SharedData sh = p.GetComponent<ItemDrop>().m_itemData.m_shared;
            sh.m_name = Tiers.Line[tier] + " Lasso";
            sh.m_description = "A coil of " + RopeOf[tier] + " rope, held in both hands. Hold the right mouse button to swing it, left to throw: "
                + "the loop flies in an arc and catches the animal it touches.\n"
                + "Catches wild " + Catches(tier) + " that haven't noticed you, after a tug of war. Holds any tamed animal.\n"
                + "A quick right-click lets go. With animals on the rope, E on something you built ties them to it.";
            sh.m_icons = new[] { Items.Tinted(Icon(), Recipes.Tint[tier], "Beastkeeper_LassoIcon" + tier) };
            sh.m_maxStackSize = 1;
            sh.m_weight = 1f;
            sh.m_teleportable = true;
            // takes both hand slots (no shield or torch with it), but keeps the one-handed pose
            sh.m_itemType = ItemDrop.ItemData.ItemType.TwoHandedWeapon;
            sh.m_animationState = ItemDrop.ItemData.AnimationState.OneHanded;
            sh.m_damages = new HitData.DamageTypes();
            sh.m_damagesPerLevel = new HitData.DamageTypes();
            sh.m_attackForce = 0f;
            sh.m_blockPower = 0f;
            sh.m_blockPowerPerLevel = 0f;
            sh.m_useDurability = false;
            sh.m_maxQuality = 1;

            // model: hide the club, show a coil of rope where it was (the "attach" child is what the hand holds)
            foreach (Renderer r in p.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
            // the club's swing trail (the white streak when you run) goes too (particles are renderers, already hidden)
            foreach (MeleeWeaponTrail t in p.GetComponentsInChildren<MeleeWeaponTrail>(true)) Object.DestroyImmediate(t);
            Transform attach = p.transform.Find("attach");
            GameObject coil = new GameObject("Beastkeeper_Coil");
            coil.transform.SetParent(attach != null ? attach : p.transform, false);
            coil.AddComponent<Coil>().Tint = Recipes.Tint[tier];
            Plugin.Debug("[lasso] attach child " + (attach != null ? "found" : "missing") + "; the original is still called " + src.GetComponent<ItemDrop>().m_itemData.m_shared.m_name);
        }

        // which wild animals each tier catches (its own tier and the ones below)
        internal static string Catches(int tier)
        {
            string[] by = { "", "boars, deer and hens", "bears", "wolves", "lox", "hares and asksvin" };
            List<string> parts = new List<string>();
            for (int t = 1; t <= tier; t++) parts.Add(by[t]);
            string s = parts[parts.Count - 1];
            if (parts.Count > 1) s = string.Join(", ", parts.GetRange(0, parts.Count - 1).ToArray()) + " and " + s;
            return s;
        }

        private static Sprite _icon;

        // a coil of rope, drawn in code
        private static Sprite Icon()
        {
            Color light = Items.LeatherLight, dark = Items.LeatherDark;
            Vector2 c = new Vector2(30f, 34f);
            if (_icon != null) return _icon;
            return _icon = Items.MakeIcon("Beastkeeper_LassoIcon", p =>
            {
                Color col = Color.clear;
                for (int k = 0; k < 3; k++)   // three loops of the coil, a little squashed
                {
                    Vector2 d = p - c - new Vector2(k * 2.5f, -k * 1.5f);
                    d.y *= 1.25f;
                    float r = d.magnitude;
                    float ring = Mathf.Abs(r - (21f - k * 1.2f));
                    if (ring < 3.2f)
                    {
                        float twist = Mathf.Sin(Mathf.Atan2(d.y, d.x) * 14f + r * 0.6f) * 0.5f + 0.5f;
                        Color rope = Color.Lerp(dark, light, 0.35f + 0.65f * twist * (1f - ring / 3.2f));
                        rope.a = Mathf.Clamp01((3.2f - ring) * 1.5f);
                        col = Color.Lerp(col, rope, rope.a);
                        col.a = Mathf.Max(col.a, rope.a);
                    }
                }
                float tx = 46f + (p.y - 6f) * 0.35f;   // loose end hanging down to the right
                if (p.y > 4f && p.y < 22f && Mathf.Abs(p.x - tx) < 2.4f)
                    col = Color.Lerp(dark, light, 0.4f + 0.6f * (Mathf.Sin(p.y * 1.4f) * 0.5f + 0.5f));
                return col;
            });
        }
    }
}
