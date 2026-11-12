using HarmonyLib;
using UnityEngine;

namespace Beastkeeper
{
    // Console command (cheats on, devcommands): bk_drake <frost|fire|poison|earth|lightning> [glow]
    // spawns a tamed, grown drake of that element in front of you, ready for a saddle.
    [HarmonyPatch]
    internal static class DrakeCommand
    {
        [HarmonyPatch(typeof(Terminal), "InitTerminal"), HarmonyPostfix]
        private static void Add()
        {
            new Terminal.ConsoleCommand("bk_drake", "[frost|fire|poison|earth|lightning] [glow] - a tamed grown drake (Beastkeeper)", new Terminal.ConsoleEvent(Run), true);
        }

        private static void Run(Terminal.ConsoleEventArgs args)
        {
            Player p = Player.m_localPlayer;
            if (p == null || ZNetScene.instance == null) return;
            string want = args.Length > 1 ? args[1].ToLowerInvariant() : "frost";
            int el = want == "fire" ? Drake.Fire : want == "poison" ? Drake.Poison : want == "earth" || want == "stone" ? Drake.Earth
                : want == "lightning" ? Drake.Lightning : Drake.Frost;
            GameObject prefab = ZNetScene.instance.GetPrefab("Hatchling");
            if (prefab == null) { args.Context.AddString("No drake prefab."); return; }
            Vector3 at = p.transform.position + p.transform.forward * 4f + Vector3.up;
            GameObject go = Object.Instantiate(prefab, at, Quaternion.LookRotation(-p.transform.forward));
            Character c = go.GetComponent<Character>();
            ZNetView nv = Leash.View(c);
            if (c == null || nv == null || !nv.IsValid()) return;
            ZDO z = nv.GetZDO();
            z.Set(Drake.ElementKey, el);
            if (args.Length > 2 && args[2].ToLowerInvariant() == "glow") z.Set(Drake.GlowKey, 1);
            z.Set(Halter.Key, 1);
            c.SetTamed(true);
            Home.Set(c, at);
            Drake.KeepSize(c);
            args.Context.AddString("A grown " + Drake.ElementName(el) + " drake.");
        }
    }
}
