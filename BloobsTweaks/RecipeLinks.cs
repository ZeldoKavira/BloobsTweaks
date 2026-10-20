using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace BloobsTweaks
{
    // Turns the ingredient names in skill-window recipe lists ("Input Items: 1x Sand") into links that open
    // the window where that ingredient is made or gathered, so recipes can be followed back to their source.
    [HarmonyPatch]
    internal static class RecipeLinks
    {
        private const string Prefix = "bt:";

        // Every RecipeInfo* component writes ingredients as "<color=yellow|#FFFF00>N</color>x Name" + newline.
        private static readonly Regex Ingredient = new Regex(@"<color=(?:yellow|#FFFF00)>(\d+)</color>x ([^\n<]+)", RegexOptions.Compiled);

        private const string Enough = "#9CFF9C";
        private const string Short = "#FF7B7B";

        private static readonly Dictionary<Type, FieldInfo> TextFields = new Dictionary<Type, FieldInfo>();

        static IEnumerable<MethodBase> TargetMethods() =>
            typeof(InventorySystem).Assembly.GetTypes()
                .Where(t => typeof(MonoBehaviour).IsAssignableFrom(t) && t.Name.StartsWith("RecipeInfo"))
                .Select(t => t.GetMethod("DisplayRecipes", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null))
                .Where(m => m != null);

        static void Postfix(MonoBehaviour __instance)
        {
            if (!Plugin.RecipeLinksEnabled.Value && !Plugin.RecipeAmountColors.Value)
                return;
            try
            {
                var type = __instance.GetType();
                if (!TextFields.TryGetValue(type, out var field))
                    TextFields[type] = field = type.GetField("recipeTextElements", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (!(field?.GetValue(__instance) is List<TMP_Text> texts))
                    return;
                var inventory = Scene.Find<InventorySystem>();
                foreach (var text in texts)
                    if (text != null)
                        Decorate(text, inventory);

                // Recipe lists are only written when a tab opens; keep the colours current while it's showing.
                if (Plugin.RecipeAmountColors.Value && __instance.GetComponent<RecipeRefresher>() == null)
                    __instance.gameObject.AddComponent<RecipeRefresher>().Target = __instance;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Decorating recipe text failed: {e.Message}");
            }
        }

        // Colours "Nx Name" green when you have N (bag, plus bank if crafting from the bank is on) and red when
        // you don't, and turns the name into a link to where the ingredient comes from.
        private static void Decorate(TMP_Text text, InventorySystem inventory)
        {
            var s = text.text;
            if (string.IsNullOrEmpty(s))
                return;

            // Leave the "Output Items" line alone: it's the recipe's own product, already in this window.
            var cut = s.IndexOf("Output Items", StringComparison.Ordinal);
            var head = cut >= 0 ? s.Substring(0, cut) : s;
            var tail = cut >= 0 ? s.Substring(cut) : "";

            var anyLink = false;
            var decorated = Ingredient.Replace(head, m =>
            {
                var amount = m.Groups[1].Value;
                var raw = m.Groups[2].Value;
                var name = raw.Trim();
                var trailing = raw.Substring(raw.TrimEnd().Length);

                var colour = "yellow";
                if (Plugin.RecipeAmountColors.Value && inventory != null && int.TryParse(amount, out var need))
                    colour = BankCrafting.Count(inventory, name, Plugin.CraftFromBank.Value) >= need ? Enough : Short;

                var label = $"<color={colour}>{amount}</color>x ";
                if (Plugin.RecipeLinksEnabled.Value && ItemUses.HasSource(name))
                {
                    anyLink = true;
                    var nameColour = Plugin.RecipeAmountColors.Value ? colour : "#7FD4FF";
                    return $"{label}<link=\"{Prefix}{name}\"><color={nameColour}><u>{name}</u></color></link>{trailing}";
                }
                return Plugin.RecipeAmountColors.Value ? $"{label}<color={colour}>{name}</color>{trailing}" : m.Value;
            });
            if (decorated == head)
                return;

            text.text = decorated + tail;
            if (anyLink)
            {
                // The click has to land on the text itself for us to see which link it hit.
                text.raycastTarget = true;
                if (text.GetComponent<RecipeLinkClicks>() == null)
                    text.gameObject.AddComponent<RecipeLinkClicks>();
            }
        }

        internal static bool TryGetLink(TMP_Text text, PointerEventData e, out string itemName)
        {
            itemName = null;
            var index = TMP_TextUtilities.FindIntersectingLink(text, e.position, e.pressEventCamera);
            if (index < 0 || index >= text.textInfo.linkCount)
                return false;
            var id = text.textInfo.linkInfo[index].GetLinkID();
            if (!id.StartsWith(Prefix))
                return false;
            itemName = id.Substring(Prefix.Length);
            return true;
        }
    }

    // Re-runs a recipe list's own DisplayRecipes every couple of seconds while it's on screen, so the
    // have/haven't colours follow the inventory and bank as they change.
    internal class RecipeRefresher : MonoBehaviour
    {
        public MonoBehaviour Target;
        private MethodInfo _display;
        private float _next;

        private void OnEnable() => _next = Time.unscaledTime + 2f;

        private void Update()
        {
            if (Target == null || !Plugin.RecipeAmountColors.Value || Time.unscaledTime < _next)
                return;
            _next = Time.unscaledTime + 2f;
            if (_display == null)
                _display = Target.GetType().GetMethod("DisplayRecipes", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            try { _display?.Invoke(Target, null); }
            catch (Exception e) { Plugin.Log.LogWarning($"Refreshing recipe list failed: {e.Message}"); enabled = false; }
        }
    }

    internal class RecipeLinkClicks : MonoBehaviour, IPointerClickHandler
    {
        private TMP_Text _text;

        public void OnPointerClick(PointerEventData e)
        {
            if (_text == null)
                _text = GetComponent<TMP_Text>();

            if (Plugin.RecipeLinksEnabled.Value && e.button == PointerEventData.InputButton.Left &&
                _text != null && RecipeLinks.TryGetLink(_text, e, out var itemName))
            {
                ItemUses.Open(itemName, null, sourceFirst: true);
                return;
            }

            // Not on a link: let whatever sits above the text handle the click as it did before.
            if (transform.parent != null)
                ExecuteEvents.ExecuteHierarchy(transform.parent.gameObject, e, ExecuteEvents.pointerClickHandler);
        }
    }
}
