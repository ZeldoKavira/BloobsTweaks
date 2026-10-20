using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace BloobsTweaks
{
    // "Keep 1" for the auto-sell / auto-bank list: click an entry's name in the list to toggle it. A marked item never
    // has its last one sold or banked out of the inventory. Each tick:
    //  - entries with one (or none) left are taken out of that tick's pick, so they don't use up a soul's turn;
    //  - the amount moved is capped at what's left over one.
    // Marks are kept in the mod's config (KeepOneItems).
    [HarmonyPatch]
    internal static class KeepOne
    {
        private const char Separator = '|';
        private static HashSet<string> _items;

        internal static HashSet<string> Items
        {
            get
            {
                if (_items == null)
                    _items = new HashSet<string>((Plugin.KeepOneItems.Value ?? "").Split(new[] { Separator }, StringSplitOptions.RemoveEmptyEntries));
                return _items;
            }
        }

        internal static void Toggle(string item)
        {
            if (!Items.Remove(item))
                Items.Add(item);
            Plugin.KeepOneItems.Value = string.Join(Separator.ToString(), Items.OrderBy(i => i));
        }

        // --- During each tick, set aside entries that are down to their last one -------------------------------

        private static readonly List<(AutoManageEntry Entry, AutoMode Mode)> SetAside = new List<(AutoManageEntry, AutoMode)>();

        private static void SetAsideLastOnes(SoulCompendiumManager manager)
        {
            SetAside.Clear();
            if (Items.Count == 0 || manager.inventorySystem == null)
                return;
            foreach (var entry in manager.autoEntries.Values)
                if (entry != null && entry.mode != AutoMode.None && Items.Contains(entry.itemName) &&
                    manager.inventorySystem.GetItemQuantity(entry.itemName) <= 1)
                {
                    SetAside.Add((entry, entry.mode));
                    entry.mode = AutoMode.None;
                }
        }

        private static void Restore()
        {
            foreach (var (entry, mode) in SetAside)
                if (entry != null)
                    entry.mode = mode;
            SetAside.Clear();
        }

        [HarmonyPatch(typeof(SoulCompendiumManager), "AutoSellItems")]
        [HarmonyPrefix]
        static void BeforeSell(SoulCompendiumManager __instance) => SetAsideLastOnes(__instance);

        [HarmonyPatch(typeof(SoulCompendiumManager), "AutoSellItems")]
        [HarmonyFinalizer]
        static void AfterSell() => Restore();

        [HarmonyPatch(typeof(SoulCompendiumManager), "AutoBankItems")]
        [HarmonyPrefix]
        static void BeforeBank(SoulCompendiumManager __instance) => SetAsideLastOnes(__instance);

        [HarmonyPatch(typeof(SoulCompendiumManager), "AutoBankItems")]
        [HarmonyFinalizer]
        static void AfterBank() => Restore();

        // --- Cap the amount moved so one stays behind ---------------------------------------------------------

        private static void Cap(InventorySystem inventory, List<(string itemName, int quantity)> batch)
        {
            if (batch == null || Items.Count == 0)
                return;
            for (var i = batch.Count - 1; i >= 0; i--)
            {
                var (name, quantity) = batch[i];
                if (!Items.Contains(name))
                    continue;
                var allowed = Math.Min(quantity, inventory.GetItemQuantity(name) - 1);
                if (allowed <= 0)
                    batch.RemoveAt(i);
                else
                    batch[i] = (name, allowed);
            }
        }

        [HarmonyPatch(typeof(InventorySystem), nameof(InventorySystem.SellItemsBatch))]
        [HarmonyPrefix]
        static void CapSell(InventorySystem __instance, List<(string itemName, int quantity)> itemsToSell) => Cap(__instance, itemsToSell);

        [HarmonyPatch(typeof(InventorySystem), nameof(InventorySystem.SendItemsToBankBatch))]
        [HarmonyPrefix]
        static void CapBank(InventorySystem __instance, List<(string itemName, int quantity)> itemsToBank) => Cap(__instance, itemsToBank);

        // --- New entries start as "keep 1" ----------------------------------------------------------------
        // Items the player adds go through AddItemToAutoManageInternal; loading the saved list uses a separate
        // method, so existing entries keep whatever they were set to.

        [HarmonyPatch(typeof(SoulCompendiumManager), "AddItemToAutoManageInternal")]
        [HarmonyPrefix]
        static void BeforeAdd(SoulCompendiumManager __instance, string itemName, out bool __state) =>
            __state = itemName != null && __instance.autoEntries.ContainsKey(itemName);

        [HarmonyPatch(typeof(SoulCompendiumManager), "AddItemToAutoManageInternal")]
        [HarmonyPostfix]
        static void AfterAdd(SoulCompendiumManager __instance, string itemName, bool __state)
        {
            if (__state || !Plugin.KeepOneByDefault.Value || itemName == null ||
                !__instance.autoEntries.TryGetValue(itemName, out var entry) || Items.Contains(itemName))
                return;
            Toggle(itemName);
            var text = entry != null ? NameText(entry) : null;
            text?.GetComponent<KeepOneClick>()?.Refresh();
        }

        // --- List UI: click the name to toggle ------------------------------------------------------------

        private static readonly AccessTools.FieldRef<AutoManageEntry, TMP_Text> NameText =
            AccessTools.FieldRefAccess<AutoManageEntry, TMP_Text>("nameText");

        [HarmonyPatch(typeof(AutoManageEntry), nameof(AutoManageEntry.InitializeUI))]
        [HarmonyPostfix]
        static void AddToggle(AutoManageEntry __instance)
        {
            var text = NameText(__instance);
            if (text == null)
                return;
            text.raycastTarget = true;
            var toggle = text.GetComponent<KeepOneClick>() ?? text.gameObject.AddComponent<KeepOneClick>();
            toggle.Entry = __instance;
            toggle.Text = text;
            toggle.Refresh();
        }

        internal class KeepOneClick : MonoBehaviour, IPointerClickHandler
        {
            public AutoManageEntry Entry;
            public TMP_Text Text;

            public void OnPointerClick(PointerEventData e)
            {
                if (Entry == null || e.button != PointerEventData.InputButton.Left)
                    return;
                Toggle(Entry.itemName);
                Refresh();
            }

            public void Refresh()
            {
                if (Entry == null || Text == null)
                    return;
                Text.richText = true;
                Text.text = Items.Contains(Entry.itemName)
                    ? $"{Entry.itemName} <color=yellow>(keep 1)</color>"
                    : Entry.itemName;
            }
        }
    }
}
