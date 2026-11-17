using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Beastkeeper
{
    // Where tamed drakes come from (agreed with the user, 2026-11-11): Moder's eggs, the dragon eggs the game
    // already leaves in the drake nests of the Mountains. Once Moder is dead, one of her eggs can hatch:
    // - taking an egg from a nest makes the drakes around it come for you;
    // - lay it on the ground and give it an item, which decides what the drake will be:
    //   Freeze Gland or Crystal = frost, Surtling Core = fire, Ooze = poison, Obsidian = earth, Thunderstone = lightning;
    // - kept warm by a fire (EggNeedsFire), it hatches after HatchMinutes into a tamed baby drake of that element.
    // The egg is the game's own item: it still can't go through portals, and it still summons Moder.
    [HarmonyPatch]
    internal static class DrakeEgg
    {
        internal const string EggPrefab = "DragonEgg";
        private const string ModerKey = "defeated_dragon";
        private static readonly int WarmKey = "Beastkeeper_eggWarm".GetStableHashCode();   // seconds kept warm so far
        // Time away counts too (user, 2026-11-17): at each warm check the egg writes the time and how long its fire can
        // still burn; when its land loads again, the time gone by counts, up to what that fire could burn meanwhile.
        private static readonly int WarmAtKey = "Beastkeeper_eggWarmAt".GetStableHashCode();   // server time (ms) of the last warm check, 0 = it was cold
        private static readonly int BurnKey = "Beastkeeper_eggBurn".GetStableHashCode();       // seconds its fire could still burn then

        internal static bool IsEgg(GameObject go) { return go != null && Utils.GetPrefabName(go) == EggPrefab; }

        internal static bool ModerDead()
        {
            return ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(ModerKey);
        }

        private static bool On() { return Plugin.Enabled.Value && Plugin.DrakeMounts != null && (Plugin.DrakeMounts.Value ?? "").Trim().Length > 0; }

        // ------------------------------------------------------------ taking an egg from a nest

        [HarmonyPatch(typeof(Humanoid), "Pickup"), HarmonyPrefix]
        private static void BeforePickup(Humanoid __instance, GameObject go, out Vector3 __state)
        {
            __state = IsEgg(go) ? go.transform.position : Vector3.zero;
        }

        [HarmonyPatch(typeof(Humanoid), "Pickup"), HarmonyPostfix]
        private static void AfterPickup(Humanoid __instance, bool __result, Vector3 __state)
        {
            if (!__result || __state == Vector3.zero || !On() || __instance != Player.m_localPlayer) return;
            int angry = 0;
            foreach (Character c in Character.GetAllCharacters())
            {
                if (c == null || c.IsDead() || c.IsTamed() || !Drake.Is(c)) continue;
                if (Vector3.Distance(c.transform.position, __state) > Plugin.DrakeEggAlertRange.Value) continue;
                MonsterAI ai = c.GetComponent<MonsterAI>();
                if (ai == null) continue;
                Traverse.Create(ai).Field("m_targetCreature").SetValue(__instance);
                ai.Alert();
                angry++;
            }
            if (angry > 0) __instance.Message(MessageHud.MessageType.Center, angry == 1 ? "A drake saw you take the egg!" : angry + " drakes saw you take the egg!");
            Plugin.Debug("[egg] taken from " + __state + ": " + angry + " drakes come for it");
        }

        // ------------------------------------------------------------ waking the egg with an item

        // the item that decides the drake's element (0 = not one of them)
        internal static int ElementOf(ItemDrop.ItemData item)
        {
            if (item == null || item.m_dropPrefab == null) return 0;
            switch (item.m_dropPrefab.name)
            {
                case "FreezeGland": case "Crystal": return Drake.Frost;
                case "SurtlingCore": return Drake.Fire;
                case "Ooze": return Drake.Poison;
                case "Obsidian": return Drake.Earth;
                case "Thunderstone": return Drake.Lightning;
            }
            return 0;
        }

        // the item each element's egg is woken with, for the drop below
        internal static string ItemOf(int el)
        {
            switch (el)
            {
                case Drake.Frost: return "FreezeGland";
                case Drake.Fire: return "SurtlingCore";
                case Drake.Poison: return "Ooze";
                case Drake.Earth: return "Obsidian";
                case Drake.Lightning: return "Thunderstone";
            }
            return null;
        }

        // A drake hatched from an egg also drops one of the item its egg was woken with (user, 2026-11-17),
        // on top of what a drake drops anyway, and only frost ones keep the freeze glands. Wild drakes are left as they are.
        [HarmonyPatch(typeof(CharacterDrop), "GenerateDropList"), HarmonyPostfix]
        private static void ElementDrop(CharacterDrop __instance, List<KeyValuePair<GameObject, int>> __result)
        {
            if (!On() || __result == null || ObjectDB.instance == null) return;
            Character c = __instance.GetComponent<Character>();
            if (c == null || !Drake.Is(c)) return;
            ZNetView nv = c.GetComponent<ZNetView>();
            if (nv == null || !nv.IsValid()) return;
            string item = ItemOf(nv.GetZDO().GetInt(Drake.ElementKey, 0));
            GameObject prefab = item != null ? ObjectDB.instance.GetItemPrefab(item) : null;
            if (prefab == null) return;
            // only frost drakes keep the game's freeze glands (user, 2026-11-17)
            int gone = 0;
            if (item != "FreezeGland") gone = __result.RemoveAll(kv => kv.Key != null && kv.Key.name == "FreezeGland");
            if (gone > 0) Plugin.Debug("[egg] its freeze glands left out: not a frost drake");
            // no trophy from a hatched drake (user, 2026-11-17): otherwise eggs would be a trophy farm
            if (__result.RemoveAll(kv => kv.Key != null && kv.Key.name == "TrophyHatchling") > 0) Plugin.Debug("[egg] no trophy: it hatched from an egg");
            __result.Add(new KeyValuePair<GameObject, int>(prefab, 1));
            Plugin.Debug("[egg] a " + Drake.ElementName(nv.GetZDO().GetInt(Drake.ElementKey, 0)) + " drake died: it also drops 1 " + item);
        }

        [HarmonyPatch(typeof(ItemDrop), "UseItem"), HarmonyPrefix]
        private static bool GiveItem(ItemDrop __instance, Humanoid user, ItemDrop.ItemData item, ref bool __result)
        {
            if (!On() || !IsEgg(__instance.gameObject)) return true;
            Player p = user as Player;
            ZNetView nv = __instance.GetComponent<ZNetView>();
            if (p == null || nv == null || !nv.IsValid()) return true;
            __result = true;
            if (!ModerDead()) { p.Message(MessageHud.MessageType.Center, "The egg is cold and still. Its mother still lives."); return false; }
            if (__instance.m_itemData.m_stack > 1) { p.Message(MessageHud.MessageType.Center, "Lay the eggs down one by one."); return false; }
            if (nv.GetZDO().GetInt(Drake.ElementKey, 0) != 0) { p.Message(MessageHud.MessageType.Center, "This egg is already awake."); return false; }
            int el = ElementOf(item);
            if (el == 0) { p.Message(MessageHud.MessageType.Center, "The egg doesn't stir. It wants something else."); return false; }
            if (!nv.IsOwner()) nv.ClaimOwnership();
            nv.GetZDO().Set(Drake.ElementKey, el);
            nv.GetZDO().Set(WarmKey, 0f);
            p.GetInventory().RemoveItem(item, 1);
            p.Message(MessageHud.MessageType.Center, "The egg stirs: a " + Drake.ElementName(el).ToLowerInvariant() + " drake grows inside."
                + (Plugin.DrakeEggNeedsFire.Value ? " Keep it warm by a fire." : ""));
            Plugin.Debug("[egg] woken with " + item.m_dropPrefab.name + ": element " + el);
            return false;
        }

        private static string Choices()
        {
            return "Freeze Gland or Crystal (frost), Surtling Core (fire), Ooze (poison), Obsidian (earth), Thunderstone (lightning).";
        }

        // ------------------------------------------------------------ laying the egg by a fire

        // looking at a campfire, brazier or hearth, the egg's hotbar key lays it on the ground beside the fire,
        // on your side of it, close enough to keep it warm
        [HarmonyPatch(typeof(Fireplace), "UseItem"), HarmonyPrefix]
        private static bool LayByFire(Fireplace __instance, Humanoid user, ItemDrop.ItemData item, ref bool __result)
        {
            if (!On() || item == null || item.m_dropPrefab == null || item.m_dropPrefab.name != EggPrefab) return true;
            Player p = user as Player;
            if (p == null) return true;
            __result = true;
            Vector3 fire = __instance.transform.position;
            Vector3 side = p.transform.position - fire;
            side.y = 0f;
            if (side.sqrMagnitude < 0.01f) side = Vector3.forward;
            side.Normalize();
            Collider col = __instance.GetComponentInChildren<Collider>();
            float reach = col != null ? Mathf.Max(col.bounds.extents.x, col.bounds.extents.z) : 0.6f;
            Vector3 at = fire + side * Mathf.Clamp(reach + 0.45f, 0.8f, 2.2f);
            RaycastHit hit;
            if (Physics.Raycast(at + Vector3.up * 2f, Vector3.down, out hit, 6f, LayerMask.GetMask("terrain", "Default", "static_solid", "piece"), QueryTriggerInteraction.Ignore))
                at = hit.point + Vector3.up * 0.15f;
            ItemDrop egg = ItemDrop.DropItem(item, 1, at, Quaternion.LookRotation(-side));
            if (egg == null) return false;
            p.GetInventory().RemoveItem(item, 1);
            p.Message(MessageHud.MessageType.Center, ModerDead()
                ? (Plugin.DrakeEggNeedsFire.Value ? "You lay the egg by the fire. Make it an offering to wake it." : "You lay the egg down. Make it an offering to wake it.")
                : "You lay the egg by the fire, but it stays cold: its mother still lives.");
            Plugin.Debug("[egg] laid by " + __instance.name + " at " + at);
            return false;
        }

        // ------------------------------------------------------------ what you see on the egg

        [HarmonyPatch(typeof(ItemDrop), "GetHoverText"), HarmonyPostfix]
        private static void Hover(ItemDrop __instance, ref string __result)
        {
            if (!On() || !IsEgg(__instance.gameObject)) return;
            ZNetView nv = __instance.GetComponent<ZNetView>();
            if (nv == null || !nv.IsValid()) return;
            if (DrakeBreed.Laid(__instance.m_itemData)) __result += "\n<color=#c8b48a>Laid by your drakes</color>";
            if (!ModerDead()) return;
            int el = nv.GetZDO().GetInt(Drake.ElementKey, 0);
            // what to offer is for the player to find out, or for Hugin to hint (user, 2026-11-17)
            if (el == 0) { __result += "\n[<color=yellow><b>1-8</b></color>] <color=#9fd8ff>Make an offering</color>"; Tips.Once("egg"); return; }
            float warm = nv.GetZDO().GetFloat(WarmKey, 0f), need = HatchSeconds;
            string state = Warm(__instance) ? "warm" : "cold: lay it by a fire";
            __result += "\n<color=#9fd8ff>" + Drake.ElementName(el) + " drake egg, " + state + ". Hatching " + Mathf.FloorToInt(100f * Mathf.Clamp01(warm / need)) + "%</color>";
        }

        // ------------------------------------------------------------ hatching

        private static float HatchSeconds { get { return Mathf.Max(10f, Plugin.DrakeHatchMinutes.Value * 60f); } }

        private static bool Warm(ItemDrop egg)
        {
            if (!Plugin.DrakeEggNeedsFire.Value) return true;
            return EffectArea.IsPointInsideArea(egg.transform.position, EffectArea.Type.Heat, 0.5f) != null;
        }

        // how long the fires keeping it warm can still burn (seconds); a fire that never runs out, or heat that isn't
        // a fire, counts as forever
        private const float Forever = 1e9f;
        private static float BurnLeft(ItemDrop egg)
        {
            if (!Plugin.DrakeEggNeedsFire.Value) return Forever;
            float best = 0f;
            foreach (Collider col in Physics.OverlapSphere(egg.transform.position, 0.5f, ~0, QueryTriggerInteraction.Collide))
            {
                EffectArea area = col != null ? col.GetComponent<EffectArea>() : null;
                if (area == null || (area.m_type & EffectArea.Type.Heat) == 0) continue;
                Fireplace f = col.GetComponentInParent<Fireplace>();
                if (f == null || f.m_infiniteFuel) return Forever;
                ZNetView fv = f.GetComponent<ZNetView>();
                if (fv == null || !fv.IsValid()) continue;
                best = Mathf.Max(best, fv.GetZDO().GetFloat(ZDOVars.s_fuel, 0f) * f.m_secPerFuel);
            }
            return best;
        }

        [HarmonyPatch(typeof(ZNetScene), "Awake"), HarmonyPostfix]
        private static void AddWarmer(ZNetScene __instance)
        {
            GameObject egg = __instance.GetPrefab(EggPrefab);
            if (egg != null && egg.GetComponent<EggWarmer>() == null) egg.AddComponent<EggWarmer>();
        }

        // on every dragon egg: counts the time it's kept warm, and hatches it (on the game that owns the egg)
        internal class EggWarmer : MonoBehaviour
        {
            private ItemDrop _item;
            private ZNetView _nv;
            private float _next;

            private void Awake()
            {
                _item = GetComponent<ItemDrop>();
                _nv = GetComponent<ZNetView>();
                _next = Time.time + Random.Range(1f, 5f);
            }

            private void Update()
            {
                if (Time.time < _next) return;
                const float step = 5f;
                _next = Time.time + step;
                if (!On() || _item == null || _nv == null || !_nv.IsValid()) return;
                if (DrakeBreed.Nested(_nv)) DrakeBreed.Stand(_item);   // laid on a nest: stands there until taken
                if (!_nv.IsOwner()) return;
                ZDO z = _nv.GetZDO();
                int el = z.GetInt(Drake.ElementKey, 0);
                if (el == 0 || _item.m_itemData.m_stack > 1 || !ModerDead() || ZNet.instance == null) return;
                long nowMs = (long)(ZNet.instance.GetTimeSeconds() * 1000.0);
                long lastMs = z.GetLong(WarmAtKey, 0L);
                float add = 0f;
                // back after a while away: the time gone by, while its fire could still burn
                if (lastMs > 0L)
                {
                    float gap = (nowMs - lastMs) / 1000f;
                    if (gap > step * 3f)
                    {
                        add = Mathf.Min(gap, z.GetFloat(BurnKey, 0f));
                        if (add > 0f) Plugin.Debug("[egg] " + Drake.ElementName(el) + " egg: " + Mathf.RoundToInt(gap / 60f) + " min away, " + Mathf.RoundToInt(add / 60f) + " min of it kept warm");
                    }
                }
                bool warmNow = Warm(_item);
                if (warmNow) { add = Mathf.Max(add, step); z.Set(WarmAtKey, nowMs); z.Set(BurnKey, BurnLeft(_item)); }
                else if (lastMs != 0L) z.Set(WarmAtKey, 0L);
                if (add <= 0f) return;
                float warm = z.GetFloat(WarmKey, 0f) + add;
                z.Set(WarmKey, warm);
                if (warm >= HatchSeconds) Hatch(el);
            }

            private void Hatch(int el)
            {
                GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(Drake.BabyPrefab) : null;
                if (prefab == null) { Plugin.Log.LogWarning("No " + Drake.BabyPrefab + " prefab: the egg can't hatch."); return; }
                Vector3 at = transform.position + Vector3.up * 0.3f;
                GameObject go = Instantiate(prefab, at, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f));
                Character baby = go.GetComponent<Character>();
                ZNetView nv = go.GetComponent<ZNetView>();
                if (baby == null || nv == null || !nv.IsValid()) return;
                Drake.MakeBaby(baby, nv, el, at, _item != null ? _item.m_itemData : null);
                GameObject boar = ZNetScene.instance.GetPrefab("Boar");
                Procreation pr = boar != null ? boar.GetComponent<Procreation>() : null;
                if (pr != null && pr.m_birthEffects != null) pr.m_birthEffects.Create(at, Quaternion.identity);
                Player p = Player.m_localPlayer;
                if (p != null && Vector3.Distance(p.transform.position, at) < 30f)
                    p.Message(MessageHud.MessageType.Center, "A " + Drake.ElementName(el).ToLowerInvariant() + " drake hatched!"
                        + (nv.GetZDO().GetInt(Drake.GlowKey, 0) == 1 ? " Its eyes and horns glow!" : ""));
                Plugin.Debug("[egg] hatched a " + Drake.ElementName(el) + " drake at " + at + " (see its stars on it)");
                _nv.Destroy();
            }
        }
    }
}
