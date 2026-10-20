using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace BloobsTweaks
{
    // The game lets homesteading take seeds, ranch eggs, swarm eggs and decoration costs from the bank only from
    // homestead house tier 7. Each of those gates reads HomesteadTierSwitcher.highestPurchasedTier and compares it
    // to 7; inside just those methods we make that read report at least 7, so the bank is used at any tier. The
    // real tier - and everything else it unlocks - is untouched. The game's own "use seeds from bank" and "save
    // one seed in bank" options still apply.
    [HarmonyPatch]
    internal static class HomesteadBankUnlock
    {
        private const int BankTier = 7;

        private static readonly (string Type, string Method)[] Gates =
        {
            ("CropGrowth", "CanUseBankSeeds"),
            ("RanchEggSlot", "CanUseBankSeeds"),
            ("RanchCaptureController", "GetAvailableSeedCount"),
            ("HomesteadingUIFishingSwarm", "CanUseBankEggs"),
            ("HomesteadingSeedAvailability", "Read"),
            ("AutoHomesteadingController", "HasSeedsFor"),
            ("AutoHomesteadingController", "HasSeedsForRanch"),
            ("HomesteadingDecorationCostPayment", "CanUseBank"),
        };

        private static readonly FieldInfo Tier = AccessTools.Field(typeof(HomesteadTierSwitcher), nameof(HomesteadTierSwitcher.highestPurchasedTier));

        static IEnumerable<MethodBase> TargetMethods()
        {
            var assembly = typeof(InventorySystem).Assembly;
            foreach (var (typeName, methodName) in Gates)
            {
                var type = assembly.GetType(typeName);
                var method = type != null ? AccessTools.Method(type, methodName) : null;
                if (method == null)
                    Plugin.Log.LogWarning($"Homestead bank unlock: {typeName}.{methodName} not found");
                else
                    yield return method;
            }
        }

        public static int EffectiveTier(HomesteadTierSwitcher switcher)
        {
            var real = switcher.highestPurchasedTier;
            return Plugin.HomesteadBankAnyTier.Value && real < BankTier ? BankTier : real;
        }

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var replacement = AccessTools.Method(typeof(HomesteadBankUnlock), nameof(EffectiveTier));
            foreach (var ins in instructions)
            {
                // ldfld (instance on the stack) -> call EffectiveTier(instance): same stack shape.
                if (ins.opcode == OpCodes.Ldfld && Equals(ins.operand, Tier))
                {
                    ins.opcode = OpCodes.Call;
                    ins.operand = replacement;
                }
                yield return ins;
            }
        }
    }
}
