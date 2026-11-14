using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Beastkeeper
{
    // Fighting from a drake, like a bow: hold the right mouse button to aim, the left one shoots where you aim.
    // Each element shoots what a creature of the game already throws (agreed with the user, 2026-11-11):
    // frost = the drake's own ice spit, earth = the troll's rock, fire = the fenring cultist's flames,
    // poison = the greydwarf shaman's poison, lightning = Eikthyr's lightning (with its own strike effect).
    //
    // The drake plays its own attack animation (it spits, or whatever it does), and the shot leaves at the
    // moment that animation says so (the same event the game uses), not at once. Fire and poison are streams:
    // the animation is slowed down and the shot keeps coming for a while. After it ends the drake waits
    // AttackCooldown seconds before it can attack again.
    // Every element hits as hard (AttackDamage, in its own kind of damage) and flies faster than the original
    // (AttackSpeed), so it reaches farther.
    [HarmonyPatch]
    internal static partial class Drake
    {
        private class Shot
        {
            internal ItemDrop.ItemData Item;
            internal Attack Attack;
            internal GameObject Prefab;   // the projectile, or what the attack spawns where it lands
            internal bool AtTarget;       // spawned at the target, not shot from the head
            internal string From;
        }

        private static readonly Dictionary<int, Shot> Shots = new Dictionary<int, Shot>();
        private static GameObject _strike;
        private static bool _strikeLooked;
        private static float _nextShot;
        internal static bool Aiming;
        private static Vector3 _aimAt;            // where the camera pointed when you clicked: the attack goes there
        private const float IdleShare = 0.25f;     // a tamed drake gives its idle roar a quarter as often

        // tamed drakes roar far less often (the game plays an idle sound every few seconds, half of the time)
        [HarmonyPatch(typeof(BaseAI), "DoIdleSound"), HarmonyPrefix]
        private static bool QuietRoar(BaseAI __instance)
        {
            Character c = Fast.AIChar(__instance);
            if (c == null || !c.IsTamed() || !Is(c)) return true;
            return Random.value < IdleShare;
        }

        // the attack in progress
        private static Character _pending;       // waiting for the animation's moment to shoot
        private static Player _pendingRider;
        private static float _startedAt;
        private static bool _launched;
        private static int _burstLeft;
        private static float _burstNext, _burstEnd;
        private static Animator _slowed;
        private static float _slowedSpeed;
        private const float BurstSlow = 0.5f;
        // fire: 1 + 6 fireballs over 1.5 s (one every 0.25 s); poison: 1 + 10 over 1.6 s
        private static float BurstSeconds(int el) { return el == Fire ? 1.5f : 1.6f; }
        private static float BurstEvery(int el) { return el == Fire ? 0.25f : 0.16f; }

        private static bool IsStream(int el) { return el == Fire || el == Poison; }

        // where each element looks for its attack: creatures first, then items (by name), in order
        private static string[] SourcesOf(int el)
        {
            switch (el)
            {
                case Earth: return new[] { "creature:Troll" };
                // projectiles the game really shows: a fireball, and the poison ball of the blobs
                case Fire: return new[] { "item:StaffFireball", "projectile:GoblinShaman_projectile_fireball", "projectile:Imp_fireball_projectile", "creature:Fenring_Cultist" };
                // a spit or ball of poison, never a thrown bomb or flask
                case Poison: return new[] { "creature:Greydwarf_Shaman", "projectile:*poison*", "projectile:*acid*", "projectile:*spit*" };
                case Lightning: return new[] { "creature:Eikthyr", "item:StaffLightning", "item:*lightning*" };
                default: return new[] { "creature:" + BabyPrefab };
            }
        }

        private static Shot ShotOf(int el)
        {
            Shot s;
            if (Shots.TryGetValue(el, out s)) return s;
            s = null;
            foreach (string src in SourcesOf(el)) { s = Find(src, el); if (s != null) break; }
            if (s == null && el != Frost) s = ShotOf(Frost);   // nothing found: the ice spit
            if (ZNetScene.instance != null && ObjectDB.instance != null) Shots[el] = s;   // only remember once the game is loaded
            Plugin.Debug("[drake] " + ElementName(el) + " attack: " + (s != null ? s.From + " (" + (s.AtTarget ? "at target " : "") + s.Prefab.name + ")" : "none found"));
            return s;
        }

        private static List<GameObject> ItemsOf(string src)
        {
            List<GameObject> items = new List<GameObject>();
            if (src.StartsWith("creature:"))
            {
                GameObject p = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(src.Substring(9)) : null;
                Humanoid h = p != null ? p.GetComponent<Humanoid>() : null;
                if (h == null) return items;
                if (h.m_defaultItems != null) items.AddRange(h.m_defaultItems);
                if (h.m_randomWeapon != null) items.AddRange(h.m_randomWeapon);
                if (h.m_randomSets != null) foreach (Humanoid.ItemSet set in h.m_randomSets) if (set != null && set.m_items != null) items.AddRange(set.m_items);
            }
            else if (src.StartsWith("item:") && ObjectDB.instance != null)
            {
                string want = src.Substring(5).ToLowerInvariant();
                bool wild = want.StartsWith("*") && want.EndsWith("*");
                string part = want.Trim('*');
                foreach (GameObject g in ObjectDB.instance.m_items)
                    if (g != null && (wild ? g.name.ToLowerInvariant().Contains(part) : g.name.ToLowerInvariant() == want)) items.Add(g);
            }
            return items;
        }

        private static Shot Find(string src, int el)
        {
            if (src.StartsWith("projectile:"))
            {
                // a projectile prefab by name, with the drake's own spit supplying the rest (damage numbers, force)
                string pname = src.Substring(11);
                GameObject pf = pname.Contains("*") ? Wild(pname) : Prefab(pname);
                Shot own = el != Frost ? ShotOf(Frost) : null;
                if (pf == null || pf.GetComponent<IProjectile>() == null || own == null) return null;
                return new Shot { Item = own.Item, Attack = own.Attack, Prefab = pf, AtTarget = false, From = pf.name };
            }
            Shot best = null;
            float bestScore = -1f;
            foreach (GameObject g in ItemsOf(src))
            {
                ItemDrop d = g != null ? g.GetComponent<ItemDrop>() : null;
                if (d == null) continue;
                ItemDrop.ItemData.SharedData sh = d.m_itemData.m_shared;
                foreach (Attack a in new[] { sh.m_attack, sh.m_secondaryAttack })
                {
                    if (a == null) continue;
                    GameObject prefab = a.m_attackProjectile;
                    bool atTarget = false;
                    if (prefab == null || prefab.GetComponent<IProjectile>() == null)
                    {
                        prefab = a.m_spawnOnTrigger;   // attacks that spawn something where they land (a lightning strike)
                        atTarget = true;
                        if (prefab == null || prefab.GetComponent<IProjectile>() == null) continue;
                    }
                    float score = sh.m_damages.GetTotalDamage();
                    string n = (g.name + " " + prefab.name).ToLowerInvariant();
                    if (el == Lightning && (n.Contains("lightning") || n.Contains("antler") || n.Contains("thunder"))) score += 10000f;
                    if (el == Fire && n.Contains("fire")) score += 10000f;
                    if (el == Poison && n.Contains("poison")) score += 10000f;
                    if (score > bestScore) { bestScore = score; best = new Shot { Item = d.m_itemData, Attack = a, Prefab = prefab, AtTarget = atTarget, From = g.name }; }
                }
            }
            return best;
        }

        // a projectile whose name has this part (*part*), skipping thrown bombs and flasks; all candidates go to the log
        private static GameObject Wild(string pattern)
        {
            if (ZNetScene.instance == null) return null;
            string part = pattern.Trim('*').ToLowerInvariant();
            List<string> seen = new List<string>();
            GameObject best = null;
            foreach (GameObject g in ZNetScene.instance.m_prefabs)
            {
                if (g == null || g.GetComponent<IProjectile>() == null || g.GetComponent<ItemDrop>() != null) continue;
                string n = g.name.ToLowerInvariant();
                if (!n.Contains(part)) continue;
                seen.Add(g.name);
                if (n.Contains("bomb") || n.Contains("bile") || n.Contains("bottle") || n.Contains("flask") || n.Contains("potion") || n.Contains("mead")) continue;
                if (best == null || (n.Contains("projectile") && !best.name.ToLowerInvariant().Contains("projectile"))) best = g;
            }
            Plugin.Debug("[drake] projectiles with '" + part + "': " + string.Join(", ", seen.ToArray()) + " -> " + (best != null ? best.name : "none"));
            return best;
        }

        // the lightning strike of the game's own bosses and staff: a prefab with "lightning" in its name that
        // hits where it lands (not an item). Written to the log once, so the choice can be checked.
        private static GameObject Strike()
        {
            if (_strikeLooked) return _strike;
            if (ZNetScene.instance == null) return null;
            _strikeLooked = true;
            List<string> seen = new List<string>();
            GameObject best = null;
            int bestScore = -1;
            foreach (GameObject g in ZNetScene.instance.m_prefabs)
            {
                if (g == null) continue;
                string n = g.name.ToLowerInvariant();
                if (!n.Contains("lightning") && !n.Contains("thunder")) continue;
                if (g.GetComponent<ItemDrop>() != null || g.GetComponent<Character>() != null) continue;
                seen.Add(g.name);
                if (g.GetComponent<IProjectile>() == null) continue;
                int score = (n.Contains("aoe") ? 4 : 0) + (n.Contains("strike") ? 2 : 0) + (n.StartsWith("fx_") || n.StartsWith("vfx_") ? -3 : 0);
                if (score > bestScore) { bestScore = score; best = g; }
            }
            _strike = best;
            Plugin.Debug("[drake] lightning prefabs: " + string.Join(", ", seen.ToArray()) + " -> strike effect " + (best != null ? best.name : "none"));
            return _strike;
        }

        // ------------------------------------------------------------ the left button while riding a drake

        internal static void Shoot(Character c, Player rider)
        {
            if (Time.time < _nextShot || _pending != null || _burstLeft > 0) return;
            if (!Aiming) { Say("Hold the right mouse button to aim, then shoot with the left."); return; }
            if (!MayAttack(c)) { Say("Too high to attack: fly lower."); return; }
            int el = ElementOf(c);
            if (ShotOf(el) == null) { Say("Your drake has no attack."); return; }
            if (!CanSpend(AttackCost())) { Say("Your drake is too tired to attack: let it rest near the ground."); return; }
            // the target is fixed at the click (not when the animation lets go of the shot a moment later)
            if (_queuedC == null) { _aimAt = AimPoint(c, rider); _aimFoe = el == Lightning ? AimFoe(c, _aimAt) : null; }
            if (_queuedC == null && !c.IsFlying())
            {
                if (!TakeOffToAttack(c)) { Say("Your drake is too tired to fly up and attack."); return; }
                _queuedC = c; _queuedRider = rider; _queuedAt = Time.time + 1.0f;   // shoot once it's up
                return;
            }

            Spend(AttackCost());
            _chargeUsed = _shotCharge;
            _shotCharge = 0f;
            Humanoid h = c as Humanoid;
            ItemDrop.ItemData move = h != null ? OwnMove(h) : null;
            _launched = false;
            _burstLeft = 0;
            _startedAt = Time.time;
            _nextShot = Time.time + 30f;   // set for real when the attack ends
            if (move != null)
            {
                ZNetView nv = Leash.View(c);
                if (nv != null && nv.IsValid() && !nv.IsOwner()) nv.ClaimOwnership();
                if (h.GetCurrentWeapon() != move) h.EquipItem(move, false);
                _pending = c;
                _pendingRider = rider;
                // it keeps moving while it attacks, at half its speed (the game would stop it)
                move.m_shared.m_attack.m_speedFactor = MoveShare;
                move.m_shared.m_attack.m_speedFactorRotation = 1f;
                if (h.StartAttack(null, false))
                {
                    Hurry(c, true);
                    Plugin.Debug("[drake] attack animation " + move.m_shared.m_name + " (" + move.m_shared.m_attack.m_attackAnimation + ")");
                    return;
                }
                _pending = null;
            }
            // no animation to wait for: shoot now
            Launch(c, rider);
            _nextShot = Time.time + Mathf.Max(0.2f, Plugin.DrakeAttackCooldown.Value);
        }

        // the drake's own attack with a projectile (its spit), else any attack with an animation
        private static ItemDrop.ItemData OwnMove(Humanoid h)
        {
            ItemDrop.ItemData any = null;
            foreach (ItemDrop.ItemData it in h.GetInventory().GetAllItems())
            {
                if (it == null || !it.IsWeapon() || it.m_shared.m_attack == null || string.IsNullOrEmpty(it.m_shared.m_attack.m_attackAnimation)) continue;
                if (it.m_shared.m_attack.m_attackProjectile != null) return it;
                if (any == null) any = it;
            }
            return any;
        }

        // the game's own moment for the shot (the attack's animation event): shoot ours instead
        private static readonly System.Reflection.FieldInfo AttackOwner = AccessTools.Field(typeof(Attack), "m_character");

        [HarmonyPatch(typeof(Attack), "ProjectileAttackTriggered"), HarmonyPrefix]
        private static bool AtTheMoment(Attack __instance)
        {
            Humanoid ch = AttackOwner != null ? AttackOwner.GetValue(__instance) as Humanoid : null;
            // a tamed drake fighting on its own: its own element instead of the ice spit (DrakeDefend.cs)
            if (ch != null && ch != _pending && ch.IsTamed() && ch != Mount.RiddenCharacter() && Is(ch)) return !Defend(ch, __instance);
            if (_pending == null || _launched) return true;
            if (ch != _pending) return true;
            Launch(ch, _pendingRider);
            return false;
        }

        // runs every frame: waits for the attack to end, keeps the streams coming, then starts the cooldown
        private static Character _queuedC;
        private static Player _queuedRider;
        private static float _queuedAt;

        internal static void Tick()
        {
            Character ridden = Mount.RiddenCharacter();
            if (ridden != null && !Is(ridden)) ridden = null;
            if (ridden != _timerOf) SwitchDrake(ridden);
            if (_queuedC != null && Time.time >= _queuedAt)
            {
                Character qc = _queuedC;
                _queuedC = null;
                if (qc != null && !qc.IsDead() && Mount.RiddenCharacter() == qc && qc.IsFlying()) { _queuedC = qc; Shoot(qc, _queuedRider); _queuedC = null; }
            }
            if (_pending == null && _burstLeft <= 0 && _slowed == null) return;
            Character c = _pending;
            Player rider = _pendingRider;
            if (c == null || c.IsDead() || Mount.RiddenCharacter() != c)
            {
                Slow(null, false);
                Hurry(null, false);
                _pending = null; _burstLeft = 0;
                _nextShot = Time.time + Mathf.Max(0.2f, Plugin.DrakeAttackCooldown.Value);   // cut short: the wait starts now
                return;
            }
            int el = ElementOf(c);
            // the animation never reached its moment (a bite, not a spit): shoot after a short wait
            if (!_launched && Time.time - _startedAt > 1.4f) Launch(c, rider);
            if (_burstLeft > 0 && Time.time >= _burstNext)
            {
                _burstNext = Time.time + BurstEvery(el);
                _burstLeft--;
                Spawn(c, rider, ShotOf(el), false);
            }
            Humanoid h = c as Humanoid;
            bool animating = h != null && h.InAttack();
            if (_launched && _burstLeft <= 0 && (!animating || Time.time - _startedAt > 8f))
            {
                Slow(null, false);
                _pending = null;
                // a stream's wait starts with the stream, so fire and poison attack as often as the single shots
                float from = IsStream(el) ? _burstEnd - BurstSeconds(el) : Time.time;
                _nextShot = Mathf.Max(Time.time, from + Mathf.Max(0.2f, Plugin.DrakeAttackCooldown.Value)) + _saved;
                _saved = 0f;
            }
        }

        // slow (or restore) the drake's animation while it breathes a stream
        private static void Slow(Character c, bool on)
        {
            if (!on)
            {
                if (_slowed != null) _slowed.speed = _slowedSpeed;
                _slowed = null;
                return;
            }
            Animator an = c != null ? c.GetComponentInChildren<Animator>() : null;
            if (an == null) return;
            _slowed = an;
            _slowedSpeed = an.speed;
            an.speed = _slowedSpeed * BurstSlow;
        }

        // the shot itself, at the animation's moment
        // ------------------------------------------------------------ one attack, one amount of damage
        // Whatever the borrowed projectiles do (a stream of fireballs, a boss lightning that hits again and again),
        // one attack hurts each target by at most the same amount, before the target's weakness or resistance:
        // twice AttackDamage. So every element is as strong, and only the kind of damage makes a difference.
        private static readonly Dictionary<Character, float> _dealt = new Dictionary<Character, float>();
        private static float _attackUntil, _starMult = 1f;

        private static void NewAttack(int el)
        {
            _dealt.Clear();
            _starMult = DamageOf(Mount.RiddenCharacter());
            _attackMult = el == Earth ? 1f + _chargeUsed * (Plugin.DrakeEarthChargeMax.Value - 1f) : el == Frost && Plugin.DrakeFrostShards.Value > 0 ? 1.2f : 1f;   // frost: ball + shards go past the usual limit
            _attackUntil = Time.time + (IsStream(el) ? BurstSeconds(el) : 0f) + 5f;
        }

        // ---- party members are spared (Shieldmates / Warband, optional: found by name, so nothing breaks without it)
        private static System.Reflection.MethodInfo _isAlly;
        private static bool _allyLooked;

        private static bool IsAlly(Character c)
        {
            Player p = c as Player;
            if (p == null || p == Player.m_localPlayer) return false;
            if (!_allyLooked)
            {
                _allyLooked = true;
                try
                {
                    foreach (System.Reflection.Assembly a in System.AppDomain.CurrentDomain.GetAssemblies())
                    {
                        System.Type t = a.GetType("Warband.Party", false);
                        if (t != null) { _isAlly = t.GetMethod("IsAlly", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public); break; }
                    }
                }
                catch (System.Exception) { _isAlly = null; }
            }
            if (_isAlly == null) return false;
            try { return (bool)_isAlly.Invoke(null, new object[] { p.GetZDOID().UserID }); }
            catch (System.Exception) { return false; }
        }

        [HarmonyPatch(typeof(Character), "Damage"), HarmonyPrefix, HarmonyPriority(Priority.Low)]
        private static bool Budget(Character __instance, HitData hit)
        {
            if (hit == null || __instance == null) return true;
            if (Plugin.DrakeSpareParty.Value && __instance is Player)
            {
                Character att = hit.GetAttacker();
                if (att != null && att == Mount.RiddenCharacter() && Is(att) && IsAlly(__instance)) return false;
            }
            // a drake fighting on its own: only its burning is held (BurnCap), at its own stars
            Character own = hit.GetAttacker();
            if (own != null && own != Mount.RiddenCharacter() && own.IsTamed() && Is(own)) { _drakeHitFrame = Time.frameCount; _burnMult = DamageOf(own); return true; }
            if (Time.time > _attackUntil) return true;
            Character drake = Mount.RiddenCharacter();
            if (drake == null || hit.GetAttacker() != drake || !Is(drake)) return true;
            _drakeHitFrame = Time.frameCount;   // the burning this hit starts is capped just below
            _burnMult = _starMult;
            float total = hit.GetTotalDamage();
            if (total <= 0f) return true;
            float done;
            _dealt.TryGetValue(__instance, out done);
            float cap = Plugin.DrakeAttackDamage.Value * 2f * _attackMult * _starMult;
            if (ElementOf(drake) == Fire) cap = Mathf.Min(cap, Plugin.DrakeFireMax.Value * _starMult);   // its burning keeps hurting after: less up front
            float left = cap - done;
            if (left <= 0.01f) return false;   // this attack has done its part on this target
            if (total > left) hit.m_damage.Modify(left / total);
            _dealt[__instance] = done + Mathf.Min(total, left);
            return true;
        }

        // ------------------------------------------------------------ burning doesn't pile up
        // The game adds every new fire hit to what is still burning on the target (and spreads it over the same time),
        // so a few attacks in a row would burn for ever more. Poison doesn't stack (a bigger hit only replaces it).
        // Here what a drake has set on fire never holds more than FireMaxDamage, whatever the number of attacks.
        private static int _drakeHitFrame = -1;
        private static float _burnMult = 1f;
        private static readonly System.Reflection.FieldInfo BurnLeft = AccessTools.Field(typeof(SE_Burning), "m_fireDamageLeft");
        private static readonly System.Reflection.FieldInfo BurnPerHit = AccessTools.Field(typeof(SE_Burning), "m_fireDamagePerHit");

        [HarmonyPatch(typeof(Character), "AddFireDamage"), HarmonyPostfix]
        private static void BurnCap(Character __instance)
        {
            if (Time.frameCount != _drakeHitFrame || BurnLeft == null || BurnPerHit == null) return;
            SEMan se = __instance.GetSEMan();
            SE_Burning b = se != null ? se.GetStatusEffect(SEMan.s_statusEffectBurning) as SE_Burning : null;
            if (b == null) return;
            float cap = Plugin.DrakeFireMax.Value * _burnMult;
            float left = (float)BurnLeft.GetValue(b);
            if (left <= cap) return;
            int ticks = Mathf.Max(1, (int)(b.m_ttl / b.m_damageInterval));
            BurnLeft.SetValue(b, cap);
            BurnPerHit.SetValue(b, cap / ticks);
            Plugin.Debug("[drake] burning on " + __instance.name + " held at " + cap.ToString("0") + " (was " + left.ToString("0") + ")");
        }

        // The wind-up of the attack plays faster (the shot leaves sooner after the click), and the time saved is added
        // to the wait after it: the drake attacks exactly as often as before.
        private const float HurryBy = 1.7f, MoveShare = 0.5f;
        private static Animator _hurried;
        private static float _hurriedSpeed, _saved;

        private static void Hurry(Character c, bool on)
        {
            if (!on)
            {
                if (_hurried != null) _hurried.speed = _hurriedSpeed;
                _hurried = null;
                return;
            }
            _saved = 0f;
            Animator an = c != null ? c.GetComponentInChildren<Animator>() : null;
            if (an == null) return;
            _hurried = an;
            _hurriedSpeed = an.speed;
            an.speed = _hurriedSpeed * HurryBy;
        }

        [HarmonyPatch(typeof(Character), "CanMove"), HarmonyPostfix]
        private static void MovesWhileAttacking(Character __instance, ref bool __result)
        {
            if (!__result && _pending != null && __instance == _pending && !__instance.IsStaggering()) __result = true;
        }

        private static void Launch(Character c, Player rider)
        {
            if (_hurried != null) { _saved = (Time.time - _startedAt) * (HurryBy - 1f); Hurry(null, false); }
            _launched = true;
            NewAttack(ElementOf(c));
            int el = ElementOf(c);
            Shot s = ShotOf(el);
            if (s == null) return;
            Spawn(c, rider, s, true);
            if (IsStream(el))
            {
                _burstLeft = Mathf.RoundToInt(BurstSeconds(el) / BurstEvery(el));
                _burstNext = Time.time + BurstEvery(el);
                _burstEnd = Time.time + BurstSeconds(el);
            }
        }

        private static void Spawn(Character c, Player rider, Shot s, bool first)
        {
            if (s == null || c == null) return;
            int el = ElementOf(c);
            Vector3 from = c.GetHeadPoint();
            Vector3 target = _aimAt;
            if (target == Vector3.zero) target = AimPoint(c, rider);
            // lightning only reaches so far: past that it falls on the ground at the edge of its reach
            float reach = Plugin.DrakeLightningRange.Value;
            // lightning finds the enemy you had in your sights when you clicked, wherever it has moved to since
            Character foe = el == Lightning && _aimFoe != null && !_aimFoe.IsDead() ? _aimFoe : null;
            if (foe != null) target = foe.GetCenterPoint();
            if (el == Lightning && Vector3.Distance(from, target) > reach)
            {
                target = from + (target - from).normalized * reach;
                if (ZoneSystem.instance != null) target.y = Mathf.Max(ZoneSystem.instance.GetGroundHeight(target), ZoneSystem.instance.m_waterLevel);
            }
            Vector3 dir = (target - from).normalized;
            Attack a = s.Attack;
            // the rock is heavy: it falls as it flies, so it leaves a bit higher to still land where you aimed
            if (el == Earth && !s.AtTarget) dir = Lob(from, target, ShotSpeed(a, el), EarthGravity(s));
            Quaternion look = Quaternion.LookRotation(dir);
            float scale = Mathf.Max(1f, c.transform.localScale.x);
            Vector3 mouth = from + dir * 1.2f * scale;

            HitData.DamageTypes dmg = new HitData.DamageTypes();
            float amount = Plugin.DrakeAttackDamage.Value * DamageOf(c);
            if (IsStream(el)) amount /= Mathf.Max(1f, BurstSeconds(el) / BurstEvery(el)) / 2f;   // many small hits, about twice one shot in all
            else if (el == Earth) amount *= 2f * _attackMult;   // one big hit worth the same: the stream's total (a charged rock hits harder)
            else if (el == Frost) amount *= (Shards(s) ? 1.6f : 2f);   // with shards: the ball hits for 80% of an attack, the shards add 40% more
            switch (el)
            {
                case Fire: dmg.m_fire = amount; break;
                // half hurts at once (the acid burns on contact), half is the poison that keeps hurting after
                case Poison: dmg.m_damage = amount * 0.5f; dmg.m_poison = amount * 0.5f; break;
                case Earth: dmg.m_blunt = amount; break;
                case Lightning: dmg.m_lightning = amount; break;
                default: dmg.m_frost = amount; break;
            }
            HitData hd = new HitData();
            hd.m_damage = dmg;
            hd.m_pushForce = s.Item.m_shared.m_attackForce;
            hd.m_toolTier = (short)s.Item.m_shared.m_toolTier;
            hd.m_dodgeable = true;
            hd.m_ranged = true;   // its own damage setting, not the tamed-animal multiplier
            hd.m_blockable = true;
            // no borrowed status effect: the kind of damage brings its own (burning, poison, frost slow), so every
            // element hits for the same and only the target's weakness or resistance makes a difference

            if (first)
            {
                if (a.m_startEffect != null) a.m_startEffect.Create(mouth, look);
                if (a.m_triggerEffect != null) a.m_triggerEffect.Create(mouth, look);
            }

            int count = s.AtTarget ? 1 : Mathf.Clamp(a.m_projectiles, 1, 12);
            if (IsStream(el) || el == Frost || el == Earth) count = 1;   // one ball with all the damage, not a spray that mostly misses
            float speed = ShotSpeed(a, el);
            float spread = IsStream(el) ? 4f : count > 1 ? Mathf.Min(6f, a.m_projectileAccuracy) : 0f;
            if (count > 1) { DamageTypes_Scale(ref hd, 1f / count); }
            for (int i = 0; i < count; i++)
            {
                Vector3 d = look * (Quaternion.Euler(Random.Range(-spread, spread), Random.Range(-spread, spread), 0f) * Vector3.forward);
                Vector3 at = s.AtTarget ? target + Vector3.up * 0.1f : mouth;
                GameObject go = Object.Instantiate(s.Prefab, at, s.AtTarget ? Quaternion.identity : Quaternion.LookRotation(d));
                IProjectile pr = go.GetComponent<IProjectile>();
                if (pr == null) { Object.Destroy(go); continue; }
                NoSpawns(go);
                pr.Setup(c, s.AtTarget ? Vector3.zero : d * speed, a.m_attackHitNoise, hd, s.Item, null);
                Projectile fly = go.GetComponent<Projectile>();
                if (el == Earth && fly != null) Rock(fly, go);
                if (el == Frost && fly != null && Shards(s)) _ice[fly] = new Ice { Owner = c, Shot = s, Hit = hd, Speed = speed };
                if (el == Lightning && fly != null && !s.AtTarget) fly.m_ttl = Mathf.Min(fly.m_ttl, reach / Mathf.Max(1f, speed));
                if (el == Frost && !s.AtTarget) go.transform.localScale *= Mathf.Max(0.5f, Plugin.DrakeFrostSize.Value);   // a bigger ice ball
            }

            // the lightning also strikes where it lands, with the same effect as the game's own lightning (Eikthyr's):
            // the lightning area (hits everything there) and, if that has no flash of its own, the flash
            if (el == Lightning && first) Strike(c, foe != null && Vector3.Distance(from, foe.transform.position) <= reach ? foe.transform.position : target, a, s);
            if (first) Plugin.Debug("[drake] " + ElementName(el) + " shot from " + s.From + ": " + s.Prefab.name + (s.AtTarget ? " at the target" : " at " + speed.ToString("0") + " m/s"));
        }

        // debug: every hit of the ridden drake, after the target's resistances
        [HarmonyPatch(typeof(Character), "ApplyDamage"), HarmonyPrefix]
        private static void Landed(Character __instance, HitData hit, HitData.DamageModifier mod)
        {
            if (hit == null || Mount.RiddenCharacter() == null || hit.GetAttacker() != Mount.RiddenCharacter()) return;
            HitData.DamageTypes d = hit.m_damage;
            Plugin.Debug("[drake] hit " + __instance.name + " for " + hit.GetTotalDamage().ToString("0.0") + " (" + mod + "; frost " + d.m_frost.ToString("0") + " fire " + d.m_fire.ToString("0")
                + " poison " + d.m_poison.ToString("0") + " true " + d.m_damage.ToString("0") + " blunt " + d.m_blunt.ToString("0") + " lightning " + d.m_lightning.ToString("0") + "), health " + __instance.GetHealth().ToString("0") + "/" + __instance.GetMaxHealth().ToString("0"));
        }

        // the stone drake's rocks: the camera shakes a little where one lands (more when it lands close)
        // (the rock's own landing, with its crater and bounce, and the ice ball's shards are in DrakeSplash.cs)

        // a borrowed projectile may make creatures or items where it lands (the blob bomb spawns blobs): never
        private static void NoSpawns(GameObject go)
        {
            Projectile p = go.GetComponent<Projectile>();
            if (p == null) return;
            if (p.m_spawnOnHit != null && (p.m_spawnOnHit.GetComponent<Character>() != null || p.m_spawnOnHit.GetComponent<ItemDrop>() != null)) p.m_spawnOnHit = null;
            if (p.m_randomSpawnOnHit != null) p.m_randomSpawnOnHit.RemoveAll(g => g == null || g.GetComponent<Character>() != null || g.GetComponent<ItemDrop>() != null);
            // nor an area of its own that hurts on top of the shot (the fireball's blast had its own damage)
            if (p.m_spawnOnHit != null && p.m_spawnOnHit.GetComponent<Aoe>() != null) p.m_spawnOnHit = null;
            if (p.m_randomSpawnOnHit != null) p.m_randomSpawnOnHit.RemoveAll(g => g.GetComponent<Aoe>() != null);
            p.m_onlySpawnedProjectilesDealDamage = false;
            p.m_respawnItemOnHit = false;
            p.m_spawnItem = null;
        }

        private static void DamageTypes_Scale(ref HitData hd, float k)
        {
            hd.m_damage.Modify(k);
        }

        // Aim help for the lightning: the enemy you were aiming at. The one closest to the line of sight wins, if it is
        // within AimHelpDegrees of it (or within AimHelpNear metres of the point you aimed at) and in reach.
        private static Character _aimFoe;
        private const float AimHelpDegrees = 7f, AimHelpNear = 4f;

        private static Character AimFoe(Character c, Vector3 aim)
        {
            Transform cam = GameCamera.instance != null ? GameCamera.instance.transform : null;
            if (cam == null) return null;
            float reach = Plugin.DrakeLightningRange.Value + 5f;
            Character best = null;
            float bestScore = float.MaxValue;
            foreach (Character ch in Character.GetAllCharacters())
            {
                if (ch == null || ch == c || ch.IsDead() || ch.IsPlayer() || ch.IsTamed()) continue;
                Vector3 p = ch.GetCenterPoint();
                if (Vector3.Distance(p, c.transform.position) > reach) continue;
                Vector3 to = p - cam.position;
                if (Vector3.Dot(to, cam.forward) <= 0f) continue;
                float ang = Vector3.Angle(cam.forward, to);
                float near = Vector3.Distance(p, aim);
                if (ang > AimHelpDegrees && near > AimHelpNear) continue;
                if (!BaseAI.IsEnemy(c, ch)) continue;
                float score = Mathf.Min(ang, near * 2f);
                if (score < bestScore) { bestScore = score; best = ch; }
            }
            if (best != null) Plugin.Debug("[drake] lightning aimed at " + best.name);
            return best;
        }

        // where the camera's centre points (past the drake and its rider)
        private static Vector3 AimPoint(Character c, Player rider)
        {
            Transform cam = GameCamera.instance != null ? GameCamera.instance.transform : null;
            if (cam == null) return c.GetHeadPoint() + c.transform.forward * 50f;
            RaycastHit[] hits = Physics.RaycastAll(cam.position, cam.forward, 250f,
                LayerMask.GetMask("Default", "static_solid", "terrain", "piece", "character", "character_net", "character_ghost", "Water"), QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            Vector3 at = cam.position + cam.forward * 250f;
            foreach (RaycastHit h in hits)
            {
                if (h.collider == null) continue;
                Character hc = h.collider.GetComponentInParent<Character>();
                if (hc == c || hc == rider) continue;
                if (h.distance < 3f) continue;
                if (h.distance < best) { best = h.distance; at = h.point; }
            }
            return at;
        }

        // ------------------------------------------------------------ the rider never dies of a fall from a drake

        private static float _safeUntil;

        private static GameObject Prefab(string name) { return ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(name) : null; }

        // does this prefab (or the effects its attack lists) have a light, so it lights up the surroundings?
        private static bool Lights(GameObject g)
        {
            if (g == null) return false;
            if (g.GetComponentInChildren<Light>(true) != null) return true;
            foreach (MonoBehaviour mb in g.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null) continue;
                foreach (System.Reflection.FieldInfo f in mb.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                {
                    EffectList el = f.GetValue(mb) as EffectList;
                    if (el == null || el.m_effectPrefabs == null) continue;
                    foreach (EffectList.EffectData e in el.m_effectPrefabs)
                        if (e != null && e.m_prefab != null && e.m_prefab.GetComponentInChildren<Light>(true) != null) return true;
                }
            }
            return false;
        }

        private static void Strike(Character c, Vector3 target, Attack a, Shot s)
        {
            Vector3 at = target + Vector3.up * 0.1f;
            GameObject area = Prefab("lightningAOE") ?? Strike();
            GameObject flash = Prefab("fx_Lightning");
            bool lit = false;
            if (area != null && area != s.Prefab)
            {
                GameObject go = Object.Instantiate(area, at, Quaternion.identity);
                IProjectile pr = go.GetComponent<IProjectile>();
                HitData sh = new HitData();
                sh.m_damage = new HitData.DamageTypes();
                sh.m_damage.m_lightning = Plugin.DrakeAttackDamage.Value * DamageOf(c);
                sh.m_ranged = true;   // not the tamed-animal multiplier
                // the boss's lightning hits again and again while it lasts, and chains to more strikes: here it hits once
                Aoe aoe = go.GetComponent<Aoe>();
                if (aoe != null)
                {
                    aoe.m_useAttackSettings = true;
                    aoe.m_hitInterval = 0f;
                    aoe.m_chainStartChance = 0f;
                    aoe.m_damagePerLevel = new HitData.DamageTypes();
                }
                sh.m_dodgeable = true;
                sh.m_blockable = true;
                if (pr != null) pr.Setup(c, Vector3.zero, a.m_attackHitNoise, sh, s.Item, null);
                lit = Lights(area);
            }
            if (!lit && flash != null) Object.Instantiate(flash, at, Quaternion.identity);
            Plugin.Debug("[drake] lightning at the target: " + (area != null ? area.name : "no area") + (lit ? " (it has its own light)" : flash != null ? " + " + flash.name : ""));
        }

        // one second of nothing hurting you, from the moment you climb on a drake
        internal static void KeepSafe(float seconds) { _safeUntil = Mathf.Max(_safeUntil, Time.time + seconds); }

        [HarmonyPatch(typeof(Character), "Damage"), HarmonyPrefix]
        private static bool NoFall(Character __instance, HitData hit)
        {
            if (hit == null || __instance != Player.m_localPlayer) return true;
            if (Time.time - OffAt < 6f) Plugin.Debug("[drake] you were hit just after getting off: " + hit.m_hitType + ", " + hit.GetTotalDamage().ToString("0") + " damage, from " + (hit.GetAttacker() != null ? hit.GetAttacker().name : "nothing"));
            if (Time.time > _safeUntil) return true;
            Plugin.Debug("[drake] a hit on the rider was cancelled while climbing on (" + hit.m_hitType + ")");
            return false;
        }
    }
}
