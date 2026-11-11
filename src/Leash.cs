using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Beastkeeper
{
    // Animals on a lasso.
    //
    // The animal's ZDO gets "Beastkeeper_leash" = the holder's player ID, so everyone with the mod draws the rope.
    // The holder's game takes the animal over (ownership) and runs its AI itself while it is on the rope:
    // it walks after the holder, keeps calm (never turns hostile), and during a catch it fights the rope.
    // Tamed animals are also told to follow the holder the game's own way, so a friend without the mod who
    // ends up simulating the animal still sees it follow.
    // The rope gets taut past SlowDistance (the holder walks slower away from the animal) and slips off at
    // BreakDistance.
    [HarmonyPatch]
    internal static class Leash
    {
        internal static readonly int Key = "Beastkeeper_leash".GetStableHashCode();
        private static readonly int FollowKey = "Beastkeeper_follow".GetStableHashCode();

        private static readonly FieldInfo TameAI = AccessTools.Field(typeof(Tameable), "m_monsterAI");
        private static readonly FieldInfo TameChar = AccessTools.Field(typeof(Tameable), "m_character");
        private static readonly FieldInfo TameView = AccessTools.Field(typeof(Tameable), "m_nview");
        private static readonly FieldInfo AIView = AccessTools.Field(typeof(BaseAI), "m_nview");
        private static readonly FieldInfo AIChar = AccessTools.Field(typeof(BaseAI), "m_character");
        private static readonly FieldInfo CharBody = AccessTools.Field(typeof(Character), "m_body");
        private static readonly FieldInfo TargetCreature = AccessTools.Field(typeof(MonsterAI), "m_targetCreature");
        private static readonly FieldInfo SemanChar = AccessTools.Field(typeof(SEMan), "m_character");
        private static readonly MethodInfo SetAlerted = AccessTools.Method(typeof(BaseAI), "SetAlerted");
        private static readonly MethodInfo MoveToM = AccessTools.Method(typeof(BaseAI), "MoveTo", new[] { typeof(float), typeof(Vector3), typeof(float), typeof(bool) });

        private static readonly List<Character> _mine = new List<Character>();
        private static readonly List<ZDOID> _order = new List<ZDOID>();   // the animals you caught, oldest first
        private static float _nextCheck;

        // Just caught after a fight: the rope can't slip for a few seconds while the animal comes in close.
        private static readonly Dictionary<Character, float> _grace = new Dictionary<Character, float>();
        internal static void Grace(Character c) { if (c != null) _grace[c] = Time.time + 6f; }
        private static bool InGrace(Character c)
        {
            float until;
            return _grace.TryGetValue(c, out until) && Time.time < until;
        }

        // ------------------------------------------------------------ helpers

        internal static ZNetView View(Component c) { return c != null ? c.GetComponent<ZNetView>() : null; }

        internal static long Holder(ZNetView nv)
        {
            if (nv == null || !nv.IsValid()) return 0L;
            return nv.GetZDO().GetLong(Key, 0L);
        }

        internal static Player FindPlayer(long id)
        {
            if (id == 0L) return null;
            foreach (Player p in Player.GetAllPlayers())
                if (p != null && p.GetPlayerID() == id) return p;
            return null;
        }

        // Animals: the ones the game can tame, the ones with animal AI (deer, hares...), the "animal" faction,
        // and the ExtraAnimals list (the boar, the neck and others use the same body type as the monsters
        // with weapons, so the type alone can't tell them apart).
        internal static bool Eligible(Character c)
        {
            if (c == null || c.IsPlayer() || c.IsBoss() || c.IsDead()) return false;
            string prefab = Utils.GetPrefabName(c.gameObject);
            if (InList(Plugin.NotAnimals.Value, prefab)) return false;
            if (c.GetComponent<Tameable>() != null || c.GetComponent<AnimalAI>() != null) return true;
            if (c.m_faction == Character.Faction.AnimalsVeg) return true;
            return InList(Plugin.ExtraAnimals.Value, prefab);
        }

        private static bool InList(string list, string prefab)
        {
            if (string.IsNullOrEmpty(list)) return false;
            foreach (string s in list.Split(','))
                if (string.Equals(s.Trim(), prefab, System.StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        internal static bool Noticed(Character c)
        {
            BaseAI ai = c.GetComponent<BaseAI>();
            // only the red mark (alerted) counts; the yellow one (it's aware of something) still lets you catch it.
            // An animal held by a trap can't run, so it can be caught anyway.
            return ai != null && ai.IsAlerted() && !Trap.HeldOrJustFreed(c) && !Sea.Harpooned(c);
        }

        internal static bool IsMine(Character c)
        {
            Player p = Player.m_localPlayer;
            return p != null && c != null && Holder(View(c)) == p.GetPlayerID();
        }

        internal static int CountMine(long me)
        {
            int n = 0;
            foreach (Character c in Character.GetAllCharacters())
                if (c != null && !c.IsPlayer() && Holder(View(c)) == me) n++;
            return n;
        }

        internal static void Calm(BaseAI ai)
        {
            if (ai == null) return;
            if (ai.IsAlerted() && SetAlerted != null) SetAlerted.Invoke(ai, new object[] { false });
            MonsterAI mai = ai as MonsterAI;
            if (mai != null && TargetCreature != null) TargetCreature.SetValue(mai, null);
        }

        internal static void MoveTo(BaseAI ai, float dt, Vector3 point, bool run)
        {
            if (MoveToM != null) MoveToM.Invoke(ai, new object[] { dt, point, 0f, run });
        }

        internal static Rigidbody Body(Character c)
        {
            return c != null && CharBody != null ? CharBody.GetValue(c) as Rigidbody : null;
        }

        private static void Say(Player p, string msg)
        {
            if (p != null) p.Message(MessageHud.MessageType.Center, msg);
        }

        // ------------------------------------------------------------ the lasso lands on an animal

        internal static void OnHit(Character c, Player p)
        {
            ZNetView nv = View(c);
            if (nv == null || !nv.IsValid()) return;
            string name = c.GetHoverName();
            long me = p.GetPlayerID();
            long holder = Holder(nv);

            if (holder == me)
            {
                Say(p, name + " is already on your lasso. Right-click to let go.");
                return;
            }
            if (holder != 0L)
            {
                Player other = FindPlayer(holder);
                if (other != null && Vector3.Distance(other.transform.position, c.transform.position) < 40f)
                {
                    Say(p, name + " is on " + other.GetPlayerName() + "'s lasso.");
                    return;
                }
            }
            if (!Eligible(c))
            {
                Plugin.Debug("[leash] not an animal: " + Utils.GetPrefabName(c.gameObject) + " (faction " + c.m_faction + ", " + c.GetType().Name + ")");
                Say(p, "The lasso won't hold " + name + ".");
                return;
            }
            if (CountMine(me) >= Plugin.MaxLeashed.Value)
            {
                Say(p, "You can't hold more than " + Plugin.MaxLeashed.Value + " animals at once.");
                return;
            }
            if (c.IsTamed())
            {
                CatchTamed(c, p);
                Say(p, name + " is on your lasso.");
                return;
            }
            if (!Plugin.CatchWild.Value) { Say(p, "The lasso only holds tamed animals."); return; }
            int need = Tiers.Animal(c), have = Lasso.Set.Equipped(p);
            if (have < need)
            {
                Say(p, "Your " + Tiers.Line[Mathf.Max(1, have)] + " Lasso is too weak for " + name + ": you need " + Tiers.Need("Lasso", need) + ".");
                Plugin.Debug("[leash] " + name + " is tier " + need + ", lasso tier " + have);
                return;
            }
            if (Noticed(c))
            {
                Say(p, name + " saw you coming and shook the rope off.");
                Plugin.Debug("[leash] " + name + " had noticed the player, no catch");
                return;
            }
            Tug.Start(c, p);
        }

        internal static System.Collections.Generic.List<Character> Mine() { return new List<Character>(_mine); }

        internal static void CatchTamed(Character c, Player p)
        {
            ZNetView nv = View(c);
            nv.ClaimOwnership();
            ZDO zdo = nv.GetZDO();
            zdo.Set(Key, p.GetPlayerID());
            Caught(c);
            Follow(c, p);
            Plugin.Debug("[leash] lassoed tamed " + c.GetHoverName() + " (" + c.name + ")");
        }

        internal static void Caught(Character c)
        {
            ZDOID id = c.GetZDOID();
            _order.Remove(id);
            _order.Add(id);
        }

        // Right-click with the lasso: the animal under the crosshair if it is yours, else the last one caught.
        internal static Character LetGoOne(Player p, Character aimed)
        {
            Character pick = aimed != null && IsMine(aimed) ? aimed : null;
            for (int i = _order.Count - 1; pick == null && i >= 0; i--)
            {
                ZDOID id = _order[i];
                Character c = _mine.Find(m => m != null && m.GetZDOID() == id);
                if (c != null) pick = c;
            }
            if (pick == null && _mine.Count > 0) pick = _mine[_mine.Count - 1];
            if (pick == null) return null;
            Release(pick, true);
            Say(p, "You let go of " + pick.GetHoverName() + ".");
            return pick;
        }

        // tamed animals also follow the holder the game's own way
        internal static void Follow(Character c, Player p)
        {
            ZNetView nv = View(c);
            MonsterAI ai = c.GetComponent<MonsterAI>();
            if (ai != null)
            {
                ai.ResetPatrolPoint();
                ai.SetFollowTarget(p.gameObject);
            }
            nv.GetZDO().Set(ZDOVars.s_follow, p.GetPlayerName());
            nv.GetZDO().Set(FollowKey, 1);
        }

        // Takes the rope off. Wild animals that were not caught yet run off.
        internal static void Release(Character c, bool calm)
        {
            ZNetView nv = View(c);
            if (nv == null || !nv.IsValid()) return;
            if (!nv.IsOwner()) nv.ClaimOwnership();
            ZDO zdo = nv.GetZDO();
            zdo.Set(Key, 0L);
            if (zdo.GetInt(FollowKey, 0) == 1)
            {
                zdo.Set(FollowKey, 0);
                MonsterAI ai = c.GetComponent<MonsterAI>();
                if (ai != null) { ai.SetFollowTarget(null); ai.SetPatrolPoint(); }
                zdo.Set(ZDOVars.s_follow, "");
            }
            if (c.IsTamed())
            {
                BaseAI tai = c.GetComponent<BaseAI>();
                if (tai != null) tai.SetPatrolPoint();   // a tamed animal stays around where you let go
                Home.Set(c, c.transform.position);
            }
            if (!calm && !c.IsTamed())
            {
                BaseAI ai = c.GetComponent<BaseAI>();
                if (ai != null) ai.Alert();
            }
            Tug.Forget(c);
            _mine.Remove(c);
            _order.Remove(c.GetZDOID());
            Plugin.Debug("[leash] rope off " + c.GetHoverName() + (calm ? "" : " (it runs off)"));
        }

        // ------------------------------------------------------------ the holder's side

        internal static void Update()
        {
            Player p = Player.m_localPlayer;
            if (p == null) { _mine.Clear(); return; }
            if (Time.time < _nextCheck) return;
            _nextCheck = Time.time + 0.25f;

            long me = p.GetPlayerID();
            string myName = p.GetPlayerName();
            _mine.Clear();
            if (_grace.Count > 0)
            {
                List<Character> old = new List<Character>();
                foreach (KeyValuePair<Character, float> kv in _grace) if (kv.Key == null || Time.time > kv.Value) old.Add(kv.Key);
                foreach (Character o in old) _grace.Remove(o);
            }
            foreach (Character c in Character.GetAllCharacters())
            {
                if (c == null || c.IsPlayer()) continue;
                ZNetView nv = View(c);
                if (Holder(nv) != me) continue;
                if (c.IsDead()) continue;
                ZDO zdo = nv.GetZDO();
                float dist = Vector3.Distance(c.transform.position, p.transform.position);

                if (!Tug.Is(c) && !InGrace(c) && dist > Plugin.BreakDistance.Value)
                {
                    Release(c, c.IsTamed());
                    Effects.Shake(2f);
                    Say(p, "The rope slipped off " + c.GetHoverName() + ".");
                    continue;
                }
                if (c.IsTamed())
                {
                    if (zdo.GetInt(FollowKey, 0) == 0 && c.GetComponent<Tameable>() != null)
                    {
                        if (!nv.IsOwner()) nv.ClaimOwnership();
                        Follow(c, p);                       // just got tamed while on the rope
                        Say(p, c.GetHoverName() + " is tamed and follows you.");
                    }
                    else if (zdo.GetString(ZDOVars.s_follow, "") != myName)
                    {
                        Release(c, true);                   // someone pressed E: it stays
                        continue;
                    }
                }
                if (!nv.IsOwner() && dist < 60f)
                {
                    nv.ClaimOwnership();
                    Plugin.Debug("[leash] took over " + c.GetHoverName() + " to lead it");
                }
                _mine.Add(c);
            }
        }

        // The rope gets taut past SlowDistance: walking away from the animal gets slower.
        private static float _nextStrain;
        [HarmonyPatch(typeof(SEMan), "ApplyStatusEffectSpeedMods"), HarmonyPostfix]
        private static void Taut(SEMan __instance, ref float speed, Vector3 dir)
        {
            if (!Plugin.Enabled.Value || _mine.Count == 0 || SemanChar == null) return;
            Player p = Player.m_localPlayer;
            if (p == null || SemanChar.GetValue(__instance) as Character != p) return;
            float slow = Plugin.SlowDistance.Value, brk = Mathf.Max(slow + 0.1f, Plugin.BreakDistance.Value);
            Vector3 move = new Vector3(dir.x, 0f, dir.z);
            if (move.sqrMagnitude < 0.0001f) return;
            move.Normalize();
            float factor = 1f;
            foreach (Character c in _mine)
            {
                if (c == null || Tug.Is(c)) continue;
                Vector3 away = p.transform.position - c.transform.position;
                away.y = 0f;
                float d = away.magnitude;
                if (d <= slow) continue;
                float dot = Vector3.Dot(move, away / d);
                if (dot <= 0f) continue;
                // eases in fast so you feel the rope early, and is at its heaviest right before it slips
                float k = Mathf.Sqrt(Mathf.Clamp01((d - slow) / (brk - slow)));
                factor = Mathf.Min(factor, Mathf.Lerp(1f, Mathf.Lerp(1f, Plugin.SlowestSpeed.Value, k), dot));
            }
            speed *= factor;
            // close to the limit the rope jerks at your arm
            if (factor < 0.45f && Time.time > _nextStrain)
            {
                _nextStrain = Time.time + 0.6f;
                Effects.Shake(0.35f);
            }
        }

        // ------------------------------------------------------------ the animal's side (runs where it is simulated)

        [HarmonyPatch(typeof(MonsterAI), "UpdateAI"), HarmonyPrefix]
        private static bool MonsterUpdate(MonsterAI __instance, float dt, ref bool __result) { return Drive(__instance, dt, ref __result); }

        [HarmonyPatch(typeof(AnimalAI), "UpdateAI"), HarmonyPrefix]
        private static bool AnimalUpdate(AnimalAI __instance, float dt, ref bool __result) { return Drive(__instance, dt, ref __result); }

        private static bool Drive(BaseAI ai, float dt, ref bool result)
        {
            if (!Plugin.Enabled.Value) return true;
            Player p = Player.m_localPlayer;
            if (p == null) return true;
            ZNetView nv = AIView != null ? AIView.GetValue(ai) as ZNetView : null;
            if (Holder(nv) != p.GetPlayerID()) return true;
            Character c = AIChar != null ? AIChar.GetValue(ai) as Character : null;
            if (c == null || c.IsDead()) return true;

            if (!Tug.Is(c) && Defend.Active(ai, c, null)) return true;   // an enemy near: it fights (see Defend)
            result = true;
            Calm(ai);
            if (Tug.Is(c)) { Tug.Drive(ai, c, p, dt); return false; }

            float dist = Vector3.Distance(c.transform.position, p.transform.position);
            if (dist < Plugin.RopeLength.Value) { ai.StopMoving(); Squeeze.Forget(c); }
            else
            {
                bool run = dist > Plugin.RunDistance.Value;
                MoveTo(ai, dt, p.transform.position, run);
                Squeeze.Check(ai, c, p.transform, Plugin.RopeLength.Value + 1f, run);   // a door in the way
            }
            return false;
        }

        // ------------------------------------------------------------ no interacting during a catch

        [HarmonyPatch(typeof(Player), "Interact"), HarmonyPrefix]
        private static bool Interact(Player __instance)
        {
            return !(Plugin.Enabled.Value && __instance == Player.m_localPlayer && Tug.Active);
        }

        // ------------------------------------------------------------ hover text

        [HarmonyPatch(typeof(Tameable), "GetHoverText"), HarmonyPostfix]
        private static void TameHover(Tameable __instance, ref string __result)
        {
            Character c = TameChar != null ? TameChar.GetValue(__instance) as Character : null;
            __result = Mount.WithoutGameKeys(c, __result) + HoverLine(c) + Tie.HoverLine(c) + Mount.HoverLine(c) + Favorite.HoverLine(c) + TrapLine(c);
        }

        [HarmonyPatch(typeof(Character), "GetHoverText"), HarmonyPostfix]
        private static void CharHover(Character __instance, ref string __result)
        {
            if (__instance.GetComponent<Tameable>() != null) return;   // the line comes from Tameable
            if (Plugin.Enabled.Value && __instance.IsTamed() && Halter.Wears(__instance))
                __result += "\n<color=#d8b080>Tamed</color> (wears a halter)" + Rename.HoverLine(__instance);
            __result += HoverLine(__instance) + Tie.HoverLine(__instance) + Mount.HoverLine(__instance) + Favorite.HoverLine(__instance) + TrapLine(__instance);
        }

        private static string TrapLine(Character c)
        {
            return Plugin.Enabled.Value && c != null && Trap.IsHeld(c) ? "\n<color=#ff9060>Caught in a trap</color>" : "";
        }

        private static string HoverLine(Character c)
        {
            Player p = Player.m_localPlayer;
            if (!Plugin.Enabled.Value || p == null || c == null) return "";
            long holder = Holder(View(c));
            if (holder == 0L) return "";
            if (holder == p.GetPlayerID() && Halter.NeedsHalter(c) && !Tug.Is(c))
                return "\n<color=#d8b080>On your lasso</color>\nPut a <color=yellow>Halter</color> on it to tame it";
            if (holder == p.GetPlayerID())
                return "\n<color=#d8b080>On your lasso</color>" + " (right-click with the lasso to let go)";
            Player other = FindPlayer(holder);
            return "\n<color=#d8b080>On " + (other != null ? other.GetPlayerName() + "'s" : "someone's") + " lasso</color>";
        }

        // ------------------------------------------------------------ every animal gets a rope drawer

        [HarmonyPatch(typeof(Character), "Awake"), HarmonyPostfix]
        private static void CharacterAwake(Character __instance)
        {
            if (__instance.IsPlayer()) return;
            if (__instance.GetComponent<Tameable>() == null && __instance.GetComponent<AnimalAI>() == null && !Eligible(__instance)) return;
            if (__instance.GetComponent<Rope>() == null) __instance.gameObject.AddComponent<Rope>();
            if (__instance.GetComponent<Mount>() == null) __instance.gameObject.AddComponent<Mount>();
            Trap.RegisterOn(__instance);
            Feed.RegisterOn(__instance);
            Bags.Attach(__instance);
            Breed.Attach(__instance);
        }
    }
}
