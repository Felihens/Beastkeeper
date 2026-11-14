using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Beastkeeper
{
    [BepInPlugin(GUID, NAME, VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "felih.Beastkeeper";
        public const string NAME = "Beastkeeper";
        public const string VERSION = "0.4.0";

        internal static ManualLogSource Log;
        internal static Plugin Instance;
        internal static ConfigEntry<bool> Enabled, DetailedLog;

        internal static ConfigEntry<string> ExtraAnimals, NotAnimals;
        internal static ConfigEntry<int> MaxLeashed;
        internal static ConfigEntry<float> ThrowSpeed;
        internal static ConfigEntry<float> ThrowRange, RopeLength, RunDistance, SlowDistance, BreakDistance, SlowestSpeed;

        internal static ConfigEntry<bool> CatchWild;
        internal static ConfigEntry<float> StartProgress, PullPerPress, AnimalPull, HandFeedCooldown;
        internal static ConfigEntry<bool> HandFeeding;
        internal static ConfigEntry<string> SeaMounts;
        internal static ConfigEntry<float> SerpentSpeed, SerpentFoodMinutes, SerpentHungrySpeed, SerpentGetOffRange, SerpentSwimDepth, SerpentTurnSpeed, SerpentLift, SerpentSeatUp, SerpentSeatBack, SerpentMinDepth, SerpentCruiseSpeed, SerpentReverseSpeed, SerpentSprintAccel;

        internal static ConfigEntry<float> RopeWidth, ShakeStrength;

        internal static ConfigEntry<bool> ExportForEditor;
        internal static ConfigEntry<string> SaddleFit;
        internal static ConfigEntry<float> SerpentBob, CameraTilt;
        internal static int FitVersion;   // goes up when a seat or saddle setting changes, so they're redone live
        internal static ConfigEntry<string> SeatHeights, NotRideable;
        internal static ConfigEntry<float> TrapRadius, TrapHoldSeconds, RideMinHeight, RideAcceleration, RideWalkSpeed, RideSideSpeed, RideJumpForce;
        internal static ConfigEntry<float> TotemRange, TotemRadius, WanderRadius;
        internal static ConfigEntry<bool> TamedDefend;
        internal static ConfigEntry<string> TamedHealth, TamedDamage;
        internal static ConfigEntry<float> DefendRange;
        internal static ConfigEntry<KeyboardShortcut> CallKey;
        internal static ConfigEntry<string> DrakeMounts;
        internal static ConfigEntry<float> DrakeSize, DrakeSpeed, DrakeSprintSpeed, DrakeFlightSeconds, DrakeRestSeconds, DrakeMaxAltitude;
        internal static ConfigEntry<bool> DrakeAttackInAir, DrakeEggNeedsFire, RiderMotionOn;
        internal static ConfigEntry<float> DrakeHatchMinutes, DrakeGrowMinutes, DrakeEggAlertRange, DrakeAttackDamage, DrakeFireMax, DrakeAttackSpeed, DrakeAttackCooldown, DrakeGlowChance;
        internal static ConfigEntry<string>[] DrakeColors;
        internal static ConfigEntry<float> DrakeWalkSeconds, DrakeGroundSpeed, DrakeGroundSprint, DrakeFrostSize;

        private void Awake()
        {
            Log = Logger;
            Instance = this;
            Enabled = Config.Bind("1 - General", "Enabled", true, "Turns the whole mod on or off.");

            ThrowRange = Bind("2 - Lasso", "ThrowRange", 14f,
                new ConfigDescription("How far you can throw the lasso (metres).", new AcceptableValueRange<float>(4f, 40f)));
            ThrowSpeed = Bind("2 - Lasso", "ThrowSpeed", 16f,
                new ConfigDescription("How fast the loop flies (metres per second). It flies in an arc and only catches what it touches.", new AcceptableValueRange<float>(6f, 40f)));
            ExtraAnimals = Bind("2 - Lasso", "ExtraAnimals", "Bjorn,Bjorn_sleeping,Hare,Hen,Chicken,Asksvin,Lox,Boar,Wolf,Serpent",
                "Creatures the lasso holds besides the ones the game treats as animals (prefab names, separated by commas).");
            NotAnimals = Bind("2 - Lasso", "NotAnimals", "Goblin_Gem,Tick,Leech,Leech_cave,Neck,Unbjorn,Volture",
                "Creatures the lasso never holds (prefab names, separated by commas).");
            MaxLeashed = Bind("2 - Lasso", "MaxAnimals", 3,
                new ConfigDescription("How many animals you can hold on lassos at once.", new AcceptableValueRange<int>(1, 10)));
            RopeLength = Bind("2 - Lasso", "RopeLength", 2.5f,
                new ConfigDescription("How close a lassoed animal keeps to you (metres).", new AcceptableValueRange<float>(1.5f, 8f)));
            RunDistance = Bind("2 - Lasso", "RunDistance", 6f,
                new ConfigDescription("Beyond this distance a lassoed animal runs to catch up (metres).", new AcceptableValueRange<float>(3f, 20f)));
            SlowDistance = Bind("2 - Lasso", "SlowDistance", 10f,
                new ConfigDescription("Beyond this distance the rope is taut and slows you down when you walk away from the animal (metres).", new AcceptableValueRange<float>(3f, 40f)));
            BreakDistance = Bind("2 - Lasso", "BreakDistance", 17f,
                new ConfigDescription("At this distance the rope slips off the animal and it is free (metres).", new AcceptableValueRange<float>(4f, 50f)));
            SlowestSpeed = Bind("2 - Lasso", "SlowestSpeed", 0.15f,
                new ConfigDescription("Your speed right before the rope slips off (0.3 = 30%).", new AcceptableValueRange<float>(0.05f, 1f)));

            CatchWild = Bind("3 - Catching wild animals", "CatchWild", true,
                "Throw the lasso at a wild animal that hasn't noticed you, then win the tug of war to catch it (S; harder animals add A / D and the mouse buttons).");
            StartProgress = Bind("3 - Catching wild animals", "StartProgress", 0.35f,
                new ConfigDescription("How full the tug-of-war bar starts.", new AcceptableValueRange<float>(0.05f, 0.95f)));
            PullPerPress = Bind("3 - Catching wild animals", "PullPerPress", 0.06f,
                new ConfigDescription("How much each press of S fills the bar.", new AcceptableValueRange<float>(0.01f, 0.5f)));
            AnimalPull = Bind("3 - Catching wild animals", "AnimalStrength", 1f,
                new ConfigDescription("How hard animals pull back (multiplier). Bigger animals pull harder.", new AcceptableValueRange<float>(0.1f, 5f)));
            HandFeeding = Bind("3 - Catching wild animals", "HandFeeding", true,
                "Look at a tamed animal and use any food from your hotbar: it eats from your hand and heals.");
            HandFeedCooldown = Bind("3 - Catching wild animals", "HandFeedCooldown", 15f,
                new ConfigDescription("Seconds before the same animal can be hand fed again (by anyone).", new AcceptableValueRange<float>(0f, 600f)));
            TrapRadius = Bind("5 - Leg-hold trap", "CatchRadius", 1.3f,
                new ConfigDescription("How close to the middle of the trap any part of an animal's body must come to be caught (metres). The trap with its stake is about 1.2 m.", new AcceptableValueRange<float>(0.2f, 4f)));
            TrapHoldSeconds = Bind("5 - Leg-hold trap", "HoldSeconds", 5f,
                new ConfigDescription("How long the trap holds an animal before it pulls free.", new AcceptableValueRange<float>(5f, 3600f)));

            RideMinHeight = Bind("6 - Riding", "MinHeight", 1.3f,
                new ConfigDescription("Smallest animal you can ride: the top of its body must be at least this high (metres). 1.3 leaves out hens, hares, voltures and the like.", new AcceptableValueRange<float>(0.2f, 5f)));
            SeatHeights = Bind("6 - Riding", "SeatHeights", "Boar:1.03:-0.047:3.5:attach_asksvin,Wolf:1.213:-0.148:0.5:attach_asksvin,Deer:1.367:0.388:4::-45:0,Bjorn:2.355:-0.414:0:attach_asksvin:0:21.5,Serpent:1.2:2.5,Lox:3.501:-0.556:0:attach_asksvin:0:23.5,Asksvin:1.73:-0.259:0:attach_asksvin:-6:-5.5,Seal:0.773:0.333:0::-36.5:0,Moose:2.944:-0.034,Hatchling:1.667:0.039:0:attach_asksvin:0:-10",
                "Where you sit on each animal: prefab:height:forward (metres), then optionally :lean (degrees, your back bent forward), :pose (the game's riding pose, e.g. attach_asksvin), :legs forward and :legs open (degrees). Made with the saddle editor page. Animals not listed: near the top of their body.");
            NotRideable = Bind("6 - Riding", "NotRideable", "Neck,Boar_piggy,Wolf_cub,Lox_Calf,Moose_calf,Seal_Pup,Asksvin_hatchling,Unbjorn",
                "Animals that can't be saddled or ridden (prefab names, separated by commas).");
            RideWalkSpeed = Bind("6 - Riding", "WalkSpeed", 1.8f,
                new ConfigDescription("Walking speed of a ridden animal, compared to its own walk (running is unchanged).", new AcceptableValueRange<float>(0.5f, 5f)));
            RiderMotionOn = Bind("6 - Riding", "RiderMotion", true,
                "On: the saddle, the seat and your bones follow each mount's walk and run as posed in the rider editor (only seen by players with the mod). Off: they stay still on the animal.");
            RideJumpForce = Bind("6 - Riding", "JumpForce", 5f,
                new ConfigDescription("How high a ridden animal jumps when you press jump (the same for every animal; 5 is about a metre). 0 = no jumping.", new AcceptableValueRange<float>(0f, 20f)));
            RideSideSpeed = Bind("6 - Riding", "SidewaysSpeed", 0.45f,
                new ConfigDescription("Speed going sideways or backwards, compared to going forward.", new AcceptableValueRange<float>(0.1f, 1f)));
            SaddleFit = Bind("6 - Riding", "SaddleFit", "Boar:1:0.118:-0.115:-28.6:0:0:0:0,Wolf:0.871:0.115:0:-26.8:0:0:0:0,Wolf-Leather:1:-0.01:0.075:11.6:0:0:0:-0.002:0.96:1.47:0,Boar-Leather:1.29:-0.022:0.039:3.1:0:0:0:-0.001:0.93:1.17:0.2,Deer-Leather:0.89:0.041:-0.454:0:0:0:0:0:1:1.38:0.16,Bjorn-Leather:1:0.094:0.175:9.6:0:0:0:0:1:1.08:-0.35,Lox-Leather:1.24:0.099:0.159:11.9:0:0:0:0:1:1.13:-0.4,Asksvin-Leather:0.84:0.072:0.048:34.4:0:0:0:0:1:0.8:0.09,Seal-Leather:1.13:-0.028:-0.434:0:0:0:0:0:1:1:-0.24,Moose-Leather:1.03:0.084:-0.305:6:0:0:0:0.017:1.21:1.03:0,Hatchling-Leather:1:0.071:0.116:53.2:0:0:0:-0.005:0.97:0.8:0.22",
                "How the leather saddle sits on each animal: prefab-Leather:size:up:forward:tilt:front:turn:roll:side:width:drop:pinch. Made with the saddle editor page. Changes show right away.");
            SaddleFit.SettingChanged += (s, e) => FitVersion++;
            CameraTilt = Bind("6 - Riding", "CameraTilt", 0.6f,
                new ConfigDescription("How much the steady riding camera tilts to the sides with your head (0 = none, 1 = as much as your head, up to 3 = three times as much). Always smoothed, at most 15 degrees.", new AcceptableValueRange<float>(0f, 3f)));
            SeatHeights.SettingChanged += (s, e) => FitVersion++;
            RideAcceleration = Bind("6 - Riding", "Acceleration", 1.2f,
                new ConfigDescription("How quickly the animal gets up to speed (full speed in 1/this seconds). It stops four times faster.", new AcceptableValueRange<float>(0.2f, 10f)));

            TotemRange = Bind("7 - Beast totem", "Range", 60f,
                new ConfigDescription("Tamed animals within this distance of a totem belong to it (metres).", new AcceptableValueRange<float>(10f, 300f)));
            TotemRadius = Bind("7 - Beast totem", "Radius", 12f,
                new ConfigDescription("Tamed animals that stray further than this from their totem walk back (metres).", new AcceptableValueRange<float>(3f, 100f)));
            WanderRadius = Bind("7 - Beast totem", "WanderRadius", 6f,
                new ConfigDescription("Animals tamed with a halter graze this far around the spot where you left them (when no totem is near).", new AcceptableValueRange<float>(1f, 50f)));

            TamedHealth = Bind("8 - Defending", "TamedHealth", "120,500,350,1300,1100",
                "Lowest health of a tamed animal per tier (Leather, Troll, Wolf, Lox, Carapace), before stars; the game's own is kept when higher.");
            TamedDamage = Bind("8 - Defending", "TamedDamage", "2.5,1.2,2.5,1.3,1.6",
                "How much harder a tamed animal hits than the game's own, per tier (Leather, Troll, Wolf, Lox, Carapace).");
            TamedDefend = Bind("8 - Defending", "Enabled", true,
                "Tamed animals that can fight (wolf, boar, bear, asksvin, lox...) attack enemies that come near, even while tied, kept by a totem or on your lasso. Off: they stay calm.");
            DefendRange = Bind("8 - Defending", "Range", 15f,
                "How close (metres) an enemy must come before a tamed animal fights it. For a tied animal, counted from the reach of its rope.");
            CallKey = Bind("10 - Favorite mount", "CallKey", new KeyboardShortcut(KeyCode.U),
                "Looking at a tamed animal that has worn a saddle: makes it your favorite mount (press again to undo). Anywhere else: calls your favorite mount to your side, wherever it is. None = off.");
            DrakeMounts = Bind("11 - Drake", "Drakes", "Hatchling",
                "Flying mounts (prefab names, separated by commas; the first is what Moder's eggs hatch into). Wild ones can't be caught: after Moder, take a dragon egg from a drake nest, give it an item and keep it by a fire. Only a Drake Saddle fits a grown one. Empty = off.");
            DrakeSize = Bind("11 - Drake", "Size", 1.8f,
                new ConfigDescription("How big a tamed drake grows, so a viking fits on its back (1 = the game's size).", new AcceptableValueRange<float>(1f, 3f)));
            DrakeSpeed = Bind("11 - Drake", "FlySpeed", 9f,
                new ConfigDescription("Flying speed of a ridden drake (metres per second).", new AcceptableValueRange<float>(2f, 30f)));
            DrakeSprintSpeed = Bind("11 - Drake", "SprintSpeed", 15f,
                new ConfigDescription("Flying speed with the run key (metres per second). Sprinting tires it 50% faster.", new AcceptableValueRange<float>(2f, 40f)));
            DrakeFlightSeconds = Bind("11 - Drake", "FlightSeconds", 45f,
                new ConfigDescription("How long a drake flies higher than 3 m before it's tired and glides down (seconds).", new AcceptableValueRange<float>(5f, 600f)));
            DrakeRestSeconds = Bind("11 - Drake", "RestSeconds", 20f,
                new ConfigDescription("How long a tired drake needs near the ground to rest fully (seconds). Hand feeding rests it at once.", new AcceptableValueRange<float>(1f, 600f)));
            DrakeMaxAltitude = Bind("11 - Drake", "MaxAltitude", 35f,
                new ConfigDescription("The highest a ridden drake flies above the ground or the sea (metres).", new AcceptableValueRange<float>(5f, 300f)));
            DrakeAttackInAir = Bind("11 - Drake", "AttackInAir", false,
                "On: the drake attacks from any height. Off: only near the ground (up to 4 m).");
            DrakeHatchMinutes = Bind("11 - Drake", "HatchMinutes", 5f,
                new ConfigDescription("How long a woken dragon egg must be kept warm before it hatches (minutes).", new AcceptableValueRange<float>(0.2f, 600f)));
            DrakeEggNeedsFire = Bind("11 - Drake", "EggNeedsFire", true,
                "On: a woken egg only warms up next to a fire (like the game's chicken eggs). Off: it hatches anywhere.");
            DrakeGrowMinutes = Bind("11 - Drake", "GrowMinutes", 30f,
                new ConfigDescription("How long a hatched baby drake takes to grow up and take a saddle (minutes).", new AcceptableValueRange<float>(1f, 600f)));
            DrakeEggAlertRange = Bind("11 - Drake", "EggAlertRange", 60f,
                new ConfigDescription("Taking a dragon egg makes the wild drakes this close come for you (metres; 0 = none).", new AcceptableValueRange<float>(0f, 200f)));
            DrakeFireMax = Bind("11 - Drake", "FireMaxDamage", 200f,
                new ConfigDescription("Most a fire drake's attack hurts one target, before its resistances: lower than the others, since the burning keeps hurting after.", new AcceptableValueRange<float>(1f, 2000f)));
            DrakeAttackDamage = Bind("11 - Drake", "AttackDamage", 250f,
                new ConfigDescription("Damage of one ridden drake attack, whatever its element (each keeps its own kind: frost, fire, poison, blunt, lightning).", new AcceptableValueRange<float>(1f, 1000f)));
            DrakeAttackSpeed = Bind("11 - Drake", "AttackSpeed", 1.5f,
                new ConfigDescription("How much faster (and so farther) the attacks fly than the creature it comes from.", new AcceptableValueRange<float>(0.5f, 5f)));
            string[] elements = { "Frost", "Fire", "Poison", "Earth", "Lightning" };
            string[] colors =
            {
                "scales:original,wings:#56809E,horns:#B3DEFF,eyes:original,glow:#9FDEFE",
                "scales:#813232,wings:#B12A25,horns:#EB2424,eyes:#FFFFFF,glow:#FF0000",
                "scales:#45824C,wings:#11A72F,horns:#BBFE9A,eyes:#DB0000,glow:#75DB42",
                "scales:#7D5936,wings:#82490D,horns:#E69E56,eyes:#FF0000,glow:#AD6500",
                "scales:#A08C40,wings:#B08C07,horns:#CFB34F,eyes:#A30000,glow:#FFC800",
            };
            DrakeColors = new ConfigEntry<string>[elements.Length];
            for (int i = 0; i < elements.Length; i++)
            {
                DrakeColors[i] = Bind("12 - Drake colours", elements[i], colors[i],
                    "Colours of a " + elements[i].ToLowerInvariant() + " drake: scales (the painted part of its skin), wings (the rest), horns, eyes, and glow (eyes and horns of the rare glowing ones). "
                    + "#RRGGBB, or 'original' for the game's own look. Easiest to set in the drake colour editor.");
                DrakeColors[i].SettingChanged += (s, e) => Drake.ColorsChanged();
            }
            DrakeWalkSeconds = Bind("11 - Drake", "WalkSeconds", 12f,
                new ConfigDescription("A tamed drake walks this long before it takes off and flies the rest of the way (seconds).", new AcceptableValueRange<float>(0f, 600f)));
            DrakeFrostSize = Bind("11 - Drake", "FrostSize", 2.5f,
                new ConfigDescription("How much bigger than the game's own ice ball a frost drake's shot is.", new AcceptableValueRange<float>(0.5f, 6f)));
            DrakeGroundSpeed = Bind("11 - Drake", "GroundSpeed", 4f,
                new ConfigDescription("How fast a drake walks under its rider after landing (metres per second).", new AcceptableValueRange<float>(1f, 15f)));
            DrakeGroundSprint = Bind("11 - Drake", "GroundSprint", 8f,
                new ConfigDescription("How fast it walks with the run key, on the ground (metres per second).", new AcceptableValueRange<float>(1f, 20f)));
            DrakeGlowChance = Bind("11 - Drake", "GlowChance", 0.2f,
                new ConfigDescription("Chance a drake hatches with eyes and horns that glow in its element's colour (0 = never, 1 = always).", new AcceptableValueRange<float>(0f, 1f)));
            DrakeAttackCooldown = Bind("11 - Drake", "AttackCooldown", 1.5f,
                new ConfigDescription("Seconds between two ridden drake attacks.", new AcceptableValueRange<float>(0.2f, 10f)));
            RopeWidth = Bind("4 - Rope look", "RopeWidth", 0.05f, "Thickness of the rope (metres).");
            ShakeStrength = Bind("4 - Rope look", "ShakeStrength", 1f,
                new ConfigDescription("Camera shake at key moments of a catch (multiplier, 0 = off).", new AcceptableValueRange<float>(0f, 3f)));

            DetailedLog = Bind("9 - Debug", "DetailedLog", false, "Writes details to the BepInEx log (for bug reports), and at game start the list of every creature and whether you can tame or ride it.");
            ExportForEditor = Bind("9 - Debug", "ExportForEditor", false,
                "For the saddle editor page: saves the rideable animals' models when you enter a world, and your character in every riding pose the next time you ride (to BepInEx/config/Beastkeeper_models), then turns itself off.");

            Lasso.Init();
            Trap.Init();
            Mount.Init();
            Totem.Init();

            SeaMounts = Bind("9 - Sea serpent", "SeaMounts", "Serpent", "Creatures ridden on the water: you sit on the head, it stays at the surface, it needs fish (prefab names, separated by commas).");
            SerpentSpeed = Bind("9 - Sea serpent", "Speed", 12f,
                new ConfigDescription("Top speed of a ridden serpent, when you hold run (metres per second; a longship at full sail does about 10, and only with the wind).", new AcceptableValueRange<float>(2f, 40f)));
            SerpentCruiseSpeed = Bind("9 - Sea serpent", "CruiseSpeed", 3f,
                new ConfigDescription("Speed of a ridden serpent when you just swim forward, without running (metres per second; about a rowed ship).", new AcceptableValueRange<float>(1f, 20f)));
            SerpentReverseSpeed = Bind("9 - Sea serpent", "ReverseSpeed", 2f,
                new ConfigDescription("Speed of a ridden serpent backing off with S (metres per second). Always the same, even when it's hungry.", new AcceptableValueRange<float>(0.5f, 10f)));
            SerpentSprintAccel = Bind("9 - Sea serpent", "SprintAcceleration", 6f,
                new ConfigDescription("How fast a ridden serpent speeds up when you hold run (metres per second, every second).", new AcceptableValueRange<float>(1f, 30f)));
            SerpentFoodMinutes = Bind("9 - Sea serpent", "FoodMinutes", 5f,
                new ConfigDescription("Minutes of riding each part of the food bar lasts (4 parts, one fish each).", new AcceptableValueRange<float>(0.5f, 60f)));
            SerpentHungrySpeed = Bind("9 - Sea serpent", "HungrySpeed", 0.5f,
                new ConfigDescription("Speed of a hungry serpent (empty food bar), compared to its full speed.", new AcceptableValueRange<float>(0.1f, 1f)));
            SerpentGetOffRange = Bind("9 - Sea serpent", "GetOffRange", 20f,
                new ConfigDescription("You can only get off a serpent within this distance of land or a ship (metres).", new AcceptableValueRange<float>(5f, 100f)));
            SerpentTurnSpeed = Bind("9 - Sea serpent", "TurnSpeed", 50f,
                new ConfigDescription("How fast A/D turn a ridden serpent (degrees per second), like steering a boat.", new AcceptableValueRange<float>(10f, 180f)));
            SerpentLift = Bind("9 - Sea serpent", "Lift", 0.6f,
                new ConfigDescription("How much higher a ridden serpent's body shows, so its head is out of the water (metres).", new AcceptableValueRange<float>(0f, 3f)));
            SerpentSeatUp = Bind("9 - Sea serpent", "SeatUp", 0.45f,
                new ConfigDescription("How high above the serpent's head you sit (metres).", new AcceptableValueRange<float>(-1f, 3f)));
            SerpentBob = Bind("9 - Sea serpent", "Bob", 1.4f,
                new ConfigDescription("How much a ridden serpent bobs up and down and sways as it swims (1 = gentle, 2 = twice as much).", new AcceptableValueRange<float>(0f, 3f)));
            SerpentMinDepth = Bind("9 - Sea serpent", "MinDepth", 1.2f,
                new ConfigDescription("A ridden serpent runs aground like a boat: it won't swim where the water is shallower than this (metres).", new AcceptableValueRange<float>(0.2f, 5f)));
            SerpentSeatBack = Bind("9 - Sea serpent", "SeatBack", 0.3f,
                new ConfigDescription("How far behind the tip of the serpent's head you sit (metres).", new AcceptableValueRange<float>(-3f, 3f)));
            SerpentSwimDepth = Bind("9 - Sea serpent", "SwimDepth", 0.3f,
                new ConfigDescription("How deep a ridden serpent swims under the surface (metres). Lower = more of it above the water.", new AcceptableValueRange<float>(0f, 3f)));
            Tidy();
            Halter.Init();
            new Harmony(GUID).PatchAll();
            Compat.Init();
            Log.LogInfo(NAME + " " + VERSION + " loaded.");
        }


        // ------------------------------------------------------------ the few settings players see
        // Everything else is tuned in the code: it lives in a config that is never written to a file.
        private static readonly string[] Shown =
        {
            "1 - General/Enabled", "10 - Favorite mount/CallKey", "8 - Defending/Enabled",
            "4 - Rope look/ShakeStrength", "9 - Debug/DetailedLog",
            "2 - Lasso/ExtraAnimals", "2 - Lasso/NotAnimals", "6 - Riding/NotRideable",
        };
        private ConfigFile _fixed;

        private ConfigFile For(string section, string key)
        {
            if (System.Array.IndexOf(Shown, section + "/" + key) >= 0) return Config;
            if (_fixed == null)
            {
                _fixed = new ConfigFile(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Beastkeeper_fixed_" + System.Guid.NewGuid().ToString("N") + ".cfg"), false);
                _fixed.SaveOnConfigSet = false;
            }
            return _fixed;
        }

        private ConfigEntry<T> Bind<T>(string section, string key, T value, string description) { return For(section, key).Bind(section, key, value, description); }
        private ConfigEntry<T> Bind<T>(string section, string key, T value, ConfigDescription description) { return For(section, key).Bind(section, key, value, description); }

        // drop the settings that are no longer shown from the file
        private void Tidy()
        {
            try
            {
                System.Collections.Generic.Dictionary<ConfigDefinition, string> orphans = Traverse.Create(Config).Property("OrphanedEntries").GetValue<System.Collections.Generic.Dictionary<ConfigDefinition, string>>();
                if (orphans != null && orphans.Count > 0) { orphans.Clear(); Config.Save(); }
            }
            catch (System.Exception e) { Log.LogWarning("couldn't tidy the config file: " + e.Message); }
        }
        private void Update()
        {
            if (!Enabled.Value) return;
            Leash.Update();
            Tug.Update();
            Mount.UpdatePress();
            Favorite.Update();
            Throw.Update();
            Show.Update();
            Sea.UpdateBar();
            Drake.UpdateBar();
            Drake.Tick();
            Exporter.Update();
        }

        private void OnGUI()
        {
            if (!Enabled.Value) return;
            Throw.OnGUI();
            Tug.OnGUI();
        }

        internal static void Debug(string msg)
        {
            if (DetailedLog != null && DetailedLog.Value) Log.LogInfo(msg);
        }
    }
}
