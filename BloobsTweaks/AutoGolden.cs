using System;
using System.Collections;
using HarmonyLib;
using UnityEngine;

namespace BloobsTweaks
{
    // Gathering occasionally turns a resource into a glowing "Golden Resource" for a few seconds; mousing over
    // it calls the resource's own claim method (wired up in the scene, not in code) for a doubled reward. We
    // hook each ApplyGlow and call that same claim method ourselves, so rewards, XP, challenge progress and
    // the rest all come from the game's own code.
    internal class AutoGolden : MonoBehaviour
    {
        internal static AutoGolden Instance;

        internal static int Claimed;

        private static readonly AccessTools.FieldRef<Trees, bool> TreeGlowing = AccessTools.FieldRefAccess<Trees, bool>("isGlowing");
        private static readonly AccessTools.FieldRef<Ores, bool> OreGlowing = AccessTools.FieldRefAccess<Ores, bool>("isGlowing");
        private static readonly AccessTools.FieldRef<Bushes, bool> BushGlowing = AccessTools.FieldRefAccess<Bushes, bool>("isGlowing");
        private static readonly AccessTools.FieldRef<FishingSpots, bool> FishGlowing = AccessTools.FieldRefAccess<FishingSpots, bool>("isGlowing");
        private static readonly AccessTools.FieldRef<Chests, Vector2> ChestGlowMouse = AccessTools.FieldRefAccess<Chests, Vector2>("mousePositionWhenGlowStarted");

        private void Awake() => Instance = this;

        // Claim on the next frame rather than inside ApplyGlow: trees and fishing spots glow from inside their
        // damage loop, and claiming resets the damage counter that loop is still using.
        internal static void Queue(MonoBehaviour resource, Func<bool> isGlowing, Action claim)
        {
            if (!Plugin.AutoGoldenEnabled.Value || Instance == null || resource == null || !isGlowing())
                return;
            Instance.StartCoroutine(ClaimNextFrame(resource, isGlowing, claim));
        }

        private static IEnumerator ClaimNextFrame(MonoBehaviour resource, Func<bool> isGlowing, Action claim)
        {
            yield return null;
            // The reward goes into the inventory, which right after loading a save isn't set up yet. Wait for it
            // (a glow lasts several seconds) rather than claiming into a half-built inventory.
            var until = Time.unscaledTime + 5f;
            while (!InventoryReady() && Time.unscaledTime < until)
                yield return null;
            if (resource == null || !Plugin.AutoGoldenEnabled.Value || !isGlowing() || !InventoryReady())
                yield break;
            try
            {
                claim();
                if (!isGlowing())
                    Claimed++;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Auto-claim of golden {resource.GetType().Name} failed: {e}");
            }
        }

        private static bool InventoryReady()
        {
            var inventory = Scene.Find<InventorySystem>();
            return inventory != null && inventory.inventoryItems != null && inventory.inventoryItems.Count >= inventory.maxInventorySize;
        }

        [HarmonyPatch(typeof(Trees), nameof(Trees.ApplyGlow))]
        private static class TreePatch
        {
            static void Postfix(Trees __instance) =>
                Queue(__instance, () => TreeGlowing(__instance), __instance.OnGolden);
        }

        [HarmonyPatch(typeof(Ores), nameof(Ores.ApplyGlow))]
        private static class OrePatch
        {
            static void Postfix(Ores __instance) =>
                Queue(__instance, () => OreGlowing(__instance), __instance.OnGolden);
        }

        [HarmonyPatch(typeof(Bushes), nameof(Bushes.ApplyGlow))]
        private static class BushPatch
        {
            static void Postfix(Bushes __instance) =>
                Queue(__instance, () => BushGlowing(__instance), __instance.OnGolden);
        }

        [HarmonyPatch(typeof(FishingSpots), nameof(FishingSpots.ApplyGlow))]
        private static class FishingSpotPatch
        {
            static void Postfix(FishingSpots __instance) =>
                Queue(__instance, () => FishGlowing(__instance), __instance.OnGoldenOpp);
        }

        [HarmonyPatch(typeof(Chests), nameof(Chests.ApplyGlow))]
        private static class ChestPatch
        {
            // Chests only pay out once the mouse has moved a little since the glow began, so move the
            // remembered start position out of the way before claiming.
            static void Postfix(Chests __instance) =>
                Queue(__instance, () => __instance.isGlowing, () =>
                {
                    ChestGlowMouse(__instance) = new Vector2(-100000f, -100000f);
                    __instance.OnGolden();
                });
        }
    }
}
