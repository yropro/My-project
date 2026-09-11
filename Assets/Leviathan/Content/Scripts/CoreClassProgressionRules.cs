using HarmonyLib;
using StarVortex;
using System;

/// <summary>
/// Optional contract for a custom class whose specialization-point budget is
/// granted by ranks in one native Upgrade. This keeps class rank/refund rules
/// generic without putting class-specific keys into Core.
/// </summary>
public interface ICoreProgressionRankPolicy
{
    Upgrade.Key ProgressionUpgradeKey { get; }
    Upgrade.Category ClassCategory { get; }

    /// <summary>
    /// Return the total specialization points available if this progression
    /// upgrade were exactly progressionRank.
    /// </summary>
    int GetGrantedPointsForProgressionRank(Pilot pilot, int progressionRank);
}

/// <summary>
/// Shared invariants around mutually-exclusive custom classes and the native
/// progression ranks that fund their Core specialization trees.
/// </summary>
public static class CoreClassProgressionRules
{
    public static bool TryGetProgressionPolicy(
        Upgrade.Key key,
        out ICoreSpecializationPolicy specializationPolicy,
        out ICoreProgressionRankPolicy progressionPolicy)
    {
        specializationPolicy = null;
        progressionPolicy = null;

        var policies = CoreSpecializationPolicies.All();
        for (int i = 0; i < policies.Count; i++)
        {
            ICoreProgressionRankPolicy candidate =
                policies[i] as ICoreProgressionRankPolicy;
            if (candidate == null || candidate.ProgressionUpgradeKey != key)
                continue;

            specializationPolicy = policies[i];
            progressionPolicy = candidate;
            return true;
        }

        return false;
    }

    public static bool TryGetClassPolicy(
        Upgrade.Category category,
        out ICoreSpecializationPolicy specializationPolicy,
        out ICoreProgressionRankPolicy progressionPolicy)
    {
        specializationPolicy = null;
        progressionPolicy = null;

        var policies = CoreSpecializationPolicies.All();
        for (int i = 0; i < policies.Count; i++)
        {
            ICoreProgressionRankPolicy candidate =
                policies[i] as ICoreProgressionRankPolicy;
            if (candidate == null || candidate.ClassCategory != category)
                continue;

            specializationPolicy = policies[i];
            progressionPolicy = candidate;
            return true;
        }

        return false;
    }

    /// <summary>
    /// A progression rank may only be reduced when the resulting point budget
    /// still covers every currently-invested node in that class.
    /// </summary>
    public static bool CanSetProgressionRank(
        Pilot pilot,
        Upgrade.Key key,
        int proposedRank,
        out string reason)
    {
        reason = string.Empty;
        if (pilot == null || proposedRank < 0)
            return true;

        ICoreSpecializationPolicy policy;
        ICoreProgressionRankPolicy progression;
        if (!TryGetProgressionPolicy(key, out policy, out progression))
            return true;

        int currentRank = pilot.GetUpgradeLevel(key);
        if (proposedRank >= currentRank)
            return true;

        int invested = CoreSpecializationRuntime.GetSpentPointsForClass(
            pilot,
            policy.ClassId);
        int granted = Math.Max(
            0,
            progression.GetGrantedPointsForProgressionRank(
                pilot,
                proposedRank));

        if (invested <= granted)
            return true;

        reason =
            "Refund specialization nodes first. Rank " +
            proposedRank.ToString() + " would grant " +
            granted.ToString() + " " + policy.PointCurrencyName +
            ", but " + invested.ToString() + " are currently invested.";
        return false;
    }

    /// <summary>
    /// Custom standalone classes are mutually exclusive at the native class
    /// ownership level. A class must be fully refunded before another can be
    /// bought. Re-asserting the category already owned is harmless.
    /// </summary>
    public static bool CanUnlockClass(
        Pilot pilot,
        Upgrade.Category requestedCategory,
        out string reason)
    {
        reason = string.Empty;
        if (pilot == null)
            return true;

        ICoreSpecializationPolicy requestedPolicy;
        ICoreProgressionRankPolicy requestedProgression;
        if (!TryGetClassPolicy(
                requestedCategory,
                out requestedPolicy,
                out requestedProgression))
        {
            return true;
        }

        if (IsClassPresent(pilot, requestedProgression))
            return true;

        var policies = CoreSpecializationPolicies.All();
        for (int i = 0; i < policies.Count; i++)
        {
            ICoreProgressionRankPolicy candidate =
                policies[i] as ICoreProgressionRankPolicy;
            if (candidate == null ||
                candidate.ClassCategory == requestedCategory)
            {
                continue;
            }

            if (!IsClassPresent(pilot, candidate))
                continue;

            reason =
                "Custom classes are mutually exclusive. Refund " +
                policies[i].ProgressionName +
                " before unlocking " + requestedPolicy.ProgressionName + ".";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Core's specialization state must be empty and the class progression rank
    /// must be zero before native class refund is allowed. Native Star Vortex
    /// still enforces its own remaining-upgrades-in-category rule as well.
    /// </summary>
    public static bool CanRefundClass(
        Pilot pilot,
        Upgrade.Category category,
        out string reason)
    {
        reason = string.Empty;
        if (pilot == null)
            return true;

        ICoreSpecializationPolicy policy;
        ICoreProgressionRankPolicy progression;
        if (!TryGetClassPolicy(category, out policy, out progression))
            return true;

        int invested = CoreSpecializationRuntime.GetSpentPointsForClass(
            pilot,
            policy.ClassId);
        if (invested > 0)
        {
            reason =
                "Refund all " + policy.PointCurrencyName +
                " nodes before refunding this class.";
            return false;
        }

        int rank = pilot.GetUpgradeLevel(progression.ProgressionUpgradeKey);
        if (rank > 0)
        {
            reason =
                "Reduce " + policy.ProgressionName +
                " to rank 0 before refunding this class.";
            return false;
        }

        return true;
    }

    private static bool IsClassPresent(
        Pilot pilot,
        ICoreProgressionRankPolicy progression)
    {
        if (pilot == null || progression == null)
            return false;

        if (pilot.unlockedSecondaryClasses != null &&
            pilot.unlockedSecondaryClasses.Contains(progression.ClassCategory))
        {
            return true;
        }

        // Also treat a positive progression rank as owned. This protects old or
        // temporarily inconsistent saves without depending on Star Vortex's
        // non-public HasUpgradeInCategory helper.
        return pilot.GetUpgradeLevel(progression.ProgressionUpgradeKey) > 0;
    }
}

// -----------------------------------------------------------------------------
// Native UI + authoritative mutation guards
// -----------------------------------------------------------------------------

// These native methods are intentionally patched by string name. Existing
// Leviathan integration accesses them reflectively, so Core must not require
// them to be public merely to compile.
[HarmonyPatch(typeof(SciencePanel), "CanDowngradeTooltip")]
public static class CoreScienceProgressionDowngradePatch
{
    public static void Postfix(
        Upgrade __0,
        GameShip ___ship,
        ref string __result)
    {
        if (__result != null || __0 == null || ___ship == null || ___ship.pilot == null)
            return;

        int current = ___ship.pilot.GetUpgradeLevel(__0.key);
        if (current <= 0)
            return;

        string reason;
        if (!CoreClassProgressionRules.CanSetProgressionRank(
                ___ship.pilot,
                __0.key,
                current - 1,
                out reason))
        {
            __result = reason;
        }
    }
}

[HarmonyPatch(typeof(CoreUpgrades), "CanDowngradeTooltip")]
public static class CoreUpgradesProgressionDowngradePatch
{
    public static void Postfix(
        Upgrade __0,
        GameShip ___ship,
        ref string __result)
    {
        if (__result != null || __0 == null || ___ship == null || ___ship.pilot == null)
            return;

        int current = ___ship.pilot.GetUpgradeLevel(__0.key);
        if (current <= 0)
            return;

        string reason;
        if (!CoreClassProgressionRules.CanSetProgressionRank(
                ___ship.pilot,
                __0.key,
                current - 1,
                out reason))
        {
            __result = reason;
        }
    }
}

[HarmonyPatch(typeof(Pilot), "SetUpgrade")]
public static class CoreProgressionRankMutationGuardPatch
{
    public static bool Prefix(
        Pilot __instance,
        Upgrade.Key __0,
        int __1)
    {
        if (__instance == null)
            return true;

        string reason;
        if (!CoreClassProgressionRules.CanSetProgressionRank(
                __instance,
                __0,
                __1,
                out reason))
        {
            UnityEngine.Debug.LogWarning(
                "[CoreClassProgression] Blocked progression downgrade: " + reason);
            return false;
        }

        ICoreSpecializationPolicy policy;
        ICoreProgressionRankPolicy progression;
        if (__1 > __instance.GetUpgradeLevel(__0) &&
            CoreClassProgressionRules.TryGetProgressionPolicy(
                __0,
                out policy,
                out progression) &&
            !CoreClassProgressionRules.CanUnlockClass(
                __instance,
                progression.ClassCategory,
                out reason))
        {
            UnityEngine.Debug.LogWarning(
                "[CoreClassProgression] Blocked progression rank for mutually-exclusive class: " +
                reason);
            return false;
        }

        return true;
    }
}

[HarmonyPatch(typeof(Pilot), "UnlockSecondaryClass")]
public static class CoreMutuallyExclusiveClassUnlockPatch
{
    public static bool Prefix(Pilot __instance, Upgrade.Category __0)
    {
        string reason;
        if (CoreClassProgressionRules.CanUnlockClass(
                __instance,
                __0,
                out reason))
        {
            return true;
        }

        UnityEngine.Debug.LogWarning(
            "[CoreClassProgression] Blocked custom class unlock: " + reason);
        return false;
    }
}

[HarmonyPatch(typeof(Pilot), "RemoveSecondaryClass")]
public static class CoreClassRefundGuardPatch
{
    public static bool Prefix(
        Pilot __instance,
        Upgrade.Category __0,
        ref bool __result)
    {
        string reason;
        if (CoreClassProgressionRules.CanRefundClass(
                __instance,
                __0,
                out reason))
        {
            return true;
        }

        UnityEngine.Debug.LogWarning(
            "[CoreClassProgression] Blocked custom class refund: " + reason);
        __result = false;
        return false;
    }
}

[HarmonyPatch(typeof(UpgradeClassDisplay), "ShowBuyButton")]
public static class CoreClassBuyButtonGuardPatch
{
    public static void Prefix(
        Upgrade.Category ___category,
        GameShip ___ship,
        ref bool __0)
    {
        if (!__0 || ___ship == null || ___ship.pilot == null)
            return;

        string reason;
        if (!CoreClassProgressionRules.CanUnlockClass(
                ___ship.pilot,
                ___category,
                out reason))
        {
            __0 = false;
        }
    }
}

[HarmonyPatch(typeof(UpgradeClassDisplay), "ShowSellButton")]
public static class CoreClassSellButtonGuardPatch
{
    public static void Prefix(
        Upgrade.Category ___category,
        GameShip ___ship,
        ref bool __0)
    {
        if (!__0 || ___ship == null || ___ship.pilot == null)
            return;

        string reason;
        if (!CoreClassProgressionRules.CanRefundClass(
                ___ship.pilot,
                ___category,
                out reason))
        {
            __0 = false;
        }
    }
}
