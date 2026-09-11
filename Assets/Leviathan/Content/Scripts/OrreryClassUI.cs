using HarmonyLib;
using StarVortex;
using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Minimal native UI bridge for custom Orrery category 18. Star Vortex class
/// panels enumerate the compile-time Upgrade.Category enum, so custom categories
/// must insert their own display after the native rebuild. This intentionally
/// mirrors the proven Leviathan shim without introducing a generic UI manager.
/// </summary>
public static class OrreryClassUI
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
        AccessTools.Method(typeof(UpgradeClassDisplay), "ShowBuyButton", new Type[] { typeof(bool) });
    private static readonly MethodInfo ShowSellButtonMethod =
        AccessTools.Method(typeof(UpgradeClassDisplay), "ShowSellButton", new Type[] { typeof(bool) });
    private static readonly MethodInfo UpgradeDisplaySelectMethod =
        AccessTools.Method(typeof(UpgradeDisplay), "Select");
    private static readonly MethodInfo IsSecondaryClassUnlockedMethod =
        AccessTools.Method(typeof(Pilot), "IsSecondaryClassUnlocked", new Type[] { typeof(Upgrade.Category) });
    private static readonly MethodInfo HasUpgradeInCategoryMethod =
        AccessTools.Method(typeof(Pilot), "HasUpgradeInCategory", new Type[] { typeof(Upgrade.Category) });
    private static readonly MethodInfo CountItemMethod =
        AccessTools.Method(typeof(GameShip), "CountItem", new Type[] { typeof(string) });

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

    public static void AddToSciencePanel(SciencePanel panel, object[] rebuildArgs)
    {
        if (panel == null)
            return;

        GameShip ship = GetShipFromArgs(rebuildArgs);
        if (ship == null && ScienceShipField != null)
            ship = ScienceShipField.GetValue(panel) as GameShip;
        if (ship == null || ship.pilot == null)
            return;

        bool unlocked = IsClassUnlocked(ship.pilot);
        Transform locked = GetTransform(ScienceLockedContainerField == null ? null : ScienceLockedContainerField.GetValue(panel));
        Transform unlockedContainer = GetTransform(ScienceUnlockedContainerField == null ? null : ScienceUnlockedContainerField.GetValue(panel));
        IList tracked = ScienceUnlockedDisplaysField == null ? null : ScienceUnlockedDisplaysField.GetValue(panel) as IList;

        RetireDisplays(locked, tracked);
        RetireDisplays(unlockedContainer, tracked);

        Transform parent = unlocked ? unlockedContainer : locked;
        if (parent == null)
            return;

        UpgradeClassDisplay display = InstantiateClassDisplay(
            ScienceClassPrefabField == null ? null : ScienceClassPrefabField.GetValue(panel),
            parent);
        if (display == null)
        {
            Debug.LogError("[Orrery] Could not instantiate SciencePanel class display.");
            return;
        }

        SetField(ForceActiveColorField, display, !unlocked);
        SetField(PreviewOnlyField, display, !unlocked);
        if (ScienceTooltipContainerField != null)
            SetField(TooltipContainerOverrideField, display, ScienceTooltipContainerField.GetValue(panel));

        InvokeClassInit(display, ship, panel);
        UpgradeDisplay progression = AddProgression(display);
        RestoreRequestedSelection(progression, rebuildArgs);

        if (unlocked)
        {
            ShowSellButtonMethod.Invoke(display, new object[] { !HasUpgradeInCategory(ship.pilot) });
            if (tracked != null)
                tracked.Add(display);
        }
        else
        {
            ShowBuyButtonMethod.Invoke(display, new object[] { HasInfectedSeed(panel, ship) });
        }
    }

    public static void AddToCoreUpgrades(CoreUpgrades core, object[] rebuildArgs)
    {
        if (core == null)
            return;

        GameShip ship = CoreShipField == null ? null : CoreShipField.GetValue(core) as GameShip;
        if (ship == null || ship.pilot == null || !IsClassUnlocked(ship.pilot))
            return;

        Transform parent = GetTransform(CoreSecondaryContainerField == null ? null : CoreSecondaryContainerField.GetValue(core));
        if (parent == null)
            return;

        IList tracked = CoreClassDisplaysField == null ? null : CoreClassDisplaysField.GetValue(core) as IList;
        RetireDisplays(parent, tracked);

        UpgradeClassDisplay display = InstantiateClassDisplay(
            CoreClassPrefabField == null ? null : CoreClassPrefabField.GetValue(core),
            parent);
        if (display == null)
        {
            Debug.LogError("[Orrery] Could not instantiate CoreUpgrades class display.");
            return;
        }

        InvokeClassInit(display, ship, core);
        UpgradeDisplay progression = AddProgression(display);
        RestoreRequestedSelection(progression, rebuildArgs);
        if (tracked != null)
            tracked.Add(display);
    }

    private static void InvokeClassInit(UpgradeClassDisplay display, GameShip ship, object context)
    {
        if (ClassInitMethod == null)
            throw new InvalidOperationException("UpgradeClassDisplay.Init was not found for Orrery UI.");
        ClassInitMethod.Invoke(display, new object[]
        {
            OrrerySpecializationPolicy.OrreryCategory,
            ship,
            context
        });
    }

    private static UpgradeDisplay AddProgression(UpgradeClassDisplay display)
    {
        if (AddUpgradeMethod == null)
            throw new InvalidOperationException("UpgradeClassDisplay.AddUpgrade was not found for Orrery UI.");
        return AddUpgradeMethod.Invoke(display, new object[] { OrrerySpecializationPolicy.Upgrade }) as UpgradeDisplay;
    }

    private static bool IsClassUnlocked(Pilot pilot)
    {
        if (pilot == null || IsSecondaryClassUnlockedMethod == null)
            return false;
        object result = IsSecondaryClassUnlockedMethod.Invoke(
            pilot,
            new object[] { OrrerySpecializationPolicy.OrreryCategory });
        return result is bool && (bool)result;
    }

    private static bool HasUpgradeInCategory(Pilot pilot)
    {
        if (pilot == null || HasUpgradeInCategoryMethod == null)
            return false;
        object result = HasUpgradeInCategoryMethod.Invoke(
            pilot,
            new object[] { OrrerySpecializationPolicy.OrreryCategory });
        return result is bool && (bool)result;
    }

    private static bool HasInfectedSeed(SciencePanel panel, GameShip ship)
    {
        if (panel == null || ship == null || ScienceSeedField == null || CountItemMethod == null)
            return false;
        object seed = ScienceSeedField.GetValue(panel);
        string filename = GetFilename(seed);
        if (string.IsNullOrEmpty(filename))
            return false;
        object result = CountItemMethod.Invoke(ship, new object[] { filename });
        return result is int && (int)result >= 1;
    }

    private static string GetFilename(object source)
    {
        if (source == null)
            return null;
        Type type = source.GetType();
        PropertyInfo property = type.GetProperty(
            "filename",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property != null)
            return property.GetValue(source, null) as string;
        FieldInfo field = type.GetField(
            "filename",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return field == null ? null : field.GetValue(source) as string;
    }

    private static UpgradeClassDisplay InstantiateClassDisplay(object prefabValue, Transform parent)
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

    private static void RetireDisplays(Transform container, IList tracked)
    {
        if (container == null || ClassCategoryField == null)
            return;
        UpgradeClassDisplay[] displays = container.GetComponentsInChildren<UpgradeClassDisplay>(true);
        for (int i = 0; i < displays.Length; i++)
        {
            UpgradeClassDisplay display = displays[i];
            if (display == null)
                continue;
            Upgrade.Category category = (Upgrade.Category)ClassCategoryField.GetValue(display);
            if (category != OrrerySpecializationPolicy.OrreryCategory)
                continue;
            if (tracked != null && tracked.Contains(display))
                tracked.Remove(display);
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
        Transform transform = value as Transform;
        if (transform != null)
            return transform;
        Component component = value as Component;
        if (component != null)
            return component.transform;
        GameObject gameObject = value as GameObject;
        return gameObject == null ? null : gameObject.transform;
    }

    private static void RestoreRequestedSelection(UpgradeDisplay progression, object[] args)
    {
        if (progression == null || UpgradeDisplaySelectMethod == null ||
            args == null || args.Length == 0 || args[0] == null)
            return;
        try
        {
            Upgrade.Key selected = (Upgrade.Key)Convert.ToInt32(args[0]);
            if (selected == OrrerySpecializationPolicy.UpgradeKey)
                UpgradeDisplaySelectMethod.Invoke(progression, null);
        }
        catch
        {
            // Native rebuild args are not guaranteed to carry a selected key.
        }
    }

    private static void SetField(FieldInfo field, object target, object value)
    {
        if (field != null && target != null)
            field.SetValue(target, value);
    }
}

// -----------------------------------------------------------------------------
// Native metadata/presentation shims for category 18 and key 88.
// -----------------------------------------------------------------------------

[HarmonyPatch]
public static class OrreryUpgradeGetCategoryPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(Upgrade), "GetCategory",
            new Type[] { typeof(Upgrade.Category), typeof(bool) });
    }

    public static bool Prefix(Upgrade.Category __0, ref string __result)
    {
        if (__0 != OrrerySpecializationPolicy.OrreryCategory)
            return true;
        __result = "Orrery";
        return false;
    }
}

[HarmonyPatch]
public static class OrreryUpgradeGetNamePatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(Upgrade), "GetName",
            new Type[] { typeof(Upgrade.Key), typeof(bool) });
    }

    public static bool Prefix(Upgrade.Key __0, ref string __result)
    {
        if (__0 != OrrerySpecializationPolicy.UpgradeKey)
            return true;
        __result = "Orrery";
        return false;
    }
}

[HarmonyPatch]
public static class OrreryUpgradeGetDescriptionPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(Upgrade), "GetDescription",
            new Type[] { typeof(Upgrade.Key), typeof(bool) });
    }

    public static bool Prefix(Upgrade.Key __0, ref string __result)
    {
        if (__0 != OrrerySpecializationPolicy.UpgradeKey)
            return true;
        __result =
            "Awakens the Orrery class and grants 2 Orrery Points per rank. " +
            "Orrery assembles elemental formulas through its orbiting satellites.";
        return false;
    }
}

[HarmonyPatch]
public static class OrreryUpgradeIsFreePatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(Upgrade), "IsFree",
            new Type[] { typeof(Upgrade.Key) });
    }

    public static bool Prefix(Upgrade.Key __0, ref bool __result)
    {
        if (__0 != OrrerySpecializationPolicy.UpgradeKey)
            return true;
        __result = false;
        return false;
    }
}

// Temporary presentation fallback. This is deliberately visual-only and can be
// replaced when Orrery receives its own art/color without changing class logic.
[HarmonyPatch]
public static class OrreryCategoryIconPatch
{
    private static MethodBase target;

    public static MethodBase TargetMethod()
    {
        if (target != null)
            return target;
        target = typeof(WorldController).GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(m =>
                m.Name == "GetUpgradeCategoryIcon" &&
                m.ReturnType == typeof(Sprite) &&
                m.GetParameters().Length >= 1 &&
                m.GetParameters()[0].ParameterType == typeof(Upgrade.Category));
        return target;
    }

    public static bool Prefix(
        WorldController __instance,
        object[] __args,
        ref Sprite __result)
    {
        if (__args == null || __args.Length == 0 ||
            (Upgrade.Category)__args[0] != OrrerySpecializationPolicy.OrreryCategory)
            return true;
        object[] fallback = (object[])__args.Clone();
        fallback[0] = Upgrade.Category.Tempest;
        __result = (Sprite)target.Invoke(__instance, fallback);
        return false;
    }
}

[HarmonyPatch]
public static class OrreryCategoryColorPatch
{
    private static MethodBase target;

    public static MethodBase TargetMethod()
    {
        if (target != null)
            return target;
        target = typeof(Palette).GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(m =>
                m.Name == "GetUpgradeCategoryColor" &&
                m.ReturnType == typeof(Color) &&
                m.GetParameters().Length >= 1 &&
                m.GetParameters()[0].ParameterType == typeof(Upgrade.Category));
        return target;
    }

    public static bool Prefix(Palette __instance, object[] __args, ref Color __result)
    {
        if (__args == null || __args.Length == 0 ||
            (Upgrade.Category)__args[0] != OrrerySpecializationPolicy.OrreryCategory)
            return true;
        object[] fallback = (object[])__args.Clone();
        fallback[0] = Upgrade.Category.Tempest;
        __result = (Color)target.Invoke(__instance, fallback);
        return false;
    }
}

[HarmonyPatch(typeof(Pilot), "HasLockedSecondaryClass")]
public static class OrreryHasLockedSecondaryClassPatch
{
    public static void Postfix(Pilot __instance, ref bool __result)
    {
        if (__result || __instance == null || IsSecondaryClassUnlockedMethod == null)
            return;
        object result = IsSecondaryClassUnlockedMethod.Invoke(
            __instance,
            new object[] { OrrerySpecializationPolicy.OrreryCategory });
        if (result is bool && !(bool)result)
            __result = true;
    }

    private static readonly MethodInfo IsSecondaryClassUnlockedMethod =
        AccessTools.Method(typeof(Pilot), "IsSecondaryClassUnlocked",
            new Type[] { typeof(Upgrade.Category) });
}

[HarmonyPatch(typeof(SciencePanel), "RebuildSecondaryClasses")]
public static class OrrerySciencePanelClassPatch
{
    public static void Postfix(SciencePanel __instance, object[] __args)
    {
        OrreryClassUI.AddToSciencePanel(__instance, __args);
    }
}

[HarmonyPatch(typeof(CoreUpgrades), "RebuildUpgrades")]
public static class OrreryCoreUpgradesClassPatch
{
    public static void Postfix(CoreUpgrades __instance, object[] __args)
    {
        OrreryClassUI.AddToCoreUpgrades(__instance, __args);
    }
}

[HarmonyPatch(typeof(UpgradeDisplay), "Init")]
public static class OrreryProgressionIconPatch
{
    public static void Postfix(UpgradeDisplay __instance, Upgrade __0)
    {
        if (__instance == null || __0 == null ||
            __0.key != OrrerySpecializationPolicy.UpgradeKey ||
            __instance.iconImage == null || WorldController.instance == null)
            return;
        __instance.iconImage.sprite = WorldController.instance.GetUpgradeCategoryIcon(
            OrrerySpecializationPolicy.OrreryCategory);
    }
}
