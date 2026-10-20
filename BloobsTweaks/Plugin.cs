using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace BloobsTweaks
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "zeldo.bloobs.tweaks";
        public const string Name = "Bloobs Tweaks";
        public const string Version = "1.21.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<KeyboardShortcut> MenuKey;

        internal static ConfigEntry<bool> OverflowEnabled;
        internal static ConfigEntry<bool> OverflowMessages;
        internal static ConfigEntry<float> OverflowMessageCooldown;
        internal static ConfigEntry<bool> OverflowMuteBankSound;
        internal static ConfigEntry<bool> CraftFromBank;
        internal static ConfigEntry<bool> HomesteadFromBank;
        internal static ConfigEntry<bool> HomesteadBankAnyTier;
        internal static ConfigEntry<bool> BaitFromBank;
        internal static ConfigEntry<bool> KeysFromBank;
        internal static ConfigEntry<bool> EatFromBank;
        internal static ConfigEntry<bool> UnlimitedAutoManage;
        internal static ConfigEntry<bool> InfiniteResources;
        internal static ConfigEntry<bool> LootShowTotals;

        internal static ConfigEntry<bool> AutoGoldenEnabled;

        internal static ConfigEntry<bool> UntrackedChallengesEnabled;

        internal static ConfigEntry<bool> AutoRefightEnabled;
        internal static ConfigEntry<int> AutoRefightMaxDeaths;
        internal static ConfigEntry<float> AutoRefightDelay;

        internal static ConfigEntry<bool> ItemTooltips;
        internal static ConfigEntry<bool> ItemCtrlClick;
        internal static ConfigEntry<bool> RecipeLinksEnabled;
        internal static ConfigEntry<bool> RecipeAmountColors;
        internal static ConfigEntry<bool> KeyWalksToChest;

        internal static ConfigEntry<bool> BestToolsFromBank;
        internal static ConfigEntry<bool> BestGearOnCombat;
        internal static ConfigEntry<bool> FastestBoots;

        internal static ConfigEntry<float> SpeedMultiplier;
        internal static ConfigEntry<float> SpeedMax;
        internal static ConfigEntry<bool> SmoothHighSpeed;
        internal static ConfigEntry<bool> SkillCatchUp;

        internal static ConfigEntry<bool> AutoLoadEnabled;
        internal static ConfigEntry<int> AutoLoadSlot;
        internal static ConfigEntry<int> AutoLoadLastSlot;

        private void Awake()
        {
            Log = Logger;

            MenuKey = Config.Bind("Menu", "ToggleKey", new KeyboardShortcut(KeyCode.F8),
                "Opens and closes the in-game settings window.");

            OverflowEnabled = Config.Bind("OverflowToBank", "Enabled", true,
                "Send items to the bank when they would otherwise be lost to a full inventory.");
            OverflowMessages = Config.Bind("OverflowToBank", "ShowMessages", true,
                "Show an on-screen note when items are sent to the bank.");
            OverflowMessageCooldown = Config.Bind("OverflowToBank", "MessageCooldownSeconds", 15f,
                "Minimum seconds between notes for the same item. Quantities banked in between are summed into the next note.");
            OverflowMuteBankSound = Config.Bind("OverflowToBank", "MuteBankSound", true,
                "Skip the bank click sound for overflow deposits, which would otherwise play on every gather.");

            CraftFromBank = Config.Bind("Bank", "CraftFromBank", true,
                "Crafting stations, artisan skills and firemaking count materials in the bank and use them when the inventory runs short.");
            HomesteadFromBank = Config.Bind("Bank", "HomesteadFromBank", true,
                "Seed research, workbench recipes, house upgrades and ranch traps use the bank when the inventory runs short.");
            HomesteadBankAnyTier = Config.Bind("Bank", "HomesteadBankAnyTier", true,
                "Let crop seeds, ranch eggs, swarm eggs and decoration costs come from the bank before homestead tier 7 " +
                "(the game normally unlocks that at tier 7). The game's 'use seeds from bank' and 'save one in bank' options still apply.");
            BaitFromBank = Config.Bind("Bank", "BaitFromBank", true,
                "Fishing uses feathers, worms, mackerel, herring, frost/sand worms and bananas from the bank when the inventory runs out.");
            KeysFromBank = Config.Bind("Bank", "KeysFromBank", true,
                "Loot chests use keys, and thieving uses lockpicks, from the bank when the inventory runs out.");
            EatFromBank = Config.Bind("Bank", "EatFromBank", true,
                "When auto-eat finds no food in the food slot or inventory, it eats the lowest-HP food from the bank (the game's own rule).");
            UnlimitedAutoManage = Config.Bind("Bank", "UnlimitedAutoSellList", true,
                "No cap on how many items can be on the auto-sell / auto-bank list. Amount per tick, items per tick and tick timing are unchanged. " +
                "If you turn this off (or remove the mod), the game drops list entries beyond its own cap.");
            LootShowTotals = Config.Bind("Bank", "PickupShowsTotals", true,
                "Add how many you now have in your bag and bank to 'Picked up' notes (item pickups and every gathering skill).");

            AutoGoldenEnabled = Config.Bind("AutoGolden", "Enabled", true,
                "Claim Golden Resource rewards (trees, ores, bushes, fishing spots, thieving chests) as soon as they glow, without mousing over them.");
            InfiniteResources = Config.Bind("Resources", "InfiniteResourceHealth", true,
                "Trees, rocks, bushes and fishing spots never run out. All their rewards come per 100 damage, so nothing is lost. " +
                "Thieving chests are excluded: they only roll their golden chance when they respawn.");

            AutoRefightEnabled = Config.Bind("Combat", "AutoRefight", true,
                "After dying mid-fight, head back and fight the same enemy again (auto Beast Mastery resumes too).");
            AutoRefightMaxDeaths = Config.Bind("Combat", "AutoRefightMaxDeathsWithoutKill", 3,
                "Stop refighting after this many deaths in a row with no kill in between, so an unwinnable fight doesn't loop.");
            AutoRefightDelay = Config.Bind("Combat", "AutoRefightDelaySeconds", 3f,
                "Real seconds to wait after dying before heading back.");

            UntrackedChallengesEnabled = Config.Bind("Challenges", "CountUntracked", true,
                "Once Auto Navigate + Start is owned and Auto Progress is on, actions also count toward untracked challenges " +
                "whose current tier Auto Progress covers. They roll into later tiers by the same rules as tracked ones.");

            ItemTooltips = Config.Bind("ItemInfo", "TooltipUses", true,
                "Add 'Used in / Made with / Gathered with' lines to inventory and bank tooltips, taken from the skill windows.");
            ItemCtrlClick = Config.Bind("ItemInfo", "CtrlClickOpensSkill", true,
                "Ctrl+left-click an item in the inventory or bank to open the skill window that uses it. Click again to cycle through skills.");

            RecipeLinksEnabled = Config.Bind("ItemInfo", "RecipeIngredientLinks", true,
                "In skill-window recipe lists, click an ingredient's name to open the window where it is made or gathered.");
            RecipeAmountColors = Config.Bind("ItemInfo", "RecipeIngredientColors", true,
                "In skill-window recipe lists, show each 'Nx Ingredient' green when you have N and red when you don't (bank included when crafting from the bank).");

            KeyWalksToChest = Config.Bind("ItemInfo", "ClickKeyWalksToChest", true,
                "Left-click a chest key in the inventory to walk to the nearest chest it opens and start opening it.");

            BestToolsFromBank = Config.Bind("Gear", "BestToolsFromBank", true,
                "When a gathering skill picks its tool, equip a better usable axe/pickaxe/rod/lens from the bank first.");
            BestGearOnCombat = Config.Bind("Gear", "BestGearOnCombatStart", true,
                "When a fight starts, equip the best armour and weapon you own (inventory or bank) for your combat style. " +
                "Only the active equipment set is changed; replaced pieces go to the bank.");
            FastestBoots = Config.Bind("Gear", "FastestBootsOutsideCombat", true,
                "Outside combat, keep your fastest usable boots on (checked every 10 seconds and when a dexterity course starts). " +
                "Boots are the only gear with run speed, and it speeds up all walking. In a fight the combat pick decides the boots.");

            SpeedMultiplier = Config.Bind("GameSpeed", "Multiplier", 1f,
                "Multiplies how fast the game runs. Stacks with the game's own 2x/3x/turbo speed-ups. 1 = normal.");
            SpeedMax = Config.Bind("GameSpeed", "MaxMultiplier", 200f,
                "Upper end of the speed slider in the settings window.");
            SmoothHighSpeed = Config.Bind("GameSpeed", "SmoothHighSpeed", true,
                "While the game runs faster than normal: glide the camera instead of snapping, play the Bloob's and enemies' " +
                "animations at normal speed, and keep XP/damage/loot popups up for their usual real-time duration.");
            SkillCatchUp = Config.Bind("GameSpeed", "GatheringCatchUp", true,
                "Skills normally manage at most one action per frame (gathering, thieving, crafting, firemaking, combat, tracking, devotion, courses), which caps them " +
                "well below high speed settings. This runs the actions owed for each frame so they keep up with the game speed.");

            AutoLoadEnabled = Config.Bind("Launch", "AutoLoadSave", false,
                "Load a save as soon as the main menu appears after launching. Hold Shift while the menu appears to skip it. " +
                "Never starts a new game: a slot without a save is left alone.");
            AutoLoadSlot = Config.Bind("Launch", "AutoLoadSlot", -1,
                "Which save to load: -1 = the last one played, 0-2 = Save 1-3, 3 = Turbo Mode.");
            AutoLoadLastSlot = Config.Bind("Launch", "LastPlayedSlot", -1,
                "Set by the mod whenever a save is loaded; used when AutoLoadSlot is -1.");

            // Earlier versions shipped defaults of 10 and 50; move anyone still on one of those to the new default.
            if (Mathf.Approximately(SpeedMax.Value, 10f) || Mathf.Approximately(SpeedMax.Value, 50f))
                SpeedMax.Value = 200f;

            // Patch each feature on its own: PatchAll stops at the first failure, so one feature broken by a
            // game update would silently take every later one down with it.
            var harmony = new Harmony(Guid);
            foreach (var type in AccessTools.GetTypesFromAssembly(typeof(Plugin).Assembly))
            {
                try
                {
                    harmony.CreateClassProcessor(type).Patch();
                }
                catch (System.Exception e)
                {
                    Log.LogError($"Patch {type.Name} failed; that feature is off: {e}");
                }
            }

            // BepInEx's own manager object isn't guaranteed to survive scene loads in every game, so the
            // behaviours get a host of their own.
            var host = new GameObject("BloobsTweaks");
            host.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(host);
            host.AddComponent<GameSpeed>();
            host.AddComponent<SmoothAnimations>();
            host.AddComponent<AutoGolden>();
            host.AddComponent<AutoRefight>();
            host.AddComponent<FastestBootsWatcher>();
            host.AddComponent<ItemUsesWarmup>();
            host.AddComponent<AutoLoadSave>();
            host.AddComponent<SettingsWindow>();

            Log.LogInfo($"{Name} {Version} loaded");
        }
    }
}
