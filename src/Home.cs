using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Beastkeeper
{
    // Where tamed animals live when nobody leads them.
    //  - Animals tamed with a halter (deer, neck...) have no tame behaviour of their own in the game: left
    //    alone they would act wild again and run from you. Here they stay calm and graze around their home,
    //    the spot where you last let them go.
    //  - A Beast Totem marks a home for every tamed animal around it (TotemRange): any of them that strays
    //    further than TotemRadius walks back, including the game's own tamed animals (boar, wolf, lox...)
    //    told to stay. Animals following someone, on a lasso or ridden are left alone.
    [HarmonyPatch]
    internal static class Home
    {
        internal static readonly int HomeKey = "Beastkeeper_home".GetStableHashCode();
        private static readonly FieldInfo AIView = AccessTools.Field(typeof(BaseAI), "m_nview");
        private static readonly FieldInfo AIChar = AccessTools.Field(typeof(BaseAI), "m_character");

        private class Wander
        {
            public Vector3 Target;
            public bool Moving;
            public float Until;
        }
        private static readonly Dictionary<Character, Wander> States = new Dictionary<Character, Wander>();

        internal static void Set(Character c, Vector3 at)
        {
            ZNetView nv = Leash.View(c);
            if (nv == null || !nv.IsValid()) return;
            if (!nv.IsOwner()) nv.ClaimOwnership();
            nv.GetZDO().Set(HomeKey, at);
            States.Remove(c);
        }

        [HarmonyPatch(typeof(MonsterAI), "UpdateAI"), HarmonyPrefix, HarmonyPriority(Priority.Low)]
        private static bool MonsterUpdate(MonsterAI __instance, float dt, ref bool __result) { return Run(__instance, dt, ref __result); }

        [HarmonyPatch(typeof(AnimalAI), "UpdateAI"), HarmonyPrefix, HarmonyPriority(Priority.Low)]
        private static bool AnimalUpdate(AnimalAI __instance, float dt, ref bool __result) { return Run(__instance, dt, ref __result); }

        private static bool Run(BaseAI ai, float dt, ref bool result)
        {
            if (!Plugin.Enabled.Value) return true;
            Character c = AIChar != null ? AIChar.GetValue(ai) as Character : null;
            ZNetView nv = AIView != null ? AIView.GetValue(ai) as ZNetView : null;
            if (c == null || nv == null || !nv.IsValid() || !nv.IsOwner() || c.IsDead() || !c.IsTamed() || c.IsPlayer()) return true;
            if (Mount.IsRidden(c) || Leash.Holder(nv) != 0L || Trap.IsHeld(c)) return true;   // someone else drives it
            bool tied = Tie.IsTied(c);
            if (tied)
            {
                Tie.CheckPiece(c);
                tied = Tie.IsTied(c);
                if (tied && Tie.Loose(c)) return true;   // hit: it does what it would do, and comes back after
            }
            MonsterAI mai = ai as MonsterAI;
            if (!tied && mai != null && mai.GetFollowTarget() != null) return true;            // following its master
            if (tied && mai != null && mai.GetFollowTarget() != null) mai.SetFollowTarget(null);

            bool gameTame = c.GetComponent<Tameable>() != null && !tied;
            Totem totem = tied ? null : Totem.Nearest(c.transform.position, Plugin.TotemRange.Value);
            Vector3 home;
            float radius;
            if (tied) { home = Tie.Anchor(c); radius = Tie.Length - 1.5f; }   // the rope is Length: graze inside it
            else if (totem != null) { home = totem.transform.position; radius = Plugin.TotemRadius.Value; }
            else
            {
                if (gameTame) return true;   // the game's own tamed animals: the game's own behaviour
                ZDO z = nv.GetZDO();
                home = z.GetVec3(HomeKey, Vector3.zero);
                if (home == Vector3.zero) { home = c.transform.position; z.Set(HomeKey, home); }
                radius = Plugin.WanderRadius.Value;
            }

            Vector3 d = c.transform.position - home;
            d.y = 0f;
            float dist = d.magnitude;
            if (gameTame && dist <= radius) return true;   // close enough: back to the game's behaviour

            Leash.Calm(ai);
            result = true;
            Wander w;
            if (!States.TryGetValue(c, out w)) { w = new Wander { Until = Time.time + Random.Range(1f, 4f) }; States[c] = w; }
            if (States.Count > 200) States.Clear();

            if (dist > radius + 1.5f)
            {
                // strayed: walk home (run when far)
                Leash.MoveTo(ai, dt, home, dist > 25f);
                w.Moving = false;
                return false;
            }
            // graze: stand a while, walk a few steps to a nearby spot, stand again
            if (w.Moving)
            {
                Vector3 t = w.Target - c.transform.position;
                t.y = 0f;
                if (t.magnitude < 1f || Time.time > w.Until) { w.Moving = false; w.Until = Time.time + Random.Range(4f, 11f); ai.StopMoving(); }
                else Leash.MoveTo(ai, dt, w.Target, false);
            }
            else
            {
                ai.StopMoving();
                if (Time.time > w.Until)
                {
                    Vector2 r = Random.insideUnitCircle * radius * 0.8f;
                    w.Target = home + new Vector3(r.x, 0f, r.y);
                    w.Moving = true;
                    w.Until = Time.time + 8f;
                }
            }
            return false;
        }
    }

    // The Beast Totem: a pole crowned with a deer trophy, built with the hammer (Misc tab) near a workbench.
    // The hammer's remove button takes it down and gives the materials back, like any other piece.
    [HarmonyPatch]
    internal class Totem : MonoBehaviour, Hoverable, Interactable
    {
        internal const string PrefabName = "Beastkeeper_TotemPiece";
        private const float PoleTop = 1.8f;
        private static GameObject _prefab;
        private static bool _dressed;
        private static readonly List<Totem> All = new List<Totem>();

        private ZNetView _nv;

        internal static void Init() { }

        internal static Totem Nearest(Vector3 at, float range)
        {
            Totem best = null;
            float bd = range;
            foreach (Totem t in All)
            {
                if (t == null || t._nv == null || !t._nv.IsValid()) continue;
                float d = Vector3.Distance(at, t.transform.position);
                if (d <= bd) { bd = d; best = t; }
            }
            return best;
        }

        private static GameObject Prefab()
        {
            if (_prefab == null) _prefab = BuildPrefab();
            return _prefab;
        }

        // ------------------------------------------------------------ registering

        [HarmonyPatch(typeof(ZNetScene), "Awake"), HarmonyPostfix]
        private static void RegisterPrefab(ZNetScene __instance)
        {
            GameObject p = Prefab();
            if (!__instance.m_prefabs.Contains(p)) __instance.m_prefabs.Add(p);
            var named = AccessTools.Field(typeof(ZNetScene), "m_namedPrefabs").GetValue(__instance) as Dictionary<int, GameObject>;
            if (named != null) named[PrefabName.GetStableHashCode()] = p;
            if (ObjectDB.instance != null) AddToHammer(ObjectDB.instance);
        }

        [HarmonyPatch(typeof(ObjectDB), "Awake"), HarmonyPostfix]
        private static void DBAwake(ObjectDB __instance) { AddToHammer(__instance); }

        [HarmonyPatch(typeof(ObjectDB), "CopyOtherDB"), HarmonyPostfix]
        private static void DBCopy(ObjectDB __instance) { AddToHammer(__instance); }

        private static void AddToHammer(ObjectDB db)
        {
            if (db == null || db.m_items == null || db.m_items.Count == 0) return;
            GameObject hammer = db.GetItemPrefab("Hammer");
            PieceTable table = hammer != null ? hammer.GetComponent<ItemDrop>().m_itemData.m_shared.m_buildPieces : null;
            if (table == null) return;
            GameObject p = Prefab();
            Dress(db);
            Piece piece = p.GetComponent<Piece>();
            List<Piece.Requirement> reqs = new List<Piece.Requirement>();
            foreach (string part in Recipes.Totem.Split(','))
            {
                string[] kv = part.Split(':');
                if (kv.Length != 2) continue;
                GameObject ip = db.GetItemPrefab(kv[0].Trim());
                int amount;
                if (ip == null || !int.TryParse(kv[1].Trim(), out amount)) { Plugin.Log.LogWarning("Beast Totem cost: unknown item '" + part + "'."); continue; }
                reqs.Add(new Piece.Requirement { m_resItem = ip.GetComponent<ItemDrop>(), m_amount = amount, m_recover = true });
            }
            piece.m_resources = reqs.ToArray();
            GameObject sp = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab("piece_workbench") : null;
            piece.m_craftingStation = sp != null ? sp.GetComponent<CraftingStation>() : null;
            GameObject trophy = Crown(db);
            if (trophy != null && piece.m_icon == null)
            {
                Sprite[] icons = trophy.GetComponent<ItemDrop>().m_itemData.m_shared.m_icons;
                if (icons != null && icons.Length > 0) piece.m_icon = icons[0];
            }
            if (!table.m_pieces.Contains(p)) table.m_pieces.Add(p);
            Plugin.Debug("[totem] in the hammer: " + reqs.Count + " materials, station " + (piece.m_craftingStation != null ? piece.m_craftingStation.name : "none"));
        }

        // ------------------------------------------------------------ the model

        private static GameObject BuildPrefab()
        {
            GameObject holder = new GameObject("Beastkeeper_TotemHolder");
            holder.SetActive(false);
            DontDestroyOnLoad(holder);
            GameObject root = new GameObject(PrefabName);
            root.transform.SetParent(holder.transform, false);
            int layer = LayerMask.NameToLayer("piece");
            if (layer < 0) layer = 0;
            root.layer = layer;
            ZNetView nv = root.AddComponent<ZNetView>();
            nv.m_persistent = true;
            Part(root.transform, "pole", PrimitiveType.Cylinder, new Vector3(0f, PoleTop / 2f, 0f), new Vector3(0.14f, PoleTop / 2f, 0.14f), layer, true);
            Part(root.transform, "bar", PrimitiveType.Cube, new Vector3(0f, 1.35f, 0f), new Vector3(0.7f, 0.07f, 0.07f), layer, false);
            Part(root.transform, "base", PrimitiveType.Cylinder, new Vector3(0f, 0.05f, 0f), new Vector3(0.45f, 0.05f, 0.45f), layer, true);
            GameObject top = new GameObject("skull");
            top.layer = layer;
            top.transform.SetParent(root.transform, false);
            top.transform.localPosition = new Vector3(0f, PoleTop, 0f);
            Piece piece = root.AddComponent<Piece>();
            piece.m_name = "Beast Totem";
            piece.m_description = "A pole crowned with a wreath of flowers. Tamed animals around it that stray too far walk back to it, unless they follow someone or are tied up.";
            piece.m_category = Piece.PieceCategory.Misc;
            piece.m_groundOnly = false;
            root.AddComponent<Totem>();
            return root;
        }

        private static void Part(Transform parent, string name, PrimitiveType shape, Vector3 at, Vector3 size, int layer, bool keepCollider)
        {
            GameObject g = GameObject.CreatePrimitive(shape);
            g.name = name;
            g.layer = layer;
            g.transform.SetParent(parent, false);
            g.transform.localPosition = at;
            g.transform.localScale = size;
            Collider col = g.GetComponent<Collider>();
            if (col != null && !keepCollider) DestroyImmediate(col);
        }

        // the game's midsummer flower crown (the deer trophy if this game has no crown)
        private static GameObject Crown(ObjectDB db)
        {
            return db.GetItemPrefab("HelmetMidsummerCrown") ?? db.GetItemPrefab("TrophyDeer");
        }

        // wood for the pole; on top, the flower crown
        private static void Dress(ObjectDB db)
        {
            if (_dressed) return;
            GameObject wood = db.GetItemPrefab("Wood");
            Renderer wr = wood != null ? wood.GetComponentInChildren<MeshRenderer>(true) : null;
            GameObject trophy = Crown(db);
            if (wr == null || trophy == null) return;
            _dressed = true;
            GameObject root = Prefab();
            foreach (MeshRenderer r in root.GetComponentsInChildren<MeshRenderer>(true)) r.sharedMaterial = wr.sharedMaterial;
            Transform top = root.transform.Find("skull");
            Transform attach = trophy.transform.Find("attach");
            GameObject m;
            if (attach != null)
            {
                m = Instantiate(attach.gameObject, top, false);
                m.transform.localPosition = Vector3.zero;
                m.transform.localRotation = Quaternion.identity;
            }
            else
            {
                MeshFilter tf = trophy.GetComponentInChildren<MeshFilter>(true);
                if (tf == null) return;
                m = Instantiate(tf.gameObject, top, false);
                m.transform.localPosition = Vector3.zero;
            }
            m.name = "model";
            foreach (Collider c in m.GetComponentsInChildren<Collider>(true)) DestroyImmediate(c);
            foreach (MonoBehaviour b in m.GetComponentsInChildren<MonoBehaviour>(true)) DestroyImmediate(b);
            SetLayer(m.transform, root.layer);
            // the prefab is inactive, so renderer bounds are empty: work them out from the meshes
            Bounds b2 = new Bounds();
            bool any = false;
            foreach (MeshFilter f in m.GetComponentsInChildren<MeshFilter>(true))
            {
                if (f.sharedMesh == null) continue;
                Bounds mb = f.sharedMesh.bounds;
                Matrix4x4 w = root.transform.worldToLocalMatrix * f.transform.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    Vector3 pt = w.MultiplyPoint3x4(corner);
                    if (!any) { b2 = new Bounds(pt, Vector3.zero); any = true; } else b2.Encapsulate(pt);
                }
            }
            if (any)
            {
                // centred over the pole, the pole going a little way up into the neck
                Vector3 shift = new Vector3(-b2.center.x, PoleTop - 0.15f - b2.min.y, -b2.center.z);
                m.transform.localPosition += Quaternion.Inverse(top.localRotation) * shift;
                Plugin.Debug("[totem] top from " + trophy.name + " " + (attach != null ? "attach" : "mesh") + ", size " + b2.size.ToString("0.00"));
            }
        }

        private static void SetLayer(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            foreach (Transform c in t) SetLayer(c, layer);
        }

        // ------------------------------------------------------------ in the world

        private void Awake()
        {
            _nv = GetComponent<ZNetView>();
            if (_nv != null && _nv.GetZDO() != null) All.Add(this);
        }

        private void OnDestroy() { All.Remove(this); }

        public string GetHoverName() { return "Beast Totem"; }
        public float GetHoverOffset() { return 2f; }

        public string GetHoverText()
        {
            if (_nv == null || !_nv.IsValid()) return "";
            int n = 0;
            foreach (Character c in Character.GetAllCharacters())
                if (c != null && !c.IsPlayer() && c.IsTamed() && Vector3.Distance(c.transform.position, transform.position) <= Plugin.TotemRange.Value) n++;
            return "Beast Totem\n<color=#d8b080>" + n + " tamed animal" + (n == 1 ? "" : "s") + " around</color>";
        }

        public bool Interact(Humanoid user, bool hold, bool alt) { return false; }

        public bool UseItem(Humanoid user, ItemDrop.ItemData item) { return false; }
    }
}
