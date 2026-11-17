using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Beastkeeper
{
    // The Beast Totem window (E on the totem), in the same look as the Recipe Book's: a dark window in the middle of
    // the screen, the mouse free, Esc closes. One list of your animals: your favorite first, marked in gold, then the
    // ones resting in the totems (they stay with you: take them out at any totem) and the tamed ones by this totem
    // that can go in. Up to 10 of each kind rest in the totems; the favorite doesn't count.
    // Tabs at the top show all of them or one kind; below them, filters by stars and glow. (user, 2026-11-17)
    [HarmonyPatch]
    internal static class TotemWindow
    {
        internal static bool Open;
        private static Totem _totem;
        private static int _state;
        private static float _nextCheck;
        private static Favorite.Entry _fav;
        private static List<Favorite.Entry> _stored = new List<Favorite.Entry>(), _near = new List<Favorite.Entry>();
        private static List<string> _kinds = new List<string>();          // prefabs, in the tabs' order
        private static Dictionary<string, string> _kindName = new Dictionary<string, string>();
        private static string _tab = "";      // "" = All, else a prefab (kept between openings)
        private static int _stars = -1;       // -1 any, else exactly 0, 1 or 2 stars
        private static bool _glowOnly;
        private static string _renaming, _renameText = "";   // which name is being edited ("fav", "s3", "n<id>"), and the text
        private static bool _focusRename;
        private static Vector2 _scroll;
        private const float CloseDistance = 7f;
        private static int _openedFrame;
        private static float _closedByUse = -10f;
        private static bool _help;
        private static Vector2 _helpScroll;

        internal static void Show(Totem t)
        {
            if (Time.time - _closedByUse < 0.4f) return;   // the E that just closed it, reaching the totem too (user: E didn't close)
            _totem = t;
            Open = true;
            _openedFrame = Time.frameCount;
            _nextCheck = 0f;
        }

        private static void Close() { Open = false; _totem = null; _renaming = null; _help = false; }

        // ---- while it is open: free mouse, no walking, looking, zooming or hotkeys; Esc closes it

        [HarmonyPatch(typeof(GameCamera), "UpdateMouseCapture"), HarmonyPostfix]
        private static void FreeCursor()
        {
            if (!Open) return;
            ZCursor.LockState = CursorLockMode.None;
            ZCursor.Show();
        }

        [HarmonyPatch(typeof(PlayerController), "TakeInput"), HarmonyPrefix]
        private static bool NoControl(ref bool __result)
        {
            if (!Open) return true;
            __result = false;
            return false;
        }

        [HarmonyPatch(typeof(Player), "TakeInput"), HarmonyPrefix]
        private static bool NoHotkeys(ref bool __result)
        {
            if (!Open) return true;
            __result = false;
            return false;
        }

        [HarmonyPatch(typeof(ZInput), "GetMouseScrollWheel"), HarmonyPrefix]
        private static bool NoZoom(ref float __result)
        {
            if (!Open) return true;
            __result = 0f;
            return false;
        }

        [HarmonyPatch(typeof(ZInput), "Internal_GetMouseScrollWheel"), HarmonyPrefix]
        private static bool NoZoomInner(ref float __result) { return NoZoom(ref __result); }

        [HarmonyPatch(typeof(Menu), "Update"), HarmonyPrefix]
        private static bool EscCloses()
        {
            if (Open && ZInput.GetKeyDown(KeyCode.Escape)) { if (_renaming != null) _renaming = null; else Close(); return false; }
            // the use key (E) that opened it closes it too (user, 2026-11-17); not while typing a name, nor on the press that opened it
            if (Open && _renaming == null && Time.frameCount > _openedFrame + 1 && (ZInput.GetButtonDown("Use") || ZInput.GetButtonDown("JoyUse"))) { Close(); _closedByUse = Time.time; return false; }
            return true;
        }

        // ---- drawing

        private static GUIStyle _bg, _title, _section, _text, _small, _button, _buttonOff, _rowButton, _rowButtonOff, _close, _field, _tiny,
            _tabOn, _tabOff, _favBox, _rowBox, _favTag;
        private const float W = 600f, H = 760f;

        private static Texture2D Tex(Color c)
        {
            Texture2D t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            t.hideFlags = HideFlags.HideAndDontSave;   // else the game's clean-up of unused textures deletes it after a while (the window lost its background)
            return t;
        }

        private static void Styles()
        {
            if (_bg != null && _bg.normal.background != null) return;
            _bg = new GUIStyle(GUI.skin.box) { normal = { background = Tex(new Color(0.08f, 0.07f, 0.06f, 0.94f)) } };
            _title = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, normal = { textColor = new Color(1f, 0.82f, 0.45f) } };
            _text = new GUIStyle(GUI.skin.label) { fontSize = 16, wordWrap = true, normal = { textColor = new Color(0.92f, 0.9f, 0.85f) } };
            _small = new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = true, richText = true, normal = { textColor = new Color(0.7f, 0.68f, 0.62f) } };
            _button = new GUIStyle(GUI.skin.button) { fontSize = 17, fontStyle = FontStyle.Bold,
                normal = { background = Tex(new Color(0.95f, 0.72f, 0.3f, 0.85f)), textColor = Color.black },
                hover = { background = Tex(new Color(1f, 0.82f, 0.45f, 0.95f)), textColor = Color.black } };
            _buttonOff = new GUIStyle(_button) { normal = { background = Tex(new Color(0.25f, 0.23f, 0.2f, 0.9f)), textColor = new Color(0.6f, 0.58f, 0.52f) },
                hover = { background = Tex(new Color(0.25f, 0.23f, 0.2f, 0.9f)), textColor = new Color(0.6f, 0.58f, 0.52f) } };
            _section = new GUIStyle(_text) { fontSize = 17, fontStyle = FontStyle.Bold, normal = { textColor = new Color(1f, 0.82f, 0.45f) } };
            _rowButton = new GUIStyle(_button) { fontSize = 15 };
            _rowButtonOff = new GUIStyle(_buttonOff) { fontSize = 15 };
            _field = new GUIStyle(GUI.skin.textField) { fontSize = 16, normal = { background = Tex(new Color(0f, 0f, 0f, 0.6f)), textColor = Color.white },
                focused = { background = Tex(new Color(0f, 0f, 0f, 0.75f)), textColor = Color.white } };
            _tiny = new GUIStyle(GUI.skin.button) { fontSize = 12, normal = { background = Tex(new Color(0f, 0f, 0f, 0.45f)), textColor = new Color(0.85f, 0.8f, 0.7f) },
                hover = { background = Tex(new Color(0.3f, 0.25f, 0.15f, 0.8f)), textColor = Color.white } };
            _close = new GUIStyle(GUI.skin.button) { fontSize = 15, normal = { background = Tex(new Color(0f, 0f, 0f, 0.5f)), textColor = new Color(0.85f, 0.83f, 0.78f) } };
            _tabOff = new GUIStyle(GUI.skin.button) { fontSize = 14, padding = new RectOffset(10, 10, 5, 5), margin = new RectOffset(0, 4, 0, 4),
                normal = { background = Tex(new Color(0.18f, 0.16f, 0.13f, 0.95f)), textColor = new Color(0.8f, 0.77f, 0.7f) },
                hover = { background = Tex(new Color(0.3f, 0.25f, 0.15f, 0.95f)), textColor = Color.white } };
            _tabOn = new GUIStyle(_tabOff) { fontStyle = FontStyle.Bold,
                normal = { background = Tex(new Color(0.95f, 0.72f, 0.3f, 0.9f)), textColor = Color.black },
                hover = { background = Tex(new Color(1f, 0.82f, 0.45f, 0.95f)), textColor = Color.black } };
            _rowBox = new GUIStyle(GUI.skin.box) { padding = new RectOffset(8, 8, 6, 6), margin = new RectOffset(0, 6, 0, 4),
                normal = { background = Tex(new Color(1f, 1f, 1f, 0.04f)) } };
            _favBox = new GUIStyle(_rowBox) { normal = { background = Tex(new Color(0.55f, 0.4f, 0.12f, 0.35f)) } };
            _favTag = new GUIStyle(_small) { fontStyle = FontStyle.Bold, normal = { textColor = new Color(1f, 0.82f, 0.45f) } };
        }

        private static void Refresh(Player p)
        {
            _fav = Favorite.FavoriteEntry(p, _totem, out _state);
            _stored = Favorite.StableEntries(p);
            _near = Favorite.NearEntries(p, _totem);
            _kinds.Clear();
            _kindName.Clear();
            List<Favorite.Entry> all = new List<Favorite.Entry>(_stored);
            all.AddRange(_near);
            if (_fav != null) all.Add(_fav);
            foreach (Favorite.Entry e in all)
                if (!_kindName.ContainsKey(e.Prefab)) { _kindName[e.Prefab] = e.Kind; _kinds.Add(e.Prefab); }
            _kinds.Sort((a, b) => string.Compare(_kindName[a], _kindName[b], System.StringComparison.OrdinalIgnoreCase));
            if (_tab.Length > 0 && !_kindName.ContainsKey(_tab)) _tab = "";
        }

        private static int StoredOf(string prefab)
        {
            int n = 0;
            foreach (Favorite.Entry e in _stored) if (e.Prefab == prefab) n++;
            return n;
        }

        private static bool Shown(Favorite.Entry e)
        {
            if (e == null) return false;
            if (_tab.Length > 0 && e.Prefab != _tab) return false;
            if (_stars >= 0 && e.Stars != _stars) return false;
            if (_glowOnly && !e.Glows) return false;
            return true;
        }

        internal static void OnGUI()
        {
            if (!Open) return;
            Player p = Player.m_localPlayer;
            if (p == null || p.IsDead() || _totem == null || Vector3.Distance(p.transform.position, _totem.transform.position) > CloseDistance) { Close(); return; }
            if (Event.current.type == EventType.Layout && Time.time >= _nextCheck)   // same rows in Layout and Repaint
            {
                _nextCheck = Time.time + 0.4f;
                Refresh(p);
            }
            Styles();
            float s = Screen.height / 1080f;
            Matrix4x4 old = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(s, s, 1f));
            float sw = Screen.width / s, sh = Screen.height / s;
            Rect win = new Rect((sw - W) / 2f, (sh - H) / 2f, W, H);
            GUI.Box(win, GUIContent.none, _bg);
            GUI.Box(win, GUIContent.none, _bg);

            GUILayout.BeginArea(new Rect(win.x + 20, win.y + 14, W - 40, H - 28));
            GUILayout.BeginHorizontal();
            GUILayout.Label("Beast Totem", _title);
            GUILayout.FlexibleSpace();
            // a page of short tips instead of the list (user, 2026-11-17); "?" again or Back returns
            if (GUILayout.Button("?", _help ? _tabOn : _close, GUILayout.Width(32))) { _help = !_help; _scroll = Vector2.zero; }
            GUILayout.Space(4);
            if (GUILayout.Button("X", _close, GUILayout.Width(32))) Close();
            GUILayout.EndHorizontal();
            GUILayout.Space(6);

            if (_help) { Help(); GUILayout.EndArea(); GUI.matrix = old; return; }

            Tabs(W - 40);
            Filters();
            GUILayout.Space(6);

            _scroll = GUILayout.BeginScrollView(_scroll, false, true);
            int shown = 0;
            if (Shown(_fav)) { FavoriteRow(p); shown++; }

            bool header = false;
            foreach (Favorite.Entry e in _stored)
            {
                if (!Shown(e)) continue;
                if (!header) { GUILayout.Space(4); GUILayout.Label("In the totems", _section); header = true; }
                GUILayout.BeginHorizontal(_rowBox);
                string nn = Row("s" + e.Index, e);
                if (nn != null) Favorite.RenameStored(p, e.Index, nn);
                if (GUILayout.Button("Take out", _rowButton, GUILayout.Width(110), GUILayout.Height(30))) { Favorite.TakeOutOfStable(p, _totem, e.Index); _nextCheck = 0f; }
                GUILayout.EndHorizontal();
                shown++;
            }

            header = false;
            foreach (Favorite.Entry e in _near)
            {
                if (!Shown(e) || e.Animal == null) continue;
                if (!header) { GUILayout.Space(4); GUILayout.Label("By the totem", _section); header = true; }
                GUILayout.BeginHorizontal(_rowBox);
                string nn = Row("n" + e.Animal.GetZDOID(), e);
                if (nn != null) Favorite.RenameAnimal(p, e.Animal, nn);
                if (StoredOf(e.Prefab) >= Favorite.StableMax) GUILayout.Button("Full", _rowButtonOff, GUILayout.Width(110), GUILayout.Height(30));
                else if (GUILayout.Button("Store", _rowButton, GUILayout.Width(110), GUILayout.Height(30))) { Favorite.StoreInStable(p, _totem, e.Animal); _nextCheck = 0f; }
                GUILayout.EndHorizontal();
                shown++;
            }

            if (shown == 0)
                GUILayout.Label(_tab.Length == 0 && _stars < 0 && !_glowOnly ? "No animal here yet: bring a tamed animal close to the totem to store it." : "No animal matches these filters.", _small);
            GUILayout.EndScrollView();

            GUILayout.Space(4);
            string key = Plugin.CallKey.Value.MainKey == KeyCode.None ? "the favorite key" : Plugin.CallKey.Value.ToString();
            if (_state == 0) GUILayout.Label("No favorite mount yet: look at a saddled animal and press " + key + " to choose it.", _small);
            GUILayout.Label("Tamed animals within " + (int)Favorite.StoreRangeShown + " m of the totem can rest inside it, up to " + Favorite.StableMax
                + " of each kind (your favorite doesn't count). They stay with you: take them out at any totem. Saddle bags are emptied on the ground first.", _small);
            GUILayout.EndArea();
            GUI.matrix = old;
        }

        // ---- tips: how the mod works, in a few lines each
        private static void Help()
        {
            string fav = Plugin.CallKey.Value.MainKey == KeyCode.None ? "the favorite key" : Plugin.CallKey.Value.ToString();
            string orders = Plugin.OrderKey.Value.MainKey == KeyCode.None ? "the orders key" : "hold " + Plugin.OrderKey.Value;
            _helpScroll = GUILayout.BeginScrollView(_helpScroll, false, true);
            HelpPart("Catching", "Equip a lasso, hold right-click to swing the loop and left-click to throw it at a calm animal. Then keep pressing S to pull it in. Each lasso holds animals of its own tier and below. A trap holds even a wary animal for a few seconds.");
            HelpPart("Taming", "Food alone tames nothing. Look at an animal on your lasso and press a halter's hotbar key: it is tamed at once. With the lasso, E on a tree or a wall ties it there.");
            HelpPart("Riding", "Look at a tamed animal and press a saddle's hotbar key. E gets on and off, Shift + E renames it, Alt + E takes the saddle off. Deer and lox carry bags: hold E. While riding, " + orders + " opens the orders wheel.");
            HelpPart("Your favorite mount", "Look at a saddled animal and press " + fav + ". Then " + fav + " anywhere calls it to your side. If it dies, it comes back at a Beast Totem for a few trophies of its kind.");
            HelpPart("The Beast Totem", "Tamed animals graze in its circle and come back when they stray. Store up to 10 of each kind inside and take them out at any totem. Feed a tamed animal any food to heal it.");
            HelpPart("Drakes", "After Moder dies, her eggs can hatch. Lay one on the ground and make it an offering of something that carries an element: the cold of frost, the heat of fire, the foulness of a swamp, the weight of stone or the roar of a storm. What you offer, it becomes. Then keep it warm by a fire. A grown drake takes the Drake Saddle and flies.");
            HelpPart("Drake nests", "Two grown drakes near a straw rug lay an egg on it every few days. Look at the rug to see how the nest is doing. Laid eggs can't summon Moder.");
            GUILayout.EndScrollView();
            GUILayout.Space(6);
            if (GUILayout.Button("Back", _rowButton, GUILayout.Width(110), GUILayout.Height(30))) _help = false;
        }

        private static void HelpPart(string title, string text)
        {
            GUILayout.BeginVertical(_rowBox);
            GUILayout.Label(title, _section);
            GUILayout.Label(text, _text);
            GUILayout.EndVertical();
        }

        // All, then one tab per kind ("Drake 3/10": how many rest in the totems), wrapped over as many lines as needed
        private static void Tabs(float width)
        {
            List<string> labels = new List<string>();
            List<string> keys = new List<string>();
            labels.Add("All");
            keys.Add("");
            foreach (string k in _kinds)
            {
                int n = StoredOf(k);
                labels.Add(_kindName[k] + (n > 0 ? " " + n + "/" + Favorite.StableMax : ""));
                keys.Add(k);
            }
            float used = 0f;
            GUILayout.BeginHorizontal();
            for (int i = 0; i < labels.Count; i++)
            {
                GUIStyle st = _tab == keys[i] ? _tabOn : _tabOff;
                float w = st.CalcSize(new GUIContent(labels[i])).x + st.margin.right;
                if (used > 0f && used + w > width) { GUILayout.FlexibleSpace(); GUILayout.EndHorizontal(); GUILayout.BeginHorizontal(); used = 0f; }
                if (GUILayout.Button(labels[i], st, GUILayout.ExpandWidth(false))) { _tab = keys[i]; _scroll = Vector2.zero; }
                used += w;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        private static void Filters()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Stars:", _small, GUILayout.ExpandWidth(false));
            string[] names = { "Any", "0", "1", "2" };
            for (int i = 0; i < names.Length; i++)
                if (GUILayout.Button(names[i], _stars == i - 1 ? _tabOn : _tabOff, GUILayout.ExpandWidth(false))) { _stars = i - 1; _scroll = Vector2.zero; }
            GUILayout.Space(14);
            if (GUILayout.Button("Glowing only", _glowOnly ? _tabOn : _tabOff, GUILayout.ExpandWidth(false))) { _glowOnly = !_glowOnly; _scroll = Vector2.zero; }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        // your favorite, first in the list and marked in gold, with what can be done with it where it is
        private static void FavoriteRow(Player p)
        {
            string key = Plugin.CallKey.Value.MainKey == KeyCode.None ? "the favorite key" : Plugin.CallKey.Value.ToString();
            GUILayout.BeginHorizontal(_favBox);
            GUILayout.BeginVertical();
            GUILayout.Label("Your favorite mount", _favTag);
            string nn = NameLine("fav", _fav.Name, _section);
            if (nn != null) Favorite.RenameFavorite(p, nn);
            if (_fav.Detail.Length > 0) GUILayout.Label(_fav.Detail, _small);
            string where = _state == 1 ? "Rests in the totems, out of the world. Any totem gives it back."
                : _state == 2 ? "Here by the totem. Its saddle bags are emptied on the ground if you store it."
                : _state == 3 ? "Somewhere else: bring it close to the totem to store it (" + key + " calls it to you)."
                : "It is gone. It comes back tamed, with its stars and name, but without a saddle; trophies of its kind in your bag or in chests nearby pay.";
            GUILayout.Label(where, _small);
            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            switch (_state)
            {
                case 1:
                    if (GUILayout.Button("Take out", _rowButton, GUILayout.Width(110), GUILayout.Height(30))) { Favorite.TakeOutAt(p, _totem); _nextCheck = 0f; }
                    break;
                case 2:
                    if (GUILayout.Button("Store", _rowButton, GUILayout.Width(110), GUILayout.Height(30))) { Favorite.StoreAt(p, _totem); _nextCheck = 0f; }
                    break;
                case 3:
                    GUILayout.Button("Store", _rowButtonOff, GUILayout.Width(110), GUILayout.Height(30));
                    break;
                default:
                    if (GUILayout.Button("Bring back\n(" + Favorite.FavoritePrice(p) + ")", _rowButton, GUILayout.Width(150), GUILayout.Height(44))) { Favorite.Revive(p, _totem); _nextCheck = 0f; }
                    break;
            }
            GUILayout.EndHorizontal();
        }

        // a row: the name (with Rename) and a short line; returns the new name once one is confirmed
        private static string Row(string id, Favorite.Entry e)
        {
            GUILayout.BeginVertical();
            string renamed = NameLine(id, e.Name, _text);
            if (e.Detail.Length > 0) GUILayout.Label(e.Detail, _small);
            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            return renamed;
        }

        // A name with a small Rename button; once pressed, a text box with OK and Cancel (Enter confirms, Esc cancels).
        // Returns the new name when confirmed, else null. (user, 2026-11-17)
        private static string NameLine(string id, string name, GUIStyle style)
        {
            GUILayout.BeginHorizontal();
            if (_renaming != id)
            {
                GUILayout.Label(name, style, GUILayout.ExpandWidth(false));
                GUILayout.Space(6);
                if (GUILayout.Button("Rename", _tiny, GUILayout.Width(64), GUILayout.Height(22))) { _renaming = id; _renameText = name; _focusRename = true; }
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                return null;
            }
            Event e = Event.current;
            bool enter = e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter);
            if (enter) e.Use();
            GUI.SetNextControlName("bk_rename");
            _renameText = GUILayout.TextField(_renameText ?? "", 20, _field, GUILayout.Width(190), GUILayout.Height(26));
            if (_focusRename && e.type == EventType.Repaint) { GUI.FocusControl("bk_rename"); _focusRename = false; }
            bool ok = GUILayout.Button("OK", _tiny, GUILayout.Width(44), GUILayout.Height(26)) || enter;
            bool cancel = GUILayout.Button("Cancel", _tiny, GUILayout.Width(60), GUILayout.Height(26));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            if (cancel) { _renaming = null; return null; }
            if (!ok) return null;
            _renaming = null;
            _nextCheck = 0f;
            GUI.FocusControl(null);
            return (_renameText ?? "").Trim();
        }
    }
}
