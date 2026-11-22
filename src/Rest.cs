using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Beastkeeper
{
    // Tamed animals heal only near a Beast Totem (within its Range), 2026-11-21, agreed with the user: with saddle
    // armor and no sneak attacks mounts last much longer in a fight, so healing on the road would be too easy.
    // - the game's own slow healing (a full heal in an hour, only while fed) is replaced: near a totem an animal
    //   rests and heals fully in HealMinutes, fed or not; away from a totem it doesn't heal on its own;
    // - hand feeding works only near a totem (Feed).
    // The serpent and players are left alone.
    [HarmonyPatch]
    internal static class Rest
    {
        private const float CheckEvery = 2f;     // the game heals every 2 s too
        private const float HealMinutes = 5f;    // from no health to full while resting near a totem
        private static readonly Dictionary<int, float> _checked = new Dictionary<int, float>();

        internal static bool NearTotem(Character c)
        {
            return c != null && Totem.Nearest(c.transform.position, Plugin.TotemRange.Value) != null;
        }

        private static bool Ours(Character c)
        {
            return Plugin.Enabled.Value && c != null && !c.IsPlayer() && c.IsTamed() && !Sea.IsSeaMount(c);
        }

        // the game's own healing is off for our tamed animals
        [HarmonyPatch(typeof(BaseAI), "UpdateRegeneration"), HarmonyPrefix]
        private static bool Regen(Character ___m_character) { return !Ours(___m_character); }

        // ours runs after the AI update: a postfix runs even when Home, Leash or Mount skip the game's update (an
        // animal kept by a totem, tied or ridden), which also skips the game's healing
        [HarmonyPatch(typeof(MonsterAI), "UpdateAI"), HarmonyPostfix]
        private static void Monster(Character ___m_character, ZNetView ___m_nview) { Tick(___m_character, ___m_nview); }

        [HarmonyPatch(typeof(AnimalAI), "UpdateAI"), HarmonyPostfix]
        private static void Animal(Character ___m_character, ZNetView ___m_nview) { Tick(___m_character, ___m_nview); }

        private static void Tick(Character c, ZNetView nv)
        {
            if (!Ours(c) || nv == null || !nv.IsValid() || !nv.IsOwner()) return;
            int id = c.GetInstanceID();
            float now = Time.time, at;
            if (!_checked.TryGetValue(id, out at)) { _checked[id] = now; return; }
            float dt = now - at;
            if (dt < CheckEvery) return;
            _checked[id] = now;
            if (_checked.Count > 512) _checked.Clear();
            if (c.IsDead() || c.GetHealth() >= c.GetMaxHealth() || !NearTotem(c)) return;
            c.Heal(c.GetMaxHealth() * Mathf.Min(dt, 10f) / (HealMinutes * 60f), true);
        }
    }
}
