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
        private class Look { public bool Threat, Back, HasFrom; public float Until, BackUntil; public Vector3 From, BackTo; }

        // While fighting it may go this far past the chase limit (a drake circles its foe), and an enemy it already
        // fights may be this far past the limit before it lets go.
        private const float ChaseRoom = 12f, KeepFighting = 5f;
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

        // Creatures that could be a threat (alive, wild, with a fighting AI), gathered once for every animal
        // instead of each tamed animal going through every creature in the world.
        private static readonly List<Character> _threats = new List<Character>();
        private static float _threatsAt = -99f;
        internal static List<Character> Threats()
        {
            if (Time.time - _threatsAt < 0.5f) return _threats;
            _threatsAt = Time.time;
            _threats.Clear();
            foreach (Character o in Character.GetAllCharacters())
            {
                if (o == null || o.IsDead() || o.IsPlayer() || o.IsTamed()) continue;
                BaseAI oai = o.GetBaseAI();
                if (oai == null || oai is AnimalAI) continue;   // prey: deer, hares, fish...
                _threats.Add(o);
            }
            return _threats;
        }

        private static float Flat(Vector3 a, Vector3 b) { a.y = 0f; b.y = 0f; return Vector3.Distance(a, b); }

        // where it fights from: its rope's post, the player leading it, you (a favorite mount following you), its
        // master, else the spot where it was before the fight began. It chases no further than ChaseLimit from there.
        // gameLed: once it stops fighting, the game's own AI leads it (following its master, or loose with no
        // totem), and that would keep chasing; elsewhere this mod leads it back as soon as no enemy is in reach.
        private static Vector3 Anchor(BaseAI ai, Character c, Vector3? post, Look l, out bool fromSpot, out bool gameLed)
        {
            fromSpot = false;
            Vector3 at;
            if (FixedAnchor(ai, c, post, out at, out gameLed)) { l.HasFrom = false; return at; }
            fromSpot = true;
            gameLed = true;
            if (!l.HasFrom) { l.From = c.transform.position; l.HasFrom = true; }
            return l.From;
        }

        private static bool FixedAnchor(BaseAI ai, Character c, Vector3? post, out Vector3 at, out bool gameLed)
        {
            at = Vector3.zero;
            gameLed = false;
            if (post.HasValue) { at = post.Value; return true; }
            if (Orders.StayAt(c, out at)) return true;   // told to stay: it fights around that spot
            Player holder = Leash.FindPlayer(Leash.Holder(Fast.AIView(ai)));
            if (holder != null) { at = holder.transform.position; return true; }
            Player me;
            if (Home.FollowsYou(c, out me)) { at = me.transform.position; return true; }
            MonsterAI mai = ai as MonsterAI;
            GameObject ft = mai != null ? mai.GetFollowTarget() : null;
            if (ft != null) { at = ft.transform.position; gameLed = true; return true; }
            Totem totem = Totem.Nearest(c.transform.position, Plugin.TotemRange.Value);
            if (totem != null) { at = totem.transform.position; return true; }
            return false;
        }

        // A drake (and some other animals) runs from an enemy it hasn't been alerted by, which made a defender back
        // away from the very enemy it should fight. When it is to fight, it is alerted and doesn't run.
        private static readonly System.Reflection.MethodInfo SetAlertedM = HarmonyLib.AccessTools.Method(typeof(BaseAI), "SetAlerted");

        internal static void Ready(BaseAI ai, Character c)
        {
            MonsterAI mai = ai as MonsterAI;
            if (mai == null) return;
            if (mai.m_fleeIfNotAlerted) { mai.m_fleeIfNotAlerted = false; Plugin.Debug("[defend] " + c.name + " no longer runs from enemies before it is alerted"); }
            if (!ai.IsAlerted() && SetAlertedM != null) SetAlertedM.Invoke(ai, new object[] { true });
        }

        // gone too far after an enemy: where it walks back to
        internal static bool Returning(Character c, out Vector3 to)
        {
            Look l;
            to = Vector3.zero;
            if (c == null || !Looks.TryGetValue(c, out l) || !l.Back) return false;
            to = l.BackTo;
            return true;
        }

        // post: where it's tied (its rope reaches Tie.Length around it), or null
        internal static bool Active(BaseAI ai, Character c, Vector3? post)
        {
            if (!Plugin.TamedDefend.Value || !(ai is MonsterAI) || c == null || !c.IsTamed() || Sea.IsSeaMount(c) || !CanFight(c)) return false;
            Look l;
            if (Looks.TryGetValue(c, out l) && Time.time < l.Until) return l.Threat;
            if (l == null) { l = new Look(); Looks[c] = l; if (Looks.Count > 300) Looks.Clear(); }
            l.Until = Time.time + 0.75f;
            bool was = l.Threat, wasBack = l.Back;
            l.Threat = false;
            bool fromSpot, gameLed;
            Vector3 anchor = Anchor(ai, c, post, l, out fromSpot, out gameLed);
            float limit = Plugin.ChaseLimit.Value;
            float away = Flat(c.transform.position, anchor);
            // Sent back only where the game would keep it chasing, and only well past the limit; then it comes most
            // of the way back (and for at least a few seconds) before it may fight again, so it doesn't go to and fro.
            bool back = gameLed && (away > limit + ChaseRoom || (wasBack && (away > limit * 0.5f || Time.time < l.BackUntil)));
            if (back && !wasBack)
            {
                l.BackUntil = Time.time + 4f;
                Plugin.Debug("[defend] " + c.name + " chased " + away.ToString("0") + " m away: it comes back");
            }
            l.Back = back;
            l.BackTo = anchor;
            if (back) return false;
            float range = Plugin.DefendRange.Value;
            foreach (Character o in Threats())
            {
                if (o == null || o == c || o.IsDead()) continue;
                if (!BaseAI.IsEnemy(c, o)) continue;
                if (Flat(o.transform.position, anchor) > limit + (was ? KeepFighting : 0f)) continue;   // beyond what it may chase
                Vector3 at = post.HasValue ? post.Value : c.transform.position;
                Vector3 d = o.transform.position - at;
                d.y = 0f;
                float dist = d.magnitude - (post.HasValue ? Tie.Length : 0f);
                if (!post.HasValue && !fromSpot) dist = Mathf.Min(dist, Flat(o.transform.position, anchor));   // near whoever it keeps to
                if (dist <= range) { l.Threat = true; break; }
            }
            if (!l.Threat && fromSpot) l.From = c.transform.position;   // calm: the next fight starts from here
            if (l.Threat) Ready(ai, c);
            if (l.Threat && !was) Plugin.Debug("[defend] " + c.name + " sees a threat: it fights");
            else if (!l.Threat && was) Plugin.Debug("[defend] " + c.name + ": no enemy within reach any more");
            return l.Threat;
        }
    }
}
