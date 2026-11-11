using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Beastkeeper
{
    // Saddle bags on the pack animals (deer and lox): hold E on a saddled one to open them, like a chest.
    // The bags are the game's own container, kept on the animal itself (in its saved data), so what you put in
    // stays when you leave and comes back with the world. Only players with the mod can open them; nothing is
    // lost for the others. If the animal dies, everything in its bags falls to the ground.
    [HarmonyPatch]
    internal static class Bags
    {
        private static readonly Dictionary<string, Vector2Int> Size = new Dictionary<string, Vector2Int>(System.StringComparer.OrdinalIgnoreCase)
        {
            { "Deer", new Vector2Int(5, 2) },   // like a wooden chest
            { "Lox", new Vector2Int(6, 3) },
        };
        private static readonly MethodInfo DropAll = AccessTools.Method(typeof(Container), "DropAllItems", new System.Type[0]);
        private static Sprite _bkg;

        internal static bool IsPacker(Character c) { return c != null && Size.ContainsKey(Mount.Kind(c.gameObject)); }

        private static Container Of(Character c)
        {
            Transform t = c != null ? c.transform.Find("Beastkeeper_Bags") : null;
            return t != null ? t.GetComponent<Container>() : null;
        }

        // on every pack animal as it loads (only the inventory lives in its saved data, the bags object is ours)
        internal static void Attach(Character c)
        {
            Vector2Int size;
            if (c == null || !Size.TryGetValue(Mount.Kind(c.gameObject), out size)) return;
            ZNetView nv = Leash.View(c);
            if (nv == null || nv.GetZDO() == null || Of(c) != null) return;
            GameObject g = new GameObject("Beastkeeper_Bags");
            g.SetActive(false);
            g.transform.SetParent(c.transform, false);
            Container ct = g.AddComponent<Container>();
            ct.m_name = "Saddle bags";
            ct.m_width = size.x;
            ct.m_height = size.y;
            ct.m_rootObjectOverride = nv;
            ct.m_privacy = Container.PrivacySetting.Public;
            ct.m_checkGuardStone = false;
            ct.m_autoDestroyEmpty = false;
            ct.m_defaultItems = new DropTable();
            ct.m_bkg = Background();
            g.SetActive(true);
        }

        // the wooden chest's backdrop for the bags window
        private static Sprite Background()
        {
            if (_bkg != null || ZNetScene.instance == null) return _bkg;
            GameObject chest = ZNetScene.instance.GetPrefab("piece_chest_wood");
            Container cc = chest != null ? chest.GetComponent<Container>() : null;
            return _bkg = cc != null ? cc.m_bkg : null;
        }

        internal static bool Empty(Character c)
        {
            Container ct = Of(c);
            return ct == null || ct.GetInventory() == null || ct.GetInventory().NrOfItems() == 0;
        }

        internal static void Open(Player p, Character c)
        {
            if (!IsPacker(c)) return;
            Container ct = Of(c);
            if (ct == null) { p.Message(MessageHud.MessageType.Center, "These bags aren't ready yet."); return; }
            if (ct.m_bkg == null) ct.m_bkg = Background();
            ct.Interact(p, false, false);
            Plugin.Debug("[bags] opening " + c.name + "'s bags (" + ct.m_width + "x" + ct.m_height + ")");
        }

        // empties the bags onto the ground (on the game that owns the animal)
        internal static void SpillNow(Character c)
        {
            Container ct = Of(c);
            if (ct == null || Empty(c) || DropAll == null) return;
            DropAll.Invoke(ct, null);
        }

        // when a pack animal dies, its bags spill on the ground (on the game that owns it)
        [HarmonyPatch(typeof(Character), "OnDeath"), HarmonyPrefix]
        private static void Spill(Character __instance)
        {
            if (!Plugin.Enabled.Value || !IsPacker(__instance)) return;
            ZNetView nv = Leash.View(__instance);
            Container ct = Of(__instance);
            if (nv == null || !nv.IsValid() || !nv.IsOwner() || ct == null || Empty(__instance) || DropAll == null) return;
            DropAll.Invoke(ct, null);
            Plugin.Debug("[bags] " + __instance.name + " died: bags spilled");
        }
    }
}
