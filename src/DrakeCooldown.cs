using System.Collections.Generic;
using UnityEngine;

namespace Beastkeeper
{
    // The ridden drake's attack, as an icon in the bottom right corner of the screen: the icon of its element
    // (the game's own status icons: burning, frost, poison, shock; a stone for earth), darkened from the top while
    // the attack waits, with the seconds left. Dimmed while the drake attacks, red when it is too tired to attack.
    // A frost drake shows a second icon to its left: the blizzard's own wait.
    internal static partial class Drake
    {
        private static readonly Dictionary<int, Sprite> _elIcons = new Dictionary<int, Sprite>();
        private static float _cdEnd = -1f, _cdTotal = 1f, _readyAt = -10f;
        private static bool _wasWaiting;
        private static float _blizzardReadyAt = -10f;
        private static bool _blizzardWaiting, _blizzardIconTried;
        private static Sprite _blizzardIcon;

        private static Sprite EffectIcon(string name)
        {
            if (ObjectDB.instance == null) return null;
            StatusEffect se = ObjectDB.instance.GetStatusEffect(name.GetStableHashCode());
            return se != null ? se.m_icon : null;
        }

        private static Sprite ItemIcon(string name)
        {
            if (ObjectDB.instance == null) return null;
            GameObject p = ObjectDB.instance.GetItemPrefab(name);
            ItemDrop d = p != null ? p.GetComponent<ItemDrop>() : null;
            return d != null && d.m_itemData.m_shared.m_icons != null && d.m_itemData.m_shared.m_icons.Length > 0 ? d.m_itemData.m_shared.m_icons[0] : null;
        }

        private static Sprite IconOf(int el)
        {
            Sprite s;
            if (_elIcons.TryGetValue(el, out s) && s != null) return s;
            switch (el)
            {
                case Fire: s = EffectIcon("Burning") ?? ItemIcon("SurtlingCore"); break;
                case Frost: s = EffectIcon("Frost") ?? ItemIcon("FreezeGland"); break;
                case Poison: s = EffectIcon("Poison") ?? ItemIcon("Ooze"); break;
                case Lightning: s = EffectIcon("Lightning") ?? ItemIcon("Thunderstone"); break;
                case Earth: s = ItemIcon("Stone"); break;
            }
            if (s != null) _elIcons[el] = s;
            return s;
        }

        private static Color ColorOf(int el)
        {
            switch (el)
            {
                case Fire: return new Color(1f, 0.55f, 0.2f);
                case Frost: return new Color(0.55f, 0.85f, 1f);
                case Poison: return new Color(0.55f, 0.9f, 0.3f);
                case Lightning: return new Color(0.75f, 0.7f, 1f);
                case Earth: return new Color(0.75f, 0.6f, 0.42f);
            }
            return Color.white;
        }

        internal static void DrawCooldown()
        {
            Character c = Mount.RiddenCharacter();
            if (c == null || !Is(c) || Hud.instance == null || Event.current == null || Event.current.type != EventType.Repaint) return;
            int el = ElementOf(c);
            if (ShotOf(el) == null) return;
            if (_px == null) { _px = new Texture2D(1, 1); _px.SetPixel(0, 0, Color.white); _px.Apply(); Object.DontDestroyOnLoad(_px); }

            bool busy = _pending != null || _burstLeft > 0 || _queuedC != null;
            float left = busy ? 0f : _nextShot - Time.time;
            if (!busy && left > 0f && Mathf.Abs(_nextShot - _cdEnd) > 0.001f) { _cdEnd = _nextShot; _cdTotal = Mathf.Max(0.1f, left); }
            bool waiting = busy || left > 0f;
            if (_wasWaiting && !waiting) _readyAt = Time.time;   // a short flash when it's ready again
            _wasWaiting = waiting;
            bool tired = !waiting && !CanSpend(AttackCost());

            float s = Screen.height / 1080f;
            float size = 64f * s, margin = 24f * s;
            Rect r = new Rect(Screen.width - margin - size, Screen.height - margin - size, size, size);
            float flash = Mathf.Clamp01(1f - (Time.time - _readyAt) / 0.4f);
            Slot(r, s, IconOf(el), ColorOf(el), busy ? 1f : Mathf.Clamp01(left / _cdTotal), busy ? 0f : left, busy, tired, flash);

            // the frost drake's blizzard has a wait of its own: a second icon left of the attack's
            if (el == Frost)
            {
                float bl = Blizzard.WaitLeft();
                if (_blizzardWaiting && bl <= 0f) _blizzardReadyAt = Time.time;
                _blizzardWaiting = bl > 0f;
                Rect br = new Rect(r.x - size - 14f * s, r.y, size, size);
                float bflash = Mathf.Clamp01(1f - (Time.time - _blizzardReadyAt) / 0.4f);
                Slot(br, s, BlizzardIcon(), new Color(0.85f, 0.93f, 1f), Mathf.Clamp01(bl / Blizzard.Seconds), bl, false, false, bflash);
            }
        }

        // the blizzard's icon: the game's freezing (or cold) icon, one that isn't the frost attack's own
        private static Sprite BlizzardIcon()
        {
            if (_blizzardIconTried) return _blizzardIcon;
            if (ObjectDB.instance == null) return null;
            _blizzardIconTried = true;
            Sprite attack = IconOf(Frost);
            Sprite[] options = { EffectIcon("Freezing"), EffectIcon("Cold"), ItemIcon("FreezeGland") };
            foreach (Sprite o in options) if (o != null && o != attack) { _blizzardIcon = o; break; }
            if (_blizzardIcon == null) _blizzardIcon = attack;
            Plugin.Debug("[blizzard] icon " + (_blizzardIcon != null ? _blizzardIcon.name : "none") + " (frost attack icon " + (attack != null ? attack.name : "none") + ")");
            return _blizzardIcon;
        }

        // One icon: a frame in its colour (brighter for a moment when it's ready), the picture, the wait darkening it
        // from the top down as it runs out (k), the seconds left. busy: dimmed (the attack is going on); tired: red.
        private static void Slot(Rect r, float s, Sprite icon, Color tint, float k, float left, bool busy, bool tired, float flash)
        {
            Color old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.75f);
            GUI.DrawTexture(new Rect(r.x - 3f * s, r.y - 3f * s, r.width + 6f * s, r.height + 6f * s), _px);
            GUI.color = Color.Lerp(tint * 0.8f, Color.white, flash);
            GUI.DrawTexture(new Rect(r.x - 2f * s, r.y - 2f * s, r.width + 4f * s, r.height + 4f * s), _px);
            GUI.color = new Color(0.12f, 0.12f, 0.14f, 1f);
            GUI.DrawTexture(r, _px);

            Rect ir = new Rect(r.x + 6f * s, r.y + 6f * s, r.width - 12f * s, r.height - 12f * s);
            GUI.color = busy ? new Color(1f, 1f, 1f, 0.45f) : tired ? new Color(1f, 0.45f, 0.4f, 1f) : Color.white;
            if (icon != null && icon.texture != null)
            {
                Rect tr = icon.textureRect;
                Texture t = icon.texture;
                GUI.DrawTextureWithTexCoords(ir, t, new Rect(tr.x / t.width, tr.y / t.height, tr.width / t.width, tr.height / t.height));
            }
            else
            {
                GUI.color = tint;
                GUI.DrawTexture(ir, _px);
            }

            if (k > 0f)
            {
                GUI.color = new Color(0f, 0f, 0f, busy ? 0.35f : 0.62f);
                GUI.DrawTexture(new Rect(r.x, r.y + r.height * (1f - k), r.width, r.height * k), _px);
            }
            if (!busy && left > 0.05f)
            {
                GUIStyle st = new GUIStyle(GUI.skin.label);
                st.alignment = TextAnchor.MiddleCenter;
                st.fontStyle = FontStyle.Bold;
                st.fontSize = Mathf.Max(10, Mathf.RoundToInt(22f * s));
                string txt = left < 10f ? left.ToString("0.0") : Mathf.CeilToInt(left).ToString();
                GUI.color = new Color(0f, 0f, 0f, 0.9f);
                GUI.Label(new Rect(r.x + 1.5f * s, r.y + 1.5f * s, r.width, r.height), txt, st);
                GUI.color = Color.white;
                GUI.Label(r, txt, st);
            }
            GUI.color = old;
        }
    }
}
