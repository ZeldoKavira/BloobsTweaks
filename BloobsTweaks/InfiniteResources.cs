using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace BloobsTweaks
{
    // Trees, rocks, bushes and fishing spots never run out. Every reward they give - items, XP, random and seed
    // drops, golden-glow rolls, challenge progress - is paid per 100 damage dealt, not when the node is used up;
    // running out only means a stump, a respawn wait, a walk to the next node, and cancelling any golden glow in
    // progress. So before each hit we top a node's health up so it can't reach zero, and the per-100-damage
    // rewards carry on exactly as before.
    // Thieving chests are deliberately left out: their golden-glow chance is rolled only when a chest respawns,
    // so a chest that never empties would never turn golden.
    [HarmonyPatch]
    internal static class InfiniteResources
    {
        private static readonly Type[] Nodes = { typeof(Trees), typeof(Ores), typeof(Bushes), typeof(FishingSpots) };

        private class NodeFields
        {
            public FieldInfo Health, BaseHealth, DoubleDamage;
        }

        private static readonly Dictionary<Type, NodeFields> Fields = new Dictionary<Type, NodeFields>();

        static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var type in Nodes)
            {
                var method = AccessTools.Method(type, "ReduceHealth", new[] { typeof(int) });
                var fields = new NodeFields
                {
                    Health = AccessTools.Field(type, "health"),
                    BaseHealth = AccessTools.Field(type, "baseHealth"),
                    DoubleDamage = AccessTools.Field(type, "takesDoubleDamage"),
                };
                if (method == null || fields.Health == null || fields.BaseHealth == null)
                {
                    Plugin.Log.LogWarning($"Infinite resources: {type.Name} doesn't look as expected; skipped");
                    continue;
                }
                Fields[type] = fields;
                yield return method;
            }
        }

        static void Prefix(object __instance, int damage)
        {
            if (!Plugin.InfiniteResources.Value || !Fields.TryGetValue(__instance.GetType(), out var f))
                return;
            var health = (int)f.Health.GetValue(__instance);
            if (health <= 0)
                return; // already used up (respawning) before the setting was turned on
            var hit = damage * (f.DoubleDamage != null && (bool)f.DoubleDamage.GetValue(__instance) ? 2 : 1);
            var keep = Math.Max((int)f.BaseHealth.GetValue(__instance), hit * 2 + 100);
            if (health < keep)
                f.Health.SetValue(__instance, keep);
        }
    }
}
