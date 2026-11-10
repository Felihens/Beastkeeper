using UnityEngine;

namespace Beastkeeper
{
    // Catching a wild animal: a tug of war. The animal bolts, zig-zagging on the rope (which won't break and
    // holds it within reach), and a bar shows who is winning. Tap S to dig in and pull.
    // The animal's tier makes it harder, each tier adding something new:
    //   1  only S
    //   2  it yanks to one side: tap the opposite key (A or D) in time
    //   3  it also drags to one side: hold the opposite key until the gauge fills
    //   4  it also bucks: click the mouse button shown (left or right) in time
    //   5  all of it, faster and more often
    // A better lasso doesn't make it easier: it only lets you catch stronger animals.
    // Full bar = caught: it stays on the rope, calm, ready for a halter. Empty bar = it breaks free.
    internal static class Tug
    {
        private enum Kind { None, Tap, Hold, Mouse }

        // the rope holds a fighting animal this close: where the rope gets taut, so a catch never ends past it
        private static float TugRope { get { return Mathf.Max(4f, Plugin.SlowDistance.Value); } }
        private static readonly float[] PullByTier = { 0f, 0.12f, 0.15f, 0.18f, 0.21f, 0.24f };
        private static readonly float[] Window     = { 0f, 0f, 1.1f, 1.0f, 0.9f, 0.7f };   // time to react
        private static readonly float[] HoldTime   = { 0f, 0f, 0f, 1.3f, 1.2f, 1.0f };     // how long to hold
        private static readonly float[] EveryMin   = { 0f, 0f, 1.8f, 1.5f, 1.2f, 0.9f };
        private static readonly float[] EveryMax   = { 0f, 0f, 2.8f, 2.4f, 2.0f, 1.5f };

        private static Character _target;
        private static int _tier;
        private static float _progress, _pull, _seed, _shown, _digUntil;
        private static Kind _kind;
        private static int _side;              // tap/hold: -1 = it pulls to the left of the screen, 1 = right. mouse: 0 left, 1 right button
        private static float _eventEnd, _nextEvent, _flash, _held;
        private static bool _holding;
        private static Texture2D _white;
        private static GUIStyle _style, _key, _small;

        internal static bool Active { get { return _target != null; } }
        internal static bool Is(Character c) { return c != null && c == _target; }
        // a right click during a bucking moment answers it instead of letting go of the animal
        private static float _rightClickAt = -9f;
        internal static bool AteRightClick { get { return (_target != null && _kind == Kind.Mouse) || Time.time - _rightClickAt < 0.6f; } }

        internal static void Forget(Character c)
        {
            if (c == _target) { _target = null; Effects.SetDigging(Player.m_localPlayer, false); }
        }

        internal static void Start(Character c, Player p)
        {
            ZNetView nv = Leash.View(c);
            nv.ClaimOwnership();
            nv.GetZDO().Set(Leash.Key, p.GetPlayerID());
            Leash.Caught(c);
            _target = c;
            _tier = Mathf.Clamp(Tiers.Tug(c), 1, Tiers.Max);
            _progress = _shown = Plugin.StartProgress.Value;
            _pull = PullByTier[_tier] * Plugin.AnimalPull.Value;
            _seed = Random.value * 10f;
            _kind = Kind.None;
            _nextEvent = Time.time + Random.Range(1.2f, 2f);
            Effects.Shake(1.5f);
            p.Message(MessageHud.MessageType.Center, c.GetHoverName() + " fights the rope! Tap S to pull.");
            Plugin.Debug("[tug] start on " + c.GetHoverName() + " tier " + _tier + ", pulls " + _pull.ToString("0.00") + "/s");
        }

        internal static void LetGo()
        {
            if (_target == null) return;
            Character c = _target;
            _target = null;
            Effects.SetDigging(Player.m_localPlayer, false);
            Leash.Release(c, false);
            Plugin.Debug("[tug] let go");
        }

        internal static void Update()
        {
            if (_target == null) return;
            Player p = Player.m_localPlayer;
            if (p == null || p.IsDead() || _target.IsDead() || Leash.Holder(Leash.View(_target)) != p.GetPlayerID())
            {
                Character c = _target;
                _target = null;
                Effects.SetDigging(p, false);
                if (c != null && !c.IsDead() && p != null) Leash.Release(c, false);
                Plugin.Debug("[tug] stopped");
                return;
            }
            ZNetView nv = Leash.View(_target);
            if (!nv.IsOwner()) nv.ClaimOwnership();

            if (Input.GetKeyDown(KeyCode.S))
            {
                _progress += Plugin.PullPerPress.Value;
                _digUntil = Time.time + 0.4f;
            }
            Effects.SetDigging(p, Time.time < _digUntil);
            _progress -= _pull * Time.deltaTime;

            if (_tier >= 2)
            {
                if (_kind == Kind.None) { if (Time.time >= _nextEvent) NewEvent(); }
                else UpdateEvent();
            }

            if (_progress >= 1f) Win(p);
            else if (_progress <= 0f) Lose(p);
        }

        private static void NewEvent()
        {
            // what can happen at this tier: tier 2 taps; 3 taps and holds; 4-5 taps, holds and bucks
            int kinds = _tier >= 4 ? 3 : _tier >= 3 ? 2 : 1;
            int pick = Random.Range(0, kinds);
            _kind = pick == 0 ? Kind.Tap : pick == 1 ? Kind.Hold : Kind.Mouse;
            _side = _kind == Kind.Mouse ? Random.Range(0, 2) : (Random.value < 0.5f ? -1 : 1);
            _eventEnd = Time.time + Window[_tier];
            _held = 0f;
            _holding = false;
            Effects.Shake(_kind == Kind.Mouse ? 1.8f : 1.2f);
        }

        private static void UpdateEvent()
        {
            switch (_kind)
            {
                case Kind.Tap:
                {
                    bool left = Input.GetKeyDown(KeyCode.A), right = Input.GetKeyDown(KeyCode.D);
                    // it yanks to the right: pull to the left (A), and the other way round
                    bool good = _side > 0 ? left : right, bad = _side > 0 ? right : left;
                    if (good) Good(0.1f);
                    else if (bad || Time.time > _eventEnd) Bad();
                    break;
                }
                case Kind.Hold:
                {
                    KeyCode want = _side > 0 ? KeyCode.A : KeyCode.D, wrong = _side > 0 ? KeyCode.D : KeyCode.A;
                    if (Input.GetKeyDown(wrong)) { Bad(); break; }
                    if (Input.GetKey(want))
                    {
                        _holding = true;
                        _held += Time.deltaTime;
                        if (_held >= HoldTime[_tier]) Good(0.14f);
                    }
                    else if (_holding) Bad();                         // let go too early
                    else if (Time.time > _eventEnd) Bad();            // never started
                    break;
                }
                case Kind.Mouse:
                {
                    bool l = Input.GetMouseButtonDown(0), r = Input.GetMouseButtonDown(1);
                    if (r) _rightClickAt = Time.time;
                    bool good = _side == 0 ? l : r, bad = _side == 0 ? r : l;
                    if (good) Good(0.12f);
                    else if (bad || Time.time > _eventEnd) Bad();
                    break;
                }
            }
        }

        private static void Good(float gain)
        {
            _progress += gain;
            Effects.Shake(0.6f);
            EndEvent();
        }

        private const float MissCost = 0.4f;   // a miss pulls the bar back 40% of the way

        private static void Bad()
        {
            _progress -= MissCost;
            _flash = Time.time + 0.4f;
            Effects.Shake(3f);
            EndEvent();
        }

        private static void EndEvent()
        {
            _kind = Kind.None;
            _holding = false;
            _nextEvent = Time.time + Random.Range(EveryMin[_tier], EveryMax[_tier]);
        }

        private static void Win(Player p)
        {
            Character c = _target;
            _target = null;
            Effects.SetDigging(p, false);
            Effects.Shake(2.5f);
            string name = c.GetHoverName();
            p.Message(MessageHud.MessageType.Center, name + " is caught! Put " + Tiers.Need("Halter", Tiers.Animal(c)) + " on it to tame it.");
            Leash.Grace(c);
            Plugin.Debug("[tug] won: " + name);
        }

        private static void Lose(Player p)
        {
            Character c = _target;
            _target = null;
            Effects.SetDigging(p, false);
            Effects.Shake(2f);
            Leash.Release(c, false);
            p.Message(MessageHud.MessageType.Center, c.GetHoverName() + " broke free!");
            Plugin.Debug("[tug] lost: " + c.GetHoverName());
        }

        // Runs where the animal is simulated (our game, we own it): it bolts away, zig-zagging, or pulls hard to
        // one side, or rears back; the rope holds it within TugRope metres.
        internal static void Drive(BaseAI ai, Character c, Player p, float dt)
        {
            Vector3 pp = p.transform.position, cp = c.transform.position;
            Vector3 away = cp - pp;
            away.y = 0f;
            if (away.sqrMagnitude < 0.01f) away = -p.transform.forward;
            away.Normalize();
            Vector3 side = GameCamera.instance != null ? GameCamera.instance.transform.right : Vector3.Cross(Vector3.up, away);
            side.y = 0f;
            side.Normalize();
            Vector3 goal;
            if (_kind == Kind.Tap || _kind == Kind.Hold) goal = pp + away * TugRope + side * (_side * 9f);
            else if (_kind == Kind.Mouse) goal = pp + away * (TugRope + 8f);
            else goal = pp + away * (TugRope + 4f) + side * Mathf.Sin(Time.time * 1.6f + _seed) * 4f;
            Leash.MoveTo(ai, dt, goal, true);

            Vector3 flat = cp - pp;
            flat.y = 0f;
            if (flat.magnitude > TugRope)
            {
                Vector3 held = pp + flat.normalized * TugRope;
                held.y = cp.y;
                c.transform.position = held;
                Rigidbody body = Leash.Body(c);
                if (body != null)
                {
                    body.position = held;
                    Vector3 v = body.linearVelocity;
                    float outward = Vector3.Dot(v, flat.normalized);
                    if (outward > 0f) body.linearVelocity = v - flat.normalized * outward;
                }
            }
        }

        // ------------------------------------------------------------ the bar

        internal static void OnGUI()
        {
            if (_target == null) return;
            if (_white == null) { _white = new Texture2D(1, 1); _white.SetPixel(0, 0, Color.white); _white.Apply(); }
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 18, fontStyle = FontStyle.Bold };
                _key = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 30, fontStyle = FontStyle.Bold };
                _small = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 16, fontStyle = FontStyle.Bold };
            }
            _shown = Mathf.Lerp(_shown, Mathf.Clamp01(_progress), Time.unscaledDeltaTime * 12f);
            float w = 420f, h = 26f;
            Rect bar = new Rect((Screen.width - w) / 2f, Screen.height * 0.70f, w, h);

            bool hurt = Time.time < _flash;
            GUI.color = hurt ? new Color(0.7f, 0.1f, 0.05f, 0.8f) : new Color(0f, 0f, 0f, 0.65f);
            GUI.DrawTexture(new Rect(bar.x - 3f, bar.y - 3f, bar.width + 6f, bar.height + 6f), _white);
            GUI.color = new Color(0.25f, 0.18f, 0.1f, 0.9f);
            GUI.DrawTexture(bar, _white);
            GUI.color = Color.Lerp(new Color(0.85f, 0.25f, 0.15f), new Color(0.45f, 0.85f, 0.3f), _shown);
            GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * _shown, bar.height), _white);

            string text = "Pull " + _target.GetHoverName() + " in!  Tap  S   " + new string('*', _tier);
            Rect lr = new Rect(bar.x - 100f, bar.y - 32f, bar.width + 200f, 28f);
            Label(lr, text, _style, new Color(1f, 0.92f, 0.7f));

            float pulse = 0.75f + 0.25f * Mathf.Sin(Time.time * 18f);
            Color gold = new Color(1f, 0.85f, 0.35f, pulse);
            if (_kind == Kind.Tap || _kind == Kind.Hold)
            {
                // the key sits on the side you must pull toward
                bool pressA = _side > 0;
                float size = 46f;
                float x = pressA ? bar.x - size - 18f : bar.xMax + 18f;
                Rect box = new Rect(x, bar.center.y - size / 2f, size, size);
                GUI.color = new Color(0f, 0f, 0f, 0.7f);
                GUI.DrawTexture(box, _white);
                float fill = _kind == Kind.Hold && _holding
                    ? Mathf.Clamp01(_held / HoldTime[_tier])                       // hold: fills up while you hold
                    : Mathf.Clamp01((_eventEnd - Time.time) / Window[_tier]);      // else: time left, shrinking
                GUI.color = _kind == Kind.Hold && _holding ? new Color(0.45f, 0.9f, 0.35f, 1f) : gold;
                GUI.DrawTexture(new Rect(box.x, box.yMax - 5f, box.width * fill, 5f), _white);
                Label(box, pressA ? "A" : "D", _key, gold);
                string say = _kind == Kind.Hold ? (pressA ? "< HOLD" : "HOLD >") : (pressA ? "< tap!" : "tap! >");
                Label(new Rect(box.x - 40f, box.y - 26f, box.width + 80f, 24f), say, _style, gold);
            }
            else if (_kind == Kind.Mouse)
            {
                // it rears back: the mouse button to click, above the bar
                float bw = 90f, bh = 44f;
                Rect box = new Rect(bar.center.x - bw / 2f, bar.y - 40f - bh - 34f, bw, bh);
                GUI.color = new Color(0f, 0f, 0f, 0.75f);
                GUI.DrawTexture(box, _white);
                GUI.color = gold;
                float left = Mathf.Clamp01((_eventEnd - Time.time) / Window[_tier]);
                GUI.DrawTexture(new Rect(box.x, box.yMax - 5f, box.width * left, 5f), _white);
                Label(box, _side == 0 ? "LMB" : "RMB", _key, gold);
                Label(new Rect(box.x - 80f, box.y - 24f, box.width + 160f, 22f), _side == 0 ? "it rears! left click" : "it rears! right click", _small, gold);
            }
            GUI.color = Color.white;
        }

        private static void Label(Rect r, string text, GUIStyle style, Color col)
        {
            GUI.color = new Color(0f, 0f, 0f, 0.85f * col.a);
            GUI.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), text, style);
            GUI.color = col;
            GUI.Label(r, text, style);
        }
    }
}
