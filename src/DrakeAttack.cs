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

        // the attack in progress
        private static Character _pending;       // waiting for the animation's moment to shoot
        private static Player _pendingRider;
        private static float _startedAt;
        private static bool _launched;
        private static int _burstLeft;
        private static float _burstNext, _burstEnd;
        private static Animator _slowed;
        private static float _slowedSpeed;
        private const float BurstSlow = 0.5f, BurstSeconds = 1.6f, BurstEvery = 0.16f;

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
            if (_queuedC == null && !c.IsFlying())
            {
                if (!TakeOffToAttack(c)) { Say("Your drake is too tired to fly up and attack."); return; }
                _queuedC = c; _queuedRider = rider; _queuedAt = Time.time + 1.0f;   // shoot once it's up
                return;
            }

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
                if (h.StartAttack(null, false))
                {
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
        [HarmonyPatch(typeof(Attack), "ProjectileAttackTriggered"), HarmonyPrefix]
        private static bool AtTheMoment(Attack __instance)
        {
            if (_pending == null || _launched) return true;
            Humanoid ch = Traverse.Create(__instance).Field("m_character").GetValue<Humanoid>();
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
                _pending = null; _burstLeft = 0;
                return;
            }
            int el = ElementOf(c);
            // the animation never reached its moment (a bite, not a spit): shoot after a short wait
            if (!_launched && Time.time - _startedAt > 1.4f) Launch(c, rider);
            if (_burstLeft > 0 && Time.time >= _burstNext)
            {
                _burstNext = Time.time + BurstEvery;
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
                float from = IsStream(el) ? _burstEnd - BurstSeconds : Time.time;
                _nextShot = Mathf.Max(Time.time, from + Mathf.Max(0.2f, Plugin.DrakeAttackCooldown.Value));
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
        private static float _attackUntil;

        private static void NewAttack(int el)
        {
            _dealt.Clear();
            _attackUntil = Time.time + (IsStream(el) ? BurstSeconds : 0f) + 5f;
        }

        [HarmonyPatch(typeof(Character), "Damage"), HarmonyPrefix, HarmonyPriority(Priority.Low)]
        private static bool Budget(Character __instance, HitData hit)
        {
            if (hit == null || __instance == null || Time.time > _attackUntil) return true;
            Character drake = Mount.RiddenCharacter();
            if (drake == null || hit.GetAttacker() != drake || !Is(drake)) return true;
            float total = hit.GetTotalDamage();
            if (total <= 0f) return true;
            float done;
            _dealt.TryGetValue(__instance, out done);
            float cap = Plugin.DrakeAttackDamage.Value * 2f;
            if (ElementOf(drake) == Fire) cap = Mathf.Min(cap, Plugin.DrakeFireMax.Value);   // its burning keeps hurting after: less up front
            float left = cap - done;
            if (left <= 0.01f) return false;   // this attack has done its part on this target
            if (total > left) hit.m_damage.Modify(left / total);
            _dealt[__instance] = done + Mathf.Min(total, left);
            return true;
        }

        private static void Launch(Character c, Player rider)
        {
            _launched = true;
            NewAttack(ElementOf(c));
            int el = ElementOf(c);
            Shot s = ShotOf(el);
            if (s == null) return;
            Spawn(c, rider, s, true);
            if (IsStream(el))
            {
                _burstLeft = Mathf.RoundToInt(BurstSeconds / BurstEvery);
                _burstNext = Time.time + BurstEvery;
                _burstEnd = Time.time + BurstSeconds;
            }
        }

        private static void Spawn(Character c, Player rider, Shot s, bool first)
        {
            if (s == null || c == null) return;
            int el = ElementOf(c);
            Vector3 from = c.GetHeadPoint();
            Vector3 target = AimPoint(c, rider);
            Vector3 dir = (target - from).normalized;
            Attack a = s.Attack;
            Quaternion look = Quaternion.LookRotation(dir);
            float scale = Mathf.Max(1f, c.transform.localScale.x);
            Vector3 mouth = from + dir * 1.2f * scale;

            HitData.DamageTypes dmg = new HitData.DamageTypes();
            float amount = Plugin.DrakeAttackDamage.Value;
            if (IsStream(el)) amount /= Mathf.Max(1f, BurstSeconds / BurstEvery) / 2f;   // many small hits, about twice one shot in all
            else if (el == Frost || el == Earth) amount *= 2f;   // one big hit worth the same: the stream's total, or lightning's shot plus strike
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
            float speed = Mathf.Max(20f, a.m_projectileVel) * Mathf.Max(0.5f, Plugin.DrakeAttackSpeed.Value);
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
                if (el == Earth && go.GetComponent<Projectile>() != null) _rocks.Add(go.GetComponent<Projectile>());
                pr.Setup(c, s.AtTarget ? Vector3.zero : d * speed, a.m_attackHitNoise, hd, s.Item, null);
                if (el == Frost && !s.AtTarget) go.transform.localScale *= Mathf.Max(0.5f, Plugin.DrakeFrostSize.Value);   // a bigger ice ball
            }

            // the lightning also strikes where it lands, with the same effect as the game's own lightning (Eikthyr's):
            // the lightning area (hits everything there) and, if that has no flash of its own, the flash
            if (el == Lightning && first) Strike(c, target, a, s);
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
        private static readonly HashSet<Projectile> _rocks = new HashSet<Projectile>();

        [HarmonyPatch(typeof(Projectile), "OnHit"), HarmonyPostfix]
        private static void RockHit(Projectile __instance, Vector3 hitPoint)
        {
            if (!_rocks.Remove(__instance)) return;
            _rocks.RemoveWhere(r => r == null);
            Transform cam = GameCamera.instance != null ? GameCamera.instance.transform : null;
            float far = cam != null ? Vector3.Distance(cam.position, hitPoint) : 0f;
            Effects.Shake(Mathf.Lerp(0.5f, 0.15f, Mathf.InverseLerp(5f, 60f, far)));
        }

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
                sh.m_damage.m_lightning = Plugin.DrakeAttackDamage.Value;
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
