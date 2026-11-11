using HarmonyLib;
using UnityEngine;

namespace Beastkeeper
{
    // Names for animals tamed with a halter that the game can't tame itself (deer, bear...): the game's own
    // rename lives in Tameable, which they don't have. Shift + E opens the game's name box; the name is kept in
    // the animal's data, so every player with the mod sees it.
    [HarmonyPatch]
    internal static class Rename
    {
        private static readonly int NameKey = "Beastkeeper_name".GetStableHashCode();

        // animals that use our rename instead of the game's
        internal static bool Ours(Character c)
        {
            return c != null && !c.IsPlayer() && c.GetComponent<Tameable>() == null && c.IsTamed() && !Sea.IsSeaMount(c);
        }

        private static string Name(Character c)
        {
            ZNetView nv = Leash.View(c);
            return nv != null && nv.IsValid() ? nv.GetZDO().GetString(NameKey, "") : "";
        }

        internal static void Ask(Character c)
        {
            if (TextInput.instance == null || c == null) return;
            TextInput.instance.RequestText(new Box(c), "$hud_rename", 10);
        }

        private class Box : TextReceiver
        {
            private readonly Character _c;
            internal Box(Character c) { _c = c; }
            public string GetText() { return Name(_c); }
            public void SetText(string text)
            {
                ZNetView nv = Leash.View(_c);
                if (nv == null || !nv.IsValid()) return;
                if (!nv.IsOwner()) nv.ClaimOwnership();
                nv.GetZDO().Set(NameKey, (text ?? "").Trim());
                Plugin.Debug("[rename] " + _c.name + " is now " + text);
            }
        }

        [HarmonyPatch(typeof(Character), "GetHoverName"), HarmonyPostfix]
        private static void ShowName(Character __instance, ref string __result)
        {
            if (!Plugin.Enabled.Value || __instance.IsPlayer() || __instance.GetComponent<Tameable>() != null) return;
            string n = Name(__instance);
            if (n.Length > 0) __result = n;
        }

        // Shift + E on a halter-tamed animal without a saddle (a saddled one goes through Mount's press)
        [HarmonyPatch(typeof(Player), "Interact"), HarmonyPrefix, HarmonyPriority(Priority.Low)]
        private static bool Press(Player __instance, GameObject go, bool hold, bool alt, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            if (!Plugin.Enabled.Value || !alt || hold || __instance != Player.m_localPlayer || go == null) return true;
            Character c = go.GetComponentInParent<Character>();
            if (!Ours(c) || Mount.Saddled(c) || Tie.Busy(__instance, c)) return true;
            Ask(c);
            return false;
        }

        internal static string HoverLine(Character c)
        {
            return Plugin.Enabled.Value && Ours(c) && !Mount.Saddled(c) ? "\n[<color=yellow><b>Shift + E</b></color>] Rename" : "";
        }
    }
}
