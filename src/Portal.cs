using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Beastkeeper
{
    // Portals (user, 2026-11-16):
    //  - Ride into a portal: you and your animal go through together, and you are on its back again on the other side.
    //  - Animals on your lasso go through with you and stay on the rope.
    // When the game sends you through a portal, the animals are moved to its exit at once (their saved place, so they
    // are there when that land loads). While you travel and for a few seconds after, the rope doesn't slip off.
    [HarmonyPatch]
    internal static class Portal
    {
        private const float QuietAfter = 8f, RemountWait = 15f, Touch = 0.1f;
        private static bool _viaPortal;
        private static float _quietUntil, _remountUntil, _nextTouch, _lastTry;
        private static ZDOID _remount = ZDOID.None;
        private static TeleportWorld _lastPortal;
        private static readonly MethodInfo TeleportM = AccessTools.Method(typeof(TeleportWorld), "Teleport", new[] { typeof(Player) });

        internal static bool Quiet() { return Time.time < _quietUntil; }

        [HarmonyPatch(typeof(TeleportWorld), "Teleport"), HarmonyPrefix]
        private static void Before(Player player) { _viaPortal = player != null && player == Player.m_localPlayer; }

        [HarmonyPatch(typeof(TeleportWorld), "Teleport"), HarmonyPostfix]
        private static void After() { _viaPortal = false; }

        [HarmonyPatch(typeof(Player), "TeleportTo"), HarmonyPostfix]
        private static void Went(Player __instance, Vector3 pos, Quaternion rot, bool __result)
        {
            if (!_viaPortal || !__result || __instance != Player.m_localPlayer || !Plugin.Enabled.Value) return;
            _viaPortal = false;
            Player p = __instance;
            _quietUntil = Time.time + 30f;   // kept going while you travel (Update)
            Vector3 fwd = rot * Vector3.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
            fwd.Normalize();
            Vector3 side = Vector3.Cross(Vector3.up, fwd);

            Character ridden = Mount.LeaveForPortal(p);
            if (ridden != null)
            {
                Send(ridden, pos + fwd * (ridden.GetRadius() + 0.5f), rot);
                ZNetView nv = ridden.GetComponent<ZNetView>();
                _remount = nv != null && nv.IsValid() ? nv.GetZDO().m_uid : ZDOID.None;
                _remountUntil = Time.time + 60f;
                Plugin.Debug("[portal] " + ridden.name + " goes through with you; you'll ride it again on the other side");
            }

            long me = p.GetPlayerID();
            int n = 0;
            foreach (Character c in Character.GetAllCharacters())
            {
                if (c == null || c.IsPlayer() || c.IsDead() || c == ridden) continue;
                if (Leash.Holder(Leash.View(c)) != me) continue;
                float sideWays = (n % 2 == 0 ? 1f : -1f) * (1.5f + c.GetRadius() + 1f * (n / 2));
                Send(c, pos + side * sideWays + fwd * (1f + c.GetRadius()), rot);
                n++;
            }
            if (n > 0) Plugin.Debug("[portal] " + n + " animal(s) on your lasso go through with you");
        }

        // to the other side: its saved place too, so it is there when that land loads
        private static void Send(Character c, Vector3 at, Quaternion rot)
        {
            ZNetView nv = Leash.View(c);
            if (nv == null || !nv.IsValid()) return;
            if (!nv.IsOwner()) nv.ClaimOwnership();
            c.transform.position = at;
            c.transform.rotation = rot;
            Rigidbody rb = Leash.Body(c);
            if (rb != null) { rb.position = at; rb.velocity = Vector3.zero; }
            nv.GetZDO().SetPosition(at);
            nv.GetZDO().SetRotation(rot);
            Home.Set(c, at);
            Leash.Grace(c);
        }

        internal static void Update()
        {
            Player p = Player.m_localPlayer;
            if (p == null) { _remount = ZDOID.None; return; }
            if (p.IsTeleporting())
            {
                _quietUntil = Mathf.Max(_quietUntil, Time.time + QuietAfter);
                if (!_remount.IsNone()) _remountUntil = Time.time + RemountWait;
                return;
            }
            if (!_remount.IsNone()) Remount(p);
            else Touching(p);
        }

        private static void Remount(Player p)
        {
            if (Time.time > _remountUntil)
            {
                Plugin.Debug("[portal] your animal didn't turn up on this side in time: get on it yourself");
                _remount = ZDOID.None;
                return;
            }
            GameObject go = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(_remount) : null;
            Character c = go != null ? go.GetComponent<Character>() : null;
            if (c == null || c.IsDead()) return;
            if (Vector3.Distance(c.transform.position, p.transform.position) > 30f) Send(c, p.transform.position + p.transform.forward * (c.GetRadius() + 0.5f), p.transform.rotation);
            if (Mount.RideAgain(p, c))
            {
                Plugin.Debug("[portal] riding " + c.name + " again");
                _remount = ZDOID.None;
            }
        }

        // The portal notices a player walking into it, not one sitting on an animal (the animal's body gets there
        // first, or the rider sits above the opening): while you ride, touching a portal with the animal sends you.
        private static void Touching(Player p)
        {
            Character c = Mount.RiddenCharacter();
            if (c == null || Time.time < _nextTouch || TeleportM == null) return;
            _nextTouch = Time.time + Touch;
            Collider[] hits = Physics.OverlapSphere(c.GetCenterPoint(), c.GetRadius() + 0.4f, ~0, QueryTriggerInteraction.Collide);
            foreach (Collider col in hits)
            {
                TeleportWorld w = col != null && col.isTrigger && col.GetComponent<TeleportWorldTrigger>() != null ? col.GetComponentInParent<TeleportWorld>() : null;
                if (w == null) continue;
                if (w == _lastPortal && Time.time < _lastTry + 3f) return;   // once per visit (a refusal is said once)
                _lastPortal = w;
                _lastTry = Time.time;
                Plugin.Debug("[portal] " + c.name + " touched a portal with you on its back");
                TeleportM.Invoke(w, new object[] { p });
                return;
            }
        }
    }
}
