using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Beastkeeper
{
    // Our items: each one is a copy of a vanilla item with its own name, icon and recipe, registered in
    // ObjectDB and ZNetScene. Players without the mod simply don't know them: if one is dropped on the
    // ground they don't see it, nothing else happens.
    internal class CustomItem
    {
        internal string PrefabName;
        internal string[] Sources;
        internal Action<GameObject, GameObject> Setup;      // (our copy, the vanilla source)
        internal Recipes.R Recipe;
        internal GameObject Prefab;

        internal bool Is(ItemDrop.ItemData item)
        {
            if (item == null) return false;
            if (item.m_dropPrefab != null) return item.m_dropPrefab.name == PrefabName;
            return Prefab != null && item.m_shared == Prefab.GetComponent<ItemDrop>().m_itemData.m_shared;
        }
    }

    [HarmonyPatch]
    internal static class Items
    {
        private static readonly MethodInfo UpdateRegisters = AccessTools.Method(typeof(ObjectDB), "UpdateRegisters");
        private static readonly FieldInfo NamedPrefabs = AccessTools.Field(typeof(ZNetScene), "m_namedPrefabs");
        private static readonly List<CustomItem> All = new List<CustomItem>();
        private static GameObject _holder;

        internal static CustomItem Add(CustomItem item) { All.Add(item); return item; }

        private static GameObject Build(CustomItem item, Func<string, GameObject> find)
        {
            if (item.Prefab != null) return item.Prefab;
            GameObject src = null;
            foreach (string s in item.Sources) { src = find(s); if (src != null) break; }
            if (src == null) return null;
            if (_holder == null)
            {
                _holder = new GameObject("Beastkeeper_Prefabs");
                _holder.SetActive(false);
                Object.DontDestroyOnLoad(_holder);
            }
            GameObject p = Object.Instantiate(src, _holder.transform, false);
            p.name = item.PrefabName;
            p.GetComponent<ItemDrop>().m_itemData.m_dropPrefab = p;
            item.Prefab = p;
            item.Setup(p, src);
            Plugin.Debug("[items] built " + item.PrefabName + " from " + src.name);
            return p;
        }

        [HarmonyPatch(typeof(ObjectDB), "Awake"), HarmonyPostfix]
        private static void ObjectDBAwake(ObjectDB __instance) { Register(__instance); }

        [HarmonyPatch(typeof(ObjectDB), "CopyOtherDB"), HarmonyPostfix]
        private static void ObjectDBCopy(ObjectDB __instance) { Register(__instance); }

        [HarmonyPatch(typeof(ZNetScene), "Awake"), HarmonyPostfix]
        private static void ZNetSceneAwake(ZNetScene __instance)
        {
            var named = NamedPrefabs != null ? NamedPrefabs.GetValue(__instance) as Dictionary<int, GameObject> : null;
            foreach (CustomItem item in All)
            {
                GameObject p = Build(item, n => __instance.m_prefabs.Find(g => g != null && g.name == n));
                if (p == null) { Plugin.Log.LogWarning("Could not build " + item.PrefabName + ": no source item found."); continue; }
                if (!__instance.m_prefabs.Contains(p)) __instance.m_prefabs.Add(p);
                if (named != null) named[item.PrefabName.GetStableHashCode()] = p;
            }
            if (ObjectDB.instance != null) Register(ObjectDB.instance);
        }

        private static void Register(ObjectDB db)
        {
            if (db == null || db.m_items == null || db.m_items.Count == 0) return;
            bool changed = false;
            foreach (CustomItem item in All)
            {
                GameObject p = Build(item, n => db.m_items.Find(g => g != null && g.name == n));
                if (p == null) continue;
                if (!db.m_items.Contains(p)) { db.m_items.Add(p); changed = true; }
            }
            if (changed && UpdateRegisters != null) UpdateRegisters.Invoke(db, null);
            foreach (CustomItem item in All)
                if (item.Prefab != null) AddRecipe(db, item);
        }

        private static void AddRecipe(ObjectDB db, CustomItem item)
        {
            ItemDrop drop = item.Prefab.GetComponent<ItemDrop>();
            Recipe recipe = db.m_recipes.Find(r => r != null && r.m_item == drop);
            CraftingStation station = null;
            string st = (item.Recipe.Station ?? "").Trim();
            if (st.Length > 0)
            {
                GameObject sp = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(st) : null;
                if (sp == null) return;   // the station isn't known yet: try again when ZNetScene wakes up
                station = sp.GetComponent<CraftingStation>();
            }
            List<Piece.Requirement> reqs = new List<Piece.Requirement>();
            foreach (string part in (item.Recipe.Cost ?? "").Split(','))
            {
                string[] kv = part.Split(':');
                if (kv.Length != 2) continue;
                GameObject ip = db.GetItemPrefab(kv[0].Trim());
                int amount;
                if (ip == null || !int.TryParse(kv[1].Trim(), out amount)) { Plugin.Log.LogWarning(item.PrefabName + " recipe: unknown item '" + part + "'."); continue; }
                reqs.Add(new Piece.Requirement { m_resItem = ip.GetComponent<ItemDrop>(), m_amount = amount, m_recover = true });
            }
            if (recipe == null)
            {
                recipe = ScriptableObject.CreateInstance<Recipe>();
                recipe.name = "Recipe_" + item.PrefabName;
                db.m_recipes.Add(recipe);
            }
            recipe.m_item = drop;
            recipe.m_amount = 1;
            recipe.m_enabled = true;
            recipe.m_craftingStation = station;
            recipe.m_minStationLevel = Mathf.Max(1, item.Recipe.Level);
            recipe.m_resources = reqs.ToArray();
            Plugin.Debug("[items] recipe " + item.PrefabName + ": " + reqs.Count + " ingredients at " + (station != null ? station.name : "hand"));
        }

        // ------------------------------------------------------------ icon helpers

        internal static Sprite MakeIcon(string name, Func<Vector2, Color> pixel)
        {
            const int n = 64;
            Texture2D t = new Texture2D(n, n, TextureFormat.RGBA32, false);
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                    t.SetPixel(x, y, pixel(new Vector2(x + 0.5f, y + 0.5f)));
            t.Apply();
            Sprite s = Sprite.Create(t, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f));
            s.name = name;
            return s;
        }

        // the same icon in a tier's colour
        internal static Sprite Tinted(Sprite s, Color tint, string name)
        {
            if (s == null || tint == Color.white) return s;
            Texture2D src = s.texture;
            Texture2D t = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
            Color[] px = src.GetPixels();
            for (int i = 0; i < px.Length; i++) { Color c = px[i]; px[i] = new Color(c.r * tint.r, c.g * tint.g, c.b * tint.b, c.a); }
            t.SetPixels(px);
            t.Apply();
            Sprite o = Sprite.Create(t, new Rect(0, 0, src.width, src.height), new Vector2(0.5f, 0.5f));
            o.name = name;
            return o;
        }

        internal static readonly Color LeatherLight = new Color(0.78f, 0.6f, 0.38f), LeatherDark = new Color(0.36f, 0.24f, 0.13f);
    }
}
