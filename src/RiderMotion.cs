using System;
using System.IO;
using UnityEngine;

namespace Beastkeeper
{
    // How the saddle and the rider move with each mount's walk or run, posed frame by frame in the rider editor.
    // For every mount and movement (walk, run) the data holds a loop of frames: the saddle's shift and turn, the
    // rider's seat shift, and a turn for some of the rider's bones. The mod plays the loop in step with the
    // mount's own animation (its animator's loop position), blended in while it moves and out when it stops.
    // The data ships inside the mod (res/riders.json). A file Beastkeeper_models/riders.json, when there is one,
    // is used instead and read again when it changes, so the numbers can be tuned with the game open.
    internal static class RiderMotion
    {
        [Serializable] public class Root { public MountData[] mounts; }
        [Serializable] public class MountData { public string kind; public ClipData[] clips; }
        [Serializable] public class ClipData { public string label; public float cycle; public string[] bones; public FrameData[] frames; }
        [Serializable] public class FrameData { public float[] q; public float[] seat; public float[] sp; public float[] sq; }

        // one moment of the loop, for the mount being drawn (reused, so nothing is allocated each frame)
        internal class Frame
        {
            internal string[] Bones;          // the rider's bones that turn
            internal Quaternion[] Turn;       // their turns (on top of the game's pose), already blended by the weight
            internal Vector3 Seat;            // the rider's shift, in the animal's frame
            internal Vector3 SaddlePos;       // the saddle's shift, in the animal's frame
            internal Quaternion SaddleTurn = Quaternion.identity;   // and its turn about its own middle, on the animal's axes
            internal object Clip;
        }

        private static Root _data;
        private static bool _said;
        private static DateTime _stamp;
        private static float _nextCheck;
        private static bool _loaded;

        private static string FilePath { get { return Path.Combine(Path.Combine(BepInEx.Paths.ConfigPath, "Beastkeeper_models"), "riders.json"); } }

        private static Root Read(string text)
        {
            System.Collections.Generic.Dictionary<string, object> o = MiniJson.Obj(MiniJson.Parse(text));
            System.Collections.Generic.List<object> ms = MiniJson.Arr(MiniJson.Get(o, "mounts"));
            Root r = new Root { mounts = new MountData[ms != null ? ms.Count : 0] };
            for (int i = 0; i < r.mounts.Length; i++)
            {
                System.Collections.Generic.Dictionary<string, object> m = MiniJson.Obj(ms[i]);
                System.Collections.Generic.List<object> cs = MiniJson.Arr(MiniJson.Get(m, "clips"));
                MountData md = new MountData { kind = MiniJson.Str(MiniJson.Get(m, "kind")), clips = new ClipData[cs != null ? cs.Count : 0] };
                for (int j = 0; j < md.clips.Length; j++)
                {
                    System.Collections.Generic.Dictionary<string, object> c = MiniJson.Obj(cs[j]);
                    System.Collections.Generic.List<object> fs = MiniJson.Arr(MiniJson.Get(c, "frames"));
                    ClipData cd = new ClipData { label = MiniJson.Str(MiniJson.Get(c, "label")), cycle = MiniJson.Num(MiniJson.Get(c, "cycle")), bones = MiniJson.Strings(MiniJson.Get(c, "bones")), frames = new FrameData[fs != null ? fs.Count : 0] };
                    for (int k = 0; k < cd.frames.Length; k++)
                    {
                        System.Collections.Generic.Dictionary<string, object> f = MiniJson.Obj(fs[k]);
                        cd.frames[k] = new FrameData { q = MiniJson.Floats(MiniJson.Get(f, "q")), seat = MiniJson.Floats(MiniJson.Get(f, "seat")), sp = MiniJson.Floats(MiniJson.Get(f, "sp")), sq = MiniJson.Floats(MiniJson.Get(f, "sq")) };
                    }
                    md.clips[j] = cd;
                }
                r.mounts[i] = md;
            }
            return r;
        }

        private static void Load()
        {
            if (_loaded && Time.time < _nextCheck) return;
            _nextCheck = Time.time + 3f;
            try
            {
                string p = FilePath;
                if (File.Exists(p))
                {
                    DateTime t = File.GetLastWriteTimeUtc(p);
                    if (_loaded && t == _stamp) return;
                    _data = Read(File.ReadAllText(p));
                    _stamp = t;
                    Plugin.Debug("[rider] loaded riders.json from the config folder");
                }
                else if (!_loaded)
                {
                    using (Stream s = typeof(RiderMotion).Assembly.GetManifestResourceStream("Beastkeeper.riders.json"))
                    {
                        if (s != null) using (StreamReader r = new StreamReader(s)) _data = Read(r.ReadToEnd());
                    }
                    if (_data != null && _data.mounts != null) Plugin.Debug("[rider] saddle and rider motion for " + _data.mounts.Length + " mounts");
                }
                if ((_data == null || _data.mounts == null || _data.mounts.Length == 0) && !_said) { _said = true; Plugin.Log.LogWarning("[rider] the saddle and rider motion data could not be read: no motion"); }
                _loaded = true;
            }
            catch (Exception e) { Plugin.Log.LogWarning("[rider] couldn't read the rider motion: " + e.Message); _loaded = true; }
        }

        private static ClipData Find(string kind, string label)
        {
            Load();
            if (_data == null || _data.mounts == null) return null;
            foreach (MountData m in _data.mounts)
            {
                if (m == null || m.clips == null || !string.Equals(m.kind, kind, StringComparison.OrdinalIgnoreCase)) continue;
                ClipData only = null;
                int n = 0;
                foreach (ClipData c in m.clips)
                {
                    if (c == null || c.frames == null || c.frames.Length == 0) continue;
                    if (c.label == label) return c;
                    only = c; n++;
                }
                // a movement nobody posed stays on the base seat (the walk was checked as it is); the one exception is
                // an animal with a single gait, like the seal, whose one loop serves for any speed
                return n == 1 && SingleGait(kind) ? only : null;
            }
            return null;
        }

        private static bool SingleGait(string kind) { return string.Equals(kind, "Seal", StringComparison.OrdinalIgnoreCase); }

        internal static bool Has(string kind) { return Find(kind, "run") != null; }

        // the loop of this mount and movement at 'phase' (0..1 of its animation's loop), blended by 'weight'; false = none
        internal static bool At(string kind, string label, float phase, float weight, Frame f)
        {
            ClipData c = Find(kind, label);
            if (c == null) return false;
            int n = c.frames.Length;
            float x = Mathf.Repeat(phase, 1f) * n;
            int i0 = Mathf.FloorToInt(x) % n, i1 = (i0 + 1) % n;
            float k = x - Mathf.Floor(x);
            FrameData a = c.frames[i0], b = c.frames[i1];
            int nb = c.bones != null ? c.bones.Length : 0;
            if (f.Clip != c)
            {
                f.Clip = c;
                f.Bones = c.bones ?? new string[0];
                f.Turn = new Quaternion[nb];
            }
            for (int i = 0; i < nb; i++)
            {
                if (a.q == null || b.q == null || a.q.Length < i * 4 + 4 || b.q.Length < i * 4 + 4) { f.Turn[i] = Quaternion.identity; continue; }
                Quaternion qa = new Quaternion(a.q[i * 4], a.q[i * 4 + 1], a.q[i * 4 + 2], a.q[i * 4 + 3]);
                Quaternion qb = new Quaternion(b.q[i * 4], b.q[i * 4 + 1], b.q[i * 4 + 2], b.q[i * 4 + 3]);
                f.Turn[i] = Quaternion.Slerp(Quaternion.identity, Quaternion.Slerp(qa, qb, k), weight);
            }
            f.Seat = V3(a.seat, b.seat, k) * weight;
            f.SaddlePos = V3(a.sp, b.sp, k) * weight;
            f.SaddleTurn = Quaternion.Slerp(Quaternion.identity, Quaternion.Slerp(Q4(a.sq), Q4(b.sq), k), weight);
            return true;
        }

        private static Vector3 V3(float[] a, float[] b, float k)
        {
            if (a == null || b == null || a.Length < 3 || b.Length < 3) return Vector3.zero;
            return Vector3.Lerp(new Vector3(a[0], a[1], a[2]), new Vector3(b[0], b[1], b[2]), k);
        }

        private static Quaternion Q4(float[] a)
        {
            return a != null && a.Length >= 4 ? new Quaternion(a[0], a[1], a[2], a[3]) : Quaternion.identity;
        }
    }
}
