using HarmonyLib;
using UnityEngine;

namespace Beastkeeper
{
    // Tying animals up: with tamed animals on your lasso, press E on anything you built (a post, a fence, a wall)
    // and the rope's end is tied there. They graze around it, within Length metres, and don't wander off while you
    // work. Any number of animals can share one spot.
    // If one is hit, the knot slips: it runs like it always would, and once the danger is over (LooseSeconds) it
    // walks back to where it was tied and stays again.
    // E with a lasso on a tied animal takes it back onto your rope. Riding a tied animal unties it.
    // If the piece it's tied to is torn down, the animal is free (it stays around there).
    [HarmonyPatch]
    internal static class Tie
    {
        internal const float Length = 4f;
        private const float LooseSeconds = 20f;
        private static readonly int AtKey = "Beastkeeper_tie".GetStableHashCode();
        private static readonly int LooseKey = "Beastkeeper_tieLoose".GetStableHashCode();
        private static readonly System.Collections.Generic.KeyValuePair<int, int> PieceKey = ZDO.GetHashZDOID("Beastkeeper_tiePiece");

        private static ZDO Zdo(Character c)
        {
            ZNetView nv = Leash.View(c);
            return nv != null && nv.IsValid() ? nv.GetZDO() : null;
        }

        private static float Now() { return ZNet.instance != null ? (float)ZNet.instance.GetTimeSeconds() : Time.time; }

        internal static bool IsTied(Character c)
        {
            ZDO z = Zdo(c);
            return z != null && z.GetVec3(AtKey, Vector3.zero) != Vector3.zero;
        }

        internal static Vector3 Anchor(Character c)
        {
            ZDO z = Zdo(c);
            return z != null ? z.GetVec3(AtKey, Vector3.zero) : Vector3.zero;
        }

        // the knot slipped a moment ago (it was hit): it may run for now
        internal static bool Loose(Character c)
        {
            ZDO z = Zdo(c);
            return z != null && Now() < z.GetFloat(LooseKey, 0f);
        }

        internal static void Untie(Character c)
        {
            ZNetView nv = Leash.View(c);
            if (nv == null || !nv.IsValid() || !IsTied(c)) return;
            if (!nv.IsOwner()) nv.ClaimOwnership();
            nv.GetZDO().Set(AtKey, Vector3.zero);
            nv.GetZDO().Set(LooseKey, 0f);
            nv.GetZDO().Set(PieceKey, ZDOID.None);
            Plugin.Debug("[tie] untied " + c.name);
        }

        // the lasso's E on a tied animal (untie) wins over its other E actions
        internal static bool Busy(Player p, Character c) { return IsTied(c) && Lasso.Set.Equipped(p) > 0; }

        [HarmonyPatch(typeof(Player), "Interact"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static bool Interact(Player __instance, GameObject go, bool hold, bool alt)
        {
            if (!Plugin.Enabled.Value || __instance != Player.m_localPlayer || hold || alt || go == null) return true;
            if (Tug.Active || Lasso.Set.Equipped(__instance) == 0) return true;
            Player p = __instance;

            // a tied animal: back onto your rope
            Character c = go.GetComponentInParent<Character>();
            if (c != null && !c.IsPlayer())
            {
                if (!IsTied(c)) return true;
                if (Leash.CountMine(p.GetPlayerID()) >= Plugin.MaxLeashed.Value)
                {
                    p.Message(MessageHud.MessageType.Center, "You can't hold more than " + Plugin.MaxLeashed.Value + " animals at once.");
                    return false;
                }
                Untie(c);
                Leash.CatchTamed(c, p);
                p.Message(MessageHud.MessageType.Center, "You untie " + c.GetHoverName() + ". It's on your lasso.");
                return false;
            }

            // something you built: tie the animals on your rope to it
            Piece piece = go.GetComponentInParent<Piece>();
            Component post = piece;
            if (post == null) post = go.GetComponentInParent<TreeBase>();   // a standing tree
            if (post == null) return true;
            System.Collections.Generic.List<Character> mine = Leash.Mine();
            if (mine.Count == 0) return true;   // nothing on the rope: the piece's own E (a door, a chest...)
            Collider col = go.GetComponent<Collider>();
            if (col == null) col = go.GetComponentInParent<Collider>();
            Vector3 hand = Rope.Hand(p);
            bool solid = col != null && !(col is MeshCollider && !((MeshCollider)col).convex);
            Vector3 at = solid ? col.ClosestPoint(hand) : col != null ? col.bounds.ClosestPoint(hand) : post.transform.position + Vector3.up;
            if (piece == null)   // a tree: round the middle of the trunk, at the height of your hand
                at = new Vector3(post.transform.position.x, Mathf.Max(hand.y, post.transform.position.y + 0.5f), post.transform.position.z);
            if (at == Vector3.zero) at += Vector3.up * 0.01f;
            ZNetView pnv = post.GetComponent<ZNetView>();
            string what = piece != null ? Localization.instance.Localize(piece.m_name) : "tree";
            int tied = 0, wild = 0;
            string names = "";
            foreach (Character a in mine)
            {
                if (a == null || Tug.Is(a)) continue;
                if (!a.IsTamed()) { wild++; continue; }
                ZNetView nv = Leash.View(a);
                Leash.Release(a, true);
                if (!nv.IsOwner()) nv.ClaimOwnership();
                nv.GetZDO().Set(AtKey, at);
                nv.GetZDO().Set(LooseKey, 0f);
                nv.GetZDO().Set(PieceKey, pnv != null && pnv.IsValid() ? pnv.GetZDO().m_uid : ZDOID.None);
                tied++;
                names += (names.Length > 0 ? ", " : "") + a.GetHoverName();
            }
            if (tied > 0) p.Message(MessageHud.MessageType.Center, "You tie " + names + " to the " + what + ".");
            else if (wild > 0) p.Message(MessageHud.MessageType.Center, "Tame it first: a wild animal would chew through the rope.");
            Plugin.Debug("[tie] tied " + tied + " at " + at.ToString("0.0") + " (" + post.name + ")");
            return false;
        }

        // hit while tied: the knot slips (runs on the game that owns the animal)
        [HarmonyPatch(typeof(Character), "RPC_Damage"), HarmonyPostfix]
        private static void Hit(Character __instance, HitData hit)
        {
            if (!Plugin.Enabled.Value || __instance == null || __instance.IsPlayer() || !IsTied(__instance)) return;
            ZNetView nv = Leash.View(__instance);
            if (nv == null || !nv.IsOwner()) return;
            nv.GetZDO().Set(LooseKey, Now() + LooseSeconds);
            Plugin.Debug("[tie] " + __instance.name + " was hit: the knot slips");
        }

        // the piece it was tied to is gone: free (checked by the owner, now and then)
        internal static void CheckPiece(Character c)
        {
            ZDO z = Zdo(c);
            if (z == null || ZDOMan.instance == null) return;
            ZDOID id = z.GetZDOID(PieceKey);
            if (id.IsNone() || ZDOMan.instance.GetZDO(id) != null) return;
            Untie(c);
            Home.Set(c, c.transform.position);
            Plugin.Debug("[tie] " + c.name + "'s post is gone: it's free");
        }

        internal static string HoverLine(Character c)
        {
            if (!Plugin.Enabled.Value || c == null || !IsTied(c)) return "";
            Player p = Player.m_localPlayer;
            string s = "\n<color=#d8b080>Tied up</color>";
            if (p != null && Lasso.Set.Equipped(p) > 0) s += "\n[<color=yellow><b>E</b></color>] Untie";
            return s;
        }
    }
}
