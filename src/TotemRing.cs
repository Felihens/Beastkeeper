using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Beastkeeper
{
    // A dashed, slowly turning circle on the ground showing a Beast Totem's area (TotemRadius), where its animals
    // graze. It shows while you place a totem and while you look at one. It only shows: buildings or anything else
    // inside the circle never stop you building the totem.
    internal static class TotemRing
    {
        private const int Dashes = 24, PerDash = 5, Samples = 72;
        private static readonly FieldInfo GhostF = AccessTools.Field(typeof(Player), "m_placementGhost");
        private static readonly Color Tint = new Color(0.55f, 1f, 0.45f, 1f);
        private static GameObject _root;
        private static LineRenderer[] _lines;
        private static readonly float[] _heights = new float[Samples];
        private static Vector3 _center, _sampledAt;
        private static float _radius, _nextSample;
        private static int _mask;

        internal static void Update()
        {
            Player p = Player.m_localPlayer;
            Vector3 center;
            if (p == null || Hud.IsUserHidden() || !Where(p, out center))
            {
                if (_root != null && _root.activeSelf) _root.SetActive(false);
                return;
            }
            if (Vector3.Distance(center, _center) > 0.2f || Mathf.Abs(_radius - Plugin.TotemRadius.Value) > 0.01f) _nextSample = 0f;
            _center = center;
            _radius = Plugin.TotemRadius.Value;
            Draw();
        }

        // the totem being placed, else the totem you look at
        private static bool Where(Player p, out Vector3 center)
        {
            center = Vector3.zero;
            GameObject ghost = GhostF != null ? GhostF.GetValue(p) as GameObject : null;
            if (ghost != null && ghost.activeInHierarchy && ghost.name.StartsWith(Totem.PrefabName)) { center = ghost.transform.position; return true; }
            GameObject hover = p.GetHoverObject();
            Totem t = hover != null ? hover.GetComponentInParent<Totem>() : null;
            if (t == null) return false;
            center = t.transform.position;
            return true;
        }

        private static float Ground(Vector3 at, float fallback)
        {
            if (_mask == 0) _mask = LayerMask.GetMask("terrain", "piece", "static_solid", "Default");
            RaycastHit hit;
            if (Physics.Raycast(at + Vector3.up * 4f, Vector3.down, out hit, 12f, _mask, QueryTriggerInteraction.Ignore)) return hit.point.y;
            if (ZoneSystem.instance != null) return ZoneSystem.instance.GetGroundHeight(at);
            return fallback;
        }

        private static void Build()
        {
            _root = new GameObject("Beastkeeper_TotemRing");
            Object.DontDestroyOnLoad(_root);
            Material mat = new Material(Shader.Find("Sprites/Default"));
            mat.color = new Color(1.8f, 1.8f, 1.8f, 1f);   // a touch over 1: a light glow from the game's bloom
            _lines = new LineRenderer[Dashes];
            for (int i = 0; i < Dashes; i++)
            {
                GameObject g = new GameObject("dash" + i);
                g.transform.SetParent(_root.transform, false);
                LineRenderer l = g.AddComponent<LineRenderer>();
                l.sharedMaterial = mat;
                l.useWorldSpace = true;
                l.positionCount = PerDash;
                l.numCapVertices = 2;
                l.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                l.receiveShadows = false;
                _lines[i] = l;
            }
        }

        private static void Draw()
        {
            if (_root == null) Build();
            if (!_root.activeSelf) _root.SetActive(true);
            if (Time.time >= _nextSample || Vector3.Distance(_sampledAt, _center) > 0.2f)
            {
                _nextSample = Time.time + 0.5f;
                _sampledAt = _center;
                for (int i = 0; i < Samples; i++)
                {
                    float a = i / (float)Samples * Mathf.PI * 2f;
                    Vector3 pt = _center + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * _radius;
                    _heights[i] = Ground(pt, _center.y) + 0.12f;
                }
            }
            float t = Time.time;
            float pulse = 0.75f + 0.2f * Mathf.Sin(t * 4f);
            Color c = new Color(Tint.r, Tint.g, Tint.b, pulse);
            float width = 0.09f;
            float turn = t * 0.2f;
            float step = Mathf.PI * 2f / Dashes;
            for (int d = 0; d < Dashes; d++)
            {
                LineRenderer l = _lines[d];
                l.startColor = l.endColor = c;
                l.startWidth = l.endWidth = width;
                for (int k = 0; k < PerDash; k++)
                {
                    float a = turn + d * step + k / (float)(PerDash - 1) * step * 0.55f;
                    float fi = Mathf.Repeat(a, Mathf.PI * 2f) / (Mathf.PI * 2f) * Samples;
                    int i0 = Mathf.FloorToInt(fi) % Samples, i1 = (i0 + 1) % Samples;
                    float y = Mathf.Lerp(_heights[i0], _heights[i1], fi - Mathf.Floor(fi));
                    l.SetPosition(k, new Vector3(_center.x + Mathf.Cos(a) * _radius, y, _center.z + Mathf.Sin(a) * _radius));
                }
            }
        }
    }
}
