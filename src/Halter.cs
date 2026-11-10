using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Beastkeeper
{
    // The halter is the only way to tame an animal (for players with the mod: the game's own taming by feeding
    // is switched off). Catch the animal on your lasso, hover it and press the halter's hotbar key: it is tamed at
    // once and the halter is used up. The halter must be the animal's tier or better.
    // The tamed flag is the game's own, so it counts as tamed for everyone, with or without the mod.
    [HarmonyPatch]
    internal static class Halter
    {
        internal static readonly ToolSet Set = new ToolSet();
        internal static readonly int Key = "Beastkeeper_halter".GetStableHashCode();
        private static readonly MethodInfo InteractAnim = AccessTools.Method(typeof(Humanoid), "DoInteractAnimation");
        private static readonly MethodInfo TameMethod = AccessTools.Method(typeof(Tameable), "Tame");
        private static readonly string[] Stuff = { "", "Leather straps", "Troll hide straps", "Wolf hide straps", "Linen straps with lox hide", "Red jute straps" };

        internal static bool IsHalter(ItemDrop.ItemData item) { return Set.Is(item); }

        internal static bool NeedsHalter(Character c)
        {
            return c != null && !c.IsTamed();
        }

        internal static bool Wears(Character c)
        {
            ZNetView nv = Leash.View(c);
            return nv != null && nv.IsValid() && nv.GetZDO().GetInt(Key, 0) == 1;
        }

        internal static void Init()
        {
            for (int t = 1; t <= Tiers.Max; t++)
            {
                int tier = t;
                Set.ByTier[t] = Items.Add(new CustomItem
                {
                    PrefabName = t == 1 ? "Beastkeeper_Halter" : "Beastkeeper_Halter" + t,
                    Sources = new[] { "LeatherScraps", "DeerHide" },
                    Recipe = Recipes.Halter[t],
                    Setup = (p, src) =>
                    {
                        ItemDrop.ItemData.SharedData sh = p.GetComponent<ItemDrop>().m_itemData.m_shared;
                        sh.m_name = Tiers.Line[tier] + " Halter";
                        sh.m_description = Stuff[tier] + " for an animal's head. Tames wild " + Lasso.Catches(tier) + ": catch one with the lasso, "
                            + "hover it and press the halter's hotbar key. It is tamed at once.";
                        sh.m_icons = new[] { Items.Tinted(Icon(), Recipes.Tint[tier], "Beastkeeper_HalterIcon" + tier) };
                        sh.m_itemType = ItemDrop.ItemData.ItemType.Material;
                        sh.m_maxStackSize = 10;
                        sh.m_weight = 0.5f;
                        sh.m_teleportable = true;
                    },
                });
            }
        }

        // the game's taming (feeding a wild animal over time) does nothing for players with the mod
        // Only a halter tames an animal: food (or another mod's taming boost) can't finish the job on its own.
        private static bool _taming;
        [HarmonyPatch(typeof(Tameable), "Tame"), HarmonyPrefix]
        private static bool OnlyByHalter(Tameable __instance)
        {
            if (!Plugin.Enabled.Value || _taming) return true;
            Character c = __instance.GetComponent<Character>();
            if (c == null || Sea.IsSeaMount(c) || !Leash.Eligible(c)) return true;
            Plugin.Debug("[halter] blocked taming without a halter on " + c.name);
            return false;
        }

        [HarmonyPatch(typeof(Tameable), "DecreaseRemainingTime"), HarmonyPrefix]
        private static bool NoFeedTaming() { return !Plugin.Enabled.Value; }

        [HarmonyPatch(typeof(Humanoid), "UseItem"), HarmonyPrefix]
        private static bool UseItem(Humanoid __instance, Inventory inventory, ItemDrop.ItemData item, bool fromInventoryGui)
        {
            if (!Plugin.Enabled.Value || fromInventoryGui || !IsHalter(item)) return true;
            Player p = __instance as Player;
            if (p == null || p != Player.m_localPlayer) return true;
            GameObject hover = p.GetHoverObject();
            Character c = hover != null ? hover.GetComponentInParent<Character>() : null;
            if (c == null || c.IsPlayer()) return true;

            string name = c.GetHoverName();
            if (c.IsTamed()) { Say(p, name + " is already tamed."); return false; }
            if (!Leash.IsMine(c) || Tug.Is(c)) { Say(p, "Catch " + name + " with your lasso first."); return false; }
            int need = Tiers.Animal(c), have = Set.TierOf(item);
            if (have < need) { Say(p, "The " + item.m_shared.m_name + " won't fit " + name + ": you need " + Tiers.Need("Halter", need) + "."); return false; }

            ZNetView nv = Leash.View(c);
            if (!nv.IsOwner()) nv.ClaimOwnership();
            nv.GetZDO().Set(Key, 1);
            Tameable tm = c.GetComponent<Tameable>();
            _taming = true;
            try
            {
                if (tm != null && TameMethod != null) TameMethod.Invoke(tm, null);   // the game's own taming: effects, its tamed behaviour
                else c.SetTamed(true);
            }
            finally { _taming = false; }
            if (!c.IsTamed()) c.SetTamed(true);
            Home.Set(c, c.transform.position);
            Leash.Follow(c, p);   // it stays on your rope and walks with you
            (inventory ?? p.GetInventory()).RemoveOneItem(item);
            if (InteractAnim != null) InteractAnim.Invoke(p, new object[] { hover });
            Effects.Shake(1f);
            Say(p, name + " wears the halter: tamed!");
            Plugin.Debug("[halter] tamed " + name + " (" + c.name + ")");
            return false;
        }

        private static void Say(Player p, string msg) { p.Message(MessageHud.MessageType.Center, msg); }

        private static Sprite _icon;

        // leather straps in a loop with a ring, drawn in code
        private static Sprite Icon()
        {
            Color light = Items.LeatherLight, dark = Items.LeatherDark, metal = new Color(0.72f, 0.72f, 0.68f);
            Vector2 c = new Vector2(30f, 36f);
            if (_icon != null) return _icon;
            return _icon = Items.MakeIcon("Beastkeeper_HalterIcon", p =>
            {
                Vector2 d = p - c;
                d.x *= 0.85f;
                float r = d.magnitude;
                Color col = Color.clear;
                if (Mathf.Abs(r - 20f) < 3.4f)                                 // the head loop
                {
                    float stitch = Mathf.Abs(r - 20f) > 2.2f ? 0.25f : 1f;
                    col = Color.Lerp(dark, light, 0.4f * stitch + 0.2f);
                }
                if (Mathf.Abs(p.x - 30f) < 3f && p.y > 14f && p.y < 58f)      // strap across the nose
                    col = Color.Lerp(dark, light, 0.75f);
                float ring = (p - new Vector2(30f, 14f)).magnitude;            // ring for the rope
                if (Mathf.Abs(ring - 5.5f) < 1.6f) col = metal;
                float lead = 30f + (14f - p.y) * 0.2f;                        // short lead under the ring
                if (p.y < 9f && p.y > 1f && Mathf.Abs(p.x - lead) < 1.8f) col = dark;
                return col;
            });
        }
    }
}
