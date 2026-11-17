using System.Collections.Generic;
using UnityEngine;

namespace Beastkeeper
{
    // A tamed drake fighting on its own (nobody on its back) uses the game's attack, which is the hatchling's ice
    // spit for every drake. At the moment that spit would leave, the drake's own element goes instead, at the
    // creature it is fighting: the same shots and damage as when ridden (stars count), fire and poison as streams,
    // frost as the beam on its foe and stone as the spike under its foe.
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
            if (foe == null || foe.IsDead()) return false;
            // the new frost and stone attacks, as when ridden (user, 2026-11-16)
            if (UsesBeam(el))
            {
                DefendBeam b = c.GetComponent<DefendBeam>();
                if (b == null) b = c.gameObject.AddComponent<DefendBeam>();
                b.Begin(foe);
                Plugin.Debug("[drake] " + c.name + " (not ridden) beams " + foe.name);
                return true;
            }
            if (UsesSpike(el))
            {
                EruptAt(c, foe);
                Plugin.Debug("[drake] " + c.name + " (not ridden) raises a spike under " + foe.name);
                return true;
            }
            if (s == null) return false;
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

        // a stone drake on its own: the spike right under its foe, half charged
        private const float DefendSpikeCharge = 0.5f;

        private static void EruptAt(Character c, Character foe)
        {
            Vector3 aim = _aimAt;
            float mult = _attackMult, charge = _chargeUsed;
            try
            {
                _aimAt = foe.transform.position;
                _chargeUsed = DefendSpikeCharge;
                _attackMult = 1f + DefendSpikeCharge * (Plugin.DrakeEarthChargeMax.Value - 1f);
                Erupt(c);
            }
            finally
            {
                _aimAt = aim; _attackMult = mult; _chargeUsed = charge;
            }
        }

        // a frost drake on its own: the beam for BeamSeconds, following its foe (a little behind it, so a quick foe
        // can step out of it); everybody sees it through the same messages as a rider's beam. No blizzard, as before:
        // that stays the rider's.
        private class DefendBeam : MonoBehaviour
        {
            private Character _c, _foe, _struck;
            private BeamView _view;
            private Vector3 _end;
            private float _until, _nextTick, _nextSend, _onFoe;

            internal void Begin(Character foe)
            {
                _c = GetComponent<Character>();
                _foe = foe;
                _until = Time.time + BeamSeconds;
                _nextTick = Time.time;
                _nextSend = 0f;
                _onFoe = 0f;
                _struck = null;
                _end = foe.GetCenterPoint();
                if (_view == null) _view = NewView("defend");
                Open(_view, _c, BeamSeconds);
                Send(_c, 0, Vector3.zero, BeamSeconds);
                enabled = true;
            }

            private void Stop()
            {
                if (_c != null) Send(_c, 2, Vector3.zero, 0f);
                if (_view != null) Close(_view);
                enabled = false;
            }

            private void LateUpdate()
            {
                if (_c == null || _c.IsDead() || Mount.RiddenCharacter() == _c || Time.time >= _until) { Stop(); return; }
                if (_foe != null && !_foe.IsDead())
                    _end = Vector3.Lerp(_end, _foe.GetCenterPoint(), 1f - Mathf.Exp(-8f * Time.deltaTime));
                Vector3 mouth, dir;
                MouthOf(_c, _end, out mouth, out dir);
                Character struck;
                Vector3 end = BeamHit(_c, mouth, dir, out struck);
                _view.End = end; _view.HasEnd = true;
                Draw(_view, mouth, end, dir);
                if (Time.time >= _nextSend) { _nextSend = Time.time + 1f / BeamSendHz; Send(_c, 1, end, _until - Time.time); }
                Character foe = struck != null && struck != _c && !struck.IsDead() && !struck.IsPlayer() && !IsAlly(struck) && BaseAI.IsEnemy(_c, struck) ? struck : null;
                if (foe != _struck) { _struck = foe; _onFoe = 0f; }
                if (foe != null) _onFoe += Time.deltaTime;
                if (Time.time < _nextTick) return;
                _nextTick = Time.time + BeamTick;
                if (foe != null) BeamDamage(_c, foe, end, dir, _onFoe);
            }

            private void OnDestroy()
            {
                if (_view != null && _view.Root != null) Destroy(_view.Root);
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
