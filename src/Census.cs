using System.Text;
using HarmonyLib;
using UnityEngine;

namespace Beastkeeper
{
    // Writes to the log, once per game start, every creature in the game and what Beastkeeper lets you do
    // with it: tame it (the game's way or with the halter) and ride it. Only with DetailedLog on.
    [HarmonyPatch]
    internal static class Census
    {
        private static bool _done;

        [HarmonyPatch(typeof(ZNetScene), "Awake"), HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void List(ZNetScene __instance)
        {
            if (_done || !Plugin.DetailedLog.Value) return;
            _done = true;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[census] prefab | name | faction | boss | ai | tame | height | ride");
            foreach (GameObject g in __instance.m_prefabs)
            {
                if (g == null) continue;
                Character c = g.GetComponent<Character>();
                if (c == null || c is Player) continue;
                string prefab = g.name;
                bool gameTame = g.GetComponent<Tameable>() != null;
                string ai = g.GetComponent<AnimalAI>() != null ? "AnimalAI" : g.GetComponent<MonsterAI>() != null ? "MonsterAI" : "none";
                bool eligible = !c.m_boss && !Has(Plugin.NotAnimals.Value, prefab)
                    && (gameTame || ai == "AnimalAI" || c.m_faction == Character.Faction.AnimalsVeg || Has(Plugin.ExtraAnimals.Value, prefab));
                string tame = !eligible ? "no" : gameTame ? "game" : "halter";
                float h = Height(g);
                string ride = !eligible ? "no" : Has(Plugin.NotRideable.Value, prefab) ? "blocked" : h >= Plugin.RideMinHeight.Value ? "yes" : "too small";
                string name = Localization.instance != null ? Localization.instance.Localize(c.m_name) : c.m_name;
                sb.AppendLine("[census] " + prefab + " | " + name + " | " + c.m_faction + " | " + (c.m_boss ? "boss" : "-") + " | " + ai
                    + " | " + tame + " | " + h.ToString("0.00") + " | " + ride);
            }
            Plugin.Log.LogInfo(sb.ToString());
        }

        // top of the body above the feet, from the prefab's collider (prefabs aren't in the world, so no bounds)
        private static float Height(GameObject g)
        {
            Collider col = g.GetComponent<Collider>();
            if (col == null) col = g.GetComponentInChildren<Collider>(true);
            if (col == null) return 0f;
            float sy = col.transform.lossyScale.y;
            float top = 0f;
            CapsuleCollider cap = col as CapsuleCollider;
            BoxCollider box = col as BoxCollider;
            SphereCollider sph = col as SphereCollider;
            if (cap != null) top = cap.center.y + cap.height / 2f;
            else if (box != null) top = box.center.y + box.size.y / 2f;
            else if (sph != null) top = sph.center.y + sph.radius;
            Vector3 local = new Vector3(0f, top, 0f);
            return (col.transform.position + col.transform.rotation * Vector3.Scale(local, col.transform.lossyScale)).y - g.transform.position.y + 0f * sy;
        }

        private static bool Has(string list, string prefab)
        {
            if (string.IsNullOrEmpty(list)) return false;
            foreach (string s in list.Split(','))
                if (string.Equals(s.Trim(), prefab, System.StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
