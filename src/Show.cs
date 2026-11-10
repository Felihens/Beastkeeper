using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Beastkeeper
{
    // Lets everyone with the mod see your lasso work, not only you:
    //  - while you aim, a flag on your character says so, and every game draws the loop spinning over your hand;
    //  - when you throw, a message tells every game where the loop left your hand and how fast, so they fly
    //    the same arc; a second message says how it ended (caught that animal, or landed there).
    // Your game alone decides what the loop hits; the others only show it.
    internal static class Show
    {
        private const string Rpc = "Beastkeeper_Throw";
        private const int Start = 0, End = 1;
        private static readonly int SpinKey = "Beastkeeper_spin".GetStableHashCode();

        private class Spin
        {
            public LineRenderer Loop, Rope;
            public float Angle;
        }

        private class Ending
        {
            public ZDOID Caught;
            public Vector3 At;
            public float When;
        }

        private static readonly Dictionary<Player, Spin> Spins = new Dictionary<Player, Spin>();
        private static readonly Dictionary<string, Ending> Endings = new Dictionary<string, Ending>();
        private static ZRoutedRpc _registeredOn;

        // ---------------------------------------------------------------- the spinning loop

        internal static void SetSpinning(Player p, bool on)
        {
            if (p == null) return;
            ZNetView nv = p.GetComponent<ZNetView>();
            if (nv == null || !nv.IsValid()) return;
            int want = on ? 1 : 0;
            if (nv.GetZDO().GetInt(SpinKey, 0) != want) nv.GetZDO().Set(SpinKey, want);   // also clears a flag left from last time
        }

        internal static void Update()
        {
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (rpc != null && rpc != _registeredOn)
            {
                rpc.Register<ZPackage>(Rpc, new Action<long, ZPackage>(OnMessage));
                _registeredOn = rpc;
            }

            foreach (Player p in Player.GetAllPlayers())
            {
                if (p == null) continue;
                ZNetView nv = p.GetComponent<ZNetView>();
                bool on = nv != null && nv.IsValid() && !p.IsDead() && nv.GetZDO().GetInt(SpinKey, 0) == 1;
                Spin s;
                Spins.TryGetValue(p, out s);
                if (!on) { if (s != null) { Drop(s); Spins.Remove(p); } continue; }
                if (s == null)
                {
                    s = new Spin { Loop = Throw.NewLoop(p.transform, "Beastkeeper_SpinLoop"), Rope = Rope.NewLine(p.transform, "Beastkeeper_SpinRope") };
                    Spins[p] = s;
                }
                Draw(p, s);
            }
            // players who left
            if (Spins.Count > 0)
            {
                List<Player> gone = null;
                foreach (KeyValuePair<Player, Spin> kv in Spins)
                    if (kv.Key == null) { if (gone == null) gone = new List<Player>(); gone.Add(kv.Key); Drop(kv.Value); }
                if (gone != null) foreach (Player g in gone) Spins.Remove(g);
            }
        }

        private static void Draw(Player p, Spin s)
        {
            s.Angle += Time.deltaTime * Mathf.PI * 2f * 1.6f;      // 1.6 turns a second
            Vector3 hand = Rope.RightHand(p);
            // the loop turns flat over the hand, a little tilted, its middle circling the hand
            Vector3 swirl = new Vector3(Mathf.Cos(s.Angle), 0f, Mathf.Sin(s.Angle));
            Vector3 center = hand + Vector3.up * 0.45f + swirl * 0.22f;
            Vector3 normal = (Vector3.up + swirl * 0.3f).normalized;
            Throw.LayLoop(s.Loop, center, normal, 0.6f, s.Angle);
            // the rope from the hand to the knot of the loop (the point nearest the hand)
            Vector3 knot = center + Vector3.ProjectOnPlane(hand - center, normal).normalized * 0.6f;
            Rope.Lay(s.Rope, hand, knot, 0f);
        }

        private static void Drop(Spin s)
        {
            if (s.Loop != null) UnityEngine.Object.Destroy(s.Loop.gameObject);
            if (s.Rope != null) UnityEngine.Object.Destroy(s.Rope.gameObject);
        }

        // ---------------------------------------------------------------- the throw

        internal static void SendStart(int id, Vector3 pos, Vector3 vel, float range)
        {
            ZPackage p = new ZPackage();
            p.Write(Start);
            p.Write(id);
            p.Write(pos);
            p.Write(vel);
            p.Write(range);
            Send(p);
        }

        internal static void SendEnd(int id, ZDOID caught, Vector3 at)
        {
            ZPackage p = new ZPackage();
            p.Write(End);
            p.Write(id);
            p.Write(caught);
            p.Write(at);
            Send(p);
        }

        private static void Send(ZPackage p)
        {
            if (ZRoutedRpc.instance != null) ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, Rpc, new object[] { p });
        }

        private static void OnMessage(long sender, ZPackage pkg)
        {
            try
            {
                if (sender == ZNet.GetUID()) return;   // our own throw: already shown
                int kind = pkg.ReadInt(), id = pkg.ReadInt();
                string key = sender + ":" + id;
                if (kind == Start)
                {
                    Vector3 pos = pkg.ReadVector3(), vel = pkg.ReadVector3();
                    float range = pkg.ReadSingle();
                    Player thrower = Thrower(sender);
                    if (thrower == null || Player.m_localPlayer == null) return;
                    if (Vector3.Distance(thrower.transform.position, Player.m_localPlayer.transform.position) > 120f) return;
                    Plugin.Instance.StartCoroutine(Fly(thrower, key, pos, vel, range));
                }
                else if (kind == End)
                {
                    Ending e = new Ending { Caught = pkg.ReadZDOID(), At = pkg.ReadVector3(), When = Time.time };
                    Endings[key] = e;
                    if (Endings.Count > 30) Endings.Clear();
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Lasso message failed: " + e.Message); }
        }

        private static Player Thrower(long sender)
        {
            foreach (Player p in Player.GetAllPlayers())
                if (p != null && p.GetZDOID().UserID == sender) return p;
            return null;
        }

        // Someone else's loop: the same arc as on their screen, until their game says how it ended.
        private static IEnumerator Fly(Player p, string key, Vector3 pos, Vector3 vel, float range)
        {
            LineRenderer rope = Rope.NewLine(null, "Beastkeeper_ThrowRope");
            LineRenderer loop = Throw.NewLoop(null, "Beastkeeper_ThrowLoop");
            float flown = 0f;
            bool lying = false;
            Ending end = null;
            while (p != null && flown < 4f)
            {
                if (Endings.TryGetValue(key, out end)) { Endings.Remove(key); break; }
                float dt = Time.deltaTime;
                flown += dt;
                Vector3 hand = Rope.RightHand(p);
                if (!lying)
                {
                    Vector3 next = pos + vel * dt;
                    vel += Vector3.down * Throw.Gravity * dt;
                    Vector3 out_ = next - hand;
                    if (out_.magnitude > range) { next = hand + out_.normalized * range; vel = new Vector3(0f, Mathf.Min(vel.y, 0f), 0f); }
                    float g = Throw.Ground(next);
                    if (next.y <= g) { next.y = g; lying = true; }
                    pos = next;
                }
                Throw.LayLoop(loop, pos + (lying ? Vector3.up * 0.03f : Vector3.zero), lying ? Vector3.up : (vel.normalized + Vector3.up).normalized, Throw.LoopRadius, flown * 6f);
                Rope.Lay(rope, hand, pos, Mathf.Clamp01(flown * 2f) * 0.4f);
                yield return null;
            }

            Character caught = null;
            if (end != null && !end.Caught.IsNone() && ZNetScene.instance != null)
            {
                GameObject go = ZNetScene.instance.FindInstance(end.Caught);
                caught = go != null ? go.GetComponent<Character>() : null;
            }
            if (caught != null && p != null)
            {
                Transform neck = null;
                Vector3 from = pos;
                for (float t = 0f; t < 0.2f && caught != null && p != null; t += Time.deltaTime)
                {
                    Vector3 n = Rope.Neck(caught, ref neck);
                    Throw.LayLoop(loop, Vector3.Lerp(from, n, t / 0.2f), Vector3.up, Mathf.Lerp(Throw.LoopRadius, 0.18f, t / 0.2f), t * 6f);
                    Rope.Lay(rope, Rope.RightHand(p), n, 0.2f);
                    yield return null;
                }
            }
            else if (p != null)
            {
                if (end != null) pos = end.At;
                for (float t = 0f; t < 0.5f && p != null; t += Time.deltaTime)
                {
                    Throw.LayLoop(loop, pos + Vector3.up * 0.03f, Vector3.up, Throw.LoopRadius, 0f);
                    Rope.Lay(rope, Rope.RightHand(p), pos, 0.6f);
                    yield return null;
                }
                Vector3 start = pos;
                for (float t = 0f; t < 0.45f && p != null; t += Time.deltaTime)
                {
                    Vector3 hand = Rope.RightHand(p);
                    Vector3 q = Vector3.Lerp(start, hand, t / 0.45f);
                    q.y = Mathf.Max(Throw.Ground(q) + 0.03f, Mathf.Lerp(start.y, hand.y, Mathf.Pow(t / 0.45f, 3f)));
                    Throw.LayLoop(loop, q, Vector3.up, Mathf.Lerp(Throw.LoopRadius, 0.15f, t / 0.45f), 0f);
                    Rope.Lay(rope, hand, q, 0.3f);
                    yield return null;
                }
            }
            if (rope != null) UnityEngine.Object.Destroy(rope.gameObject);
            if (loop != null) UnityEngine.Object.Destroy(loop.gameObject);
        }
    }
}
