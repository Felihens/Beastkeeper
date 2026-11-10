using System.Collections;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using UnityEngine;

namespace Beastkeeper
{
    // Other mods that tame animals their own way. In this mod only a halter tames, so their taming parts are
    // switched off while Beastkeeper is on (their other features stay as they are).
    internal static class Compat
    {
        private const string Impactful = "MidnightsFX.ImpactfulSkills";

        internal static void Init()
        {
            Plugin.Instance.StartCoroutine(Later());
        }

        // every mod has loaded by the first frame
        private static IEnumerator Later()
        {
            yield return null;
            if (!Plugin.Enabled.Value) yield break;
            BepInEx.PluginInfo info;
            if (!Chainloader.PluginInfos.TryGetValue(Impactful, out info) || info.Instance == null) yield break;
            ConfigFile cfg = info.Instance.Config;
            Set(cfg, "AnimalTamingSpeedFactor", 1f);          // no faster taming from its skill
            Set(cfg, "EnableTamingHandFeeding", false);       // no taming by hand feeding
            Set(cfg, "EnableTamingProgressDisplay", false);   // no taming bar: there is no taming progress here
        }

        private static void Set(ConfigFile cfg, string key, object value)
        {
            foreach (ConfigDefinition def in cfg.Keys)
            {
                if (def.Key != key) continue;
                ConfigEntryBase e = cfg[def];
                if (Equals(e.BoxedValue, value)) return;
                e.BoxedValue = value;
                Plugin.Debug("[compat] Impactful Skills: " + key + " set to " + value + " (only the halter tames)");
                return;
            }
        }
    }
}
