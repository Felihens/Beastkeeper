using System;
using UnityEngine;

namespace Beastkeeper
{
    // Favorite mount: look at a tamed animal that wears (or once wore) a saddle and press CallKey (U) to make it
    // your favorite; press again on it to undo. What it is gets written down on your character, one per world:
    // the species, its stars, its saddle, its name and its halter.
    // CallKey anywhere else brings it to your side, wherever it is (from far away when you host or play alone;
    // on a dedicated server that needs the mod on the server too).
    // If it dies, the game that saw it die tells its player. It can then only come back at a Beast Totem: look
    // at the totem and press CallKey, paying 5 food (what it eats first, else food you eat). It comes back
    // tamed, with its stars, name and halter, but without a saddle and with empty bags.
    // Copies left over from before (an older called one) go away when they show up near you (bags spill first).
    [HarmonyLib.HarmonyPatch]
    internal static class Favorite
    {
        private static readonly int IdKey = "Beastkeeper_favId".GetStableHashCode();
        private static readonly int LastSaddleKey = "Beastkeeper_lastSaddle".GetStableHashCode();
        private static readonly int CopyOfKey = "Beastkeeper_copyOf".GetStableHashCode();
        private static readonly int CopyGenKey = "Beastkeeper_copyGen".GetStableHashCode();
        private static readonly int CopyByKey = "Beastkeeper_copyBy".GetStableHashCode();
        private static readonly int OurNameKey = "Beastkeeper_name".GetStableHashCode();   // Rename.cs
        private const float Cooldown = 3f;

        private static float _lastCall = -100f;
        private static float _nextSweep;

        private class Template
        {
            public long Id;
            public string Prefab;
            public int Level;
            public int Saddle;
            public bool Halter;
            public string TamedName;   // the game's own name (animals it can tame)
            public string OurName;     // ours (halter-tamed animals)

            public string Save()
            {
                return string.Join("|", new[] { Id.ToString(), Prefab, Level.ToString(), Saddle.ToString(), Halter ? "1" : "0", Clean(TamedName), Clean(OurName) });
            }

            public static Template Load(string v)
            {
                if (string.IsNullOrEmpty(v)) return null;
                string[] s = v.Split('|');
                if (s.Length < 7) return null;
                Template t = new Template();
                if (!long.TryParse(s[0], out t.Id) || !int.TryParse(s[2], out t.Level) || !int.TryParse(s[3], out t.Saddle)) return null;
                t.Prefab = s[1];
                t.Halter = s[4] == "1";
                t.TamedName = s[5];
                t.OurName = s[6];
                return t;
            }

            public string Name()
            {
                if (!string.IsNullOrEmpty(OurName)) return OurName;
                if (!string.IsNullOrEmpty(TamedName)) return TamedName;
                GameObject g = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(Prefab) : null;
                Character c = g != null ? g.GetComponent<Character>() : null;
                return c != null && Localization.instance != null ? Localization.instance.Localize(c.m_name) : Prefab;
            }

            private static string Clean(string s) { return (s ?? "").Replace("|", ""); }
        }

        private static string WorldKey(string what)
        {
            long world = ZNet.instance != null ? ZNet.instance.GetWorldUID() : 0L;
            return "Beastkeeper_" + what + "_" + world;
        }

        private static Template Get(Player p)
        {
            string v;
            return p != null && p.m_customData.TryGetValue(WorldKey("favorite"), out v) ? Template.Load(v) : null;
        }

        private static long Gen(Player p)
        {
            string v;
            long g;
            return p != null && p.m_customData.TryGetValue(WorldKey("favoriteGen"), out v) && long.TryParse(v, out g) ? g : 0L;
        }

        private static ZDO Zdo(Character c)
        {
            ZNetView nv = Leash.View(c);
            return nv != null && nv.IsValid() ? nv.GetZDO() : null;
        }

        // the marked animal and every mount called from it share its id
        internal static bool IsFavorite(Character c)
        {
            ZDO z = Zdo(c);
            Template t = Get(Player.m_localPlayer);
            return z != null && t != null && z.GetLong(IdKey, 0L) == t.Id;
        }

        internal static bool IsCalled(Character c)
        {
            ZDO z = Zdo(c);
            return z != null && z.GetLong(CopyOfKey, 0L) != 0L;
        }

        // the saddle went on: remember it, so the animal can be a favorite even with the saddle off again
        internal static void MarkSaddled(ZNetView nv, int tier)
        {
            if (nv != null && nv.IsValid()) nv.GetZDO().Set(LastSaddleKey, tier);
        }

        private static int SaddleFor(Character c)
        {
            int now = Mount.SaddleTier(c);
            if (now > 0) return now;
            ZDO z = Zdo(c);
            return z != null ? Mathf.Clamp(z.GetInt(LastSaddleKey, 0), 0, Tiers.Saddles) : 0;
        }

        internal static bool CanBeFavorite(Character c)
        {
            if (c == null || c.IsPlayer() || c.IsDead() || !c.IsTamed() || Sea.IsSeaMount(c) || c.GetComponent<Mount>() == null) return false;
            return Mount.Rideable(c) && SaddleFor(c) > 0;
        }

        private static string Key() { return Plugin.CallKey.Value.ToString(); }

        internal static string HoverLine(Character c)
        {
            if (!Plugin.Enabled.Value || Plugin.CallKey.Value.MainKey == KeyCode.None || !CanBeFavorite(c)) return "";
            if (IsFavorite(c))
                return "\n<color=#ffd040>★ Favorite mount</color>" + (IsCalled(c) ? " (called)" : "") + "  [<color=yellow><b>" + Key() + "</b></color>] Remove";
            return "\n[<color=yellow><b>" + Key() + "</b></color>] Make favorite mount";
        }

        // ------------------------------------------------------------ the key

        internal static void Update()
        {
            Player p = Player.m_localPlayer;
            if (p == null) return;
            Register();
            CheckAsk(p);
            if (Time.time > _nextSweep) { _nextSweep = Time.time + 2f; SendOldAway(p); }

            if (Plugin.CallKey.Value.MainKey == KeyCode.None || !Plugin.CallKey.Value.IsDown()) return;
            if (Chat.instance != null && Chat.instance.HasFocus()) return;
            if (Console.IsVisible() || TextInput.IsVisible() || Menu.IsVisible() || InventoryGui.IsVisible() || Minimap.IsOpen()) return;
            if (p.IsDead() || p.InCutscene() || p.IsTeleporting()) return;

            GameObject hover = p.GetHoverObject();
            Totem totem = hover != null ? hover.GetComponentInParent<Totem>() : null;
            if (totem != null) { Revive(p, totem); return; }
            Character c = hover != null ? hover.GetComponentInParent<Character>() : null;
            if (c != null && !c.IsPlayer() && c.IsTamed() && c.GetComponent<Mount>() != null && !Sea.IsSeaMount(c))
            {
                Toggle(p, c);
                return;
            }
            Call(p);
        }

        private static void Toggle(Player p, Character c)
        {
            string name = c.GetHoverName();
            if (IsFavorite(c))
            {
                p.m_customData.Remove(WorldKey("favorite"));
                p.Message(MessageHud.MessageType.Center, name + " is no longer your favorite mount.");
                return;
            }
            if (!CanBeFavorite(c))
            {
                p.Message(MessageHud.MessageType.Center, Mount.Rideable(c) ? "Put a saddle on " + name + " first." : name + " can't be ridden.");
                return;
            }
            ZNetView nv = Leash.View(c);
            ZDO z = nv.GetZDO();
            long id = z.GetLong(IdKey, 0L);
            if (id == 0L)
            {
                if (!nv.IsOwner()) nv.ClaimOwnership();
                System.Random r = new System.Random();
                while (id == 0L) id = ((long)r.Next() << 31) ^ r.Next() ^ DateTime.UtcNow.Ticks;
                z.Set(IdKey, id);
            }
            Template t = new Template
            {
                Id = id,
                Prefab = Utils.GetPrefabName(c.gameObject),
                Level = c.GetLevel(),
                Saddle = SaddleFor(c),
                Halter = Halter.Wears(c),
                TamedName = z.GetString(ZDOVars.s_tamedName, ""),
                OurName = z.GetString(OurNameKey, ""),
            };
            p.m_customData[WorldKey("favorite")] = t.Save();
            p.Message(MessageHud.MessageType.Center, name + " is now your favorite mount. Press " + Key() + " to call it.");
            Plugin.Debug("[favorite] " + t.Save());
        }

        private static void Call(Player p)
        {
            Template t = Get(p);
            if (t == null)
            {
                p.Message(MessageHud.MessageType.Center, "No favorite mount yet: look at a saddled animal and press " + Key() + ".");
                return;
            }
            if (Mount.RiddenCharacter() != null || p.IsAttached()) return;
            if (Time.time - _lastCall < Cooldown) return;
            GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(t.Prefab) : null;
            if (prefab == null || prefab.GetComponent<Character>() == null)
            {
                p.Message(MessageHud.MessageType.Center, "Your favorite mount (" + t.Prefab + ") doesn't exist in this game.");
                return;
            }
            _lastCall = Time.time;

            long gen = DateTime.UtcNow.Ticks;
            Vector3 at = Spot(p);
            Quaternion facing = Quaternion.LookRotation(Flat(p.transform.forward));

            // 1) it's loaded near here: bring it over
            Character here = FindLoaded(t.Id);
            if (here != null)
            {
                long rider = Mount.RiderOf(here);
                if (rider != 0L && rider != p.GetPlayerID() && Leash.FindPlayer(rider) != null)
                {
                    p.Message(MessageHud.MessageType.Center, "Someone is riding " + here.GetHoverName() + ".");
                    return;
                }
                SetGen(p, gen);
                SetDead(p, false);
                MoveLoaded(here, at, facing, gen);
                SendOldAway(p);
                Arrived(p, here.GetHoverName());
                return;
            }

            // 2) it's somewhere else in the world and this game knows it (always so when hosting or alone)
            ZDO far = FindZdo(t.Id);
            if (far != null)
            {
                long rider = far.GetLong(Mount.RiderKey, 0L);
                if (rider != 0L && rider != p.GetPlayerID() && Leash.FindPlayer(rider) != null)
                {
                    p.Message(MessageHud.MessageType.Center, "Someone is riding " + t.Name() + ".");
                    return;
                }
                SetGen(p, gen);
                SetDead(p, false);
                MoveFar(far, at, facing, ZDOMan.GetSessionID(), gen);
                SendOldAway(p);
                Arrived(p, t.Name());
                return;
            }

            // 3) it died: only a totem brings it back
            if (Dead(p)) { SayDied(p, t); return; }

            // 4) on a dedicated server only the server knows the whole world: ask it
            if (ZNet.instance != null && !ZNet.instance.IsServer() && ZRoutedRpc.instance != null)
            {
                _ask = new Ask { Template = t, At = at, Facing = facing, Gen = gen, Until = Time.time + 4f };
                ZRoutedRpc.instance.InvokeRoutedRPC(RpcCall, new object[] { t.Id, at, facing, gen });
                return;
            }

            // 5) this game knows the whole world and it isn't there: it died (maybe while you were away)
            SetDead(p, true);
            SayDied(p, t);
        }

        // ------------------------------------------------------------ death and the totem

        private const string RpcDied = "Beastkeeper_FavoriteDied";
        private const int ReviveFood = 5;

        private static bool Dead(Player p)
        {
            string v;
            return p != null && p.m_customData.TryGetValue(WorldKey("favoriteDead"), out v) && v == "1";
        }

        private static void SetDead(Player p, bool dead)
        {
            if (dead) p.m_customData[WorldKey("favoriteDead")] = "1";
            else p.m_customData.Remove(WorldKey("favoriteDead"));
        }

        private static void SayDied(Player p, Template t)
        {
            p.Message(MessageHud.MessageType.Center, t.Name() + " died. Look at a Beast Totem and press " + Key() + " to bring it back.");
        }

        // The game that owns the animal sees it die and tells everyone; its player writes it down.
        [HarmonyLib.HarmonyPatch(typeof(Character), "OnDeath"), HarmonyLib.HarmonyPrefix]
        private static void OnDeath(Character __instance)
        {
            if (!Plugin.Enabled.Value || __instance == null || __instance.IsPlayer() || ZRoutedRpc.instance == null) return;
            ZDO z = Zdo(__instance);
            if (z == null || !Leash.View(__instance).IsOwner()) return;
            long id = z.GetLong(IdKey, 0L);
            if (id == 0L) return;
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcDied, new object[] { id, z.GetLong(CopyOfKey, 0L), z.GetLong(CopyGenKey, 0L) });
        }

        private static void OnDied(long sender, long id, long copyOf, long gen)
        {
            Player p = Player.m_localPlayer;
            Template t = Get(p);
            if (t == null || t.Id != id) return;
            if (copyOf != 0L && gen != Gen(p)) return;   // an old leftover copy, not the current one
            SetDead(p, true);
            p.Message(MessageHud.MessageType.Center, t.Name() + " died! A Beast Totem can bring it back (" + Key() + ").");
            Plugin.Debug("[favorite] the favorite mount died");
        }

        internal static string TotemLine()
        {
            Player p = Player.m_localPlayer;
            Template t = Get(p);
            if (!Plugin.Enabled.Value || t == null || Plugin.CallKey.Value.MainKey == KeyCode.None) return "";
            if (!Dead(p) && (FindLoaded(t.Id) != null || FindZdo(t.Id) != null)) return "";
            return "\n[<color=yellow><b>" + Key() + "</b></color>] Bring " + t.Name() + " back (" + ReviveFood + " food)";
        }

        private static void Revive(Player p, Totem totem)
        {
            Template t = Get(p);
            if (t == null) { p.Message(MessageHud.MessageType.Center, "You have no favorite mount."); return; }
            // found somewhere this game knows: it's alive. Not found: bring it back, even if no one told us it died
            // (the death message can miss you, e.g. when a friend's game or the server owned it at that moment).
            if (FindLoaded(t.Id) != null || FindZdo(t.Id) != null)
            {
                SetDead(p, false);
                p.Message(MessageHud.MessageType.Center, t.Name() + " is alive: press " + Key() + " anywhere to call it.");
                return;
            }
            Plugin.Debug("[favorite] totem: " + t.Name() + " not found (marked dead: " + Dead(p) + "), bringing it back");
            GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(t.Prefab) : null;
            if (prefab == null || prefab.GetComponent<Character>() == null) return;
            string food;
            if (!PayFood(p, totem.transform.position, prefab, out food))
            {
                p.Message(MessageHud.MessageType.Center, "Bring " + ReviveFood + " food to bring " + t.Name() + " back (what it eats, or food you eat), in your bag or in chests nearby.");
                return;
            }
            long gen = DateTime.UtcNow.Ticks;
            // a new id: if the old one was alive after all, somewhere far, it stays a normal tamed animal, not a second favorite
            long oldId = t.Id;
            System.Random r = new System.Random();
            t.Id = 0L;
            while (t.Id == 0L || t.Id == oldId) t.Id = ((long)r.Next() << 31) ^ r.Next() ^ gen;
            p.m_customData[WorldKey("favorite")] = t.Save();
            SetGen(p, gen);
            SetDead(p, false);
            Vector3 at = SpotNear(totem.transform.position, Flat(p.transform.position - totem.transform.position));
            Spawn(p, t, prefab, at, Quaternion.LookRotation(Flat(p.transform.position - at)), gen);
            p.Message(MessageHud.MessageType.Center, t.Name() + " is back (" + ReviveFood + " " + food + "). Saddle it again to ride.");
            Effects.Shake(0.6f);
        }

        private const float ChestRange = 30f;
        private static readonly System.Reflection.MethodInfo CheckAccess = HarmonyLib.AccessTools.Method(typeof(Container), "CheckAccess");

        // 5 of one food, from your bag first and then from chests near the totem:
        // what the animal eats first, else anything you can eat
        private static bool PayFood(Player p, Vector3 near, GameObject prefab, out string what)
        {
            what = "";
            System.Collections.Generic.List<Inventory> sources = new System.Collections.Generic.List<Inventory> { p.GetInventory() };
            System.Collections.Generic.List<Container> chests = new System.Collections.Generic.List<Container>();
            foreach (Container ct in UnityEngine.Object.FindObjectsOfType<Container>())
            {
                if (ct == null || ct.GetInventory() == null || ct.name == "Beastkeeper_Bags" || ct.GetComponent<TombStone>() != null) continue;
                if (Vector3.Distance(ct.transform.position, near) > ChestRange) continue;
                if (ct.IsInUse() || (CheckAccess != null && !(bool)CheckAccess.Invoke(ct, new object[] { p.GetPlayerID() }))) continue;
                ZNetView nv = ct.GetComponent<ZNetView>();
                if (nv == null || !nv.IsValid()) continue;
                chests.Add(ct);
                sources.Add(ct.GetInventory());
            }

            MonsterAI ai = prefab.GetComponent<MonsterAI>();
            System.Collections.Generic.Dictionary<string, int> diet = new System.Collections.Generic.Dictionary<string, int>();
            System.Collections.Generic.Dictionary<string, int> any = new System.Collections.Generic.Dictionary<string, int>();
            foreach (Inventory inv in sources)
                foreach (ItemDrop.ItemData it in inv.GetAllItems())
                {
                    ItemDrop.ItemData.SharedData sh = it.m_shared;
                    bool eats = ai != null && ai.m_consumeItems != null && ai.m_consumeItems.Exists(f => f != null && f.m_itemData.m_shared.m_name == sh.m_name);
                    bool food = sh.m_itemType == ItemDrop.ItemData.ItemType.Consumable && (sh.m_food > 0f || sh.m_foodStamina > 0f || sh.m_foodEitr > 0f);
                    if (eats) diet[sh.m_name] = (diet.ContainsKey(sh.m_name) ? diet[sh.m_name] : 0) + it.m_stack;
                    else if (food) any[sh.m_name] = (any.ContainsKey(sh.m_name) ? any[sh.m_name] : 0) + it.m_stack;
                }
            string pick = null;
            foreach (var kv in diet) if (kv.Value >= ReviveFood) { pick = kv.Key; break; }
            if (pick == null) foreach (var kv in any) if (kv.Value >= ReviveFood) { pick = kv.Key; break; }
            if (pick == null) return false;

            int left = ReviveFood;
            for (int i = 0; i < sources.Count && left > 0; i++)
            {
                int have = sources[i].CountItems(pick);
                if (have <= 0) continue;
                int take = Mathf.Min(have, left);
                if (i > 0)
                {
                    ZNetView nv = chests[i - 1].GetComponent<ZNetView>();
                    if (!nv.IsOwner()) nv.ClaimOwnership();
                }
                sources[i].RemoveItem(pick, take);
                left -= take;
            }
            what = Localization.instance != null ? Localization.instance.Localize(pick) : pick;
            Plugin.Debug("[favorite] paid " + ReviveFood + " " + pick + " (" + chests.Count + " chests in reach)");
            return true;
        }

        private static void SetGen(Player p, long gen) { p.m_customData[WorldKey("favoriteGen")] = gen.ToString(); }

        private static void Arrived(Player p, string name)
        {
            p.Message(MessageHud.MessageType.Center, name + " comes to you.");
            Effects.Shake(0.3f);
        }

        private static void Spawn(Player p, Template t, GameObject prefab, Vector3 at, Quaternion facing, long gen)
        {
            GameObject go = UnityEngine.Object.Instantiate(prefab, at, facing);
            Character c = go.GetComponent<Character>();
            ZNetView nv = Leash.View(c);
            if (c == null || nv == null || !nv.IsValid()) { p.Message(MessageHud.MessageType.Center, "Your favorite mount couldn't come."); return; }

            ZDO z = nv.GetZDO();
            if (t.Level > 1) c.SetLevel(t.Level);
            z.Set(IdKey, t.Id);
            z.Set(CopyOfKey, t.Id);
            z.Set(CopyGenKey, gen);
            z.Set(CopyByKey, p.GetPlayerID());
            z.Set(LastSaddleKey, t.Saddle);   // it comes back without a saddle, but stays a favorite
            if (t.Halter) z.Set(Halter.Key, 1);
            if (!string.IsNullOrEmpty(t.TamedName)) z.Set(ZDOVars.s_tamedName, t.TamedName);
            if (!string.IsNullOrEmpty(t.OurName)) z.Set(OurNameKey, t.OurName);
            c.SetTamed(true);
            if (t.Halter) Home.Set(c, at);
            Plugin.Debug("[favorite] brought back a " + t.Prefab + " (stars " + t.Level + ") at a totem");
        }

        // ------------------------------------------------------------ finding it

        // the marked animal itself first, else a mount called before
        private static Character FindLoaded(long id)
        {
            Character copy = null;
            foreach (Character c in Character.GetAllCharacters())
            {
                ZDO z = Zdo(c);
                if (z == null || c.IsDead() || z.GetLong(IdKey, 0L) != id) continue;
                if (z.GetLong(CopyOfKey, 0L) == 0L) return c;
                if (copy == null) copy = c;
            }
            return copy;
        }

        private static ZDO FindZdo(long id)
        {
            if (ZDOMan.instance == null || AllZdos == null) return null;
            System.Collections.Generic.Dictionary<ZDOID, ZDO> all = AllZdos.GetValue(ZDOMan.instance) as System.Collections.Generic.Dictionary<ZDOID, ZDO>;
            if (all == null) return null;
            ZDO copy = null;
            foreach (ZDO z in all.Values)
            {
                if (z == null || !z.IsValid() || z.GetLong(IdKey, 0L) != id) continue;
                if (z.GetLong(CopyOfKey, 0L) == 0L) return z;
                if (copy == null) copy = z;
            }
            return copy;
        }

        // the animal is here: it's untied, and if it had a home it now lives here
        private static void MoveLoaded(Character c, Vector3 at, Quaternion facing, long gen)
        {
            ZNetView nv = Leash.View(c);
            if (!nv.IsOwner()) nv.ClaimOwnership();
            ZDO z = nv.GetZDO();
            if (z.GetLong(CopyOfKey, 0L) != 0L) z.Set(CopyGenKey, gen);   // a called mount stays the current one
            Tie.Untie(c);
            if (z.GetVec3(Home.HomeKey, Vector3.zero) != Vector3.zero) Home.Set(c, at);
            c.transform.position = at;
            c.transform.rotation = facing;
            Rigidbody rb = c.GetComponent<Rigidbody>();
            if (rb != null) { rb.position = at; rb.linearVelocity = Vector3.zero; }
            z.SetPosition(at);
            z.SetRotation(facing);
            Plugin.Debug("[favorite] brought " + c.name + " over (it was loaded)");
        }

        // the animal is far away: move its saved data; the game then builds it next to the player
        private static void MoveFar(ZDO z, Vector3 at, Quaternion facing, long newOwner, long gen)
        {
            z.SetOwner(ZDOMan.GetSessionID());
            if (z.GetLong(CopyOfKey, 0L) != 0L) z.Set(CopyGenKey, gen);
            Tie.Clear(z);
            if (z.GetVec3(Home.HomeKey, Vector3.zero) != Vector3.zero) z.Set(Home.HomeKey, at);
            z.SetPosition(at);
            z.SetRotation(facing);
            if (newOwner != ZDOMan.GetSessionID()) z.SetOwner(newOwner);
            Plugin.Debug("[favorite] brought it over from far away (" + z.m_uid + ")");
        }

        // ------------------------------------------------------------ asking a dedicated server

        private class Ask { public Template Template; public Vector3 At; public Quaternion Facing; public long Gen; public float Until; }
        private static Ask _ask;
        private const string RpcCall = "Beastkeeper_CallMount";
        private const string RpcAnswer = "Beastkeeper_CallMountAnswer";
        private static readonly System.Reflection.FieldInfo AllZdos = HarmonyLib.AccessTools.Field(typeof(ZDOMan), "m_objectsByID");
        private static ZRoutedRpc _registeredOn;

        private static void Register()
        {
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (rpc == null || rpc == _registeredOn) return;
            rpc.Register<long, Vector3, Quaternion, long>(RpcCall, new RoutedMethod<long, Vector3, Quaternion, long>.Method(OnCall));
            rpc.Register<bool>(RpcAnswer, new Action<long, bool>(OnAnswer));
            rpc.Register<long, long, long>(RpcDied, new Action<long, long, long, long>(OnDied));
            _registeredOn = rpc;
        }

        // on the server
        private static void OnCall(long sender, long id, Vector3 at, Quaternion facing, long gen)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            ZDO z = FindZdo(id);
            if (z != null)
            {
                MoveFar(z, at, facing, sender, gen);
                ZDOMan.instance.ForceSendZDO(sender, z.m_uid);
            }
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcAnswer, new object[] { z != null });
        }

        // back on the player's game
        private static void OnAnswer(long sender, bool found)
        {
            Ask a = _ask;
            _ask = null;
            Player p = Player.m_localPlayer;
            if (a == null || p == null) return;
            SetGen(p, a.Gen);
            SendOldAway(p);
            if (found) { SetDead(p, false); Arrived(p, a.Template.Name()); return; }
            SetDead(p, true);   // the server knows the whole world: it is gone
            SayDied(p, a.Template);
        }

        // no answer: the server doesn't have the mod, so there's no way to know if it's still alive somewhere
        private static void CheckAsk(Player p)
        {
            if (_ask == null || Time.time < _ask.Until) return;
            string name = _ask.Template.Name();
            _ask = null;
            p.Message(MessageHud.MessageType.Center, name + " isn't nearby and the server can't look for it. If it died, look at a Beast Totem and press " + Key() + ".");
        }

        // A called mount that isn't the current one goes away (its bags spill first, so nothing is lost):
        // loaded ones at once, others when they show up near you later. The marked animal itself never goes.
        private static void SendOldAway(Player p)
        {
            long me = p.GetPlayerID(), gen = Gen(p);
            foreach (Character c in Character.GetAllCharacters().ToArray())
            {
                ZDO z = Zdo(c);
                if (z == null || c.IsDead() || z.GetLong(CopyOfKey, 0L) == 0L || z.GetLong(CopyByKey, 0L) != me) continue;
                if (z.GetLong(CopyGenKey, 0L) == gen) continue;   // the current one stays
                if (Mount.IsRidden(c)) continue;
                long rider = Mount.RiderOf(c);
                if (rider != 0L && Leash.FindPlayer(rider) != null) continue;   // someone is on it: it goes later
                ZNetView nv = Leash.View(c);
                if (!nv.IsOwner()) nv.ClaimOwnership();
                Bags.SpillNow(c);
                Tie.Untie(c);
                Plugin.Debug("[favorite] sending the old called mount away (" + c.name + ")");
                ZNetScene.instance.Destroy(c.gameObject);
            }
        }

        private static Vector3 Flat(Vector3 v) { v.y = 0f; return v.sqrMagnitude < 0.001f ? Vector3.forward : v.normalized; }

        // a free spot on the ground beside you: left, right, behind, in front
        private static Vector3 Spot(Player p) { return Around(p.transform.position, Flat(p.transform.forward), 0f); }

        // in front of a totem (the side you stand on), else its sides, else behind it
        private static Vector3 SpotNear(Vector3 totem, Vector3 towardYou) { return Around(totem, Vector3.Cross(Vector3.up, towardYou), 0.6f); }

        private static Vector3 Around(Vector3 root, Vector3 fwd, float skip)
        {
            int mask = LayerMask.GetMask("terrain", "Default", "static_solid", "piece");
            Vector3 right = Flat(Vector3.Cross(Vector3.up, fwd));
            fwd = Flat(fwd);
            foreach (Vector3 dir in new[] { -right, right, -fwd, fwd })
            {
                Vector3 at = root + dir * 2.5f;
                if (Physics.Linecast(root + dir * skip + Vector3.up, at + Vector3.up, mask, QueryTriggerInteraction.Ignore)) continue;
                if (Physics.CheckSphere(at + Vector3.up * 1.3f, 0.6f, mask, QueryTriggerInteraction.Ignore)) continue;
                RaycastHit hit;
                if (Physics.Raycast(at + Vector3.up * 3f, Vector3.down, out hit, 8f, mask, QueryTriggerInteraction.Ignore)) at.y = hit.point.y + 0.1f;
                else if (ZoneSystem.instance != null) at.y = ZoneSystem.instance.GetGroundHeight(at) + 0.1f;
                return at;
            }
            return root - fwd * 2f;
        }
    }
}
