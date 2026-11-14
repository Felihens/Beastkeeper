using System.Collections.Generic;
using UnityEngine;

namespace Beastkeeper
{
    // A tamed drake fighting on its own (nobody on its back) uses the game's attack, which is the hatchling's ice
    // spit for every drake. At the moment that spit would leave, the drake's own element goes instead, at the
    // creature it is fighting: the same shots and damage as when ridden (stars count), fire and poison as streams.
    //
    // Also here: each drake keeps its own wait between attacks, so getting off one and onto another doesn't carry
    // the first one's wait (or an attack cut short) over to the second.
    internal static partial class Drake
    {
        private static readonly System.Reflection.FieldInfo AiTarget = HarmonyLib.AccessTools.Field(typeof(MonsterAI), "m_targetCreature");

        private static Character TargetOf(Character c)
        {
            MonsterAI ai = c.GetComponent<MonsterAI>();
            return ai != null && AiTarget != null ? AiTarget.GetValue(ai) as Character : null;
        }

        // true: our shot went instead of the game's spit
        private static bool Defend(Character c, Attack a)
        {
            int el = ElementOf(c);
            Shot s = ShotOf(el);
            Character foe = TargetOf(c);
            if (s == null || foe == null || foe.IsDead()) return false;
            FireAt(c, s, foe, true);
            if (IsStream(el))
            {
                DefendStream st = c.GetComponent<DefendStream>();
                if (st == null) st = c.gameObject.AddComponent<DefendStream>();
                st.Foe = foe;
                st.S = s;
                st.Every = BurstEvery(el);
                st.Left = Mathf.RoundToInt(BurstSeconds(el) / st.Every);
                st.Next = Time.time + st.Every;
            }
            Plugin.Debug("[drake] " + c.name + " (not ridden) attacks " + foe.name + " with " + ElementName(el));
            return true;
        }

        // one shot of a drake on its own, at its foe; what belongs to the rider's attack is set aside meanwhile
        private static void FireAt(Character c, Shot s, Character foe, bool first)
        {
            Vector3 aim = _aimAt;
            Character aimFoe = _aimFoe;
            float mult = _attackMult, charge = _chargeUsed;
            int el = ElementOf(c);
            try
            {
                _aimAt = foe.GetCenterPoint();
                _aimFoe = el == Lightning ? foe : null;
                _attackMult = el == Frost && Plugin.DrakeFrostShards.Value > 0 ? 1.2f : 1f;
                _chargeUsed = 0f;
                Spawn(c, null, s, first);
            }
            finally
            {
                _aimAt = aim; _aimFoe = aimFoe; _attackMult = mult; _chargeUsed = charge;
            }
        }

        // the rest of a fire or poison stream, for a drake on its own
        private class DefendStream : MonoBehaviour
        {
            internal Character Foe;
            internal Shot S;
            internal int Left;
            internal float Every, Next;

            private void Update()
            {
                if (Left <= 0 || Time.time < Next) return;
                Character c = GetComponent<Character>();
                if (c == null || c.IsDead() || Foe == null || Foe.IsDead() || Mount.RiddenCharacter() == c) { Left = 0; return; }
                Next = Time.time + Every;
                Left--;
                FireAt(c, S, Foe, false);
            }
        }

        // ---- each drake its own wait
        private static readonly Dictionary<int, float> _waits = new Dictionary<int, float>();
        private static Character _timerOf;

        private static void SwitchDrake(Character now)
        {
            float cd = Mathf.Max(0.2f, Plugin.DrakeAttackCooldown.Value);
            if (_timerOf != null)
            {
                float next = _nextShot;
                // an attack cut short (you got off mid-attack): its wait starts now
                if (_pending != null || _burstLeft > 0 || next > Time.time + cd + 5f) next = Time.time + cd;
                _waits[_timerOf.GetInstanceID()] = next;
                if (_waits.Count > 200) _waits.Clear();
            }
            Slow(null, false);
            Hurry(null, false);
            _pending = null;
            _burstLeft = 0;
            _queuedC = null;
            _saved = 0f;
            float n;
            _nextShot = now != null && _waits.TryGetValue(now.GetInstanceID(), out n) ? n : 0f;
            _timerOf = now;
            _cdEnd = -1f;
            _wasWaiting = false;
            _readyAt = -10f;
        }
    }
}
