using System.Collections.Generic;
using UnityEngine;

namespace Beastkeeper
{
    // Tamed animals defend themselves and their home. The game already has tamed fighters (wolf, boar, bear,
    // asksvin, lox...) attack enemies; while this mod leads, ties or keeps an animal near home it used to keep it
    // calm all the time, so it never fought. Now when a threat comes within DefendRange (of the animal, or of the
    // reach of its rope when tied) the mod steps back and the game's own fighting takes over; once the threat is
    // gone the animal goes back to what it was doing.
    // A threat: a living creature that isn't tamed, is the animal's enemy by the game's rules, and can fight
    // (deer, hares and other prey don't count). Animals with no attacks of their own (deer, hen, hare) keep away.
    internal static class Defend
    {
        private class Look { public bool Threat; public float Until; }
        private static readonly Dictionary<Character, Look> Looks = new Dictionary<Character, Look>();
        private static readonly Dictionary<Character, bool> Fighters = new Dictionary<Character, bool>();

        // the animal has attacks of its own
        internal static bool CanFight(Character c)
        {
            bool f;
            if (Fighters.TryGetValue(c, out f)) return f;
            f = false;
            Humanoid h = c as Humanoid;
            if (h != null && h.GetInventory() != null)
                foreach (ItemDrop.ItemData it in h.GetInventory().GetAllItems())
                    if (it != null && it.IsWeapon() && it.m_shared.m_attack != null && !string.IsNullOrEmpty(it.m_shared.m_attack.m_attackAnimation)) { f = true; break; }
            if (Fighters.Count > 300) Fighters.Clear();
            Fighters[c] = f;
            return f;
        }

        // post: where it's tied (its rope reaches Tie.Length around it), or null
        internal static bool Active(BaseAI ai, Character c, Vector3? post)
        {
            if (!Plugin.TamedDefend.Value || !(ai is MonsterAI) || c == null || !c.IsTamed() || Sea.IsSeaMount(c) || !CanFight(c)) return false;
            Look l;
            if (Looks.TryGetValue(c, out l) && Time.time < l.Until) return l.Threat;
            if (l == null) { l = new Look(); Looks[c] = l; if (Looks.Count > 300) Looks.Clear(); }
            l.Until = Time.time + 0.75f;
            l.Threat = false;
            float range = Plugin.DefendRange.Value;
            foreach (Character o in Character.GetAllCharacters())
            {
                if (o == null || o == c || o.IsDead() || o.IsPlayer() || o.IsTamed()) continue;
                BaseAI oai = o.GetBaseAI();
                if (oai == null || oai is AnimalAI) continue;   // prey: deer, hares, fish...
                if (!BaseAI.IsEnemy(c, o)) continue;
                Vector3 at = post.HasValue ? post.Value : c.transform.position;
                Vector3 d = o.transform.position - at;
                d.y = 0f;
                float dist = d.magnitude - (post.HasValue ? Tie.Length : 0f);
                if (dist <= range) { l.Threat = true; break; }
            }
            if (l.Threat) Plugin.Debug("[defend] " + c.name + " sees a threat: it fights");
            return l.Threat;
        }
    }
}
