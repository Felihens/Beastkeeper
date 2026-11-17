using UnityEngine;

namespace Beastkeeper
{
    // Chips thrown off where the stone drake's rock hits or breaks: only a picture, they fly for about a second.
    // Rock chips are small copies of the rock itself (its own mesh and stone material, so they look like stone);
    // lumps of earth are plain dark brown.
    internal class Rubble : MonoBehaviour
    {
        private static Mesh _rockMesh;
        private static Material _rockMat;
        private static float _rockSpan = 1f;      // the rock mesh's largest side, to size the chips
        private static Material[] _earth;
        private static Mesh _cube;
        private Vector3 _vel, _spin;

        // the rock's look, for the stone spike (StoneSpike.cs)
        internal static Mesh RockMesh { get { return _rockMesh; } }
        internal static Material RockMat { get { return _rockMat; } }
        internal static float RockSpan { get { return _rockSpan; } }
        private float _born, _life;

        // remember what the rock looks like, from the first one thrown
        internal static void Learn(GameObject rock)
        {
            if (_rockMesh != null || rock == null) return;
            float best = 0f;
            foreach (MeshFilter mf in rock.GetComponentsInChildren<MeshFilter>(true))
            {
                MeshRenderer mr = mf.GetComponent<MeshRenderer>();
                if (mf.sharedMesh == null || mr == null || mr.sharedMaterial == null) continue;
                float span = Mathf.Max(mf.sharedMesh.bounds.size.x, Mathf.Max(mf.sharedMesh.bounds.size.y, mf.sharedMesh.bounds.size.z));
                if (span > best) { best = span; _rockMesh = mf.sharedMesh; _rockMat = mr.sharedMaterial; _rockSpan = span; }
            }
            Plugin.Debug("[drake] rock chips: " + (_rockMesh != null ? "copies of " + _rockMesh.name : "no rock mesh found, plain lumps"));
        }

        private static bool Earth()
        {
            if (_earth != null) return _earth.Length > 0;
            Shader sh = Shader.Find("Sprites/Default");   // a plain colour the game always has (the crater uses it too)
            if (sh == null) { _earth = new Material[0]; return false; }
            Color[] cols = { new Color(0.23f, 0.16f, 0.1f), new Color(0.3f, 0.21f, 0.13f), new Color(0.17f, 0.12f, 0.08f), new Color(0.36f, 0.33f, 0.3f) };
            _earth = new Material[cols.Length];
            for (int i = 0; i < cols.Length; i++) { _earth[i] = new Material(sh); _earth[i].color = cols[i]; Object.DontDestroyOnLoad(_earth[i]); }
            GameObject tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _cube = tmp.GetComponent<MeshFilter>().sharedMesh;
            Object.Destroy(tmp);
            return true;
        }

        internal static void Burst(Vector3 at, Vector3 normal, float size, int count, bool earth)
        {
            if (normal.sqrMagnitude < 0.01f) normal = Vector3.up;
            normal.Normalize();
            bool lumps = Earth();
            for (int i = 0; i < count; i++)
            {
                // on the ground most of what flies is earth; the rest, and all of it when the rock breaks, is stone
                bool stone = _rockMesh != null && !(earth && Random.value < 0.7f);
                if (!stone && !lumps) continue;
                GameObject g = new GameObject("Beastkeeper_Rubble");
                MeshFilter mf = g.AddComponent<MeshFilter>();
                MeshRenderer rd = g.AddComponent<MeshRenderer>();
                float s = Random.Range(0.12f, 0.3f) * size;
                if (stone)
                {
                    mf.sharedMesh = _rockMesh;
                    rd.sharedMaterial = _rockMat;
                    g.transform.localScale = Vector3.one * (s / Mathf.Max(0.01f, _rockSpan));
                }
                else
                {
                    mf.sharedMesh = _cube;
                    rd.sharedMaterial = _earth[earth ? Random.Range(0, 3) : 3];
                    g.transform.localScale = new Vector3(s, s * Random.Range(0.6f, 1f), s * Random.Range(0.8f, 1.2f)) * 0.8f;
                }
                rd.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                g.transform.position = at + normal * 0.2f;
                g.transform.rotation = Random.rotation;
                Rubble r = g.AddComponent<Rubble>();
                Vector3 d = Random.onUnitSphere;
                if (Vector3.Dot(d, normal) < 0.2f) d = Vector3.Reflect(d, normal);
                r._vel = (d + normal * 0.6f).normalized * Random.Range(5f, 11f);
                r._spin = Random.onUnitSphere * Random.Range(180f, 540f);
                r._born = Time.time;
                r._life = Random.Range(0.7f, 1.2f);
            }
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            _vel += Vector3.down * 22f * dt;
            transform.position += _vel * dt;
            transform.Rotate(_spin * dt);
            float age = Time.time - _born;
            if (age > _life * 0.6f) transform.localScale *= Mathf.Max(0f, 1f - dt * 2.5f);
            if (age >= _life) Destroy(gameObject);
        }
    }
}
