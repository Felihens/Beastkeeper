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
            new Terminal.ConsoleCommand("bk_element", "[frost|fire|poison|earth|lightning] [glow|noglow] - changes the drake you are riding (Beastkeeper)", new Terminal.ConsoleEvent(Change), true);
            new Terminal.ConsoleCommand("bk_tame", "[range=15] - tames every creature near you, any kind (Beastkeeper, for testing)", new Terminal.ConsoleEvent(Tame), true);
            new Terminal.ConsoleCommand("bk_drake", "[frost|fire|poison|earth|lightning] [glow] [stars 0-2] - a tamed grown drake (Beastkeeper)", new Terminal.ConsoleEvent(Run), true);
        }

        private static int Element(string want)
        {
            return want == "fire" ? Drake.Fire : want == "poison" ? Drake.Poison : want == "earth" || want == "stone" ? Drake.Earth
                : want == "lightning" ? Drake.Lightning : Drake.Frost;
        }

        private static void Change(Terminal.ConsoleEventArgs args)
        {
            Character c = Mount.RiddenCharacter();
            ZNetView nv = c != null && Drake.Is(c) ? Leash.View(c) : null;
            if (nv == null || !nv.IsValid()) { args.Context.AddString("Ride the drake first."); return; }
            if (!nv.IsOwner()) nv.ClaimOwnership();
            int el = Element(args.Length > 1 ? args[1].ToLowerInvariant() : "frost");
            nv.GetZDO().Set(Drake.ElementKey, el);
            if (args.Length > 2) nv.GetZDO().Set(Drake.GlowKey, args[2].ToLowerInvariant() == "glow" ? 1 : 0);
            args.Context.AddString("It is a " + Drake.ElementName(el) + " drake now. (A glow shows after getting off and on, or a moment later.)");
        }

        // the game's own "tame" leaves out what it can't tame itself; this one tames any creature in range (not players)
        private static void Tame(Terminal.ConsoleEventArgs args)
        {
            Player p = Player.m_localPlayer;
            if (p == null) return;
            float range = 15f;
            if (args.Length > 1) float.TryParse(args[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out range);
            int n = 0;
            foreach (Character c in Character.GetAllCharacters())
            {
                if (c == null || c.IsPlayer() || c.IsDead() || c.IsTamed() || Vector3.Distance(c.transform.position, p.transform.position) > range) continue;
                ZNetView nv = Leash.View(c);
                if (nv == null || !nv.IsValid()) continue;
                if (!nv.IsOwner()) nv.ClaimOwnership();
                c.SetTamed(true);
                n++;
            }
            args.Context.AddString(n + " creature(s) tamed within " + range + " m.");
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
            int stars = 0;
            for (int i = 2; i < args.Length; i++)
            {
                string w = args[i].ToLowerInvariant();
                if (w == "glow") z.Set(Drake.GlowKey, 1);
                else { int n; if (int.TryParse(w, out n)) stars = Mathf.Clamp(n, 0, 2); }
            }
            if (stars > 0) c.SetLevel(stars + 1);
            z.Set(Halter.Key, 1);
            c.SetTamed(true);
            Home.Set(c, at);
            Drake.KeepSize(c);
            args.Context.AddString("A grown " + Drake.ElementName(el) + " drake" + (stars > 0 ? " with " + stars + " star(s)" : "") + ".");
        }
    }
}
