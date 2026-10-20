using System;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace BloobsTweaks
{
    // The "Picked up: X 5 Value: 50" popup gains how many you now hold: "Bag 120 · Bank 3,400". The game rebuilds
    // the popup after the item has been added, so the totals already include what was just picked up.
    [HarmonyPatch(typeof(ItemPickup), "RebuildLootInfoText")]
    internal static class LootCounts
    {
        private static readonly AccessTools.FieldRef<ItemPickup, Dictionary<string, int>> Counts =
            AccessTools.FieldRefAccess<ItemPickup, Dictionary<string, int>>("lootCounts");
        private static readonly AccessTools.FieldRef<ItemPickup, Dictionary<string, int>> Values =
            AccessTools.FieldRefAccess<ItemPickup, Dictionary<string, int>>("lootValues");
        private static readonly AccessTools.FieldRef<ItemPickup, HashSet<string>> EggTracked =
            AccessTools.FieldRefAccess<ItemPickup, HashSet<string>>("eggTrackedLootNames");

        private static readonly StringBuilder Sb = new StringBuilder(512);

        static void Postfix(ItemPickup __instance)
        {
            if (!Plugin.LootShowTotals.Value || __instance.lootInfoText == null)
                return;

            var inventory = __instance.inventorySystem;
            var bank = __instance.bankSystem != null ? __instance.bankSystem : inventory != null ? inventory.bankSystem : null;
            var counts = Counts(__instance);
            if (inventory == null || counts == null)
                return;

            var values = Values(__instance);
            var eggs = EggTracked(__instance);
            Sb.Clear();
            foreach (var pair in counts)
            {
                var name = pair.Key;
                values.TryGetValue(name, out var value);
                Sb.Append(eggs != null && eggs.Contains(name) ? "EggTracted: " : "Picked up: ")
                  .Append(name).Append(" <color=yellow>").Append(pair.Value)
                  .Append("</color> Value: <color=yellow>").Append(value).Append("</color>");

                Sb.Append(Totals(inventory, bank, name)).AppendLine();
            }
            __instance.lootInfoText.text = Sb.ToString();
        }

        internal static string Totals(InventorySystem inventory, BankSystem bank, string name)
        {
            var inBag = inventory != null ? inventory.GetItemQuantity(name) : 0;
            var inBank = bank != null && bank.IsReady ? bank.GetItemQuantity(name) : 0;
            return $"  <color=#AAAAAA>(Bag <color=#FFFFFF>{inBag:N0}</color> · Bank <color=#FFFFFF>{inBank:N0}</color>)</color>";
        }
    }

    // Gathering skills, courses, chests and crops each keep their own "Picked up: X N Value: V" note, built by an
    // AddLootInfo (or DisplayLootInfo) of their own. After each update we rebuild that note: one line per item -
    // the game's own merge looks for "\nValue" but writes " Value", so repeats pile up as separate lines - each
    // with bag and bank totals.
    [HarmonyPatch]
    internal static class SkillLootCounts
    {
        private static readonly Regex Line = new Regex(
            @"^(Picked up|EggTracted): (.+?) <color=yellow>(\d+)</color> Value: <color=yellow>(\d+)</color>(?:\s+<color=#AAAAAA>\(Bag .*\)</color>)?\s*$",
            RegexOptions.Compiled);

        private static readonly Dictionary<Type, FieldInfo> TextFields = new Dictionary<Type, FieldInfo>();

        static IEnumerable<MethodBase> TargetMethods() =>
            typeof(InventorySystem).Assembly.GetTypes()
                .Where(t => typeof(MonoBehaviour).IsAssignableFrom(t) && t != typeof(ItemPickup))
                .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                .Where(m => (m.Name == "AddLootInfo" || m.Name == "DisplayLootInfo") && LootText(m.DeclaringType) != null);

        private static FieldInfo LootText(Type type)
        {
            if (!TextFields.TryGetValue(type, out var f))
            {
                f = type.GetField("lootInfoText", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                TextFields[type] = f = f != null && typeof(TMPro.TMP_Text).IsAssignableFrom(f.FieldType) ? f : null;
            }
            return f;
        }

        static void Postfix(MonoBehaviour __instance)
        {
            if (!Plugin.LootShowTotals.Value)
                return;
            try
            {
                if (!(LootText(__instance.GetType())?.GetValue(__instance) is TMPro.TMP_Text text) || string.IsNullOrEmpty(text.text))
                    return;
                var inventory = Scene.Find<InventorySystem>();
                var bank = inventory != null ? inventory.bankSystem : null;

                // Merge by item, keeping first-seen order; lines we don't recognise are kept as they are.
                var order = new List<string>();
                var totals = new Dictionary<string, (string Verb, long Qty, long Value)>();
                var other = new List<string>();
                foreach (var raw in text.text.Split('\n'))
                {
                    if (raw.Trim().Length == 0)
                        continue;
                    var m = Line.Match(raw);
                    if (!m.Success)
                    {
                        other.Add(raw);
                        continue;
                    }
                    var name = m.Groups[2].Value;
                    var qty = long.Parse(m.Groups[3].Value);
                    var value = long.Parse(m.Groups[4].Value);
                    if (totals.TryGetValue(name, out var t))
                        totals[name] = (t.Verb, t.Qty + qty, t.Value + value);
                    else
                    {
                        order.Add(name);
                        totals[name] = (m.Groups[1].Value, qty, value);
                    }
                }
                if (order.Count == 0)
                    return;

                var sb = new StringBuilder();
                foreach (var line in other)
                    sb.Append(line).Append('\n');
                foreach (var name in order)
                {
                    var t = totals[name];
                    sb.Append(t.Verb).Append(": ").Append(name).Append(" <color=yellow>").Append(t.Qty)
                      .Append("</color> Value: <color=yellow>").Append(t.Value).Append("</color>")
                      .Append(LootCounts.Totals(inventory, bank, name)).Append('\n');
                }
                text.text = sb.ToString();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Loot totals failed: {e.Message}");
            }
        }
    }
}
