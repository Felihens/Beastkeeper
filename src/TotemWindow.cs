using HarmonyLib;
using UnityEngine;

namespace Beastkeeper
{
    // The Beast Totem window (E on the totem), in the same look as the Recipe Book's: a dark window in the middle of
    // the screen, the mouse free, Esc closes. It shows your favorite mount and lets you store it in the totem
    // (it waits there, out of the world) or take it out again, at this or any other totem. Only your favorite fits.
    [HarmonyPatch]
    internal static class TotemWindow
    {
        internal static bool Open;
        private static Totem _totem;
        private static int _state;
        private static string _name = "", _detail = "";
        private static float _nextCheck;
        private const float CloseDistance = 7f;

        internal static void Show(Totem t)
        {
            _totem = t;
            Open = true;
            _nextCheck = 0f;
        }

        private static void Close() { Open = false; _totem = null; }

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
            if (Open && ZInput.GetKeyDown(KeyCode.Escape)) { Close(); return false; }
            return true;
        }

        // ---- drawing

        private static GUIStyle _bg, _title, _text, _small, _button, _buttonOff, _close;
        private const float W = 520f, H = 330f;

        private static Texture2D Tex(Color c)
        {
            Texture2D t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            Object.DontDestroyOnLoad(t);
            return t;
        }

        private static void Styles()
        {
            if (_bg != null) return;
            _bg = new GUIStyle(GUI.skin.box) { normal = { background = Tex(new Color(0.08f, 0.07f, 0.06f, 0.94f)) } };
            _title = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, normal = { textColor = new Color(1f, 0.82f, 0.45f) } };
            _text = new GUIStyle(GUI.skin.label) { fontSize = 16, wordWrap = true, normal = { textColor = new Color(0.92f, 0.9f, 0.85f) } };
            _small = new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = true, normal = { textColor = new Color(0.7f, 0.68f, 0.62f) } };
            _button = new GUIStyle(GUI.skin.button) { fontSize = 17, fontStyle = FontStyle.Bold,
                normal = { background = Tex(new Color(0.95f, 0.72f, 0.3f, 0.85f)), textColor = Color.black },
                hover = { background = Tex(new Color(1f, 0.82f, 0.45f, 0.95f)), textColor = Color.black } };
            _buttonOff = new GUIStyle(_button) { normal = { background = Tex(new Color(0.25f, 0.23f, 0.2f, 0.9f)), textColor = new Color(0.6f, 0.58f, 0.52f) },
                hover = { background = Tex(new Color(0.25f, 0.23f, 0.2f, 0.9f)), textColor = new Color(0.6f, 0.58f, 0.52f) } };
            _close = new GUIStyle(GUI.skin.button) { fontSize = 15, normal = { background = Tex(new Color(0f, 0f, 0f, 0.5f)), textColor = new Color(0.85f, 0.83f, 0.78f) } };
        }

        internal static void OnGUI()
        {
            if (!Open) return;
            Player p = Player.m_localPlayer;
            if (p == null || p.IsDead() || _totem == null || Vector3.Distance(p.transform.position, _totem.transform.position) > CloseDistance) { Close(); return; }
            if (Time.time >= _nextCheck)
            {
                _nextCheck = Time.time + 0.4f;
                _state = Favorite.WindowState(p, _totem, out _name, out _detail);
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
            if (GUILayout.Button("X", _close, GUILayout.Width(32))) Close();
            GUILayout.EndHorizontal();
            GUILayout.Space(8);

            string key = Plugin.CallKey.Value.MainKey == KeyCode.None ? "the favorite key" : Plugin.CallKey.Value.ToString();
            switch (_state)
            {
                case 0:
                    GUILayout.Label("You have no favorite mount yet.", _text);
                    GUILayout.Space(6);
                    GUILayout.Label("Look at a saddled animal and press " + key + " to choose it. Only your favorite fits in a totem; each animal can be the favorite of one player only.", _small);
                    break;
                case 1:
                    Header();
                    GUILayout.Label("It rests inside this totem, out of the world.", _text);
                    GUILayout.Space(14);
                    if (GUILayout.Button("Take " + _name + " out", _button, GUILayout.Height(44))) { Favorite.TakeOutAt(p, _totem); _nextCheck = 0f; }
                    GUILayout.Space(6);
                    GUILayout.Label("It comes out beside this totem, with its saddle. Any totem can give it back.", _small);
                    break;
                case 2:
                    Header();
                    GUILayout.Label("It is here by the totem.", _text);
                    GUILayout.Space(14);
                    if (GUILayout.Button("Store " + _name + " in the totem", _button, GUILayout.Height(44))) { Favorite.StoreAt(p, _totem); _nextCheck = 0f; }
                    GUILayout.Space(6);
                    GUILayout.Label("It leaves the world until you take it out, here or at any other totem. Its saddle bags are emptied on the ground first.", _small);
                    break;
                case 3:
                    Header();
                    GUILayout.Label("It is somewhere else. Bring it close to this totem (" + key + " calls it to you) to store it.", _text);
                    GUILayout.Space(14);
                    GUILayout.Button("Store " + _name + " in the totem", _buttonOff, GUILayout.Height(44));
                    break;
                default:
                    Header();
                    GUILayout.Label("It can't be found: it is gone.", _text);
                    GUILayout.Space(14);
                    if (GUILayout.Button("Bring " + _name + " back (" + Favorite.ReviveCost + " food)", _button, GUILayout.Height(44))) { Favorite.Revive(p, _totem); _nextCheck = 0f; }
                    GUILayout.Space(6);
                    GUILayout.Label("It comes back tamed, with its stars and name, but without a saddle. Food in your bag or in chests nearby pays for it.", _small);
                    break;
            }
            GUILayout.EndArea();
            GUI.matrix = old;
        }

        private static void Header()
        {
            GUILayout.Label("Your favorite: " + _name, _text);
            if (_detail.Length > 0) GUILayout.Label(_detail, _small);
            GUILayout.Space(4);
        }
    }
}
