using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Beastkeeper
{
    // Hugin teaches the mod (user, 2026-11-17): the game's own raven comes with a short tip the first time you meet
    // a few things (a lasso or serpent meat in your bag, a totem nearby, a grown drake). Each tip comes
    // once per character, is kept in the game's compendium (the raven's tab), and stays away when the player turned
    // the raven's tips off in the game's settings. The totem window's "?" and the console command bk_raven <tip>
    // bring one back on purpose.
    [HarmonyPatch]
    internal static class Tips
    {
        private class Tip
        {
            public string Key, Label, Text;
            public Tip(string key, string label, string text) { Key = "bk_" + key; Label = label; Text = text; }
        }

        private static Tip[] _all;

        private static Tip[] All
        {
            get
            {
                if (_all != null) return _all;
                // only a few (user, 2026-11-17: "not so many tips"): what the hover texts can't tell you
                _all = new[]
                {
                    new Tip("lasso", "The lasso", "A rope, wanderer! Equip it, hold right-click to swing the loop and left-click to throw it at a calm beast, then keep pressing S to pull it in. Look at a beast on your rope and press a halter's hotbar key to tame it."),
                    new Tip("totem", "The Beast Totem", "Tamed beasts graze in the totem's circle and come back when they stray. Press E on it to rest your animals inside and take them out at any totem."),
                    new Tip("egg", "A sleeping egg", "This egg sleeps, but it can wake. Offer it something that carries an element: the cold of frost, the heat of fire, the foulness of a swamp, the weight of stone or the roar of a storm. What you offer, it becomes. Then keep it warm by a fire."),
                    new Tip("sea", "A drake over the sea", "Drakes fear the sea, wanderer: the serpents rule the deep water. But feed your drake the serpent's own flesh, cooked or in a stew, from your hand at a Beast Totem, and it will dare to fly over the waves for a while. Mind the time: when its courage fails, it throws you into the sea."),
                    new Tip("nest", "A drake nest", "Two grown drakes want a nest. Build a straw rug near them and every few days they lay an egg on it."),
                };
                return _all;
            }
        }

        // the raven's texts live in the game's tutorial list
        private static void Register(Tutorial tut)
        {
            if (tut == null || tut.m_texts == null) return;
            foreach (Tip t in All)
                if (!tut.m_texts.Exists(x => x != null && x.m_name == t.Key))
                    tut.m_texts.Add(new Tutorial.TutorialText { m_name = t.Key, m_topic = t.Label, m_label = t.Label, m_text = t.Text, m_globalKeyTrigger = "", m_tutorialTrigger = "" });
        }

        [HarmonyPatch(typeof(Tutorial), "Awake"), HarmonyPostfix]
        private static void OnTutorial(Tutorial __instance) { Register(__instance); }

        private static readonly HashSet<string> _asked = new HashSet<string>();

        // once per character, and only while the raven's tips are on
        private static void Show(Player p, string key)
        {
            key = "bk_" + key;
            if (_asked.Contains(key)) return;
            _asked.Add(key);
            if (p.HaveSeenTutorial(key)) return;
            Register(Tutorial.instance);
            p.ShowTutorial(key, false);
            Plugin.Debug("[tips] Hugin: " + key);
        }

        // from other parts of the mod (looking at a sleeping egg...)
        internal static void Once(string name)
        {
            Player p = Player.m_localPlayer;
            if (p == null || Tutorial.instance == null || !Plugin.Enabled.Value) return;
            try { Show(p, name); } catch (System.Exception e) { Plugin.Log.LogWarning("[tips] " + e.Message); }
        }

        // on purpose (the console): whatever was seen before
        internal static bool Force(string name)
        {
            Player p = Player.m_localPlayer;
            if (p == null || Tutorial.instance == null) return false;
            string key = name.StartsWith("bk_") ? name : "bk_" + name;
            bool known = false;
            foreach (Tip t in All) if (t.Key == key) known = true;
            if (!known) return false;
            Register(Tutorial.instance);
            p.ShowTutorial(key, true);
            Plugin.Debug("[tips] Hugin on purpose: " + key);
            return true;
        }

        internal static string Names()
        {
            List<string> n = new List<string>();
            foreach (Tip t in All) n.Add(t.Key.Substring(3));
            return string.Join(", ", n.ToArray());
        }

        // ------------------------------------------------------------ when each tip comes

        private static float _next;
        private static readonly List<Piece> _pieces = new List<Piece>();

        internal static void Update()
        {
            if (Time.time < _next) return;
            _next = Time.time + 2f;
            Player p = Player.m_localPlayer;
            if (p == null || p.IsDead() || Tutorial.instance == null || !Plugin.Enabled.Value) return;
            try
            {
                Inventory inv = p.GetInventory();
                if (inv != null)
                    foreach (ItemDrop.ItemData item in inv.GetAllItems())
                        if (Lasso.IsLasso(item)) { Show(p, "lasso"); break; }
                // serpent meat in your bag: Hugin tells you a drake dares to fly over the sea for it (2026-11-22, user)
                if (inv != null && SeaCourage.Mode == SeaCourage.Offering)
                    foreach (ItemDrop.ItemData item in inv.GetAllItems())
                        if (item != null && item.m_dropPrefab != null && SeaCourage.IsSerpent(item.m_dropPrefab.name)) { Show(p, "sea"); break; }
                _pieces.Clear();
                Piece.GetAllPiecesInRadius(p.transform.position, 12f, _pieces);
                foreach (Piece pc in _pieces)
                    if (pc != null && Utils.GetPrefabName(pc.gameObject) == Totem.PrefabName) { Show(p, "totem"); break; }
                foreach (Character c in Character.GetAllCharacters())
                    if (c != null && c.IsTamed() && Drake.Is(c) && Drake.Grown(c) >= 1f && Vector3.Distance(c.transform.position, p.transform.position) < 20f) { Show(p, "nest"); break; }
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("[tips] " + e.Message); _next = Time.time + 30f; }
        }

        // ------------------------------------------------------------ console

        [HarmonyPatch(typeof(Terminal), "InitTerminal"), HarmonyPostfix]
        private static void Command()
        {
            new Terminal.ConsoleCommand("bk_raven", "[tip] - Hugin tells a Beastkeeper tip again (no tip: the list)", new Terminal.ConsoleEvent(args =>
            {
                if (args.Length < 2) { args.Context.AddString("Tips: " + Names()); return; }
                if (!Force(args[1].ToLowerInvariant())) args.Context.AddString("No such tip. Tips: " + Names());
                else args.Context.AddString("Hugin is coming (he waits while enemies are near).");
            }));
        }
    }
}
