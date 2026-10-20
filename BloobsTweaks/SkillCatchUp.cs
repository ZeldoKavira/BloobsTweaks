using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace BloobsTweaks
{
    // The gathering skills and thieving all time their actions the same way:
    //     timer += deltaTime; if (timer >= speed) { DoAction(); timer = 0; }
    // so they manage at most one action per frame and throw the rest of the frame's time away. At high game
    // speeds a frame covers several actions' worth of time (200x at 60 fps is 3.3 s a frame against a ~1.5 s
    // swing), which made mining & co. run far slower than the speed setting. After the game's own action, we run
    // the actions still owed for that frame and keep the leftover time, stopping as soon as the target changes
    // or can no longer be worked - so a node that runs out is handled exactly once, as before.
    [HarmonyPatch]
    internal static class SkillCatchUp
    {
        private const int MaxExtraPerFrame = 50;

        private class Spec
        {
            public Type Skill;
            public string Timer, Speed, Action, Target;
            public Type Node;
            public string Workable;
            public FieldInfo TimerF, SpeedF, TargetF, SwarmF;
            public MethodInfo ActionM;
            public MemberInfo WorkableM;
        }

        private static readonly Spec[] Specs =
        {
            new Spec { Skill = typeof(WoodcuttingSkill), Timer = "chopTimer", Speed = "chopSpeed", Action = "PerformCuttingAction", Target = "targetTree", Node = typeof(Trees), Workable = "IsChoppable" },
            new Spec { Skill = typeof(MiningSkill), Timer = "chopTimer", Speed = "MiningSpeed", Action = "PerformMiningAction", Target = "targetore", Node = typeof(Ores), Workable = "isMineAble" },
            new Spec { Skill = typeof(FishingSkill), Timer = "chopTimer", Speed = "fishSpeed", Action = "PerformFishingAction", Target = "targetFish", Node = typeof(FishingSpots), Workable = "IsChoppable" },
            new Spec { Skill = typeof(ForagingSkill), Timer = "chopTimer", Speed = "ForagingSpeed", Action = "PerformForagingAction", Target = "targetBush", Node = typeof(Bushes), Workable = "IsForageAble" },
            new Spec { Skill = typeof(ThievingSkill), Timer = "thieveTimer", Speed = "ThievingSpeed", Action = "PerformThievingAction", Target = "targetore", Node = typeof(Chests), Workable = "isThievAble" },
        };

        private static readonly Dictionary<Type, Spec> BySkill = new Dictionary<Type, Spec>();

        static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var s in Specs)
            {
                s.TimerF = AccessTools.Field(s.Skill, s.Timer);
                s.SpeedF = AccessTools.Field(s.Skill, s.Speed);
                s.TargetF = AccessTools.Field(s.Skill, s.Target);
                s.ActionM = AccessTools.Method(s.Skill, s.Action);
                s.SwarmF = s.Skill == typeof(FishingSkill) ? AccessTools.Field(s.Skill, "isFishingSwarm") : null;
                const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                // Plain reflection: AccessTools logs a warning for every miss, and these are a property on some nodes, a field on others.
                s.WorkableM = (MemberInfo)s.Node.GetProperty(s.Workable, any) ?? s.Node.GetField(s.Workable, any);
                var update = AccessTools.Method(s.Skill, "Update");
                if (s.TimerF == null || s.SpeedF == null || s.TargetF == null || s.ActionM == null || s.WorkableM == null || update == null)
                {
                    Plugin.Log.LogWarning($"Skill catch-up: {s.Skill.Name} doesn't look as expected; skipped");
                    continue;
                }
                BySkill[s.Skill] = s;
                yield return update;
            }
        }

        // Time the timer will hold after this frame's deltaTime is added, before the game's own check.
        static void Prefix(MonoBehaviour __instance, out float __state)
        {
            __state = -1f;
            if (Plugin.SkillCatchUp.Value && BySkill.TryGetValue(__instance.GetType(), out var s))
                __state = (float)s.TimerF.GetValue(__instance) + Time.deltaTime;
        }

        static void Postfix(MonoBehaviour __instance, float __state)
        {
            if (__state < 0f || !BySkill.TryGetValue(__instance.GetType(), out var s))
                return;
            var speed = (float)s.SpeedF.GetValue(__instance);
            // Only when the game itself acted this frame (it zeroed the timer) and more time is owed.
            if (speed <= 0f || __state < 2f * speed || (float)s.TimerF.GetValue(__instance) != 0f)
                return;
            if (s.SwarmF != null && (bool)s.SwarmF.GetValue(__instance))
                return; // fishing swarms run their own progress logic

            var owed = Mathf.Min((int)(__state / speed) - 1, MaxExtraPerFrame);
            var target = s.TargetF.GetValue(__instance) as Transform;
            var done = 0;
            try
            {
                for (; done < owed; done++)
                {
                    if (target == null || !(s.TargetF.GetValue(__instance) as Transform == target) || !Workable(s, target))
                        break;
                    s.ActionM.Invoke(__instance, null);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Skill catch-up for {s.Skill.Name} failed: {e.InnerException?.Message ?? e.Message}");
            }
            // Keep the remainder for next frame, as an accumulating timer would.
            s.TimerF.SetValue(__instance, Mathf.Max(0f, __state - (done + 1) * speed) % speed);
        }

        private static bool Workable(Spec s, Transform target)
        {
            var node = target.GetComponent(s.Node);
            if (node == null)
                return false;
            var value = s.WorkableM is PropertyInfo p ? p.GetValue(node) : ((FieldInfo)s.WorkableM).GetValue(node);
            return value is bool b && b;
        }
    }
}
