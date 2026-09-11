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

/// <summary>
/// Native full-upgrade resets must also clear the currently effective custom
/// class specialization build before native ranks disappear. This is Core-owned
/// because the same rule applies to every registered standalone custom class.
/// </summary>
[HarmonyPatch(typeof(Pilot), "ResetUpgrades")]
public static class CoreSpecializationResetWithNativePatch
{
    public static void Prefix(Pilot __instance)
    {
        if (__instance == null)
            return;

        CoreClassId classId = CoreSpecializationRuntime.GetEffectiveClass(__instance);
        if (classId == CoreClassId.None)
            return;

        string reason;
        CoreSpecializationRuntime.ResetClassBuild(
            __instance,
            classId,
            out reason);

        if (!string.IsNullOrEmpty(reason))
        {
            Debug.LogWarning(
                "[CoreClassProgression] Could not persist specialization reset: " +
                reason);
        }
    }
}
