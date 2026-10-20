using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;

namespace BloobsTweaks
{
    // Finds where items come from and what they're spent on outside the skill windows: enemy and resource
    // drops, event drops, quest rewards, store packs, homestead houses and crops, world buildings, spells,
    // teleports, cosmetics, tracking bait and so on. Rather than special-casing each system, it walks the
    // game's data assets and scene objects, finds every reference to an item, and classifies it by the name
    // of the field holding it - the game names these consistently (possibleDrops, cropToGive, rewardItem,
    // costs, seedRequired, quantityRequired, bait, ...).
    internal static class ItemSources
    {
        internal struct Ref
        {
            public string Item;
            public bool IsCost;
            public string Category;
            public string Detail;
        }

        private const int MaxDepth = 3;

        private static readonly FieldInfo[] NoFields = new FieldInfo[0];
        private static readonly HashSet<string> LabelByField = new HashSet<string> { nameof(BMStoreUpdate), nameof(AltarUIOmni) };

        // "bonePackTier0" -> "bone pack tier 0"
        private static string Humanize(string fieldName) =>
            Regex.Replace(fieldName, "(?<=[a-z])(?=[A-Z0-9])", " ").ToLowerInvariant();

        // Checked first, so e.g. "seedRequired" is a cost even though "seedDrops" is a source.
        private static readonly string[] CostWords =
        {
            "cost", "price", "required", "require", "bait", "trap", "fuel", "payment", "consum", "ingredient", "lockpick", "logitem",
        };
        private static readonly string[] SourceWords =
        {
            "drop", "give", "reward", "loot", "added", "toadd", "pack", "pool", "yield", "produce", "prize", "types", "sale",
            "fishdata", "treedata", "oredata", "fishitem", "gem", "coal", "ashitem",
        };
        // Live state, UI-only lists, and things the skill windows already cover.
        private static readonly string[] SkipWords =
        {
            "recipe", "selected", "current", "active", "cached", "last", "inventory", "bank", "saved", "equipped", "preview",
            "container", "snowball", "collected",
        };
        private static readonly HashSet<string> CostSiblings = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "quantityRequired", "requiredAmount", "cost", "price" };

        // For a field no keyword covers: true = cost, false = source. Fishing spots name their bait fields
        // after the bait (wormItemName, herringItemName, ...); colour choices are small cosmetic price tags.
        private static readonly Dictionary<string, bool> TypeDefaults = new Dictionary<string, bool>
        {
            { nameof(FishingSpots), true }, { nameof(ColorChoice), true },
        };

        private static readonly HashSet<string> SkipTypes = new HashSet<string>
        {
            nameof(InventorySystem), nameof(InventoryUI), nameof(BankSystem), nameof(BankUI), nameof(EquipmentSystem),
            nameof(EquipmentUI), nameof(playerDataManager), nameof(ItemCompendiumManager), nameof(ChallengeManager),
            nameof(ChallengeUIManager), nameof(ChallengeData), nameof(ItemCompendiumEntryUI), nameof(AutoManageEntry),
            nameof(PetManager), nameof(SoulCompendiumManager),
        };

        private static readonly Dictionary<string, string> Categories = new Dictionary<string, string>
        {
            { nameof(BasicEnemy), "Enemy" }, { nameof(BasicNpc), "NPC" }, { nameof(Trees), "Tree" }, { nameof(Ores), "Rock" },
            { nameof(Bushes), "Bush" }, { nameof(FishingSpots), "Fishing spot" }, { nameof(Chests), "Thieving" },
            { nameof(TrapsAndBeasts), "Tracking" }, { nameof(TrapsThrow), "Tracking" }, { nameof(AttractMoth), "Tracking" },
            { nameof(BloobbirdTree), "Tracking" }, { nameof(CropData), "Homesteading" }, { nameof(CropGrowth), "Homesteading" },
            { nameof(HouseData), "Homestead house" }, { nameof(WorldBuildableData), "Building" }, { nameof(SpellData), "Spell" },
            { nameof(TeleportPointSO), "Teleport" }, { nameof(QuestData), "Quest" }, { nameof(LootChest), "Loot chest" },
            { nameof(PresentLootTable), "Present" }, { nameof(BMStoreUpdate), "Beast Mastery store" },
            { nameof(TotalLevelUI), "Event drop" }, { nameof(AltarUIOmni), "Imbuing altar" }, { nameof(BloobColourChange), "Cosmetic" },
            { nameof(RanchCaptureController), "Ranch" }, { nameof(FishingSwarmData), "Fishing swarm" },
            { nameof(AddItemsToInventory), "Gift" }, { nameof(ColorChoice), "Cosmetic" }, { nameof(ShopUI), "Shop" },
        };

        private static readonly Dictionary<Type, FieldInfo[]> FieldCache = new Dictionary<Type, FieldInfo[]>();
        private static readonly Dictionary<Type, bool> RelevantCache = new Dictionary<Type, bool>();

        internal static List<Ref> Scan(HashSet<string> itemNames, List<string> unclassified)
        {
            var refs = new List<Ref>();
            var assembly = typeof(InventorySystem).Assembly;

            foreach (var obj in Resources.FindObjectsOfTypeAll<ScriptableObject>())
            {
                var type = obj.GetType();
                if (type.Assembly != assembly || obj is Item || SkipTypes.Contains(type.Name) || !Relevant(type))
                    continue;
                // Ranch eggs are CropData too, but come from enemies rather than seeds.
                var category = obj is CropData crop && (crop.isBoss || crop.isEvent || crop.isTrackingEnemy || !string.IsNullOrEmpty(crop.sourceEnemyName))
                    ? "Ranch egg" : Category(type);
                Walk(obj, type, OwnerLabel(obj), category, "", 0, itemNames, refs, unclassified);
            }

            foreach (var mb in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
            {
                if (mb == null || !mb.gameObject.scene.IsValid())
                    continue;
                var type = mb.GetType();
                if (type.Assembly != assembly || SkipTypes.Contains(type.Name) || type.Name.StartsWith("RecipeInfo") || !Relevant(type))
                    continue;
                var category = type.Name.EndsWith("Skill") ? type.Name.Substring(0, type.Name.Length - 5) : Category(type);
                var label = type == typeof(TotalLevelUI) ? "any XP gain (seasonal)" : OwnerLabel(mb);
                Walk(mb, type, label, category, "", 0, itemNames, refs, unclassified);
            }
            return refs;
        }

        private static void Walk(object obj, Type type, string label, string category, string path, int depth,
            HashSet<string> itemNames, List<Ref> refs, List<string> unclassified)
        {
            var fields = Fields(type);
            // Neighbouring fields (quantityRequired, amount, ...) only describe an item inside a small data entry
            // such as an ItemCost or a cosmetic choice; on a whole component they'd be unrelated.
            var siblings = depth > 0 ? fields : NoFields;
            foreach (var field in fields)
            {
                // Stores keep each pack in its own list, so the field name is the best label for what's inside.
                var entryLabel = depth == 0 && (LabelByField.Contains(type.Name) || type.Name.EndsWith("Skill")) ? Humanize(field.Name) : label;
                object value;
                try { value = field.GetValue(obj); }
                catch { continue; }
                if (value == null)
                    continue;

                var fieldPath = path.Length == 0 ? field.Name : path + "." + field.Name;
                var ft = field.FieldType;

                if (value is Item item)
                {
                    if (item != null)
                        Record(item.itemName, item, fieldPath, siblings, obj, entryLabel, category, refs, unclassified);
                }
                else if (ft == typeof(string))
                {
                    var s = (string)value;
                    if (field.Name.IndexOf("item", StringComparison.OrdinalIgnoreCase) >= 0 && itemNames.Contains(s))
                        Record(s, null, fieldPath, siblings, obj, entryLabel, category, refs, unclassified);
                }
                else if (value is IList list && ElementType(ft) is Type et)
                {
                    if (typeof(Item).IsAssignableFrom(et))
                    {
                        foreach (var e in list)
                            if (e is Item i && i != null)
                                Record(i.itemName, i, fieldPath, siblings, obj, entryLabel, category, refs, unclassified);
                    }
                    else if (depth < MaxDepth && IsPlainData(et))
                    {
                        var n = 0;
                        foreach (var e in list)
                        {
                            if (e == null || ++n > 500)
                                continue;
                            Walk(e, e.GetType(), ElementLabel(e) ?? entryLabel, category, fieldPath, depth + 1, itemNames, refs, unclassified);
                        }
                    }
                }
                else if (depth < MaxDepth && IsPlainData(ft))
                {
                    Walk(value, value.GetType(), ElementLabel(value) ?? entryLabel, category, fieldPath, depth + 1, itemNames, refs, unclassified);
                }
            }
        }

        private static void Record(string itemName, Item asset, string path, FieldInfo[] siblings, object owner, string label,
            string category, List<Ref> refs, List<string> unclassified)
        {
            if (string.IsNullOrEmpty(itemName))
                return;
            var lower = path.ToLowerInvariant();
            if (SkipWords.Any(lower.Contains))
                return;

            bool isCost;
            if (CostWords.Any(lower.Contains) || siblings.Any(f => CostSiblings.Contains(f.Name)))
                isCost = true;
            else if (SourceWords.Any(lower.Contains))
                isCost = false;
            else if (TypeDefaults.TryGetValue(owner.GetType().Name, out var byType))
                isCost = byType;
            else
            {
                unclassified?.Add($"{category} / {label}: {path} -> {itemName}");
                return;
            }

            string detail;
            if (isCost)
            {
                var qty = Quantity(owner, siblings);
                detail = qty > 1 ? $"{label} ({qty})" : label;
            }
            else if (asset != null && asset.maxDropQuantity > 0 && asset.dropChance > 0f && asset.dropChance <= 1f)
            {
                var amount = asset.minDropQuantity == asset.maxDropQuantity ? $"{asset.maxDropQuantity}" : $"{asset.minDropQuantity}-{asset.maxDropQuantity}";
                detail = $"{label} ({amount}, {Chance(asset.dropChance)})";
            }
            else
                detail = label;

            refs.Add(new Ref { Item = itemName, IsCost = isCost, Category = category, Detail = detail });
        }

        private static string Chance(float p)
        {
            if (p >= 1f) return "always";
            if (p >= 0.01f) return $"{p * 100f:0.#}%";
            return $"1 in {Mathf.RoundToInt(1f / p):N0}";
        }

        private static int Quantity(object owner, FieldInfo[] fields)
        {
            foreach (var name in new[] { "quantityRequired", "requiredAmount", "quantity", "amount" })
            {
                var f = fields.FirstOrDefault(x => x.Name == name);
                if (f != null && f.GetValue(owner) is int i)
                    return i;
            }
            return 0;
        }

        // Only walk types that can hold item references somewhere, so the scene scan stays cheap.
        private static bool Relevant(Type type, int depth = 0)
        {
            if (RelevantCache.TryGetValue(type, out var known))
                return known;
            RelevantCache[type] = false; // guard against cycles
            var result = Fields(type).Any(f =>
            {
                var ft = f.FieldType;
                var et = ElementType(ft) ?? ft;
                if (typeof(Item).IsAssignableFrom(et))
                    return true;
                if (ft == typeof(string) && f.Name.IndexOf("item", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
                return depth < MaxDepth && IsPlainData(et) && Relevant(et, depth + 1);
            });
            RelevantCache[type] = result;
            return result;
        }

        private static FieldInfo[] Fields(Type type)
        {
            if (!FieldCache.TryGetValue(type, out var fields))
            {
                var list = new List<FieldInfo>();
                for (var t = type; t != null && t != typeof(MonoBehaviour) && t != typeof(ScriptableObject) && t != typeof(object); t = t.BaseType)
                    list.AddRange(t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                        .Where(f => !f.IsNotSerialized && (f.IsPublic || f.GetCustomAttribute<SerializeField>() != null)));
                FieldCache[type] = fields = list.ToArray();
            }
            return fields;
        }

        private static Type ElementType(Type t) =>
            t.IsArray ? t.GetElementType() : t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>) ? t.GetGenericArguments()[0] : null;

        // Serializable classes/structs from the game itself, not Unity objects (those are scanned on their own).
        private static bool IsPlainData(Type t) =>
            t != null && !t.IsPrimitive && !t.IsEnum && t != typeof(string) && !typeof(UnityEngine.Object).IsAssignableFrom(t) &&
            t.Assembly == typeof(InventorySystem).Assembly;

        private static string Category(Type type)
        {
            if (Categories.TryGetValue(type.Name, out var c))
                return c;
            if (type.Name.EndsWith("Course"))
                return "Dexterity course";
            return Regex.Replace(type.Name, "(?<=[a-z])(?=[A-Z])", " ");
        }

        private static readonly string[] NameFields = { "displayName", "houseName", "spellName", "questTitle", "cropName", "enemyName" };

        private static string OwnerLabel(UnityEngine.Object obj)
        {
            foreach (var name in NameFields)
                if (obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(obj) is string s && !string.IsNullOrWhiteSpace(s))
                    return s;
            return Clean(obj.name);
        }

        // Elements like cosmetic choices are best named by their sprite ("Pumpkin Hat").
        private static string ElementLabel(object element)
        {
            foreach (var f in Fields(element.GetType()))
                if (f.FieldType == typeof(Sprite) && f.GetValue(element) is Sprite sprite && sprite != null)
                    return Clean(sprite.name);
            return null;
        }

        // "Oak Tree (3)" / "Goblin(Clone)" / "Chicken_2" -> "Oak Tree" / "Goblin" / "Chicken"
        private static string Clean(string name) =>
            Regex.Replace(name ?? "", @"(\s*\(\d+\)|\(Clone\)|_\d+)+$", "").Trim();
    }
}
