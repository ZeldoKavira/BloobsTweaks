using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Slot = EquipmentSystem.EquipmentSlot;
using CombatStyle = CombatManager.CombatStyle;

namespace BloobsTweaks
{
    // Equips the best gear you own, from inventory or bank:
    //  - Tools: when a gathering skill picks its tool (UseBest*InInventory), a better usable axe / pickaxe /
    //    rod / lens sitting in the bank is equipped first, so the game's own pick finds it. The game ranks
    //    tools by `progress` within your level; so do we.
    //  - Armour and weapon for the current combat style, on starting a fight and from the settings window.
    //    Stats are scored in "levels": each stat divided by what one level of its skill gives (accuracy per
    //    Attack level, damage per Strength level, reduction per Defence level, ...), so accuracy, damage and
    //    damage reduction are weighed on one scale without guessed weights.
    // Pieces it replaces go to the bank, so it works with a full inventory. Only the active equipment set is
    // touched, and the game's equipment lock and "never equip" list are respected.
    internal static class BestGear
    {
        private static readonly Slot[] GearSlots =
            { Slot.Weapon, Slot.Helm, Slot.Body, Slot.Boots, Slot.Gloves, Slot.Cape, Slot.Ring, Slot.Amulet, Slot.Shield };

        private static readonly Func<InventoryUI, string, bool> IsEquipment =
            AccessTools.MethodDelegate<Func<InventoryUI, string, bool>>(AccessTools.Method(typeof(InventoryUI), "IsEquipment"));

        private static float _lastAutoGear = -999f;

        // ---------------------------------------------------------------------------------------------------
        // Item data shared by InventoryItem, BankItem and EquipmentItem (same field names on all three).

        private enum Source { Equipped, Inventory, Bank }

        private class Piece
        {
            public Source From;
            public object Data;
            public string Name => Get<string>("itemName");
            public T Get<T>(string field) => Fields.Read<T>(Data, field);
        }

        private static class Fields
        {
            private static readonly Dictionary<(Type, string), FieldInfo> Cache = new Dictionary<(Type, string), FieldInfo>();

            public static T Read<T>(object o, string name)
            {
                if (o == null)
                    return default;
                var key = (o.GetType(), name);
                if (!Cache.TryGetValue(key, out var f))
                    Cache[key] = f = o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public);
                return f != null && f.GetValue(o) is T v ? v : default;
            }
        }

        private static IEnumerable<Piece> Owned(InventorySystem inventory, BankSystem bank, EquipmentSystem equipment)
        {
            foreach (var i in inventory.inventoryItems)
                if (!inventory.IsPlaceholder(i))
                    yield return new Piece { From = Source.Inventory, Data = i };
            if (bank != null && bank.IsReady)
                foreach (var b in bank.bankItems)
                    if (b != null && !string.IsNullOrEmpty(b.itemName) && b.quantity > 0)
                        yield return new Piece { From = Source.Bank, Data = b };
        }

        private static bool Qualified(Piece p)
        {
            var d = playerDataManager.Instance;
            return d == null ||
                   p.Get<int>("requiredAttackLevel") <= d.AttackLevel && p.Get<int>("requiredMagicLevel") <= d.MagicLevel &&
                   p.Get<int>("requiredRangeLevel") <= d.RangeLevel && p.Get<int>("requiredDefenceLevel") <= d.DefenceLevel &&
                   p.Get<int>("requiredWoodcuttingLevel") <= d.WoodcuttingLevel && p.Get<int>("requiredFishingLevel") <= d.FishingLevel &&
                   p.Get<int>("requiredMiningLevel") <= d.MiningLevel && p.Get<int>("requiredForagingLevel") <= d.ForagingLevel;
        }

        private static bool TwoHanded(string name)
        {
            var n = name.ToLowerInvariant();
            return n.Contains("bow") || n.Contains("staff");
        }

        // ---------------------------------------------------------------------------------------------------
        // Equipping

        private static Dictionary<Slot, EquipmentSystem.EquipmentItem> SlotsFor(EquipmentSystem es, Slot slot) =>
            EquipmentSystem.SetSlots.Contains(slot) ? es.ActiveSet : es.globalEquipment;

        // Takes the piece from wherever it is and equips it. Anything it displaces goes to the bank first, so
        // the game's own swap never needs a free inventory slot.
        private static bool Equip(Piece piece, Slot slot, EquipmentSystem es, InventorySystem inventory, BankSystem bank)
        {
            if (es.IsSlotLocked() || bank == null || !bank.IsReady)
                return false;

            var displaced = new List<(Slot, EquipmentSystem.EquipmentItem)>();
            if (SlotsFor(es, slot).TryGetValue(slot, out var current))
                displaced.Add((slot, current));
            if (slot == Slot.Weapon && TwoHanded(piece.Name) && es.ActiveSet.TryGetValue(Slot.Shield, out var shield))
                displaced.Add((Slot.Shield, shield));
            if (displaced.Any(d => !bank.CanAddItem(d.Item2.itemName, 1)))
                return false;

            foreach (var (s, item) in displaced)
            {
                var inSet = EquipmentSystem.SetSlots.Contains(s);
                SlotsFor(es, s).Remove(s);
                if (inSet)
                    playerDataManager.Instance.GetSavedEquipmentSet(es.activeSetIndex).Remove(item);
                else
                    playerDataManager.Instance.GetSavedGlobalEquipment().Remove(item);
                es.equipmentUI.ClearItemImage(s);
                Quiet(() => bank.AddItem(item.itemName, 1, item.commonImage, item.value, item.hpvalue, item.requiredWoodcuttingLevel,
                    item.requiredMiningLevel, item.requiredFishingLevel, item.requiredForagingLevel, item.requiredAttackLevel,
                    item.requiredMagicLevel, item.requiredRangeLevel, item.requiredDefenceLevel, item.requiredHomesteadingLevel,
                    item.accuracy, item.damage, item.damageReduction, item.progress, item.rangeDamage, item.magicDamage,
                    item.rangeAccuracy, item.magicAccuracy, item.fishingBonusXp, item.foragingBonusXp, item.miningBonusXp,
                    item.woodcuttingBonusXp, item.trackingBonusXp, item.runSpeed, allowAutoSort: false));
            }

            var p = piece;
            var name = p.Name;
            if (p.From == Source.Inventory)
                inventory.RemoveItem(name, 1);
            else if (p.From == Source.Bank)
                Quiet(() => bank.RemoveItem(name, 1));

            var equipped = es.AddItem(name, 1, p.Get<Sprite>("commonImage"), p.Get<int>("value"), p.Get<int>("hpvalue"),
                p.Get<int>("requiredWoodcuttingLevel"), p.Get<int>("requiredMiningLevel"), p.Get<int>("requiredFishingLevel"),
                p.Get<int>("requiredForagingLevel"), p.Get<int>("requiredAttackLevel"), p.Get<int>("requiredMagicLevel"),
                p.Get<int>("requiredRangeLevel"), p.Get<int>("requiredDefenceLevel"), p.Get<int>("requiredHomesteadingLevel"),
                p.Get<float>("accuracy"), p.Get<int>("damage"), p.Get<float>("damageReduction"), p.Get<int>("progress"),
                p.Get<int>("rangeDamage"), p.Get<int>("magicDamage"), p.Get<float>("rangeAccuracy"), p.Get<float>("magicAccuracy"),
                p.Get<float>("fishingBonusXp"), p.Get<float>("foragingBonusXp"), p.Get<float>("miningBonusXp"),
                p.Get<float>("woodcuttingBonusXp"), p.Get<float>("trackingBonusXp"), p.Get<float>("runSpeed"));
            if (equipped != null)
                return true;

            Plugin.Log.LogWarning($"Equipping {name} failed; it stays in the bank");
            return false;
        }

        private static void Quiet(Action bankAction)
        {
            InventoryAddItemPatch.Depositing = true;
            try { bankAction(); }
            finally { InventoryAddItemPatch.Depositing = false; }
        }

        private static (InventorySystem, BankSystem, EquipmentSystem, InventoryUI) Systems()
        {
            var ui = Scene.Find<InventoryUI>();
            var inventory = ui != null ? ui.inventorySystem : null;
            var es = ui != null ? ui.equipmentSystem : null;
            return (inventory, inventory != null ? inventory.bankSystem : null, es, ui);
        }

        // ---------------------------------------------------------------------------------------------------
        // Tools

        private static void BestToolFromBank(Slot slot, Func<playerDataManager, int> level, string levelField)
        {
            if (!Plugin.BestToolsFromBank.Value)
                return;
            try
            {
                var (inventory, bank, es, _) = Systems();
                if (inventory == null || bank == null || !bank.IsReady || es == null || es.IsSlotLocked() || playerDataManager.Instance == null)
                    return;
                var have = level(playerDataManager.Instance);
                // Rank exactly as the game's UseBest* does: by the item asset of that name, not the copy stored in
                // the inventory/bank, whose progress and level fields can be 0 (e.g. for shop-bought tools).
                var assets = new Dictionary<string, Item>();
                foreach (var a in Resources.FindObjectsOfTypeAll<Item>())
                    if (a != null && !assets.ContainsKey(a.name))
                        assets[a.name] = a; // the game takes the first asset with the name, too
                Item Asset(Piece p) => p.Name != null && assets.TryGetValue(p.Name, out var a) ? a : null;
                int Progress(Piece p) => Asset(p) is Item a ? a.progress : p.Get<int>("progress");
                bool Usable(Piece p)
                {
                    if (EquipmentSystem.GetEquipmentSlotForItemStatic(p.Name) != slot)
                        return false;
                    var a = Asset(p);
                    var need = a != null ? Fields.Read<int>(a, levelField) : p.Get<int>(levelField);
                    return need <= have;
                }

                var best = Owned(inventory, null, es).Where(Usable).Select(Progress).DefaultIfEmpty(0).Max();
                if (es.globalEquipment.TryGetValue(slot, out var worn) && worn != null)
                {
                    var wornPiece = new Piece { From = Source.Equipped, Data = worn };
                    if (Usable(wornPiece))
                        best = Math.Max(best, Progress(wornPiece));
                }

                var fromBank = Owned(inventory, bank, es).Where(p => p.From == Source.Bank && Usable(p))
                    .OrderByDescending(Progress).FirstOrDefault();
                if (fromBank == null || Progress(fromBank) <= best)
                    return;
                var name = fromBank.Name;
                if (Equip(fromBank, slot, es, inventory, bank))
                    Message($"Equipped <color=yellow>{name}</color> from the bank.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Best tool from bank failed: {e.Message}");
            }
        }

        [HarmonyPatch(typeof(WoodcuttingSkill), nameof(WoodcuttingSkill.UseBestAxeInInventory))]
        private static class AxePatch { static void Prefix() => BestToolFromBank(Slot.Axe, d => d.WoodcuttingLevel, "requiredWoodcuttingLevel"); }

        [HarmonyPatch(typeof(MiningSkill), nameof(MiningSkill.UseBestPickaxeInInventory))]
        private static class PickaxePatch { static void Prefix() => BestToolFromBank(Slot.Pickaxe, d => d.MiningLevel, "requiredMiningLevel"); }

        [HarmonyPatch(typeof(FishingSkill), nameof(FishingSkill.UseBestRodInInventory))]
        private static class RodPatch { static void Prefix() => BestToolFromBank(Slot.Rod, d => d.FishingLevel, "requiredFishingLevel"); }

        [HarmonyPatch(typeof(ForagingSkill), nameof(ForagingSkill.UseBestLensInInventory))]
        private static class LensPatch { static void Prefix() => BestToolFromBank(Slot.Lens, d => d.ForagingLevel, "requiredForagingLevel"); }

        // ---------------------------------------------------------------------------------------------------
        // Combat gear

        private class Weights
        {
            public float Accuracy, Damage, Reduction;
            public string AccuracyField, DamageField;
        }

        private static float PerLevel<T>(string field, float fallback) where T : Component
        {
            var c = Scene.Find<T>();
            var v = c != null ? Fields.Read<float>(c, field) : 0f;
            return v > 0f ? v : fallback;
        }

        private static Weights WeightsFor(CombatStyle style)
        {
            var w = new Weights { Reduction = 1f / PerLevel<DefenceSkill>("DamageReductionIncreaseOnLevelUp", 0.05f) };
            switch (style)
            {
                case CombatStyle.Ranged:
                    w.AccuracyField = "rangeAccuracy";
                    w.DamageField = "rangeDamage";
                    w.Accuracy = 1f / PerLevel<RangeSkill>("accuracyIncreaseOnLevelUp", 0.01f);
                    w.Damage = 1f / PerLevel<RangeSkill>("rangePowerIncreaseOnLevelUp", 1f);
                    break;
                case CombatStyle.Magic:
                    w.AccuracyField = "magicAccuracy";
                    w.DamageField = "magicDamage";
                    w.Accuracy = 1f / PerLevel<MagicSkill>("accuracyIncreaseOnLevelUp", 0.01f);
                    w.Damage = 1f / PerLevel<MagicSkill>("magicPowerIncreaseOnLevelUp", 1f);
                    break;
                default:
                    w.AccuracyField = "accuracy";
                    w.DamageField = "damage";
                    w.Accuracy = 1f / PerLevel<AttackSkill>("accuracyIncreaseOnLevelUp", 0.01f);
                    w.Damage = 1f / PerLevel<StrengthSkill>("damageIncreaseOnLevelUp", 1f);
                    break;
            }
            return w;
        }

        private static float Score(Piece p, Weights w) =>
            p.Get<float>(w.AccuracyField) * w.Accuracy + p.Get<int>(w.DamageField) * w.Damage + p.Get<float>("damageReduction") * w.Reduction;

        private static bool WeaponFits(string name, CombatStyle style)
        {
            var n = name.ToLowerInvariant();
            switch (style)
            {
                case CombatStyle.Ranged: return n.Contains("bow");
                case CombatStyle.Magic: return n.Contains("staff");
                default: return !n.Contains("bow") && !n.Contains("staff");
            }
        }

        // Returns how many pieces were changed.
        internal static int EquipBestForCombat(bool announce)
        {
            var (inventory, bank, es, ui) = Systems();
            if (inventory == null || es == null || ui == null || CombatManager.Instance == null)
                return 0;
            if (es.IsSlotLocked())
            {
                if (announce)
                    Message("Equipment is locked - unlock it to auto-equip.");
                return 0;
            }

            var style = CombatManager.Instance.GetPlayerCombatStyle();
            if (style == CombatStyle.None)
                style = CombatStyle.Melee;
            var w = WeightsFor(style);
            var changed = new List<string>();

            foreach (var slot in GearSlots)
            {
                // No shield with a bow or staff.
                if (slot == Slot.Shield && es.ActiveSet.TryGetValue(Slot.Weapon, out var weapon) && TwoHanded(weapon.itemName))
                    continue;

                es.ActiveSet.TryGetValue(slot, out var worn);
                var wornPiece = worn != null ? new Piece { From = Source.Equipped, Data = worn } : null;
                var wornScore = wornPiece != null && (slot != Slot.Weapon || WeaponFits(worn.itemName, style)) ? Score(wornPiece, w) : float.NegativeInfinity;

                Piece best = null;
                var bestScore = wornScore;
                foreach (var p in Owned(inventory, bank, es))
                {
                    var name = p.Name;
                    if (EquipmentSystem.GetEquipmentSlotForItemStatic(name) != slot || !IsEquipment(ui, name) || !Qualified(p))
                        continue;
                    if (slot == Slot.Weapon && !WeaponFits(name, style))
                        continue;
                    var s = Score(p, w);
                    if (s > bestScore + 0.0001f && s > 0f)
                    {
                        best = p;
                        bestScore = s;
                    }
                }

                if (best != null)
                {
                    var name = best.Name;
                    if (Equip(best, slot, es, inventory, bank))
                        changed.Add(name);
                }
            }

            if (changed.Count > 0)
                Message($"Equipped best {style.ToString().ToLowerInvariant()} gear: <color=yellow>{string.Join(", ", changed)}</color>");
            else if (announce)
                Message($"Already wearing the best {style.ToString().ToLowerInvariant()} gear you own.");
            return changed.Count;
        }

        // Starting a fight (button, click or auto-navigation) - at most every few seconds, since auto combat
        // calls this for every new target.
        [HarmonyPatch(typeof(CombatManager), nameof(CombatManager.InitiateAutoCombat))]
        private static class CombatStartPatch
        {
            static void Prefix()
            {
                if (!Plugin.BestGearOnCombat.Value || Time.unscaledTime - _lastAutoGear < 5f)
                    return;
                _lastAutoGear = Time.unscaledTime;
                try
                {
                    EquipBestForCombat(false);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Auto-equip failed: {e.Message}");
                }
            }
        }

        // ---------------------------------------------------------------------------------------------------
        // Run speed: boots are the only gear that adds it, and it applies to all walking, not just courses.

        private static string _lastSpeedBoots;

        // Puts on the usable boots with the most run speed (inventory or bank). Outside combat only - in a fight
        // the combat pick decides the boots. Quiet when it's just putting the same speed boots back after a fight.
        internal static bool EquipFastestBoots(bool announce)
        {
            if (!Plugin.FastestBoots.Value)
                return false;
            var (inventory, bank, es, ui) = Systems();
            if (inventory == null || es == null || ui == null || es.IsSlotLocked())
                return false;
            if (CombatManager.Instance != null && CombatManager.Instance.IsAutoCombatActive())
                return false;

            es.ActiveSet.TryGetValue(Slot.Boots, out var worn);
            var best = worn != null ? worn.runSpeed : 0f;
            Piece pick = null;
            foreach (var p in Owned(inventory, bank, es))
            {
                var name = p.Name;
                if (EquipmentSystem.GetEquipmentSlotForItemStatic(name) != Slot.Boots || !IsEquipment(ui, name) || !Qualified(p))
                    continue;
                var speed = p.Get<float>("runSpeed");
                if (speed > best + 0.0001f)
                {
                    best = speed;
                    pick = p;
                }
            }
            if (pick == null)
                return false;

            var chosen = pick.Name;
            if (!Equip(pick, Slot.Boots, es, inventory, bank))
                return false;
            Scene.Find<DexteritySkill>()?.UpdateRunSpeed();
            if (announce || chosen != _lastSpeedBoots)
                Message($"Equipped faster boots: <color=yellow>{chosen}</color> (+{best:0.##} run speed)");
            _lastSpeedBoots = chosen;
            return true;
        }

        // Starting a dexterity course checks straight away rather than waiting for the periodic check.
        [HarmonyPatch]
        private static class CourseStartPatch
        {
            static IEnumerable<MethodBase> TargetMethods() =>
                typeof(InventorySystem).Assembly.GetTypes()
                    .Where(t => typeof(MonoBehaviour).IsAssignableFrom(t) && t.Name.EndsWith("Course"))
                    .Select(t => t.GetMethod("StartCourse", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    .Where(m => m != null);

            static void Prefix()
            {
                try { EquipFastestBoots(false); }
                catch (Exception e) { Plugin.Log.LogWarning($"Fastest boots failed: {e.Message}"); }
            }
        }

        private static void Message(string text) =>
            Scene.Messages?.ShowMessage(text);
    }
}

namespace BloobsTweaks
{
    // Every few seconds outside combat, swaps in faster boots if you own any (they speed up all walking).
    internal class FastestBootsWatcher : MonoBehaviour
    {
        private float _next;

        private void Update()
        {
            if (Time.unscaledTime < _next)
                return;
            _next = Time.unscaledTime + 10f;
            if (!Plugin.FastestBoots.Value || Scene.Find<InventoryUI>() == null)
                return;
            try { BestGear.EquipFastestBoots(false); }
            catch (Exception e) { Plugin.Log.LogWarning($"Fastest boots check failed: {e.Message}"); }
        }
    }
}
