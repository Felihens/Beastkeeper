using System;
using System.IO;
using System.Collections.Generic;
using UnityEngine;

namespace Beastkeeper
{
    // The walk of a tamed drake on the ground, posed frame by frame in the drake walk editor.
    // The game's drake does walk (its animator has a Walk clip), and that is what you see until a walk is
    // saved: put the editor's file in BepInEx/config/Beastkeeper_models/drake_walk.json and the drake plays
    // those frames instead. The frames are rotations of its bones (Spine1, Shoulder.l, Tail3...), blended over
    // the game's own pose after the animator has run each frame, so nothing jumps when it starts or stops.
    // The cycle follows the distance it covers (MetresPerCycle of the file), so the steps never slide.
    // The file is read again whenever it changes, so it can be edited with the game open.
    internal class DrakeWalk : MonoBehaviour
    {
        [Serializable] public class WalkFile { public float cycleSeconds; public float metresPerCycle; public string[] bones; public WalkFrame[] frames; }
        [Serializable] public class WalkFrame { public float[] q; public float[] root; }

        private static WalkFile _file, _embedded;
        private static DateTime _stamp;
        private static float _nextCheck;
        private static bool _warned;
        private static string Path { get { return System.IO.Path.Combine(System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "Beastkeeper_models"), "drake_walk.json"); } }

        private static WalkFile Read(string text)
        {
            Dictionary<string, object> o = MiniJson.Obj(MiniJson.Parse(text));
            if (o == null) return null;
            WalkFile f = new WalkFile();
            f.cycleSeconds = MiniJson.Num(MiniJson.Get(o, "cycleSeconds"), 0.8f);
            f.metresPerCycle = MiniJson.Num(MiniJson.Get(o, "metresPerCycle"), 2f);
            f.bones = MiniJson.Strings(MiniJson.Get(o, "bones"));
            List<object> fr = MiniJson.Arr(MiniJson.Get(o, "frames"));
            f.frames = new WalkFrame[fr != null ? fr.Count : 0];
            for (int i = 0; i < f.frames.Length; i++)
            {
                Dictionary<string, object> d = MiniJson.Obj(fr[i]);
                f.frames[i] = new WalkFrame { q = MiniJson.Floats(MiniJson.Get(d, "q")), root = MiniJson.Floats(MiniJson.Get(d, "root")) };
            }
            return f;
        }

        private static WalkFile Load()
        {
            if (Time.time < _nextCheck) return _file;
            _nextCheck = Time.time + 2f;
            try
            {
                string p = Path;
                if (!File.Exists(p))
                {
                    // the walk that ships with the mod
                    if (_embedded == null && !_warned)
                    {
                        _warned = true;
                        using (Stream s = typeof(DrakeWalk).Assembly.GetManifestResourceStream("Beastkeeper.drake_walk.json"))
                            if (s != null) using (StreamReader r = new StreamReader(s)) _embedded = Read(r.ReadToEnd());
                        if (_embedded == null || _embedded.frames == null || _embedded.frames.Length == 0) { _embedded = null; Plugin.Log.LogWarning("[walk] the drake walk could not be read"); }
                        else Plugin.Debug("[walk] loaded the built-in walk: " + _embedded.frames.Length + " frames");
                    }
                    if (_file != _embedded) _bonesFor = null;
                    _file = _embedded;
                    return _file;
                }
                DateTime t = File.GetLastWriteTimeUtc(p);
                if (_file != null && t == _stamp) return _file;
                WalkFile f = Read(File.ReadAllText(p));
                if (f == null || f.frames == null || f.frames.Length == 0 || f.bones == null) { Plugin.Log.LogWarning("[walk] drake_walk.json has no frames or bones (" + (f == null ? "nothing read" : (f.frames == null ? 0 : f.frames.Length) + " frames, " + (f.bones == null ? 0 : f.bones.Length) + " bones") + ")"); _file = null; return null; }
                _file = f; _stamp = t; _bonesFor = null;
                Plugin.Debug("[walk] loaded " + f.frames.Length + " frames, " + f.bones.Length + " bones, " + f.metresPerCycle.ToString("0.0") + " m a cycle");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[walk] couldn't read drake_walk.json: " + e.Message); _file = null; }
            return _file;
        }

        private static WalkFile _bonesFor;
        private Character _c;
        private Transform[] _bones;
        private Transform _root;
        private WalkFile _mine;
        private Vector3 _last;
        private float _phase, _weight, _speed;
        private bool _moving;

        // where it is in its walk loop (0..1), for the saddle and rider motion that goes with it
        internal float Phase { get { return Mathf.Repeat(_phase, 1f); } }

        private void Awake()
        {
            _c = GetComponent<Character>();
            _last = transform.position;
        }

        private void Bind(WalkFile f)
        {
            _mine = f;
            _bones = new Transform[f.bones.Length];
            Dictionary<string, Transform> all = new Dictionary<string, Transform>();
            foreach (Transform t in GetComponentsInChildren<Transform>(true)) if (!all.ContainsKey(t.name)) all[t.name] = t;
            for (int i = 0; i < f.bones.Length; i++) { Transform t; all.TryGetValue(f.bones[i], out t); _bones[i] = t; if (t == null) Plugin.Debug("[walk] no bone " + f.bones[i]); }
            _root = null;
            all.TryGetValue("Root", out _root);
        }

        // the editor works mirrored on x (three.js); the game's frame is the other way
        private static Quaternion Q(float[] a, int i) { return new Quaternion(a[i], -a[i + 1], -a[i + 2], a[i + 3]); }

        // debug: once a second, what the game's animator is doing with this drake (why its own walk may not show)
        private float _nextProbe;
        private Animator _an;
        private void Probe(bool ground, float speed)
        {
            if (Time.time < _nextProbe) return;
            _nextProbe = Time.time + 1.5f;
            if (!ground) { Plugin.Debug("[walk] flying (speed " + speed.ToString("0.0") + " m/s), walk file with " + (_mine != null ? _mine.frames.Length : 0) + " frames"); return; }
            if (_an == null) _an = GetComponentInChildren<Animator>();
            if (_an == null) { Plugin.Debug("[walk] no animator"); return; }
            string state = "?";
            foreach (string n in new[] { "Movement_Ground", "Movement_Flying", "Land", "Takeoff", "Jump", "attack", "In Water" })
                if (_an.GetCurrentAnimatorStateInfo(0).IsName(n)) { state = n; break; }
            Plugin.Debug("[walk] on the ground: state " + state + ", speed " + speed.ToString("0.0") + " m/s, animator forward_speed " + _an.GetFloat("forward_speed").ToString("0.0")
                + ", flying " + _an.GetBool("flying") + ", animator speed " + _an.speed.ToString("0.00") + ", moveDir " + _c.GetMoveDir().magnitude.ToString("0.0"));
        }

        private void LateUpdate()
        {
            if (_c == null || !Plugin.Enabled.Value || !_c.IsTamed() || _c.IsDead()) return;
            WalkFile f = Load();
            if (f == null) return;
            if (f != _mine) Bind(f);
            // How fast it goes now, in any direction: from its body's own velocity, which is smooth. (The position only
            // changes on the game's physics steps, so a speed from it jumps between zero and a lot from frame to frame:
            // the walk then kept falling back to its first frame, and only ran once it went fast.)
            Vector3 vel = _c.GetVelocity();
            vel.y = 0f;
            float dt = Mathf.Max(0.0001f, Time.deltaTime);
            _speed = Mathf.Lerp(_speed, vel.magnitude, Mathf.Min(1f, dt * 12f));
            bool ground = !_c.IsFlying();
            _moving = ground && (_moving ? _speed > 0.02f : _speed > 0.05f);   // any speed at all is a walk
            bool walking = _moving;
            Probe(ground, _speed);
            // on the ground the frames always rule: walking plays them, standing still settles on the first frame
            // (the game's own standing pose is the one it is built in, upright, tail hanging)
            _weight = Mathf.Lerp(_weight, ground ? 1f : 0f, dt * 8f);
            if (_weight < 0.01f) return;
            if (walking) _phase += _speed * dt / Mathf.Max(0.3f, f.metresPerCycle);
            else if (_speed < 0.02f) _phase = Mathf.MoveTowards(_phase, Mathf.Round(_phase), dt * 0.6f);

            float pos = Mathf.Repeat(_phase, 1f) * f.frames.Length;
            int i0 = Mathf.FloorToInt(pos) % f.frames.Length, i1 = (i0 + 1) % f.frames.Length;
            float k = pos - Mathf.Floor(pos);
            float[] a = f.frames[i0].q, b = f.frames[i1].q;
            for (int i = 0; i < _bones.Length; i++)
            {
                Transform t = _bones[i];
                if (t == null || a == null || b == null || a.Length < i * 4 + 4 || b.Length < i * 4 + 4) continue;
                Quaternion ours = Quaternion.Slerp(Q(a, i * 4), Q(b, i * 4), k);
                t.localRotation = Quaternion.Slerp(t.localRotation, ours, _weight);
            }
            if (_root != null && f.frames[i0].root != null && f.frames[i1].root != null && f.frames[i0].root.Length >= 3 && f.frames[i1].root.Length >= 3)
            {
                float[] r0 = f.frames[i0].root, r1 = f.frames[i1].root;
                Vector3 ours = new Vector3(-Mathf.Lerp(r0[0], r1[0], k), Mathf.Lerp(r0[1], r1[1], k), Mathf.Lerp(r0[2], r1[2], k));
                _root.localPosition = Vector3.Lerp(_root.localPosition, ours, _weight);
            }
        }
    }
}
