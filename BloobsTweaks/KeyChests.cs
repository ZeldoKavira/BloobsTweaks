using System;
using System.Collections;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;

namespace BloobsTweaks
{
    // Left-clicking a chest key in the inventory (which otherwise does nothing) walks the Bloob to the nearest
    // chest that takes it and starts that chest's own auto-open. The game's auto-open already stops other
    // skills and waits until the Bloob is in range, so we only add the walk.
    [HarmonyPatch(typeof(InventoryUI), "OnSlotClick")]
    internal static class KeyChests
    {
        private static readonly Func<InventoryUI, bool> AnyShopOpen =
            AccessTools.MethodDelegate<Func<InventoryUI, bool>>(AccessTools.Method(typeof(InventoryUI), "IsAnyShopOpen"));

        // Same guard order as the game's own left-click handling, so locks, shift/ctrl shortcuts, the bank and
        // shops all keep working as before.
        static bool Prefix(InventoryUI __instance, PointerEventData eventData, string itemName, int slotIndex)
        {
            if (!Plugin.KeyWalksToChest.Value || eventData.button != PointerEventData.InputButton.Left || string.IsNullOrEmpty(itemName))
                return true;
            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift) || Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
                return true;
            var inventory = __instance.inventorySystem;
            if (inventory == null || inventory.IsSlotLocked(slotIndex) || (__instance.bankUI != null && __instance.bankUI.IsBankUIOpen) || AnyShopOpen(__instance))
                return true;

            var chests = Resources.FindObjectsOfTypeAll<LootChest>()
                .Where(c => c != null && c.gameObject.scene.IsValid() && string.Equals(c.keyName, itemName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (chests.Count == 0)
                return true; // not a chest key

            var mover = Scene.Find<CharacterMovement>();
            if (mover == null)
                return true;
            var from = mover.transform.position;
            var chest = chests.OrderBy(c => Vector3.Distance(from, c.transform.position)).First();

            if (chest.requiresCookingLevel && chest.cookingSkill != null &&
                chest.cookingSkill.CookingLevel < chest.requiredCookingLevel && chest.cookingSkill.cookingPrestigeLevel < 1)
            {
                __instance.levelUpMessage?.ShowMessage($"That chest needs Cooking level <color=yellow>{chest.requiredCookingLevel}</color>.");
                return false;
            }

            if (CombatManager.Instance != null && CombatManager.Instance.IsAutoCombatActive())
                CombatManager.Instance.StopAutoCombat();
            if (AutoGolden.Instance == null)
                return true;
            AutoGolden.Instance.StartCoroutine(Walk(++_walkId, chest, mover));
            __instance.levelUpMessage?.ShowMessage($"Heading to the chest for your <color=yellow>{itemName}</color>.");
            return false;
        }

        private static int _walkId;

        // Read through reflection: FollowerEntity's type drags in several A* assemblies at compile time.
        private static readonly System.Reflection.FieldInfo FollowerField = AccessTools.Field(typeof(CharacterMovement), "followerEntity");
        private static System.Reflection.PropertyInfo _destination;

        private static Vector3? Destination(CharacterMovement mover)
        {
            var follower = FollowerField?.GetValue(mover);
            if (follower == null)
                return null;
            if (_destination == null)
                _destination = follower.GetType().GetProperty("destination");
            return _destination?.GetValue(follower) is Vector3 v ? v : (Vector3?)null;
        }

        // Pathfinding only covers the loaded part of the world, so a long walk stops at the edge of each area.
        // Keep re-issuing the walk until the Bloob is in range. The game sets the destination to the Bloob's own
        // position when it stops, so a destination that's neither the chest nor where the Bloob stands means
        // the player sent it somewhere else (a click, another skill) - then we let go.
        private static IEnumerator Walk(int id, LootChest chest, CharacterMovement mover)
        {
            var target = chest.transform.position;
            var started = false;
            if (chest.isActiveAndEnabled)
            {
                chest.StartAutoOpenChest();
                started = true;
            }
            mover.MoveTo(target);

            var deadline = Time.unscaledTime + 300f;
            while (id == _walkId && chest != null && mover != null && Time.unscaledTime < deadline)
            {
                yield return new WaitForSecondsRealtime(0.25f);

                // A chest in an area that wasn't loaded can't run its auto-open until it is.
                if (!started && chest.isActiveAndEnabled)
                {
                    chest.StartAutoOpenChest();
                    started = true;
                }

                var pos = mover.transform.position;
                if (Vector2.Distance(pos, target) <= chest.activationDistance * 0.8f)
                {
                    if (started)
                        yield break;
                    continue;
                }

                var dest = Destination(mover) ?? target;
                var headingToChest = Vector2.Distance(dest, target) < 1f;
                var stopped = !mover.isMoving || Vector2.Distance(dest, pos) < 0.5f;
                if (!headingToChest && !stopped)
                    yield break;
                if (stopped)
                    mover.MoveTo(target);
            }
        }
    }
}
