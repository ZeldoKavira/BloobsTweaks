using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace BloobsTweaks
{
    // Crafting, firemaking, combat, tracking, devotion and dexterity courses run as coroutines that do one step
    // and then wait - WaitForSeconds(t), or a frame-by-frame countdown (remaining -= deltaTime; yield return null).
    // Unity resumes a coroutine at most once per frame, so at high game speeds, where one frame covers more game
    // time than a step takes, they manage one step per frame and the rest of the frame's time is lost.
    //
    // In those classes, coroutines are started under a small scheduler instead (StartCoroutine is redirected; the
    // Coroutine handle handed back is the scheduler's, so StopCoroutine / StopAllCoroutines still stop it):
    //  - WaitForSeconds(t) spends exactly t of the frame's game time, and the loop carries on in the same frame
    //    while time is left;
    //  - a plain "next frame" yield is given a slice of the frame's time (0.1 s at most) as its deltaTime, again
    //    repeatedly within the frame, so countdown loops finish items mid-frame and start the next;
    //  - anything else (nested coroutines, real-time waits) is passed to Unity unchanged.
    // At normal speed a frame is shorter than a slice, so this behaves exactly like Unity's own scheduling.
    [HarmonyPatch]
    internal static class CoroutineCatchUp
    {
        private const float SliceSeconds = 0.1f;
        private const int MaxStepsPerFrame = 200;
        private const float MaxCarry = 10f;

        private static readonly string[] Types =
        {
            "FiremakingSkill", "CookingSkill", "SmithingSkill", "CraftingSkill", "BowCraftingSkill", "ImbuingSkill",
            "HerbologySkill", "DevotionSkill", "TrackingSkill", "DexteritySkill", "CombatManager",
            "CookingUI", "SmithingUI", "FurnaceUI", "FoundryUI", "WorkbenchUI", "LoomUI", "WellUI", "HerbWellUI",
            "HerbCauldron", "BowCraftingUI", "ImbuingUI", "BarMaker", "UnfinishedPotionMaker", "SacrificeUI",
            "AutoImbuingController", "TrapsAndBeasts", "TrapsThrow", "AttractMoth", "BloobbirdTree",
            "AltarUI", "AltarArdentUI", "AltarConstrictionUI", "AltarRadianceUI", "AltarUIBlood", "AltarUIDevouring",
            "AltarUIDistruption", "AltarUIEntropy", "AltarUIFrost", "AltarUIOmni", "AltarUIPlasma", "AltarUIRift",
            "AltarUIVerdance", "AltarVoidUI",
        };

        private static readonly MethodInfo StartIE = AccessTools.Method(typeof(MonoBehaviour), nameof(MonoBehaviour.StartCoroutine), new[] { typeof(IEnumerator) });
        private static readonly MethodInfo DeltaTime = AccessTools.PropertyGetter(typeof(Time), nameof(Time.deltaTime));
        private static readonly FieldInfo WaitSeconds = AccessTools.Field(typeof(WaitForSeconds), "m_Seconds");

        static IEnumerable<MethodBase> TargetMethods()
        {
            var assembly = typeof(InventorySystem).Assembly;
            var names = new HashSet<string>(Types);
            var found = new List<MethodBase>();
            foreach (var type in assembly.GetTypes())
            {
                // Top-level classes in the list, plus every dexterity course; nested (compiler-generated) types
                // hold the coroutine bodies.
                var top = type;
                while (top.DeclaringType != null)
                    top = top.DeclaringType;
                if (!names.Contains(top.Name) && !(top.Name.EndsWith("Course") && typeof(MonoBehaviour).IsAssignableFrom(top)))
                    continue;
                foreach (var m in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (!m.IsAbstract && !m.ContainsGenericParameters && m.GetMethodBody() != null && Uses(m))
                        found.Add(m);
            }
            Plugin.Log.LogInfo($"Coroutine catch-up: patching {found.Count} methods");
            return found;
        }

        private static bool Uses(MethodInfo m)
        {
            try
            {
                return PatchProcessor.ReadMethodBody(m).Any(i => Equals(i.Value, StartIE) || Equals(i.Value, DeltaTime));
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
                if ((ins.opcode == OpCodes.Call || ins.opcode == OpCodes.Callvirt) && Equals(ins.operand, StartIE))
                {
                    ins.opcode = OpCodes.Call;
                    ins.operand = AccessTools.Method(typeof(CoroutineCatchUp), nameof(Start));
                }
                else if (ins.opcode == OpCodes.Call && Equals(ins.operand, DeltaTime))
                {
                    ins.operand = AccessTools.Method(typeof(CoroutineCatchUp), nameof(Delta));
                }
                yield return ins;
            }
        }

        // ---------------------------------------------------------------------------------------------------

        public static Coroutine Start(MonoBehaviour owner, IEnumerator routine)
        {
            if (!Plugin.SkillCatchUp.Value || routine == null)
                return owner.StartCoroutine(routine);
            return owner.StartCoroutine(Run(routine));
        }

        // The deltaTime a coroutine body sees: its slice while the scheduler is stepping it, else the real one.
        private static readonly Stack<float> Deltas = new Stack<float>();

        public static float Delta() => Deltas.Count > 0 ? Deltas.Peek() : Time.deltaTime;

        private static bool Step(IEnumerator inner, float delta)
        {
            Deltas.Push(delta);
            try { return inner.MoveNext(); }
            finally { Deltas.Pop(); }
        }

        private static IEnumerator Run(IEnumerator inner)
        {
            // Like Unity, run the body up to its first yield straight away.
            if (!Step(inner, Time.deltaTime))
                yield break;

            var budget = 0f;   // game time of the current frame not yet spent
            var steps = 0;     // steps taken in the current frame
            while (true)
            {
                var y = inner.Current;

                if (y != null && !(y is WaitForSeconds))
                {
                    // Nested coroutine, real-time wait, custom instruction: Unity's job.
                    yield return y;
                    budget = 0f;
                    steps = 0;
                    if (!Step(inner, Time.deltaTime))
                        yield break;
                    continue;
                }

                float delta;
                if (y is WaitForSeconds wait)
                {
                    var remaining = WaitSeconds != null ? (float)WaitSeconds.GetValue(wait) : 0f;
                    while (budget < remaining)
                    {
                        remaining -= budget;
                        yield return null;
                        budget = Time.deltaTime;
                        steps = 0;
                    }
                    budget -= remaining;
                    delta = Mathf.Min(Time.deltaTime, SliceSeconds);
                }
                else
                {
                    // "Next frame": hand out a slice of this frame's remaining time, or wait for the next frame.
                    if (budget <= 0.0001f)
                    {
                        yield return null;
                        budget = Time.deltaTime;
                        steps = 0;
                    }
                    delta = Mathf.Min(budget, SliceSeconds);
                    budget -= delta;
                }

                if (++steps > MaxStepsPerFrame)
                {
                    // Runaway guard: finish the frame and carry a bounded amount of time over.
                    var carry = Mathf.Min(budget, MaxCarry);
                    yield return null;
                    budget = carry + Time.deltaTime;
                    steps = 0;
                }

                if (!Step(inner, delta))
                    yield break;
            }
        }
    }
}
