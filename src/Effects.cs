using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Beastkeeper
{
    // Feel of the tug of war:
    //  - while you dig your heels in (pressing S), your feet throw up dust and make the sliding sound, the
    //    same effect the game plays when you slide down a steep slope. Other players with the mod see it too
    //    (a flag on your player ZDO);
    //  - a camera shake at a few chosen moments (the animal yanks, you miss a yank, the catch, it breaks free,
    //    the rope slips off).
    [HarmonyPatch]
    internal static class Effects
    {
        internal static readonly int DigKey = "Beastkeeper_dig".GetStableHashCode();
        private static readonly FieldInfo Sliding = AccessTools.Field(typeof(Character), "m_sliding");
        private static bool _digging;

        internal static void SetDigging(Player p, bool on)
        {
            if (p == null || on == _digging) return;
            _digging = on;
            ZNetView nv = p.GetComponent<ZNetView>();
            if (nv != null && nv.IsValid() && nv.IsOwner()) nv.GetZDO().Set(DigKey, on ? 1 : 0);
        }

        internal static void Shake(float strength)
        {
            Player p = Player.m_localPlayer;
            if (p == null || GameCamera.instance == null || Plugin.ShakeStrength.Value <= 0f) return;
            GameCamera.instance.AddShake(p.transform.position, 10f, strength * Plugin.ShakeStrength.Value, false);
        }

        [HarmonyPatch(typeof(Character), "UpdateContinousEffects"), HarmonyPrefix]
        private static void Before(Character __instance, out bool __state)
        {
            __state = false;
            if (!Plugin.Enabled.Value || Sliding == null) return;
            Player p = __instance as Player;
            if (p == null) return;
            bool dig = p == Player.m_localPlayer ? _digging : RemoteDigging(p);
            if (!dig || (bool)Sliding.GetValue(__instance)) return;
            __state = true;
            Sliding.SetValue(__instance, true);
        }

        [HarmonyPatch(typeof(Character), "UpdateContinousEffects"), HarmonyPostfix]
        private static void After(Character __instance, bool __state)
        {
            if (__state) Sliding.SetValue(__instance, false);
        }

        private static bool RemoteDigging(Player p)
        {
            ZNetView nv = p.GetComponent<ZNetView>();
            return nv != null && nv.IsValid() && nv.GetZDO().GetInt(DigKey, 0) == 1;
        }
    }
}
