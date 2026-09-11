using HarmonyLib;
using StarVortex;
using UnityEngine;

/// <summary>
/// Guard the mutation entry point itself, not only UpgradeClassDisplay's button
/// state. SciencePanel.BuyClass consumes the class-unlock item before rebuilding
/// UI, so a bypassed/externally-invoked buy must fail before any side effects.
/// </summary>
[HarmonyPatch(typeof(SciencePanel), nameof(SciencePanel.BuyClass))]
public static class CoreCustomClassBuyMutationGuardPatch
{
    public static bool Prefix(
        Upgrade.Category category,
        GameShip ___ship)
    {
        if (___ship == null || ___ship.pilot == null)
            return true;

        string reason;
        if (CoreClassProgressionRules.CanUnlockClass(
                ___ship.pilot,
                category,
                out reason))
        {
            return true;
        }

        Debug.LogWarning(
            "[CoreClassProgression] Blocked custom class purchase before " +
            "consuming its unlock item: " + reason);
        return false;
    }
}

/// <summary>
/// Fail-safe for direct/test progression mutations that skip SciencePanel.
/// Normal rank increases inside an already-owned class remain untouched.
/// </summary>
[HarmonyPatch(typeof(Pilot), nameof(Pilot.SetUpgrade))]
public static class CoreCustomClassDirectProgressionActivationGuardPatch
{
    public static bool Prefix(
        Pilot __instance,
        Upgrade.Key key,
        int level)
    {
        if (__instance == null || level <= 0)
            return true;

        ICoreSpecializationPolicy specialization;
        ICoreProgressionRankPolicy progression;
        if (!CoreClassProgressionRules.TryGetProgressionPolicy(
                key,
                out specialization,
                out progression))
        {
            return true;
        }

        // If this class is already owned, this is an ordinary progression rank
        // change and mutual-exclusion was settled when the class was bought.
        bool owned = __instance.unlockedSecondaryClasses != null &&
            __instance.unlockedSecondaryClasses.Contains(
                progression.ClassCategory);
        if (owned)
            return true;

        string reason;
        if (CoreClassProgressionRules.CanUnlockClass(
                __instance,
                progression.ClassCategory,
                out reason))
        {
            return true;
        }

        Debug.LogWarning(
            "[CoreClassProgression] Blocked direct custom class activation: " +
            reason);
        return false;
    }
}
