using HarmonyLib;
using StarVortex;
using UnityEngine;

/// <summary>
/// Guard the mutation entry point itself, not only UpgradeClassDisplay's button
/// state. SciencePanel.BuyClass consumes the class-unlock item before rebuilding
/// UI, so a bypassed/externally-invoked buy must fail before any side effects.
/// CoreClassProgressionRules owns the actual mutual-exclusion decision.
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
