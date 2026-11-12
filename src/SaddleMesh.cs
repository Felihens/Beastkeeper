using System.Collections.Generic;
using UnityEngine;

namespace Beastkeeper
{
    // A leather riding saddle made in code, shaped to the animal: a blanket draped over its back, a padded seat on
    // top with a raised front and back, and a strap down each side. The back is taken as an arch (an ellipse seen
    // from the front) as wide as the animal, so the same code fits a boar, a wolf or a bear.
    // Built with its top centre at (0,0,0): x right, y up, z forward.
    internal static class SaddleMesh
    {
        private class Builder
        {
            public readonly List<Vector3> V = new List<Vector3>();
            public readonly List<Vector2> UV = new List<Vector2>();
            public readonly List<int> T = new List<int>();

            // a grid of nu x nv cells, drawn on both sides (so it never shows up hollow from any angle)
            public void Grid(int nu, int nv, System.Func<float, float, Vector3> pos, Rect area)
            {
                for (int side = 0; side < 2; side++)
                {
                    int start = V.Count;
                    for (int j = 0; j <= nv; j++)
                        for (int i = 0; i <= nu; i++)
                        {
                            float u = (float)i / nu, v = (float)j / nv;
                            V.Add(pos(u, v));
                            UV.Add(new Vector2(area.x + area.width * u, area.y + area.height * v));
                        }
                    for (int j = 0; j < nv; j++)
                        for (int i = 0; i < nu; i++)
                        {
                            int a = start + j * (nu + 1) + i, b = a + 1, c = a + nu + 1, d = c + 1;
                            if (side == 0) { T.Add(a); T.Add(c); T.Add(b); T.Add(b); T.Add(c); T.Add(d); }
                            else { T.Add(a); T.Add(b); T.Add(c); T.Add(b); T.Add(d); T.Add(c); }
                        }
                }
            }

            public Mesh ToMesh()
            {
                Mesh m = new Mesh { name = "Beastkeeper_Saddle" };
                m.SetVertices(V);
                m.SetUVs(0, UV);
                m.SetTriangles(T, 0);
                m.RecalculateNormals();
                m.RecalculateBounds();
                return m;
            }
        }

        // texture areas (see MakeTexture)
        private static readonly Rect Leather = new Rect(0f, 0.5f, 0.5f, 0.5f);
        private static readonly Rect Cloth = new Rect(0.5f, 0.5f, 0.5f, 0.5f);
        private static readonly Rect Strap = new Rect(0f, 0f, 1f, 0.5f);

        // halfWidth: half the width of the animal's back (metres in the animal's frame)
        internal static Mesh Build(float halfWidth, float depth, float pinch)
        {
            float a = Mathf.Max(0.08f, halfWidth);   // arch: half width
            float b = a * Mathf.Clamp(depth, 0.3f, 2.5f);   // arch: height of the curve (bigger = sides hang straighter, closer to the body)
            float cy = -b;                           // centre of the arch (its top is at y = 0)
            Builder mb = new Builder();

            // a point on the back's arch: phi = angle from the top (sideways), z along the back, off = height above it
            float bPhi = 72f * Mathf.Deg2Rad;
            pinch = Mathf.Clamp(pinch, -0.5f, 0.8f);
            // pinch draws the lower parts of the sides in towards each other (more the lower they are)
            System.Func<float, float, float, Vector3> arch = (phi, z, off) =>
            {
                float s = phi / bPhi;
                return new Vector3((a + off) * Mathf.Sin(phi) * (1f - pinch * s * s), cy + (b + off) * Mathf.Cos(phi), z);
            };

            // blanket: wide and thin, hanging well down the sides, its front and back ends drooping a little
            float bl = a * 1.05f, bt = 0.018f;
            System.Func<float, float, float, Vector3> blanket = (u, v, t) =>
            {
                float phi = Mathf.Lerp(-bPhi, bPhi, u), z = Mathf.Lerp(-bl, bl, v);
                Vector3 p = arch(phi, z, 0.012f + t * bt);
                p.y -= 0.05f * a * (z / bl) * (z / bl);
                return p;
            };
            Shell(mb, blanket, 16, 10, Cloth);

            // seat: narrower, thicker, dipped in the middle, rising at the back (cantle) and a little at the front
            float sl = a * 0.72f, sPhi = 48f * Mathf.Deg2Rad, st = 0.035f, baseOff = 0.012f + bt + 0.004f;
            System.Func<float, float, float, Vector3> seat = (u, v, t) =>
            {
                float phi = Mathf.Lerp(-sPhi, sPhi, u), z = Mathf.Lerp(-sl, sl, v);
                float side = Mathf.Abs(u - 0.5f) * 2f;            // 0 in the middle, 1 at the edges
                float along = z / sl;                              // -1 back, +1 front
                float rise = along < 0f ? 0.11f * a * Mathf.Pow(-along, 3f) : 0.06f * a * Mathf.Pow(along, 3f);
                float thick = st * (1f - 0.5f * side * side);      // thinner towards the edges
                Vector3 p = arch(phi, z, baseOff + t * thick + t * rise * (1f - side * 0.6f));
                return p;
            };
            Shell(mb, seat, 12, 12, Leather);

            // a strap down each side, from the seat's edge to below the blanket
            float strapW = Mathf.Max(0.03f, a * 0.14f);
            for (int s = -1; s <= 1; s += 2)
            {
                float sideSign = s;
                System.Func<float, float, float, Vector3> strap = (u, v, t) =>
                {
                    float phi = sideSign * Mathf.Lerp(sPhi * 0.8f, bPhi + 12f * Mathf.Deg2Rad, v);
                    float z = Mathf.Lerp(-strapW, strapW, u) - a * 0.05f;
                    return arch(phi, z, 0.012f + bt + 0.006f + t * 0.008f);
                };
                Shell(mb, strap, 2, 8, Strap);
            }
            return mb.ToMesh();
        }

        // a thick sheet: its top, its underside and its four edges
        private static void Shell(Builder mb, System.Func<float, float, float, Vector3> f, int nu, int nv, Rect area)
        {
            mb.Grid(nu, nv, (u, v) => f(u, v, 1f), area);
            mb.Grid(nu, nv, (u, v) => f(u, v, 0f), area);
            Rect edge = new Rect(Strap.x, Strap.y, Strap.width, Strap.height * 0.15f);
            mb.Grid(nu, 1, (u, t) => f(u, 0f, t), edge);
            mb.Grid(nu, 1, (u, t) => f(u, 1f, t), edge);
            mb.Grid(nv, 1, (v, t) => f(0f, v, t), edge);
            mb.Grid(nv, 1, (v, t) => f(1f, v, t), edge);
        }

        // ------------------------------------------------------------------ look

        private static Material _mat;

        // the lox saddle's own material (so it is lit like the game's things), with our leather painted on it
        private static readonly Material[] _byTier = new Material[Tiers.Saddles + 1];

        // each tier's saddle: the same leather, dyed in the tier's colour
        internal static Material Material(int tier)
        {
            tier = Mathf.Clamp(tier, 1, Tiers.Saddles);
            if (_byTier[tier] != null) return _byTier[tier];
            Material m = new Material(Material());
            m.name = "Beastkeeper_Saddle" + tier;
            m.color = Recipes.Tint[tier];
            return _byTier[tier] = m;
        }

        internal static Material Material()
        {
            if (_mat != null) return _mat;
            Material src = null;
            GameObject lox = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab("SaddleLox") : null;
            MeshRenderer mr = lox != null ? lox.GetComponentInChildren<MeshRenderer>(true) : null;
            if (mr != null) src = mr.sharedMaterial;
            _mat = src != null ? new Material(src) : new Material(Shader.Find("Standard"));
            _mat.name = "Beastkeeper_Saddle";
            _mat.mainTexture = MakeTexture();
            _mat.color = Color.white;
            foreach (string tex in new[] { "_BumpMap", "_MetallicGlossMap", "_EmissionMap", "_DetailAlbedoMap", "_DetailNormalMap" })
                if (_mat.HasProperty(tex)) _mat.SetTexture(tex, null);
            _mat.DisableKeyword("_NORMALMAP");
            _mat.DisableKeyword("_EMISSION");
            if (_mat.HasProperty("_Metallic")) _mat.SetFloat("_Metallic", 0f);
            if (_mat.HasProperty("_Glossiness")) _mat.SetFloat("_Glossiness", 0.2f);
            Plugin.Debug("[ride] saddle material from " + (src != null ? src.name + " (" + src.shader.name + ")" : "Standard"));
            return _mat;
        }

        // 256x256: top left brown leather with stitching, top right a dark red blanket with a gold border, the bottom
        // half darker leather for the straps and edges
        private static Texture2D MakeTexture()
        {
            const int N = 256, H = N / 2;
            Texture2D tx = new Texture2D(N, N, TextureFormat.RGBA32, true);
            Color[] px = new Color[N * N];
            Color leather = new Color(0.42f, 0.26f, 0.14f), cloth = new Color(0.46f, 0.09f, 0.07f);
            Color gold = new Color(0.72f, 0.55f, 0.24f), strap = new Color(0.24f, 0.15f, 0.09f), thread = new Color(0.86f, 0.78f, 0.62f);
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float n = Mathf.PerlinNoise(x * 0.06f, y * 0.06f) * 0.6f + Mathf.PerlinNoise(x * 0.25f, y * 0.25f) * 0.4f;
                    Color c;
                    if (y < H)
                    {
                        c = strap * (0.85f + 0.3f * n);
                        int sy = y % 16;
                        if ((sy == 3 || sy == 12) && (x / 5) % 2 == 0) c = Color.Lerp(c, thread, 0.6f);
                    }
                    else if (x < H)
                    {
                        int lx = x, ly = y - H;
                        c = leather * (0.8f + 0.4f * n);
                        int d = Mathf.Min(Mathf.Min(lx, H - 1 - lx), Mathf.Min(ly, H - 1 - ly));
                        if (d < 3) c *= 0.6f;
                        else if (d == 7 && ((lx + ly) / 4) % 2 == 0) c = thread;
                    }
                    else
                    {
                        int lx = x - H, ly = y - H;
                        float weave = ((lx + ly) % 4 < 2 ? 0.95f : 1.05f);
                        c = cloth * (0.85f + 0.3f * n) * weave;
                        int d = Mathf.Min(Mathf.Min(lx, H - 1 - lx), Mathf.Min(ly, H - 1 - ly));
                        if (d < 4) c = cloth * 0.6f;
                        else if (d < 12) c = gold * (0.85f + 0.3f * n);
                        else if (d < 14) c = cloth * 0.5f;
                    }
                    c.a = 1f;
                    px[y * N + x] = c;
                }
            tx.SetPixels(px);
            tx.Apply(true, true);
            tx.name = "Beastkeeper_SaddleLeather";
            tx.wrapMode = TextureWrapMode.Clamp;
            return tx;
        }
    }
}
