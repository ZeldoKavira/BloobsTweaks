using System;
using System.Text.RegularExpressions;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace BloobsTweaks
{
    // The Homesteading guide lists costs in its own rows ("Requires: <color=yellow>500</color> Logs, ..." for crops
    // and workbench, "...<color=yellow>20</color>x Planks" for houses and buildings). Colour each green when you
    // have that many and red when you don't, as in the other skills' recipe lists, and keep them current while
    // the guide is open.
    [HarmonyPatch(typeof(HomesteadingSkillGuide), "RebuildTab")]
    internal static class HomesteadGuideColors
    {
        private static readonly Regex Cost = new Regex(@"<color=yellow>([\d,]+)</color>(x?) ([^,\n<]+)", RegexOptions.Compiled);

        static void Postfix(HomesteadingSkillGuide __instance)
        {
            if (!Plugin.RecipeAmountColors.Value)
                return;
            try
            {
                Refresh(__instance);
                if (__instance.GetComponent<GuideRefresher>() == null)
                    __instance.gameObject.AddComponent<GuideRefresher>().Guide = __instance;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Homesteading guide colours failed: {e.Message}");
            }
        }

        internal static void Refresh(HomesteadingSkillGuide guide)
        {
            var inventory = Scene.Find<InventorySystem>();
            if (inventory == null)
                return;
            foreach (var text in guide.GetComponentsInChildren<TMP_Text>(true))
                if (text != null && text.name == "InputText")
                    Colour(text, inventory);
        }

        private static void Colour(TMP_Text text, InventorySystem inventory)
        {
            var memo = text.GetComponent<OriginalText>() ?? text.gameObject.AddComponent<OriginalText>();
            // The game rewrote the row since we last coloured it: that's the new original.
            if (text.text != memo.Shown)
                memo.Original = text.text;
            if (string.IsNullOrEmpty(memo.Original))
                return;

            var coloured = Cost.Replace(memo.Original, m =>
            {
                if (!long.TryParse(m.Groups[1].Value.Replace(",", ""), out var need))
                    return m.Value;
                var raw = m.Groups[3].Value;
                var name = raw.Trim();
                var trailing = raw.Substring(raw.TrimEnd().Length);
                var have = BankCrafting.Count(inventory, name, Plugin.HomesteadFromBank.Value);
                var colour = have >= need ? "#9CFF9C" : "#FF7B7B";
                return $"<color={colour}>{m.Groups[1].Value}</color>{m.Groups[2].Value} <color={colour}>{name}</color>{trailing}";
            });
            text.text = coloured;
            memo.Shown = coloured;
        }

        private class OriginalText : MonoBehaviour
        {
            public string Original;
            public string Shown;
        }

        private class GuideRefresher : MonoBehaviour
        {
            public HomesteadingSkillGuide Guide;
            private float _next;

            private void Update()
            {
                if (Guide == null || !Plugin.RecipeAmountColors.Value || Time.unscaledTime < _next)
                    return;
                _next = Time.unscaledTime + 2f;
                if (Guide.mainPanel == null || !Guide.mainPanel.activeInHierarchy)
                    return;
                try { Refresh(Guide); }
                catch (Exception e) { Plugin.Log.LogWarning($"Homesteading guide refresh failed: {e.Message}"); enabled = false; }
            }
        }
    }
}
