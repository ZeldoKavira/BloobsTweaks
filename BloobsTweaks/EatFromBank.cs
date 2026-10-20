using System;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace BloobsTweaks
{
    // Auto-eat looks for food in the food slot, then the inventory (lowest-HP food first), and stops when both are
    // empty. When they are, we offer the lowest-HP food in the bank by the same rule, as a stand-in inventory entry.
    // The game's ConsumeFood heals from it and tries to remove it from the inventory (which finds nothing, so does
    // nothing); afterwards we take it from the bank instead.
    [HarmonyPatch]
    internal static class EatFromBank
    {
        // Stand-in entries we handed out, so only those are taken from the bank.
        private static readonly ConditionalWeakTable<InventorySystem.InventoryItem, object> FromBank =
            new ConditionalWeakTable<InventorySystem.InventoryItem, object>();

        [HarmonyPatch(typeof(HitPointsSkill), "FindLowestHpItem")]
        [HarmonyPostfix]
        static void OfferBankFood(HitPointsSkill __instance, ref object __result)
        {
            if (__result != null || !Plugin.EatFromBank.Value)
                return;
            try
            {
                var bank = __instance.inventorySystem != null ? __instance.inventorySystem.bankSystem : null;
                if (bank == null || !bank.IsReady)
                    return;

                BankSystem.BankItem pick = null;
                foreach (var b in bank.bankItems)
                    if (b != null && !string.IsNullOrEmpty(b.itemName) && b.quantity > 0 && b.hpvalue > 0 &&
                        (pick == null || b.hpvalue < pick.hpvalue))
                        pick = b;
                if (pick == null)
                    return;

                var standIn = new InventorySystem.InventoryItem
                {
                    itemName = pick.itemName,
                    quantity = pick.quantity,
                    hpvalue = pick.hpvalue,
                    value = pick.value,
                    commonImage = pick.commonImage,
                };
                FromBank.Add(standIn, null);
                __result = standIn;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Eat from bank failed: {e.Message}");
            }
        }

        [HarmonyPatch(typeof(HitPointsSkill), "ConsumeFood")]
        [HarmonyPostfix]
        static void TakeFromBank(HitPointsSkill __instance, object itemObj, int quantity)
        {
            if (!(itemObj is InventorySystem.InventoryItem item) || !FromBank.TryGetValue(item, out _))
                return;
            FromBank.Remove(item);
            var bank = __instance.inventorySystem != null ? __instance.inventorySystem.bankSystem : null;
            if (bank == null)
                return;
            InventoryAddItemPatch.Depositing = true; // no bank click sound
            try { bank.RemoveItem(item.itemName, quantity); }
            finally { InventoryAddItemPatch.Depositing = false; }
        }
    }
}
