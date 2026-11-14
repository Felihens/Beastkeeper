using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Beastkeeper
{
    // The leg-hold trap: a hotbar item. Use it and you set a trap on the ground in front of you.
    // The first wild animal that steps on it is held by the leg for a while (HoldSeconds): it can't walk away,
    // though a boar still bites whoever comes too close. A held animal can be lassoed even if it saw you.
    // E on a trap picks it up (and frees what it holds); Shift+E sets a sprung trap again.
    //
    // The trap is a world object saved with the world. The game of whoever owns the trap (usually the one
    // nearest) watches for animals; the game that moves the animal keeps it in place.
    // Players without the mod don't see traps, and their game ignores them.
    [HarmonyPatch]
    internal class Trap : MonoBehaviour, Hoverable, Interactable
    {
        internal const string PrefabName = "Beastkeeper_TrapPiece";
        internal static readonly ToolSet Set = new ToolSet();
        private static GameObject _prefab;
        private static readonly int TierKey = "Beastkeeper_trapTier".GetStableHashCode();
        private static readonly string[] Names = { "", "Flint Trap", "Bronze Trap", "Silver Trap", "Black Metal Trap", "Carapace Trap" };
        private static readonly string[] Jaws = { "", "Flint jaws", "Bronze jaws", "Iron and silver jaws", "Black metal jaws", "Carapace jaws" };

        private static readonly int ArmedKey = "Beastkeeper_armed".GetStableHashCode();
        private static readonly KeyValuePair<int, int> CaughtKey = ZDO.GetHashZDOID("Beastkeeper_caught");
        // on the animal: which trap holds it, and until when (game time, seconds)
        internal static readonly KeyValuePair<int, int> HeldByKey = ZDO.GetHashZDOID("Beastkeeper_trap");
        internal static readonly int HeldUntilKey = "Beastkeeper_trapUntil".GetStableHashCode();
        private const string RpcTrapped = "Beastkeeper_Trapped";
        private static readonly int BloodKey = "Beastkeeper_blood".GetStableHashCode();   // 1 once it has caught something, until it is set again

        private static readonly FieldInfo AIChar = AccessTools.Field(typeof(BaseAI), "m_character");

        private ZNetView _nv;
        private Transform _jawA, _jawB;
        private float _jawAngle = -1f, _next, _blood = -1f;
        private bool _dressed, _wasArmed = true;
        private Material _ironMat;
        private Color _tint = Color.white;
        private GameObject _bloodDrops;

        // ---------------------------------------------------------------- the item

        internal static void Init()
        {
            for (int t = 1; t <= Tiers.Max; t++)
            {
                int tier = t;
                Set.ByTier[t] = Items.Add(new CustomItem
                {
                    PrefabName = t == 1 ? "Beastkeeper_Trap" : "Beastkeeper_Trap" + t,
                    Sources = new[] { "Flint", "Stone" },
                    Recipe = Recipes.Trap[t],
                    Setup = (p, src) =>
                    {
                        ItemDrop.ItemData.SharedData sh = p.GetComponent<ItemDrop>().m_itemData.m_shared;
                        sh.m_name = Names[tier];
                        sh.m_description = Jaws[tier] + " on a wooden spring. Use it from the hotbar to set it on the ground in front of you: "
                            + "it holds wild " + Lasso.Catches(tier) + " by the leg for a while. E picks it up, Shift+E sets it again.";
                        sh.m_icons = new[] { Items.Tinted(Icon(), Recipes.Tint[tier], "Beastkeeper_TrapIcon" + tier) };
                        sh.m_itemType = ItemDrop.ItemData.ItemType.Material;
                        sh.m_maxStackSize = 10;
                        sh.m_weight = 2f;
                        sh.m_teleportable = true;
                    },
                });
            }
        }

        internal static bool IsTrapItem(ItemDrop.ItemData item) { return Set.Is(item); }

        // Using the trap from the hotbar sets it.
        [HarmonyPatch(typeof(Humanoid), "UseItem"), HarmonyPrefix]
        private static bool UseItem(Humanoid __instance, Inventory inventory, ItemDrop.ItemData item, bool fromInventoryGui)
        {
            if (!Plugin.Enabled.Value || !IsTrapItem(item)) return true;
            Player p = __instance as Player;
            if (p == null || p != Player.m_localPlayer) return true;
            if (_prefab == null) { p.Message(MessageHud.MessageType.Center, "The trap isn't ready yet."); return false; }
            if (p.IsAttached() || p.InWater() || !p.IsOnGround()) { p.Message(MessageHud.MessageType.Center, "Stand on solid ground to set a trap."); return false; }

            Vector3 at = p.transform.position + p.transform.forward * 1.3f;
            RaycastHit hit;
            if (Physics.Raycast(at + Vector3.up * 2f, Vector3.down, out hit, 4f, LayerMask.GetMask("terrain", "Default", "static_solid", "piece"), QueryTriggerInteraction.Ignore))
                at = hit.point;
            else if (ZoneSystem.instance != null) at.y = ZoneSystem.instance.GetGroundHeight(at);
            GameObject go = Instantiate(_prefab, at, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f));
            ZNetView nv = go.GetComponent<ZNetView>();
            if (nv != null && nv.IsValid()) { nv.GetZDO().Set(ArmedKey, 1); nv.GetZDO().Set(BloodKey, 0); nv.GetZDO().Set(TierKey, Set.TierOf(item)); }
            (inventory ?? p.GetInventory()).RemoveOneItem(item);
            Effects.Shake(0.4f);
            p.Message(MessageHud.MessageType.Center, "Trap set.");
            Plugin.Debug("[trap] set at " + at.ToString("0.0"));
            return false;
        }

        // ---------------------------------------------------------------- the world object

        [HarmonyPatch(typeof(ZNetScene), "Awake"), HarmonyPostfix]
        private static void RegisterPrefab(ZNetScene __instance)
        {
            if (_prefab == null) _prefab = BuildPrefab();
            if (!__instance.m_prefabs.Contains(_prefab)) __instance.m_prefabs.Add(_prefab);
            var named = AccessTools.Field(typeof(ZNetScene), "m_namedPrefabs").GetValue(__instance) as Dictionary<int, GameObject>;
            if (named != null) named[PrefabName.GetStableHashCode()] = _prefab;
        }

        private const float Size = 1.5f;   // the whole trap, compared to the first small one

        private static GameObject BuildPrefab()
        {
            GameObject holder = new GameObject("Beastkeeper_TrapHolder");
            holder.SetActive(false);
            DontDestroyOnLoad(holder);
            GameObject root = new GameObject(PrefabName);
            root.transform.SetParent(holder.transform, false);
            int layer = LayerMask.NameToLayer("piece_nonsolid");
            if (layer < 0) layer = 0;
            root.layer = layer;
            ZNetView nv = root.AddComponent<ZNetView>();
            nv.m_persistent = true;

            // an iron leg-hold trap on two wooden planks: the jaws lie open, and snap up to meet in the middle
            // (a plain invisible disc is what you aim at to use it)
            Part(root.transform, "plate", PrimitiveType.Cylinder, Size * new Vector3(0f, 0.03f, 0f), Size * new Vector3(0.6f, 0.03f, 0.6f), layer, true);
            Object.DestroyImmediate(root.transform.Find("plate").GetComponent<MeshRenderer>());
            GameObject model = new GameObject("model");
            model.layer = layer;
            model.transform.SetParent(root.transform, false);
            model.transform.localScale = Vector3.one * Size;
            Shape(model.transform, "wood", TrapModel.Wood(), layer);
            Shape(model.transform, "iron", TrapModel.Frame(), layer);
            Transform a = Hinge(model.transform, "jawA", new Vector3(0f, TrapModel.HingeY, 0f), layer);
            Transform b = Hinge(model.transform, "jawB", new Vector3(0f, TrapModel.HingeY, 0f), layer);
            Mesh jaw = TrapModel.Jaw();
            Shape(a, "iron", jaw, layer);
            Shape(b, "iron", jaw, layer);
            Shape(model.transform, "blood", TrapModel.Blood(), layer).SetActive(false);
            root.AddComponent<Trap>();
            return root;
        }

        private static Transform Hinge(Transform parent, string name, Vector3 at, int layer)
        {
            GameObject h = new GameObject(name);
            h.layer = layer;
            h.transform.SetParent(parent, false);
            h.transform.localPosition = at;
            return h.transform;
        }

        private static GameObject Shape(Transform parent, string name, Mesh mesh, int layer)
        {
            GameObject g = new GameObject(name);
            g.layer = layer;
            g.transform.SetParent(parent, false);
            g.AddComponent<MeshFilter>().sharedMesh = mesh;
            g.AddComponent<MeshRenderer>();
            return g;
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

        private void Awake()
        {
            _nv = GetComponent<ZNetView>();
            _jawA = transform.Find("model/jawA");
            _jawB = transform.Find("model/jawB");
            Transform bl = transform.Find("model/blood");
            _bloodDrops = bl != null ? bl.gameObject : null;
        }

        // borrow the game's own wood and flint looks (they are only known once the item list exists)
        private void Dress()
        {
            if (_dressed || ObjectDB.instance == null) return;
            Material wood = Look("Wood"), iron = Look("Iron") ?? Look("IronScrap") ?? Look("Flint");
            if (wood == null && iron == null) return;
            _dressed = true;
            // this trap's own copy of the iron, so its blood stains only this one
            _ironMat = iron != null ? new Material(iron) : null;
            _tint = Recipes.Tint[Tier];
            Material blood = new Material(iron ?? wood);
            blood.name = "Beastkeeper_TrapBlood";
            blood.mainTexture = null;
            blood.color = new Color(0.32f, 0.02f, 0.02f);
            if (blood.HasProperty("_Glossiness")) blood.SetFloat("_Glossiness", 0.7f);
            foreach (MeshRenderer r in GetComponentsInChildren<MeshRenderer>(true))
            {
                if (r.name == "wood") { if (wood != null) r.sharedMaterial = wood; }
                else if (r.name == "iron") { if (_ironMat != null) r.sharedMaterial = _ironMat; }
                else if (r.name == "blood") r.sharedMaterial = blood;
            }
        }

        private static Material Look(string item)
        {
            GameObject go = ObjectDB.instance.GetItemPrefab(item);
            Renderer r = go != null ? go.GetComponentInChildren<MeshRenderer>(true) : null;
            return r != null ? r.sharedMaterial : null;
        }

        private int Tier { get { return _nv != null && _nv.IsValid() ? Mathf.Clamp(_nv.GetZDO().GetInt(TierKey, 1), 1, Tiers.Max) : 1; } }
        private bool Armed { get { return _nv != null && _nv.IsValid() && _nv.GetZDO().GetInt(ArmedKey, 0) == 1; } }
        private ZDOID Caught { get { return _nv != null && _nv.IsValid() ? _nv.GetZDO().GetZDOID(CaughtKey) : ZDOID.None; } }

        private void Update()
        {
            if (_nv == null || !_nv.IsValid()) return;
            Dress();
            // jaws: flat when set, up and closed when sprung
            bool armed = Armed;
            float want = armed ? 0f : 86f;
            bool first = _jawAngle < 0f;
            _jawAngle = first ? want : Mathf.MoveTowards(_jawAngle, want, Time.deltaTime * 900f);
            if (_jawA != null) _jawA.localRotation = Quaternion.Euler(-_jawAngle, 0f, 0f);
            if (_jawB != null) _jawB.localRotation = Quaternion.Euler(0f, 180f, 0f) * Quaternion.Euler(-_jawAngle, 0f, 0f);
            // it just snapped shut on something: blood, on every game that sees it happen
            if (!first && _wasArmed && !armed) Splash();
            _wasArmed = armed;
            // the blood stays on it until it is set again
            float blood = _nv.GetZDO().GetInt(BloodKey, 0) == 1 ? 1f : 0f;
            if (blood != _blood)
            {
                _blood = blood;
                if (_bloodDrops != null) _bloodDrops.SetActive(blood > 0f);
                if (_ironMat != null) _ironMat.color = Color.Lerp(_tint, new Color(0.62f, 0.26f, 0.24f), blood);
            }

            if (!_nv.IsOwner() || Time.time < _next) return;
            _next = Time.time + 0.2f;
            if (Armed) Watch();
            else if (!Caught.IsNone() && Holding() == null) _nv.GetZDO().Set(CaughtKey, ZDOID.None);   // it got free
        }

        // the first wild animal standing on the plate
        private void Watch()
        {
            Vector3 me = transform.position;
            float r = Plugin.TrapRadius.Value;
            foreach (Character c in Character.GetAllCharacters())
            {
                if (c == null || c.IsPlayer() || c.IsDead() || c.IsTamed() || c.IsBoss() || !Leash.Eligible(c)) continue;
                if (Tiers.Animal(c) > Tier) continue;   // too strong for this trap: it steps right through
                // the nearest part of its body, not its middle: a long wolf stepping over the trap is caught too
                Collider body = c.GetComponent<Collider>();
                bool solid = body != null && !(body is MeshCollider && !((MeshCollider)body).convex);
                Vector3 d = (solid ? body.ClosestPoint(me) : c.transform.position) - me;
                if (Mathf.Abs(d.y) > 1.2f) continue;
                d.y = 0f;
                if (d.magnitude > r) continue;
                Spring(c);
                return;
            }
        }

        private void Spring(Character c)
        {
            ZDO z = _nv.GetZDO();
            z.Set(ArmedKey, 0);
            z.Set(CaughtKey, c.GetZDOID());
            z.Set(BloodKey, 1);
            ZNetView anv = Leash.View(c);
            if (anv != null && anv.IsValid()) anv.InvokeRPC(ZNetView.Everybody, RpcTrapped, new object[] { _nv.GetZDO().m_uid });
            Player p = Player.m_localPlayer;
            if (p != null && Vector3.Distance(p.transform.position, transform.position) < 12f) Effects.Shake(1.2f);
            Plugin.Debug("[trap] sprung on " + c.GetHoverName() + " (" + c.name + ")");
        }

        // the caught animal's own blood (its hit effect) where the jaws bite, plus the game's blood spray
        private void Splash()
        {
            if (_nv.GetZDO().GetInt(BloodKey, 0) != 1) return;   // snapped shut on nothing
            Vector3 at = transform.position + Vector3.up * 0.15f;
            GameObject go = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(Caught) : null;
            Character c = go != null ? go.GetComponent<Character>() : null;
            if (c != null && c.m_hitEffects != null) c.m_hitEffects.Create(at, Quaternion.LookRotation(Vector3.up), null, 1f);
            GameObject spray = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab("vfx_BloodHit") : null;
            if (spray != null) Instantiate(spray, at, Quaternion.LookRotation(Vector3.up));
            Plugin.Debug("[trap] blood: " + (c != null ? c.name + " hit effect" : "no animal") + (spray != null ? " + vfx_BloodHit" : ""));
        }

        // the animal this trap holds right now, if it is loaded and still held
        private Character Holding()
        {
            ZDOID id = Caught;
            if (id.IsNone() || ZNetScene.instance == null) return null;
            GameObject go = ZNetScene.instance.FindInstance(id);
            Character c = go != null ? go.GetComponent<Character>() : null;
            if (c == null || c.IsDead()) return null;
            ZNetView nv = Leash.View(c);
            if (nv == null || !nv.IsValid() || nv.GetZDO().GetZDOID(HeldByKey) != _nv.GetZDO().m_uid) return null;
            return c;
        }

        // ---------------------------------------------------------------- the animal's side

        // every modded game registers this on the animals; only the one moving the animal acts on it
        internal static void RegisterOn(Character c)
        {
            ZNetView nv = Leash.View(c);
            if (nv == null || nv.GetZDO() == null) return;
            nv.Register<ZDOID>(RpcTrapped, (sender, trap) =>
            {
                if (!nv.IsValid() || !nv.IsOwner()) return;
                nv.GetZDO().Set(HeldByKey, trap);
                nv.GetZDO().Set(HeldUntilKey, (float)ZNet.instance.GetTimeSeconds() + Plugin.TrapHoldSeconds.Value);
            });
        }

        private static readonly Dictionary<Character, float> LastSeenHeld = new Dictionary<Character, float>();

        internal static bool IsHeld(Character c)
        {
            ZNetView nv = Leash.View(c);
            if (nv == null || !nv.IsValid() || ZNet.instance == null) return false;
            ZDO z = nv.GetZDO();
            bool held = !z.GetZDOID(HeldByKey).IsNone() && (float)ZNet.instance.GetTimeSeconds() < z.GetFloat(HeldUntilKey, 0f);
            if (held)
            {
                if (LastSeenHeld.Count > 200) LastSeenHeld.Clear();
                LastSeenHeld[c] = Time.time;
            }
            return held;
        }

        // held now, or pulled free a moment ago: a loop thrown just before the trap let go still counts
        internal static bool HeldOrJustFreed(Character c)
        {
            if (IsHeld(c)) return true;
            float t;
            return c != null && LastSeenHeld.TryGetValue(c, out t) && Time.time - t < 1.5f;
        }

        private static void Free(ZNetView nv)
        {
            nv.GetZDO().Set(HeldByKey, ZDOID.None);
            nv.GetZDO().Set(HeldUntilKey, 0f);
        }

        // after the animal's own AI has run: keep it on the trap
        [HarmonyPatch(typeof(MonsterAI), "UpdateAI"), HarmonyPostfix]
        private static void MonsterHold(MonsterAI __instance) { Hold(__instance); }

        [HarmonyPatch(typeof(AnimalAI), "UpdateAI"), HarmonyPostfix]
        private static void AnimalHold(AnimalAI __instance) { Hold(__instance); }

        private static void Hold(BaseAI ai)
        {
            if (!Plugin.Enabled.Value) return;
            Character c = Fast.AIChar(ai);
            if (c == null) return;
            ZNetView nv = Fast.AIView(ai);
            if (nv == null || !nv.IsValid() || !nv.IsOwner()) return;
            ZDO z = nv.GetZDO();
            ZDOID trapId = z.GetZDOID(HeldByKey);
            if (trapId.IsNone()) return;
            if (!IsHeld(c) || Leash.Holder(nv) != 0L || c.IsTamed() || c.IsDead()) { Free(nv); Plugin.Debug("[trap] " + c.GetHoverName() + " is free"); return; }
            GameObject tgo = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(trapId) : null;
            if (tgo == null) { Free(nv); return; }
            ai.StopMoving();
            Vector3 at = tgo.transform.position;
            Vector3 d = c.transform.position - at;
            d.y = 0f;
            if (d.magnitude > 0.35f)
            {
                Vector3 pos = new Vector3(at.x, c.transform.position.y, at.z) + d.normalized * 0.3f;
                Rigidbody body = Leash.Body(c);
                if (body != null) { body.position = pos; body.velocity = Vector3.zero; }
                else c.transform.position = pos;
            }
        }

        // ---------------------------------------------------------------- hover and E

        public string GetHoverName() { return Names[Tier]; }
        public float GetHoverOffset() { return 0.4f; }

        public string GetHoverText()
        {
            if (_nv == null || !_nv.IsValid()) return "";
            string state;
            if (Armed) state = "<color=#d8b080>Set</color>";
            else
            {
                Character c = Holding();
                state = c != null ? "<color=#ff9060>Holding " + c.GetHoverName() + "</color>" : "<color=#a0a0a0>Sprung</color>";
            }
            string s = Names[Tier] + " (" + state + ")\n[<color=yellow><b>$KEY_Use</b></color>] Pick up";
            if (!Armed && Holding() == null) s += "\n[<color=yellow><b>$KEY_AltPlace + $KEY_Use</b></color>] Set it again";
            return Localization.instance.Localize(s);
        }

        public bool Interact(Humanoid user, bool hold, bool alt)
        {
            if (hold || _nv == null || !_nv.IsValid()) return false;
            Player p = user as Player;
            if (p == null) return false;
            if (alt && !Armed && Holding() == null)
            {
                if (!_nv.IsOwner()) _nv.ClaimOwnership();
                _nv.GetZDO().Set(ArmedKey, 1);
                _nv.GetZDO().Set(CaughtKey, ZDOID.None);
                _nv.GetZDO().Set(BloodKey, 0);
                p.Message(MessageHud.MessageType.Center, "Trap set again.");
                return true;
            }
            // pick it up: whatever it holds is free
            if (!_nv.IsOwner()) _nv.ClaimOwnership();
            if (!p.GetInventory().AddItem(Set.Prefab(Tier), 1))
            {
                p.Message(MessageHud.MessageType.Center, "Your bag is full.");
                return false;
            }
            ZNetScene.instance.Destroy(gameObject);
            p.Message(MessageHud.MessageType.Center, "You pick up the trap.");
            return true;
        }

        public bool UseItem(Humanoid user, ItemDrop.ItemData item) { return false; }

        private static Sprite _icon;

        // flint jaws, open, drawn in code
        private static Sprite Icon()
        {
            Color wood = new Color(0.55f, 0.38f, 0.2f), woodDark = new Color(0.3f, 0.2f, 0.1f), flint = new Color(0.55f, 0.58f, 0.62f), flintDark = new Color(0.28f, 0.3f, 0.34f);
            if (_icon != null) return _icon;
            return _icon = Items.MakeIcon("Beastkeeper_TrapIcon", p =>
            {
                Color col = Color.clear;
                Vector2 c = new Vector2(32f, 32f);
                Vector2 d = p - c;
                d.y *= 1.6f;
                float r = d.magnitude;
                if (r < 13f) col = Color.Lerp(woodDark, wood, 0.5f + 0.5f * Mathf.Sin(p.x * 0.7f) * 0.5f);   // the plate
                if (Mathf.Abs(r - 22f) < 3.5f)                                                              // the jaws ring
                {
                    float ang = Mathf.Atan2(d.y, d.x);
                    float tooth = Mathf.Abs(Mathf.Sin(ang * 9f));
                    col = Color.Lerp(flintDark, flint, tooth);
                    if (r - 22f > 2f && tooth < 0.3f) col = Color.clear;
                }
                if (Mathf.Abs(p.y - 32f) < 2f && Mathf.Abs(p.x - 32f) < 27f && r >= 13f && col.a == 0f) col = woodDark;   // the spring bar
                return col;
            });
        }
    }
}
