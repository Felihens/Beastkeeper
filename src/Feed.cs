using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Beastkeeper
{
    // Feeding by hand: look at a tamed animal and use any food from your hotbar. It eats from your hand and heals
    // (as much as the food's health, at least MinHeal). It also counts as fed, so the game's tamed animals can
    // breed and hares breed too (see Breed). Each animal eats once every HandFeedCooldown seconds, from anyone.
    // Wild animals can't be fed by hand: they are tamed only with a halter.
    [HarmonyPatch]
    internal static class Feed
    {
        private const string Rpc = "Beastkeeper_HandFeed";
        private const float HealLow = 0.25f, HealHigh = 0.4f, HealSeconds = 10f;   // 25% to 40% of its health, over 10 seconds
        private static readonly int FedAtKey = "Beastkeeper_handfed".GetStableHashCode();
        private static readonly MethodInfo ResetTimer = AccessTools.Method(typeof(Tameable), "ResetFeedingTimer");

        internal static void RegisterOn(Character c)
        {
            ZNetView nv = Leash.View(c);
            if (nv == null || nv.GetZDO() == null) return;
            nv.Register<float>(Rpc, (sender, heal) => Eat(c, nv, heal));
        }

        private static float Now() { return ZNet.instance != null ? (float)ZNet.instance.GetTimeSeconds() : Time.time; }

        internal static bool FedWithin(Character c, float seconds)
        {
            ZNetView nv = Leash.View(c);
            if (nv == null || !nv.IsValid()) return false;
            float at = nv.GetZDO().GetFloat(FedAtKey, -99999f);
            return Now() - at <= seconds;
        }

        // any food: things you can eat, and whatever this animal eats on its own (raw meat for a wolf...)
        private static bool IsFood(Character c, ItemDrop.ItemData item)
        {
            ItemDrop.ItemData.SharedData sh = item.m_shared;
            if (sh.m_itemType == ItemDrop.ItemData.ItemType.Consumable && (sh.m_food > 0f || sh.m_foodStamina > 0f || sh.m_foodEitr > 0f)) return true;
            MonsterAI ai = c.GetComponent<MonsterAI>();
            return ai != null && ai.m_consumeItems != null && ai.m_consumeItems.Exists(f => f != null && f.m_itemData.m_shared.m_name == sh.m_name);
        }

        [HarmonyPatch(typeof(Humanoid), "UseItem"), HarmonyPrefix]
        private static bool UseItem(Humanoid __instance, Inventory inventory, ItemDrop.ItemData item, bool fromInventoryGui, bool __runOriginal)
        {
            if (!__runOriginal) return false;   // already used (the serpent's fish, a halter...)
            if (!Plugin.Enabled.Value || !Plugin.HandFeeding.Value || fromInventoryGui || item == null) return true;
            Player p = __instance as Player;
            if (p == null || p != Player.m_localPlayer) return true;
            GameObject hover = p.GetHoverObject();
            Character c = hover != null ? hover.GetComponentInParent<Character>() : null;
            if (c == null || c.IsPlayer() || c.IsDead() || !c.IsTamed() || !Leash.Eligible(c) || Sea.IsSeaMount(c)) return true;
            if (!IsFood(c, item)) return true;   // not food: let the game (or other mods) handle the item
            ZNetView nv = Leash.View(c);
            if (nv == null || !nv.IsValid()) return true;

            string name = c.GetHoverName();
            float wait = nv.GetZDO().GetFloat(FedAtKey, -9999f) + Plugin.HandFeedCooldown.Value - Now();
            if (wait > 0f)
            {
                p.Message(MessageHud.MessageType.Center, name + " isn't hungry yet (" + Mathf.CeilToInt(wait) + " s).");
                return false;
            }
            // simple food (berries) heals a quarter of its health; hearty food (a good meal) up to 40%
            float share = Mathf.Lerp(HealLow, HealHigh, Mathf.InverseLerp(10f, 60f, item.m_shared.m_food));
            float heal = c.GetMaxHealth() * share;
            float missing = c.GetMaxHealth() - c.GetHealth();
            (inventory ?? p.GetInventory()).RemoveOneItem(item);
            nv.InvokeRPC(Rpc, heal);
            p.Message(MessageHud.MessageType.Center, name + " eats from your hand" + (missing > 1f ? ": +" + Mathf.RoundToInt(Mathf.Min(heal, missing)) + " health over " + HealSeconds.ToString("0") + " s." : "."));
            return false;
        }

        // on the animal's owner
        private static void Eat(Character c, ZNetView nv, float heal)
        {
            if (c == null || !nv.IsValid() || !nv.IsOwner()) return;
            nv.GetZDO().Set(FedAtKey, Now());
            Tameable t = c.GetComponent<Tameable>();
            if (t != null)
            {
                if (ResetTimer != null) ResetTimer.Invoke(t, null);   // fed: the game's tamed animals can breed
                if (t.m_sootheEffect != null) t.m_sootheEffect.Create(c.GetCenterPoint(), Quaternion.identity);
            }
            if (heal > 0f) Plugin.Instance.StartCoroutine(HealSlowly(c, heal));
            Plugin.Debug("[feed] " + c.name + " hand fed, heal " + heal.ToString("0") + " over " + HealSeconds + " s");
        }

        private static System.Collections.IEnumerator HealSlowly(Character c, float total)
        {
            const int steps = 10;
            for (int i = 0; i < steps; i++)
            {
                if (c == null || c.IsDead()) yield break;
                if (c.GetHealth() < c.GetMaxHealth()) c.Heal(total / steps, i == 0);
                yield return new WaitForSeconds(HealSeconds / steps);
            }
        }
    }
}
