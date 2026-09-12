using HarmonyLib;
using StarVortex;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Player-authored Orrery satellite designs.
///
/// Numeric slot N maps directly to satellite id N. Files live in the same
/// persistent Templates root as ordinary/Leviathan ship designs, under a dedicated
/// Orrery folder, so users can author a small ship in the normal ship builder and
/// save it directly as satellite 1, 2, 3, etc.
/// </summary>
public static class OrrerySatelliteTemplates
{
    public const string FolderName = "Orrery";
    private const string UidPrefix = "Orrery.";

    public static void EnsureDirectory()
    {
        Directory.CreateDirectory(GetDirectory());
    }

    public static bool TryNormalizeSlot(string value, out int slot)
    {
        slot = 0;
        int parsed;
        if (string.IsNullOrWhiteSpace(value) ||
            !int.TryParse(value.Trim(), out parsed) ||
            parsed < 1 || parsed > OrreryRuntime.Tuning.PersistedSatelliteDesignSlots)
        {
            return false;
        }

        slot = parsed;
        return true;
    }

    public static string GetSlotPath(int slot)
    {
        return Path.Combine(GetDirectory(), slot.ToString() + ".template");
    }

    public static bool TemplateUsesPath(Template template, string path)
    {
        if (template == null || template.metaData == null ||
            string.IsNullOrEmpty(template.metaData.file) ||
            string.IsNullOrEmpty(path))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(template.metaData.file),
                Path.GetFullPath(path),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static bool TryGetSlot(Template template, out int slot)
    {
        slot = 0;
        if (template == null || template.metaData == null)
            return false;

        string uid = template.metaData.uid;
        if (string.IsNullOrEmpty(uid) ||
            !uid.StartsWith(UidPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return TryNormalizeSlot(uid.Substring(UidPrefix.Length), out slot);
    }

    public static Template SaveSlot(int slot, string serialized)
    {
        if (slot < 1 || slot > OrreryRuntime.Tuning.PersistedSatelliteDesignSlots)
            throw new ArgumentOutOfRangeException("slot");
        if (string.IsNullOrEmpty(serialized))
            throw new ArgumentException("Satellite design is empty.", "serialized");

        EnsureDirectory();
        string path = GetSlotPath(slot);
        Template template = null;

        if (File.Exists(path))
        {
            try
            {
                template = Savable.Load<Template>(path, false);
            }
            catch
            {
                template = null;
            }
        }

        if (template == null)
            template = new Template();

        template.metaData.file = path;
        template.metaData.name = slot.ToString();
        template.metaData.uid = UidPrefix + slot.ToString();
        template.serialized = serialized;
        template.Save(true);
        return template;
    }

    public static bool TryLoadSerializedBody(int slot, out string serialized)
    {
        serialized = null;
        Template template = LoadSlot(slot);
        if (template == null || string.IsNullOrEmpty(template.serialized))
            return false;

        serialized = template.serialized;
        return true;
    }

    public static List<Template> GetPresetTemplates()
    {
        List<Template> result = new List<Template>(
            OrreryRuntime.Tuning.PersistedSatelliteDesignSlots);

        for (int slot = 1;
            slot <= OrreryRuntime.Tuning.PersistedSatelliteDesignSlots;
            slot++)
        {
            Template template = LoadSlot(slot);
            if (template != null)
                result.Add(template);
        }

        return result;
    }

    private static Template LoadSlot(int slot)
    {
        if (slot < 1 || slot > OrreryRuntime.Tuning.PersistedSatelliteDesignSlots)
            return null;

        string path = GetSlotPath(slot);
        if (!File.Exists(path))
            return null;

        try
        {
            return Savable.Load<Template>(path, false);
        }
        catch (Exception ex)
        {
            Debug.LogWarning(
                "[Orrery] Could not load satellite template " + slot + ": " + ex.Message);
            return null;
        }
    }

    private static string GetDirectory()
    {
        return Path.Combine(
            Application.persistentDataPath,
            "Templates",
            FolderName);
    }
}

/// <summary>
/// Small ship-builder bridge for saving numeric Orrery satellite designs. Kept
/// separate from Leviathan's role-based save mode so neither class needs to know
/// the other's naming rules.
/// </summary>
public sealed class OrreryShipBuilderIntegration : MonoBehaviour
{
    private static readonly FieldInfo CurrentTemplateField =
        AccessTools.Field(typeof(ShipBuilderTemplates), "currentTemplate");
    private static readonly MethodInfo UpdateButtonStatusMethod =
        AccessTools.Method(typeof(ShipBuilderTemplates), "UpdateButtonStatus");

    private ShipBuilderTemplates templates;
    private ShipBuilder shipBuilder;
    private Button saveButton;
    private bool saveMode;
    private int pendingReplaceSlot;

    public bool IsOrrerySaveMode { get { return saveMode; } }
    public bool HasPendingReplace { get { return saveMode && pendingReplaceSlot > 0; } }

    public void Configure(
        ShipBuilderTemplates shipBuilderTemplates,
        ShipBuilder builder,
        bool enabledForPlayer)
    {
        templates = shipBuilderTemplates;
        shipBuilder = builder;
        OrrerySatelliteTemplates.EnsureDirectory();

        if (templates == null || templates.saveAsButton == null)
            return;

        if (saveButton == null)
            CreateButton();

        if (saveButton != null)
            saveButton.gameObject.SetActive(enabledForPlayer);
    }

    private void CreateButton()
    {
        Button source = templates.saveAsButton;
        if (source == null || source.transform.parent == null)
            return;

        GameObject clone = UnityEngine.Object.Instantiate(
            source.gameObject,
            source.transform.parent);
        clone.name = "OrrerySaveSatelliteButton";
        saveButton = clone.GetComponent<Button>();
        if (saveButton == null)
        {
            UnityEngine.Object.Destroy(clone);
            return;
        }

        saveButton.onClick = new Button.ButtonClickedEvent();
        saveButton.onClick.AddListener(BeginSaveMode);
        LeviathanReflection.SetFirstTmpText(clone, "Save Orrery Satellite");
        PlaceButton(clone);
    }

    private void PlaceButton(GameObject clone)
    {
        Transform parent = templates.saveAsButton.transform.parent;
        LayoutGroup layout = parent.GetComponent<LayoutGroup>();
        if (layout != null)
        {
            // Leviathan's save button also inserts after Save As. Put Orrery one
            // slot later so both are stable when both classes are available.
            clone.transform.SetSiblingIndex(
                Mathf.Min(parent.childCount - 1,
                    templates.saveAsButton.transform.GetSiblingIndex() + 2));
            return;
        }

        RectTransform cloneRect = clone.GetComponent<RectTransform>();
        RectTransform saveAsRect =
            templates.saveAsButton.GetComponent<RectTransform>();
        RectTransform saveRect = templates.saveButton == null
            ? null
            : templates.saveButton.GetComponent<RectTransform>();
        if (cloneRect == null || saveAsRect == null)
            return;

        Vector2 step = new Vector2(0f, -(saveAsRect.rect.height + 8f));
        if (saveRect != null)
        {
            Vector2 nativeStep =
                saveAsRect.anchoredPosition - saveRect.anchoredPosition;
            if (nativeStep.sqrMagnitude > 4f)
                step = nativeStep;
        }

        cloneRect.anchoredPosition = saveAsRect.anchoredPosition + step * 2f;
    }

    private void BeginSaveMode()
    {
        if (templates == null || shipBuilder == null)
            return;

        templates.SaveAsClicked();
        if (templates.saveAsObject == null || !templates.saveAsObject.activeSelf)
            return;

        saveMode = true;
        pendingReplaceSlot = 0;

        int slot;
        Template current = GetCurrentTemplate();
        if (!OrrerySatelliteTemplates.TryGetSlot(current, out slot))
            slot = 1;
        SetSaveAsText(slot.ToString());
    }

    public void SaveOrrerySatellite()
    {
        if (!saveMode || templates == null || shipBuilder == null)
            return;

        if (!shipBuilder.IsValid())
        {
            shipBuilder.ShowError("Invalid ship design.");
            return;
        }

        int slot;
        if (!OrrerySatelliteTemplates.TryNormalizeSlot(GetSaveAsText(), out slot))
        {
            shipBuilder.ShowError(
                "Orrery satellite must be a number from 1 to " +
                OrreryRuntime.Tuning.PersistedSatelliteDesignSlots + ".");
            return;
        }

        string path = OrrerySatelliteTemplates.GetSlotPath(slot);
        Template current = GetCurrentTemplate();
        if (File.Exists(path) &&
            !OrrerySatelliteTemplates.TemplateUsesPath(current, path))
        {
            pendingReplaceSlot = slot;
            if (templates.replaceOverlay != null)
                templates.replaceOverlay.SetActive(true);
            return;
        }

        SaveSlot(slot);
    }

    public void ReplaceOrrerySatellite()
    {
        if (pendingReplaceSlot <= 0)
            return;

        int slot = pendingReplaceSlot;
        pendingReplaceSlot = 0;
        if (templates != null && templates.replaceOverlay != null)
            templates.replaceOverlay.SetActive(false);
        SaveSlot(slot);
    }

    public void CancelReplace()
    {
        pendingReplaceSlot = 0;
    }

    public void EndSaveMode()
    {
        saveMode = false;
        pendingReplaceSlot = 0;
    }

    private void SaveSlot(int slot)
    {
        try
        {
            Template saved = OrrerySatelliteTemplates.SaveSlot(
                slot,
                shipBuilder.SerializeParts());
            if (CurrentTemplateField != null)
                CurrentTemplateField.SetValue(templates, saved);

            LeviathanReflection.SetFieldTmpText(
                templates,
                "templateNameText",
                slot.ToString());
            if (UpdateButtonStatusMethod != null)
                UpdateButtonStatusMethod.Invoke(templates, null);
            templates.ClosePanels();
        }
        catch (Exception ex)
        {
            shipBuilder.ShowError(
                "Could not save Orrery satellite: " + ex.Message);
        }
    }

    private Template GetCurrentTemplate()
    {
        return CurrentTemplateField == null || templates == null
            ? null
            : CurrentTemplateField.GetValue(templates) as Template;
    }

    private string GetSaveAsText()
    {
        object input = LeviathanReflection.GetFieldValue(
            templates,
            "saveAsTemplateName");
        if (input == null)
            return string.Empty;

        PropertyInfo property = input.GetType().GetProperty("text");
        return property == null
            ? string.Empty
            : property.GetValue(input, null) as string ?? string.Empty;
    }

    private void SetSaveAsText(string value)
    {
        object input = LeviathanReflection.GetFieldValue(
            templates,
            "saveAsTemplateName");
        if (input == null)
            return;

        PropertyInfo property = input.GetType().GetProperty("text");
        if (property != null && property.CanWrite)
            property.SetValue(input, value, null);
    }
}

public static class OrreryPresetUI
{
    private static readonly FieldInfo FolderNameField =
        AccessTools.Field(typeof(FolderDisplay), "folderName");
    private static readonly FieldInfo PlayerLevelField =
        AccessTools.Field(typeof(ShipBuilderTemplates), "playerLevel");
    private static readonly FieldInfo ClassOverrideField =
        AccessTools.Field(typeof(ShipBuilderTemplates), "classOverride");
    private static readonly FieldInfo IsPlayerField =
        AccessTools.Field(typeof(ShipBuilderTemplates), "isPlayer");

    public static void AddFolder(ShipBuilderTemplates templates)
    {
        if (templates == null || templates.folderPrefab == null ||
            templates.templateContainer == null)
        {
            return;
        }

        List<Template> entries = OrrerySatelliteTemplates.GetPresetTemplates();
        GameObject folderObject = UnityEngine.Object.Instantiate(
            templates.folderPrefab,
            templates.templateContainer);
        FolderDisplay display = folderObject.GetComponent<FolderDisplay>();
        if (display == null)
        {
            UnityEngine.Object.Destroy(folderObject);
            return;
        }

        display.Init("Conclave", entries.Count, templates);
        if (FolderNameField != null)
            FolderNameField.SetValue(display, OrrerySatelliteTemplates.FolderName);
        LeviathanReflection.SetFieldTmpText(
            display,
            "nameText",
            OrrerySatelliteTemplates.FolderName);
        if (display.factionIcon != null)
            display.factionIcon.gameObject.SetActive(false);
    }

    public static void RenderFolder(ShipBuilderTemplates templates)
    {
        if (templates == null || templates.templateContainer == null)
            return;

        Utils.EmptyGameObjects(templates.templateContainer);
        AddBackButton(templates);
        List<Template> entries = OrrerySatelliteTemplates.GetPresetTemplates();

        int playerLevel = GetPrivateValue<int>(PlayerLevelField, templates);
        bool classOverride = GetPrivateValue<bool>(ClassOverrideField, templates);
        bool isPlayer = GetPrivateValue<bool>(IsPlayerField, templates);

        for (int i = 0; i < entries.Count; i++)
        {
            GameObject templateObject = UnityEngine.Object.Instantiate(
                templates.templatePrefab,
                templates.templateContainer);
            TemplateDisplay display = templateObject.GetComponent<TemplateDisplay>();
            if (display == null)
            {
                UnityEngine.Object.Destroy(templateObject);
                continue;
            }
            display.Init(
                playerLevel,
                classOverride,
                entries[i],
                templates,
                !isPlayer);
        }
    }

    private static void AddBackButton(ShipBuilderTemplates templates)
    {
        if (templates.folderPrefab == null)
            return;

        GameObject backObject = UnityEngine.Object.Instantiate(
            templates.folderPrefab,
            templates.templateContainer);
        FolderDisplay display = backObject.GetComponent<FolderDisplay>();
        if (display != null)
            display.Init(string.Empty, 0, templates);
    }

    private static T GetPrivateValue<T>(FieldInfo field, object instance)
    {
        if (field == null || instance == null)
            return default(T);
        object value = field.GetValue(instance);
        return value is T ? (T)value : default(T);
    }
}

public static class OrreryShipBuilderAvailability
{
    public static bool IsActiveForLocalPlayer()
    {
        CoreOwnerContext context = CoreClassRuntime.CurrentContext;
        return context != null && context.IsValid &&
            context.ClassId == CoreClassId.Orrery &&
            context.Ship != null && OrreryRuntime.IsActive(context.Ship);
    }
}

[HarmonyPatch]
public static class OrreryShipBuilderTemplatesInitPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(ShipBuilderTemplates),
            "Init",
            new Type[]
            {
                typeof(int), typeof(bool), typeof(string[]), typeof(string[]),
                typeof(ShipBuilder), typeof(bool), typeof(bool)
            });
    }

    public static void Postfix(
        ShipBuilderTemplates __instance,
        ShipBuilder __4,
        bool __5)
    {
        OrreryShipBuilderIntegration integration =
            __instance.GetComponent<OrreryShipBuilderIntegration>();
        if (integration == null)
            integration = __instance.gameObject.AddComponent<OrreryShipBuilderIntegration>();
        integration.Configure(
            __instance,
            __4,
            __5 && OrreryShipBuilderAvailability.IsActiveForLocalPlayer());
    }
}

[HarmonyPatch]
public static class OrreryShipBuilderSetSectionPatch
{
    private static readonly FieldInfo CurrentFolderField =
        AccessTools.Field(typeof(ShipBuilderTemplates), "currentFolder");

    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(ShipBuilderTemplates), "SetSection");
    }

    public static bool Prefix(ShipBuilderTemplates __instance, object[] __args)
    {
        int section = GetSection(__args);
        string folder = GetCurrentFolder(__instance);
        bool orreryFolder = folder.Equals(
            OrrerySatelliteTemplates.FolderName,
            StringComparison.Ordinal);
        bool enabled = OrreryShipBuilderAvailability.IsActiveForLocalPlayer() &&
            LeviathanPresetUI.IsPlayerBuilder(__instance);

        if (orreryFolder && (!enabled || section != 1))
        {
            if (CurrentFolderField != null)
                CurrentFolderField.SetValue(__instance, string.Empty);
            return true;
        }

        if (section == 1 && orreryFolder && enabled)
        {
            OrreryPresetUI.RenderFolder(__instance);
            return false;
        }

        return true;
    }

    public static void Postfix(ShipBuilderTemplates __instance, object[] __args)
    {
        if (GetSection(__args) != 1 ||
            !OrreryShipBuilderAvailability.IsActiveForLocalPlayer() ||
            !LeviathanPresetUI.IsPlayerBuilder(__instance) ||
            !string.IsNullOrEmpty(GetCurrentFolder(__instance)))
        {
            return;
        }

        OrreryPresetUI.AddFolder(__instance);
    }

    private static int GetSection(object[] args)
    {
        return args == null || args.Length == 0 || args[0] == null
            ? -1
            : Convert.ToInt32(args[0]);
    }

    private static string GetCurrentFolder(ShipBuilderTemplates templates)
    {
        return CurrentFolderField == null || templates == null
            ? string.Empty
            : CurrentFolderField.GetValue(templates) as string ?? string.Empty;
    }
}

[HarmonyPatch(typeof(ShipBuilderTemplates), "SaveAsTemplate")]
public static class OrreryShipBuilderSaveAsTemplatePatch
{
    public static bool Prefix(ShipBuilderTemplates __instance)
    {
        OrreryShipBuilderIntegration integration =
            __instance.GetComponent<OrreryShipBuilderIntegration>();
        if (integration == null || !integration.IsOrrerySaveMode)
            return true;
        integration.SaveOrrerySatellite();
        return false;
    }
}

[HarmonyPatch(typeof(ShipBuilderTemplates), "Replace")]
public static class OrreryShipBuilderReplacePatch
{
    public static bool Prefix(ShipBuilderTemplates __instance)
    {
        OrreryShipBuilderIntegration integration =
            __instance.GetComponent<OrreryShipBuilderIntegration>();
        if (integration == null || !integration.HasPendingReplace)
            return true;
        integration.ReplaceOrrerySatellite();
        return false;
    }
}

[HarmonyPatch(typeof(ShipBuilderTemplates), "CancelReplace")]
public static class OrreryShipBuilderCancelReplacePatch
{
    public static void Postfix(ShipBuilderTemplates __instance)
    {
        OrreryShipBuilderIntegration integration =
            __instance.GetComponent<OrreryShipBuilderIntegration>();
        if (integration != null)
            integration.CancelReplace();
    }
}

[HarmonyPatch(typeof(ShipBuilderTemplates), "ClosePanels")]
public static class OrreryShipBuilderClosePanelsPatch
{
    public static void Postfix(ShipBuilderTemplates __instance)
    {
        OrreryShipBuilderIntegration integration =
            __instance.GetComponent<OrreryShipBuilderIntegration>();
        if (integration != null)
            integration.EndSaveMode();
    }
}
