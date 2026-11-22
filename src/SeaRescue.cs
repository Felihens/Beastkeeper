using System.Collections;
using HarmonyLib;
using UnityEngine;

namespace Beastkeeper
{
    // After a drake throws its rider into the sea (SeaCourage), 2026-11-22, agreed with the user:
    // - it doesn't land on the water: it flies low over the waves beside its rider and follows them while they swim
    //   (user: low, so they can get back on; landing would sink it);
    // - nobody can get back on it over deep water while its courage is gone (user: only a new offering, so swim to land);
    // - once both are back over land it's an ordinary drake again;
    // - if the rider dies at sea while it follows, it shines, rises in a beam of light and goes into the rider's
    //   totems (as their favorite, or with the other stored animals even past the limit of 10).
    // Only this case: dying anywhere else, or with another animal, changes nothing.
    [HarmonyPatch]
    internal static class SeaRescue
    {
        internal static readonly int RiderKey = "Beastkeeper_seaRider".GetStableHashCode();
        private const float Hover = 2f;          // how high above the water it flies beside its rider (metres)
        private const float Beside = 2.5f;       // how far to its rider's side
        private const float Near = 1.5f;         // closer than this (flat) to its spot it only keeps its height

        internal static long Following(Character c)
        {
            ZNetView nv = Leash.View(c);
            return nv != null && nv.IsValid() ? nv.GetZDO().GetLong(RiderKey, 0L) : 0L;
        }

        // on the game that owns the drake (its rider's, just thrown off)
        internal static void Mark(Character c, Player p)
        {
            ZNetView nv = Leash.View(c);
            if (c == null || p == null || nv == null || !nv.IsValid()) return;
            if (!nv.IsOwner()) nv.ClaimOwnership();
            nv.GetZDO().Set(RiderKey, p.GetPlayerID());
        }

        internal static void Clear(Character c, string why)
        {
            ZNetView nv = Leash.View(c);
            if (nv == null || !nv.IsValid() || nv.GetZDO().GetLong(RiderKey, 0L) == 0L) return;
            if (!nv.IsOwner()) return;
            nv.GetZDO().Set(RiderKey, 0L);
        }

        private static bool AtSea(Vector3 p) { return Drake.WaterDepth(p) > Drake.DeepWater; }

        private static float Flat(Vector3 v) { v.y = 0f; return v.magnitude; }

        // getting on over deep water without courage: refused (true = refused)
        internal static bool RefuseRide(Player p, Character c)
        {
            if (!Drake.Is(c) || SeaCourage.Mode != SeaCourage.Offering || SeaCourage.Left(c) > 0f) return false;
            if (!AtSea(c.transform.position) && !AtSea(p.transform.position)) return false;
            p.Message(MessageHud.MessageType.Center, c.GetHoverName() + " won't let you on over the sea: its courage is gone. Swim to land.");
            return true;
        }

        // the drake's own AI is replaced while it follows its fallen rider. It runs after the mod's other AI prefixes
        // (Home's favorite-follow, the lasso...), so its steering is the one that counts.
        [HarmonyPatch(typeof(MonsterAI), "UpdateAI"), HarmonyPrefix, HarmonyPriority(Priority.Last)]
        private static bool Follow(MonsterAI __instance, float dt, Character ___m_character, ZNetView ___m_nview, ref bool __result)
        {
            Character c = ___m_character;
            if (!Plugin.Enabled.Value || c == null || ___m_nview == null || !___m_nview.IsValid() || !___m_nview.IsOwner()) return true;
            long id = ___m_nview.GetZDO().GetLong(RiderKey, 0L);
            if (id == 0L || !Drake.Is(c) || c.IsDead()) return true;
            if (Mount.IsRidden(c)) { Clear(c, "ridden again"); return true; }
            Player p = Leash.FindPlayer(id);
            if (p == null) { Clear(c, "its rider is gone"); return true; }
            __result = true;
            if (p.IsDead()) return false;   // SeaRescue.Died takes it away
            Vector3 here = c.transform.position, them = p.transform.position;
            if (!AtSea(here) && !AtSea(them)) { Clear(c, "back over land"); return true; }

            if (!c.IsFlying()) c.TakeOff();
            Vector3 side = Vector3.Cross(Vector3.up, p.transform.forward);
            if (side.sqrMagnitude < 0.01f) side = Vector3.right;
            Vector3 target = them + side.normalized * Beside;
            target.y = Mathf.Max(ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : them.y, them.y) + Hover;
            Vector3 flat = target - here;
            flat.y = 0f;
            float rise = Mathf.Clamp((target.y - here.y) * 0.25f, -1f, 1f);
            Vector3 dir = (flat.magnitude > Near ? flat.normalized : Vector3.zero) + Vector3.up * rise;
            if (dir.sqrMagnitude > 1f) dir.Normalize();
            c.SetMoveDir(dir);
            c.SetRun(flat.magnitude > 15f);
            Vector3 look = flat.sqrMagnitude > 0.5f ? flat.normalized : (them - here);
            look.y = 0f;
            if (look.sqrMagnitude > 0.01f) c.SetLookDir(look.normalized, 0f);
            return false;
        }

        // ------------------------------------------------------------ the rider dies at sea

        [HarmonyPatch(typeof(Player), "OnDeath"), HarmonyPrefix]
        private static void Died(Player __instance)
        {
            if (!Plugin.Enabled.Value || __instance == null || __instance != Player.m_localPlayer) return;
            try
            {
                long me = __instance.GetPlayerID();
                foreach (Character c in Character.GetAllCharacters())
                {
                    if (c == null || c.IsDead() || !Drake.Is(c) || Following(c) != me) continue;
                    // written down now, while the character is still alive (it's saved with the death);
                    // it leaves the world a moment later, while the camera pulls away
                    if (!Favorite.StoreFromSea(__instance, c)) continue;
                    Plugin.Instance.StartCoroutine(Leave(c));
                }
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("[sea] " + e); }
        }

        private static IEnumerator Leave(Character c)
        {
            yield return new WaitForSeconds(1.2f);
            if (c == null) yield break;
            Glow.Leave(c, true);
        }
    }
}
