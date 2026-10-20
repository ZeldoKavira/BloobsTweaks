using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace BloobsTweaks
{
    // The game's longest InventorySystem.AddItem overload is the one that does the work; the shorter ones
    // forward to it. When the item has no stack in the inventory and there's no free slot, the original just
    // shows "Inventory is full" and drops the item. We catch that case and deposit into the bank instead.
    [HarmonyPatch]
    internal static class InventoryAddItemPatch
    {
        internal static bool Depositing;

        private static readonly Dictionary<string, PendingNote> Pending = new Dictionary<string, PendingNote>();

        private class PendingNote
        {
            public int Quantity;
            public float LastShown = float.NegativeInfinity;
        }

        static MethodBase TargetMethod() =>
            typeof(InventorySystem).GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => m.Name == nameof(InventorySystem.AddItem))
                .OrderByDescending(m => m.GetParameters().Length)
                .First();

        static bool Prefix(InventorySystem __instance, object[] __args)
        {
            if (!Plugin.OverflowEnabled.Value)
                return true;

            var itemName = (string)__args[0];
            var quantity = (int)__args[1];
            if (string.IsNullOrEmpty(itemName) || quantity <= 0)
                return true;

            // Same tests the original makes: stack onto an existing entry, else take the first free slot.
            foreach (var item in __instance.inventoryItems)
                if (!__instance.IsPlaceholder(item) && item.itemName == itemName)
                    return true;
            if (__instance.FindFirstPlaceholderIndex() >= 0)
                return true;

            var bank = __instance.bankSystem;
            if (bank == null || !bank.IsReady || !bank.CanAddItem(itemName, quantity))
                return true; // let the game show its usual inventory-full message

            var before = bank.GetItemByName(itemName)?.quantity ?? -1;
            var countBefore = bank.GetRealItemCount();

            Depositing = true;
            try
            {
                bank.AddItem(itemName, quantity, (Sprite)__args[2], (int)__args[3], (int)__args[4],
                    (int)__args[5], (int)__args[6], (int)__args[7], (int)__args[8], (int)__args[9],
                    (int)__args[10], (int)__args[11], (int)__args[12], (int)__args[13],
                    (float)__args[14], (int)__args[15], (float)__args[16], (int)__args[17],
                    (int)__args[18], (int)__args[19], (float)__args[20], (float)__args[21],
                    (float)__args[22], (float)__args[23], (float)__args[24], (float)__args[25],
                    (float)__args[26], (float)__args[27], allowAutoSort: false);
            }
            finally
            {
                Depositing = false;
            }

            // Mirror the game's own auto-bank check that the deposit actually landed.
            var after = bank.GetItemByName(itemName);
            var landed = after != null && (before < 0 ? bank.GetRealItemCount() > countBefore : after.quantity > before);
            if (!landed)
                return true;

            // Side effects the original would have had for a brand-new item.
            __instance.itemCompendiumManager?.AddItemToCompendium(itemName);
            if (itemName == "Banana" && __instance.steamManager != null &&
                !__instance.steamManager.IsAchievementUnlocked("BLOOBS_ACHIEVEMENT_OBTAINED_BANANA"))
                AchievementManager.Instance?.ObtainedBanana();
            if (itemName == "Chocolate Egg" && __instance.steamManager != null &&
                !__instance.steamManager.IsAchievementUnlocked("BLOOBS_ACHIEVEMENT_OBTAINED_CHOCOLATE_EGG"))
                AchievementManager.Instance?.ObtainedChocolateEgg();

            Note(__instance, itemName, quantity);
            return false;
        }

        private static void Note(InventorySystem inventory, string itemName, int quantity)
        {
            if (!Plugin.OverflowMessages.Value)
                return;

            if (!Pending.TryGetValue(itemName, out var note))
                Pending[itemName] = note = new PendingNote();
            note.Quantity += quantity;

            var now = Time.unscaledTime;
            if (now - note.LastShown < Plugin.OverflowMessageCooldown.Value)
                return;

            var message = inventory.showMessage ?? inventory.levelUpMessage;
            message?.ShowMessage($"Inventory full - banked <color=yellow>{note.Quantity}</color> x <color=yellow>{itemName}</color>");
            note.Quantity = 0;
            note.LastShown = now;
        }
    }

    [HarmonyPatch(typeof(AudioManager), nameof(AudioManager.PlayBankClickSound))]
    internal static class BankClickSoundPatch
    {
        static bool Prefix() => !(InventoryAddItemPatch.Depositing && Plugin.OverflowMuteBankSound.Value);
    }
}
