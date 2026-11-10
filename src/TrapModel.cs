using System.Collections.Generic;
using UnityEngine;

namespace Beastkeeper
{
    // The leg-hold trap's look, made in code: two iron half-ring jaws with teeth, hinged on a bar across the middle,
    // a round pan to step on, a leaf spring at each end, two wooden planks under it all, and a short chain to a
    // wooden stake. Sizes in metres for a trap of size 1 (the trap scales it). x across, z along the jaws, y up.
    internal static class TrapModel
    {
        internal const float JawRadius = 0.22f;
        internal const float HingeY = 0.045f;

        private class B
        {
            public readonly List<Vector3> V = new List<Vector3>();
            public readonly List<Vector2> UV = new List<Vector2>();
            public readonly List<int> T = new List<int>();

            public void Tri(Vector3 a, Vector3 b, Vector3 c)
            {
                // both sides, each with its own corners (so each side gets its own lighting): no face ever shows up missing
                for (int side = 0; side < 2; side++)
                {
                    int i = V.Count;
                    V.Add(a); V.Add(b); V.Add(c);
                    UV.Add(new Vector2(a.x + a.z, a.y) * 3f); UV.Add(new Vector2(b.x + b.z, b.y) * 3f); UV.Add(new Vector2(c.x + c.z, c.y) * 3f);
                    if (side == 0) { T.Add(i); T.Add(i + 1); T.Add(i + 2); }
                    else { T.Add(i); T.Add(i + 2); T.Add(i + 1); }
                }
            }

            public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) { Tri(a, b, c); Tri(a, c, d); }

            public void Box(Vector3 centre, Vector3 size, Quaternion rot)
            {
                Vector3 h = size * 0.5f;
                Vector3[] p = new Vector3[8];
                for (int i = 0; i < 8; i++)
                    p[i] = centre + rot * new Vector3((i & 1) == 0 ? -h.x : h.x, (i & 2) == 0 ? -h.y : h.y, (i & 4) == 0 ? -h.z : h.z);
                Quad(p[0], p[1], p[3], p[2]); Quad(p[4], p[6], p[7], p[5]);
                Quad(p[0], p[4], p[5], p[1]); Quad(p[2], p[3], p[7], p[6]);
                Quad(p[0], p[2], p[6], p[4]); Quad(p[1], p[5], p[7], p[3]);
            }

            // a square bar following a path
            public void Bar(Vector3[] path, float w, float h)
            {
                for (int i = 0; i < path.Length - 1; i++)
                {
                    Vector3 a = path[i], b = path[i + 1];
                    Vector3 f = (b - a).normalized;
                    Vector3 side = Vector3.Cross(Vector3.up, f).normalized * (w * 0.5f);
                    Vector3 up = Vector3.up * (h * 0.5f);
                    Vector3[] ca = { a - side - up, a + side - up, a + side + up, a - side + up };
                    Vector3[] cb = { b - side - up, b + side - up, b + side + up, b - side + up };
                    for (int k = 0; k < 4; k++) Quad(ca[k], ca[(k + 1) % 4], cb[(k + 1) % 4], cb[k]);
                }
            }

            // a three-sided tooth: base on the bar, tip pointing 'dir'
            public void Tooth(Vector3 at, Vector3 along, Vector3 dir, float width, float length)
            {
                Vector3 a = at - along * (width * 0.5f), b = at + along * (width * 0.5f), c = at + Vector3.up * (width * 0.6f);
                Vector3 tip = at + dir * length + Vector3.up * (width * 0.15f);
                Tri(a, b, tip); Tri(b, c, tip); Tri(c, a, tip); Tri(a, b, c);
            }

            public void Torus(Vector3 centre, Quaternion rot, float r, float tube, int n = 10, int m = 6)
            {
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < m; j++)
                        Quad(TorusPoint(centre, rot, r, tube, n, m, i, j), TorusPoint(centre, rot, r, tube, n, m, i + 1, j),
                             TorusPoint(centre, rot, r, tube, n, m, i + 1, j + 1), TorusPoint(centre, rot, r, tube, n, m, i, j + 1));
            }

            private static Vector3 TorusPoint(Vector3 centre, Quaternion rot, float r, float tube, int n, int m, int ii, int jj)
            {
                float u = ii * Mathf.PI * 2f / n, v = jj * Mathf.PI * 2f / m;
                Vector3 ring = new Vector3(Mathf.Cos(u), 0f, Mathf.Sin(u));
                return centre + rot * (ring * (r + tube * Mathf.Cos(v)) + Vector3.up * (tube * Mathf.Sin(v)));
            }

            public Mesh Mesh(string name)
            {
                Mesh m = new Mesh { name = name };
                m.SetVertices(V); m.SetUVs(0, UV); m.SetTriangles(T, 0);
                m.RecalculateNormals(); m.RecalculateBounds();
                return m;
            }
        }

        // one jaw: a half ring on the +z side of its hinge (the jaw's own origin), teeth on its inner edge
        internal static Mesh Jaw()
        {
            B b = new B();
            const int n = 16;
            float r = JawRadius;
            Vector3[] path = new Vector3[n + 1];
            for (int i = 0; i <= n; i++) { float t = Mathf.PI * i / n; path[i] = new Vector3(r * Mathf.Cos(t), 0f, r * Mathf.Sin(t)); }
            b.Bar(path, 0.022f, 0.02f);
            for (int i = 1; i < n; i += 2)
            {
                float t = Mathf.PI * i / n;
                Vector3 at = new Vector3(r * Mathf.Cos(t), 0.004f, r * Mathf.Sin(t));
                Vector3 inward = -new Vector3(Mathf.Cos(t), 0f, Mathf.Sin(t));
                Vector3 along = new Vector3(-Mathf.Sin(t), 0f, Mathf.Cos(t));
                b.Tooth(at + inward * 0.008f, along, inward, 0.03f, 0.045f);
            }
            return b.Mesh("Beastkeeper_TrapJaw");
        }

        // the iron frame: hinge bar, pan, two leaf springs and the chain
        internal static Mesh Frame()
        {
            B b = new B();
            float r = JawRadius, y = HingeY;
            b.Box(new Vector3(0f, y, 0f), new Vector3(2f * r + 0.06f, 0.022f, 0.026f), Quaternion.identity);       // hinge bar
            for (int s = -1; s <= 1; s += 2)
            {
                // a leaf spring: a long flat V going out from the jaw ends
                float x0 = s * (r + 0.02f);
                b.Box(new Vector3(x0 + s * 0.12f, y + 0.012f, 0f), new Vector3(0.24f, 0.012f, 0.05f), Quaternion.Euler(0f, 0f, s * -4f));
                b.Box(new Vector3(x0 + s * 0.12f, y + 0.036f, 0f), new Vector3(0.24f, 0.012f, 0.045f), Quaternion.Euler(0f, 0f, s * 6f));
                b.Box(new Vector3(x0 + s * 0.245f, y + 0.024f, 0f), new Vector3(0.02f, 0.04f, 0.05f), Quaternion.identity);
            }
            // the pan: a round plate in the middle, on a short stem
            const int seg = 14;
            float pr = 0.075f, py = y + 0.016f;
            for (int i = 0; i < seg; i++)
            {
                float a0 = Mathf.PI * 2f * i / seg, a1 = Mathf.PI * 2f * (i + 1) / seg;
                Vector3 p0 = new Vector3(pr * Mathf.Cos(a0), py, pr * Mathf.Sin(a0)), p1 = new Vector3(pr * Mathf.Cos(a1), py, pr * Mathf.Sin(a1));
                b.Tri(new Vector3(0f, py, 0f), p1, p0);
                b.Quad(p0, p1, p1 - Vector3.up * 0.008f, p0 - Vector3.up * 0.008f);
            }
            // chain from the right spring to the stake
            Vector3 c0 = new Vector3(r + 0.29f, 0.02f, 0f);
            for (int i = 0; i < 5; i++)
                b.Torus(c0 + new Vector3(i * 0.045f, 0f, 0.012f * Mathf.Sin(i * 1.7f)),
                        i % 2 == 0 ? Quaternion.identity : Quaternion.Euler(90f, 0f, 0f), 0.022f, 0.006f);
            return b.Mesh("Beastkeeper_TrapFrame");
        }

        // two crossed planks under the trap and a stake for the chain
        internal static Mesh Wood()
        {
            B b = new B();
            b.Box(new Vector3(0f, 0.012f, 0f), new Vector3(0.7f, 0.024f, 0.13f), Quaternion.Euler(0f, 8f, 0f));
            b.Box(new Vector3(0f, 0.014f, 0f), new Vector3(0.13f, 0.028f, 0.58f), Quaternion.Euler(0f, -6f, 0f));
            // the stake, leaning a little, pointed top
            Vector3 s = new Vector3(JawRadius + 0.55f, 0.11f, 0f);
            Quaternion lean = Quaternion.Euler(0f, 0f, -8f);
            b.Box(s, new Vector3(0.05f, 0.22f, 0.05f), lean);
            Vector3 top = s + lean * new Vector3(0f, 0.11f, 0f);
            Vector3 tip = top + lean * new Vector3(0f, 0.05f, 0f);
            Vector3[] q = { top + lean * new Vector3(-0.025f, 0f, -0.025f), top + lean * new Vector3(0.025f, 0f, -0.025f),
                            top + lean * new Vector3(0.025f, 0f, 0.025f), top + lean * new Vector3(-0.025f, 0f, 0.025f) };
            for (int i = 0; i < 4; i++) b.Tri(q[i], q[(i + 1) % 4], tip);
            return b.Mesh("Beastkeeper_TrapWood");
        }

        // blood drops on the planks and the pan: a few flat blobs
        internal static Mesh Blood()
        {
            B b = new B();
            Random.State old = Random.state;
            Random.InitState(1234);
            for (int i = 0; i < 9; i++)
            {
                Vector2 p = Random.insideUnitCircle * 0.2f;
                float s = Random.Range(0.02f, 0.05f);
                float y = new Vector2(p.x, p.y).magnitude < 0.08f ? HingeY + 0.018f : 0.027f;
                int seg = 7;
                Vector3 c = new Vector3(p.x, y, p.y);
                for (int k = 0; k < seg; k++)
                {
                    float a0 = Mathf.PI * 2f * k / seg, a1 = Mathf.PI * 2f * (k + 1) / seg;
                    float w0 = s * (0.7f + 0.3f * Mathf.Sin(a0 * 3f + i)), w1 = s * (0.7f + 0.3f * Mathf.Sin(a1 * 3f + i));
                    b.Tri(c, c + new Vector3(Mathf.Cos(a1) * w1, 0f, Mathf.Sin(a1) * w1), c + new Vector3(Mathf.Cos(a0) * w0, 0f, Mathf.Sin(a0) * w0));
                }
            }
            Random.state = old;
            return b.Mesh("Beastkeeper_TrapBlood");
        }
    }
}
