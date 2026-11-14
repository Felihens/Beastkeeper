using System.Collections.Generic;
using UnityEngine;

namespace Beastkeeper
{
    // Where the stone drake's rock lands on the ground: a splash of dark earth thrown outwards (flat on the ground)
    // and a puff of dust. Only a picture (no collider); it fades after a few seconds.
    internal class Crater : MonoBehaviour
    {
        private static readonly Queue<GameObject> Alive = new Queue<GameObject>();
        private static Sprite _soil, _puff;
        private const float Life = 5f, FadeTime = 1.5f, GrowTime = 0.12f;
        private float _born, _size;
        private SpriteRenderer _decal;

        private static bool Ready()
        {
            if (_soil != null && _puff != null) return true;
            try
            {
                Texture2D a = MakeSoil();
                Texture2D b = MakePuff();
                _soil = Sprite.Create(a, new Rect(0f, 0f, a.width, a.height), new Vector2(0.5f, 0.5f), 64f);
                _puff = Sprite.Create(b, new Rect(0f, 0f, b.width, b.height), new Vector2(0.5f, 0.5f), 64f);
                Object.DontDestroyOnLoad(_soil);
                Object.DontDestroyOnLoad(_puff);
                return true;
            }
            catch (System.Exception) { return false; }
        }

        // dark earth, irregular edge, with a few streaks of dirt thrown out from the middle
        private static Texture2D MakeSoil()
        {
            const int N = 128;
            Texture2D t = new Texture2D(N, N, TextureFormat.RGBA32, false);
            t.wrapMode = TextureWrapMode.Clamp;
            float p1 = Random.value * 6.28f, p2 = Random.value * 6.28f, p3 = Random.value * 6.28f;
            int rays = 9;
            float[] ang = new float[rays], len = new float[rays];
            for (int i = 0; i < rays; i++) { ang[i] = Random.value * 6.2832f; len[i] = Random.Range(0.7f, 1f); }
            Color[] px = new Color[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float u = (x + 0.5f) / N * 2f - 1f, v = (y + 0.5f) / N * 2f - 1f;
                    float r = Mathf.Sqrt(u * u + v * v);
                    float th = Mathf.Atan2(v, u);
                    float edge = 0.5f + 0.1f * Mathf.Sin(th * 3f + p1) + 0.06f * Mathf.Sin(th * 7f + p2) + 0.04f * Mathf.Sin(th * 13f + p3);
                    float a = Mathf.Clamp01((edge - r) / 0.16f);
                    float streak = 0f;
                    for (int i = 0; i < rays; i++)
                    {
                        float d = Mathf.Abs(Mathf.DeltaAngle(th * Mathf.Rad2Deg, ang[i] * Mathf.Rad2Deg)) * Mathf.Deg2Rad;
                        float s = Mathf.Exp(-d * d / 0.008f) * Mathf.Clamp01((r - 0.2f) / 0.2f) * Mathf.Clamp01((len[i] - r) / 0.2f);
                        if (s > streak) streak = s;
                    }
                    float n = Random.value;
                    float alpha = Mathf.Max(a, streak * 0.85f) * (0.7f + 0.3f * n);
                    float core = Mathf.Clamp01(1f - r / 0.4f);
                    Color c = Color.Lerp(new Color(0.3f, 0.22f, 0.14f), new Color(0.1f, 0.07f, 0.045f), Mathf.Clamp01(core + 0.25f * n));
                    c.a = alpha;
                    px[y * N + x] = c;
                }
            t.SetPixels(px);
            t.Apply();
            Object.DontDestroyOnLoad(t);
            return t;
        }

        private static Texture2D MakePuff()
        {
            const int N = 64;
            Texture2D t = new Texture2D(N, N, TextureFormat.RGBA32, false);
            t.wrapMode = TextureWrapMode.Clamp;
            Color[] px = new Color[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float u = (x + 0.5f) / N * 2f - 1f, v = (y + 0.5f) / N * 2f - 1f;
                    float r = Mathf.Sqrt(u * u + v * v);
                    float a = Mathf.Clamp01(1f - r);
                    px[y * N + x] = new Color(1f, 1f, 1f, a * a);
                }
            t.SetPixels(px);
            t.Apply();
            Object.DontDestroyOnLoad(t);
            return t;
        }

        // decal: the splash on the ground (a rock that only breaks in the air or rolls to a stop has none, just the dust)
        internal static void Make(Vector3 at, Vector3 normal, float size, bool big, bool decal)
        {
            if (!Ready()) return;
            if (normal.sqrMagnitude < 0.01f) normal = Vector3.up;
            normal.Normalize();
            if (Vector3.Angle(normal, Vector3.up) > 35f) normal = Vector3.Slerp(Vector3.up, normal, 0.4f).normalized;   // steep ground: stay almost flat
            GameObject root = new GameObject("Beastkeeper_Crater");
            root.transform.position = at + normal * 0.07f;
            root.transform.rotation = Quaternion.LookRotation(-normal) * Quaternion.Euler(0f, 0f, Random.value * 360f);
            float d = (big ? 4.2f : 2.6f) * size;   // width of the splash, in metres
            Crater c = root.AddComponent<Crater>();
            c._born = Time.time;
            c._size = d / 2f;
            if (decal)
            {
                SpriteRenderer sr = root.AddComponent<SpriteRenderer>();
                sr.sprite = _soil;
                sr.color = new Color(1f, 1f, 1f, 0.92f);
                sr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                c._decal = sr;
                root.transform.localScale = Vector3.one * c._size * 0.6f;
            }
            Dust(root.transform, at, normal, size, big);
            Alive.Enqueue(root);
            while (Alive.Count > 10) { GameObject old = Alive.Dequeue(); if (old != null) Object.Destroy(old); }
        }

        // the puff of dust: soft brown clouds that spread over the ground, rise a little and fade
        private static void Dust(Transform parent, Vector3 at, Vector3 normal, float size, bool big)
        {
            GameObject go = new GameObject("dust");
            go.transform.position = at + normal * 0.25f;
            go.transform.rotation = Quaternion.LookRotation(normal);   // its XY plane is the ground
            go.transform.SetParent(parent, true);
            ParticleSystem ps = go.AddComponent<ParticleSystem>();
            ps.Stop();
            ParticleSystem.MainModule main = ps.main;
            main.duration = 0.5f;
            main.loop = false;
            main.playOnAwake = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.8f, 1.5f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(1.5f * size, 4.5f * size);
            main.startSize = new ParticleSystem.MinMaxCurve(0.9f * size, 1.9f * size);
            main.startColor = new Color(0.5f, 0.4f, 0.29f, 0.6f);
            main.gravityModifier = -0.04f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, 6.28f);
            ParticleSystem.EmissionModule em = ps.emission;
            em.rateOverTime = 0f;
            em.SetBursts(new ParticleSystem.Burst[] { new ParticleSystem.Burst(0f, (short)(big ? 18 : 9)) });
            ParticleSystem.ShapeModule sh = ps.shape;
            sh.shapeType = ParticleSystemShapeType.Circle;
            sh.radius = 0.3f * size;
            ParticleSystem.ColorOverLifetimeModule col = ps.colorOverLifetime;
            col.enabled = true;
            Gradient g = new Gradient();
            g.SetKeys(new GradientColorKey[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                      new GradientAlphaKey[] { new GradientAlphaKey(0.8f, 0f), new GradientAlphaKey(0.45f, 0.45f), new GradientAlphaKey(0f, 1f) });
            col.color = g;
            ParticleSystem.SizeOverLifetimeModule sz = ps.sizeOverLifetime;
            sz.enabled = true;
            sz.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.5f, 1f, 1.5f));
            ParticleSystemRenderer pr = go.GetComponent<ParticleSystemRenderer>();
            Shader shader = Shader.Find("Sprites/Default");
            if (pr != null && shader != null && _puff != null)
            {
                Material m = new Material(shader);
                m.mainTexture = _puff.texture;
                pr.material = m;
            }
            ps.Play();
        }

        private void Update()
        {
            float age = Time.time - _born;
            if (_decal != null)
            {
                transform.localScale = Vector3.one * _size * (0.6f + 0.4f * Mathf.Clamp01(age / GrowTime));
                float a = 0.92f * Mathf.Clamp01((Life - age) / FadeTime);
                _decal.color = new Color(1f, 1f, 1f, a);
            }
            if (age >= Life) Destroy(gameObject);
        }

        private void OnDestroy()
        {
            foreach (ParticleSystemRenderer pr in GetComponentsInChildren<ParticleSystemRenderer>())
                if (pr != null && pr.material != null) Destroy(pr.material);
        }
    }
}
