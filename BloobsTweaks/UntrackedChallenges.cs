using System;
using System.Collections.Generic;
using HarmonyLib;

namespace BloobsTweaks
{
    // Normally an action only counts toward tracked challenges (plus, with the Passive Progress reward, one
    // untracked challenge when nothing tracked matched). Once the player owns Auto Navigate + Start and has
    // Auto Progress switched on, this also counts it toward every untracked challenge the action matches that
    // sits at a tier Auto Progress would run on its own (tier index <= Auto Progress level), or is in repeatable
    // mode, which the game auto-enters at any level. Tier completion then goes through the game's own
    // CompleteTier, which rolls into the next tier exactly when Auto Progress would have and otherwise stops -
    // so untracked challenges end up where tracked ones would.
    [HarmonyPatch(typeof(ChallengeManager), "AddProgressInternal")]
    internal static class UntrackedChallenges
    {
        private delegate bool ApplyProgress(ChallengeManager manager, ChallengeData challenge, double amount,
            ref bool anyChanged, out double appliedAmount, bool keepTracked);

        private static readonly Func<ChallengeManager, ChallengeData, ChallengeType, string, string, string, bool> MatchesCall =
            AccessTools.MethodDelegate<Func<ChallengeManager, ChallengeData, ChallengeType, string, string, string, bool>>(
                AccessTools.Method(typeof(ChallengeManager), "MatchesProgressCall"));

        private static readonly ApplyProgress Apply =
            AccessTools.MethodDelegate<ApplyProgress>(AccessTools.Method(typeof(ChallengeManager), "TryApplyProgressAmount"));

        private static readonly Action<ChallengeManager, ChallengeData> NotifyProgress =
            AccessTools.MethodDelegate<Action<ChallengeManager, ChallengeData>>(
                AccessTools.Method(typeof(ChallengeManager), "NotifyChallengeProgressChanged"));

        private static readonly Action<ChallengeManager> NotifyChanged =
            AccessTools.MethodDelegate<Action<ChallengeManager>>(
                AccessTools.Method(typeof(ChallengeManager), "NotifyChallengesChanged"));

        private static readonly AccessTools.FieldRef<ChallengeManager, ChallengeData[]> Catalogue =
            AccessTools.FieldRefAccess<ChallengeManager, ChallengeData[]>("allChallengesData");

        internal static int Applied;

        internal struct Candidate
        {
            public ChallengeData Challenge;
            public int Tier;
            public int Repeats;
            public double Progress;
        }

        internal static bool Unlocked =>
            ChallengeAutoNavigateStartUpgrade.Instance != null && ChallengeAutoNavigateStartUpgrade.Instance.IsPurchased &&
            ChallengeAutoProgressUpgrade.Instance != null && ChallengeAutoProgressUpgrade.Instance.AutoProgressEnabled &&
            ChallengeAutoProgressUpgrade.Instance.AutoProgressLevel > 0;

        private static bool Excluded(ChallengeType type) =>
            type == ChallengeType.Gameplay || type == ChallengeType.Lore || type == ChallengeType.Explore;

        // Pick the candidates before the game runs, so we can tell afterwards which ones it already advanced
        // itself (its own Passive Progress pick) and not count the action twice.
        static void Prefix(ChallengeManager __instance, ChallengeType type, string target, double amount, string skill,
            string category, out List<Candidate> __state)
        {
            __state = null;
            if (!Plugin.UntrackedChallengesEnabled.Value || !Unlocked || Excluded(type) || !(amount > 0) || double.IsInfinity(amount))
                return;

            var all = Catalogue(__instance);
            if (all == null)
                return;

            var autoLevel = ChallengeAutoProgressUpgrade.Instance.AutoProgressLevel;
            var player = playerDataManager.Instance;
            foreach (var challenge in all)
            {
                if (challenge == null || string.IsNullOrEmpty(challenge.challengeId) || Excluded(challenge.type))
                    continue;
                if (!MatchesCall(__instance, challenge, type, target, skill, category))
                    continue;
                var id = challenge.challengeId;
                if (__instance.IsTracked(id))
                    continue; // the game handles tracked ones
                // Repeatable mode (all tiers done) is always auto-entered, whatever the Auto Progress level, so it
                // always counts. Otherwise the current tier must be one Auto Progress covers.
                var repeatable = __instance.IsRepeatable(id);
                var tier = __instance.GetCurrentTierIndex(id);
                if (!repeatable && (tier > autoLevel || tier >= challenge.TierCount))
                    continue;
                if (player != null && !ChallengeUIManager.MeetsLevelRequirement(player, challenge))
                    continue;

                (__state ??= new List<Candidate>()).Add(new Candidate { Challenge = challenge, Tier = tier, Repeats = __instance.GetRepeatCount(id), Progress = __instance.GetProgress(id) });
            }
        }

        static void Postfix(ChallengeManager __instance, double amount, List<Candidate> __state)
        {
            if (__state == null)
                return;

            var anyChanged = false;
            foreach (var c in __state)
            {
                var id = c.Challenge.challengeId;
                if (__instance.IsTracked(id) || __instance.GetCurrentTierIndex(id) != c.Tier ||
                    __instance.GetRepeatCount(id) != c.Repeats || __instance.GetProgress(id) != c.Progress)
                    continue; // the game already counted this action here

                try
                {
                    if (Apply(__instance, c.Challenge, amount, ref anyChanged, out _, false))
                    {
                        Applied++;
                        NotifyProgress(__instance, c.Challenge);
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Untracked progress for challenge {id} failed: {e.Message}");
                }
            }

            if (anyChanged)
                NotifyChanged(__instance);
        }
    }
}
