using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Beastkeeper
{
    // A drake flying over the sea (2026-11-22, agreed with the user, from a GitHub request). The sea is the
    // serpent's: by default (DrakeOverSea = Offering) a drake only dares to fly out over deep water after you
    // feed it serpent by hand at a Beast Totem. That gives it "sea courage": seconds that run down only while it
    // flies over deep water (shown as a status icon while you ride it, with warnings at 1 min, 30 s and 10 s).
    // When the courage runs out over the sea, the drake throws its rider off into the water.
    // Off = never over the sea (as before); Free = over the sea without limit.
    [HarmonyPatch]
    internal static class SeaCourage
    {
        internal const string Off = "Off", Offering = "Offering", Free = "Free";
        private static readonly int Key = "Beastkeeper_seaCourage".GetStableHashCode();
        private const string Rpc = "Beastkeeper_SeaFood";
        // seconds over deep water (2026-11-22): 5 min for the cooked meat, 15 min for the stew
        private const float MeatSeconds = 300f, StewSeconds = 900f;
        private static readonly float[] Warnings = { 60f, 30f, 10f };

        private static float _left;
        private static Character _ridden;

        // its courage ran out over the sea: it dives to the waves (Drake.Drive) and throws its rider in near the water.
        // Reaching land first ends it.
        internal static bool Diving;

        internal static string Mode
        {
            get
            {
                string m = Plugin.DrakeOverSea != null ? Plugin.DrakeOverSea.Value : Offering;
                return m == Off || m == Free ? m : Offering;
            }
        }

        // how many seconds over the sea this food gives a drake (0 = none)
        internal static float FoodSeconds(ItemDrop.ItemData item)
        {
            if (item == null || Mode != Offering) return 0f;
            string n = item.m_dropPrefab != null ? item.m_dropPrefab.name : "";
            if (n == "SerpentStew") return StewSeconds;
            if (n == "SerpentMeatCooked") return MeatSeconds;
            return 0f;
        }

        // raw serpent meat too (for Hugin's tip: it only has to be cooked first)
        internal static bool IsSerpent(string prefab) { return prefab == "SerpentMeat" || prefab == "SerpentMeatCooked" || prefab == "SerpentStew"; }

        internal static string Clock(float s)
        {
            int t = Mathf.CeilToInt(Mathf.Max(0f, s));
            return (t / 60) + ":" + (t % 60).ToString("00");
        }

        internal static void RegisterOn(Character c, ZNetView nv)
        {
            nv.Register<float>(Rpc, (sender, seconds) => Fed(c, nv, seconds));
        }

        internal static void Feed(ZNetView nv, float seconds) { nv.InvokeRPC(Rpc, seconds); }

        // on the drake's owner
        private static void Fed(Character c, ZNetView nv, float seconds)
        {
            if (c == null || nv == null || !nv.IsValid() || !nv.IsOwner()) return;
            float now = Mathf.Max(nv.GetZDO().GetFloat(Key, 0f), seconds);
            nv.GetZDO().Set(Key, now);
            if (_ridden == c) _left = now;
            Plugin.Debug("[sea] " + c.name + " fed serpent: sea courage " + now.ToString("0") + " s");
        }

        // a drake may fly out over deep water now
        internal static bool Brave(Character c)
        {
            string m = Mode;
            if (m == Free) return true;
            if (m == Off) return false;
            return _ridden == c ? _left > 0f : Left(c) > 0f;
        }

        internal static float Left(Character c)
        {
            ZNetView nv = Leash.View(c);
            return nv != null && nv.IsValid() ? nv.GetZDO().GetFloat(Key, 0f) : 0f;
        }

        internal static void OnRide(Character c)
        {
            _ridden = c;
            _left = Left(c);
            Diving = false;
        }

        internal static void OnGetOff(Character c)
        {
            Save(c);
            if (_ridden == c) _ridden = null;
            Diving = false;
            Icon(false);
        }

        internal static void Save(Character c)
        {
            ZNetView nv = Leash.View(c);
            if (_ridden != c || nv == null || !nv.IsValid() || !nv.IsOwner()) return;
            nv.GetZDO().Set(Key, _left);
        }

        // every frame while you ride a drake in the air
        internal static void Tick(Character c, bool sea, float dt)
        {
            bool counting = Mode == Offering && _ridden == c;
            Icon(counting && _left > 0f);
            if (Diving && !sea)
            {
                Diving = false;
                Message("Your drake reaches land and calms down.");
            }
            if (!counting || !sea || _left <= 0f) return;
            float before = _left;
            _left = Mathf.Max(0f, _left - dt);
            foreach (float w in Warnings)
                if (before > w && _left <= w)
                    Message("Your drake's courage over the sea runs low: " + Clock(_left) + " left. Head for land!");
            if (_left > 0f) return;
            Save(c);
            Icon(false);
            Diving = true;
            Message("Your drake's courage is gone: it dives towards the waves!");
            Plugin.Debug("[sea] courage ran out over the sea: diving");
        }

        // close to the water while diving: the rider falls in, and the drake stays above them (SeaRescue)
        internal static void ThrowOff(Character c)
        {
            Diving = false;
            SeaRescue.Mark(c, Player.m_localPlayer);
            Message("Your drake throws you off into the sea!");
            Plugin.Debug("[sea] rider thrown into the sea");
            Player p = Player.m_localPlayer;
            if (p != null) p.StopDoodadControl();
        }

        private static void Message(string s)
        {
            Player p = Player.m_localPlayer;
            if (p != null) p.Message(MessageHud.MessageType.Center, s);
        }

        // ------------------------------------------------------------ the status icon while you ride

        private static StatusEffect _se;
        private static readonly int SeHash = "Beastkeeper_SeaCourage".GetStableHashCode();
        private static readonly FieldInfo SeTime = AccessTools.Field(typeof(StatusEffect), "m_time");

        private static StatusEffect Make(ObjectDB db)
        {
            if (_se != null && _se.m_icon != null) return _se;
            if (_se == null)
            {
                _se = ScriptableObject.CreateInstance<StatusEffect>();
                _se.name = "Beastkeeper_SeaCourage";
                _se.m_name = "Sea courage";
                _se.m_tooltip = "Your drake dares to fly over the sea. The time runs only over deep water.";
                _se.m_ttl = 1f;
            }
            GameObject meat = db != null ? db.GetItemPrefab("SerpentMeatCooked") : null;
            ItemDrop d = meat != null ? meat.GetComponent<ItemDrop>() : null;
            if (d != null && d.m_itemData.m_shared.m_icons != null && d.m_itemData.m_shared.m_icons.Length > 0) _se.m_icon = d.m_itemData.m_shared.m_icons[0];
            return _se;
        }

        [HarmonyPatch(typeof(ObjectDB), "Awake"), HarmonyPostfix]
        private static void DbAwake(ObjectDB __instance) { Register(__instance); }

        [HarmonyPatch(typeof(ObjectDB), "CopyOtherDB"), HarmonyPostfix]
        private static void DbCopy(ObjectDB __instance) { Register(__instance); }

        private static void Register(ObjectDB db)
        {
            if (db == null || db.m_items == null || db.m_items.Count == 0) return;
            StatusEffect se = Make(db);
            if (!db.m_StatusEffects.Contains(se)) db.m_StatusEffects.Add(se);
        }

        private static void Icon(bool show)
        {
            Player p = Player.m_localPlayer;
            if (p == null) return;
            SEMan man = p.GetSEMan();
            StatusEffect on = man.GetStatusEffect(SeHash);
            if (!show)
            {
                if (on != null) man.RemoveStatusEffect(SeHash, true);
                return;
            }
            if (on == null)
            {
                on = man.AddStatusEffect(Make(ObjectDB.instance), false, 0, 0f, 0);
                if (on == null) return;
            }
            // the time shown is the courage left (it only runs over the sea, so it is set every frame)
            float t = SeTime != null ? (float)SeTime.GetValue(on) : 0f;
            on.m_ttl = t + _left;
        }
    }
}
