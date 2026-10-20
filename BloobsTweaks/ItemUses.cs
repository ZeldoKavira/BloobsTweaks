using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace BloobsTweaks
{
    // Works out what an item is for from the game's own skill guide windows (CookingSkillUI, SmithingSkillUI,
    // ...). Each window's tabs hold RecipeInfo* components listing the recipes or gatherables shown on that
    // tab, which tells us the skill and the exact tab, as well as the ingredients and products.
    internal static class ItemUses
    {
        // The last two come from ItemSources and have no skill window to open.
        internal enum Role { UsedIn, MadeIn, GatheredIn, ObtainedFrom, SpentOn }

        internal class Use
        {
            public Role Role;
            public string Skill;
            public MonoBehaviour Window;
            public int Tab;
            public string Detail;
            public int Level;
        }

        private static Dictionary<string, List<Use>> _index;
        private static List<MonoBehaviour> _windows = new List<MonoBehaviour>();
        private static string _lastOpenedItem;
        private static int _lastOpenedIndex;
        private static bool _loggedHierarchy;

        private static readonly Dictionary<Type, FieldInfo> PanelsField = new Dictionary<Type, FieldInfo>();
        private static readonly Dictionary<Type, FieldInfo> MainPanelField = new Dictionary<Type, FieldInfo>();

        internal static bool IsBuilt => _index != null && _windows.Count > 0 && _windows.All(w => w != null);

        internal static void Warm() => EnsureIndex();

        internal static List<Use> Get(string itemName)
        {
            if (string.IsNullOrEmpty(itemName))
                return null;
            EnsureIndex();
            return _index != null && _index.TryGetValue(itemName, out var uses) ? uses : null;
        }

        // The windows live in the game scene, so the index goes stale when it reloads (e.g. back to menu).
        private static void EnsureIndex()
        {
            if (IsBuilt)
                return;
            try
            {
                Build();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Building the item-use index failed: {e}");
                _index = new Dictionary<string, List<Use>>();
            }
        }

        private static IEnumerable<Type> WindowTypes() =>
            typeof(InventorySystem).Assembly.GetTypes().Where(t =>
                typeof(MonoBehaviour).IsAssignableFrom(t) && t.Name.EndsWith("SkillUI") && t.Name != nameof(HowToPlaySkillUI) &&
                AccessTools.Field(t, "uiPanels")?.FieldType == typeof(GameObject[]) &&
                AccessTools.Field(t, "mainPanel")?.FieldType == typeof(GameObject) &&
                AccessTools.Method(t, "OpenPanel", new[] { typeof(int) }) != null &&
                AccessTools.Method(t, "OpenMainPanel", Type.EmptyTypes) != null);

        private static void Build()
        {
            _index = new Dictionary<string, List<Use>>(StringComparer.OrdinalIgnoreCase);
            _windows = new List<MonoBehaviour>();

            foreach (var type in WindowTypes())
            {
                PanelsField[type] = AccessTools.Field(type, "uiPanels");
                MainPanelField[type] = AccessTools.Field(type, "mainPanel");
                var skill = SkillName(type);

                // FindObjectsOfTypeAll also returns inactive objects; keep the ones that belong to a scene.
                foreach (var window in Resources.FindObjectsOfTypeAll(type).OfType<MonoBehaviour>().Where(w => w.gameObject.scene.IsValid()))
                {
                    _windows.Add(window);
                    var tabs = (GameObject[])PanelsField[type].GetValue(window);
                    if (tabs == null)
                        continue;
                    for (var tab = 0; tab < tabs.Length; tab++)
                    {
                        if (tabs[tab] == null)
                            continue;
                        foreach (var info in tabs[tab].GetComponentsInChildren<MonoBehaviour>(true))
                            if (info != null && info.GetType().Name.StartsWith("RecipeInfo"))
                                foreach (var entry in ItemsListedBy(info))
                                    AddEntry(entry, skill, window, tab);
                    }
                }
            }

            _unclassified = new List<string>();
            if (_windows.Count > 0)
            {
                AddGatheringFallbacks();
                AddOtherSources();
                Plugin.Log.LogInfo($"Item-use index: {_index.Count} items across {_windows.Count} skill windows");
            }
            Dump();
        }

        private static List<string> _unclassified = new List<string>();

        private static void AddOtherSources()
        {
            var names = new HashSet<string>(Item.GetAllItems().Where(i => i != null && !string.IsNullOrEmpty(i.itemName)).Select(i => i.itemName));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var refs = ItemSources.Scan(names, _unclassified);
                foreach (var r in refs)
                    Add(r.Item, r.IsCost ? Role.SpentOn : Role.ObtainedFrom, r.Category, null, -1, r.Detail, 0);
                Plugin.Log.LogInfo($"Drop/cost scan: {refs.Count} references in {sw.ElapsedMilliseconds} ms");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Drop/cost scan failed: {e}");
            }
        }

        private static IEnumerable<Item> ItemsListedBy(MonoBehaviour info)
        {
            foreach (var field in info.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var t = field.FieldType;
                var element = t.IsArray ? t.GetElementType() : t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>) ? t.GetGenericArguments()[0] : null;
                if (element == null || !typeof(Item).IsAssignableFrom(element))
                    continue;
                if (field.GetValue(info) is IEnumerable list)
                    foreach (var item in list)
                        if (item is Item i && i != null)
                            yield return i;
            }
        }

        private static void AddEntry(Item entry, string skill, MonoBehaviour window, int tab)
        {
            var in1 = Str(entry, "inputItemName1");
            var in2 = Str(entry, "inputItemName2");
            var out1 = Str(entry, "outputItemName");
            var out2 = Str(entry, "outputItemName2");
            var level = Int(entry, "levelRequirement");

            if (in1 != null || in2 != null)
            {
                // Devotion recipes have no output item; the recipe's own name says what it is.
                var product = out1 ?? entry.itemName;
                if (in1 != null) Add(in1, Role.UsedIn, skill, window, tab, product, level);
                if (in2 != null) Add(in2, Role.UsedIn, skill, window, tab, product, level);

                var ingredients = string.Join(" + ", new[] { (in1, Int(entry, "inputItemAmount1")), (in2, Int(entry, "inputItemAmount2")) }
                    .Where(x => x.Item1 != null).Select(x => x.Item2 > 1 ? $"{x.Item2}x {x.Item1}" : x.Item1));
                if (out1 != null) Add(out1, Role.MadeIn, skill, window, tab, ingredients, level);
                if (out2 != null) Add(out2, Role.MadeIn, skill, window, tab, ingredients, level);
            }
            else
            {
                var gatherLevel = new[]
                {
                    entry.requiredWoodcuttingLevel, entry.requiredMiningLevel, entry.requiredFishingLevel,
                    entry.requiredForagingLevel, entry.requiredHomesteadingLevel
                }.Max();
                Add(entry.itemName, Role.GatheredIn, skill, window, tab, null, Math.Max(level, gatherLevel));
            }
        }

        private static void Add(string item, Role role, string skill, MonoBehaviour window, int tab, string detail, int level)
        {
            if (string.IsNullOrEmpty(item))
                return;
            if (!_index.TryGetValue(item, out var uses))
                _index[item] = uses = new List<Use>();
            if (uses.Any(u => u.Role == role && u.Skill == skill && u.Detail == detail))
                return;
            uses.Add(new Use { Role = role, Skill = skill, Window = window, Tab = tab, Detail = detail, Level = level });
        }

        // Recipe classes don't share a base with these fields, so look them up per type. (AccessTools.Field
        // logs a warning for every miss, which floods the log for e.g. RecipeSmithing.outputItemName2.)
        private static readonly Dictionary<(Type, string), FieldInfo> Fields = new Dictionary<(Type, string), FieldInfo>();

        private static FieldInfo FieldOf(Type type, string name)
        {
            if (!Fields.TryGetValue((type, name), out var field))
                Fields[(type, name)] = field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field;
        }

        private static string Str(object o, string field)
        {
            var value = FieldOf(o.GetType(), field)?.GetValue(o) as string;
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private static int Int(object o, string field) =>
            FieldOf(o.GetType(), field)?.GetValue(o) is int i ? i : 0;

        // Gathering windows don't list their resources through RecipeInfo components, but each gatherable has
        // its own data type. Point those at their skill's window, on the tab whose text names the resource.
        private static readonly Dictionary<Type, string> GatherSkills = new Dictionary<Type, string>
        {
            { typeof(OreData), "Mining" },
            { typeof(TreeData), "Woodcutting" },
            { typeof(FishData), "Fishing" },
            { typeof(BushData), "Foraging" },
            { typeof(ThievingData), "Thieving" },
        };

        private static void AddGatheringFallbacks()
        {
            var tabTexts = new Dictionary<MonoBehaviour, List<(int Tab, string Text)>>();
            foreach (var item in Item.GetAllItems())
            {
                if (item == null || string.IsNullOrEmpty(item.itemName) || !GatherSkills.TryGetValue(item.GetType(), out var skill))
                    continue;
                if (_index.TryGetValue(item.itemName, out var existing) && existing.Any(u => u.Role == Role.GatheredIn))
                    continue;
                var window = _windows.FirstOrDefault(w => SkillName(w.GetType()) == skill);
                if (window == null)
                    continue;

                if (!tabTexts.TryGetValue(window, out var texts))
                    tabTexts[window] = texts = TabTexts(window);
                var tab = texts.Where(t => t.Text.IndexOf(item.itemName, StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(t => t.Tab).DefaultIfEmpty(-1).First();

                var level = new[]
                {
                    item.requiredWoodcuttingLevel, item.requiredMiningLevel, item.requiredFishingLevel,
                    item.requiredForagingLevel, item.requiredHomesteadingLevel
                }.Max();
                Add(item.itemName, Role.GatheredIn, skill, window, tab, null, level);
            }
        }

        private static List<(int, string)> TabTexts(MonoBehaviour window)
        {
            var result = new List<(int, string)>();
            var tabs = (GameObject[])PanelsField[window.GetType()].GetValue(window);
            for (var tab = 0; tabs != null && tab < tabs.Length; tab++)
                if (tabs[tab] != null)
                    foreach (var text in tabs[tab].GetComponentsInChildren<TMPro.TMP_Text>(true))
                        if (!string.IsNullOrEmpty(text.text))
                            result.Add((tab, text.text));
            return result;
        }

        // "BowCraftingSkillUI" -> "Bow Crafting"
        private static string SkillName(Type windowType)
        {
            var raw = windowType.Name.Substring(0, windowType.Name.Length - "SkillUI".Length);
            var sb = new StringBuilder();
            foreach (var c in raw)
            {
                if (char.IsUpper(c) && sb.Length > 0)
                    sb.Append(' ');
                sb.Append(c);
            }
            return sb.ToString();
        }

        // --- Tooltip -------------------------------------------------------------------------------------

        private const int MaxLinesPerRole = 6;

        internal static string Describe(string itemName)
        {
            var uses = Get(itemName);
            if (uses == null || uses.Count == 0)
                return null;

            var sb = new StringBuilder();
            AppendRole(sb, uses, Role.UsedIn, "Used in", u => $"{u.Skill}: {u.Detail}{Lv(u)}");
            AppendRole(sb, uses, Role.MadeIn, "Made with", u => $"{u.Skill}{Lv(u)}: {u.Detail}");
            AppendRole(sb, uses, Role.GatheredIn, "Gathered with", u => $"{u.Skill}{Lv(u)}");
            AppendRole(sb, uses, Role.ObtainedFrom, "Obtained from", u => $"{u.Skill}: {u.Detail}");
            AppendRole(sb, uses, Role.SpentOn, "Spent on", u => $"{u.Skill}: {u.Detail}");
            var targets = OpenTargets(uses);
            if (Plugin.ItemCtrlClick.Value && targets.Count > 0)
                sb.Append("\n<size=80%><color=#AAAAAA>Ctrl+click to open the ").Append(targets[0].Skill).Append(" window</color></size>");
            return sb.ToString();
        }

        private static string Lv(Use u) => u.Level > 0 ? $" (Lv {u.Level})" : "";

        private static void AppendRole(StringBuilder sb, List<Use> uses, Role role, string heading, Func<Use, string> line)
        {
            var matching = uses.Where(u => u.Role == role).OrderBy(u => u.Skill).ThenBy(u => u.Level).ToList();
            if (matching.Count == 0)
                return;
            sb.Append("\n<color=#7FD4FF>").Append(heading).Append(":</color>");
            foreach (var u in matching.Take(MaxLinesPerRole))
                sb.Append("\n  ").Append(line(u));
            if (matching.Count > MaxLinesPerRole)
                sb.Append($"\n  <color=#AAAAAA>...and {matching.Count - MaxLinesPerRole} more</color>");
        }

        // --- Ctrl+click ----------------------------------------------------------------------------------

        // One target per (window, tab). From the inventory the question is "what is this for", so uses come
        // first; from a recipe's ingredient it's "where do I get this", so sources come first.
        private static List<Use> OpenTargets(List<Use> uses, bool sourceFirst = false) =>
            uses.Where(u => u.Window != null)
                .OrderBy(u => sourceFirst ? (u.Role == Role.UsedIn ? 2 : (int)u.Role - 1) : (int)u.Role).ThenBy(u => u.Level)
                .GroupBy(u => (u.Window, u.Tab)).Select(g => g.First()).ToList();

        internal static bool HasSource(string itemName) =>
            Get(itemName)?.Any(u => u.Window != null && (u.Role == Role.MadeIn || u.Role == Role.GatheredIn)) == true;

        internal static void Open(string itemName, LevelUpMessage message, bool sourceFirst = false)
        {
            if (message == null)
                message = Scene.Messages;
            var uses = Get(itemName);
            if (uses == null || uses.Count == 0)
            {
                message?.ShowMessage($"No skill window lists <color=yellow>{itemName}</color>.");
                return;
            }

            var targets = OpenTargets(uses, sourceFirst);
            if (targets.Count == 0)
            {
                message?.ShowMessage($"No skill window lists <color=yellow>{itemName}</color> - see its tooltip for where it comes from.");
                return;
            }
            _lastOpenedIndex = itemName == _lastOpenedItem ? (_lastOpenedIndex + 1) % targets.Count : 0;
            _lastOpenedItem = itemName;
            var target = targets[_lastOpenedIndex];

            try
            {
                ShowWindow(target);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Opening the {target.Skill} window failed: {e}");
                return;
            }

            if (targets.Count > 1)
                message?.ShowMessage($"<color=yellow>{itemName}</color>: {target.Skill} ({_lastOpenedIndex + 1}/{targets.Count}) - Ctrl+click again for the next");
        }

        private static void ShowWindow(Use target)
        {
            var window = target.Window;
            var type = window.GetType();

            // Only one skill window at a time, as when opening them from the skills panel.
            foreach (var other in _windows)
                if (other != null && other != window && MainPanel(other) is GameObject p && p.activeSelf)
                    AccessTools.Method(other.GetType(), "CloseMainPanel")?.Invoke(other, null);

            var main = MainPanel(window);
            if (main != null && !main.activeSelf)
                AccessTools.Method(type, "OpenMainPanel").Invoke(window, null);
            if (target.Tab >= 0)
                AccessTools.Method(type, "OpenPanel", new[] { typeof(int) }).Invoke(window, new object[] { target.Tab });

            if (main != null && !main.activeInHierarchy)
            {
                // The window sits under a hidden panel; show the chain so it's actually visible.
                if (!_loggedHierarchy)
                {
                    _loggedHierarchy = true;
                    Plugin.Log.LogInfo($"{type.Name} is under a hidden parent: {Path(main.transform)}");
                }
                for (var t = main.transform.parent; t != null; t = t.parent)
                    if (!t.gameObject.activeSelf)
                        t.gameObject.SetActive(true);
            }
        }

        private static GameObject MainPanel(MonoBehaviour window) =>
            MainPanelField.TryGetValue(window.GetType(), out var f) ? f.GetValue(window) as GameObject : null;

        private static string Path(Transform t)
        {
            var parts = new List<string>();
            for (; t != null; t = t.parent)
                parts.Add(t.gameObject.activeSelf ? t.name : t.name + " (hidden)");
            parts.Reverse();
            return string.Join(" / ", parts);
        }

        // --- Data dump -----------------------------------------------------------------------------------

        // Everything the game knows about items, and what we derived, so gaps can be spotted.
        private static void Dump()
        {
            if (_windows.Count == 0)
                return;
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"# {Plugin.Name} {Plugin.Version} item dump, {DateTime.Now}");
                sb.AppendLine();
                sb.AppendLine("## Skill windows");
                foreach (var w in _windows)
                {
                    var tabs = (GameObject[])PanelsField[w.GetType()].GetValue(w);
                    sb.AppendLine($"{w.GetType().Name}: {Path(MainPanel(w)?.transform ?? w.transform)}");
                    for (var i = 0; tabs != null && i < tabs.Length; i++)
                        sb.AppendLine($"    tab {i}: {(tabs[i] != null ? tabs[i].name : "(none)")}");
                }

                sb.AppendLine();
                sb.AppendLine("## Items (Resources/Recipes)");
                // The same item often exists as several assets (one per drop table), so list each name once.
                foreach (var item in Item.GetAllItems().Where(i => i != null).GroupBy(i => i.itemName).Select(g => g.First()).OrderBy(i => i.itemName))
                {
                    var uses = Get(item.itemName);
                    sb.AppendLine($"{item.itemName}  [{item.GetType().Name}]  info: {item.information}");
                    if (uses != null)
                        foreach (var u in uses)
                            sb.AppendLine($"    {u.Role} {u.Skill} tab {u.Tab}{Lv(u)}: {u.Detail}");
                }

                // Item references the drop/cost scan couldn't classify, to spot gaps in its keyword lists.
                sb.AppendLine();
                sb.AppendLine("## Unclassified item references");
                foreach (var line in _unclassified.Distinct().OrderBy(l => l))
                    sb.AppendLine(line);

                File.WriteAllText(System.IO.Path.Combine(Paths.BepInExRootPath, "BloobsTweaks-items.txt"), sb.ToString());
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Item dump failed: {e.Message}");
            }
        }
    }

    // Builds the index (and writes the dump) once the game scene is up, instead of on the first hover.
    internal class ItemUsesWarmup : MonoBehaviour
    {
        private float _next;

        private void Update()
        {
            if (Time.unscaledTime < _next)
                return;
            _next = Time.unscaledTime + 5f;
            if (!ItemUses.IsBuilt && Scene.Find<InventoryUI>() != null)
                ItemUses.Warm();
        }
    }

    // Inventory and bank tooltips: remember which item the game is describing, then add our lines to the
    // text it hands to the tooltip.
    [HarmonyPatch]
    internal static class ItemTooltipPatches
    {
        private static string _item;

        [HarmonyPatch(typeof(InventoryUI), "UpdateTooltip")]
        [HarmonyPrefix]
        static void InventoryBegin(InventorySystem.InventoryItem item) => _item = item?.itemName;

        [HarmonyPatch(typeof(InventoryUI), "UpdateTooltip")]
        [HarmonyFinalizer]
        static void InventoryEnd() => _item = null;

        [HarmonyPatch(typeof(BankUI), "OnPointerEnter")]
        [HarmonyPrefix]
        static void BankBegin(string itemName) => _item = itemName;

        [HarmonyPatch(typeof(BankUI), "OnPointerEnter")]
        [HarmonyFinalizer]
        static void BankEnd() => _item = null;

        [HarmonyPatch(typeof(TooltipInv), nameof(TooltipInv.SetAndShowTooltip))]
        [HarmonyPrefix]
        static void InventoryTooltip(ref string message) => Append(ref message);

        [HarmonyPatch(typeof(TooltipUI), nameof(TooltipUI.SetAndShowTooltip))]
        [HarmonyPrefix]
        static void BankTooltip(ref string message) => Append(ref message);

        private static void Append(ref string message)
        {
            if (_item == null || !Plugin.ItemTooltips.Value)
                return;
            try
            {
                var extra = ItemUses.Describe(_item);
                if (extra != null)
                    message += extra;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Item tooltip for {_item} failed: {e.Message}");
            }
        }
    }

    // Ctrl+left-click an item in the inventory or bank to open the skill window it belongs to.
    [HarmonyPatch]
    internal static class ItemCtrlClickPatches
    {
        private static bool CtrlHeld => Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

        [HarmonyPatch(typeof(InventoryUI), "OnSlotClick")]
        [HarmonyPrefix]
        static bool Inventory(InventoryUI __instance, UnityEngine.EventSystems.PointerEventData eventData, string itemName)
        {
            if (!Plugin.ItemCtrlClick.Value || !CtrlHeld || eventData.button != UnityEngine.EventSystems.PointerEventData.InputButton.Left)
                return true;
            ItemUses.Open(itemName, __instance.levelUpMessage);
            return false;
        }

        [HarmonyPatch(typeof(BankUI), nameof(BankUI.OnSlotClick))]
        [HarmonyPrefix]
        static bool Bank(BankUI __instance, string itemName)
        {
            if (!Plugin.ItemCtrlClick.Value || !CtrlHeld)
                return true;
            ItemUses.Open(itemName, __instance.levelUpMessage);
            return false;
        }
    }
}
