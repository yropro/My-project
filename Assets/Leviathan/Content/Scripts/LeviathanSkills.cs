using HarmonyLib;
using StarVortex;
using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Registers Leviathan skills in the game's native upgrade data and bridges
/// the few places that enumerate the compile-time Upgrade.Category enum.
/// Purchasing, selling, ranking, point accounting and Pilot persistence remain
/// native game behavior.
/// </summary>
public static class LeviathanSkillSystem
{
    private const int GrowthValuePerRank = 3;
    private const int GrowthLevels = 5;
    private const int ConstrictorLevels = 5;
    private const int PredatorLevels = 5;

    private static readonly FieldInfo UpgradesField =
        AccessTools.Field(typeof(Upgrade), "upgrades");

    private static readonly FieldInfo UpgradeLookupField =
        AccessTools.Field(typeof(Upgrade), "upgradeLookup");

    private static readonly FieldInfo UpgradeKeyField =
        AccessTools.Field(typeof(Upgrade), "key");

    private static readonly FieldInfo UpgradeCategoryField =
        AccessTools.Field(typeof(Upgrade), "category");

    private static readonly FieldInfo UpgradeNameField =
        AccessTools.Field(typeof(Upgrade), "name");

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

    public static Upgrade Growth { get; private set; }
    public static Upgrade Constrictor { get; private set; }
    public static Upgrade Predator { get; private set; }

    public static void Register()
    {
        if (UpgradesField == null ||
            UpgradeLookupField == null ||
            UpgradeConstructor == null ||
            GetUpgradeMethod == null)
        {
            throw new Exception(
                "[Leviathan] Could not resolve native Upgrade registration members."
            );
        }

        Upgrade[] nativeUpgrades = UpgradesField.GetValue(null) as Upgrade[];

        if (nativeUpgrades == null)
        {
            throw new Exception(
                "[Leviathan] Native Upgrade.upgrades array was null."
            );
        }

        System.Collections.Generic.List<Upgrade> upgrades =
            new System.Collections.Generic.List<Upgrade>(nativeUpgrades);

        Growth = EnsureUpgrade(
            upgrades,
            LeviathanMod.GrowthUpgrade,
            0,                      // requiredLevel
            false,                  // percentage
            GrowthValuePerRank,     // UI value: +3
            GrowthLevels,
            "Growth"
        );

        Constrictor = EnsureUpgrade(
            upgrades,
            LeviathanMod.ConstrictorUpgrade,
            0,                      // requiredLevel
            false,                  // percentage
            0,                      // behavior is described by the skill text
            ConstrictorLevels,
            "Constrictor"
        );

        Predator = EnsureUpgrade(
            upgrades,
            LeviathanMod.PredatorUpgrade,
            0,                      // requiredLevel
            false,                  // percentage
            0,                      // behavior is described by the skill text
            PredatorLevels,
            "Predator"
        );

        UpgradesField.SetValue(null, upgrades.ToArray());
        RebuildUpgradeLookup();
    }

    private static Upgrade EnsureUpgrade(
        System.Collections.Generic.List<Upgrade> upgrades,
        Upgrade.Key key,
        int requiredLevel,
        bool percentage,
        int value,
        int levels,
        string name)
    {
        Upgrade existing = null;

        for (int i = 0; i < upgrades.Count; i++)
        {
            Upgrade candidate = upgrades[i];

            if (candidate == null)
                continue;

            Upgrade.Key candidateKey =
                (Upgrade.Key)UpgradeKeyField.GetValue(candidate);

            if (candidateKey == key)
            {
                existing = candidate;
                break;
            }
        }

        if (existing != null)
        {
            Upgrade.Category category =
                (Upgrade.Category)UpgradeCategoryField.GetValue(existing);

            if (category != LeviathanMod.LeviathanCategory)
            {
                throw new Exception(
                    "[Leviathan] Upgrade key " +
                    ((int)key).ToString() +
                    " is already registered by another upgrade/category. " +
                    "Refusing to overwrite it."
                );
            }

            string existingName = UpgradeNameField == null
                ? string.Empty
                : UpgradeNameField.GetValue(existing) as string;

            Debug.Log(
                "[Leviathan] Upgrade key " +
                ((int)key).ToString() +
                " already present in native Upgrade array" +
                (string.IsNullOrEmpty(existingName)
                    ? "."
                    : " as '" + existingName + "'.")
            );

            return existing;
        }

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
                "[Leviathan] Failed to construct native upgrade '" +
                name + "'."
            );
        }

        upgrades.Add(created);

        Debug.Log(
            "[Leviathan] Registered native " +
            name +
            " upgrade. Key = " +
            ((int)key).ToString() +
            ", Category = 17, Levels = " +
            levels.ToString() +
            "."
        );

        return created;
    }

    private static void RebuildUpgradeLookup()
    {
        // Upgrade.GetUpgrade lazily creates a dictionary from Upgrade.upgrades.
        // Clear the old cache, then immediately force a rebuild that includes
        // every custom Leviathan key.
        UpgradeLookupField.SetValue(null, null);

        object growth = GetUpgradeMethod.Invoke(
            null,
            new object[] { LeviathanMod.GrowthUpgrade }
        );

        object constrictor = GetUpgradeMethod.Invoke(
            null,
            new object[] { LeviathanMod.ConstrictorUpgrade }
        );

        object predator = GetUpgradeMethod.Invoke(
            null,
            new object[] { LeviathanMod.PredatorUpgrade }
        );

        if (growth == null || constrictor == null || predator == null)
        {
            throw new Exception(
                "[Leviathan] Upgrade lookup rebuilt without all Leviathan skills."
            );
        }

        Debug.Log(
            "[Leviathan] Native Upgrade lookup rebuilt with Growth, Constrictor and Predator."
        );
    }
}

/// <summary>
/// Shared reflection helpers for creating Leviathan through the exact same
/// UpgradeClassDisplay/UpgradeDisplay prefabs used by native classes.
/// </summary>
public static class LeviathanSkillUI
{
    private static readonly FieldInfo ClassCategoryField =
        AccessTools.Field(typeof(UpgradeClassDisplay), "category");

    private static readonly FieldInfo ForceActiveColorField =
        AccessTools.Field(typeof(UpgradeClassDisplay), "forceActiveColor");

    private static readonly FieldInfo PreviewOnlyField =
        AccessTools.Field(typeof(UpgradeClassDisplay), "previewOnly");

    private static readonly FieldInfo TooltipContainerOverrideField =
        AccessTools.Field(typeof(UpgradeClassDisplay), "tooltipContainerOverride");

    private static readonly MethodInfo ClassInitMethod =
        AccessTools.Method(typeof(UpgradeClassDisplay), "Init");

    private static readonly MethodInfo AddUpgradeMethod =
        AccessTools.Method(typeof(UpgradeClassDisplay), "AddUpgrade");

    private static readonly MethodInfo ShowBuyButtonMethod =
        AccessTools.Method(
            typeof(UpgradeClassDisplay),
            "ShowBuyButton",
            new Type[] { typeof(bool) }
        );

    private static readonly MethodInfo ShowSellButtonMethod =
        AccessTools.Method(
            typeof(UpgradeClassDisplay),
            "ShowSellButton",
            new Type[] { typeof(bool) }
        );

    private static readonly MethodInfo UpgradeDisplaySelectMethod =
        AccessTools.Method(typeof(UpgradeDisplay), "Select");

    private static readonly MethodInfo IsSecondaryClassUnlockedMethod =
        AccessTools.Method(
            typeof(Pilot),
            "IsSecondaryClassUnlocked",
            new Type[] { typeof(Upgrade.Category) }
        );

    private static readonly MethodInfo HasUpgradeInCategoryMethod =
        AccessTools.Method(
            typeof(Pilot),
            "HasUpgradeInCategory",
            new Type[] { typeof(Upgrade.Category) }
        );

    private static readonly MethodInfo CountItemMethod =
        AccessTools.Method(
            typeof(GameShip),
            "CountItem",
            new Type[] { typeof(string) }
        );

    private static readonly FieldInfo ScienceClassPrefabField =
        AccessTools.Field(typeof(SciencePanel), "upgradeClassDisplayPrefab");

    private static readonly FieldInfo ScienceLockedContainerField =
        AccessTools.Field(typeof(SciencePanel), "lockedClassContainer");

    private static readonly FieldInfo ScienceUnlockedContainerField =
        AccessTools.Field(typeof(SciencePanel), "unlockedClassContainer");

    private static readonly FieldInfo ScienceTooltipContainerField =
        AccessTools.Field(typeof(SciencePanel), "tooltipContainer");

    private static readonly FieldInfo ScienceShipField =
        AccessTools.Field(typeof(SciencePanel), "ship");

    private static readonly FieldInfo ScienceSeedField =
        AccessTools.Field(typeof(SciencePanel), "infectedSeedItemBase");

    private static readonly FieldInfo ScienceUnlockedDisplaysField =
        AccessTools.Field(typeof(SciencePanel), "unlockedClassDisplays");

    private static readonly FieldInfo CoreClassPrefabField =
        AccessTools.Field(typeof(CoreUpgrades), "upgradeDisplayClassPrefab");

    private static readonly FieldInfo CoreSecondaryContainerField =
        AccessTools.Field(typeof(CoreUpgrades), "secondaryContainer");

    private static readonly FieldInfo CoreShipField =
        AccessTools.Field(typeof(CoreUpgrades), "ship");

    private static readonly FieldInfo CoreClassDisplaysField =
        AccessTools.Field(typeof(CoreUpgrades), "upgradeClassDisplays");

    public static void AddToSciencePanel(
        SciencePanel panel,
        object[] rebuildArgs)
    {
        if (panel == null ||
            LeviathanSkillSystem.Growth == null ||
            LeviathanSkillSystem.Constrictor == null ||
            LeviathanSkillSystem.Predator == null)
            return;

        GameShip ship = GetShipFromArgs(rebuildArgs);

        if (ship == null && ScienceShipField != null)
            ship = ScienceShipField.GetValue(panel) as GameShip;

        if (ship == null || ship.pilot == null)
            return;

        bool unlocked = IsClassUnlocked(ship.pilot);

        Transform lockedContainer = GetTransform(
            ScienceLockedContainerField?.GetValue(panel)
        );

        Transform unlockedContainer = GetTransform(
            ScienceUnlockedContainerField?.GetValue(panel)
        );

        IList unlockedDisplays =
            ScienceUnlockedDisplaysField?.GetValue(panel) as IList;

        // Vanilla rebuilds these containers by destroying their existing
        // children. Unity destruction is deferred until the end of the frame,
        // so an old Leviathan display can still be discoverable here even
        // though it is already on its way out. Explicitly retire old copies
        // from both possible containers before creating the fresh native one.
        RetireLeviathanDisplays(lockedContainer, unlockedDisplays);
        RetireLeviathanDisplays(unlockedContainer, unlockedDisplays);

        Transform container = unlocked ? unlockedContainer : lockedContainer;

        if (container == null)
            return;

        UpgradeClassDisplay display = InstantiateClassDisplay(
            ScienceClassPrefabField?.GetValue(panel),
            container
        );

        if (display == null)
        {
            Debug.LogError(
                "[Leviathan] Could not instantiate SciencePanel class display."
            );
            return;
        }

        SetField(ForceActiveColorField, display, !unlocked);
        SetField(PreviewOnlyField, display, !unlocked);

        if (ScienceTooltipContainerField != null)
        {
            SetField(
                TooltipContainerOverrideField,
                display,
                ScienceTooltipContainerField.GetValue(panel)
            );
        }

        InvokeClassInit(display, ship, panel);
        UpgradeDisplay[] skillDisplays = AddLeviathanSkills(display);
        RestoreRequestedSelection(skillDisplays, rebuildArgs);

        if (unlocked)
        {
            bool canSell = !HasUpgradeInCategory(ship.pilot);
            ShowSellButtonMethod?.Invoke(display, new object[] { canSell });

            if (unlockedDisplays != null)
                unlockedDisplays.Add(display);
        }
        else
        {
            ShowBuyButtonMethod?.Invoke(
                display,
                new object[] { HasInfectedSeed(panel, ship) }
            );
        }
    }

    public static void AddToCoreUpgrades(
        CoreUpgrades core,
        object[] rebuildArgs)
    {
        if (core == null ||
            LeviathanSkillSystem.Growth == null ||
            LeviathanSkillSystem.Constrictor == null ||
            LeviathanSkillSystem.Predator == null)
            return;

        GameShip ship = CoreShipField?.GetValue(core) as GameShip;

        if (ship == null || ship.pilot == null)
            return;

        if (!IsClassUnlocked(ship.pilot))
            return;

        Transform container = GetTransform(
            CoreSecondaryContainerField?.GetValue(core)
        );

        if (container == null)
            return;

        RetireLeviathanDisplays(container, CoreClassDisplaysField?.GetValue(core) as IList);

        UpgradeClassDisplay display = InstantiateClassDisplay(
            CoreClassPrefabField?.GetValue(core),
            container
        );

        if (display == null)
        {
            Debug.LogError(
                "[Leviathan] Could not instantiate CoreUpgrades class display."
            );
            return;
        }

        InvokeClassInit(display, ship, core);
        UpgradeDisplay[] skillDisplays = AddLeviathanSkills(display);
        RestoreRequestedSelection(skillDisplays, rebuildArgs);

        IList displays = CoreClassDisplaysField?.GetValue(core) as IList;

        if (displays != null)
            displays.Add(display);
    }

    private static void InvokeClassInit(
        UpgradeClassDisplay display,
        GameShip ship,
        object upgradeContext)
    {
        if (ClassInitMethod == null)
            throw new Exception("[Leviathan] UpgradeClassDisplay.Init not found.");

        ClassInitMethod.Invoke(
            display,
            new object[]
            {
                LeviathanMod.LeviathanCategory,
                ship,
                upgradeContext
            }
        );
    }

    private static UpgradeDisplay[] AddLeviathanSkills(UpgradeClassDisplay display)
    {
        if (AddUpgradeMethod == null)
            throw new Exception("[Leviathan] UpgradeClassDisplay.AddUpgrade not found.");

        UpgradeDisplay growth = AddUpgradeMethod.Invoke(
            display,
            new object[] { LeviathanSkillSystem.Growth }
        ) as UpgradeDisplay;

        UpgradeDisplay predator = AddUpgradeMethod.Invoke(
            display,
            new object[] { LeviathanSkillSystem.Predator }
        ) as UpgradeDisplay;

        UpgradeDisplay constrictor = AddUpgradeMethod.Invoke(
            display,
            new object[] { LeviathanSkillSystem.Constrictor }
        ) as UpgradeDisplay;

        return new UpgradeDisplay[] { growth, predator, constrictor };
    }

    private static bool IsClassUnlocked(Pilot pilot)
    {
        if (pilot == null || IsSecondaryClassUnlockedMethod == null)
            return false;

        object result = IsSecondaryClassUnlockedMethod.Invoke(
            pilot,
            new object[] { LeviathanMod.LeviathanCategory }
        );

        return result is bool && (bool)result;
    }

    private static bool HasUpgradeInCategory(Pilot pilot)
    {
        if (pilot == null || HasUpgradeInCategoryMethod == null)
            return false;

        object result = HasUpgradeInCategoryMethod.Invoke(
            pilot,
            new object[] { LeviathanMod.LeviathanCategory }
        );

        return result is bool && (bool)result;
    }

    private static bool HasInfectedSeed(
        SciencePanel panel,
        GameShip ship)
    {
        if (panel == null || ship == null || ScienceSeedField == null)
            return false;

        object seed = ScienceSeedField.GetValue(panel);

        if (seed == null)
            return false;

        string filename = GetFilename(seed);

        if (string.IsNullOrEmpty(filename) || CountItemMethod == null)
            return false;

        object result = CountItemMethod.Invoke(
            ship,
            new object[] { filename }
        );

        return result is int && (int)result >= 1;
    }

    private static string GetFilename(object source)
    {
        Type type = source.GetType();

        PropertyInfo property = type.GetProperty(
            "filename",
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic
        );

        if (property != null)
            return property.GetValue(source, null) as string;

        FieldInfo field = type.GetField(
            "filename",
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic
        );

        return field == null ? null : field.GetValue(source) as string;
    }

    private static UpgradeClassDisplay InstantiateClassDisplay(
        object prefabValue,
        Transform parent)
    {
        GameObject prefab = prefabValue as GameObject;

        if (prefab == null)
        {
            Component component = prefabValue as Component;

            if (component != null)
                prefab = component.gameObject;
        }

        if (prefab == null || parent == null)
            return null;

        GameObject clone = UnityEngine.Object.Instantiate(prefab, parent);
        return clone.GetComponent<UpgradeClassDisplay>();
    }

    private static void RetireLeviathanDisplays(
        Transform container,
        IList trackedDisplays)
    {
        if (container == null || ClassCategoryField == null)
            return;

        UpgradeClassDisplay[] displays =
            container.GetComponentsInChildren<UpgradeClassDisplay>(true);

        for (int i = 0; i < displays.Length; i++)
        {
            UpgradeClassDisplay display = displays[i];

            if (display == null)
                continue;

            Upgrade.Category category =
                (Upgrade.Category)ClassCategoryField.GetValue(display);

            if (category != LeviathanMod.LeviathanCategory)
                continue;

            if (trackedDisplays != null && trackedDisplays.Contains(display))
                trackedDisplays.Remove(display);

            // Hide immediately so a deferred Destroy cannot participate in
            // layout or make the replacement appear duplicated for one frame.
            display.gameObject.SetActive(false);
            UnityEngine.Object.Destroy(display.gameObject);
        }
    }

    private static GameShip GetShipFromArgs(object[] args)
    {
        if (args == null)
            return null;

        for (int i = 0; i < args.Length; i++)
        {
            GameShip ship = args[i] as GameShip;

            if (ship != null)
                return ship;
        }

        return null;
    }

    private static Transform GetTransform(object value)
    {
        if (value is Transform)
            return (Transform)value;

        Component component = value as Component;

        if (component != null)
            return component.transform;

        GameObject gameObject = value as GameObject;
        return gameObject == null ? null : gameObject.transform;
    }

    private static void RestoreRequestedSelection(
        UpgradeDisplay[] skillDisplays,
        object[] args)
    {
        if (skillDisplays == null ||
            UpgradeDisplaySelectMethod == null ||
            args == null ||
            args.Length == 0 ||
            args[0] == null)
        {
            return;
        }

        Upgrade.Key selected;

        try
        {
            selected = (Upgrade.Key)Convert.ToInt32(args[0]);
        }
        catch
        {
            return;
        }

        int index = -1;

        if (selected == LeviathanMod.GrowthUpgrade)
            index = 0;
        else if (selected == LeviathanMod.PredatorUpgrade)
            index = 1;
        else if (selected == LeviathanMod.ConstrictorUpgrade)
            index = 2;

        if (index >= 0 &&
            index < skillDisplays.Length &&
            skillDisplays[index] != null)
        {
            UpgradeDisplaySelectMethod.Invoke(skillDisplays[index], null);
        }
    }

    private static void SetField(
        FieldInfo field,
        object target,
        object value)
    {
        if (field != null && target != null)
            field.SetValue(target, value);
    }
}

// -----------------------------------------------------------------------------
// Native upgrade metadata shims
// -----------------------------------------------------------------------------

[HarmonyPatch]
public static class LeviathanUpgradeGetCategoryPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(Upgrade),
            "GetCategory",
            new Type[] { typeof(Upgrade.Category), typeof(bool) }
        );
    }

    public static bool Prefix(
        Upgrade.Category __0,
        ref string __result)
    {
        if (__0 != LeviathanMod.LeviathanCategory)
            return true;

        __result = "Leviathan";
        return false;
    }
}

[HarmonyPatch]
public static class LeviathanUpgradeGetNamePatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(Upgrade),
            "GetName",
            new Type[] { typeof(Upgrade.Key), typeof(bool) }
        );
    }

    public static bool Prefix(
        Upgrade.Key __0,
        ref string __result)
    {
        if (__0 == LeviathanMod.GrowthUpgrade)
        {
            __result = "Growth";
            return false;
        }

        if (__0 == LeviathanMod.PredatorUpgrade)
        {
            __result = "Predator";
            return false;
        }

        if (__0 == LeviathanMod.ConstrictorUpgrade)
        {
            __result = "Constrictor";
            return false;
        }

        return true;
    }
}

[HarmonyPatch]
public static class LeviathanUpgradeGetDescriptionPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(Upgrade),
            "GetDescription",
            new Type[] { typeof(Upgrade.Key), typeof(bool) }
        );
    }

    public static bool Prefix(
        Upgrade.Key __0,
        ref string __result)
    {
        if (__0 == LeviathanMod.GrowthUpgrade)
        {
            __result =
                "Adds 3 Leviathan body segments and increases mass by 40% per rank.";
            return false;
        }

        if (__0 == LeviathanMod.PredatorUpgrade)
        {
            __result =
                "Transforms Assault lunges into heavy Leviathan strikes. " +
                "Damage, critical chance, range, speed and cooldown improve with rank.";
            return false;
        }

        if (__0 == LeviathanMod.ConstrictorUpgrade)
        {
            __result =
                "Leviathan body segments deal contact damage using the equipped Assault weapon.";
            return false;
        }

        return true;
    }
}

[HarmonyPatch]
public static class LeviathanUpgradeIsFreePatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(Upgrade),
            "IsFree",
            new Type[] { typeof(Upgrade.Key) }
        );
    }

    public static bool Prefix(
        Upgrade.Key __0,
        ref bool __result)
    {
        if (__0 != LeviathanMod.GrowthUpgrade &&
            __0 != LeviathanMod.ConstrictorUpgrade &&
            __0 != LeviathanMod.PredatorUpgrade)
        {
            return true;
        }

        __result = false;
        return false;
    }
}

// Temporary presentation fallback: until Leviathan gets its own art/color,
// ask the native game for Gladiator's existing category presentation.
[HarmonyPatch]
public static class LeviathanCategoryIconPatch
{
    private static MethodBase target;

    public static MethodBase TargetMethod()
    {
        if (target != null)
            return target;

        target = typeof(WorldController).GetMethods(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic
            )
            .FirstOrDefault(
                m => m.Name == "GetUpgradeCategoryIcon" &&
                     m.ReturnType == typeof(Sprite) &&
                     m.GetParameters().Length >= 1 &&
                     m.GetParameters()[0].ParameterType ==
                        typeof(Upgrade.Category)
            );

        return target;
    }

    public static bool Prefix(
        WorldController __instance,
        object[] __args,
        ref Sprite __result)
    {
        if (__args == null ||
            __args.Length == 0 ||
            (Upgrade.Category)__args[0] != LeviathanMod.LeviathanCategory)
        {
            return true;
        }

        object[] fallbackArgs = (object[])__args.Clone();
        fallbackArgs[0] = Upgrade.Category.Gladiator;
        __result = (Sprite)target.Invoke(__instance, fallbackArgs);
        return false;
    }
}

[HarmonyPatch]
public static class LeviathanCategoryColorPatch
{
    private static MethodBase target;

    public static MethodBase TargetMethod()
    {
        if (target != null)
            return target;

        target = typeof(Palette).GetMethods(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic
            )
            .FirstOrDefault(
                m => m.Name == "GetUpgradeCategoryColor" &&
                     m.ReturnType == typeof(Color) &&
                     m.GetParameters().Length >= 1 &&
                     m.GetParameters()[0].ParameterType ==
                        typeof(Upgrade.Category)
            );

        return target;
    }

    public static bool Prefix(
        Palette __instance,
        object[] __args,
        ref Color __result)
    {
        if (__args == null ||
            __args.Length == 0 ||
            (Upgrade.Category)__args[0] != LeviathanMod.LeviathanCategory)
        {
            return true;
        }

        object[] fallbackArgs = (object[])__args.Clone();
        fallbackArgs[0] = Upgrade.Category.Gladiator;
        __result = (Color)target.Invoke(__instance, fallbackArgs);
        return false;
    }
}

// The native method enumerates Upgrade.Category, so it cannot see our numeric
// category 17. Preserve vanilla's result and additionally account for Leviathan.
[HarmonyPatch(typeof(Pilot), "HasLockedSecondaryClass")]
public static class LeviathanHasLockedSecondaryClassPatch
{
    public static void Postfix(Pilot __instance, ref bool __result)
    {
        if (__result || __instance == null)
            return;

        MethodInfo method = AccessTools.Method(
            typeof(Pilot),
            "IsSecondaryClassUnlocked",
            new Type[] { typeof(Upgrade.Category) }
        );

        if (method == null)
            return;

        object result = method.Invoke(
            __instance,
            new object[] { LeviathanMod.LeviathanCategory }
        );

        if (result is bool && !(bool)result)
            __result = true;
    }
}

// -----------------------------------------------------------------------------
// Native class-list insertion
// -----------------------------------------------------------------------------

[HarmonyPatch(typeof(SciencePanel), "RebuildSecondaryClasses")]
public static class LeviathanSciencePanelClassPatch
{
    public static void Postfix(
        SciencePanel __instance,
        object[] __args)
    {
        LeviathanSkillUI.AddToSciencePanel(__instance, __args);
    }
}

[HarmonyPatch(typeof(CoreUpgrades), "RebuildUpgrades")]
public static class LeviathanCoreUpgradesClassPatch
{
    public static void Postfix(
        CoreUpgrades __instance,
        object[] __args)
    {
        LeviathanSkillUI.AddToCoreUpgrades(__instance, __args);
    }
}

// Temporary Leviathan skill icon fallback. UpgradeDisplay otherwise leaves the
// prefab icon unchanged because keys 81/82/83 are absent from its serialized array.
[HarmonyPatch]
public static class LeviathanSkillIconPatch
{
    private static readonly FieldInfo UpgradeIconsField =
        AccessTools.Field(typeof(UpgradeDisplay), "upgradeIcons");

    private static readonly FieldInfo IconImageField =
        AccessTools.Field(typeof(UpgradeDisplay), "iconImage");

    public static MethodBase TargetMethod()
    {
        return typeof(UpgradeDisplay).GetMethods(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic
            )
            .FirstOrDefault(
                m => m.Name == "Init" &&
                     m.GetParameters().Length == 4 &&
                     m.GetParameters()[0].ParameterType == typeof(Upgrade) &&
                     m.GetParameters()[1].ParameterType == typeof(GameShip) &&
                     m.GetParameters()[3].ParameterType ==
                        typeof(UpgradeClassDisplay)
            );
    }

    public static void Postfix(
        UpgradeDisplay __instance,
        object[] __args)
    {
        if (__instance == null ||
            __args == null ||
            __args.Length == 0 ||
            !(__args[0] is Upgrade))
        {
            return;
        }

        Upgrade upgrade = (Upgrade)__args[0];
        FieldInfo keyField = AccessTools.Field(typeof(Upgrade), "key");

        if (keyField == null)
            return;

        Upgrade.Key leviathanKey =
            (Upgrade.Key)keyField.GetValue(upgrade);

        Upgrade.Key fallbackKey;

        if (leviathanKey == LeviathanMod.GrowthUpgrade)
            fallbackKey = Upgrade.Key.GladiatorHullLeech;
        else if (leviathanKey == LeviathanMod.ConstrictorUpgrade)
            fallbackKey = Upgrade.Key.GladiatorHullLeech;
        else if (leviathanKey == LeviathanMod.PredatorUpgrade)
            fallbackKey = Upgrade.Key.GladiatorEventHorizon;
        else
            return;

        Array icons = UpgradeIconsField?.GetValue(__instance) as Array;
        Image image = IconImageField?.GetValue(__instance) as Image;

        if (icons == null || image == null)
            return;

        for (int i = 0; i < icons.Length; i++)
        {
            object icon = icons.GetValue(i);

            if (icon == null)
                continue;

            FieldInfo iconKeyField = AccessTools.Field(icon.GetType(), "key");
            FieldInfo spriteField = AccessTools.Field(icon.GetType(), "sprite");

            if (iconKeyField == null || spriteField == null)
                continue;

            Upgrade.Key key = (Upgrade.Key)iconKeyField.GetValue(icon);

            if (key != fallbackKey)
                continue;

            Sprite sprite = spriteField.GetValue(icon) as Sprite;

            if (sprite != null)
                image.sprite = sprite;

            return;
        }
    }
}

// -----------------------------------------------------------------------------
// Live Growth application
// -----------------------------------------------------------------------------

/// <summary>
/// Growth is stored and purchased through the native upgrade system. Hook the
/// actual GameShip.SetUpgrade mutation so changing Growth anywhere immediately
/// refreshes the live Leviathan, rather than tying the behavior to one UI.
/// </summary>
[HarmonyPatch]
public static class LeviathanGrowthLiveRefreshPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(GameShip),
            "SetUpgrade",
            new Type[]
            {
                typeof(Upgrade.Key),
                typeof(int)
            }
        );
    }

    public static void Postfix(
        GameShip __instance,
        Upgrade.Key __0)
    {
        if (__instance == null ||
            __0 != LeviathanMod.GrowthUpgrade ||
            LeviathanMod.Controller == null ||
            WorldController.instance == null ||
            WorldController.instance.GetCurrentPlayerShip() != __instance)
        {
            return;
        }

        LeviathanMod.Controller.RequestGrowthRefresh(__instance);
    }
}
