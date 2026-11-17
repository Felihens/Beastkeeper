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
    // at the totem and press CallKey, paying 3 of its kind's trophies, 5 from tier 3 on (no trophy: 5 food). It comes back
    // tamed, with its stars, name and halter, but without a saddle and with empty bags.
    // Copies left over from before (an older called one) go away when they show up near you (bags spill first).
    // A Beast Totem is also a safe: press E on it for a window where you store your favorite (it vanishes from the
    // world, its bags spill first) and take it out again at any totem, saddle and all. Only the favorite can be stored,
    // and one animal can be the favorite of one player only (the animal remembers who).
    [HarmonyLib.HarmonyPatch]
    internal static class Favorite
    {
        private static readonly int IdKey = "Beastkeeper_favId".GetStableHashCode();
        private static readonly int LastSaddleKey = "Beastkeeper_lastSaddle".GetStableHashCode();
        private static readonly int CopyOfKey = "Beastkeeper_copyOf".GetStableHashCode();
        private static readonly int CopyGenKey = "Beastkeeper_copyGen".GetStableHashCode();
        private static readonly int CopyByKey = "Beastkeeper_copyBy".GetStableHashCode();
        private static readonly int OurNameKey = "Beastkeeper_name".GetStableHashCode();   // Rename.cs
        private static readonly int FavOwnerKey = "Beastkeeper_favOwner".GetStableHashCode();         // the player whose favorite it is
        private static readonly int FavOwnerNameKey = "Beastkeeper_favOwnerName".GetStableHashCode();
        private const float StoreRange = 25f;   // how close to the totem the favorite must be to be stored
        private const float Cooldown = 3f;

        private static float _lastCall = -100f;
        private static float _nextSweep;

        internal static readonly int HpShareKey = "Beastkeeper_hpShare".GetStableHashCode();   // health to come back with (share of its full health)

        private class Template
        {
            public long Id;
            public string Prefab;
            public int Level;
            public int Saddle;
            public bool Halter;
            public string TamedName;   // the game's own name (animals it can tame)
            public string OurName;     // ours (halter-tamed animals)
            public string Look = "";   // what makes it look and fight as it does: a drake's element and glow, a hare's colours

            public string Save()
            {
                return string.Join("|", new[] { Id.ToString(), Prefab, Level.ToString(), Saddle.ToString(), Halter ? "1" : "0", Clean(TamedName), Clean(OurName), Clean(Look) });
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
                t.Look = s.Length > 7 ? s[7] : "";
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

            private static string Clean(string s) { return (s ?? "").Replace("|", "").Replace("\n", "").Replace("\r", ""); }
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

        internal static bool Has(Player p) { return Get(p) != null; }

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
            {
                string order = Orders.Name(Orders.Of(c));
                return "\n<color=#ffd040>★ Favorite mount</color>" + (IsCalled(c) ? " (called)" : "") + (order != "" ? " - " + order : "") + "  [<color=yellow><b>" + Key() + "</b></color>] Remove"
                    + (Plugin.OrderKey.Value.MainKey != KeyCode.None ? "  [<color=yellow><b>hold " + Plugin.OrderKey.Value + "</b></color>] Orders" : "");
            }
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
            if (Console.IsVisible() || TextInput.IsVisible() || Menu.IsVisible() || InventoryGui.IsVisible() || Minimap.IsOpen() || TotemWindow.Open) return;
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
                SetStored(p, false);
                ReleaseLock(c, p);
                p.Message(MessageHud.MessageType.Center, name + " is no longer your favorite mount.");
                return;
            }
            if (!CanBeFavorite(c))
            {
                p.Message(MessageHud.MessageType.Center, Mount.Rideable(c) ? "Put a saddle on " + name + " first." : name + " can't be ridden.");
                return;
            }
            ZNetView nv = Leash.View(c);
            string moved = null;
            if (Stored(p))
            {
                // Your favorite rests in a totem (user, 2026-11-17): choosing another one moves it to the totems' other
                // animals, still yours, to take out at any totem like them; refused only when that place is full.
                Template was = Get(p);
                System.Collections.Generic.List<Template> stable = StableList(p);
                if (was != null && PerKind(stable, was.Prefab) >= StableMax)
                {
                    p.Message(MessageHud.MessageType.Center, "Your favorite rests in a Beast Totem and the totems already hold " + StableMax + " other animals of its kind: take one out first.");
                    return;
                }
                if (was != null)
                {
                    was.Id = 0L;   // a plain stored animal now: no favorite mark when it comes out
                    stable.Add(was);
                    SaveStable(p, stable);
                    moved = was.Name();
                    Plugin.Debug("[favorite] the stored favorite " + was.Save() + " moved to the other animals (" + PerKind(stable, was.Prefab) + "/" + StableMax + " of its kind)");
                }
                SetStored(p, false);
            }
            ZDO z = nv.GetZDO();
            // one animal, one favorite: if another player already chose it, it's taken
            long owner = z.GetLong(FavOwnerKey, 0L);
            if (owner != 0L && owner != p.GetPlayerID())
            {
                string who = z.GetString(FavOwnerNameKey, "");
                p.Message(MessageHud.MessageType.Center, name + " is already " + (who.Length > 0 ? who + "'s" : "another player's") + " favorite mount.");
                return;
            }
            long id = z.GetLong(IdKey, 0L);
            if (id == 0L)
            {
                if (!nv.IsOwner()) nv.ClaimOwnership();
                System.Random r = new System.Random();
                while (id == 0L) id = ((long)r.Next() << 31) ^ r.Next() ^ DateTime.UtcNow.Ticks;
                z.Set(IdKey, id);
            }
            // the animal that was your favorite before is free for others again
            Template old = Get(p);
            if (old != null && old.Id != id)
            {
                Character oc = FindLoaded(old.Id);
                if (oc != null) ReleaseLock(oc, p);
                else { ZDO oz = FindZdo(old.Id); if (oz != null && oz.GetLong(FavOwnerKey, 0L) == p.GetPlayerID()) { oz.SetOwner(ZDOMan.GetSessionID()); oz.Set(FavOwnerKey, 0L); } }
            }
            if (!nv.IsOwner()) nv.ClaimOwnership();
            z.Set(FavOwnerKey, p.GetPlayerID());
            z.Set(FavOwnerNameKey, p.GetPlayerName());
            Template t = Capture(c, id);
            p.m_customData[WorldKey("favorite")] = t.Save();
            p.Message(MessageHud.MessageType.Center, name + " is now your favorite mount. Press " + Key() + " to call it."
                + (moved != null ? "\n" + moved + " stays in the Beast Totems with your other animals." : ""));
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
            if (Stored(p)) { p.Message(MessageHud.MessageType.Center, t.Name() + " rests inside a Beast Totem: press E on a totem to take it out."); return; }
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

        private static readonly System.Reflection.FieldInfo LastHitF = HarmonyLib.AccessTools.Field(typeof(Character), "m_lastHit");

        // The game that owns the animal sees it die and tells everyone; its player writes it down.
        [HarmonyLib.HarmonyPatch(typeof(Character), "OnDeath"), HarmonyLib.HarmonyPrefix]
        private static void OnDeath(Character __instance)
        {
            if (!Plugin.Enabled.Value || __instance == null || __instance.IsPlayer()) return;
            if (__instance.IsTamed())
            {
                // any tamed animal: where and by whom, so a missing one can be traced in the log
                HitData last = LastHitF != null ? LastHitF.GetValue(__instance) as HitData : null;
                Character by = last != null ? last.GetAttacker() : null;
                Plugin.Log.LogInfo("[died] tamed " + __instance.GetHoverName() + " (" + __instance.name + ") at " + __instance.transform.position.ToString("F0")
                    + (by != null ? ", killed by " + by.GetHoverName() + " (" + by.name + ")" : ""));
            }
            if (ZRoutedRpc.instance == null) return;
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
            if (Stored(p)) return "\n[<color=yellow><b>" + Key() + "</b></color>] Take " + t.Name() + " out";
            if (!Dead(p) && (FindLoaded(t.Id) != null || FindZdo(t.Id) != null)) return "";
            return "\n[<color=yellow><b>" + Key() + "</b></color>] Bring " + t.Name() + " back (" + PriceOf(t.Prefab) + ")";
        }

        internal static void Revive(Player p, Totem totem)
        {
            Template t = Get(p);
            if (t == null) { p.Message(MessageHud.MessageType.Center, "You have no favorite mount."); return; }
            if (Stored(p)) { TakeOutAt(p, totem); return; }
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
                p.Message(MessageHud.MessageType.Center, TrophyOf(prefab) != null
                    ? "Bring " + PriceOf(t.Prefab) + " to bring " + t.Name() + " back, in your bag or in chests nearby."
                    : "Bring " + ReviveFood + " food to bring " + t.Name() + " back (what it eats, or food you eat), in your bag or in chests nearby.");
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
            p.Message(MessageHud.MessageType.Center, t.Name() + " is back (" + food + "). Saddle it again to ride.");
            Effects.Shake(0.6f);
        }

        private const float ChestRange = 30f;
        private static readonly System.Reflection.MethodInfo CheckAccess = HarmonyLib.AccessTools.Method(typeof(Container), "CheckAccess");

        // Bringing a favorite back costs trophies of its own kind (user, 2026-11-17), from your bag first and then
        // from chests near the totem. Animals that drop no trophy (hens, young ones...) still cost 5 of one food:
        // what the animal eats first, else anything you can eat.
        // 3 trophies; 5 from tier 3 on (wolf, drake, lox, hare, asksvin...) (user, 2026-11-17)
        private static int TrophiesFor(string prefab) { return Tiers.OfPrefab(prefab) >= 3 ? 5 : 3; }

        internal static string TrophyOf(GameObject prefab)
        {
            CharacterDrop cd = prefab != null ? prefab.GetComponent<CharacterDrop>() : null;
            if (cd == null || cd.m_drops == null) return null;
            foreach (CharacterDrop.Drop d in cd.m_drops)
            {
                ItemDrop id = d != null && d.m_prefab != null ? d.m_prefab.GetComponent<ItemDrop>() : null;
                if (id != null && id.m_itemData.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Trophy) return id.m_itemData.m_shared.m_name;
            }
            return null;
        }

        // "3 Boar trophy" / "5 food"
        internal static string PriceOf(string prefabName)
        {
            GameObject g = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabName) : null;
            string trophy = TrophyOf(g);
            if (trophy == null) return ReviveFood + " food";
            return TrophiesFor(prefabName) + " " + (Localization.instance != null ? Localization.instance.Localize(trophy) : trophy);
        }

        internal static string FavoritePrice(Player p) { Template t = Get(p); return t != null ? PriceOf(t.Prefab) : ""; }

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
            int need = ReviveFood;
            string trophy = TrophyOf(prefab);
            if (trophy != null)
            {
                need = TrophiesFor(Utils.GetPrefabName(prefab));
                int have = 0;
                foreach (Inventory inv in sources) foreach (ItemDrop.ItemData it in inv.GetAllItems()) if (it.m_shared.m_name == trophy) have += it.m_stack;
                if (have < need) return false;
                pick = trophy;
            }
            else
            {
                foreach (var kv in diet) if (kv.Value >= ReviveFood) { pick = kv.Key; break; }
                if (pick == null) foreach (var kv in any) if (kv.Value >= ReviveFood) { pick = kv.Key; break; }
                if (pick == null) return false;
            }

            int left = need;
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
            what = need + " " + (Localization.instance != null ? Localization.instance.Localize(pick) : pick);
            Plugin.Debug("[favorite] paid " + need + " " + pick + " (" + chests.Count + " chests in reach)");
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
            Spawn(p, t, prefab, at, facing, gen, false);
        }

        private static void Spawn(Player p, Template t, GameObject prefab, Vector3 at, Quaternion facing, long gen, bool keepSaddle)
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
            z.Set(LastSaddleKey, t.Saddle);   // it comes back without a saddle (unless it was stored), but stays a favorite
            int wears = SaddleOn(t);
            if (keepSaddle && wears > 0) z.Set(Mount.SaddleKey, wears);
            z.Set(FavOwnerKey, p.GetPlayerID());
            z.Set(FavOwnerNameKey, p.GetPlayerName());
            if (t.Halter) z.Set(Halter.Key, 1);
            if (!string.IsNullOrEmpty(t.TamedName)) z.Set(ZDOVars.s_tamedName, t.TamedName);
            if (!string.IsNullOrEmpty(t.OurName)) z.Set(OurNameKey, t.OurName);
            SetLook(z, t.Look);
            if (!keepSaddle) z.Set(HpShareKey, 0f);   // brought back to life: at full health (only a stored one keeps its wounds)
            c.SetTamed(true);
            if (t.Halter) Home.Set(c, at);
            Plugin.Debug("[favorite] brought back a " + t.Prefab + " (stars " + t.Level + ") at a totem");
        }

        // ------------------------------------------------------------ the totem as a safe

        private static Template Capture(Character c, long id)
        {
            ZDO z = Zdo(c);
            return new Template
            {
                Id = id,
                Prefab = Utils.GetPrefabName(c.gameObject),
                Level = c.GetLevel(),
                Saddle = SaddleFor(c),
                Halter = Halter.Wears(c),
                TamedName = z != null ? z.GetString(ZDOVars.s_tamedName, "") : "",
                OurName = z != null ? z.GetString(OurNameKey, "") : "",
                Look = LookOf(z) + ",hp=" + Mathf.Clamp(c.GetHealth() / Mathf.Max(1f, c.GetMaxHealth()), 0.01f, 1f).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                    + ",sd=" + Mount.SaddleTier(c),   // the saddle it wears right now (Saddle may be one it wore before)
            };
        }

        // "e=2,g=1" (drake element, glow) / "h=0.53,s=0.6,v=1,hg=1" (hare hue, strength, lightness, glow)
        private static string LookOf(ZDO z)
        {
            if (z == null) return "";
            System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
            System.Collections.Generic.List<string> parts = new System.Collections.Generic.List<string>();
            int el = z.GetInt(Drake.ElementKey, 0);
            if (el != 0) parts.Add("e=" + el);
            if (z.GetInt(Drake.GlowKey, 0) == 1) parts.Add("g=1");
            float hue = z.GetFloat(Breed.ColorKey, -1f);
            if (hue >= 0f)
            {
                parts.Add("h=" + hue.ToString("0.####", inv));
                parts.Add("s=" + z.GetFloat(Breed.SatKey, 0.55f).ToString("0.####", inv));
                parts.Add("v=" + z.GetFloat(Breed.ValKey, 1f).ToString("0.####", inv));
            }
            if (z.GetInt(Breed.GlowKey, 0) == 1) parts.Add("hg=1");
            return string.Join(",", parts.ToArray());
        }

        private static void SetLook(ZDO z, string look)
        {
            if (z == null || string.IsNullOrEmpty(look)) return;
            System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
            foreach (string part in look.Split(','))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string k = part.Substring(0, eq), v = part.Substring(eq + 1);
                float f;
                int n;
                if (k == "e" && int.TryParse(v, out n)) z.Set(Drake.ElementKey, n);
                else if (k == "g" && v == "1") z.Set(Drake.GlowKey, 1);
                else if (k == "hp" && float.TryParse(v, System.Globalization.NumberStyles.Float, inv, out f)) z.Set(HpShareKey, f);
                else if (k == "hg" && v == "1") z.Set(Breed.GlowKey, 1);
                else if (k == "h" && float.TryParse(v, System.Globalization.NumberStyles.Float, inv, out f)) z.Set(Breed.ColorKey, f);
                else if (k == "s" && float.TryParse(v, System.Globalization.NumberStyles.Float, inv, out f)) z.Set(Breed.SatKey, f);
                else if (k == "v" && float.TryParse(v, System.Globalization.NumberStyles.Float, inv, out f)) z.Set(Breed.ValKey, f);
            }
        }

        // the saddle it wore when it was stored ("sd=" in Look); older saves only know Saddle
        private static int SaddleOn(Template t)
        {
            if (t.Look != null)
                foreach (string part in t.Look.Split(','))
                {
                    int n;
                    if (part.StartsWith("sd=") && int.TryParse(part.Substring(3), out n)) return Mathf.Clamp(n, 0, Tiers.Saddles);
                }
            return t.Saddle;
        }

        // the animal stops being yours alone: others may choose it (only when this player's mark is on it)
        private static void ReleaseLock(Character c, Player p)
        {
            ZDO z = Zdo(c);
            if (z == null || z.GetLong(FavOwnerKey, 0L) != p.GetPlayerID()) return;
            ZNetView nv = Leash.View(c);
            if (!nv.IsOwner()) nv.ClaimOwnership();
            z.Set(FavOwnerKey, 0L);
            z.Set(FavOwnerNameKey, "");
        }

        internal static bool Stored(Player p)
        {
            string v;
            return p != null && p.m_customData.TryGetValue(WorldKey("favoriteStored"), out v) && v == "1";
        }

        private static void SetStored(Player p, bool on)
        {
            if (on) p.m_customData[WorldKey("favoriteStored")] = "1";
            else p.m_customData.Remove(WorldKey("favoriteStored"));
        }

        // what the totem window shows: 0 no favorite, 1 stored, 2 here (can be stored), 3 somewhere else, 4 gone
        internal static int WindowState(Player p, Totem totem, out string name, out string detail)
        {
            name = ""; detail = "";
            Template t = Get(p);
            if (t == null) return 0;
            name = t.Name();
            System.Collections.Generic.List<string> d = new System.Collections.Generic.List<string>();
            LookTags(t.Look, d);
            if (t.Level > 1) d.Add((t.Level - 1) + (t.Level == 2 ? " star" : " stars"));
            if (t.Saddle > 0) d.Add("saddled");
            if (t.Halter) d.Add("wears a halter");
            detail = string.Join(", ", d.ToArray());
            if (Stored(p)) return 1;
            Character c = FindLoaded(t.Id);
            if (c != null && !c.IsDead() && Vector3.Distance(c.transform.position, totem.transform.position) <= StoreRange) return 2;
            if (c != null || FindZdo(t.Id) != null) return 3;
            return 4;
        }

        // ------------------------------------------------------------ renaming from the totem window (user, 2026-11-17)

        // the game's own name for animals it tames itself, ours for the others
        private static void SetTemplateName(Template t, string name)
        {
            GameObject g = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(t.Prefab) : null;
            if (g != null && g.GetComponent<Tameable>() != null) { t.TamedName = name; t.OurName = ""; }
            else { t.OurName = name; t.TamedName = ""; }
        }

        private static string CleanName(string name)
        {
            name = (name ?? "").Replace("|", "").Replace("\n", " ").Replace("\r", "").Trim();
            return name.Length > 20 ? name.Substring(0, 20) : name;
        }

        internal static void RenameFavorite(Player p, string name)
        {
            Template t = Get(p);
            if (t == null) return;
            name = CleanName(name);
            Character c = FindLoaded(t.Id);
            if (c != null) SetLiveName(c, name);
            SetTemplateName(t, name);
            p.m_customData[WorldKey("favorite")] = t.Save();
            Plugin.Debug("[rename] favorite is now '" + name + "'");
        }

        internal static void RenameStored(Player p, int index, string name)
        {
            System.Collections.Generic.List<Template> list = StableList(p);
            if (index < 0 || index >= list.Count) return;
            SetTemplateName(list[index], CleanName(name));
            SaveStable(p, list);
            Plugin.Debug("[rename] stored animal " + index + " is now '" + name + "'");
        }

        internal static void RenameAnimal(Player p, Character c, string name)
        {
            if (c == null) return;
            name = CleanName(name);
            SetLiveName(c, name);
            Template t = Get(p);
            ZDO z = Zdo(c);
            if (t != null && z != null && z.GetLong(IdKey, 0L) == t.Id) { SetTemplateName(t, name); p.m_customData[WorldKey("favorite")] = t.Save(); }
        }

        private static void SetLiveName(Character c, string name)
        {
            Tameable tm = c.GetComponent<Tameable>();
            if (tm != null) tm.SetText(name);
            else Rename.Set(c, name);
        }

        internal static int ReviveCost { get { return ReviveFood; } }
        internal static float StoreRangeShown { get { return StoreRange; } }

        // put the favorite away: it leaves the world, its bags spill on the ground, and it waits in the totem
        internal static void StoreAt(Player p, Totem totem)
        {
            Template t = Get(p);
            if (t == null || Stored(p)) return;
            Character c = FindLoaded(t.Id);
            if (c == null || c.IsDead() || Vector3.Distance(c.transform.position, totem.transform.position) > StoreRange)
            {
                p.Message(MessageHud.MessageType.Center, t.Name() + " has to be near the totem.");
                return;
            }
            long rider = Mount.RiderOf(c);
            if (Mount.IsRidden(c) || (rider != 0L && Leash.FindPlayer(rider) != null)) { p.Message(MessageHud.MessageType.Center, "Someone is riding " + t.Name() + "."); return; }
            ZNetView nv = Leash.View(c);
            if (nv == null || !nv.IsValid()) return;
            if (!nv.IsOwner()) nv.ClaimOwnership();
            p.m_customData[WorldKey("favorite")] = Capture(c, t.Id).Save();   // stars, saddle, names as they are now
            Bags.SpillNow(c);
            Tie.Untie(c);
            SetStored(p, true);
            SetDead(p, false);
            string name = t.Name();
            Plugin.Debug("[favorite] stored " + name + " in a totem");
            ZNetScene.instance.Destroy(c.gameObject);
            Effects.Shake(0.4f);
            p.Message(MessageHud.MessageType.Center, name + " rests inside the Beast Totem.");
        }

        // take it out at this totem, saddle and all
        internal static void TakeOutAt(Player p, Totem totem)
        {
            Template t = Get(p);
            if (t == null || !Stored(p)) return;
            GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(t.Prefab) : null;
            if (prefab == null || prefab.GetComponent<Character>() == null)
            {
                p.Message(MessageHud.MessageType.Center, "Your favorite mount (" + t.Prefab + ") doesn't exist in this game.");
                return;
            }
            long gen = DateTime.UtcNow.Ticks;
            SetGen(p, gen);
            SetStored(p, false);
            SetDead(p, false);
            Vector3 at = SpotNear(totem.transform.position, Flat(p.transform.position - totem.transform.position));
            Spawn(p, t, prefab, at, Quaternion.LookRotation(Flat(p.transform.position - at)), gen, true);
            p.Message(MessageHud.MessageType.Center, t.Name() + " comes out of the Beast Totem.");
            Effects.Shake(0.4f);
        }

        // ------------------------------------------------------------ the stable: other tamed animals carried with you

        // Any tamed animal near a totem that isn't another player's favorite can be stored too: it leaves the world
        // (bags spill first) and is written down on your character, up to StableMax of each kind (user, 2026-11-17:
        // 10 drakes, 10 wolves, 10 lox...), for this world. Any totem gives it back, only to you. Your favorite keeps
        // its own place and doesn't count.
        internal const int StableMax = 10;

        private static int PerKind(System.Collections.Generic.List<Template> list, string prefab)
        {
            int n = 0;
            foreach (Template t in list) if (t.Prefab == prefab) n++;
            return n;
        }

        // ------------------------------------------------------------ what the totem window lists (user, 2026-11-17: tabs by kind, stars, glow)

        internal class Entry
        {
            public string Name = "", Detail = "", Prefab = "", Kind = "";
            public int Stars, Index = -1;
            public bool Glows;
            public Character Animal;
        }

        internal static string KindOf(string prefab)
        {
            GameObject g = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefab) : null;
            Character pc = g != null ? g.GetComponent<Character>() : null;
            return pc != null && Localization.instance != null ? Localization.instance.Localize(pc.m_name) : prefab;
        }

        private static bool GlowsIn(string look)
        {
            foreach (string part in (look ?? "").Split(',')) if (part == "g=1" || part == "hg=1") return true;
            return false;
        }

        internal static System.Collections.Generic.List<Entry> StableEntries(Player p)
        {
            System.Collections.Generic.List<Entry> r = new System.Collections.Generic.List<Entry>();
            System.Collections.Generic.List<Template> list = StableList(p);
            for (int i = 0; i < list.Count; i++)
            {
                Template t = list[i];
                r.Add(new Entry { Name = t.Name(), Detail = Detail(t), Prefab = t.Prefab, Kind = KindOf(t.Prefab), Stars = Mathf.Max(0, t.Level - 1), Glows = GlowsIn(t.Look), Index = i });
            }
            return r;
        }

        internal static System.Collections.Generic.List<Entry> NearEntries(Player p, Totem totem)
        {
            System.Collections.Generic.List<Entry> r = new System.Collections.Generic.List<Entry>();
            foreach (Character c in StableCandidates(p, totem))
            {
                string prefab = Utils.GetPrefabName(c.gameObject);
                r.Add(new Entry { Name = c.GetHoverName(), Detail = AnimalDetail(c), Prefab = prefab, Kind = KindOf(prefab), Stars = Mathf.Max(0, c.GetLevel() - 1), Glows = GlowsIn(LookOf(Zdo(c))), Animal = c });
            }
            return r;
        }

        // your favorite as a row of the list, with its state (see WindowState); null when you have none
        internal static Entry FavoriteEntry(Player p, Totem totem, out int state)
        {
            string name, detail;
            state = WindowState(p, totem, out name, out detail);
            Template t = Get(p);
            if (state == 0 || t == null) return null;
            return new Entry { Name = name, Detail = detail, Prefab = t.Prefab, Kind = KindOf(t.Prefab), Stars = Mathf.Max(0, t.Level - 1), Glows = GlowsIn(t.Look), Animal = FindLoaded(t.Id) };
        }

        private static System.Collections.Generic.List<Template> StableList(Player p)
        {
            System.Collections.Generic.List<Template> list = new System.Collections.Generic.List<Template>();
            string v;
            if (p == null || !p.m_customData.TryGetValue(WorldKey("stable"), out v) || string.IsNullOrEmpty(v)) return list;
            foreach (string line in v.Split('\n'))
            {
                Template t = Template.Load(line);
                if (t != null) list.Add(t);
            }
            return list;
        }

        private static void SaveStable(Player p, System.Collections.Generic.List<Template> list)
        {
            if (list.Count == 0) { p.m_customData.Remove(WorldKey("stable")); return; }
            string[] lines = new string[list.Count];
            for (int i = 0; i < list.Count; i++) lines[i] = list[i].Save();
            p.m_customData[WorldKey("stable")] = string.Join("\n", lines);
        }

        // "Earth" for a drake of that element, "glows" for the rare glowing ones (user, 2026-11-17: easy to spot in the list)
        private static void LookTags(string look, System.Collections.Generic.List<string> d)
        {
            int el = 0;
            bool glows = false;
            foreach (string part in (look ?? "").Split(','))
            {
                if (part.StartsWith("e=")) int.TryParse(part.Substring(2), out el);
                else if (part == "g=1" || part == "hg=1") glows = true;
            }
            if (el >= Drake.Frost) d.Add(Drake.ElementName(el));
            if (glows) d.Add("<color=#FFD66B>glows</color>");
        }

        // what the window lists: name and a short line about each stored animal
        internal static string[] StableNames(Player p, out string[] details)
        {
            System.Collections.Generic.List<Template> list = StableList(p);
            string[] names = new string[list.Count];
            details = new string[list.Count];
            for (int i = 0; i < list.Count; i++) { names[i] = list[i].Name(); details[i] = Detail(list[i]); }
            return names;
        }

        private static string Detail(Template t)
        {
            System.Collections.Generic.List<string> d = new System.Collections.Generic.List<string>();
            GameObject g = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(t.Prefab) : null;
            Character pc = g != null ? g.GetComponent<Character>() : null;
            string kind = pc != null && Localization.instance != null ? Localization.instance.Localize(pc.m_name) : t.Prefab;
            if (kind != t.Name()) d.Add(kind);
            LookTags(t.Look, d);
            if (t.Level > 1) d.Add((t.Level - 1) + (t.Level == 2 ? " star" : " stars"));
            if (SaddleOn(t) > 0) d.Add("saddled");
            if (t.Halter) d.Add("wears a halter");
            return string.Join(", ", d.ToArray());
        }

        // why this animal can't go in (null: it can)
        private static string StableRefusal(Player p, Character c, Totem totem)
        {
            if (c == null || c.IsPlayer() || c.IsDead() || !c.IsTamed() || Sea.IsSeaMount(c)) return "It can't be stored.";
            if (Vector3.Distance(c.transform.position, totem.transform.position) > StoreRange) return c.GetHoverName() + " has to be near the totem.";
            ZDO z = Zdo(c);
            if (z == null) return "It can't be stored.";
            Template fav = Get(p);
            if (fav != null && z.GetLong(IdKey, 0L) == fav.Id) return c.GetHoverName() + " is your favorite: store it above.";
            long owner = z.GetLong(FavOwnerKey, 0L);
            if (owner != 0L && owner != p.GetPlayerID()) return c.GetHoverName() + " is another player's favorite mount.";
            long rider = Mount.RiderOf(c);
            if (Mount.IsRidden(c) || (rider != 0L && Leash.FindPlayer(rider) != null)) return "Someone is riding " + c.GetHoverName() + ".";
            return null;
        }

        // the tamed animals by this totem that can go in (your favorite is left out: it has its own place)
        internal static System.Collections.Generic.List<Character> StableCandidates(Player p, Totem totem)
        {
            System.Collections.Generic.List<Character> list = new System.Collections.Generic.List<Character>();
            Vector3 at = totem.transform.position;
            foreach (Character c in Character.GetAllCharacters())
                if (c != null && !c.IsPlayer() && c.IsTamed() && StableRefusal(p, c, totem) == null) list.Add(c);
            list.Sort((a, b) => Vector3.Distance(a.transform.position, at).CompareTo(Vector3.Distance(b.transform.position, at)));
            return list;
        }

        internal static string AnimalDetail(Character c)
        {
            string kind = Localization.instance != null ? Localization.instance.Localize(c.m_name) : c.m_name;
            System.Collections.Generic.List<string> d = new System.Collections.Generic.List<string>();
            if (kind != c.GetHoverName()) d.Add(kind);
            LookTags(LookOf(Zdo(c)), d);
            if (c.GetLevel() > 1) d.Add((c.GetLevel() - 1) + (c.GetLevel() == 2 ? " star" : " stars"));
            if (Mount.SaddleTier(c) > 0) d.Add("saddled");
            if (Halter.Wears(c)) d.Add("wears a halter");
            return string.Join(", ", d.ToArray());
        }

        internal static void StoreInStable(Player p, Totem totem, Character c)
        {
            string why = StableRefusal(p, c, totem);
            if (why != null) { p.Message(MessageHud.MessageType.Center, why); return; }
            System.Collections.Generic.List<Template> list = StableList(p);
            string prefab = Utils.GetPrefabName(c.gameObject);
            if (PerKind(list, prefab) >= StableMax) { p.Message(MessageHud.MessageType.Center, "You already carry " + StableMax + " " + KindOf(prefab) + " in the totems: take one out first."); return; }
            ZNetView nv = Leash.View(c);
            if (nv == null || !nv.IsValid()) return;
            if (!nv.IsOwner()) nv.ClaimOwnership();
            string name = c.GetHoverName();
            Template t = Capture(c, 0L);
            list.Add(t);
            SaveStable(p, list);
            Bags.SpillNow(c);
            Tie.Untie(c);
            Plugin.Debug("[stable] stored " + t.Save() + " (" + list.Count + "/" + StableMax + ")");
            ZNetScene.instance.Destroy(c.gameObject);
            Effects.Shake(0.4f);
            p.Message(MessageHud.MessageType.Center, name + " rests inside the Beast Totem.");
        }

        internal static void TakeOutOfStable(Player p, Totem totem, int index)
        {
            System.Collections.Generic.List<Template> list = StableList(p);
            if (index < 0 || index >= list.Count) return;
            Template t = list[index];
            GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(t.Prefab) : null;
            if (prefab == null || prefab.GetComponent<Character>() == null)
            {
                p.Message(MessageHud.MessageType.Center, t.Prefab + " doesn't exist in this game.");
                return;
            }
            Vector3 at = SpotNear(totem.transform.position, Flat(p.transform.position - totem.transform.position));
            GameObject go = UnityEngine.Object.Instantiate(prefab, at, Quaternion.LookRotation(Flat(p.transform.position - at)));
            Character c = go.GetComponent<Character>();
            ZNetView nv = Leash.View(c);
            if (c == null || nv == null || !nv.IsValid()) { p.Message(MessageHud.MessageType.Center, t.Name() + " couldn't come out."); return; }
            list.RemoveAt(index);
            SaveStable(p, list);
            ZDO z = nv.GetZDO();
            if (t.Level > 1) c.SetLevel(t.Level);
            if (t.Saddle > 0) z.Set(LastSaddleKey, t.Saddle);
            int wears = SaddleOn(t);
            if (wears > 0) z.Set(Mount.SaddleKey, wears);
            if (t.Halter) z.Set(Halter.Key, 1);
            if (!string.IsNullOrEmpty(t.TamedName)) z.Set(ZDOVars.s_tamedName, t.TamedName);
            if (!string.IsNullOrEmpty(t.OurName)) z.Set(OurNameKey, t.OurName);
            SetLook(z, t.Look);
            c.SetTamed(true);
            if (t.Halter) Home.Set(c, at);
            Plugin.Debug("[stable] took out " + t.Save() + " (" + list.Count + "/" + StableMax + " left)");
            Effects.Shake(0.4f);
            p.Message(MessageHud.MessageType.Center, t.Name() + " comes out of the Beast Totem.");
        }

        // ------------------------------------------------------------ finding it

        // the marked animal itself first, else a mount called before
        // your favorite, if it is loaded near here
        internal static Character Loaded()
        {
            Template t = Player.m_localPlayer != null ? Get(Player.m_localPlayer) : null;
            return t != null ? FindLoaded(t.Id) : null;
        }

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
