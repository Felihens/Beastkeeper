using System.Collections.Generic;
using UnityEngine;

namespace Beastkeeper
{
    // Progression. Every catchable animal has a tier (1-5) by species, and every tool (lasso, trap, halter,
    // saddle) comes in five tiers. A tool works on animals of its own tier and below:
    //   1 Leather   - boar, deer, hen
    //   2 Troll     - bear
    //   3 Wolf      - wolf
    //   4 Lox       - lox
    //   5 Carapace  - hare, asksvin
    // Tamed animals are the exception: any lasso holds any tamed animal.
    // The animal's tier also sets how hard its tug of war is.
    internal static class Tiers
    {
        internal const int Max = 5;
        internal const int Saddles = 6;   // saddles have one more: the Drake Saddle (after Moder), the only one a drake takes
        internal static readonly string[] Line = { "", "Leather", "Troll", "Wolf", "Lox", "Carapace", "Drake" };

        private static readonly Dictionary<string, int> Species = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase)
        {
            { "Boar", 1 }, { "Boar_piggy", 1 }, { "Deer", 1 }, { "Hen", 1 }, { "Chicken", 1 },
            { "Bjorn", 2 },
            { "Wolf", 3 }, { "Wolf_cub", 3 }, { "Hatchling", 3 },
            { "Lox", 4 }, { "Lox_Calf", 4 },
            { "Hare", 5 }, { "Asksvin", 5 }, { "Asksvin_hatchling", 5 },
        };

        // the animal's tier: from the list above, else (creatures from other mods) from its biome and size
        internal static int Animal(Character c)
        {
            if (c == null) return 1;
            if (Sea.IsSeaMount(c)) return 1;   // the serpent has its own way in (the harpoon)
            int t;
            if (Species.TryGetValue(Mount.Kind(c.gameObject), out t)) return t;
            int biome;
            switch (Heightmap.FindBiome(c.transform.position))
            {
                case Heightmap.Biome.BlackForest: case Heightmap.Biome.Swamp: biome = 2; break;
                case Heightmap.Biome.Mountain: biome = 3; break;
                case Heightmap.Biome.Plains: biome = 4; break;
                case Heightmap.Biome.Mistlands: case Heightmap.Biome.AshLands: case Heightmap.Biome.DeepNorth: biome = 5; break;
                default: biome = 1; break;
            }
            float hp = c.GetMaxHealth();
            int size = hp >= 600f ? 4 : hp >= 200f ? 3 : hp >= 60f ? 2 : 1;
            return Mathf.Clamp(Mathf.Max(biome, size), 1, Max);
        }

        // how hard the tug of war is (the serpent keeps the moderate fight it always had)
        internal static int Tug(Character c)
        {
            if (c != null && Sea.IsSeaMount(c)) return 2;
            return Animal(c);
        }

        internal static string Need(string tool, int tier)
        {
            return "a " + Line[Mathf.Clamp(tier, 1, Max)] + " " + tool + " or better";
        }
    }

    // One tool in its five tiers.
    internal class ToolSet
    {
        private readonly int _max;
        internal readonly CustomItem[] ByTier;
        internal ToolSet(int max = Tiers.Max) { _max = max; ByTier = new CustomItem[max + 1]; }

        // 0 = not one of these
        internal int TierOf(ItemDrop.ItemData item)
        {
            if (item == null) return 0;
            for (int t = 1; t <= _max; t++)
                if (ByTier[t] != null && ByTier[t].Is(item)) return t;
            return 0;
        }

        internal bool Is(ItemDrop.ItemData item) { return TierOf(item) > 0; }

        internal GameObject Prefab(int tier)
        {
            CustomItem ci = ByTier[Mathf.Clamp(tier, 1, _max)];
            return ci != null ? ci.Prefab : null;
        }

        internal bool HasPrefab(int hash)
        {
            for (int t = 1; t <= _max; t++)
                if (ByTier[t] != null && ByTier[t].PrefabName.GetStableHashCode() == hash) return true;
            return false;
        }

        // the tool of this kind equipped in your hands (its tier), 0 = none
        internal int Equipped(Player p)
        {
            if (p == null) return 0;
            foreach (ItemDrop.ItemData it in p.GetInventory().GetAllItems())
            {
                int t = TierOf(it);
                if (t > 0 && p.IsItemEquiped(it)) return t;
            }
            return 0;
        }
    }

    // What each tier's tools look like and cost. Agreed with the user (2026-11-10): leather and rope goods at the
    // workbench, metal traps at the forge, carapace at the black forge, like the game's own items of the same stuff.
    internal static class Recipes
    {
        internal struct R { internal string Cost, Station; internal int Level; }

        private static R Make(string cost, string station, int level) { return new R { Cost = cost, Station = station, Level = level }; }

        internal static readonly R[] Lasso =
        {
            default(R),
            Make("LeatherScraps:4,DeerHide:1", "piece_workbench", 1),
            Make("TrollHide:3,LeatherScraps:2", "piece_workbench", 2),
            Make("WolfPelt:3,TrollHide:2", "piece_workbench", 3),
            Make("LinenThread:6,LoxPelt:2", "piece_workbench", 3),
            Make("JuteRed:4,Carapace:2", "blackforge", 1),
        };
        internal static readonly R[] Trap =
        {
            default(R),
            Make("Flint:4,Wood:2,LeatherScraps:2", "piece_workbench", 1),
            Make("Bronze:3,Wood:2", "forge", 1),
            Make("Iron:3,Silver:1,FineWood:2", "forge", 2),
            Make("BlackMetal:3,FineWood:2", "forge", 3),
            Make("Carapace:4,YggdrasilWood:2", "blackforge", 1),
        };
        internal static readonly R[] Halter =
        {
            default(R),
            Make("LeatherScraps:3,DeerHide:1", "piece_workbench", 1),
            Make("TrollHide:2,LeatherScraps:2", "piece_workbench", 2),
            Make("WolfPelt:2,TrollHide:2", "piece_workbench", 3),
            Make("LinenThread:4,LoxPelt:1", "piece_workbench", 3),
            Make("JuteRed:2,Carapace:1", "blackforge", 1),
        };
        internal static readonly R[] Saddle =
        {
            default(R),
            Make("DeerHide:4,LeatherScraps:6,Wood:2", "piece_workbench", 2),
            Make("TrollHide:5,LeatherScraps:4,FineWood:2", "piece_workbench", 2),
            Make("WolfPelt:6,TrollHide:4,FineWood:2", "piece_workbench", 3),
            Make("LoxPelt:4,LinenThread:6,FineWood:4", "piece_workbench", 3),
            Make("Carapace:6,JuteRed:4,YggdrasilWood:4", "blackforge", 1),
            Make("DragonTear:3,WolfPelt:6,Silver:4,FreezeGland:4", "piece_workbench", 4),   // Drake Saddle: after Moder
        };
        internal const string Totem = "Wood:10,Dandelion:6,LeatherScraps:2";

        // each tier's colour, for icons, coils, trap iron and saddle leather (multiplied over the base look)
        internal static readonly Color[] Tint =
        {
            Color.white,
            Color.white,                          // leather: as it is
            new Color(0.62f, 0.78f, 0.92f),       // troll: blue-grey hide
            new Color(0.88f, 0.88f, 0.92f),       // wolf: pale grey fur
            new Color(1f, 0.86f, 0.55f),          // lox: golden
            new Color(0.55f, 0.5f, 0.8f),         // carapace: dusky violet
            new Color(0.62f, 0.86f, 1f),          // drake: icy blue
        };
    }
}
