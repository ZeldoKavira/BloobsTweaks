using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace BloobsTweaks
{
    // Lets crafting use materials sitting in the bank. Every station and artisan skill reads and spends its
    // inputs through InventorySystem.GetItemQuantity / HasItem / RemoveItem, so inside those classes only
    // (including their coroutines and lambdas) we swap those calls for versions that also count the bank and
    // take any shortfall from it. Inventory checks anywhere else - quests, shops, equipment - are untouched.
    [HarmonyPatch]
    internal static class BankCrafting
    {
        // Classes that check or consume recipe inputs: the artisan skills (and firemaking, which burns logs), their
        // station windows and altars.
        private static readonly string[] CraftingTypes =
        {
            "CookingSkill", "SmithingSkill", "CraftingSkill", "BowCraftingSkill", "ImbuingSkill", "HerbologySkill", "FiremakingSkill",
            "CookingUI", "SmithingUI", "BowCraftingUI", "ImbuingUI", "FurnaceUI", "FoundryUI", "WorkbenchUI", "LoomUI",
            "WellUI", "HerbWellUI", "HerbCauldron", "BarMaker", "UnfinishedPotionMaker", "SacrificeUI", "AutoImbuingController",
            "AltarUI", "AltarArdentUI", "AltarConstrictionUI", "AltarRadianceUI", "AltarUIBlood", "AltarUIDevouring",
            "AltarUIDistruption", "AltarUIEntropy", "AltarUIFrost", "AltarUIOmni", "AltarUIPlasma", "AltarUIRift",
            "AltarUIVerdance", "AltarVoidUI",
        };

        private static readonly MethodInfo InvGetQuantity = AccessTools.Method(typeof(InventorySystem), nameof(InventorySystem.GetItemQuantity), new[] { typeof(string) });
        private static readonly MethodInfo InvHasItem = AccessTools.Method(typeof(InventorySystem), nameof(InventorySystem.HasItem), new[] { typeof(string), typeof(int) });
        private static readonly MethodInfo InvRemove = AccessTools.Method(typeof(InventorySystem), nameof(InventorySystem.RemoveItem), new[] { typeof(string), typeof(int), typeof(bool) });

        // Homesteading costs the game itself only takes from the inventory: seed research, workbench recipes,
        // house upgrades, and ranch traps. (Seeds, eggs, decorations and world buildings already use the bank,
        // seeds and eggs from homestead tier 7 - that unlock is left as the game has it.)
        private static readonly string[] HomesteadTypes = { "HSSeedResearchManager", "HSWorkbenchManager", "HomesteadTierSwitcher" };
        private static readonly (string Type, string Method)[] HomesteadMethods =
            { ("RanchCaptureController", "HasTrap"), ("RanchCaptureController", "ConsumeTrap") };

        private static readonly Dictionary<MethodInfo, MethodInfo> Replacements = new Dictionary<MethodInfo, MethodInfo>
        {
            { InvGetQuantity, AccessTools.Method(typeof(BankCrafting), nameof(GetItemQuantity)) },
            { InvHasItem, AccessTools.Method(typeof(BankCrafting), nameof(HasItem)) },
            { InvRemove, AccessTools.Method(typeof(BankCrafting), nameof(RemoveItem)) },
        };

        private static readonly Dictionary<MethodInfo, MethodInfo> HomeReplacements = new Dictionary<MethodInfo, MethodInfo>
        {
            { InvGetQuantity, AccessTools.Method(typeof(BankCrafting), nameof(HomeGetItemQuantity)) },
            { InvHasItem, AccessTools.Method(typeof(BankCrafting), nameof(HomeHasItem)) },
            { InvRemove, AccessTools.Method(typeof(BankCrafting), nameof(HomeRemoveItem)) },
        };

        private static readonly HashSet<MethodBase> HomesteadTargets = new HashSet<MethodBase>();

        // Fishing bait: feathers, worms, mackerel, herring, frost/sand worms and bananas are checked and spent by
        // the fishing spots, and FishingSkill decides from them which bait methods are usable.
        private static readonly string[] FishingTypes = { "FishingSpots", "FishingSkill" };

        private static readonly Dictionary<MethodInfo, MethodInfo> BaitReplacements = new Dictionary<MethodInfo, MethodInfo>
        {
            { InvGetQuantity, AccessTools.Method(typeof(BankCrafting), nameof(BaitGetItemQuantity)) },
            { InvHasItem, AccessTools.Method(typeof(BankCrafting), nameof(BaitHasItem)) },
            { InvRemove, AccessTools.Method(typeof(BankCrafting), nameof(BaitRemoveItem)) },
        };

        private static readonly HashSet<MethodBase> FishingTargets = new HashSet<MethodBase>();

        // Loot-chest keys (LootChest) and thieving lockpicks (Chests, ThievingSkill). Thieving checks the
        // inventory and then the equipped lockpick slot; the inventory check now includes the bank.
        private static readonly string[] KeyTypes = { "LootChest", "Chests", "ThievingSkill" };

        private static readonly Dictionary<MethodInfo, MethodInfo> KeyReplacements = new Dictionary<MethodInfo, MethodInfo>
        {
            { InvGetQuantity, AccessTools.Method(typeof(BankCrafting), nameof(KeyGetItemQuantity)) },
            { InvHasItem, AccessTools.Method(typeof(BankCrafting), nameof(KeyHasItem)) },
            { InvRemove, AccessTools.Method(typeof(BankCrafting), nameof(KeyRemoveItem)) },
        };

        private static readonly HashSet<MethodBase> KeyTargets = new HashSet<MethodBase>();

        static IEnumerable<MethodBase> TargetMethods()
        {
            var assembly = typeof(InventorySystem).Assembly;
            var found = new List<MethodBase>();
            foreach (var name in CraftingTypes)
                found.AddRange(MethodsOf(assembly, name));
            var crafting = found.Count;

            foreach (var name in HomesteadTypes)
                foreach (var m in MethodsOf(assembly, name))
                    if (HomesteadTargets.Add(m))
                        found.Add(m);
            foreach (var (typeName, methodName) in HomesteadMethods)
            {
                var m = assembly.GetType(typeName) is Type t ? AccessTools.Method(t, methodName) : null;
                if (m != null && CallsInventory(m) && HomesteadTargets.Add(m))
                    found.Add(m);
            }
            foreach (var name in FishingTypes)
                foreach (var m in MethodsOf(assembly, name))
                    if (FishingTargets.Add(m))
                        found.Add(m);
            foreach (var name in KeyTypes)
                foreach (var m in MethodsOf(assembly, name))
                    if (KeyTargets.Add(m))
                        found.Add(m);
            Plugin.Log.LogInfo($"Bank crafting: patching {crafting} crafting, {HomesteadTargets.Count} homesteading, " +
                               $"{FishingTargets.Count} fishing bait and {KeyTargets.Count} key/lockpick methods");
            return found;
        }

        private static IEnumerable<MethodInfo> MethodsOf(Assembly assembly, string name)
        {
            var type = assembly.GetType(name);
            if (type == null)
            {
                Plugin.Log.LogWarning($"Bank crafting: type {name} not found");
                yield break;
            }
            foreach (var t in WithNested(type))
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (!m.IsAbstract && !m.ContainsGenericParameters && m.GetMethodBody() != null && CallsInventory(m))
                        yield return m;
        }

        private static IEnumerable<Type> WithNested(Type type)
        {
            yield return type;
            foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                foreach (var t in WithNested(nested))
                    yield return t;
        }

        private static bool CallsInventory(MethodInfo method)
        {
            try
            {
                return PatchProcessor.ReadMethodBody(method).Any(i => i.Value is MethodInfo called && Replacements.ContainsKey(called));
            }
            catch
            {
                return false;
            }
        }

        // The replacements are static methods taking the InventorySystem first, so the call's stack layout
        // (instance, then arguments) stays exactly the same.
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var map = HomesteadTargets.Contains(original) ? HomeReplacements
                : FishingTargets.Contains(original) ? BaitReplacements
                : KeyTargets.Contains(original) ? KeyReplacements
                : Replacements;
            foreach (var ins in instructions)
            {
                if ((ins.opcode == OpCodes.Callvirt || ins.opcode == OpCodes.Call) && ins.operand is MethodInfo called &&
                    map.TryGetValue(called, out var replacement))
                {
                    ins.opcode = OpCodes.Call;
                    ins.operand = replacement;
                }
                yield return ins;
            }
        }

        private static BankSystem Bank(InventorySystem inventory)
        {
            var bank = inventory != null ? inventory.bankSystem : null;
            return bank != null && bank.IsReady ? bank : null;
        }

        // Crafting group.
        public static int GetItemQuantity(InventorySystem inventory, string itemName) => Count(inventory, itemName, Plugin.CraftFromBank.Value);
        public static bool HasItem(InventorySystem inventory, string itemName, int amount) => Has(inventory, itemName, amount, Plugin.CraftFromBank.Value);
        public static bool RemoveItem(InventorySystem inventory, string itemName, int quantity, bool updateUI) =>
            Remove(inventory, itemName, quantity, updateUI, Plugin.CraftFromBank.Value);

        // Homesteading group.
        public static int HomeGetItemQuantity(InventorySystem inventory, string itemName) => Count(inventory, itemName, Plugin.HomesteadFromBank.Value);
        public static bool HomeHasItem(InventorySystem inventory, string itemName, int amount) => Has(inventory, itemName, amount, Plugin.HomesteadFromBank.Value);
        public static bool HomeRemoveItem(InventorySystem inventory, string itemName, int quantity, bool updateUI) =>
            Remove(inventory, itemName, quantity, updateUI, Plugin.HomesteadFromBank.Value);

        // Fishing bait group.
        public static int BaitGetItemQuantity(InventorySystem inventory, string itemName) => Count(inventory, itemName, Plugin.BaitFromBank.Value);
        public static bool BaitHasItem(InventorySystem inventory, string itemName, int amount) => Has(inventory, itemName, amount, Plugin.BaitFromBank.Value);
        public static bool BaitRemoveItem(InventorySystem inventory, string itemName, int quantity, bool updateUI) =>
            Remove(inventory, itemName, quantity, updateUI, Plugin.BaitFromBank.Value);

        // Keys and lockpicks group.
        public static int KeyGetItemQuantity(InventorySystem inventory, string itemName) => Count(inventory, itemName, Plugin.KeysFromBank.Value);
        public static bool KeyHasItem(InventorySystem inventory, string itemName, int amount) => Has(inventory, itemName, amount, Plugin.KeysFromBank.Value);
        public static bool KeyRemoveItem(InventorySystem inventory, string itemName, int quantity, bool updateUI) =>
            Remove(inventory, itemName, quantity, updateUI, Plugin.KeysFromBank.Value);

        internal static int Count(InventorySystem inventory, string itemName, bool useBank)
        {
            var count = inventory.GetItemQuantity(itemName);
            if (!useBank || string.IsNullOrEmpty(itemName))
                return count;
            var bank = Bank(inventory);
            return bank != null ? count + bank.GetItemQuantity(itemName) : count;
        }

        private static bool Has(InventorySystem inventory, string itemName, int amount, bool useBank)
        {
            if (inventory.HasItem(itemName, amount))
                return true;
            return useBank && Count(inventory, itemName, true) >= amount;
        }

        // Spend from the inventory first, then the bank. If the two together still fall short, behave exactly
        // as the game does on its own.
        private static bool Remove(InventorySystem inventory, string itemName, int quantity, bool updateUI, bool useBank)
        {
            var inBag = inventory.GetItemQuantity(itemName);
            var bank = useBank ? Bank(inventory) : null;
            if (bank == null || inBag >= quantity || string.IsNullOrEmpty(itemName))
                return inventory.RemoveItem(itemName, quantity, updateUI);

            var inBank = bank.GetItemQuantity(itemName);
            if (inBag + inBank < quantity)
                return inventory.RemoveItem(itemName, quantity, updateUI);

            if (inBag > 0)
                inventory.RemoveItem(itemName, inBag, updateUI);
            InventoryAddItemPatch.Depositing = true; // keep the bank's click sound quiet, as for overflow deposits
            try
            {
                bank.RemoveItem(itemName, quantity - inBag);
            }
            finally
            {
                InventoryAddItemPatch.Depositing = false;
            }
            return true;
        }
    }

    // Stations check for a free inventory slot before they start. With overflow-to-bank on, the output can go
    // to the bank instead, so a full inventory shouldn't block them. HasSpaceForItem is only used by these gates.
    [HarmonyPatch(typeof(InventorySystem), nameof(InventorySystem.HasSpaceForItem))]
    internal static class StationSpacePatch
    {
        static void Postfix(InventorySystem __instance, string itemName, int quantity, ref bool __result)
        {
            if (__result || !Plugin.OverflowEnabled.Value || __instance.bankSystem == null || !__instance.bankSystem.IsReady)
                return;
            __result = __instance.bankSystem.CanAddItem(itemName, Math.Max(1, quantity));
        }
    }
}
