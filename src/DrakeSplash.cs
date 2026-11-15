using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Beastkeeper
{
    // What happens when a drake's shot lands (the class carries [HarmonyPatch] in DrakeAttack.cs):
    //  - frost: the ice ball breaks into shards that fly off in random directions, never too close to each other
    //  - earth: the rock is charged by holding the left button, bounces once off the ground and leaves a crater
    internal static partial class Drake
    {
        private static float _shotCharge;       // 0..1, what this shot was charged to (set when the button is let go)
        private static float _chargeUsed;       // the charge of the attack in progress
        private static float _attackMult = 1f;  // its damage multiplier (stone drake, charged)
        private static float _chargeStart = -1f;
        private static GUIStyle _maxStyle;

        // what an attack takes from the flight bar: always the same, charged or not
        private static float AttackCost() { return Plugin.DrakeAttackAirCost.Value; }

        // ---- the left button: a stone drake charges while it is held and shoots when it is let go; the others shoot at once
        internal static void Trigger(Character c, Player rider, bool held, bool was)
        {
            bool charges = ElementOf(c) == Earth && Plugin.DrakeEarthChargeSeconds.Value > 0.05f;
            if (!charges)
            {
                _chargeStart = -1f;
                _shotCharge = 0f;
                if (held && !was) Shoot(c, rider);
                return;
            }
            if (held && !was && _chargeStart < 0f)
            {
                // can't start now (still waiting, not aiming...): Shoot says why, if it needs to
                if (Time.time < _nextShot || _pending != null || _burstLeft > 0 || !Aiming) { _shotCharge = 0f; Shoot(c, rider); return; }
                _chargeStart = Time.time;
                return;
            }
            if (_chargeStart >= 0f && !held)
            {
                _shotCharge = Mathf.Clamp01((Time.time - _chargeStart) / Plugin.DrakeEarthChargeSeconds.Value);
                _chargeStart = -1f;
                Shoot(c, rider);
            }
        }

        // the charge bar, under the aim point
        internal static void DrawCharge()
        {
            if (_chargeStart < 0f) return;
            if (Mount.RiddenCharacter() == null || !Aiming) { _chargeStart = -1f; return; }
            float k = Mathf.Clamp01((Time.time - _chargeStart) / Mathf.Max(0.05f, Plugin.DrakeEarthChargeSeconds.Value));
            float s = Screen.height / 1080f;
            float w = 220f * s, h = 14f * s;
            Rect r = new Rect((Screen.width - w) / 2f, Screen.height * 0.62f, w, h);
            Color old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.65f);
            GUI.DrawTexture(new Rect(r.x - 2f, r.y - 2f, r.width + 4f, r.height + 4f), Texture2D.whiteTexture);
            bool full = k >= 0.999f;
            float pulse = full ? 0.5f + 0.5f * Mathf.Sin(Time.time * 18f) : 0f;
            GUI.color = full ? Color.Lerp(new Color(1f, 0.3f, 0.12f), Color.white, pulse) : Color.Lerp(new Color(0.95f, 0.72f, 0.3f), new Color(1f, 0.3f, 0.12f), k);
            GUI.DrawTexture(new Rect(r.x, r.y, r.width * k, r.height), Texture2D.whiteTexture);
            GUI.color = old;
            if (_maxStyle == null)
            {
                _maxStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
                _maxStyle.normal.textColor = Color.white;
            }
            _maxStyle.fontSize = Mathf.RoundToInt(17f * s);
            if (full) GUI.Label(new Rect(r.x, r.y - 26f * s, r.width, 24f * s), "MAX", _maxStyle);
        }

        // ---- the rock
        private class RockInfo
        {
            internal float Charge;
            internal HashSet<Character> Hit = new HashSet<Character>();
            internal bool Rolling;
            internal float RollStart, Spin, Size = 1f, Rolled;
        }

        private static readonly Dictionary<Projectile, RockInfo> _rocks = new Dictionary<Projectile, RockInfo>();
        private static readonly System.Reflection.FieldInfo DidHit = AccessTools.Field(typeof(Projectile), "m_didHit");
        private static readonly System.Reflection.FieldInfo MVel = AccessTools.Field(typeof(Projectile), "m_vel");
        private static bool _passed;   // the hit we just let the rock fly through

        private static float ShotSpeed(Attack a, int el) { return ShotSpeedAt(a, el, _chargeUsed); }

        private static float ShotSpeedAt(Attack a, int el, float charge)
        {
            float v = Mathf.Max(20f, a.m_projectileVel) * Mathf.Max(0.5f, Plugin.DrakeAttackSpeed.Value);
            if (el == Earth) v *= 1f - 0.2f * charge;   // a charged rock is heavier: it leaves slower
            return v;
        }

        // how heavy the rock is: it falls faster than the game's own, and more the more it is charged
        private static float EarthGravity(Shot s) { return EarthGravityAt(s, _chargeUsed); }

        private static float EarthGravityAt(Shot s, float charge)
        {
            Projectile p = s != null && s.Prefab != null ? s.Prefab.GetComponent<Projectile>() : null;
            return Mathf.Max(p != null ? p.m_gravity : 0f, 6f) * (1.2f + 0.8f * charge);
        }

        // how far the current charge has got (0 when not charging)
        private static float ChargeNow()
        {
            if (_chargeStart < 0f) return 0f;
            return Mathf.Clamp01((Time.time - _chargeStart) / Mathf.Max(0.05f, Plugin.DrakeEarthChargeSeconds.Value));
        }

        // the direction to throw something with speed v and gravity g so that it lands on 'to' (the low arc)
        private static Vector3 Lob(Vector3 from, Vector3 to, float v, float g)
        {
            Vector3 flat = to - from;
            float dy = flat.y;
            flat.y = 0f;
            float dh = flat.magnitude;
            if (dh < 1f || v < 1f) return (to - from).normalized;
            float disc = v * v * v * v - g * (g * dh * dh + 2f * dy * v * v);
            float ang = disc < 0f ? 45f * Mathf.Deg2Rad : Mathf.Atan((v * v - Mathf.Sqrt(disc)) / (g * dh));
            ang = Mathf.Clamp(ang, -30f * Mathf.Deg2Rad, 50f * Mathf.Deg2Rad);
            return (flat.normalized * Mathf.Cos(ang) + Vector3.up * Mathf.Sin(ang)).normalized;
        }

        private static void Rock(Projectile fly, GameObject go)
        {
            _rocks[fly] = new RockInfo { Charge = _chargeUsed };
            Rubble.Learn(go);   // the chips that fly off are small copies of this rock
            float grow = 1f + _chargeUsed * 0.8f;
            if (grow > 1.01f) { go.transform.localScale *= grow; fly.m_rayRadius *= grow; }
            fly.m_gravity = EarthGravity(ShotOf(Earth));
            if (Plugin.DrakeEarthRicochet.Value)
            {
                // the game's own bounce, off the ground or anything solid in the way: it is thrown back at the angle
                // of the surface, and the next hit breaks it. (Characters aren't bounced off: see BeforeHit.)
                fly.m_bounce = true;
                fly.m_bounceOnWater = false;
                fly.m_maxBounces = 1;
                fly.m_bouncePower = 0.55f;
                fly.m_bounceRoughness = 0.1f;
                fly.m_minBounceVel = 0.5f;
            }
        }

        private const float RollDecel = 2f, RollMin = 7f, RollMax = 22f, RollSeconds = 6f, RollDistance = 15f;
        private static readonly System.Reflection.FieldInfo DidBounce = AccessTools.Field(typeof(Projectile), "m_didBounce");

        // The rock's first hit decides what it does:
        //  - the ground: it doesn't bounce, it rolls on (see Roll below)
        //  - anything solid (a wall, a tree, a boulder): the game bounces it off at the angle of the surface, and the next hit breaks it
        //  - an enemy in the way is hit once, pushed a little to the side, and the rock flies on;
        //    anything that isn't an enemy (you, your party, your animals) it simply flies through
        [HarmonyPatch(typeof(Projectile), "OnHit"), HarmonyPrefix]
        private static bool BeforeHit(Projectile __instance, Collider collider, Vector3 hitPoint, bool water, Vector3 normal)
        {
            _passed = false;
            // an ice shard flies through the enemy its ice ball hit (a big one used to catch the shards as they left)
            Character skip;
            if (_shardSkip.Count > 0 && collider != null && !water && _shardSkip.TryGetValue(__instance, out skip) && skip != null
                && collider.GetComponentInParent<Character>() == skip) { _passed = true; return false; }
            RockInfo info;
            if (_rocks.Count == 0 || collider == null || water || !_rocks.TryGetValue(__instance, out info)) return true;
            Character ch = collider.GetComponentInParent<Character>();
            if (ch == null)
            {
                if (collider.GetComponent<Heightmap>() == null)
                {
                    // loose things (a body on the ground, a dropped item, a falling log, a cart): it goes through them
                    Rigidbody rb = collider.attachedRigidbody;
                    if (rb != null && !rb.isKinematic) { _passed = true; return false; }
                    // small things on the ground (branches, stones, berry bushes, stumps, low boulders): it runs over them
                    if (!Solid(collider)) { _passed = true; return false; }
                    Plugin.Debug("[drake] the rock breaks on " + collider.name);
                    return true;                                               // a wall, a tree, a boulder: it breaks (or bounces, if EarthRicochet is on)
                }
                if (info.Rolling) { _passed = true; return false; }            // rolling along the ground
                if (!Plugin.DrakeEarthRoll.Value || MVel == null) return true;
                Vector3 v = (Vector3)MVel.GetValue(__instance);
                Vector3 hv = new Vector3(v.x, 0f, v.z);
                if (hv.magnitude < 0.5f) { hv = __instance.transform.forward; hv.y = 0f; }   // straight down: it rolls the way it faced
                if (hv.sqrMagnitude < 0.0001f) hv = Vector3.forward;
                info.Rolling = true;
                info.RollStart = Time.time;
                info.Rolled = 0f;
                info.Size = 1f + info.Charge * 0.8f;
                MVel.SetValue(__instance, hv.normalized * Mathf.Clamp(hv.magnitude * 0.75f, RollMin, RollMax));
                __instance.m_bounce = false;
                __instance.m_maxBounces = 0;                                   // the first thing solid it meets breaks it
                __instance.m_ttl = Mathf.Max(__instance.m_ttl, RollSeconds + 2f);
                Landing(hitPoint, normal, info.Charge, false, true);
                Plugin.Debug("[drake] the rock lands and rolls on");
                _passed = true;
                return false;
            }
            _passed = true;
            Character owner = Mount.RiddenCharacter();
            if (ch == owner || ch.IsDead() || info.Hit.Contains(ch)) return false;
            if (owner == null || ch == Player.m_localPlayer || IsAlly(ch) || !BaseAI.IsEnemy(owner, ch)) return false;
            if (ch.IsBoss()) { _passed = false; return true; }   // a boss stops it: the rock hits and breaks on it
            info.Hit.Add(ch);
            Vector3 fly = __instance.GetVelocity();
            if (fly.sqrMagnitude < 0.01f) fly = __instance.transform.forward;
            Vector3 side = Vector3.Cross(fly.normalized, Vector3.up);
            if (Vector3.Dot(side, ch.transform.position - hitPoint) < 0f) side = -side;   // away from the rock's path
            HitData h = new HitData();
            h.m_damage = __instance.m_damage;
            h.m_pushForce = __instance.m_attackForce * 0.5f;
            h.m_point = hitPoint;
            h.m_dir = (side + Vector3.up * 0.15f).normalized;
            h.m_dodgeable = __instance.m_dodgeable;
            h.m_blockable = __instance.m_blockable;
            h.m_ranged = true;
            h.m_hitCollider = collider;
            h.SetAttacker(owner);
            h.m_hitType = HitData.HitType.EnemyHit;
            ch.Damage(h);
            if (__instance.m_hitEffects != null) __instance.m_hitEffects.Create(hitPoint, Quaternion.identity, null, 1f, -1, ch.GetZDOID());
            Plugin.Debug("[drake] the rock hit " + ch.name + " and flew on");
            return false;
        }

        // ---- the ice ball
        private class Ice
        {
            internal Character Owner;
            internal Shot Shot;
            internal HitData Hit;
            internal float Speed;
        }

        private static readonly Dictionary<Projectile, Ice> _ice = new Dictionary<Projectile, Ice>();
        private static readonly Dictionary<Projectile, Character> _shardSkip = new Dictionary<Projectile, Character>();   // shard -> the enemy it flies through

        private const float ShardSeek = 18f;   // shards look for enemies this close to where the ball broke

        private static bool Shards(Shot s) { return s != null && !s.AtTarget && Plugin.DrakeFrostShards.Value > 0; }

        // ---- after any hit of a projectile
        [HarmonyPatch(typeof(Projectile), "OnHit"), HarmonyPostfix]
        private static void AfterHit(Projectile __instance, Collider collider, Vector3 hitPoint, bool water, Vector3 normal)
        {
            if (_passed) { _passed = false; return; }   // it only flew through a character
            if (_rocks.Count == 0 && _ice.Count == 0) return;
            // the game calls this for things a shot can't hit too (the drake itself as the shot leaves, its rider):
            // nothing happened then, so nothing breaks or shatters
            bool ended = DidHit == null || (bool)DidHit.GetValue(__instance);
            bool bounced = DidBounce != null && (bool)DidBounce.GetValue(__instance);
            if (!ended && !bounced) return;
            RockInfo rock;
            if (_rocks.TryGetValue(__instance, out rock))
            {
                bool last = DidHit == null || (bool)DidHit.GetValue(__instance);   // a bounce isn't the end of it
                if (last) _rocks.Remove(__instance);
                if (_rocks.Count > 24) { List<Projectile> dead = new List<Projectile>(); foreach (Projectile k in _rocks.Keys) if (k == null) dead.Add(k); foreach (Projectile k in dead) _rocks.Remove(k); }
                if (!water) Landing(hitPoint, normal, rock.Charge, last, collider != null && collider.GetComponent<Heightmap>() != null);
            }
            Ice ice;
            if (_ice.TryGetValue(__instance, out ice))
            {
                _ice.Remove(__instance);
                if (_ice.Count > 24) { List<Projectile> dead = new List<Projectile>(); foreach (Projectile k in _ice.Keys) if (k == null) dead.Add(k); foreach (Projectile k in dead) _ice.Remove(k); }
                Character struck = collider != null ? collider.GetComponentInParent<Character>() : null;
                if (!water && struck != null && ice.Owner != null && !IsAlly(struck) && BaseAI.IsEnemy(ice.Owner, struck)) Blizzard.Begin(ice.Owner, struck);
                if (!water) Shatter(ice, hitPoint, normal, struck);
            }
        }

        // something that stops a rock: a building piece, a tree, an ore rock, or anything at least a metre tall
        private static bool Solid(Collider col)
        {
            if (col.GetComponentInParent<Pickable>() != null) return false;
            if (col.GetComponentInParent<WearNTear>() != null || col.GetComponentInParent<TreeBase>() != null
                || col.GetComponentInParent<MineRock>() != null || col.GetComponentInParent<MineRock5>() != null) return true;
            return col.bounds.size.y >= 1f;
        }

        // where the rock lands, bounces or breaks: the camera shakes, chips fly and the ground is marked
        private static void Landing(Vector3 at, Vector3 normal, float charge, bool breaks, bool ground)
        {
            Transform cam = GameCamera.instance != null ? GameCamera.instance.transform : null;
            float far = cam != null ? Vector3.Distance(cam.position, at) : 0f;
            Effects.Shake(Mathf.Lerp(0.5f, 0.15f, Mathf.InverseLerp(5f, 60f, far)) * (breaks ? 1f : 0.5f) * (1f + charge));
            float size = 1f + charge * 0.8f;
            if (ground && Plugin.DrakeEarthCrater.Value) Crater.Make(at, normal, size, breaks, true);
            Rubble.Burst(at, normal, size, breaks ? 9 : 3, ground);
        }

        // ---- the rock rolls: it follows the ground, slows down (faster on a rise, quicker on a slope down) and breaks
        // when it stops, meets something solid or reaches the water. Enemies on the way are hit by BeforeHit.
        [HarmonyPatch(typeof(Projectile), "FixedUpdate"), HarmonyPostfix]
        private static void Roll(Projectile __instance)
        {
            if (_rocks.Count == 0 || MVel == null) return;
            RockInfo info;
            if (!_rocks.TryGetValue(__instance, out info) || !info.Rolling) return;
            ZNetView nv = __instance.GetComponent<ZNetView>();
            if (nv == null || !nv.IsValid() || !nv.IsOwner()) return;
            ZoneSystem zs = ZoneSystem.instance;
            if (zs == null) return;
            Transform tr = __instance.transform;
            Vector3 pos = tr.position;
            Vector3 v = (Vector3)MVel.GetValue(__instance);
            Vector3 h = new Vector3(v.x, 0f, v.z);
            float dt = Time.fixedDeltaTime;
            float gy = zs.GetGroundHeight(pos);
            float sp = h.magnitude;
            if (sp > 0.01f)
            {
                Vector3 dir = h / sp;
                float slope = (zs.GetGroundHeight(pos + dir * 1.5f) - gy) / 1.5f;
                sp = Mathf.Clamp(sp - (RollDecel + slope * 12f) * dt, RollMin, RollMax + 4f);   // it keeps rolling: only the distance ends it
                h = dir * sp;
                info.Rolled += sp * dt;
            }
            if (info.Rolled >= RollDistance || Time.time - info.RollStart > RollSeconds || gy < zs.m_waterLevel - 0.1f)
            {
                Plugin.Debug("[drake] the rock stops after " + info.Rolled.ToString("0.0") + " m" + (gy < zs.m_waterLevel - 0.1f ? " (water)" : ""));
                BreakRock(__instance, info, pos);
                return;
            }
            MVel.SetValue(__instance, h);
            float r = 0.45f * info.Size;
            pos.y = gy + r;
            tr.position = pos;
            info.Spin += sp / r * Mathf.Rad2Deg * dt;
            tr.rotation = Quaternion.LookRotation(h) * Quaternion.Euler(info.Spin, 0f, 0f);
        }

        // the rolling rock stops: it falls apart where it is
        private static void BreakRock(Projectile p, RockInfo info, Vector3 at)
        {
            _rocks.Remove(p);
            if (p.m_hitEffects != null) p.m_hitEffects.Create(at, Quaternion.identity, null, 1f, -1, new ZDOID());
            Rubble.Burst(at, Vector3.up, info.Size, 9, false);
            if (Plugin.DrakeEarthCrater.Value) Crater.Make(at, Vector3.up, info.Size, false, false);   // just a puff of dust
            if (ZNetScene.instance != null) ZNetScene.instance.Destroy(p.gameObject);
            Plugin.Debug("[drake] the rolling rock broke");
        }

        // the ball hits for 80% of an attack and the shards, all together, for 40% (half of the ball's): 120% in all
        internal const float ShardsShare = 0.5f;

        private static HitData PartOf(HitData h, int parts)
        {
            HitData c = new HitData();
            c.m_damage = h.m_damage;
            c.m_damage.Modify(ShardsShare / Mathf.Max(1, parts));
            c.m_pushForce = h.m_pushForce * ShardsShare / Mathf.Max(1, parts);
            c.m_toolTier = h.m_toolTier;
            c.m_dodgeable = h.m_dodgeable;
            c.m_ranged = h.m_ranged;
            c.m_blockable = h.m_blockable;
            return c;
        }

        // the ball breaks: N shards leave the spot in random directions (above the surface it hit),
        // none closer than ShardAngle to another one
        // struck: the enemy the ball hit, if any. The shards then leave from just outside it, away from it, and
        // go for the others: born inside it they used to hit it again at once and vanish, so nothing showed.
        private static void Shatter(Ice ice, Vector3 at, Vector3 normal, Character struck)
        {
            int n = Plugin.DrakeFrostShards.Value;
            if (n <= 0 || ice == null || ice.Owner == null || ice.Shot == null || ice.Shot.Prefab == null) return;
            if (struck != null && struck != ice.Owner)
            {
                Vector3 c = struck.GetCenterPoint();
                Vector3 away = at - c;
                away.y = 0f;
                if (away.sqrMagnitude < 0.01f) away = -struck.transform.forward;
                away = away.normalized + Vector3.up * 0.6f;
                normal = away.normalized;
                float reachOut = 0.6f;
                Collider body = struck.GetComponent<Collider>();
                if (body != null) reachOut += Mathf.Max(body.bounds.extents.x, body.bounds.extents.z);
                at = c + normal * reachOut;
            }
            else struck = null;
            if (normal.sqrMagnitude < 0.01f) normal = Vector3.up;
            normal.Normalize();
            if (Vector3.Dot(normal, Vector3.up) < -0.3f) normal = -normal;   // hit something from below/behind: still scatter upward
            float angle = Plugin.DrakeFrostShardAngle.Value;
            List<Vector3> dirs = new List<Vector3>();
            List<float> reach = new List<float>();   // how far each shard has to fly (0 = just flies off)
            Vector3 origin = at + normal * 0.3f;

            // first: one shard for each enemy close by, the nearest first, aimed at it
            List<Character> foes = new List<Character>();
            foreach (Character ch in Character.GetAllCharacters())
            {
                if (ch == null || ch.IsDead() || ch == ice.Owner || ch == Player.m_localPlayer || ch == struck) continue;
                if (Vector3.Distance(ch.transform.position, at) > ShardSeek) continue;
                if (IsAlly(ch) || !BaseAI.IsEnemy(ice.Owner, ch)) continue;
                foes.Add(ch);
            }
            foes.Sort((x, y) => Vector3.Distance(x.transform.position, at).CompareTo(Vector3.Distance(y.transform.position, at)));
            for (int i = 0; i < foes.Count && dirs.Count < n; i++)
            {
                Vector3 to = foes[i].GetCenterPoint() - origin;
                if (to.sqrMagnitude < 0.25f) continue;
                dirs.Add(to.normalized);
                reach.Add(to.magnitude);
            }

            // the rest fly off in random directions, mostly sideways (flattened), never too close to another shard
            while (dirs.Count < n)
            {
                Vector3 pick = normal;
                for (int tries = 0; tries < 400; tries++)
                {
                    Vector3 d = Random.onUnitSphere;
                    d.y *= 0.3f;
                    d.Normalize();
                    if (Vector3.Dot(d, normal) < 0f) d = Vector3.Reflect(d, normal);
                    if (Vector3.Dot(d, normal) < 0f) continue;
                    bool ok = true;
                    foreach (Vector3 o in dirs) if (Vector3.Angle(o, d) < angle) { ok = false; break; }
                    pick = d;
                    if (ok) break;
                    if (tries % 50 == 49) angle = Mathf.Max(0f, angle - 5f);   // crowded: ease the limit a little
                }
                dirs.Add(pick);
                reach.Add(0f);
            }
            if (_shardSkip.Count > 48)
            {
                List<Projectile> gone = new List<Projectile>();
                foreach (Projectile k in _shardSkip.Keys) if (k == null) gone.Add(k);
                foreach (Projectile k in gone) _shardSkip.Remove(k);
            }
            int made = 0;
            for (int k = 0; k < dirs.Count; k++)
            {
                Vector3 dir = dirs[k];
                bool aimed = reach[k] > 0f;
                GameObject go = Object.Instantiate(ice.Shot.Prefab, at + normal * 0.3f + dir * 0.3f, Quaternion.LookRotation(dir));
                IProjectile pr = go.GetComponent<IProjectile>();
                if (pr == null) { Object.Destroy(go); continue; }
                NoSpawns(go);
                float v = ice.Speed * (aimed ? 0.9f : 0.6f);   // the ones that chase an enemy fly faster
                pr.Setup(ice.Owner, dir * v, ice.Shot.Attack != null ? ice.Shot.Attack.m_attackHitNoise : 0f, PartOf(ice.Hit, n), ice.Shot.Item, null);
                Projectile fly = go.GetComponent<Projectile>();
                if (fly != null) fly.m_ttl = aimed ? Mathf.Clamp(reach[k] / v * 1.6f, 0.4f, 1.6f) : Mathf.Min(fly.m_ttl, 1.2f);
                if (fly != null && struck != null) _shardSkip[fly] = struck;
                go.transform.localScale *= 0.8f;
                made++;
            }
            Plugin.Debug("[drake] the ice ball broke into " + made + " shards (at least " + angle.ToString("0") + " degrees apart)" + (struck != null ? " on " + struck.name : ""));
        }
    }
}
