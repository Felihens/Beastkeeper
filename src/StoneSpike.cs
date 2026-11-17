using System;
using System.Collections.Generic;
using UnityEngine;

namespace Beastkeeper
{
    // Ridden stone drake (user, 2026-11-16): no thrown rock any more. A spike of stone bursts out of the ground where
    // you aim, with smaller stones around it. Holding the left button charges it as before: a charged spike is bigger
    // and hits harder. The spike is only a picture (nothing to collide with) and sinks back after SpikeSeconds.
    // When it bursts out, every enemy within its reach is hit once: the closer to the centre, the harder it is hit
    // and the farther it is thrown (a very strong knockback right at the centre).
    // Everybody sees it: the rider's game sends where, how big and a random seed to every player, and each game with
    // the mod raises the same spike. Only the rider's game deals the damage.
    internal static partial class Drake
    {
        internal const float SpikeSeconds = 1.5f, SpikeReach = 60f;
        private const float SpikeAreaPerSize = 2.6f, SpikeCorePerSize = 1.1f;   // reach of the hit, and of the full-strength centre
        private const float PushCentre = 420f, PushEdge = 45f;                    // knockback at the centre and at the edge
        private const string SpikeRpc = "Beastkeeper_StoneSpike";
        private static ZRoutedRpc _spikeRpcOn;

        private static bool UsesSpike(int el) { return el == Earth; }

        // the stone drake's rock (the troll's): its mesh, stone material and crash are used for the spike's look
        internal static GameObject EarthRock()
        {
            Shot s = ShotOf(Earth);
            return s != null ? s.Prefab : null;
        }

        private static float SpikeSize(float charge) { return 1f + charge * 0.8f; }   // the rock grew the same way
        internal static float SpikeArea(float charge) { return SpikeAreaPerSize * SpikeSize(charge); }
        internal static float SpikeCore(float charge) { return SpikeCorePerSize * SpikeSize(charge); }

        // where the spike comes out: the ground under the point you aim at, no farther than SpikeReach
        private static bool SpikeSpot(Character c, Vector3 aim, out Vector3 at, out Vector3 normal)
        {
            Vector3 from = c.transform.position;
            Vector3 flat = aim - from;
            if (flat.magnitude > SpikeReach) aim = from + flat.normalized * SpikeReach;
            normal = Vector3.up;
            RaycastHit h;
            if (Physics.Raycast(aim + Vector3.up * 3f, Vector3.down, out h, 60f, GroundMask(), QueryTriggerInteraction.Ignore))
            {
                at = h.point;
                normal = h.normal;
            }
            else at = aim;
            if (ZoneSystem.instance != null && at.y < ZoneSystem.instance.m_waterLevel - 0.5f) return false;   // under water: nothing comes up
            return true;
        }

        // the attack's moment (Launch)
        private static void Erupt(Character c)
        {
            float charge = _chargeUsed;
            Vector3 at, normal;
            if (!SpikeSpot(c, _aimAt != Vector3.zero ? _aimAt : AimPoint(c, Player.m_localPlayer), out at, out normal))
            {
                if (Mount.RiddenCharacter() == c) Say("The stone can't rise from deep water.");   // a drake on its own just misses
                return;
            }
            int seed = UnityEngine.Random.Range(1, int.MaxValue);
            if (ZRoutedRpc.instance != null)
            {
                ZPackage p = new ZPackage();
                p.Write(at);
                p.Write(normal);
                p.Write(charge);
                p.Write(seed);
                ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, SpikeRpc, new object[] { p });   // ours too: drawn by OnSpike
            }
            else StoneSpike.Raise(at, normal, SpikeSize(charge), seed);
            SpikeHits(c, at, charge);
            Plugin.Debug("[spike] at " + at.ToString("0.0") + ", charge " + charge.ToString("0.00") + ", size " + SpikeSize(charge).ToString("0.00"));
        }

        // every enemy in reach, once: damage and knockback fall off from the centre
        private static void SpikeHits(Character c, Vector3 at, float charge)
        {
            float area = SpikeArea(charge), core = SpikeCore(charge);
            float full = Plugin.DrakeAttackDamage.Value * 2f * _attackMult * DamageOf(c);
            int n = 0;
            foreach (Character o in Character.GetAllCharacters())
            {
                if (o == null || o == c || o.IsDead() || o.IsPlayer() || o == Player.m_localPlayer) continue;
                if (IsAlly(o) || !BaseAI.IsEnemy(c, o)) continue;
                Vector3 d = o.transform.position - at;
                float up = d.y;
                d.y = 0f;
                float dist = Mathf.Max(0f, d.magnitude - o.GetRadius());
                if (dist > area || up < -2f || up > 2.5f + 3.5f * SpikeSize(charge)) continue;
                float k = dist <= core ? 1f : 1f - Mathf.InverseLerp(core, area, dist);   // 1 inside the centre, 0 at the edge
                HitData hd = new HitData();
                hd.m_damage = new HitData.DamageTypes();
                hd.m_damage.m_blunt = full * Mathf.Lerp(0.5f, 1f, k);
                hd.m_pushForce = Mathf.Lerp(PushEdge, PushCentre, k * k) * (1f + 0.5f * charge);
                hd.m_staggerMultiplier = 1f + 2f * k;
                Vector3 away = d.sqrMagnitude > 0.01f ? d.normalized : UnityEngine.Random.insideUnitSphere;
                away.y = 0f;
                hd.m_dir = (away.normalized + Vector3.up * Mathf.Lerp(0.4f, 1.3f, k)).normalized;   // the centre throws up into the air
                hd.m_point = o.GetCenterPoint();
                hd.m_ranged = true;   // its own damage setting, not the tamed-animal multiplier
                hd.m_dodgeable = true;
                hd.m_blockable = true;
                hd.m_toolTier = 2;
                hd.m_hitType = HitData.HitType.EnemyHit;
                hd.SetAttacker(c);
                o.Damage(hd);
                n++;
            }
            if (n > 0) Plugin.Debug("[spike] hit " + n + " enemies (reach " + area.ToString("0.0") + " m, full strength within " + core.ToString("0.0") + " m)");
        }

        internal static void RegisterSpike()
        {
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (rpc == null || rpc == _spikeRpcOn) return;
            rpc.Register<ZPackage>(SpikeRpc, new Action<long, ZPackage>(OnSpike));
            _spikeRpcOn = rpc;
        }

        private static void OnSpike(long sender, ZPackage p)
        {
            try
            {
                Vector3 at = p.ReadVector3();
                Vector3 normal = p.ReadVector3();
                float charge = p.ReadSingle();
                int seed = p.ReadInt();
                if (Player.m_localPlayer != null && Vector3.Distance(Player.m_localPlayer.transform.position, at) > 200f) return;   // too far to see
                StoneSpike.Raise(at, normal, SpikeSize(charge), seed);
            }
            catch (Exception e) { Plugin.Debug("[spike] bad message: " + e.Message); }
        }
    }

    // the picture: a jagged spike of stone and a ring of smaller stones, rising fast, sinking back after SpikeSeconds
    internal class StoneSpike : MonoBehaviour
    {
        private const float Rise = 0.14f, Sink = 0.35f;
        private static Material _mat;
        private readonly List<Transform> _parts = new List<Transform>();
        private readonly List<Vector3> _up = new List<Vector3>(), _down = new List<Vector3>();
        private readonly List<float> _delay = new List<float>();
        private float _born, _size;
        private bool _shook;

        internal static void Raise(Vector3 at, Vector3 normal, float size, int seed)
        {
            GameObject earth = Drake.EarthRock();
            if (earth != null) Rubble.Learn(earth);   // the rock's mesh and stone material
            if (_mat == null) _mat = Rubble.RockMat;
            System.Random rnd = new System.Random(seed);
            if (normal.sqrMagnitude < 0.01f) normal = Vector3.up;
            Vector3 up = Vector3.Slerp(Vector3.up, normal.normalized, 0.35f).normalized;   // mostly straight up, a little with the slope

            GameObject root = new GameObject("Beastkeeper_StoneSpike");
            root.transform.position = at;
            StoneSpike s = root.AddComponent<StoneSpike>();
            s._born = Time.time;
            s._size = size;

            // the big spike, leaning a little to a random side
            float h = 3.4f * size, r = 0.85f * size;
            Quaternion lean = Quaternion.AngleAxis(Range(rnd, 4f, 14f), Quaternion.AngleAxis(Range(rnd, 0f, 360f), Vector3.up) * Vector3.right);
            GameObject spike = Part(root, "spike", SpikeMesh(rnd, h, r), _mat);
            spike.transform.rotation = Quaternion.FromToRotation(Vector3.up, up) * lean * Quaternion.AngleAxis(Range(rnd, 0f, 360f), Vector3.up);
            s.Add(spike.transform, at - up * (h + 0.3f), at - up * (0.35f * size), 0f);

            // smaller ones around it: a few thin spikes and some of the rock's own stones
            int n = 6 + rnd.Next(4);
            for (int i = 0; i < n; i++)
            {
                float ang = (i + (float)rnd.NextDouble() * 0.6f) / n * 360f;
                Vector3 dir = Quaternion.AngleAxis(ang, Vector3.up) * Vector3.forward;
                float dist = Range(rnd, 1.1f, 2.3f) * size;
                Vector3 p = at + dir * dist;
                RaycastHit hit;
                if (Physics.Raycast(p + Vector3.up * 3f, Vector3.down, out hit, 8f, LayerMask.GetMask("terrain", "static_solid", "Default", "piece"), QueryTriggerInteraction.Ignore)) p = hit.point;
                float delay = Range(rnd, 0.02f, 0.12f);
                GameObject g;
                float sh;
                if (i % 3 == 0 || Rubble.RockMesh == null)
                {
                    sh = Range(rnd, 0.7f, 1.4f) * size;
                    g = Part(root, "shard", SpikeMesh(rnd, sh, Range(rnd, 0.22f, 0.36f) * size), _mat);
                    Quaternion out_ = Quaternion.FromToRotation(Vector3.up, (Vector3.up + dir * Range(rnd, 0.3f, 0.7f)).normalized);
                    g.transform.rotation = out_ * Quaternion.AngleAxis(Range(rnd, 0f, 360f), Vector3.up);
                }
                else
                {
                    float w = Range(rnd, 0.35f, 0.7f) * size;
                    g = Part(root, "stone", Rubble.RockMesh, Rubble.RockMat);
                    g.transform.localScale = new Vector3(1f, Range(rnd, 0.7f, 1.3f), 1f) * (w / Mathf.Max(0.01f, Rubble.RockSpan));
                    g.transform.rotation = Quaternion.Euler(Range(rnd, 0f, 360f), Range(rnd, 0f, 360f), Range(rnd, 0f, 360f));
                    sh = w;
                }
                s.Add(g.transform, p - Vector3.up * (sh + 0.2f), p - Vector3.up * (sh * 0.25f), delay);
            }

            // the burst: dust, chips of stone and earth, the crater mark, the rock's own crash and a shake
            Rubble.Burst(at, up, size, 12, true);
            if (Plugin.DrakeEarthCrater.Value) Crater.Make(at, normal, size * 1.3f, true, true);
            Projectile rock = earth != null ? earth.GetComponent<Projectile>() : null;
            if (rock != null && rock.m_hitEffects != null && rock.m_hitEffects.m_effectPrefabs != null)
                foreach (EffectList.EffectData e in rock.m_hitEffects.m_effectPrefabs)
                    if (e != null && e.m_enabled && e.m_prefab != null && e.m_prefab.GetComponent<ZNetView>() == null)
                        UnityEngine.Object.Instantiate(e.m_prefab, at, Quaternion.identity);
            Transform cam = GameCamera.instance != null ? GameCamera.instance.transform : null;
            float far = cam != null ? Vector3.Distance(cam.position, at) : 0f;
            Effects.Shake(Mathf.Lerp(0.9f, 0.2f, Mathf.InverseLerp(5f, 70f, far)) * (0.7f + 0.5f * (size - 1f) / 0.8f));
        }

        private void Add(Transform t, Vector3 down, Vector3 upPos, float delay)
        {
            t.position = down;
            _parts.Add(t);
            _down.Add(down);
            _up.Add(upPos);
            _delay.Add(delay);
        }

        private static float Range(System.Random r, float a, float b) { return a + (float)r.NextDouble() * (b - a); }

        private static GameObject Part(GameObject root, string name, Mesh mesh, Material mat)
        {
            GameObject g = new GameObject(name);
            g.transform.SetParent(root.transform, true);
            g.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer mr = g.AddComponent<MeshRenderer>();
            if (mat != null) mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            return g;
        }

        private void Update()
        {
            float age = Time.time - _born;
            for (int i = 0; i < _parts.Count; i++)
            {
                Transform t = _parts[i];
                if (t == null) continue;
                float a = age - _delay[i];
                float k;
                if (a <= 0f) k = 0f;
                else if (a < Rise) { float x = a / Rise; k = 1f - (1f - x) * (1f - x) * (1f - x); k *= 1.08f; }   // shoots up, overshoots a little
                else if (a < Rise + 0.08f) k = Mathf.Lerp(1.08f, 1f, (a - Rise) / 0.08f);
                else if (age < Drake.SpikeSeconds - Sink) k = 1f;
                else k = 1f - Mathf.SmoothStep(0f, 1f, (age - (Drake.SpikeSeconds - Sink)) / Sink);
                t.position = Vector3.LerpUnclamped(_down[i], _up[i], k);
            }
            if (!_shook && age > Drake.SpikeSeconds - Sink) { _shook = true; Rubble.Burst(transform.position, Vector3.up, _size * 0.6f, 4, true); }   // a little dust as it sinks
            if (age >= Drake.SpikeSeconds) Destroy(gameObject);
        }

        // a jagged stone cone: a few rings that narrow to a point, each corner pushed in or out a little, flat faces
        private static Mesh SpikeMesh(System.Random rnd, float height, float radius)
        {
            const int sides = 7;
            float[] ts = { 0f, 0.22f, 0.45f, 0.68f, 0.86f };
            List<Vector3[]> rings = new List<Vector3[]>();
            float twist = 0f;
            Vector2 bend = new Vector2(Range(rnd, -0.12f, 0.12f), Range(rnd, -0.12f, 0.12f)) * height;
            foreach (float t in ts)
            {
                Vector3[] ring = new Vector3[sides];
                float rr = radius * Mathf.Pow(1f - t, 0.85f);
                twist += Range(rnd, 4f, 14f);
                for (int i = 0; i < sides; i++)
                {
                    float ang = (i / (float)sides * 360f + twist + Range(rnd, -9f, 9f)) * Mathf.Deg2Rad;
                    float j = rr * Range(rnd, 0.78f, 1.18f);
                    ring[i] = new Vector3(Mathf.Cos(ang) * j + bend.x * t * t, t * height, Mathf.Sin(ang) * j + bend.y * t * t);
                }
                rings.Add(ring);
            }
            Vector3 tip = new Vector3(bend.x, height, bend.y);
            List<Vector3> v = new List<Vector3>();
            List<Vector2> uv = new List<Vector2>();
            List<int> tri = new List<int>();
            for (int r = 0; r < rings.Count; r++)
            {
                Vector3[] a = rings[r];
                for (int i = 0; i < sides; i++)
                {
                    int i2 = (i + 1) % sides;
                    float u0 = i / (float)sides * 2f, u1 = (i + 1) / (float)sides * 2f;
                    if (r < rings.Count - 1)
                    {
                        Vector3[] b = rings[r + 1];
                        Quad(v, uv, tri, a[i], a[i2], b[i2], b[i], u0, u1, a[i].y / 2f, b[i].y / 2f);
                    }
                    else
                    {
                        int s0 = v.Count;
                        v.Add(a[i]); v.Add(tip); v.Add(a[i2]);
                        uv.Add(new Vector2(u0, a[i].y / 2f)); uv.Add(new Vector2((u0 + u1) / 2f, tip.y / 2f)); uv.Add(new Vector2(u1, a[i2].y / 2f));
                        tri.Add(s0); tri.Add(s0 + 1); tri.Add(s0 + 2);
                    }
                }
            }
            // every face looks outward (away from the spike's middle line at its height)
            for (int i = 0; i < tri.Count; i += 3)
            {
                Vector3 a = v[tri[i]], b = v[tri[i + 1]], c = v[tri[i + 2]];
                Vector3 mid = (a + b + c) / 3f;
                float t = Mathf.Clamp01(mid.y / Mathf.Max(0.01f, height));
                Vector3 axis = new Vector3(bend.x * t * t, mid.y, bend.y * t * t);
                if (Vector3.Dot(Vector3.Cross(b - a, c - a), mid - axis) < 0f) { int k = tri[i + 1]; tri[i + 1] = tri[i + 2]; tri[i + 2] = k; }
            }
            Mesh m = new Mesh();
            m.name = "Beastkeeper_spike";
            m.SetVertices(v);
            m.SetUVs(0, uv);
            m.SetTriangles(tri, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            m.RecalculateTangents();
            return m;
        }

        // two flat triangles (own corners: the faces stay flat, like broken stone)
        private static void Quad(List<Vector3> v, List<Vector2> uv, List<int> tri, Vector3 a, Vector3 b, Vector3 c, Vector3 d, float u0, float u1, float v0, float v1)
        {
            int s = v.Count;
            v.Add(a); v.Add(d); v.Add(b);
            uv.Add(new Vector2(u0, v0)); uv.Add(new Vector2(u0, v1)); uv.Add(new Vector2(u1, v0));
            v.Add(b); v.Add(d); v.Add(c);
            uv.Add(new Vector2(u1, v0)); uv.Add(new Vector2(u0, v1)); uv.Add(new Vector2(u1, v1));
            for (int i = 0; i < 6; i++) tri.Add(s + i);
        }
    }
}
