using HarmonyLib;
using StarVortex;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Small registry for native progression upgrades that back standalone custom
/// classes. The native secondary-class unlock remains the class ownership
/// boundary; progression rank controls the active runtime and grants the
/// class's external specialization-point budget.
/// </summary>
public static class CoreClassProgression
{
    public sealed class Binding
    {
        public readonly CoreClassId ClassId;
        public readonly Upgrade.Category Category;
        public readonly Upgrade.Key ProgressionKey;
        private readonly Func<int, int> grantedPointsForRank;

        internal Binding(
            CoreClassId classId,
            Upgrade.Category category,
            Upgrade.Key progressionKey,
            Func<int, int> grantedPointsForRank)
        {
            ClassId = classId;
            Category = category;
            ProgressionKey = progressionKey;
            this.grantedPointsForRank = grantedPointsForRank;
        }

        public int GetGrantedPointsForRank(int rank)
        {
            return Math.Max(0, grantedPointsForRank == null
                ? 0
                : grantedPointsForRank(Math.Max(0, rank)));
        }
    }

    private static readonly Dictionary<CoreClassId, Binding> byClass =
        new Dictionary<CoreClassId, Binding>();
    private static readonly Dictionary<Upgrade.Category, Binding> byCategory =
        new Dictionary<Upgrade.Category, Binding>();
    private static readonly Dictionary<Upgrade.Key, Binding> byUpgrade =
        new Dictionary<Upgrade.Key, Binding>();

    public static void Register(
        CoreClassId classId,
        Upgrade.Category category,
        Upgrade.Key progressionKey,
        Func<int, int> grantedPointsForRank)
    {
        if (classId == CoreClassId.None)
            throw new ArgumentException("None cannot own class progression.", "classId");
        if (grantedPointsForRank == null)
            throw new ArgumentNullException("grantedPointsForRank");
        if (byClass.ContainsKey(classId))
            throw new InvalidOperationException("Duplicate class progression registration: " + classId);
        if (byCategory.ContainsKey(category))
            throw new InvalidOperationException("Duplicate custom class category registration: " + category);
        if (byUpgrade.ContainsKey(progressionKey))
            throw new InvalidOperationException("Duplicate class progression upgrade registration: " + progressionKey);

        Binding binding = new Binding(
            classId,
            category,
            progressionKey,
            grantedPointsForRank);
        byClass.Add(classId, binding);
        byCategory.Add(category, binding);
        byUpgrade.Add(progressionKey, binding);
    }

    public static bool TryGetByClass(CoreClassId classId, out Binding binding)
    {
        return byClass.TryGetValue(classId, out binding);
    }

    public static bool TryGetByCategory(
        Upgrade.Category category,
        out Binding binding)
    {
        return byCategory.TryGetValue(category, out binding);
    }

    public static bool TryGetByUpgrade(
        Upgrade.Key key,
        out Binding binding)
    {
        return byUpgrade.TryGetValue(key, out binding);
    }

    /// <summary>
    /// Custom standalone classes are mutually exclusive with each other even
    /// though vanilla Star Vortex permits multiple secondary classes. A class
    /// remains owned until its native secondary-class unlock is actually sold;
    /// merely reducing its progression rank to zero does not free the slot.
    /// </summary>
    public static bool TryFindOtherOwnedClass(
        Pilot pilot,
        CoreClassId exceptClass,
        out Binding other)
    {
        other = null;
        if (pilot == null)
            return false;

        foreach (KeyValuePair<CoreClassId, Binding> pair in byClass)
        {
            Binding candidate = pair.Value;
            if (candidate.ClassId == exceptClass)
                continue;

            bool unlocked = pilot.unlockedSecondaryClasses != null &&
                pilot.unlockedSecondaryClasses.Contains(candidate.Category);
            if (!unlocked)
                unlocked = pilot.HasUpgradeInCategory(candidate.Category);

            if (!unlocked)
                continue;

            other = candidate;
            return true;
        }

        return false;
    }
}

/// <summary>
/// Prevent a progression downgrade from reducing the class's point grant below
/// the amount currently committed to that class's specialization trees.
/// Leviathan keeps its pre-Core guard temporarily; all newer classes use this
/// shared path until that legacy patch is migrated out.
/// </summary>
[HarmonyPatch(typeof(Pilot), "SetUpgrade")]
public static class CoreClassProgressionDowngradeGuardPatch
{
    public static bool Prefix(Pilot __instance, Upgrade.Key key, int level)
    {
        if (__instance == null)
            return true;

        CoreClassProgression.Binding binding;
        if (!CoreClassProgression.TryGetByUpgrade(key, out binding))
            return true;

        // LeviathanSpecializationCurrency already owns the exact same guard and
        // vanilla tooltip integration. Avoid duplicate warnings until it is
        // deliberately migrated to this shared implementation.
        if (binding.ClassId == CoreClassId.Leviathan)
            return true;

        int current = __instance.GetUpgradeLevel(key);
        if (level >= current)
            return true;

        int capacity = binding.GetGrantedPointsForRank(level);
        int spent = CoreSpecializationRuntime.GetSpentPointsForClass(
            __instance,
            binding.ClassId);

        if (spent <= capacity)
            return true;

        Debug.LogWarning(
            "[CoreClassProgression] " + binding.ClassId +
            " downgrade blocked: " + spent.ToString() +
            " specialization points are invested, but rank " +
            level.ToString() + " would provide only " +
            capacity.ToString() + ".");
        return false;
    }

    internal static void AddDowngradeReason(
        Pilot pilot,
        Upgrade upgrade,
        ref string result)
    {
        if (result != null || pilot == null || upgrade == null)
            return;

        CoreClassProgression.Binding binding;
        if (!CoreClassProgression.TryGetByUpgrade(upgrade.key, out binding) ||
            binding.ClassId == CoreClassId.Leviathan)
        {
            return;
        }

        int current = pilot.GetUpgradeLevel(upgrade.key);
        if (current <= 0)
            return;

        int capacity = binding.GetGrantedPointsForRank(current - 1);
        int spent = CoreSpecializationRuntime.GetSpentPointsForClass(
            pilot,
            binding.ClassId);
        if (spent > capacity)
            result = "Refund specialization points from this class's trees first.";
    }
}

[HarmonyPatch(typeof(CoreUpgrades), "CanDowngradeTooltip")]
public static class CoreClassProgressionCoreDowngradeTooltipPatch
{
    public static void Postfix(Upgrade upgrade, ref string __result)
    {
        CoreClassProgressionDowngradeGuardPatch.AddDowngradeReason(
            CoreSpecializationRuntime.GetCurrentPilot(),
            upgrade,
            ref __result);
    }
}

[HarmonyPatch(typeof(SciencePanel), "CanDowngradeTooltip")]
public static class CoreClassProgressionScienceDowngradeTooltipPatch
{
    public static void Postfix(Upgrade upgrade, ref string __result)
    {
        CoreClassProgressionDowngradeGuardPatch.AddDowngradeReason(
            CoreSpecializationRuntime.GetCurrentPilot(),
            upgrade,
            ref __result);
    }
}

/// <summary>
/// Vanilla allows multiple secondary classes. Standalone custom classes do not:
/// the currently owned custom class must be fully emptied and sold before a
/// different custom class can be bought.
/// </summary>
[HarmonyPatch(typeof(SciencePanel), "BuyClass")]
public static class CoreCustomClassMutualExclusionPatch
{
    private static readonly FieldInfo ShipField =
        AccessTools.Field(typeof(SciencePanel), "ship");

    public static bool Prefix(SciencePanel __instance, Upgrade.Category category)
    {
        CoreClassProgression.Binding requested;
        if (!CoreClassProgression.TryGetByCategory(category, out requested))
            return true;

        GameShip ship = ShipField == null || __instance == null
            ? null
            : ShipField.GetValue(__instance) as GameShip;
        Pilot pilot = ship == null ? null : ship.pilot;
        if (pilot == null)
            return true;

        CoreClassProgression.Binding other;
        if (!CoreClassProgression.TryFindOtherOwnedClass(
                pilot,
                requested.ClassId,
                out other))
        {
            return true;
        }

        Debug.LogWarning(
            "[CoreClassProgression] Cannot unlock " + requested.ClassId +
            " while " + other.ClassId +
            " is still owned. Refund all specialization nodes, reduce its " +
            "progression rank to 0, then refund that class first.");
        return false;
    }
}

/// <summary>
/// Fail-safe for direct/test upgrade mutations that bypass SciencePanel class
/// purchase. Normal progression inside an already-owned class is unaffected.
/// </summary>
[HarmonyPatch(typeof(Pilot), "SetUpgrade")]
public static class CoreCustomClassDirectActivationGuardPatch
{
    public static bool Prefix(Pilot __instance, Upgrade.Key key, int level)
    {
        if (__instance == null || level <= 0)
            return true;

        CoreClassProgression.Binding requested;
        if (!CoreClassProgression.TryGetByUpgrade(key, out requested))
            return true;

        bool requestedOwned = __instance.unlockedSecondaryClasses != null &&
            __instance.unlockedSecondaryClasses.Contains(requested.Category);
        if (requestedOwned)
            return true;

        CoreClassProgression.Binding other;
        if (!CoreClassProgression.TryFindOtherOwnedClass(
                __instance,
                requested.ClassId,
                out other))
        {
            return true;
        }

        Debug.LogWarning(
            "[CoreClassProgression] Direct activation of " +
            requested.ClassId + " blocked while " + other.ClassId +
            " is still owned.");
        return false;
    }
}
