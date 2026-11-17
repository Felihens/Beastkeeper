using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Beastkeeper
{
    // While you hold the right mouse button on a drake, red marks with a light glow, painted flat on the ground, show
    // where its attack will land (the class carries [HarmonyPatch] in DrakeAttack.cs):
    //  - lightning: the circle that takes the damage and, dashed, the wider one that pushes (read from the game's own areas)
    //  - stone: two stripes under the rock's path, as far apart as the rock is wide (they spread while it charges, up
    //    to the full-charge rock), a ring with a smaller ring inside where it lands, and dashed stripes where it rolls on
    //  - poison: no marks (its reach is short, the fan never matched it)
    //  - frost and fire: the enemy the shot goes for is marked (a ring at its feet, a mark over its head), the view
    //    zooms in a little, and the shot leads that enemy a little. Lightning marks its enemy the same way.
    internal static partial class Drake
    {
        private static readonly Color Red = new Color(1f, 0.16f, 0.1f, 0.95f);
        private static readonly Color Halo = new Color(1f, 0.1f, 0.05f, 0.3f);
        private const float HaloWidth = 3.2f;   // the glow around a mark, times the mark's width
        private const float MarkWidth = 0.09f, Lift = 0.06f;
        private const float AssistDegrees = 5f, AssistNear = 3f;     // frost and fire: how close to your aim an enemy must be
        private const float AimZoom = 0.86f;                         // frost and fire: the view narrows to this share
        // poison: a wider spray that flies farther, each drop hurting a little around where it lands
        private const float PoisonSpread = 7f, PoisonReach = 1.6f, PoisonWide = 1.5f, PoisonArea = 1.5f;

        private static readonly List<LineRenderer> _marks = new List<LineRenderer>();
        private static int _markUsed;
        private static Material _solid, _dashed, _haloSolid, _haloDashed;
        private static Transform _markRoot;
        private static int _groundMask;
        private static float _zoom = 1f;
        private static readonly List<Vector3> _pts = new List<Vector3>();
        private static readonly List<Vector3> _path = new List<Vector3>();
        private static readonly List<Vector3> _near = new List<Vector3>();
        private static readonly List<Vector3> _far = new List<Vector3>();
        private static readonly System.Reflection.FieldInfo CamMain = AccessTools.Field(typeof(GameCamera), "m_camera");
        private static readonly System.Reflection.FieldInfo CamSky = AccessTools.Field(typeof(GameCamera), "m_skyCamera");
        private static float _previewErrorAt = -100f;

        // after the camera has moved for this frame (and after the riding camera), so the marks never lag behind it
        [HarmonyPatch(typeof(GameCamera), "LateUpdate"), HarmonyPostfix, HarmonyPriority(Priority.Low)]
        private static void Preview(GameCamera __instance)
        {
            _markUsed = 0;
            Character c = Mount.RiddenCharacter();
            Player rider = Player.m_localPlayer;
            bool on = Plugin.Enabled.Value && c != null && rider != null && Aiming && Is(c) && !c.IsDead();
            int el = on ? ElementOf(c) : 0;
            _zoom = Mathf.MoveTowards(_zoom, on && (el == Frost || el == Fire) ? AimZoom : 1f, Time.deltaTime * 0.8f);
            if (_zoom < 0.999f) Zoom(__instance, _zoom);
            if (on && Plugin.DrakeAttackPreview.Value)
            {
                try { Draw(c, rider, el); }
                catch (System.Exception e)
                {
                    if (Time.time - _previewErrorAt > 10f) { _previewErrorAt = Time.time; Plugin.Log.LogWarning("Attack preview: " + e); }
                }
            }
            for (int i = _markUsed; i < _marks.Count; i++)
                if (_marks[i] != null && _marks[i].enabled) _marks[i].enabled = false;
        }

        // the game sets the field of view again every frame, so this lasts one frame
        private static void Zoom(GameCamera gc, float k)
        {
            float fov = gc.m_fov * k;
            Camera main = CamMain != null ? CamMain.GetValue(gc) as Camera : null;
            Camera sky = CamSky != null ? CamSky.GetValue(gc) as Camera : null;
            if (main != null) main.fieldOfView = fov;
            if (sky != null) sky.fieldOfView = fov;
        }

        private static void Draw(Character c, Player rider, int el)
        {
            Shot s = ShotOf(el);
            if (s == null || s.Prefab == null) return;
            Vector3 aim = AimPoint(c, rider);
            switch (el)
            {
                case Lightning: DrawLightning(c, aim, s); break;
                case Earth: DrawSpike(c, aim); break;
                default:
                    Character foe = UsesBeam(el) ? null : FoeFor(c, aim, el, s);   // the beam goes exactly where you aim
                    if (foe != null) MarkFoe(foe);
                    break;
            }
        }

        // ------------------------------------------------------------ which enemy an attack goes for

        private static Character FoeFor(Character c, Vector3 aim, int el, Shot s)
        {
            if (el == Lightning) return AimFoe(c, aim, Plugin.DrakeLightningRange.Value + 5f, AimHelpDegrees, AimHelpNear);
            if ((el == Frost || el == Fire) && s != null) return AimFoe(c, aim, Reach(s, el), AssistDegrees, AssistNear);
            return null;
        }

        // how far a shot flies before it is gone
        private static float Reach(Shot s, int el)
        {
            Projectile p = s.Prefab != null ? s.Prefab.GetComponent<Projectile>() : null;
            float ttl = p != null && p.m_ttl > 0f ? p.m_ttl : 4f;
            return Mathf.Clamp(ShotSpeed(s.Attack, el) * ttl, 20f, 150f);
        }

        // where the enemy will be when a shot of this speed gets there
        private static Vector3 Lead(Vector3 from, Character foe, float speed)
        {
            Vector3 p = foe.GetCenterPoint();
            Vector3 v = foe.GetVelocity();
            for (int i = 0; i < 2; i++)
            {
                float t = Vector3.Distance(from, p) / Mathf.Max(5f, speed);
                p = foe.GetCenterPoint() + v * Mathf.Min(t, 2f);
            }
            return p;
        }

        // a ring at its feet and a small mark over its head
        private static void MarkFoe(Character foe)
        {
            Vector3 feet;
            if (!Ground(foe.transform.position, 1f, 4f, out feet)) feet = foe.transform.position + Vector3.up * Lift;
            Circle(feet, foe.GetRadius() + 0.35f, false);
            Transform cam = GameCamera.instance != null ? GameCamera.instance.transform : null;
            if (cam == null) return;
            float size = Mathf.Clamp(foe.GetRadius() * 0.5f, 0.25f, 0.8f);
            Vector3 top = foe.GetTopPoint() + Vector3.up * (0.45f + size + 0.08f * Mathf.Sin(Time.time * 4f));
            Vector3 right = cam.right;
            _pts.Clear();
            _pts.Add(top + (Vector3.up - right) * size);
            _pts.Add(top);
            _pts.Add(top + (Vector3.up + right) * size);
            Paint(_pts, false, false, false, 0.07f);
        }

        // ------------------------------------------------------------ lightning

        private static float _dmgR = -1f, _pushR;

        private static void DrawLightning(Character c, Vector3 aim, Shot s)
        {
            // the same choice as the attack itself (Spawn and Strike)
            Vector3 from = c.GetHeadPoint();
            float reach = Plugin.DrakeLightningRange.Value;
            Character foe = FoeFor(c, aim, Lightning, s);
            Vector3 at = aim;
            if (foe != null) at = Vector3.Distance(from, foe.transform.position) <= reach ? foe.transform.position : foe.GetCenterPoint();
            if (Vector3.Distance(from, at) > reach)
            {
                at = from + (at - from).normalized * reach;
                if (ZoneSystem.instance != null) at.y = Mathf.Max(ZoneSystem.instance.GetGroundHeight(at), ZoneSystem.instance.m_waterLevel);
            }
            float dmg, push;
            Areas(s, out dmg, out push);
            Vector3 center;
            if (!Ground(at, 2f, 30f, out center)) center = at + Vector3.up * Lift;
            Circle(center, dmg, false);
            if (push > 0f && Mathf.Abs(push - dmg) > 0.3f) Circle(center, push, true);
            if (foe != null) MarkFoe(foe);
        }

        // the radius of what hurts and of what pushes, from the areas the lightning makes (written to the log once)
        private static void Areas(Shot s, out float dmg, out float push)
        {
            if (_dmgR < 0f)
            {
                _dmgR = 0f;
                _pushR = 0f;
                List<string> log = new List<string>();
                GameObject area = Prefab("lightningAOE") ?? Strike();
                if (area != null && area != s.Prefab) AddAreas(area, true, 0f, log);
                if (s.Prefab != null) AddAreas(s.Prefab, false, s.Item != null ? s.Item.m_shared.m_attackForce : 0f, log);
                if (_dmgR <= 0f) _dmgR = 4f;
                Plugin.Debug("[preview] lightning areas: " + string.Join("; ", log.ToArray()) + " -> damage " + _dmgR.ToString("0.0") + " m, push " + _pushR.ToString("0.0") + " m");
            }
            dmg = _dmgR;
            push = _pushR;
        }

        private static void AddAreas(GameObject g, bool strike, float hitPush, List<string> log)
        {
            foreach (Aoe a in g.GetComponentsInChildren<Aoe>(true))
            {
                if (a == null) continue;
                // Strike() makes the root area of the lightning use the attack's own settings (with no push)
                bool attackSettings = a.m_useAttackSettings || (strike && a.gameObject == g);
                float force = attackSettings ? (strike ? 0f : hitPush) : a.m_attackForce;
                bool pushes = force > 0f || a.m_knockBackForce > 0f || a.m_launchForceMinMax.y > 0f;
                bool hurts = attackSettings || a.m_damage.GetTotalDamage() > 0f;
                if (hurts) _dmgR = Mathf.Max(_dmgR, a.m_radius);
                if (pushes) _pushR = Mathf.Max(_pushR, a.m_radius);
                log.Add(g.name + "/" + a.name + " r " + a.m_radius.ToString("0.0") + (hurts ? " damage" : "")
                    + (pushes ? " push (force " + force.ToString("0") + ", knockback " + a.m_knockBackForce.ToString("0") + ", launch " + a.m_launchForceMinMax.y.ToString("0") + ")" : ""));
            }
            Projectile p = g.GetComponent<Projectile>();
            if (p != null && p.m_aoe > 0f)
            {
                _dmgR = Mathf.Max(_dmgR, p.m_aoe);
                if (hitPush > 0f) _pushR = Mathf.Max(_pushR, p.m_aoe);
                log.Add(g.name + " projectile area r " + p.m_aoe.ToString("0.0"));
            }
        }

        // ------------------------------------------------------------ stone

        private static float _rockWidth = -1f;

        // the uncharged rock's width (its largest mesh, like the chips in Rubble.cs)
        private static float RockWidth(Shot s)
        {
            if (_rockWidth > 0f) return _rockWidth;
            float w = 0f;
            foreach (MeshFilter mf in s.Prefab.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf == null || mf.sharedMesh == null || mf.GetComponent<MeshRenderer>() == null) continue;
                Vector3 size = Vector3.Scale(mf.sharedMesh.bounds.size, mf.transform.lossyScale);
                w = Mathf.Max(w, Mathf.Max(size.x, size.z));
            }
            Projectile p = s.Prefab.GetComponent<Projectile>();
            if (p != null) w = Mathf.Max(w, p.m_rayRadius * 2f);
            if (w < 0.1f) w = 1f;
            _rockWidth = Mathf.Min(w, 4f);
            Plugin.Debug("[preview] rock width " + _rockWidth.ToString("0.00") + " m (ray radius " + (p != null ? p.m_rayRadius.ToString("0.00") : "?") + ", ttl " + (p != null ? p.m_ttl.ToString("0.0") : "?") + ")");
            return _rockWidth;
        }

        private static void DrawRock(Character c, Vector3 aim, Shot s)
        {
            float k = ChargeNow();
            float speed = ShotSpeedAt(s.Attack, Earth, k), g = EarthGravityAt(s, k);
            Vector3 from = c.GetHeadPoint();
            Vector3 dir = Lob(from, aim, speed, g);
            float grow = 1f + k * 0.8f;   // as in Rock()
            float width = RockWidth(s) * grow;
            Projectile pf = s.Prefab.GetComponent<Projectile>();
            float ray = (pf != null ? pf.m_rayRadius : 0.3f) * grow;
            float ttl = pf != null && pf.m_ttl > 0f ? pf.m_ttl : 10f;
            Vector3 start = from + dir * 1.2f * Mathf.Max(1f, c.transform.localScale.x);
            RaycastHit hit;
            Vector3 end, vel;
            bool landed = Fly(start, dir * speed, g, ttl, ray, _path, out end, out vel, out hit);

            // the path under the rock, then where it rolls on
            Stripes(_path, width, false);
            if (!landed) return;
            Vector3 spot = hit.point + hit.normal * Lift;
            Ring(spot, hit.normal, width * 0.5f);
            Ring(spot, hit.normal, width * 0.5f * 0.45f);
            bool ground = hit.collider != null && hit.collider.GetComponentInParent<Heightmap>() != null;
            Vector3 flat = new Vector3(vel.x, 0f, vel.z);
            if (Plugin.DrakeEarthRoll.Value && ground && flat.sqrMagnitude > 0.01f)
            {
                _path.Clear();
                flat.Normalize();
                for (float d = 0f; d <= RollDistance; d += 0.75f) _path.Add(hit.point + flat * d + Vector3.up * 0.5f);
                Stripes(_path, width, true);
            }
        }

        // the stone spike: where it comes out, how far it hits (outer circle) and the strong centre (inner circle);
        // both grow while it charges
        private static void DrawSpike(Character c, Vector3 aim)
        {
            float k = ChargeNow();
            Vector3 at, normal;
            if (!SpikeSpot(c, aim, out at, out normal)) return;
            Vector3 center = at + Vector3.up * Lift;
            Circle(center, SpikeArea(k), false);
            Circle(center, SpikeCore(k), false);
        }

        // two stripes on the ground under a path, 'width' apart
        private static void Stripes(List<Vector3> path, float width, bool dashed)
        {
            if (path.Count < 2) return;
            // the path laid on the ground, a point every 0.6 m
            List<Vector3> onGround = new List<Vector3>();
            Vector3 last = path[0];
            float left = 0f;
            for (int i = 0; i < path.Count; i++)
            {
                Vector3 p = path[i];
                Vector3 step = p - last;
                step.y = 0f;
                left += step.magnitude;
                if (i == 0 || left >= 0.6f || i == path.Count - 1)
                {
                    left = 0f;
                    Vector3 gp;
                    if (Ground(p, 0.5f, 300f, out gp)) onGround.Add(gp);
                }
                last = p;
            }
            if (onGround.Count < 2) return;
            float half = Mathf.Max(0.15f, width * 0.5f);
            for (int side = -1; side <= 1; side += 2)
            {
                _pts.Clear();
                for (int i = 0; i < onGround.Count; i++)
                {
                    Vector3 a = onGround[Mathf.Max(0, i - 1)], b = onGround[Mathf.Min(onGround.Count - 1, i + 1)];
                    Vector3 d = b - a;
                    d.y = 0f;
                    if (d.sqrMagnitude < 0.0001f) continue;
                    Vector3 perp = Vector3.Cross(Vector3.up, d.normalized) * (half * side);
                    Vector3 q = onGround[i] + perp, gq;
                    _pts.Add(Ground(q, 2f, 4f, out gq) ? gq : q);
                }
                Paint(_pts, false, dashed, true, MarkWidth);
            }
        }

        // ------------------------------------------------------------ poison

        private static void DrawSpray(Character c, Vector3 aim, Shot s)
        {
            Vector3 from = c.GetHeadPoint();
            Vector3 dir = (aim - from).normalized;
            if (dir.sqrMagnitude < 0.01f) return;
            Quaternion look = Quaternion.LookRotation(dir);
            Vector3 start = from + dir * 1.2f * Mathf.Max(1f, c.transform.localScale.x);
            float speed = ShotSpeedAt(s.Attack, Poison, 0f);
            Projectile pf = s.Prefab.GetComponent<Projectile>();
            float g = pf != null ? pf.m_gravity : 0f;
            float ttl = (pf != null && pf.m_ttl > 0f ? pf.m_ttl : 4f) * PoisonReach;
            const int N = 5;
            _near.Clear();
            _far.Clear();
            RaycastHit hit;
            Vector3 end, vel;
            for (int i = 0; i <= N; i++)
            {
                float yaw = Mathf.Lerp(-PoisonSpread, PoisonSpread, i / (float)N);
                // the drops that leave lowest land nearest; the highest ones farthest
                if (!Fly(start, look * (Quaternion.Euler(PoisonSpread, yaw, 0f) * Vector3.forward) * speed, g, ttl, 0f, null, out end, out vel, out hit)) return;
                _near.Add(hit.point + hit.normal * Lift);
                Vector3 far;
                if (Fly(start, look * (Quaternion.Euler(-PoisonSpread, yaw, 0f) * Vector3.forward) * speed, g, ttl, 0f, null, out end, out vel, out hit)) far = hit.point + hit.normal * Lift;
                else if (!Ground(end, 0.5f, 300f, out far)) return;   // gone in the air: the edge is where it dies, over the ground
                _far.Add(far);
            }
            _pts.Clear();
            _pts.AddRange(_near);
            for (int i = _far.Count - 1; i >= 0; i--) _pts.Add(_far[i]);
            Paint(_pts, true, false, true, MarkWidth);
        }

        // a wider, longer spray, each drop hurting a little around where it lands (never buildings)
        private static void Wider(Projectile fly)
        {
            fly.m_ttl *= PoisonReach;
            fly.m_rayRadius = Mathf.Max(fly.m_rayRadius, 0.2f) * PoisonWide;
            fly.m_aoe = Mathf.Max(fly.m_aoe, PoisonArea);
            fly.m_aoeSkipWearNTear = true;
        }

        // ------------------------------------------------------------ the flight of a shot, as the game moves it

        private static bool Fly(Vector3 pos, Vector3 vel, float gravity, float ttl, float radius, List<Vector3> path,
            out Vector3 end, out Vector3 endVel, out RaycastHit hit)
        {
            hit = new RaycastHit();
            if (path != null) { path.Clear(); path.Add(pos); }
            const float dt = 0.05f;
            float water = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : -10000f;
            for (float t = 0f; t < ttl; t += dt)
            {
                vel += Vector3.down * (gravity * dt);
                Vector3 step = vel * dt;
                float len = step.magnitude;
                if (len > 0.0001f)
                {
                    bool got = radius > 0.01f
                        ? Physics.SphereCast(pos, radius, step / len, out hit, len, GroundMask(), QueryTriggerInteraction.Ignore)
                        : Physics.Raycast(pos, step / len, out hit, len, GroundMask(), QueryTriggerInteraction.Ignore);
                    if (got)
                    {
                        end = hit.point;
                        endVel = vel;
                        if (path != null) path.Add(hit.point);
                        return true;
                    }
                }
                pos += step;
                if (pos.y < water)
                {
                    hit.point = new Vector3(pos.x, water, pos.z);
                    hit.normal = Vector3.up;
                    end = hit.point;
                    endVel = vel;
                    if (path != null) path.Add(hit.point);
                    return true;
                }
                if (path != null) path.Add(pos);
            }
            end = pos;
            endVel = vel;
            return false;
        }

        // ------------------------------------------------------------ painting on the ground

        private static int GroundMask()
        {
            if (_groundMask == 0) _groundMask = LayerMask.GetMask("Default", "static_solid", "terrain", "piece", "vehicle");
            return _groundMask;
        }

        // the surface under p (from 'above' over it down to 'depth' under it), a little above it; water counts
        private static bool Ground(Vector3 p, float above, float depth, out Vector3 at)
        {
            RaycastHit h;
            float water = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : -10000f;
            if (Physics.Raycast(p + Vector3.up * above, Vector3.down, out h, above + depth, GroundMask(), QueryTriggerInteraction.Ignore))
            {
                at = h.point + h.normal * Lift;
                if (at.y < water) at.y = water + Lift;
                return true;
            }
            if (p.y - depth < water) { at = new Vector3(p.x, water + Lift, p.z); return true; }
            at = p;
            return false;
        }

        // a circle laid on whatever is under it
        private static void Circle(Vector3 center, float r, bool dashed)
        {
            if (r < 0.05f) return;
            int n = Mathf.Clamp(Mathf.RoundToInt(r * 10f), 24, 72);
            _pts.Clear();
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)n * Mathf.PI * 2f;
                Vector3 q = center + new Vector3(Mathf.Cos(t), 0f, Mathf.Sin(t)) * r, g;
                _pts.Add(Ground(q, 3f, 6f, out g) ? g : new Vector3(q.x, center.y, q.z));
            }
            Paint(_pts, true, dashed, true, MarkWidth);
        }

        // a flat circle on a surface (where the rock lands, maybe on a wall)
        private static void Ring(Vector3 center, Vector3 normal, float r)
        {
            if (normal.y > 0.7f) { Circle(center, r, false); return; }
            Vector3 a = Vector3.Cross(normal, Vector3.up).normalized;
            if (a.sqrMagnitude < 0.01f) a = Vector3.right;
            Vector3 b = Vector3.Cross(normal, a);
            _pts.Clear();
            for (int i = 0; i < 32; i++)
            {
                float t = i / 32f * Mathf.PI * 2f;
                _pts.Add(center + (a * Mathf.Cos(t) + b * Mathf.Sin(t)) * r);
            }
            Paint(_pts, true, false, false, MarkWidth);
        }

        private static LineRenderer NextMark()
        {
            if (_markRoot == null)
            {
                GameObject root = new GameObject("Beastkeeper_AttackPreview");
                Object.DontDestroyOnLoad(root);
                _markRoot = root.transform;
                _marks.Clear();
            }
            if (_markUsed < _marks.Count && _marks[_markUsed] != null) return _marks[_markUsed++];
            GameObject go = new GameObject("mark");
            go.transform.SetParent(_markRoot, false);
            LineRenderer l = go.AddComponent<LineRenderer>();
            l.useWorldSpace = true;
            l.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            l.receiveShadows = false;
            l.numCapVertices = 0;
            l.numCornerVertices = 2;
            l.textureMode = LineTextureMode.Tile;
            if (_markUsed < _marks.Count) _marks[_markUsed] = l; else _marks.Add(l);
            _markUsed++;
            return l;
        }

        // flat = a ribbon lying on the ground (it faces up), else a line facing the camera
        private static void Paint(List<Vector3> pts, bool loop, bool dashed, bool flat, float width)
        {
            if (pts.Count < 2) return;
            if (_solid == null) { _solid = NewMat(false, false); _dashed = NewMat(true, false); _haloSolid = NewMat(false, true); _haloDashed = NewMat(true, true); }
            Line(pts, loop, dashed ? _haloDashed : _haloSolid, dashed, flat, width * HaloWidth, Halo);
            Line(pts, loop, dashed ? _dashed : _solid, dashed, flat, width, Red);
        }

        private static void Line(List<Vector3> pts, bool loop, Material mat, bool dashed, bool flat, float width, Color color)
        {
            LineRenderer l = NextMark();
            l.sharedMaterial = mat;
            l.alignment = flat ? LineAlignment.TransformZ : LineAlignment.View;
            l.transform.rotation = Quaternion.LookRotation(Vector3.up, Vector3.forward);
            l.textureScale = new Vector2(dashed ? 0.12f : 1f, 1f);
            l.startWidth = l.endWidth = width;
            l.startColor = l.endColor = color;
            l.loop = loop;
            l.positionCount = pts.Count;
            for (int i = 0; i < pts.Count; i++) l.SetPosition(i, pts[i]);
            l.enabled = true;
        }

        // halo: fades out across the line's width; the tint above 1 lets the game's bloom give the marks a light glow
        private static Material NewMat(bool dashed, bool halo)
        {
            Material m = new Material(Shader.Find("Sprites/Default"));
            if (dashed || halo)
            {
                int w = dashed ? 4 : 1, h = halo ? 16 : 1;
                Texture2D t = new Texture2D(w, h, TextureFormat.RGBA32, false);
                for (int y = 0; y < h; y++)
                {
                    float across = h > 1 ? Mathf.Abs(y / (float)(h - 1) * 2f - 1f) : 0f;   // 0 in the middle, 1 at the edges
                    float a = halo ? Mathf.Pow(1f - across, 2f) : 1f;
                    for (int x = 0; x < w; x++)
                        t.SetPixel(x, y, dashed && x >= 2 ? Color.clear : new Color(1f, 1f, 1f, a));
                }
                t.filterMode = dashed ? FilterMode.Point : FilterMode.Bilinear;
                t.wrapMode = TextureWrapMode.Repeat;
                t.Apply();
                m.mainTexture = t;
            }
            m.color = halo ? new Color(1.6f, 1.6f, 1.6f, 1f) : new Color(2.2f, 2.2f, 2.2f, 1f);
            m.renderQueue = halo ? 3099 : 3100;
            return m;
        }
    }
}
