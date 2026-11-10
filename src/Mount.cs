using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Beastkeeper
{
    // Riding any tamed animal big enough to carry you (boar, wolf, deer, bear, neck...).
    // The saddle is held in both hands: look at your tamed animal and press attack to put it on (attack again
    // with a saddle in hand takes it off and gives it back). A saddled animal: E rides it, Shift+E is the
    // game's usual interaction. Riding uses the game's own "controlling something" system, the one the helm
    // and the lox saddle use: you sit on its back, W/A/S/D move it the way the camera looks, Run gallops,
    // Jump makes the animal jump; crouch (or attack, dodge) gets you off.
    // Your game takes the animal over while you ride and moves it, so everyone sees you ride; the seat,
    // saddle and controls exist only for players with the mod.
    [HarmonyPatch]
    internal class Mount : MonoBehaviour, IDoodadController
    {
        internal static readonly ToolSet Set = new ToolSet();
        internal static readonly int SaddleKey = "Beastkeeper_saddle".GetStableHashCode();
        internal static readonly int RiderKey = "Beastkeeper_rider".GetStableHashCode();
        private static readonly FieldInfo AIField = AccessTools.Field(typeof(Character), "m_baseAI");
        private static readonly FieldInfo AIChar = AccessTools.Field(typeof(BaseAI), "m_character");

        internal static Mount Riding;           // the animal you ride, on your game
        private static bool _wasAttack, _muteAttack;

        private Character _c;
        private ZNetView _nv;
        private Transform _seat;
        internal Transform Seat { get { return _seat; } }
        private Vector3 _move, _look;
        private bool _run;
        private bool _autoRun, _autoPrev;       // autorun is a toggle: the game only tells us the key press
        private float _seaSpeed;                // a serpent's real speed, metres per second (negative = backing)
        private Vector3 _heading;               // last direction you steered, kept while it slows down
        private float _speed;                   // 0..1, eased

        internal static bool IsRidden(Character c) { return Riding != null && Riding._c == c; }
        internal static Character RiddenCharacter() { return Riding != null ? Riding._c : null; }

        internal static bool IsSaddle(ItemDrop.ItemData item) { return Set.Is(item); }

        private static readonly string[] Leather = { "", "deer hide", "troll hide", "wolf hide", "lox hide and linen", "carapace plates and red jute" };

        internal static void Init()
        {
            for (int t = 1; t <= Tiers.Max; t++)
            {
                int tier = t;
                Set.ByTier[t] = Items.Add(new CustomItem
                {
                    PrefabName = t == 1 ? "Beastkeeper_Saddle" : "Beastkeeper_Saddle" + t,
                    Sources = new[] { "Club", "Torch" },
                    Recipe = Recipes.Saddle[t],
                    Setup = (p, src) =>
                    {
                        ItemDrop.ItemData.SharedData sh = p.GetComponent<ItemDrop>().m_itemData.m_shared;
                        sh.m_name = Tiers.Line[tier] + " Saddle";
                        sh.m_description = "A saddle of " + Leather[tier] + ". Fits tamed " + Lasso.Catches(tier) + " big enough to carry you: "
                            + "look at the animal and press the saddle's hotbar key. Then E rides it. Shift + hold E takes the saddle off.";
                        sh.m_icons = new[] { Items.Tinted(Icon(), Recipes.Tint[tier], "Beastkeeper_SaddleIcon" + tier) };
                        sh.m_itemType = ItemDrop.ItemData.ItemType.TwoHandedWeapon;   // both hand slots, one-handed pose
                        sh.m_animationState = ItemDrop.ItemData.AnimationState.OneHanded;
                        sh.m_damages = new HitData.DamageTypes();
                        sh.m_damagesPerLevel = new HitData.DamageTypes();
                        sh.m_attackForce = 0f;
                        sh.m_blockPower = 0f;
                        sh.m_useDurability = false;
                        sh.m_maxQuality = 1;
                        sh.m_maxStackSize = 1;
                        sh.m_weight = 4f;
                        sh.m_teleportable = true;
                        foreach (Renderer r in p.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
                        foreach (MeleeWeaponTrail tr in p.GetComponentsInChildren<MeleeWeaponTrail>(true)) Object.DestroyImmediate(tr);
                        // a saddle you can see: on the ground and in your hand
                        Transform attach = p.transform.Find("attach");
                        GameObject look = new GameObject("Beastkeeper_SaddleItem");
                        look.transform.SetParent(attach != null ? attach : p.transform, false);
                        look.AddComponent<SaddleItemLook>().Tier = tier;
                    },
                });
            }
        }

        private void Awake()
        {
            _c = GetComponent<Character>();
            _nv = GetComponent<ZNetView>();
        }

        // ---------------------------------------------------------------- the saddle you can see

        private GameObject _look3d;
        private int _lookTier;
        private float _nextLook;
        private int _fitVersion;

        private Vector3 _saddleLocalPos, _seatLocalPos, _avgPos;
        private Quaternion _saddleLocalRot = Quaternion.identity, _avgRot = Quaternion.identity;
        private bool _avgSet, _wasOnGround = true;
        private float _nextHopLog;
        private Transform _trunk;

        private void LateUpdate()
        {
            if (Riding == this && _seat != null && Sea.IsSeaMount(_c)) { Sea.Ridden(_c, _seat); return; }
            bool seat = Riding == this && _seat != null;
            if (_look3d == null && !seat) { _avgSet = false; return; }
            // The saddle and your seat are children of the animal's back bone (like the other riding mods do), so
            // the game moves them with the body. Only your seat's facing is kept level with the animal.
            if (seat && !Sea.IsSeaMount(_c))
            {
                _seat.rotation = transform.rotation;
                LeanRider();
            }
        }

        private Transform _leanBone, _legLeft, _legRight;
        private Player _leanOwner;

        // bends your back and swings your legs by this animal's numbers (after the game has posed you this frame),
        // the same way the saddle editor page shows it
        private void LeanRider()
        {
            Player p = Player.m_localPlayer;
            if (p == null || Exporter.Busy) return;
            float lean = SeatNumber(3, -30f, 60f), legs = SeatNumber(5, -45f, 60f), spread = SeatNumber(6, -10f, 50f);
            if (Mathf.Abs(lean) < 0.01f && Mathf.Abs(legs) < 0.01f && Mathf.Abs(spread) < 0.01f) return;
            if (_leanOwner != p || _leanBone == null)
            {
                _leanOwner = p;
                _leanBone = _legLeft = _legRight = null;
                Transform a = null, b = null;
                foreach (Transform t in p.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name == "Spine" && _leanBone == null) _leanBone = t;
                    else if (t.name.EndsWith("UpLeg", System.StringComparison.OrdinalIgnoreCase)) { if (a == null) a = t; else if (b == null) b = t; }
                }
                // left = the thigh on your left side
                if (a != null && b != null && p.transform.InverseTransformPoint(a.position).x > p.transform.InverseTransformPoint(b.position).x) { Transform x = a; a = b; b = x; }
                _legLeft = a; _legRight = b;
                Plugin.Debug("[ride] rider bones: spine " + (_leanBone != null) + ", thighs " + (a != null ? a.name : "-") + " / " + (b != null ? b.name : "-"));
                if (_leanBone == null) return;
            }
            // The player's animation doesn't run every frame (it follows the physics steps). On a frame it skipped,
            // the bones still hold last frame's bend: bending them again would stack up and make you shake.
            Vector3 right = p.transform.right, forward = p.transform.forward;
            if (Mathf.Abs(lean) >= 0.01f && Fresh(_leanBone, _leanSet))
            {
                _leanBone.rotation = Quaternion.AngleAxis(lean, right) * _leanBone.rotation;
                _leanSet = _leanBone.localRotation;
            }
            if (Mathf.Abs(legs) < 0.01f && Mathf.Abs(spread) < 0.01f) return;
            // each thigh: out to its own side, then the feet forward
            Quaternion fwd = Quaternion.AngleAxis(-legs, right);
            if (_legLeft != null && Fresh(_legLeft, _leftSet))
            {
                _legLeft.rotation = fwd * Quaternion.AngleAxis(-spread, forward) * _legLeft.rotation;
                _leftSet = _legLeft.localRotation;
            }
            if (_legRight != null && Fresh(_legRight, _rightSet))
            {
                _legRight.rotation = fwd * Quaternion.AngleAxis(spread, forward) * _legRight.rotation;
                _rightSet = _legRight.localRotation;
            }
        }

        private Quaternion _leanSet, _leftSet, _rightSet;

        // true when the animation posed this bone again since we last bent it
        private static bool Fresh(Transform bone, Quaternion lastSet)
        {
            return Quaternion.Angle(bone.localRotation, lastSet) > 0.01f;
        }

        // one of this animal's rider numbers in SeatHeights (4th = lean, 6th = legs forward, 7th = legs open), degrees
        private float SeatNumber(int index, float min, float max)
        {
            float v;
            return SeatValue(index, out v) ? Mathf.Clamp(v, min, max) : 0f;
        }

        internal const string DefaultPose = "attach_lox";   // the game's lox riding pose
        private const float SaddleSize = 1.1f;   // the saddle's width compared to the animal's back

        // the game's riding pose for this animal (5th SeatHeights value), else the lox one
        internal string Pose()
        {
            if (Sea.IsSeaMount(_c)) return DefaultPose;
            string pose = SeatText(4);
            return pose.StartsWith("attach_") ? pose : DefaultPose;
        }

        private string SeatText(int index)
        {
            string prefab = Kind(_c.gameObject);
            foreach (string part in (Plugin.SeatHeights.Value ?? "").Split(','))
            {
                string[] kv = part.Split(':');
                if (kv.Length > index && string.Equals(kv[0].Trim(), prefab, System.StringComparison.OrdinalIgnoreCase))
                    return kv[index].Trim();
            }
            return "";
        }

        // the back bone things ride on: Spine1 (what the boar, wolf and deer models call it), else the trunk
        private Transform Back()
        {
            if (_trunk != null) return _trunk;
            foreach (Transform t in GetComponentsInChildren<Transform>(true))
                if (t.name == "Spine1") { _trunk = t; break; }
            if (_trunk == null) _trunk = SpineNear(Vector3.zero);
            if (_trunk != null) Plugin.Debug("[ride] " + _c.name + ": saddle rides on bone " + _trunk.name);
            return _trunk;
        }

        // where the back bone sits in the animal's own frame when it stands as built (taken from the prefab, so it
        // is the same every time, whatever the animal is doing now)
        private Matrix4x4 BackRest(Transform bone)
        {
            List<Transform> chain = new List<Transform>();
            for (Transform u = bone; u != null && u != transform; u = u.parent) chain.Add(u);
            GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(Utils.GetPrefabName(gameObject)) : null;
            Transform p = prefab != null ? prefab.transform : null;
            for (int i = chain.Count - 1; i >= 0 && p != null; i--) p = p.Find(chain[i].name);
            Matrix4x4 m = Matrix4x4.identity;
            if (p != null)
            {
                for (Transform u = p; u != null && u != prefab.transform; u = u.parent)
                    m = Matrix4x4.TRS(u.localPosition, u.localRotation, u.localScale) * m;
                return m;
            }
            return transform.worldToLocalMatrix * bone.localToWorldMatrix;   // no prefab: as it is now
        }

        // hang t on the back bone so that, with the animal standing as built, it sits at pos/rot (animal's frame)
        private void OnBack(Transform t, Vector3 pos, Quaternion rot, float scale)
        {
            Transform bone = Back();
            if (bone == null)
            {
                t.SetParent(transform, false);
                t.localPosition = pos; t.localRotation = rot; t.localScale = Vector3.one * scale;
                return;
            }
            Matrix4x4 inv = BackRest(bone).inverse;
            float bs = Mathf.Max(0.0001f, BackRest(bone).lossyScale.x);
            t.SetParent(bone, false);
            t.localPosition = inv.MultiplyPoint3x4(pos);
            t.localRotation = inv.rotation * rot;
            t.localScale = Vector3.one * (scale / bs);
        }

        private bool HasFit()
        {
            string prefab = Utils.GetPrefabName(_c.gameObject);
            foreach (string entry in (Plugin.SaddleFit.Value ?? "").Split(','))
                if (string.Equals(entry.Split(':')[0].Trim(), prefab, System.StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private void Update()
        {
            if (Time.time < _nextLook) return;
            _nextLook = Time.time + 0.5f;
            if (_fitVersion != Plugin.FitVersion)
            {
                // a seat or saddle setting changed: redo the saddle and your seat now
                _fitVersion = Plugin.FitVersion;
                if (_look3d != null) { Destroy(_look3d); _look3d = null; }
                if (_seat != null && !Sea.IsSeaMount(_c)) PlaceSeat();
            }
            bool want = Plugin.Enabled.Value && _c != null && _nv != null && _nv.IsValid() && Saddled(_c) && !Sea.IsSeaMount(_c);
            if (want && _look3d != null && _lookTier != SaddleTier(_c)) { Destroy(_look3d); _look3d = null; }
            if (want && _look3d == null) { _lookTier = SaddleTier(_c); _look3d = MakeSaddle(); }
            else if (!want && _look3d != null) { Destroy(_look3d); _look3d = null; }
        }

        private void OnDestroy() { if (_look3d != null) Destroy(_look3d); }

        // the game's own saddle model (asksvin's, else the lox's), sized to this animal's back
        private GameObject MakeSaddle()
        {
            if (ObjectDB.instance == null) return null;
            GameObject holder = new GameObject("Beastkeeper_SaddleLook");
            holder.transform.SetParent(transform, false);
            string from = null;
            Collider body0 = _c.GetComponent<Collider>();
            if (body0 == null) body0 = _c.GetComponentInChildren<Collider>();
            const bool leather = true;
            if (leather)
            {
                // our own leather saddle, shaped to this animal's back
                float half = 0.5f * BodyWidth(body0) / Mathf.Max(0.01f, transform.lossyScale.x) * SaddleSize * 0.4f;
                float[] shape = Fit();
                half *= Mathf.Clamp(shape[8], 0.2f, 3f);
                GameObject model = new GameObject("model");
                model.transform.SetParent(holder.transform, false);
                model.AddComponent<MeshFilter>().sharedMesh = SaddleMesh.Build(half, shape[9], shape[10]);
                model.AddComponent<MeshRenderer>().sharedMaterial = SaddleMesh.Material(SaddleTier(_c));
                from = "leather (back half width " + half.ToString("0.00") + ")";
            }
            // best: the saddle the game puts on its own mounts, in the pose it sits there (upright, facing ahead)
            foreach (string mountName in leather ? new string[0] : new[] { "Asksvin", "Lox" })
            {
                GameObject mp = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(mountName) : null;
                Tameable mt = mp != null ? mp.GetComponent<Tameable>() : null;
                if (mt == null || mt.m_saddle == null) continue;
                Transform st = mt.m_saddle.transform;
                // copy it where nothing wakes up (its own scripts would hook into this animal), strip it, then show it
                GameObject tmp = new GameObject("Beastkeeper_SaddleTmp");
                tmp.SetActive(false);
                GameObject g = Instantiate(st.gameObject, tmp.transform, false);
                foreach (MonoBehaviour mb in g.GetComponentsInChildren<MonoBehaviour>(true)) DestroyImmediate(mb);
                foreach (Collider col in g.GetComponentsInChildren<Collider>(true)) DestroyImmediate(col);
                foreach (Rigidbody rb in g.GetComponentsInChildren<Rigidbody>(true)) DestroyImmediate(rb);
                g.transform.SetParent(holder.transform, false);
                Destroy(tmp);
                g.transform.localPosition = Vector3.zero;
                g.transform.localRotation = Quaternion.Inverse(mp.transform.rotation) * st.rotation;
                g.transform.localScale = st.lossyScale;   // keep its own scale: its model is built for it
                foreach (Transform t in g.GetComponentsInChildren<Transform>(true)) t.gameObject.SetActive(true);
                // its saddle is skinned to that animal's skeleton, which our copy doesn't have: make it a plain model
                foreach (SkinnedMeshRenderer sk in g.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (sk.sharedMesh == null) { DestroyImmediate(sk); continue; }
                    GameObject host = sk.gameObject;
                    Mesh mesh = sk.sharedMesh;
                    Material[] mats = sk.sharedMaterials;
                    DestroyImmediate(sk);
                    host.AddComponent<MeshFilter>().sharedMesh = mesh;
                    host.AddComponent<MeshRenderer>().sharedMaterials = mats;
                }
                if (g.GetComponentsInChildren<Renderer>(true).Length == 0) { DestroyImmediate(g); continue; }   // no model on it
                from = mountName + " " + st.name;
                break;
            }
            if (from == null)
            {
                GameObject src = ObjectDB.instance.GetItemPrefab("SaddleAsksvin");
                if (src == null) src = ObjectDB.instance.GetItemPrefab("SaddleLox");
                if (src == null) { Destroy(holder); return null; }
                foreach (Transform child in src.transform)
                {
                    if (child.GetComponentInChildren<MeshRenderer>(true) == null && child.GetComponentInChildren<SkinnedMeshRenderer>(true) == null) continue;
                    GameObject g = Instantiate(child.gameObject, holder.transform, false);
                    g.SetActive(true);
                }
                from = src.name;
            }
            foreach (Collider col in holder.GetComponentsInChildren<Collider>(true)) Destroy(col);
            foreach (MonoBehaviour b in holder.GetComponentsInChildren<MonoBehaviour>(true)) Destroy(b);
            foreach (Rigidbody rb in holder.GetComponentsInChildren<Rigidbody>(true)) Destroy(rb);
            Renderer[] rs = holder.GetComponentsInChildren<Renderer>(true);
            foreach (Renderer r in rs) r.enabled = true;
            if (rs.Length == 0) { Destroy(holder); return null; }

            // Everything from here on is worked out in the animal's own frame (its root: right, up, forward), from
            // the models' own data. Nothing is measured in world axes, so the result doesn't depend on which way the
            // animal faces or what it's doing when the saddle is made: the same settings always give the same saddle.
            Transform hold = holder.transform;
            hold.localPosition = Vector3.zero;
            hold.localScale = Vector3.one;
            float[] fit = Fit();
            if (!leather) CutFront(holder, fit[4]);
            // size: as wide as the animal's back (local x), times SaddleSize and its own size setting
            Bounds lb = LocalBounds(holder);
            Collider body = _c.GetComponent<Collider>();
            if (body == null) body = _c.GetComponentInChildren<Collider>();
            float width = BodyWidth(body) / Mathf.Max(0.01f, transform.lossyScale.x);
            float k = leather ? Mathf.Clamp(fit[0], 0.05f, 5f)
                : Mathf.Clamp(width * SaddleSize * fit[0] / Mathf.Max(0.01f, lb.size.x), 0.05f, 5f);
            hold.localScale = Vector3.one * k;
            lb = LocalBounds(holder);
            // place: the middle of its top right where you sit, then your offsets (side, up, forward)
            Vector3 seatAt = new Vector3(0f, SeatHeight(), SeatAhead());
            Vector3 shift = new Vector3(seatAt.x - lb.center.x, seatAt.y - lb.max.y + 0.05f, seatAt.z - lb.center.z)
                          + new Vector3(fit[7], fit[1], fit[2]);
            hold.localPosition += shift;
            // turn: around its own middle, on the animal's axes (turn around up, tilt around right, roll around forward)
            Vector3 pivot = lb.center + shift;
            Quaternion turn = Quaternion.AngleAxis(fit[5], Vector3.up) * Quaternion.AngleAxis(-fit[3], Vector3.right) * Quaternion.AngleAxis(-fit[6], Vector3.forward);
            hold.localRotation = turn * hold.localRotation;
            hold.localPosition = pivot + turn * (hold.localPosition - pivot);
            // it stays on the body; LateUpdate adds the trunk's swing as the animal moves
            _saddleLocalPos = holder.transform.localPosition;
            _saddleLocalRot = holder.transform.localRotation;
            OnBack(hold, _saddleLocalPos, _saddleLocalRot, k);
            Plugin.Debug("[ride] saddle shown on " + _c.name + " from " + from + ", scale " + k.ToString("0.00"));
            return holder;
        }

        // this animal's SaddleFit entry: size, up, forward, tilt (1, 0, 0, 0 when it has none)
        // the name the saddle and seat settings go by: variants that share a model share the settings
        // (a white deer is a deer, a sleeping bear or a summoned one is a bear)
        internal static string Kind(GameObject g)
        {
            string n = Utils.GetPrefabName(g);
            if (n.EndsWith("_spiritcaller", System.StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - "_spiritcaller".Length);
            if (n.EndsWith("_sleeping", System.StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - "_sleeping".Length);
            if (string.Equals(n, "Deer_White", System.StringComparison.OrdinalIgnoreCase)) n = "Deer";
            return n;
        }

        // the saddle settings' name for this animal: the leather saddle keeps its own (Wolf-Leather), so switching
        // back to the asksvin saddle finds its old settings untouched
        internal static string FitKey(Character c)
        {
            string prefab = Kind(c.gameObject);
            return prefab + "-Leather";
        }

        private float[] Fit()
        {
            string prefab = Utils.GetPrefabName(_c.gameObject);
            // the saddle is the asksvin's: only it keeps the straps and reins up front, on anyone else they're cut off
            float front = string.Equals(prefab, "Asksvin", System.StringComparison.OrdinalIgnoreCase) ? 1f : 0f;
            float[] f = { 1f, 0f, 0f, 0f, front, 0f, 0f, 0f, 1f, 0.8f, 0f };   // ..., side width, side drop, side pinch (leather saddle shape)
            string key = FitKey(_c);
            foreach (string entry in (Plugin.SaddleFit.Value ?? "").Split(','))
            {
                string[] v = entry.Split(':');
                if (v.Length < 2 || !string.Equals(v[0].Trim(), key, System.StringComparison.OrdinalIgnoreCase)) continue;
                for (int i = 1; i < v.Length && i <= 11; i++)
                {
                    float x;
                    if (float.TryParse(v[i].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out x)) f[i - 1] = x;
                }
                if (f[0] <= 0.01f) f[0] = 1f;
            }
            return f;
        }

        // the box around the saddle's models, in the animal's frame (from the meshes' own bounds)
        private Bounds LocalBounds(GameObject holder)
        {
            bool any = false;
            Bounds b = new Bounds();
            foreach (MeshFilter mf in holder.GetComponentsInChildren<MeshFilter>(true))
            {
                Renderer r = mf.GetComponent<Renderer>();
                if (mf.sharedMesh == null || r == null || !r.enabled) continue;
                Bounds mb = mf.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 c = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    Vector3 p = transform.InverseTransformPoint(mf.transform.TransformPoint(c));
                    if (!any) { b = new Bounds(p, Vector3.zero); any = true; } else b.Encapsulate(p);
                }
            }
            return b;
        }

        // cut off what reaches too far forward (the straps and reins made for a longer animal): keep 'keep' of the
        // front half of the saddle, along the animal's forward axis. The game's model is locked, so a copy is cut.
        private void CutFront(GameObject holder, float keep)
        {
            if (keep >= 0.999f) return;
            Bounds lb = LocalBounds(holder);
            float cut = lb.center.z + lb.extents.z * Mathf.Clamp01(keep);
            int cutMeshes = 0, hidden = 0;
            foreach (MeshFilter mf in holder.GetComponentsInChildren<MeshFilter>(true))
            {
                Renderer r = mf.GetComponent<Renderer>();
                Mesh m = mf.sharedMesh;
                if (m == null || r == null) continue;
                if (!m.isReadable) m = ReadableCopy(m);
                if (m != null && m.isReadable)
                {
                    Vector3[] v = m.vertices;
                    bool[] ahead = new bool[v.Length];
                    for (int i = 0; i < v.Length; i++) ahead[i] = transform.InverseTransformPoint(mf.transform.TransformPoint(v[i])).z > cut;
                    Mesh copy = Instantiate(m);
                    for (int sub = 0; sub < m.subMeshCount; sub++)
                    {
                        int[] t = m.GetTriangles(sub);
                        List<int> keepT = new List<int>(t.Length);
                        for (int i = 0; i + 2 < t.Length; i += 3)
                            if (!ahead[t[i]] && !ahead[t[i + 1]] && !ahead[t[i + 2]]) { keepT.Add(t[i]); keepT.Add(t[i + 1]); keepT.Add(t[i + 2]); }
                        copy.SetTriangles(keepT, sub);
                    }
                    copy.RecalculateBounds();
                    mf.sharedMesh = copy;
                    cutMeshes++;
                }
                else if (transform.InverseTransformPoint(r.bounds.center).z > cut) { r.enabled = false; hidden++; }
            }
            Plugin.Debug("[ride] saddle front cut at " + keep.ToString("0.00") + ": " + cutMeshes + " meshes trimmed, " + hidden + " parts hidden");
        }

        private static readonly Dictionary<Mesh, Mesh> Copies = new Dictionary<Mesh, Mesh>();

        // a readable duplicate of a mesh the game keeps locked on the graphics card: its vertex and index data are
        // read back from there and put into a new mesh with the same layout
        private static Mesh ReadableCopy(Mesh m)
        {
            Mesh copy;
            if (Copies.TryGetValue(m, out copy) && copy != null) return copy;
            try
            {
                copy = new Mesh { name = m.name + " (Beastkeeper)" };
                copy.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                int n = m.vertexCount;
                copy.SetVertexBufferParams(n, m.GetVertexAttributes());
                for (int st = 0; st < m.vertexBufferCount; st++)
                {
                    using (GraphicsBuffer vb = m.GetVertexBuffer(st))
                    {
                        if (vb == null) continue;
                        byte[] data = new byte[vb.count * vb.stride];
                        vb.GetData(data);
                        copy.SetVertexBufferData(data, 0, 0, data.Length, st, UnityEngine.Rendering.MeshUpdateFlags.DontValidateIndices);
                    }
                }
                int[] all;
                using (GraphicsBuffer ib = m.GetIndexBuffer())
                {
                    bool small = m.indexFormat == UnityEngine.Rendering.IndexFormat.UInt16;
                    int count = ib.count;
                    all = new int[count];
                    if (small) { ushort[] u = new ushort[count]; ib.GetData(u); for (int i = 0; i < count; i++) all[i] = u[i]; }
                    else ib.GetData(all);
                }
                copy.subMeshCount = m.subMeshCount;
                for (int sub = 0; sub < m.subMeshCount; sub++)
                {
                    UnityEngine.Rendering.SubMeshDescriptor d = m.GetSubMesh(sub);
                    int[] t = new int[d.indexCount];
                    for (int i = 0; i < d.indexCount; i++) t[i] = all[d.indexStart + i] + d.baseVertex;
                    copy.SetTriangles(t, sub, false);
                }
                copy.RecalculateBounds();
                Copies[m] = copy;
                Plugin.Debug("[ride] copied the locked mesh " + m.name + " (" + n + " vertices, " + m.subMeshCount + " parts)");
                return copy;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Couldn't copy the saddle model " + m.name + ": " + e.Message);
                Copies[m] = null;
                return null;
            }
        }


        // While you ride, the camera is a floating thing of its own: it hangs behind a point above the animal, at your
        // zoom distance, looking where you look. That point glides after the animal (smoothly, never with the shaking of
        // its steps or yours). It only comes closer when a wall or the ground is in the way.
        private static float _camLog;
        private static readonly FieldInfo CamDistance = AccessTools.Field(typeof(GameCamera), "m_distance");

        [HarmonyPatch(typeof(GameCamera), "LateUpdate"), HarmonyPostfix]
        private static void FloatingCamera(GameCamera __instance)
        {
            Mount m = Riding;
            Player player = Player.m_localPlayer;
            if (m == null || player == null || !Plugin.Enabled.Value || m._c == null || Sea.IsSeaMount(m._c) || !player.IsAttached())
            { _camSet = false; _camWorldSet = false; return; }
            Vector3 pivot = Post(m);
            Transform cam = __instance.transform;
            Vector3 back = -cam.forward;
            float dist = CamDistance != null ? (float)CamDistance.GetValue(__instance) : 4f;
            dist = Mathf.Max(dist, 0.5f);
            RaycastHit hit;
            if (Physics.SphereCast(pivot, Mathf.Max(0.05f, __instance.m_raycastWidth), back, out hit, dist, __instance.m_blockCameraMask, QueryTriggerInteraction.Ignore))
                dist = Mathf.Max(0.3f, hit.distance - 0.1f);
            Vector3 floating = pivot + back * dist;
            cam.position = floating;
            // and level: no tilting to the side, whatever your character's head does
            // ...except a small, slow tilt that follows your head
            if (Mathf.Abs(cam.forward.y) < 0.98f)
            {
                Quaternion level = Quaternion.LookRotation(cam.forward, Vector3.up);
                float roll = Vector3.SignedAngle(level * Vector3.up, cam.up, cam.forward);
                float want = Mathf.Clamp(roll * Plugin.CameraTilt.Value, -15f, 15f);
                _camRoll = Mathf.Lerp(_camRoll, want, 1f - Mathf.Exp(-2f * Time.deltaTime));
                cam.rotation = Quaternion.AngleAxis(_camRoll, cam.forward) * level;
            }
            if (Time.time > _camLog)
            {
                _camLog = Time.time + 2f;
                Plugin.Debug("[camera] floating on " + m._c.name + ": distance " + dist.ToString("0.0") + ", height " + (pivot.y - m.transform.position.y).ToString("0.00"));
            }
        }

        // the point the camera hangs from: above the animal at a rider's eye height, gliding after it
        private static Vector3 Post(Mount m)
        {
            float dt = Mathf.Max(Time.deltaTime, 0.0001f);
            Vector3 foot = m.transform.position;
            Vector3 target = foot + Vector3.up * ((m.SeatHeight() + 1.0f) * m.transform.lossyScale.y);
            if (!_camWorldSet || (_camWorld - target).sqrMagnitude > 25f) { _camWorld = target; _camVel = Vector3.zero; _camWorldSet = true; }
            Vector3 flatVel = new Vector3(_camVel.x, 0f, _camVel.z);
            Vector3 flat = Vector3.SmoothDamp(new Vector3(_camWorld.x, 0f, _camWorld.z), new Vector3(target.x, 0f, target.z), ref flatVel, 0.1f, Mathf.Infinity, dt);
            float vy = _camVel.y;
            float cy = Mathf.SmoothDamp(_camWorld.y, target.y, ref vy, 0.45f, Mathf.Infinity, dt);
            _camWorld = new Vector3(flat.x, cy, flat.z);
            _camVel = new Vector3(flatVel.x, vy, flatVel.z);
            return _camWorld;
        }

        private static bool _camSet, _camWorldSet;
        private static Vector3 _camLocal, _camWorld, _camVel;
        private static float _camY, _camRoll;

        // your seat: on the trunk like the saddle, so you both move with the body (a serpent's is placed every frame)
        private void PlaceSeat()
        {
            if (_seat == null) return;
            if (Sea.IsSeaMount(_c))
            {
                _seat.SetParent(transform, false);
                _seat.localPosition = new Vector3(0f, SeatHeight(), SeatAhead());
                _seat.localRotation = Quaternion.identity;
                return;
            }
            _seatLocalPos = new Vector3(0f, SeatHeight(), SeatAhead());
            OnBack(_seat, _seatLocalPos, Quaternion.identity, 1f);
            _seat.rotation = transform.rotation;
        }

        // Put t on the trunk bone, at 'world' as it would be with the animal standing in its rest pose (from the
        // model's own bind pose). It then moves with the body, and lands in the same spot whatever the animal was
        // doing when it was placed, walking or standing.
        private Transform OnTrunk(Transform t, Matrix4x4 world)
        {
            Transform bone = SpineNear(Vector3.zero);
            if (bone == null)
            {
                t.SetParent(transform, true);
                t.position = world.GetColumn(3);
                t.rotation = world.rotation;
                return null;
            }
            Matrix4x4 rest = bone.localToWorldMatrix;
            try
            {
                SkinnedMeshRenderer sk = _c.GetComponentInChildren<SkinnedMeshRenderer>();
                int i = sk != null && sk.bones != null ? System.Array.IndexOf(sk.bones, bone) : -1;
                Matrix4x4[] bind = i >= 0 && sk.sharedMesh != null ? sk.sharedMesh.bindposes : null;
                if (bind != null && i < bind.Length) rest = sk.transform.localToWorldMatrix * bind[i].inverse;
            }
            catch (System.Exception e) { Plugin.Debug("[ride] no rest pose for " + bone.name + ": " + e.Message); }
            Matrix4x4 rel = rest.inverse * world;
            t.SetParent(bone, false);
            t.localPosition = rel.GetColumn(3);
            t.localRotation = rel.rotation;
            t.localScale = rel.lossyScale;
            return bone;
        }

        // how wide the animal's body is, from the shape of its collider (its world bounds aren't ready right after
        // it loads, which made the saddle come out too small)
        internal static float BodyWidth(Collider body)
        {
            if (body == null) return 0.6f;
            Vector3 sc = body.transform.lossyScale;
            float side = Mathf.Max(Mathf.Abs(sc.x), Mathf.Abs(sc.z));
            CapsuleCollider cap = body as CapsuleCollider;
            if (cap != null) return 2f * cap.radius * side;
            SphereCollider sph = body as SphereCollider;
            if (sph != null) return 2f * sph.radius * side;
            BoxCollider box = body as BoxCollider;
            if (box != null) return Mathf.Min(box.size.x * Mathf.Abs(sc.x), box.size.z * Mathf.Abs(sc.z));
            Vector3 b = body.bounds.size;
            return b.x > 0.05f && b.z > 0.05f ? Mathf.Min(b.x, b.z) : 0.6f;
        }

        // the trunk bone of the animal
        private Transform SpineNear(Vector3 at)
        {
            SkinnedMeshRenderer sk = _c.GetComponentInChildren<SkinnedMeshRenderer>();
            if (sk == null || sk.bones == null) return null;
            // the trunk: the hips/pelvis, else the spine bone nearest the root (the upper spine bones bend with the neck)
            Transform hips = null, spine = null;
            int sd = int.MaxValue;
            foreach (Transform t in sk.bones)
            {
                if (t == null) continue;
                string n = t.name.ToLowerInvariant();
                // only bones down the middle of the body (not a left or right one, like the moose's DEF-pelvis.L)
                if (n.EndsWith(".l") || n.EndsWith(".r") || n.EndsWith("_l") || n.EndsWith("_r") || n.Contains("left") || n.Contains("right")) continue;
                if (hips == null && (n.Contains("pelvis") || n.Contains("hip"))) hips = t;
                if (n.Contains("spine") || n.Contains("back"))
                {
                    int depth = 0;
                    for (Transform u = t; u != null && u != transform; u = u.parent) depth++;
                    if (depth < sd) { sd = depth; spine = t; }
                }
            }
            if (hips != null) return hips;
            if (spine != null) return spine;
            return sk.rootBone;
        }

        private static Bounds WorldBounds(Renderer[] rs)
        {
            Bounds b = rs[0].bounds;
            for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
            return b;
        }

        // ---------------------------------------------------------------- who can be ridden

        internal static bool Saddled(Character c) { return SaddleTier(c) > 0; }

        // which saddle it wears: 1-5, 0 = none (saddles from before the tiers were leather: 1)
        internal static int SaddleTier(Character c)
        {
            ZNetView nv = Leash.View(c);
            return nv != null && nv.IsValid() ? Mathf.Clamp(nv.GetZDO().GetInt(SaddleKey, 0), 0, Tiers.Max) : 0;
        }

        internal static long RiderOf(Character c)
        {
            ZNetView nv = Leash.View(c);
            return nv != null && nv.IsValid() ? nv.GetZDO().GetLong(RiderKey, 0L) : 0L;
        }

        private static float Height(Character c)
        {
            Collider col = c.GetComponent<Collider>();
            if (col == null) col = c.GetComponentInChildren<Collider>();
            return col != null ? col.bounds.max.y - c.transform.position.y : 0f;
        }

        internal static bool BigEnough(Character c) { return Height(c) >= Plugin.RideMinHeight.Value; }

        internal static bool Rideable(Character c)
        {
            string prefab = Utils.GetPrefabName(c.gameObject);
            foreach (string part in (Plugin.NotRideable.Value ?? "").Split(','))
                if (string.Equals(part.Trim(), prefab, System.StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        // how high the seat is on this animal: from the SeatHeights list, else near the top of its body
        private float SeatHeight()
        {
            float h;
            if (SeatValue(1, out h)) return h;
            return Mathf.Max(0.5f, Height(_c) * 0.92f);
        }

        // how far forward the seat is: the third number in SeatHeights (prefab:height:forward)
        private float SeatAhead()
        {
            float f;
            return SeatValue(2, out f) ? f : 0f;
        }

        private bool SeatValue(int index, out float value)
        {
            value = 0f;
            string prefab = Kind(_c.gameObject);
            foreach (string part in (Plugin.SeatHeights.Value ?? "").Split(','))
            {
                string[] kv = part.Split(':');
                if (kv.Length > index && string.Equals(kv[0].Trim(), prefab, System.StringComparison.OrdinalIgnoreCase)
                    && float.TryParse(kv[index].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value))
                    return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- getting on and off

        private void Ride(Player p)
        {
            if (Riding != null) return;
            if (!_nv.IsOwner()) _nv.ClaimOwnership();
            if (Leash.Holder(_nv) != 0L) Leash.Release(_c, true);   // off the lasso first
            Tie.Untie(_c);                                           // and off the post
            if (_seat == null)
            {
                GameObject s = new GameObject("Beastkeeper_Seat");
                s.transform.SetParent(transform, false);
                _seat = s.transform;
            }
            PlaceSeat();
            _nv.GetZDO().Set(RiderKey, p.GetPlayerID());
            _move = Vector3.zero;
            _heading = Sea.IsSeaMount(_c) ? Sea.Facing(_c) : transform.forward;
            _heading.y = 0f;
            _speed = 0f;
            _seaSpeed = 0f;
            _autoRun = false;
            Riding = this;
            if (Sea.IsSeaMount(_c)) Sea.OnRide(_c);
            p.StartDoodadControl(this);
            p.AttachStart(_seat, _c.gameObject, true, false, false, Pose(), Vector3.up * 0.5f, null);
            p.Message(MessageHud.MessageType.Center, "Riding " + _c.GetHoverName() + (Sea.IsSeaMount(_c) ? ". Crouch to get off." : ". E to get off."));
            Plugin.Debug("[ride] on " + _c.name + ", seat at " + _seat.localPosition.ToString("0.00"));
        }

        // the game calls this when you jump, attack or dodge, or the animal is gone
        public void OnUseStop(Player player)
        {
            if (Riding == this) Riding = null;
            if (_nv != null && _nv.IsValid() && _nv.IsOwner() && _nv.GetZDO().GetLong(RiderKey, 0L) == player.GetPlayerID())
                _nv.GetZDO().Set(RiderKey, 0L);
            if (player.IsAttached()) player.AttachStop();
            if (_c != null)
            {
                bool sea = Sea.IsSeaMount(_c);
                if (sea) Sea.OnGetOff(_c);
                if (!sea || !Sea.PlaceOnShip(player, _c)) player.transform.position = Beside();
                if (!sea) Land(player, player.transform.position);
                BaseAI ai = _c.GetBaseAI();
                if (ai != null) { ai.StopMoving(); Leash.Calm(ai); ai.SetPatrolPoint(); }
                Home.Set(_c, transform.position);   // it grazes here (or goes back to its totem)
            }
            Plugin.Debug("[ride] got off");
        }

        // Moving only the player's transform isn't enough: its physics body still sits on the saddle and pulls you
        // back up there. Move the body too, and do it again after the next physics step.
        private static void Land(Player p, Vector3 at)
        {
            Place(p, at);
            if (Plugin.Instance != null) Plugin.Instance.StartCoroutine(LandAgain(p, at));
        }

        private static System.Collections.IEnumerator LandAgain(Player p, Vector3 at)
        {
            yield return new WaitForFixedUpdate();
            if (p != null && !p.IsAttached() && Vector3.Distance(p.transform.position, at) > 0.3f) Place(p, at);
        }

        private static void Place(Player p, Vector3 at)
        {
            p.transform.position = at;
            Rigidbody rb = p.GetComponent<Rigidbody>();
            if (rb == null) return;
            rb.position = at;
            rb.velocity = Vector3.zero;
        }

        // where you land when you get off: on the ground next to the animal's side (left, else right, else behind, else in front),
        // clear of its body; on its back only when walls stand on all four sides
        private Vector3 Beside()
        {
            Collider body = _c.GetComponent<Collider>();
            if (body == null) body = _c.GetComponentInChildren<Collider>();
            float half = 0.5f * BodyWidth(body);
            int mask = LayerMask.GetMask("terrain", "Default", "static_solid", "piece");
            Vector3 root = transform.position;
            foreach (Vector3 dir in new[] { -transform.right, transform.right, -transform.forward, transform.forward })
            {
                Vector3 at = root + dir * (half + 0.8f);
                if (Physics.Linecast(root + Vector3.up, at + Vector3.up, mask, QueryTriggerInteraction.Ignore)) continue;   // a wall or a tree there
                RaycastHit hit;
                if (Physics.Raycast(at + Vector3.up * 3f, Vector3.down, out hit, 8f, mask, QueryTriggerInteraction.Ignore)) at.y = hit.point.y + 0.05f;
                else if (ZoneSystem.instance != null) at.y = ZoneSystem.instance.GetGroundHeight(at) + 0.05f;
                return at;
            }
            if (Sea.IsSeaMount(_c))   // the serpent keeps its own way (unchanged)
            {
                Vector3 fallback = root - transform.right * (half + 0.8f);
                fallback.y = root.y + 0.3f;
                return fallback;
            }
            // walls on all four sides: you stay up on its back (never through a wall)
            return _seat != null ? _seat.position + Vector3.up * 0.3f : root + Vector3.up * (Height(_c) + 0.3f);
        }

        public void ApplyControlls(Vector3 moveDir, Vector3 lookDir, bool run, bool autoRun, bool block)
        {
            if (autoRun && !_autoPrev) _autoRun = !_autoRun;
            _autoPrev = autoRun;
            if (moveDir.z < -0.1f) _autoRun = false;          // S cancels it
            if (_autoRun && moveDir.z < 0.1f) moveDir.z = 1f; // A/D still steer while it runs on
            // the game drops "run" when no movement key is held (it doesn't know about our autorun), so read the key itself
            if (_autoRun && !run) run = ZInput.GetButton("Run") || ZInput.GetButton("JoyRun");
            _move = moveDir;
            _look = lookDir;
            // autorun walks (or cruises, on a serpent); the run key makes it run, autorun or not
            _run = run;
        }

        public Component GetControlledComponent() { return _c; }
        public Vector3 GetPosition() { return _seat != null ? _seat.position : transform.position; }
        public bool IsValid() { return this != null && _c != null && !_c.IsDead() && _nv != null && _nv.IsValid() && Saddled(_c); }

        // ---------------------------------------------------------------- moving the animal

        internal static bool Drive(BaseAI ai, float dt)
        {
            Mount m = Riding;
            if (m == null || ai == null) return false;
            Character c = AIChar != null ? AIChar.GetValue(ai) as Character : null;
            if (c != m._c) return false;
            if (!m._nv.IsOwner()) m._nv.ClaimOwnership();
            Leash.Calm(ai);
            Vector3 fwd = m._look;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.01f) fwd = c.transform.forward;
            fwd.Normalize();
            // You drive the body directly: it goes exactly where W/A/S/D point (from the camera), never
            // where the animal happens to face, and stops quickly when you let go. The game's own AI
            // movement is not used at all, so no wandering, no random hops. Only the speed is eased.
            if (Sea.IsSeaMount(c))
            {
                // a serpent steers like a boat: A/D turn it, W swims ahead, S backs off slowly
                if (m._heading.sqrMagnitude < 0.01f) m._heading = Sea.Facing(c);
                m._heading = Quaternion.Euler(0f, m._move.x * Plugin.SerpentTurnSpeed.Value * dt, 0f) * m._heading.normalized;
                // W cruises at about a rowed ship's speed, W + run sprints up to full speed quickly,
                // S backs off at a fixed slow speed. Hunger slows the forward speeds, never the reverse.
                float hunger = Sea.SpeedFactor(c);
                float goal = m._move.z > 0.05f ? (m._run ? Plugin.SerpentSpeed.Value : Plugin.SerpentCruiseSpeed.Value) * hunger
                           : m._move.z < -0.05f ? -Plugin.SerpentReverseSpeed.Value : 0f;
                float accel = goal > m._seaSpeed && m._run && goal > 0f ? Plugin.SerpentSprintAccel.Value : 3f;
                m._seaSpeed = Mathf.MoveTowards(m._seaSpeed, goal, accel * dt);
                // like a boat it runs aground: it can't swim into shallow water or onto land, only turn or back off
                if (Mathf.Abs(m._seaSpeed) > 0.05f && Sea.Aground(c, m._heading * Mathf.Sign(m._seaSpeed))) m._seaSpeed = 0f;
                m._speed = Mathf.Clamp(m._seaSpeed / Mathf.Max(1f, Plugin.SerpentSpeed.Value), -1f, 1f);
                if (Mathf.Abs(m._seaSpeed) > 0.05f) { c.SetMoveDir(m._heading * Mathf.Sign(m._seaSpeed)); c.SetRun(true); }
                else { c.SetMoveDir(Vector3.zero); c.SetRun(false); }
                c.SetLookDir(m._heading, 0f);
                Sea.Steering(c, m._heading, Mathf.Abs(m._speed));
                Sea.Burn(c, dt);
                return true;
            }
            // (debug) the body leaving the ground on its own while ridden
            bool onGround = c.IsOnGround();
            if (m._wasOnGround && !onGround && Time.time > m._nextHopLog)
            {
                m._nextHopLog = Time.time + 1f;
                Plugin.Debug("[ride] " + c.name + " left the ground (speed " + c.GetVelocity().magnitude.ToString("0.0") + ", up " + c.GetVelocity().y.ToString("0.0") + ")");
            }
            m._wasOnGround = onGround;
            Vector3 want = Quaternion.LookRotation(fwd) * new Vector3(m._move.x, 0f, m._move.z);
            bool pressing = want.sqrMagnitude > 0.01f;
            if (pressing) m._heading = want.normalized;
            float rate = pressing ? Plugin.RideAcceleration.Value : Plugin.RideAcceleration.Value * 4f;
            m._speed = Mathf.MoveTowards(m._speed, pressing ? 1f : 0f, rate * dt);
            if (m._speed > 0.05f && m._heading.sqrMagnitude > 0.01f)
            {
                c.SetLookDir(fwd, 0f);   // the body faces the camera: A/D step sideways, S backs up
                c.SetMoveDir(m._heading * m._speed);
                c.SetRun(m._run && m._speed > 0.6f);
            }
            else
            {
                m._speed = 0f;
                c.SetMoveDir(Vector3.zero);
                c.SetRun(false);
            }
            return true;
        }

        private static readonly FieldInfo SemanChar = AccessTools.Field(typeof(SEMan), "m_character");

        // the ridden animal's speed: walking is quicker than its lazy wild amble, and going sideways or
        // backwards is slower than going forward
        // In the air (a jump, a bump) some animals freeze in a still pose (the moose, the boar): their animation has
        // no falling legs. While you ride, tell their animation they're still on the ground so the legs keep going.
        private static readonly FieldInfo OnGroundHash = AccessTools.Field(typeof(Character), "s_onGround");
        private static readonly FieldInfo ZAnimField = AccessTools.Field(typeof(Character), "m_zanim");
        [HarmonyPatch(typeof(Character), "CustomFixedUpdate"), HarmonyPostfix]
        private static void KeepLegsGoing(Character __instance)
        {
            Mount m = Riding;
            if (m == null || m._c != __instance || !Plugin.Enabled.Value || OnGroundHash == null || ZAnimField == null) return;
            if (__instance.IsOnGround() || __instance.IsSwimming() || Sea.IsSeaMount(__instance)) return;
            ZSyncAnimation za = ZAnimField.GetValue(__instance) as ZSyncAnimation;
            if (za != null) za.SetBool((int)OnGroundHash.GetValue(null), true);
        }

        // Some animals pick random idle poses (the seal rolls onto its back in the water): not while you ride them.
        // Holding the timer at 0 means no new pick; value 0 is the plain pose. The game then applies it as usual.
        private static float _raLog;
        [HarmonyPatch(typeof(RandomAnimation), "FixedUpdate"), HarmonyPrefix]
        private static void NoRandomIdle(RandomAnimation __instance)
        {
            Mount m = Riding;
            if (m == null || m._c == null || !Plugin.Enabled.Value || __instance.GetComponentInParent<Character>() != m._c) return;
            foreach (RandomAnimation.RandomValue v in __instance.m_values)
            {
                v.m_timer = 0f;
                v.m_value = 0;
            }
            if (Time.time > _raLog)
            {
                _raLog = Time.time + 10f;
                List<string> names = new List<string>();
                foreach (RandomAnimation.RandomValue v in __instance.m_values) names.Add(v.m_name + "(" + v.m_values + ")");
                Plugin.Debug("[ride] " + m._c.name + ": random idles held still: " + string.Join(", ", names.ToArray()));
            }
        }

        // the deer is the quick one: a ridden deer always runs a little faster than a ridden boar (10%)
        private static float _deerRun = -1f, _deerWalk = -1f;
        private static float DeerEdge(Character c, bool running)
        {
            if (!string.Equals(Kind(c.gameObject), "Deer", System.StringComparison.OrdinalIgnoreCase)) return 1f;
            if (_deerRun < 0f)
            {
                GameObject bp = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab("Boar") : null;
                Character boar = bp != null ? bp.GetComponent<Character>() : null;
                _deerRun = boar != null && c.m_runSpeed > 0.1f ? Mathf.Max(1f, boar.m_runSpeed * 1.1f / c.m_runSpeed) : 1f;
                _deerWalk = boar != null && c.m_walkSpeed > 0.1f ? Mathf.Max(1f, boar.m_walkSpeed * 1.1f / c.m_walkSpeed) : 1f;
                Plugin.Debug("[ride] deer run " + c.m_runSpeed.ToString("0.0") + " / walk " + c.m_walkSpeed.ToString("0.0")
                    + (boar != null ? ", boar run " + boar.m_runSpeed.ToString("0.0") + " / walk " + boar.m_walkSpeed.ToString("0.0") : "")
                    + ": deer boost x" + _deerRun.ToString("0.00") + " / x" + _deerWalk.ToString("0.00"));
            }
            return running ? _deerRun : _deerWalk;
        }

        [HarmonyPatch(typeof(SEMan), "ApplyStatusEffectSpeedMods"), HarmonyPostfix]
        private static void Speed(SEMan __instance, ref float speed, Vector3 dir)
        {
            Mount m = Riding;
            if (m == null || !Plugin.Enabled.Value || SemanChar == null) return;
            if (SemanChar.GetValue(__instance) as Character != m._c) return;
            if (Sea.IsSeaMount(m._c)) { speed = Mathf.Abs(m._seaSpeed); return; }
            bool running = m._run && m._speed > 0.6f;
            if (!running) speed *= Plugin.RideWalkSpeed.Value;
            speed *= DeerEdge(m._c, running);
            // a seal with a rider swims at set speeds (m/s): 4 swimming along, 8 with the run key (a serpent sprints at 12)
            if (m._c.IsSwimming() && string.Equals(Utils.GetPrefabName(m._c.gameObject), "Seal", System.StringComparison.OrdinalIgnoreCase))
                speed = m._run ? 8f : 4f;
            Vector3 d = dir;
            d.y = 0f;
            if (d.sqrMagnitude < 0.0001f) return;
            float ahead = Vector3.Dot(d.normalized, m._c.transform.forward);   // 1 forward, 0 sideways, -1 back
            float slow = Plugin.RideSideSpeed.Value;
            speed *= ahead >= 0f ? Mathf.Lerp(slow, 1f, ahead) : slow;
        }

        [HarmonyPatch(typeof(MonsterAI), "UpdateAI"), HarmonyPrefix, HarmonyPriority(Priority.High)]
        private static bool MonsterUpdate(MonsterAI __instance, float dt, ref bool __result)
        {
            if (!Plugin.Enabled.Value || !Drive(__instance, dt)) return true;
            __result = true;
            return false;
        }

        [HarmonyPatch(typeof(AnimalAI), "UpdateAI"), HarmonyPrefix, HarmonyPriority(Priority.High)]
        private static bool AnimalUpdate(AnimalAI __instance, float dt, ref bool __result)
        {
            if (!Plugin.Enabled.Value || !Drive(__instance, dt)) return true;
            __result = true;
            return false;
        }

        // ---------------------------------------------------------------- E on a saddled animal
        //  E               ride
        //  hold E          open its saddle bags (pack animals: deer, lox)
        //  Shift + E       the game's usual (rename)
        //  Shift + hold E  take the saddle off (bags must be empty)
        // A press is only decided when you let go or have held it long enough, so holding never rides first.

        private const float HoldTime = 0.45f;
        private static Character _pressOn;
        private static bool _pressAlt;
        private static float _pressAt;
        private static readonly MethodInfo TameInteract = AccessTools.Method(typeof(Tameable), "Interact");

        [HarmonyPatch(typeof(Player), "Interact"), HarmonyPrefix]
        private static bool Interact(Player __instance, GameObject go, bool hold, bool alt, bool __runOriginal)
        {
            if (!__runOriginal) return false;   // another part of the mod (untying) already took this press
            if (!Plugin.Enabled.Value || __instance != Player.m_localPlayer || go == null) return true;
            Character c = go.GetComponentInParent<Character>();
            if (c == null || c.IsPlayer() || !Saddled(c) || c.GetComponent<Mount>() == null) return true;
            if (Tie.Busy(__instance, c)) return true;   // the lasso's E (untie) comes first
            if (hold) return false;                       // holding is ours, see UpdatePress
            if (__instance.IsAttached() || __instance.GetDoodadController() != null) return false;
            if (!alt && !Bags.IsPacker(c)) { TryRide(__instance, c); return false; }   // nothing to hold for: ride now
            _pressOn = c;
            _pressAlt = alt;
            _pressAt = Time.time;
            return false;
        }

        // every frame: a pending E press becomes a ride (or a rename) when let go, or the held action after HoldTime
        internal static void UpdatePress()
        {
            if (_pressOn == null) return;
            Player p = Player.m_localPlayer;
            Character c = _pressOn;
            if (p == null || c == null || c.IsDead()) { _pressOn = null; return; }
            bool down = ZInput.GetButton("Use") || ZInput.GetButton("JoyUse");
            if (Time.time - _pressAt >= HoldTime)
            {
                _pressOn = null;
                if (_pressAlt) TakeSaddleOff(p, c);
                else Bags.Open(p, c);
                return;
            }
            if (down) return;
            _pressOn = null;
            if (!_pressAlt) TryRide(p, c);
            else
            {
                Tameable t = c.GetComponent<Tameable>();
                if (t != null && TameInteract != null) TameInteract.Invoke(t, new object[] { p, false, true });
            }
        }

        private static void TryRide(Player p, Character c)
        {
            Mount m = c.GetComponent<Mount>();
            if (m == null) return;
            if (!c.IsTamed()) { p.Message(MessageHud.MessageType.Center, c.GetHoverName() + " isn't tamed."); return; }
            if (!Rideable(c)) { p.Message(MessageHud.MessageType.Center, c.GetHoverName() + " can't be ridden."); return; }
            long rider = RiderOf(c);
            if (rider != 0L && rider != p.GetPlayerID() && Leash.FindPlayer(rider) != null)
            {
                p.Message(MessageHud.MessageType.Center, "Someone is riding it.");
                return;
            }
            if (p.IsAttached() || p.GetDoodadController() != null) return;
            m.Ride(p);
        }

        // ---------------------------------------------------------------- keys while riding

        private static bool _wasCrouch, _wasAttack2, _wasAttack1;

        // while riding: jump makes the animal jump (instead of getting you off), crouch gets you off,
        // attack and secondary attack make fighting animals use their own attacks
        [HarmonyPatch(typeof(Player), "SetControls"), HarmonyPrefix, HarmonyPriority(Priority.High)]
        private static void RideKeys(Player __instance, ref bool jump, ref bool crouch, ref bool dodge, ref bool attack, ref bool attackHold,
            ref bool secondaryAttack, ref bool secondaryAttackHold, ref bool block, ref bool blockHold)
        {
            Mount m = Riding;
            if (m == null || !Plugin.Enabled.Value || __instance != Player.m_localPlayer || __instance.GetDoodadController() as Mount != m)
            { _wasCrouch = false; return; }
            bool sea = Sea.IsSeaMount(m._c);
            if (jump) { jump = false; if (!sea) m.Hop(); }
            if (sea) dodge = attack = secondaryAttack = false;   // none of these may throw you off into the open sea
            else
            {
                // left button: its normal attack; right button (and the middle one): its strong attack
                bool a1 = attack || attackHold, a2 = secondaryAttack || secondaryAttackHold || block || blockHold;
                if (a1 && !_wasAttack1) Fight.Attack(m._c, false);
                if (a2 && !_wasAttack2) Fight.Attack(m._c, true);
                _wasAttack1 = a1;
                _wasAttack2 = a2;
                attack = attackHold = secondaryAttack = secondaryAttackHold = block = blockHold = dodge = false;
            }
            bool down = crouch;
            crouch = false;
            if (!sea) { _wasCrouch = down; return; }   // on land E gets you off; crouching does nothing
            if (down && !_wasCrouch)
            {
                _wasCrouch = true;
                if (sea && !Sea.CanGetOff(m._c))
                {
                    __instance.Message(MessageHud.MessageType.Center, "Too far from land or a ship to get off.");
                    return;
                }
                __instance.StopDoodadControl();
                return;
            }
            _wasCrouch = down;
        }

        // how high each mount jumps (metres, about); the rest: 1 m up to deer size, then in step with their height
        private static readonly System.Collections.Generic.Dictionary<string, float> JumpHeight =
            new System.Collections.Generic.Dictionary<string, float>(System.StringComparer.OrdinalIgnoreCase)
            { { "Moose", 3f }, { "Asksvin", 3f }, { "Wolf", 2f } };

        // the asksvin leaps forward like a lizard at its prey (metres a second, forward, while in the air)
        private static readonly System.Collections.Generic.Dictionary<string, float> Lunge =
            new System.Collections.Generic.Dictionary<string, float>(System.StringComparer.OrdinalIgnoreCase)
            { { "Asksvin", 9f } };

        // the jump force for a height: JumpForce (5) is about a metre, and the height grows with the square of the force
        internal static float JumpScale(Character c)
        {
            float h;
            if (!JumpHeight.TryGetValue(Kind(c.gameObject), out h)) h = Mathf.Max(1f, Height(c) / 1.8f);
            return Mathf.Sqrt(h);
        }

        private Character _lunging;
        private float _lungeSpeed, _lungeUntil;

        // in the air after a lunge: keep it flying forward (the game would slow it down to its walking pace)
        [HarmonyPatch(typeof(Character), "CustomFixedUpdate"), HarmonyPostfix]
        private static void KeepLunging(Character __instance)
        {
            Mount m = Riding;
            if (m == null || m._lunging != __instance || !Plugin.Enabled.Value) return;
            if (Time.time > m._lungeUntil || (Time.time > m._lungeUntil - 1.9f && __instance.IsOnGround())) { m._lunging = null; return; }
            Rigidbody rb = __instance.GetComponent<Rigidbody>();
            if (rb == null) return;
            Vector3 v = __instance.transform.forward * m._lungeSpeed;
            v.y = rb.velocity.y;
            rb.velocity = v;
        }

        private void Hop()
        {
            if (_c == null || !_c.IsOnGround() || Plugin.RideJumpForce.Value <= 0f) return;
            if (!_nv.IsOwner()) _nv.ClaimOwnership();
            float old = _c.m_jumpForce;
            // the same jump for every mount up to deer size (their own jumps are far too high with a rider);
            // bigger animals jump higher, about in step with their height
            _c.m_jumpForce = Plugin.RideJumpForce.Value * JumpScale(_c);
            _c.Jump(true);
            _c.m_jumpForce = old;
            float lunge;
            if (Lunge.TryGetValue(Kind(_c.gameObject), out lunge))
            {
                _lunging = _c;
                _lungeSpeed = lunge;
                _lungeUntil = Time.time + 2f;   // the first 0.1 s it may still touch the ground
            }
            // some animals (deer, wolf) get stuck in their own jump pose and stop running their legs: skip it
            Animator an = _c.GetComponentInChildren<Animator>();
            if (an != null) an.ResetTrigger("jump");
        }

        // ---------------------------------------------------------------- the saddle in your hands

        [HarmonyPatch(typeof(Player), "SetControls"), HarmonyPrefix]
        private static void Controls(Player __instance, ref bool attack, ref bool attackHold, ref bool secondaryAttack, ref bool secondaryAttackHold)
        {
            if (!Plugin.Enabled.Value || __instance != Player.m_localPlayer) return;
            if (_muteAttack)
            {
                // the saddle just left your hands: the same click must not become a punch
                if (attack || attackHold) { attack = attackHold = false; return; }
                _muteAttack = false;
            }
            ItemDrop.ItemData held = Riding != null ? null : Holding(__instance);
            if (held == null) { _wasAttack = false; return; }
            bool press = attack && !_wasAttack;
            _wasAttack = attack || attackHold;
            attack = attackHold = secondaryAttack = secondaryAttackHold = false;
            if (press) { UseSaddle(__instance, held); _muteAttack = true; }
        }

        // pressing a saddle's hotbar key while looking at an animal saddles it straight away
        [HarmonyPatch(typeof(Humanoid), "UseItem"), HarmonyPrefix]
        private static bool UseItem(Humanoid __instance, ItemDrop.ItemData item, bool fromInventoryGui)
        {
            if (!Plugin.Enabled.Value || fromInventoryGui || !IsSaddle(item)) return true;
            Player p = __instance as Player;
            if (p == null || p != Player.m_localPlayer) return true;
            GameObject hover = p.GetHoverObject();
            Character c = hover != null ? hover.GetComponentInParent<Character>() : null;
            if (c == null || c.IsPlayer()) return true;   // not looking at an animal: equip it as usual
            UseSaddle(p, item);
            return false;
        }

        private static ItemDrop.ItemData Holding(Player p)
        {
            foreach (ItemDrop.ItemData it in p.GetInventory().GetAllItems())
                if (IsSaddle(it) && p.IsItemEquiped(it)) return it;
            return null;
        }

        private static void UseSaddle(Player p, ItemDrop.ItemData saddle)
        {
            GameObject hover = p.GetHoverObject();
            Character c = hover != null ? hover.GetComponentInParent<Character>() : null;
            if (c == null || c.IsPlayer()) { p.Message(MessageHud.MessageType.Center, "Look at a tamed animal to saddle it."); return; }
            string name = c.GetHoverName();
            ZNetView nv = Leash.View(c);
            if (nv == null || !nv.IsValid()) return;
            if (Saddled(c)) { p.Message(MessageHud.MessageType.Center, name + " already wears a saddle (Shift + hold E takes it off)."); return; }
            if (!Leash.Eligible(c) || c.GetComponent<Mount>() == null) { p.Message(MessageHud.MessageType.Center, "You can't saddle " + name + "."); return; }
            if (!Rideable(c)) { p.Message(MessageHud.MessageType.Center, name + " can't be ridden."); return; }
            if (!c.IsTamed()) { p.Message(MessageHud.MessageType.Center, name + " must be tamed first."); return; }
            if (!BigEnough(c) && !Sea.IsSeaMount(c)) { p.Message(MessageHud.MessageType.Center, name + " is too small to carry you."); return; }
            int tier = Set.TierOf(saddle), need = Tiers.Animal(c);
            if (tier < need) { p.Message(MessageHud.MessageType.Center, "The " + saddle.m_shared.m_name + " doesn't fit " + name + ": you need " + Tiers.Need("Saddle", need) + "."); return; }
            if (!nv.IsOwner()) nv.ClaimOwnership();
            nv.GetZDO().Set(SaddleKey, tier);
            if (p.IsItemEquiped(saddle)) p.UnequipItem(saddle, false);
            p.GetInventory().RemoveItem(saddle, 1);
            Effects.Shake(0.8f);
            p.Message(MessageHud.MessageType.Center, name + " wears the " + saddle.m_shared.m_name + ". Press E to ride.");
            Plugin.Debug("[ride] saddled " + c.name + " with tier " + tier + " (height " + Height(c).ToString("0.00") + " m)");
        }

        private static void TakeSaddleOff(Player p, Character c)
        {
            string name = c.GetHoverName();
            ZNetView nv = Leash.View(c);
            int tier = SaddleTier(c);
            if (nv == null || !nv.IsValid() || tier == 0) return;
            if (RiderOf(c) != 0L && Leash.FindPlayer(RiderOf(c)) != null) { p.Message(MessageHud.MessageType.Center, "Someone is riding it."); return; }
            if (!Bags.Empty(c)) { p.Message(MessageHud.MessageType.Center, "Empty " + name + "'s saddle bags first."); return; }
            if (!nv.IsOwner()) nv.ClaimOwnership();
            nv.GetZDO().Set(SaddleKey, 0);
            GameObject prefab = Set.Prefab(tier);
            if (!p.GetInventory().AddItem(prefab, 1))
                ItemDrop.DropItem(prefab.GetComponent<ItemDrop>().m_itemData.Clone(), 1, p.transform.position + p.transform.forward, Quaternion.identity);
            p.Message(MessageHud.MessageType.Center, "You take the saddle off " + name + ".");
        }

        internal static string HoverLine(Character c)
        {
            if (!Plugin.Enabled.Value || c == null || !Saddled(c)) return "";
            string worn = "\n<color=#d8b080>" + Tiers.Line[SaddleTier(c)] + " Saddle</color>";
            if (!Rideable(c)) return worn + " (can't be ridden)\n[<color=yellow><b>Shift + hold E</b></color>] Take the saddle off";
            long rider = RiderOf(c);
            if (rider != 0L && Leash.FindPlayer(rider) != null) return worn + " (ridden)";
            string s = worn + "\n[<color=yellow><b>E</b></color>] Ride";
            if (Bags.IsPacker(c)) s += "\n[<color=yellow><b>Hold E</b></color>] Saddle bags";
            if (c.GetComponent<Tameable>() != null) s += "\n[<color=yellow><b>Shift + E</b></color>] Rename";
            return s + "\n[<color=yellow><b>Shift + hold E</b></color>] Take the saddle off";
        }

        // a saddled animal's E rides it: the game's own "Pet" and "Rename" lines go (ours list them)
        internal static string WithoutGameKeys(Character c, string text)
        {
            if (!Plugin.Enabled.Value || c == null || text == null || !Saddled(c) || !Rideable(c) || Sea.IsSeaMount(c) || Localization.instance == null) return text;
            string pet = Localization.instance.Localize("$hud_pet"), rename = Localization.instance.Localize("$hud_rename");
            System.Collections.Generic.List<string> keep = new System.Collections.Generic.List<string>();
            foreach (string line in text.Split('\n'))
                if ((pet.Length == 0 || !line.Contains(pet)) && (rename.Length == 0 || !line.Contains(rename))) keep.Add(line);
            return string.Join("\n", keep.ToArray());
        }

        // a saddle with a stirrup, drawn in code
        private static Sprite Icon()
        {
            Color light = Items.LeatherLight, dark = Items.LeatherDark, metal = new Color(0.7f, 0.7f, 0.66f);
            return Items.MakeIcon("Beastkeeper_SaddleIcon", p =>
            {
                Color col = Color.clear;
                // the seat: a wide curve, high at the back (left) and the front (right)
                float x = (p.x - 32f) / 26f;
                if (Mathf.Abs(x) <= 1f)
                {
                    float top = 40f - 10f * (1f - x * x) + (x < -0.7f ? (-0.7f - x) * 30f : 0f) + (x > 0.75f ? (x - 0.75f) * 22f : 0f);
                    float bottom = 26f - 6f * (1f - x * x);
                    if (p.y <= top && p.y >= bottom)
                    {
                        float k = (p.y - bottom) / Mathf.Max(1f, top - bottom);
                        col = Color.Lerp(dark, light, 0.3f + 0.6f * k);
                        if (top - p.y < 1.6f || p.y - bottom < 1.6f) col = dark;
                    }
                }
                // the flap and the stirrup strap hanging down
                if (Mathf.Abs(p.x - 30f) < 2f && p.y < 26f && p.y > 10f) col = dark;
                float ring = (p - new Vector2(30f, 8f)).magnitude;
                if (Mathf.Abs(ring - 5f) < 1.5f && p.y < 11f) col = metal;
                return col;
            });
        }
    }
}
