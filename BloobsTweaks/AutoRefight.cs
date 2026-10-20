using System.Collections;
using HarmonyLib;
using UnityEngine;

namespace BloobsTweaks
{
    // Dying sends the Bloob home at full HP and ends combat (and auto Beast Mastery) for good. This remembers
    // how the current fight was started - a CombatStarter target (Beast Mastery task / target buttons) or a
    // specific enemy (clicked, or auto-navigation) - and after a death replays that same start, so the game
    // walks back and resumes exactly as it would have from the original click, auto-BM included.
    //
    // Guard against death loops: after MaxDeathsWithoutKill deaths with no kill in between, it stops and says so.
    internal class AutoRefight : MonoBehaviour
    {
        internal static AutoRefight Instance;

        // How the last fight was started.
        private static CombatStarter _starter;
        private static int _index = -1;
        private static BasicEnemy _enemy;
        private static string _enemyName;

        private static int _deathsWithoutKill;
        // True while we replay a start ourselves, so it isn't mistaken for the player starting a new fight.
        private static bool _replaying;

        internal static string Status { get; private set; } = "";

        private void Awake() => Instance = this;

        private static void RememberTarget(CombatStarter starter, int index)
        {
            if (starter == null || starter.enemies == null || index < 0 || index >= starter.enemies.Count)
                return;
            _starter = starter;
            _index = index;
            _enemy = starter.enemies[index];
            _enemyName = _enemy != null ? _enemy.name : null;
            NewFight();
        }

        // A fight the player (or auto-BM moving on to a new task) started resets the death-loop guard.
        private static void NewFight()
        {
            if (_replaying)
                return;
            _deathsWithoutKill = 0;
            Status = "";
        }

        private static void RememberEnemy(CombatStarter starter, BasicEnemy enemy)
        {
            if (enemy == null)
                return;
            _starter = starter;
            _index = -1;
            _enemy = enemy;
            _enemyName = enemy.name;
            NewFight();
        }

        [HarmonyPatch(typeof(CombatStarter), nameof(CombatStarter.SetTargetByIndex))]
        private static class SetTargetPatch
        {
            static void Prefix(CombatStarter __instance, int index) => RememberTarget(__instance, index);
        }

        [HarmonyPatch(typeof(CombatStarter), nameof(CombatStarter.StartCombatWithEnemy))]
        private static class StartWithEnemyPatch
        {
            static void Prefix(CombatStarter __instance, BasicEnemy targetEnemy) => RememberEnemy(__instance, targetEnemy);
        }

        // Every start ends here, including clicking an enemy in the world. Keep a remembered CombatStarter
        // target when it's the same enemy (that's the starter arriving); otherwise this is a new fight.
        [HarmonyPatch(typeof(CombatManager), nameof(CombatManager.InitiateAutoCombat))]
        private static class InitiatePatch
        {
            static void Postfix(BasicEnemy enemy)
            {
                if (enemy == null || enemy.name == _enemyName)
                    return;
                RememberEnemy(null, enemy);
            }
        }

        [HarmonyPatch(typeof(CombatManager), nameof(CombatManager.RecordKill))]
        private static class KillPatch
        {
            static void Postfix()
            {
                if (_deathsWithoutKill > 0)
                    Status = "";
                _deathsWithoutKill = 0;
            }
        }

        // Runs before the game clears the combat state, so we can tell whether the Bloob died mid-fight.
        [HarmonyPatch(typeof(CombatManager), nameof(CombatManager.OnPlayerDeath))]
        private static class DeathPatch
        {
            static void Prefix(CombatManager __instance)
            {
                var inCombat = __instance.IsAutoCombatActive() || __instance.currentTargetEnemy != null;
                if (!Plugin.AutoRefightEnabled.Value || !inCombat || _enemyName == null || Instance == null)
                    return;

                _deathsWithoutKill++;
                var max = Mathf.Max(1, Plugin.AutoRefightMaxDeaths.Value);
                if (_deathsWithoutKill > max)
                {
                    Status = $"Paused: died {_deathsWithoutKill} times in a row to {_enemyName} without a kill. Start a fight to reset.";
                    Message($"Auto-refight paused: <color=yellow>{_deathsWithoutKill}</color> deaths in a row without a kill against <color=yellow>{_enemyName}</color>.");
                    return;
                }
                Instance.StartCoroutine(Instance.Refight(_deathsWithoutKill, max));
            }
        }

        private IEnumerator Refight(int attempt, int max)
        {
            var starter = _starter;
            var index = _index;
            var enemy = _enemy;
            var name = _enemyName;

            // Let the death teleport, rebirth message and the game's own clean-up finish first.
            yield return new WaitForSecondsRealtime(Mathf.Max(0.5f, Plugin.AutoRefightDelay.Value));

            var combat = CombatManager.Instance;
            if (!Plugin.AutoRefightEnabled.Value || combat == null || combat.IsAutoCombatActive())
                yield break; // turned off meanwhile, or the player already started something

            Status = $"Refighting {name} ({attempt}/{max} deaths without a kill)";
            Message($"Died fighting <color=yellow>{name}</color> - heading back ({attempt}/{max}).");

            _replaying = true;
            try
            {
                if (starter != null && index >= 0)
                {
                    starter.SetTargetByIndex(index);
                    yield break;
                }

                if (enemy == null)
                    enemy = FindByName(name);
                if (starter == null)
                    starter = Scene.Find<CombatStarter>();
                if (enemy == null || starter == null || !starter.StartCombatWithEnemy(enemy))
                    Message($"Auto-refight couldn't find <color=yellow>{name}</color>.");
            }
            finally
            {
                _replaying = false;
            }
        }

        private static BasicEnemy FindByName(string name)
        {
            foreach (var e in FindObjectsOfType<BasicEnemy>())
                if (e.name == name)
                    return e;
            return null;
        }

        private static void Message(string text) =>
            Scene.Messages?.ShowMessage(text);
    }
}
