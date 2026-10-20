using HarmonyLib;

namespace BloobsTweaks
{
    // Lifts the cap on how many items can be on the auto-sell / auto-bank list (normally 1 + Soulbinding level/10
    // + 5 per prestige, 61 at most). Everything that reads the cap - adding, loading the saved list, the count
    // display, and the clean-up that drops entries over the cap - goes through GetMaxAutoManagedEntries.
    // How much is sold or banked per tick, how many items per tick (active souls) and how often are separate
    // and stay as the game has them.
    [HarmonyPatch]
    internal static class UnlimitedAutoManage
    {
        // Comfortably more than the ~2,100 items in the game; kept readable rather than int.MaxValue.
        private const int Unlimited = 9999;

        [HarmonyPatch(typeof(SoulCompendiumManager), nameof(SoulCompendiumManager.GetMaxAutoManagedEntries))]
        [HarmonyPostfix]
        static void Cap(ref int __result)
        {
            if (Plugin.UnlimitedAutoManage.Value)
                __result = Unlimited;
        }

        [HarmonyPatch(typeof(SoulCompendiumManager), "UpdateAutoEntryCountText")]
        [HarmonyPostfix]
        static void Label(SoulCompendiumManager __instance)
        {
            if (!Plugin.UnlimitedAutoManage.Value || __instance.autoEntryCountText == null)
                return;
            __instance.autoEntryCountText.text = __instance.autoEntryCountText.text
                .Replace($"/<color=yellow>{Unlimited}</color>", "/<color=yellow>Unlimited</color>");
        }
    }
}
