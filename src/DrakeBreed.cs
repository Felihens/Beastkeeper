using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Beastkeeper
{
    // Drakes lay eggs (user, 2026-11-17). The world's dragon eggs are few, so tamed drakes can make more:
    // - a nest is the game's straw rug (rug_straw). Two grown tamed drakes near one lay an egg on it, standing up,
    //   waiting for someone to take it;
    // - each pair lays one egg every EggEveryDays game days (3 by default, about an hour and a half), counting the time
    //   nobody was there; one egg waits per pair and per nest: a drake whose egg still sits on a nest lays no other,
    //   and its wait starts again only once that egg is taken. So a server left running gives one egg, not ten;
    // - nothing is laid while MaxDrakesNear (6) grown drakes or more are within 100 m of the nest (stored ones don't count);
    // - the egg remembers its parents: more stars and glow when they have them (user, 2026-11-17: no egg woken
    //   already by same-element parents; it is woken with an item like any other);
    // - such an egg is "not worthy": Moder's altar won't take it, so she can't be called again and again.
    [HarmonyPatch]
    internal static class DrakeBreed
    {
        internal const string NestPrefab = "rug_straw";
        internal const string LaidKey = "Beastkeeper_laid";        // item custom data: laid by drakes (not from a world nest)
        internal const string ParentsKey = "Beastkeeper_parents";  // item custom data: "stars1,stars2,glow1,glow2,el1,el2"
        private static readonly int ReadyKey = "Beastkeeper_eggReady".GetStableHashCode();   // drake: server time it may lay again
        private static readonly int WaitingKey = "Beastkeeper_eggAt".GetStableHashCode();    // drake: where its egg waits (zero = none)
        private static readonly int NestedKey = "Beastkeeper_nested".GetStableHashCode();    // egg: sits on a nest, standing up
        private const float NestReach = 12f, CountRange = 100f, EggOnNest = 1.2f;

        private static float _next;
        private static readonly List<Piece> _pieces = new List<Piece>();

        private static float Period
        {
            get
            {
                float day = EnvMan.instance != null && EnvMan.instance.m_dayLengthSec > 0 ? EnvMan.instance.m_dayLengthSec : 1800f;
                return Mathf.Max(60f, Plugin.DrakeEggEveryDays.Value * day);
            }
        }

        // ------------------------------------------------------------ laying

        internal static void Update()
        {
            if (Time.time < _next) return;
            _next = Time.time + 10f;
            Player me = Player.m_localPlayer;
            if (me == null || ZNet.instance == null || ObjectDB.instance == null || Plugin.DrakeEggEveryDays.Value <= 0f) return;
            if (!Plugin.Enabled.Value || Plugin.DrakeMounts == null || (Plugin.DrakeMounts.Value ?? "").Trim().Length == 0) return;
            _pieces.Clear();
            Piece.GetAllPiecesInRadius(me.transform.position, CountRange, _pieces);
            foreach (Piece rug in _pieces)
            {
                if (rug == null || Utils.GetPrefabName(rug.gameObject) != NestPrefab) continue;
                ZNetView rv = rug.GetComponent<ZNetView>();
                if (rv == null || !rv.IsValid() || !rv.IsOwner()) continue;   // the game that runs the nest decides
                try { Nest(rug); } catch (System.Exception e) { Plugin.Log.LogWarning("[nest] " + e.Message); }
            }
        }

        private static void Nest(Piece rug)
        {
            Vector3 at = rug.transform.position;
            if (EggNear(at, EggOnNest) != null) return;   // its egg still waits
            float now = Breed.Now();
            List<Character> pair = new List<Character>();
            int grown = 0;
            foreach (Character c in Character.GetAllCharacters())
            {
                if (c == null || c.IsDead() || !c.IsTamed() || !Drake.Is(c) || Drake.Grown(c) < 1f) continue;
                float d = Vector3.Distance(c.transform.position, at);
                if (d > CountRange) continue;
                grown++;
                if (d > NestReach) continue;
                ZNetView nv = Leash.View(c);
                if (nv == null || !nv.IsValid() || !nv.IsOwner()) continue;
                ZDO z = nv.GetZDO();
                if (Waiting(z, now)) continue;
                float ready = z.GetFloat(ReadyKey, 0f);
                if (ready <= 0f) { z.Set(ReadyKey, now + Period); continue; }   // newly grown or new here: its wait starts now
                if (ready > now) continue;
                pair.Add(c);
            }
            if (grown >= Mathf.Max(2, Plugin.DrakeMaxNear.Value)) return;
            if (pair.Count < 2) return;
            Lay(rug, pair[0], pair[1], now);
        }

        // its egg still waits on a nest? Once that egg is gone, the wait for the next starts
        private static bool Waiting(ZDO z, float now)
        {
            Vector3 eggAt = z.GetVec3(WaitingKey, Vector3.zero);
            if (eggAt == Vector3.zero) return false;
            if (Player.m_localPlayer != null && Vector3.Distance(Player.m_localPlayer.transform.position, eggAt) > CountRange) return true;   // can't see it from here
            if (EggNear(eggAt, EggOnNest) != null) return true;
            z.Set(WaitingKey, Vector3.zero);
            z.Set(ReadyKey, now + Period);
            Plugin.Debug("[nest] a drake's egg was taken: its next one in " + Mathf.RoundToInt(Period / 60f) + " min");
            return true;
        }

        private static ItemDrop EggNear(Vector3 at, float range)
        {
            foreach (Collider col in Physics.OverlapSphere(at + Vector3.up * 0.3f, range, ~0, QueryTriggerInteraction.Ignore))
            {
                ItemDrop d = col != null ? col.GetComponentInParent<ItemDrop>() : null;
                if (d != null && DrakeEgg.IsEgg(d.gameObject)) return d;
            }
            return null;
        }

        private static int Stars(Character c) { return Mathf.Clamp(c.GetLevel() - 1, 0, 2); }

        private static void Lay(Piece rug, Character a, Character b, float now)
        {
            GameObject prefab = ObjectDB.instance.GetItemPrefab(DrakeEgg.EggPrefab);
            ItemDrop proto = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (proto == null) return;
            ZDO za = Leash.View(a).GetZDO(), zb = Leash.View(b).GetZDO();
            int ea = za.GetInt(Drake.ElementKey, 0), eb = zb.GetInt(Drake.ElementKey, 0);
            ItemDrop.ItemData item = proto.m_itemData.Clone();
            item.m_stack = 1;
            item.m_dropPrefab = prefab;
            item.m_customData[LaidKey] = "1";
            item.m_customData[ParentsKey] = Stars(a) + "," + Stars(b) + "," + za.GetInt(Drake.GlowKey, 0) + "," + zb.GetInt(Drake.GlowKey, 0) + "," + ea + "," + eb;
            Vector3 spot = rug.transform.position + Vector3.up * 0.25f;
            ItemDrop egg = ItemDrop.DropItem(item, 1, spot, Quaternion.identity);
            if (egg == null) return;
            ZNetView ev = egg.GetComponent<ZNetView>();
            if (ev != null && ev.IsValid()) ev.GetZDO().Set(NestedKey, 1);
            Stand(egg);
            foreach (ZDO z in new[] { za, zb }) { z.Set(WaitingKey, spot); z.Set(ReadyKey, now + Period); }
            Plugin.Debug("[nest] " + a.GetHoverName() + " and " + b.GetHoverName() + " laid an egg on the straw rug at " + spot.ToString("0") + " (" + item.m_customData[ParentsKey] + ")");
            Player p = Player.m_localPlayer;
            if (p != null && Vector3.Distance(p.transform.position, spot) < 30f)
                p.Message(MessageHud.MessageType.Center, "Your drakes laid an egg on the straw rug!");
        }

        // an egg on a nest stands up and stays put until taken (on every game that shows it)
        internal static void Stand(ItemDrop egg)
        {
            Rigidbody rb = egg.GetComponent<Rigidbody>();
            if (rb != null) { rb.isKinematic = true; rb.velocity = Vector3.zero; }
            egg.transform.rotation = Quaternion.identity;
        }

        internal static bool Nested(ZNetView nv) { return nv != null && nv.IsValid() && nv.GetZDO().GetInt(NestedKey, 0) == 1; }

        // ------------------------------------------------------------ what the straw rug says (user, 2026-11-17)

        [HarmonyPatch(typeof(ZNetScene), "Awake"), HarmonyPostfix]
        private static void AddNestHover(ZNetScene __instance)
        {
            GameObject rug = __instance.GetPrefab(NestPrefab);
            if (rug != null && rug.GetComponent<Hoverable>() == null && rug.GetComponent<NestHover>() == null) rug.AddComponent<NestHover>();
        }

        internal class NestHover : MonoBehaviour, Hoverable
        {
            public float GetHoverOffset() { return 0.2f; }

            public string GetHoverName()
            {
                Piece pc = GetComponent<Piece>();
                return pc != null && Localization.instance != null ? Localization.instance.Localize(pc.m_name) : "Straw rug";
            }

            public string GetHoverText()
            {
                if (!Plugin.Enabled.Value || GetComponent<ZNetView>() == null || !GetComponent<ZNetView>().IsValid()) return "";
                string s = NestStatus(transform.position);
                return s.Length == 0 ? "" : GetHoverName() + "\n<color=#c8b48a>" + s + "</color>";
            }
        }

        private static float _statusAt;
        private static Vector3 _statusFor;
        private static string _status = "";

        internal static string NestStatus(Vector3 at)
        {
            if (Time.time < _statusAt && (at - _statusFor).sqrMagnitude < 0.01f) return _status;
            _statusAt = Time.time + 1f;
            _statusFor = at;
            try { _status = Status(at); } catch (System.Exception e) { _status = ""; Plugin.Log.LogWarning("[nest] " + e.Message); }
            return _status;
        }

        // only when tamed drakes are around: else a rug is just a rug
        private static string Status(Vector3 at)
        {
            if (Plugin.DrakeEggEveryDays.Value <= 0f || ZNet.instance == null) return "";
            float now = Breed.Now();
            int any = 0, grown = 0, close = 0;
            List<float> ready = new List<float>();
            foreach (Character c in Character.GetAllCharacters())
            {
                if (c == null || c.IsDead() || !c.IsTamed() || !Drake.Is(c)) continue;
                float d = Vector3.Distance(c.transform.position, at);
                if (d > CountRange) continue;
                any++;
                if (Drake.Grown(c) < 1f) continue;
                grown++;
                if (d > NestReach) continue;
                close++;
                ZNetView nv = Leash.View(c);
                if (nv == null || !nv.IsValid()) continue;
                ZDO z = nv.GetZDO();
                if (z.GetVec3(WaitingKey, Vector3.zero) != Vector3.zero) continue;
                ready.Add(z.GetFloat(ReadyKey, 0f));
            }
            if (any == 0) return "";
            int max = Mathf.Max(2, Plugin.DrakeMaxNear.Value);
            if (EggNear(at, EggOnNest) != null) return "Drake nest: an egg waits here. Take it and they lay another.";
            if (grown >= max) return "Drake nest: too many drakes around (" + grown + " grown within 100 m; fewer than " + max + ", please).";
            if (close < 2) return "Drake nest: needs two grown tamed drakes close by (" + close + " now).";
            if (ready.Count < 2) return "Drake nest: its drakes wait until their last egg is taken.";
            ready.Sort();
            if (ready[1] <= 0f) return "Drake nest: the drakes are settling in.";
            float left = ready[1] - now;
            if (left <= 0f) return "Drake nest: an egg is coming soon.";
            int min = Mathf.CeilToInt(left / 60f);
            return "Drake nest: next egg in about " + (min >= 60 ? (min / 60) + " h " + (min % 60) + " min" : min + " min") + ".";
        }

        // ------------------------------------------------------------ what an egg laid by drakes carries

        internal static bool Laid(ItemDrop.ItemData item)
        {
            string v;
            return item != null && item.m_customData != null && item.m_customData.TryGetValue(LaidKey, out v) && v == "1";
        }

        // the parents' stars and glow: (stars a, stars b, glowing parents), or false for an egg from a world nest
        internal static bool Parents(ItemDrop.ItemData item, out int s1, out int s2, out int glowing)
        {
            s1 = s2 = glowing = 0;
            string v;
            if (item == null || item.m_customData == null || !item.m_customData.TryGetValue(ParentsKey, out v)) return false;
            string[] p = v.Split(',');
            if (p.Length < 4) return false;
            int.TryParse(p[0], out s1); int.TryParse(p[1], out s2);
            int g1, g2;
            int.TryParse(p[2], out g1); int.TryParse(p[3], out g2);
            glowing = (g1 == 1 ? 1 : 0) + (g2 == 1 ? 1 : 0);
            return true;
        }

        // chances by the parents' stars put together (0..4): 2 stars / 1 star; the rest none
        private static readonly float[] Two = { 0.10f, 0.15f, 0.20f, 0.30f, 0.40f };
        private static readonly float[] One = { 0.35f, 0.40f, 0.45f, 0.45f, 0.45f };

        internal static int RollLevel(int s1, int s2)
        {
            int sum = Mathf.Clamp(s1 + s2, 0, 4);
            float r = Random.value;
            return r < Two[sum] ? 3 : r < Two[sum] + One[sum] ? 2 : 1;
        }

        // the glow chance: the setting, twice with a glowing parent, three times with two
        internal static float GlowChance(int glowing)
        {
            return Mathf.Clamp01(Plugin.DrakeGlowChance.Value * (1 + Mathf.Clamp(glowing, 0, 2)));
        }

        // ------------------------------------------------------------ Moder's altar refuses it

        [HarmonyPatch(typeof(ItemStand), "UseItem"), HarmonyPrefix]
        private static bool NotWorthy(ItemStand __instance, Humanoid user, ItemDrop.ItemData item, ref bool __result)
        {
            if (!Plugin.Enabled.Value || !Laid(item) || __instance.m_supportedItems == null) return true;
            bool eggCup = false;
            foreach (ItemDrop d in __instance.m_supportedItems) if (d != null && d.gameObject.name == DrakeEgg.EggPrefab) { eggCup = true; break; }
            if (!eggCup) return true;
            if (user != null) user.Message(MessageHud.MessageType.Center, "This egg is not worthy.");
            __result = true;
            return false;
        }
    }
}
