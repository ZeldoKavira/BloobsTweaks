using System.IO;
using HarmonyLib;
using UnityEngine;

namespace BloobsTweaks
{
    // Optionally loads a save as soon as the main menu appears after launching - the last one played, or a chosen
    // slot - doing what a slot button does: ES3CustomPath.ChangeSaveSlot, then SceneLoadUIController.LoadGameScene.
    // Only on the first visit to the menu each launch (quitting to the menu later stays there), never for a slot
    // with no save (that would start a new game), and holding Shift as the menu appears skips it.
    internal class AutoLoadSave : MonoBehaviour
    {
        private const float MenuSettleSeconds = 1.5f;

        private bool _done;
        private float _menuSeenAt = -1f;

        private void Update()
        {
            if (_done)
                return;

            // The game scene has loaded first (e.g. the setting was off): nothing to do this launch.
            if (Scene.Find<InventoryUI>() != null)
            {
                _done = true;
                return;
            }

            var loader = Scene.Find<SceneLoadUIController>();
            if (loader == null || ES3CustomPath.Instance == null)
                return;

            if (_menuSeenAt < 0f)
                _menuSeenAt = Time.unscaledTime;
            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
            {
                _done = true;
                Plugin.Log.LogInfo("Auto-load skipped (Shift held)");
                return;
            }
            // Give the menu a moment to finish setting up, as a player clicking would.
            if (Time.unscaledTime - _menuSeenAt < MenuSettleSeconds)
                return;
            _done = true;

            if (!Plugin.AutoLoadEnabled.Value)
                return;
            var slot = Plugin.AutoLoadSlot.Value >= 0 ? Plugin.AutoLoadSlot.Value : Plugin.AutoLoadLastSlot.Value;
            if (slot < 0 || slot >= ES3CustomPath.Instance.maxSaveSlots || !SaveExists(slot))
            {
                Plugin.Log.LogInfo($"Auto-load: no save in slot {slot + 1}; staying on the menu");
                return;
            }

            Plugin.Log.LogInfo($"Auto-loading save slot {slot + 1}");
            ES3CustomPath.Instance.ChangeSaveSlot(slot);
            loader.LoadGameScene();
        }

        // Same path the game builds for a slot (SaveSlotsUI.InitializeSaveSlot).
        private static bool SaveExists(int slot)
        {
            var dir = Path.Combine(Directory.GetParent(Application.persistentDataPath).FullName, "Bloobs Adventure Idle");
            return File.Exists(Path.Combine(dir, slot == 0 ? "SaveFile.es3" : $"SaveFile{slot}.es3"));
        }

        // Remember the slot of every save that gets loaded, for "last played".
        [HarmonyPatch(typeof(SceneLoadUIController), nameof(SceneLoadUIController.LoadGameScene))]
        private static class RememberSlot
        {
            static void Prefix()
            {
                if (ES3CustomPath.Instance != null)
                    Plugin.AutoLoadLastSlot.Value = ES3CustomPath.Instance.currentSaveSlot;
            }
        }
    }
}
