using System;
using HarmonyLib;
using UnityEngine;

namespace Beastkeeper
{
    // A saddle protects the animal wearing it (2026-11-21, agreed with the user):
    // - its armor (per saddle tier) works like a player's: taken off after the animal's own resistances;
    // - a saddled animal is never caught by a sneak attack (the game multiplies a hit on a creature that
    //   isn't alerted, and a tamed animal almost never is: a drake lost 324 of its 350 health to one sting).
    // Tamed health (Toughness) is sized so the animal, wearing the saddle of its own tier, dies to about five
    // hits of its biome's strong enemy. The serpent and players are left alone.
    [HarmonyPatch]
    internal static class SaddleArmor
    {
        [ThreadStatic] private static Character _hit;
        private static float[] _armor;
        private static string _seen;

        internal static float Armor(Character c)
        {
            int t = Mount.SaddleTier(c);
            if (t <= 0) return 0f;
            string s = Plugin.SaddleArmor.Value;
            if (_armor == null || s != _seen)
            {
                _seen = s;
                string[] parts = (s ?? "").Split(',');
                float[] r = new float[Tiers.Saddles + 1];
                for (int i = 1; i <= Tiers.Saddles; i++)
                {
                    float v;
                    r[i] = i - 1 < parts.Length && float.TryParse(parts[i - 1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) ? Mathf.Max(0f, v) : 0f;
                }
                _armor = r;
            }
            return _armor[Mathf.Clamp(t, 1, Tiers.Saddles)];
        }

        private static bool Applies(Character c)
        {
            return c != null && Plugin.Enabled.Value && !c.IsPlayer() && c.IsTamed() && !Sea.IsSeaMount(c) && Mount.Saddled(c);
        }

        [HarmonyPatch(typeof(Character), "RPC_Damage"), HarmonyPrefix]
        private static void Before(Character __instance, HitData hit)
        {
            _hit = null;
            if (hit == null || !Applies(__instance)) return;
            hit.m_backstabBonus = 1f;
            _hit = __instance;
        }

        // RPC_Damage applies the resistances once, then (for players) the armor: ours goes in the same place
        [HarmonyPatch(typeof(HitData), "ApplyResistance"), HarmonyPostfix]
        private static void Resisted(HitData __instance)
        {
            Character c = _hit;
            if (c == null) return;
            _hit = null;
            float ac = Armor(c);
            if (ac > 0f) __instance.ApplyArmor(ac);
        }

        [HarmonyPatch(typeof(Character), "RPC_Damage"), HarmonyPostfix]
        private static void After() { _hit = null; }
    }
}
