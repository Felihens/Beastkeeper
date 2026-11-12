using HarmonyLib;
using UnityEngine;

namespace Beastkeeper
{
    // Tamed animals are tougher and hit harder than wild ones: the game's own boar has 10 health and dies to
    // anything it meets on the way. Each tier (Leather .. Carapace, see Tiers) has a lowest health for a tamed
    // animal (the game's own is kept when it is higher), times its stars, and a multiplier for the damage it
    // deals. The serpent and players are left alone.
    [HarmonyPatch]
    internal static class Toughness
    {
        private static float[] _health, _damage;
        private static string _hSeen, _dSeen;

        private static float[] Parse(string s, ref string seen, ref float[] cache, float fallback)
        {
            if (cache != null && s == seen) return cache;
            seen = s;
            string[] parts = (s ?? "").Split(',');
            float[] r = new float[Tiers.Max + 1];
            for (int i = 1; i <= Tiers.Max; i++)
            {
                float v;
                r[i] = i - 1 < parts.Length && float.TryParse(parts[i - 1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) ? v : fallback;
            }
            cache = r;
            return r;
        }

        private static bool Applies(Character c)
        {
            return c != null && Plugin.Enabled.Value && !c.IsPlayer() && c.IsTamed() && !Sea.IsSeaMount(c) && !c.IsBoss();
        }

        internal static float HealthFloor(Character c)
        {
            return Parse(Plugin.TamedHealth.Value, ref _hSeen, ref _health, 0f)[Mathf.Clamp(Tiers.Animal(c), 1, Tiers.Max)];
        }

        internal static float DamageFactor(Character c)
        {
            return Mathf.Max(0.1f, Parse(Plugin.TamedDamage.Value, ref _dSeen, ref _damage, 1f)[Mathf.Clamp(Tiers.Animal(c), 1, Tiers.Max)]);
        }

        [HarmonyPatch(typeof(Character), "GetMaxHealthBase"), HarmonyPostfix]
        private static void Base(Character __instance, ref float __result)
        {
            if (!Applies(__instance)) return;
            __result = Mathf.Max(__result, HealthFloor(__instance));
        }

        // just tamed: its health goes up at once (the game only sets it when the animal loads)
        [HarmonyPatch(typeof(Character), "RPC_SetTamed"), HarmonyPostfix]
        private static void Tamed(Character __instance)
        {
            if (!Applies(__instance)) return;
            ZNetView nv = Leash.View(__instance);
            if (nv == null || !nv.IsValid() || !nv.IsOwner()) return;
            Refresh(__instance);
        }

        [HarmonyPatch(typeof(Humanoid), "Start"), HarmonyPostfix]
        private static void LoadedHumanoid(Humanoid __instance) { Loaded(__instance); }

        [HarmonyPatch(typeof(Character), "Start"), HarmonyPostfix]
        private static void Loaded(Character __instance)
        {
            if (!Applies(__instance)) return;
            ZNetView nv = Leash.View(__instance);
            if (nv == null || !nv.IsValid() || !nv.IsOwner()) return;
            Refresh(__instance);
        }

        private static void Refresh(Character c)
        {
            float before = c.GetMaxHealth();
            Traverse.Create(c).Method("SetupMaxHealth").GetValue();
            float after = c.GetMaxHealth();
            if (after > before + 0.5f && c.GetHealth() >= before - 0.5f) c.SetHealth(after);   // it was at full: it stays at full
            if (after > before + 0.5f) Plugin.Debug("[tough] " + c.name + " (tier " + Tiers.Animal(c) + "): health " + before.ToString("0") + " -> " + after.ToString("0"));
        }

        // what a tamed animal hits for (its bite, kick, horns; not a drake's spit, which has its own setting)
        [HarmonyPatch(typeof(Character), "Damage"), HarmonyPrefix, HarmonyPriority(Priority.High)]
        private static void Harder(HitData hit)
        {
            if (hit == null) return;
            Character a = hit.GetAttacker();
            if (!Applies(a) || hit.m_ranged) return;
            hit.m_damage.Modify(DamageFactor(a));
        }
    }
}
