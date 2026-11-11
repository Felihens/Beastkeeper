using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Beastkeeper
{
    // Doors. The game's path finding doesn't lead animals through doors, so one on your lasso (or a tamed one
    // following you) used to stop in front of the doorway. Now, when it gets no closer for a moment it walks
    // straight at you; if it's still stuck after StuckSeconds and you're near, the rope pulls it through to you.
    internal static class Squeeze
    {
        private const float StraightAfter = 1.5f, StuckSeconds = 4f, MaxReach = 20f;
        private static readonly MethodInfo MoveTowards = AccessTools.Method(typeof(BaseAI), "MoveTowards", new[] { typeof(Vector3), typeof(bool) });

        private class State { public float Best; public float Since; }
        private static readonly Dictionary<Character, State> States = new Dictionary<Character, State>();

        internal static void Forget(Character c) { States.Remove(c); }

        private static float Flat(Vector3 a, Vector3 b) { a.y = 0f; b.y = 0f; return Vector3.Distance(a, b); }

        // call after the animal was told where to go; near = how close it means to stay to the target
        internal static void Check(BaseAI ai, Character c, Transform target, float near, bool run)
        {
            if (ai == null || c == null || target == null || Sea.IsSeaMount(c) || c.IsSwimming()) { Forget(c); return; }
            Vector3 pos = c.transform.position, to = target.position;
            float d = Flat(pos, to);
            if (d <= near || d > MaxReach) { Forget(c); return; }
            State s;
            if (!States.TryGetValue(c, out s))
            {
                s = new State { Best = d, Since = Time.time };
                States[c] = s;
                if (States.Count > 100) States.Clear();
                return;
            }
            if (d < s.Best - 0.5f) { s.Best = d; s.Since = Time.time; return; }   // getting closer: fine
            float stuck = Time.time - s.Since;
            if (stuck > StuckSeconds)
            {
                Vector3 r = Random.insideUnitCircle * 0.7f;
                Vector3 at = new Vector3(to.x + r.x, to.y + 0.1f, to.z + r.y);
                Rigidbody body = Leash.Body(c);
                if (body != null) { body.velocity = Vector3.zero; body.position = at; }
                c.transform.position = at;
                Forget(c);
                Plugin.Debug("[squeeze] " + c.name + " was stuck " + d.ToString("0.0") + " m away: pulled through to " + target.name);
                return;
            }
            if (stuck > StraightAfter && MoveTowards != null)
            {
                Vector3 dir = to - pos;
                dir.y = 0f;
                MoveTowards.Invoke(ai, new object[] { dir.normalized, run });
            }
        }
    }
}
