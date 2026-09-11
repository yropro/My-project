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
    /// Return the total specialization points that would be available if this
    /// progression upgrade were exactly progressionRank. The current Pilot is
    /// provided so future classes may use other stable Pilot facts if needed.
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
    /// ownership level. A class must be refunded before another can be bought.
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

        // Re-asserting the category already held by this Pilot is harmless.
        if (IsClassPresent(pilot, requestedCategory))
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

            if (!IsClassPresent(pilot, candidate.ClassCategory))
                continue;

            reason =
                "Custom classes are mutually exclusive. Refund " +
                policies[i].ProgressionName +
                " before unlocking " + requestedPolicy.ProgressionName + ".";
            return false;
        }

        return true;
    }

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
        Upgrade.Category category)
    {
        if (pilot == null)
            return false;

        if (pilot.unlockedSecondaryClasses != null &&
            pilot.unlockedSecondaryClasses.Contains(category))
        {
            return true;
        }

        return pilot.HasUpgradeInCategory(category);
    }
}

// -----------------------------------------------------------------------------
// Native UI + authoritative mutation guards
// -----------------------------------------------------------------------------

[HarmonyPatch(typeof(SciencePanel), nameof(SciencePanel.CanDowngradeTooltip))]
public static class CoreScienceProgressionDowngradePatch
{
    public static void Postfix(
        Upgrade upgrade,
        GameShip ___ship,
        ref string __result)
    {
        if (__result != null || upgrade == null || ___ship == null)
            return;

        int current = ___ship.pilot.GetUpgradeLevel(upgrade.key);
        if (current <= 0)
            return;

        string reason;
        if (!CoreClassProgressionRules.CanSetProgressionRank(
                ___ship.pilot,
                upgrade.key,
                current - 1,
                out reason))
        {
            __result = reason;
        }
    }
}

[HarmonyPatch(typeof(CoreUpgrades), nameof(CoreUpgrades.CanDowngradeTooltip))]
public static class CoreUpgradesProgressionDowngradePatch
{
    public static void Postfix(
        Upgrade upgrade,
        GameShip ___ship,
        ref string __result)
    {
        if (__result != null || upgrade == null || ___ship == null)
            return;

        int current = ___ship.pilot.GetUpgradeLevel(upgrade.key);
        if (current <= 0)
            return;

        string reason;
        if (!CoreClassProgressionRules.CanSetProgressionRank(
                ___ship.pilot,
                upgrade.key,
                current - 1,
                out reason))
        {
            __result = reason;
        }
    }
}

[HarmonyPatch(typeof(Pilot), nameof(Pilot.SetUpgrade))]
public static class CoreProgressionRankMutationGuardPatch
{
    public static bool Prefix(Pilot __instance, Upgrade.Key key, int level)
    {
        string reason;
        if (CoreClassProgressionRules.CanSetProgressionRank(
                __instance,
                key,
                level,
                out reason))
        {
            return true;
        }

        UnityEngine.Debug.LogWarning(
            "[CoreClassProgression] Blocked progression downgrade: " + reason);
        return false;
    }
}

[HarmonyPatch(typeof(Pilot), nameof(Pilot.UnlockSecondaryClass))]
public static class CoreMutuallyExclusiveClassUnlockPatch
{
    public static bool Prefix(Pilot __instance, Upgrade.Category category)
    {
        string reason;
        if (CoreClassProgressionRules.CanUnlockClass(
                __instance,
                category,
                out reason))
        {
            return true;
        }

        UnityEngine.Debug.LogWarning(
            "[CoreClassProgression] Blocked custom class unlock: " + reason);
        return false;
    }
}

[HarmonyPatch(typeof(Pilot), nameof(Pilot.RemoveSecondaryClass))]
public static class CoreClassRefundGuardPatch
{
    public static bool Prefix(
        Pilot __instance,
        Upgrade.Category category,
        ref bool __result)
    {
        string reason;
        if (CoreClassProgressionRules.CanRefundClass(
                __instance,
                category,
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

[HarmonyPatch(typeof(UpgradeClassDisplay), nameof(UpgradeClassDisplay.ShowBuyButton))]
public static class CoreClassBuyButtonGuardPatch
{
    public static void Prefix(
        Upgrade.Category ___category,
        GameShip ___ship,
        ref bool interactable)
    {
        if (!interactable || ___ship == null)
            return;

        string reason;
        if (!CoreClassProgressionRules.CanUnlockClass(
                ___ship.pilot,
                ___category,
                out reason))
        {
            interactable = false;
        }
    }
}

[HarmonyPatch(typeof(UpgradeClassDisplay), nameof(UpgradeClassDisplay.ShowSellButton))]
public static class CoreClassSellButtonGuardPatch
{
    public static void Prefix(
        Upgrade.Category ___category,
        GameShip ___ship,
        ref bool interactable)
    {
        if (!interactable || ___ship == null)
            return;

        string reason;
        if (!CoreClassProgressionRules.CanRefundClass(
                ___ship.pilot,
                ___category,
                out reason))
        {
            interactable = false;
        }
    }
}
