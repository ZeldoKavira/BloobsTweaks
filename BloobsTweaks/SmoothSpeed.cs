using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace BloobsTweaks
{
    // Makes high game speeds easier on the eye. Only active while the game runs faster than normal.
    //  - Camera: the game follows with Lerp(t = smoothSpeed * deltaTime), which at high speed exceeds 1 and
    //    snaps the camera onto the Bloob every frame. We glide in real time instead, catching up faster the
    //    higher the speed and never letting the Bloob drift far from centre.
    //  - Animations: the Bloob's and enemies' animators play in real time, so walk/attack cycles don't strobe.
    //    Purely visual - combat runs on its own timers, not on animation events.
    //  - Popups: XP and damage numbers and the loot/gold notes time themselves in game time and vanish within a
    //    frame or two at high speed; they're switched to real time.
    internal static class SmoothSpeed
    {
        internal static bool Active => Plugin.SmoothHighSpeed.Value && Time.timeScale > 1.05f;

        // ---------------------------------------------------------------------------------------------------
        // Camera

        [HarmonyPatch(typeof(CameraMovement), "Update")]
        private static class CameraPatch
        {
            static bool Prefix(CameraMovement __instance)
            {
                if (!Active)
                    return true;
                var target = __instance.target;
                if (target == null)
                    return false;

                var t = __instance.transform;
                var goal = new Vector3(target.position.x + __instance.offset.x, target.position.y + __instance.offset.y, -10f);
                var rate = __instance.smoothSpeed * Mathf.Sqrt(Time.timeScale);
                var pos = Vector3.Lerp(t.position, goal, 1f - Mathf.Exp(-rate * Time.unscaledDeltaTime));

                // Keep the Bloob well inside the view however fast it moves.
                var cam = __instance.GetComponent<Camera>();
                var maxLag = cam != null && cam.orthographic ? cam.orthographicSize * 0.35f : 3f;
                var lag = new Vector2(goal.x - pos.x, goal.y - pos.y);
                if (lag.magnitude > maxLag)
                {
                    var keep = lag.normalized * maxLag;
                    pos = new Vector3(goal.x - keep.x, goal.y - keep.y, -10f);
                }
                pos.z = -10f;
                t.position = pos;
                return false;
            }
        }

        // ---------------------------------------------------------------------------------------------------
        // Popups

        public static float PopupDelta() => Active ? Time.unscaledDeltaTime : Time.deltaTime;

        public static object PopupWait(float seconds) =>
            Active ? (object)new WaitForSecondsRealtime(seconds) : new WaitForSeconds(seconds);

        // Level-up popups wait WaitForSecondsRealtime(5 / timeScale) - shrinking on purpose as speed rises.
        public static float PopupTimeScale() => Active ? 1f : Time.timeScale;

        // Coroutines that only show or hide notification text, found by name across the game. Similar-looking
        // timers with gameplay effects (golden glow, item despawn, enemy fade, ClearEatInfoAfterDelay which also
        // releases the auto-eat lock) are deliberately not here.
        private static readonly HashSet<string> DisplayCoroutines = new HashSet<string>
        {
            "HideLootInfo", "HideLootInfoRoutine", "HideLootMessageAfterDelay", "DeactivateLevelUpUI", "DeactivateAfterDelay",
            "DeactivateAfterDelayFloat", "ClearXPTextAfterDelay", "DeactivateCompletionText", "HideBuryInfo", "HideDPGainPopup",
            "HideFeedback", "ClearMessageAfterDelay", "DisplayRebirthMessage", "ShowDamageText", "ShowXPAnimation",
            "ShowBatchEatInfo",
        };

        [HarmonyPatch]
        private static class PopupPatch
        {
            private static readonly MethodInfo DeltaTime = AccessTools.PropertyGetter(typeof(Time), nameof(Time.deltaTime));
            private static readonly ConstructorInfo WaitCtor = AccessTools.Constructor(typeof(WaitForSeconds), new[] { typeof(float) });
            private static readonly MethodInfo TimeScale = AccessTools.PropertyGetter(typeof(Time), nameof(Time.timeScale));

            static IEnumerable<MethodBase> TargetMethods()
            {
                var found = new List<MethodBase>();
                // Whole classes that are nothing but popups.
                foreach (var type in new[] { typeof(XPPopUp), typeof(DamageNumber), typeof(GoldLootManager) })
                    foreach (var t in WithNested(type))
                        found.AddRange(t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                            .Where(m => !m.IsAbstract && !m.ContainsGenericParameters && m.GetMethodBody() != null));
                // Notification timers elsewhere (classes that also run gameplay in game time).
                foreach (var type in typeof(InventorySystem).Assembly.GetTypes())
                {
                    if (!typeof(MonoBehaviour).IsAssignableFrom(type))
                        continue;
                    foreach (var m in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (!DisplayCoroutines.Contains(m.Name) || m.ReturnType != typeof(System.Collections.IEnumerator))
                            continue;
                        var moveNext = AccessTools.EnumeratorMoveNext(m);
                        if (moveNext != null)
                            found.Add(moveNext);
                    }
                }
                return found.Distinct().Where(UsesGameTime);
            }

            private static IEnumerable<Type> WithNested(Type type)
            {
                yield return type;
                foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                    foreach (var t in WithNested(nested))
                        yield return t;
            }

            private static bool UsesGameTime(MethodBase method)
            {
                try
                {
                    return PatchProcessor.ReadMethodBody(method).Any(i => Equals(i.Value, DeltaTime) || Equals(i.Value, WaitCtor) || Equals(i.Value, TimeScale));
                }
                catch
                {
                    return false;
                }
            }

            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                foreach (var ins in instructions)
                {
                    if (ins.opcode == OpCodes.Call && Equals(ins.operand, DeltaTime))
                        ins.operand = AccessTools.Method(typeof(SmoothSpeed), nameof(PopupDelta));
                    else if (ins.opcode == OpCodes.Call && Equals(ins.operand, TimeScale))
                        ins.operand = AccessTools.Method(typeof(SmoothSpeed), nameof(PopupTimeScale));
                    else if (ins.opcode == OpCodes.Newobj && Equals(ins.operand, WaitCtor))
                    {
                        ins.opcode = OpCodes.Call;
                        ins.operand = AccessTools.Method(typeof(SmoothSpeed), nameof(PopupWait));
                    }
                    yield return ins;
                }
            }
        }
    }

    // "EATEN: ..." is hidden by ClearEatInfoAfterDelay, whose 0.5 s game-time wait also releases the auto-eat
    // lock - so that timer must stay in game time or auto-eat would slow down at high speed. Instead, while fast,
    // the hide itself is put off for a moment of real time, unless newer eat info has replaced the text by then.
    [HarmonyPatch(typeof(HitPointsSkill), "ClearEatInfo")]
    internal static class EatInfoPatch
    {
        private const float ShowFor = 1.5f;
        private static readonly AccessTools.FieldRef<HitPointsSkill, TMPro.TMP_Text> Text =
            AccessTools.FieldRefAccess<HitPointsSkill, TMPro.TMP_Text>("eatInfoText");
        private static bool _passThrough;

        static bool Prefix(HitPointsSkill __instance)
        {
            if (_passThrough || !SmoothSpeed.Active || AutoGolden.Instance == null)
                return true;
            var text = Text(__instance);
            if (text == null)
                return true;
            AutoGolden.Instance.StartCoroutine(HideLater(__instance, text, text.text));
            return false;
        }

        private static System.Collections.IEnumerator HideLater(HitPointsSkill hp, TMPro.TMP_Text text, string shown)
        {
            yield return new WaitForSecondsRealtime(ShowFor);
            if (hp == null || text == null || text.text != shown)
                yield break;
            _passThrough = true;
            try { AccessTools.Method(typeof(HitPointsSkill), "ClearEatInfo").Invoke(hp, null); }
            finally { _passThrough = false; }
        }
    }

    // Switches the Bloob's and enemies' animators to real time while the game runs fast, and back afterwards.
    internal class SmoothAnimations : MonoBehaviour
    {
        private readonly HashSet<Animator> _switched = new HashSet<Animator>();
        private float _next;

        private void Update()
        {
            if (Time.unscaledTime < _next)
                return;
            _next = Time.unscaledTime + 0.5f;

            if (!SmoothSpeed.Active)
            {
                if (_switched.Count > 0)
                {
                    foreach (var a in _switched)
                        if (a != null)
                            a.updateMode = AnimatorUpdateMode.Normal;
                    _switched.Clear();
                }
                return;
            }

            Switch(Scene.Find<CharacterMovement>()?.animator);
            foreach (var enemy in FindObjectsOfType<BasicEnemy>())
                Switch(enemy.animator);
        }

        private void Switch(Animator a)
        {
            if (a == null || a.updateMode != AnimatorUpdateMode.Normal)
                return;
            a.updateMode = AnimatorUpdateMode.UnscaledTime;
            _switched.Add(a);
        }
    }
}
