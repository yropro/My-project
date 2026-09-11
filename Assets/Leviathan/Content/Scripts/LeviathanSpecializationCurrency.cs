using HarmonyLib;
using StarVortex;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

/// <summary>
/// Native Evolution upgrade and the shared specialization-point economy.
///
/// Evolution is the only native Leviathan progression skill. Rank 1 awakens the
/// Leviathan chassis and automatically unlocks Growth; every rank grants two
/// Evolution Points that may be spent across any unlocked specialization tree.
/// </summary>
public static class LeviathanSpecializationCurrency
{
    public const int UpgradeKeyValue = 87;
    public static readonly Upgrade.Key UpgradeKey = (Upgrade.Key)UpgradeKeyValue;

    // Migration-only tombstone for saves made before native Growth was removed.
    // It is registered but never shown or used for gameplay. Once an active Pilot
    // is available, any old rank is removed after ensuring Evolution rank >= 1.
    public const int LegacyGrowthUpgradeKeyValue = 81;
    public static readonly Upgrade.Key LegacyGrowthUpgradeKey =
        (Upgrade.Key)LegacyGrowthUpgradeKeyValue;

    public const int PointsPerRank = 2;
    public const int Levels = 999;
    public const string SkillName = "Evolution";
    public const string CurrencyName = "Evolution Points";

    private static readonly FieldInfo UpgradesField =
        AccessTools.Field(typeof(Upgrade), "upgrades");

    private static readonly FieldInfo UpgradeLookupField =
        AccessTools.Field(typeof(Upgrade), "upgradeLookup");

    private static readonly FieldInfo UpgradeKeyField =
        AccessTools.Field(typeof(Upgrade), "key");

    private static readonly FieldInfo UpgradeCategoryField =
        AccessTools.Field(typeof(Upgrade), "category");

    private static readonly ConstructorInfo UpgradeConstructor =
        AccessTools.Constructor(
            typeof(Upgrade),
            new Type[]
            {
                typeof(Upgrade.Category),
                typeof(Upgrade.Key),
                typeof(int),
                typeof(bool),
                typeof(int),
                typeof(int),
                typeof(string)
            }
        );

    private static readonly MethodInfo GetUpgradeMethod =
        AccessTools.Method(
            typeof(Upgrade),
            "GetUpgrade",
            new Type[] { typeof(Upgrade.Key) }
        );

    private static Upgrade upgrade;
    private static Upgrade legacyGrowthTombstone;

    [ThreadStatic]
    private static bool migratingLegacyGrowth;

    public static bool IsMigratingLegacyGrowth
    {
        get { return migratingLegacyGrowth; }
    }

    public static Upgrade Upgrade
    {
        get
        {
            EnsureRegistered();
            return upgrade;
        }
    }

    public static void EnsureRegistered()
    {
        if (upgrade != null && legacyGrowthTombstone != null)
            return;

        if (UpgradesField == null ||
            UpgradeLookupField == null ||
            UpgradeKeyField == null ||
            UpgradeCategoryField == null ||
            UpgradeConstructor == null ||
            GetUpgradeMethod == null)
        {
            throw new Exception(
                "Could not resolve native Upgrade registration members for Evolution."
            );
        }

        Upgrade[] native = UpgradesField.GetValue(null) as Upgrade[];
        if (native == null)
            throw new Exception("Native Upgrade.upgrades array was null.");

        List<Upgrade> expanded = new List<Upgrade>(native);

        upgrade = FindRegisteredUpgrade(expanded, UpgradeKey);
        if (upgrade == null)
        {
            upgrade = CreateUpgrade(
                UpgradeKey,
                0,
                false,
                PointsPerRank,
                Levels,
                SkillName
            );
            expanded.Add(upgrade);
        }
        else
        {
            RefreshEvolutionMetadata(upgrade);
        }

        // Keep old key 81 resolvable long enough to safely load/migrate older
        // Pilots. This object is deliberately absent from LeviathanSkillUI.
        legacyGrowthTombstone = FindRegisteredUpgrade(
            expanded,
            LegacyGrowthUpgradeKey
        );

        if (legacyGrowthTombstone == null)
        {
            legacyGrowthTombstone = CreateUpgrade(
                LegacyGrowthUpgradeKey,
                0,
                false,
                0,
                1,
                "Legacy Growth (Migration Only)"
            );
            expanded.Add(legacyGrowthTombstone);
        }
        else
        {
            legacyGrowthTombstone.requiredLevel = 0;
            legacyGrowthTombstone.percentage = false;
            legacyGrowthTombstone.value = 0;
            legacyGrowthTombstone.levels = Math.Max(
                1,
                legacyGrowthTombstone.levels
            );
            legacyGrowthTombstone.name = "Legacy Growth (Migration Only)";
        }

        UpgradesField.SetValue(null, expanded.ToArray());

        // Upgrade.GetUpgrade lazily caches the array. Rebuild after both key 87
        // and the key-81 migration tombstone are guaranteed to be present.
        UpgradeLookupField.SetValue(null, null);

        if (GetUpgradeMethod.Invoke(null, new object[] { UpgradeKey }) == null)
            throw new Exception("Upgrade lookup rebuilt without Evolution.");

        if (GetUpgradeMethod.Invoke(
                null,
                new object[] { LegacyGrowthUpgradeKey }) == null)
        {
            throw new Exception(
                "Upgrade lookup rebuilt without the legacy Growth migration tombstone."
            );
        }

        Debug.Log(
            "[Leviathan] Evolution ready: +" +
            PointsPerRank.ToString() + " " + CurrencyName +
            "/rank, " + Levels.ToString() + " ranks."
        );
    }

    private static Upgrade FindRegisteredUpgrade(
        List<Upgrade> upgrades,
        Upgrade.Key key)
    {
        for (int i = 0; i < upgrades.Count; i++)
        {
            Upgrade candidate = upgrades[i];
            if (candidate == null)
                continue;

            Upgrade.Key candidateKey =
                (Upgrade.Key)UpgradeKeyField.GetValue(candidate);

            if (candidateKey != key)
                continue;

            Upgrade.Category category =
                (Upgrade.Category)UpgradeCategoryField.GetValue(candidate);

            if (category != LeviathanMod.LeviathanCategory)
            {
                throw new Exception(
                    "Upgrade key " + ((int)key).ToString() +
                    " is already owned by another category."
                );
            }

            return candidate;
        }

        return null;
    }

    private static Upgrade CreateUpgrade(
        Upgrade.Key key,
        int requiredLevel,
        bool percentage,
        int value,
        int levels,
        string name)
    {
        Upgrade created = UpgradeConstructor.Invoke(
            new object[]
            {
                LeviathanMod.LeviathanCategory,
                key,
                requiredLevel,
                percentage,
                value,
                levels,
                name
            }
        ) as Upgrade;

        if (created == null)
        {
            throw new Exception(
                "Failed to construct Leviathan native upgrade '" + name + "'."
            );
        }

        return created;
    }

    private static void RefreshEvolutionMetadata(Upgrade evolution)
    {
        evolution.requiredLevel = 0;
        evolution.percentage = false;
        evolution.value = PointsPerRank;
        evolution.levels = Levels;
        evolution.name = SkillName;
    }

    /// <summary>
    /// One-way save migration from the removed native Growth key (81).
    /// Old Growth ownership means the character was already a Leviathan, so it
    /// awakens Evolution rank 1 if necessary, then removes key 81 entirely.
    /// </summary>
    public static void MigrateLegacyGrowth(Pilot pilot)
    {
        if (pilot == null)
            return;

        EnsureRegistered();

        int oldGrowth = pilot.GetUpgradeLevel(LegacyGrowthUpgradeKey);
        if (oldGrowth <= 0)
            return;

        migratingLegacyGrowth = true;
        try
        {
            int evolution = pilot.GetUpgradeLevel(UpgradeKey);
            if (evolution < 1)
                pilot.SetUpgrade(UpgradeKey, 1);

            pilot.SetUpgrade(LegacyGrowthUpgradeKey, 0);
        }
        finally
        {
            migratingLegacyGrowth = false;
        }

        Debug.Log(
            "[Leviathan] Migrated removed native Growth rank " +
            oldGrowth.ToString() + " into Evolution activation."
        );
    }
}

[HarmonyPatch]
public static class LeviathanEvolutionUpgradeNamePatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(Upgrade),
            "GetName",
            new Type[] { typeof(Upgrade.Key), typeof(bool) }
        );
    }

    public static bool Prefix(Upgrade.Key __0, ref string __result)
    {
        if (__0 != LeviathanSpecializationCurrency.UpgradeKey)
            return true;

        __result = LeviathanSpecializationCurrency.SkillName;
        return false;
    }
}

[HarmonyPatch]
public static class LeviathanEvolutionUpgradeDescriptionPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(Upgrade),
            "GetDescription",
            new Type[] { typeof(Upgrade.Key), typeof(bool) }
        );
    }

    public static bool Prefix(Upgrade.Key __0, ref string __result)
    {
        if (__0 != LeviathanSpecializationCurrency.UpgradeKey)
            return true;

        __result =
            "Grants 2 Evolution Points per rank. Evolution rank 1 awakens " +
            "the Leviathan chassis and Growth tree automatically; spend " +
            "Evolution Points to specialize Growth or unlock other Leviathan trees.";
        return false;
    }
}

[HarmonyPatch]
public static class LeviathanEvolutionUpgradeIsFreePatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(Upgrade),
            "IsFree",
            new Type[] { typeof(Upgrade.Key) }
        );
    }

    public static bool Prefix(Upgrade.Key __0, ref bool __result)
    {
        if (__0 != LeviathanSpecializationCurrency.UpgradeKey)
            return true;

        __result = false;
        return false;
    }
}

[HarmonyPatch(typeof(UpgradeDisplay), "Init")]
public static class LeviathanEvolutionIconPatch
{
    public static void Postfix(UpgradeDisplay __instance, Upgrade __0)
    {
        if (__instance == null ||
            __0 == null ||
            __0.key != LeviathanSpecializationCurrency.UpgradeKey ||
            __instance.iconImage == null ||
            WorldController.instance == null)
        {
            return;
        }

        __instance.iconImage.sprite =
            WorldController.instance.GetUpgradeCategoryIcon(
                LeviathanMod.LeviathanCategory
            );
    }
}

// CoreClassProgressionRules owns the downgrade/refund invariant. Leviathan only
// reacts after an Evolution mutation to invalidate derived state and refresh the
// chassis-dependent Growth presentation/runtime.
[HarmonyPatch(typeof(Pilot), "SetUpgrade")]
public static class LeviathanEvolutionMutationRefreshPatch
{
    public static void Postfix(Pilot __instance, Upgrade.Key __0)
    {
        if (__instance == null ||
            __0 != LeviathanSpecializationCurrency.UpgradeKey)
        {
            return;
        }

        CoreSpecializationRuntime.InvalidateConfiguration();

        GameShip player = WorldController.instance == null
            ? null
            : WorldController.instance.GetCurrentPlayerShip();

        if (!LeviathanSpecializationCurrency.IsMigratingLegacyGrowth &&
            player != null &&
            LeviathanMod.Controller != null &&
            object.ReferenceEquals(
                GameShip.GetPlayerSourcePilot(player),
                __instance))
        {
            LeviathanMod.Controller.RequestGrowthRefreshIfNeeded(player);
        }
    }
}

// A native full upgrade reset should also clear the externally persisted web.
[HarmonyPatch(typeof(Pilot), "ResetUpgrades")]
public static class LeviathanSpecializationResetWithNativePatch
{
    public static void Prefix(Pilot __instance)
    {
        if (__instance == null)
            return;

        string reason;
        CoreSpecializationRuntime.ResetClassBuild(
            __instance,
            CoreSpecializationRuntime.GetEffectiveClass(__instance),
            out reason);

        if (!string.IsNullOrEmpty(reason))
        {
            Debug.LogWarning(
                "[Leviathan] Could not persist specialization reset: " + reason
            );
        }
    }
}
