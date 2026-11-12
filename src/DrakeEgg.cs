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

        internal static bool IsEgg(GameObject go) { return go != null && Utils.GetPrefabName(go) == EggPrefab; }

        private static bool ModerDead()
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
            if (el == 0) { p.Message(MessageHud.MessageType.Center, "The egg doesn't take that. " + Choices()); return false; }
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
                ? (Plugin.DrakeEggNeedsFire.Value ? "You lay the egg by the fire. Give it an item to wake it." : "You lay the egg down. Give it an item to wake it.")
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
            if (nv == null || !nv.IsValid() || !ModerDead()) return;
            int el = nv.GetZDO().GetInt(Drake.ElementKey, 0);
            if (el == 0) { __result += "\n<color=#9fd8ff>Give it an item to wake it (hotbar key while looking at it):</color>\n" + Choices(); return; }
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
                if (!On() || _item == null || _nv == null || !_nv.IsValid() || !_nv.IsOwner()) return;
                ZDO z = _nv.GetZDO();
                int el = z.GetInt(Drake.ElementKey, 0);
                if (el == 0 || _item.m_itemData.m_stack > 1 || !ModerDead() || !Warm(_item)) return;
                float warm = z.GetFloat(WarmKey, 0f) + step;
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
                Drake.MakeBaby(baby, nv, el, at);
                GameObject boar = ZNetScene.instance.GetPrefab("Boar");
                Procreation pr = boar != null ? boar.GetComponent<Procreation>() : null;
                if (pr != null && pr.m_birthEffects != null) pr.m_birthEffects.Create(at, Quaternion.identity);
                Player p = Player.m_localPlayer;
                if (p != null && Vector3.Distance(p.transform.position, at) < 30f)
                    p.Message(MessageHud.MessageType.Center, "A " + Drake.ElementName(el).ToLowerInvariant() + " drake hatched!"
                        + (nv.GetZDO().GetInt(Drake.GlowKey, 0) == 1 ? " Its eyes and horns glow!" : ""));
                Plugin.Debug("[egg] hatched a " + Drake.ElementName(el) + " drake at " + at);
                _nv.Destroy();
            }
        }
    }
}
