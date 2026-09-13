using HarmonyLib;
using StarVortex;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using static StarVortex.Damageable;
using static StarVortex.ShipPart;

public class LeviathanMod : IStarVortexMod
{
    // Custom enum values immediately after the game's current native ranges.
    // These are persisted through the same Pilot data structures as native
    // secondary classes/upgrades.
    public const Upgrade.Category LeviathanCategory =
        (Upgrade.Category)17;

    public const Upgrade.Key ConstrictorUpgrade =
        (Upgrade.Key)82;

    public const Upgrade.Key PredatorUpgrade =
        (Upgrade.Key)83;

    public const Upgrade.Key BehemothUpgrade =
        (Upgrade.Key)84;
    public const Upgrade.Key StarfireUpgrade =
        (Upgrade.Key)85;

    public const Upgrade.Key StellarConverterUpgrade =
        (Upgrade.Key)86;

    private static GameObject controllerObject;

    public static LeviathanController Controller { get; private set; }

    public void Init(ModInfo modInfo, Harmony harmony)
    {
        Debug.Log("[Leviathan] Native skill-class build Init");

        // Register Leviathan skills into the game's native Upgrade array and rebuild
        // Upgrade's key lookup before any UI or Pilot code can request them.
        LeviathanSkillSystem.Register();

        // Transport schemas must exist before publishers or lifecycle callbacks.
        // Registration cannot rely on Harmony hooks: PatchAll runs below.
        // These calls are idempotent and independent of policy registration.
        CoreNetwork.RegisterDefaultSlots();
        OrreryPresentationNetwork.EnsureInitialized();

        // Catalogs and slot identities must exist before any lifecycle callback.
        if (CoreSpecializationPolicies.Get(CoreClassId.Leviathan) == null)
        {
            CoreSpecializationPolicies.Register(new LeviathanSpecializationPolicy());
            CoreSpecializationPolicies.Register(new OrrerySpecializationPolicy());
            CoreClassRuntime.RegisterLocalClass(CoreClassId.Leviathan,
                pilot => pilot != null && pilot.GetUpgradeLevel(LeviathanSpecializationCurrency.UpgradeKey) >= 1,
                new CoreClassLifecycle(
                    context => { if (Controller != null) Controller.SetPlayerShip(context.Ship); },
                    context => { if (Controller != null) Controller.ClearPlayerShip(); }));
            CoreClassRuntime.RegisterLocalClass(CoreClassId.Orrery,
                pilot => pilot != null && pilot.GetUpgradeLevel(OrrerySpecializationPolicy.UpgradeKey) >= 1,
                new CoreClassLifecycle(context => OrreryRuntime.Activate(context.Ship),
                    context => OrreryRuntime.Deactivate(context.Ship)));
        }

        harmony.PatchAll();

        if (controllerObject != null)
            return;

        controllerObject = new GameObject("Leviathan Runtime");
        UnityEngine.Object.DontDestroyOnLoad(controllerObject);

        Controller = controllerObject.AddComponent<LeviathanController>();

        CoreClassRuntime.Refresh();
        Debug.Log("[Leviathan] Runtime controller created.");
    }

    public void OnGameLoaded()
    {
    }

    public void Shutdown()
    {
        Controller = null;

        if (controllerObject != null)
            UnityEngine.Object.Destroy(controllerObject);
    }

    public static bool PlayerHasLeviathan()
    {
        Pilot pilot = null;

        if (WorldController.instance != null)
        {
            GameShip player =
                WorldController.instance.GetCurrentPlayerShip();

            if (player != null)
                pilot = GameShip.GetPlayerSourcePilot(player);
        }

        if (pilot == null &&
            Core.instance != null &&
            Core.instance.player != null &&
            Core.instance.player.ship != null)
        {
            pilot = Core.instance.player.ship.pilot;
        }

        return pilot != null &&
            pilot.GetUpgradeLevel(LeviathanSpecializationCurrency.UpgradeKey) >= 1;
    }
}

[HarmonyPatch(typeof(WorldController), "PostInit")]
public static class LeviathanWorldPostInitPatch
{
    public static void Postfix(WorldController __instance)
    {
        CoreClassRuntime.WorldEntered();
    }
}

[HarmonyPatch(typeof(WorldController), "SetCurrentPlayerShip")]
public static class LeviathanPlayerShipChangedPatch
{
    public static void Postfix(GameShip __0)
    {
        CoreClassRuntime.Refresh();
    }
}

[HarmonyPatch(typeof(WorldController), "OnDestroy")]
public static class LeviathanWorldDestroyedPatch
{
    public static void Prefix()
    {
        CoreClassRuntime.Reset();
        LeviathanSegmentStatusProtection.Reset();
        LeviathanSegmentDamageLimiter.Reset();
        LeviathanSegmentTransferProtection.Reset();
        LeviathanAttachmentNormalizer.Reset();
    }
}

[HarmonyPatch]
public static class LeviathanShipBuilderTemplatesInitPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(ShipBuilderTemplates),
            "Init",
            new Type[]
            {
                typeof(int),
                typeof(bool),
                typeof(string[]),
                typeof(string[]),
                typeof(ShipBuilder),
                typeof(bool),
                typeof(bool)
            }
        );
    }

    public static void Postfix(
        ShipBuilderTemplates __instance,
        ShipBuilder __4,
        bool __5)
    {
        LeviathanShipBuilderIntegration integration =
            __instance.GetComponent<LeviathanShipBuilderIntegration>();

        if (integration == null)
        {
            integration =
                __instance.gameObject.AddComponent<
                    LeviathanShipBuilderIntegration>();
        }

        integration.Configure(
            __instance,
            __4,
            __5 && LeviathanMod.PlayerHasLeviathan()
        );
    }
}

[HarmonyPatch]
public static class LeviathanShipBuilderSetSectionPatch
{
    private static readonly FieldInfo CurrentFolderField =
        AccessTools.Field(typeof(ShipBuilderTemplates), "currentFolder");

    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(ShipBuilderTemplates),
            "SetSection"
        );
    }

    public static bool Prefix(
        ShipBuilderTemplates __instance,
        object[] __args)
    {
        int section = GetSection(__args);
        string folder = GetCurrentFolder(__instance);

        bool leviathanFolder = folder.Equals(
            LeviathanSegmentTemplates.FolderName,
            StringComparison.Ordinal
        );

        bool enabled = LeviathanMod.PlayerHasLeviathan() &&
            LeviathanPresetUI.IsPlayerBuilder(__instance);

        if (leviathanFolder && (!enabled || section != 1))
        {
            CurrentFolderField.SetValue(__instance, string.Empty);
            return true;
        }

        if (section == 1 && leviathanFolder && enabled)
        {
            LeviathanPresetUI.RenderFolder(__instance);
            return false;
        }

        return true;
    }

    public static void Postfix(
        ShipBuilderTemplates __instance,
        object[] __args)
    {
        if (GetSection(__args) != 1 ||
            !LeviathanMod.PlayerHasLeviathan() ||
            !LeviathanPresetUI.IsPlayerBuilder(__instance))
        {
            return;
        }

        if (!string.IsNullOrEmpty(GetCurrentFolder(__instance)))
            return;

        LeviathanPresetUI.AddFolder(__instance);
    }

    private static int GetSection(object[] args)
    {
        if (args == null || args.Length == 0 || args[0] == null)
            return -1;

        return Convert.ToInt32(args[0]);
    }

    private static string GetCurrentFolder(
        ShipBuilderTemplates templates)
    {
        if (CurrentFolderField == null || templates == null)
            return string.Empty;

        return CurrentFolderField.GetValue(templates) as string ??
            string.Empty;
    }
}

[HarmonyPatch(typeof(ShipBuilderTemplates), "SaveAsTemplate")]
public static class LeviathanShipBuilderSaveAsTemplatePatch
{
    public static bool Prefix(ShipBuilderTemplates __instance)
    {
        LeviathanShipBuilderIntegration integration =
            __instance.GetComponent<LeviathanShipBuilderIntegration>();

        if (integration == null || !integration.IsLeviathanSaveMode)
            return true;

        integration.SaveLeviathanPart();
        return false;
    }
}

[HarmonyPatch(typeof(ShipBuilderTemplates), "Replace")]
public static class LeviathanShipBuilderReplacePatch
{
    public static bool Prefix(ShipBuilderTemplates __instance)
    {
        LeviathanShipBuilderIntegration integration =
            __instance.GetComponent<LeviathanShipBuilderIntegration>();

        if (integration == null ||
            !integration.HasPendingLeviathanReplace)
        {
            return true;
        }

        integration.ReplaceLeviathanPart();
        return false;
    }
}

[HarmonyPatch(typeof(ShipBuilderTemplates), "CancelReplace")]
public static class LeviathanShipBuilderCancelReplacePatch
{
    public static void Postfix(ShipBuilderTemplates __instance)
    {
        LeviathanShipBuilderIntegration integration =
            __instance.GetComponent<LeviathanShipBuilderIntegration>();

        if (integration != null)
            integration.CancelLeviathanReplace();
    }
}

[HarmonyPatch(typeof(ShipBuilderTemplates), "ClosePanels")]
public static class LeviathanShipBuilderClosePanelsPatch
{
    public static void Postfix(ShipBuilderTemplates __instance)
    {
        LeviathanShipBuilderIntegration integration =
            __instance.GetComponent<LeviathanShipBuilderIntegration>();

        if (integration != null)
            integration.EndLeviathanSaveMode();
    }
}

public class LeviathanShipBuilderIntegration : MonoBehaviour
{
    private static readonly FieldInfo CurrentTemplateField =
        AccessTools.Field(typeof(ShipBuilderTemplates), "currentTemplate");

    private static readonly MethodInfo UpdateButtonStatusMethod =
        AccessTools.Method(typeof(ShipBuilderTemplates), "UpdateButtonStatus");

    private ShipBuilderTemplates templates;
    private ShipBuilder shipBuilder;
    private Button leviathanButton;
    private bool leviathanSaveMode;
    private string pendingReplaceRole;

    public bool IsLeviathanSaveMode
    {
        get { return leviathanSaveMode; }
    }

    public bool HasPendingLeviathanReplace
    {
        get
        {
            return leviathanSaveMode &&
                !string.IsNullOrEmpty(pendingReplaceRole);
        }
    }

    public void Configure(
        ShipBuilderTemplates shipBuilderTemplates,
        ShipBuilder builder,
        bool enabledForPlayer)
    {
        templates = shipBuilderTemplates;
        shipBuilder = builder;

        if (templates == null || templates.saveAsButton == null)
            return;

        if (leviathanButton == null)
            CreateButton();

        if (leviathanButton != null)
            leviathanButton.gameObject.SetActive(enabledForPlayer);
    }

    private void CreateButton()
    {
        Button source = templates.saveAsButton;

        if (source == null || source.transform.parent == null)
            return;

        GameObject clone = UnityEngine.Object.Instantiate(
            source.gameObject,
            source.transform.parent
        );

        clone.name = "LeviathanSavePartButton";

        leviathanButton = clone.GetComponent<Button>();

        if (leviathanButton == null)
        {
            UnityEngine.Object.Destroy(clone);
            return;
        }

        leviathanButton.onClick = new Button.ButtonClickedEvent();
        leviathanButton.onClick.AddListener(BeginLeviathanSaveMode);

        LeviathanReflection.SetFirstTmpText(
            clone,
            "Save Leviathan Part"
        );

        PlaceButton(clone);
    }

    private void PlaceButton(GameObject clone)
    {
        Transform parent = templates.saveAsButton.transform.parent;
        LayoutGroup layout = parent.GetComponent<LayoutGroup>();

        if (layout != null)
        {
            clone.transform.SetSiblingIndex(
                templates.saveAsButton.transform.GetSiblingIndex() + 1
            );
            return;
        }

        RectTransform cloneRect = clone.GetComponent<RectTransform>();
        RectTransform saveAsRect =
            templates.saveAsButton.GetComponent<RectTransform>();
        RectTransform saveRect =
            templates.saveButton == null
                ? null
                : templates.saveButton.GetComponent<RectTransform>();

        if (cloneRect == null || saveAsRect == null)
            return;

        Vector2 step = new Vector2(
            0f,
            -(saveAsRect.rect.height + 8f)
        );

        if (saveRect != null)
        {
            Vector2 nativeStep =
                saveAsRect.anchoredPosition - saveRect.anchoredPosition;

            if (nativeStep.sqrMagnitude > 4f)
                step = nativeStep;
        }

        cloneRect.anchoredPosition =
            saveAsRect.anchoredPosition + step;
    }

    private void BeginLeviathanSaveMode()
    {
        if (templates == null || shipBuilder == null)
            return;

        templates.SaveAsClicked();

        if (templates.saveAsObject == null ||
            !templates.saveAsObject.activeSelf)
        {
            return;
        }

        leviathanSaveMode = true;
        pendingReplaceRole = null;

        string role;
        Template current = GetCurrentTemplate();

        if (!LeviathanSegmentTemplates.TryGetRole(
                current,
                out role))
        {
            role = "Body";
        }

        SetSaveAsText(role);
    }

    public void SaveLeviathanPart()
    {
        if (!leviathanSaveMode ||
            templates == null ||
            shipBuilder == null)
        {
            return;
        }

        if (!shipBuilder.IsValid())
        {
            shipBuilder.ShowError("Invalid ship design.");
            return;
        }

        string role;

        if (!LeviathanSegmentTemplates.TryNormalizeRole(
                GetSaveAsText(),
                out role))
        {
            shipBuilder.ShowError(
                "Leviathan part must be Body, a numbered body slot, Tail, Tail_a/Tail_b, Tail_aN/Tail_bN, or BifurcateN."
            );
            return;
        }

        string path = LeviathanSegmentTemplates.GetRolePath(role);
        Template current = GetCurrentTemplate();

        if (File.Exists(path) &&
            !LeviathanSegmentTemplates.TemplateUsesPath(
                current,
                path))
        {
            pendingReplaceRole = role;

            if (templates.replaceOverlay != null)
                templates.replaceOverlay.SetActive(true);

            return;
        }

        SaveRole(role);
    }

    public void ReplaceLeviathanPart()
    {
        if (string.IsNullOrEmpty(pendingReplaceRole))
            return;

        string role = pendingReplaceRole;
        pendingReplaceRole = null;

        if (templates != null && templates.replaceOverlay != null)
            templates.replaceOverlay.SetActive(false);

        SaveRole(role);
    }

    public void CancelLeviathanReplace()
    {
        pendingReplaceRole = null;
    }

    public void EndLeviathanSaveMode()
    {
        leviathanSaveMode = false;
        pendingReplaceRole = null;
    }

    private void SaveRole(string role)
    {
        try
        {
            Template saved = LeviathanSegmentTemplates.SaveRole(
                role,
                shipBuilder.SerializeParts()
            );

            if (CurrentTemplateField != null)
                CurrentTemplateField.SetValue(templates, saved);

            LeviathanReflection.SetFieldTmpText(
                templates,
                "templateNameText",
                role
            );

            if (UpdateButtonStatusMethod != null)
                UpdateButtonStatusMethod.Invoke(templates, null);

            templates.ClosePanels();
        }
        catch (Exception ex)
        {
            shipBuilder.ShowError(
                "Could not save Leviathan part: " + ex.Message
            );
        }
    }

    private Template GetCurrentTemplate()
    {
        if (CurrentTemplateField == null || templates == null)
            return null;

        return CurrentTemplateField.GetValue(templates) as Template;
    }

    private string GetSaveAsText()
    {
        object input = LeviathanReflection.GetFieldValue(
            templates,
            "saveAsTemplateName"
        );

        if (input == null)
            return string.Empty;

        PropertyInfo property =
            input.GetType().GetProperty("text");

        if (property == null)
            return string.Empty;

        return property.GetValue(input, null) as string ?? string.Empty;
    }

    private void SetSaveAsText(string value)
    {
        object input = LeviathanReflection.GetFieldValue(
            templates,
            "saveAsTemplateName"
        );

        if (input == null)
            return;

        PropertyInfo property =
            input.GetType().GetProperty("text");

        if (property != null && property.CanWrite)
            property.SetValue(input, value, null);
    }
}

public static class LeviathanPresetUI
{
    private static readonly FieldInfo FolderNameField =
        AccessTools.Field(typeof(FolderDisplay), "folderName");

    private static readonly FieldInfo PlayerLevelField =
        AccessTools.Field(typeof(ShipBuilderTemplates), "playerLevel");

    private static readonly FieldInfo ClassOverrideField =
        AccessTools.Field(typeof(ShipBuilderTemplates), "classOverride");

    private static readonly FieldInfo IsPlayerField =
        AccessTools.Field(typeof(ShipBuilderTemplates), "isPlayer");

    public static bool IsPlayerBuilder(
        ShipBuilderTemplates templates)
    {
        return GetPrivateValue<bool>(
            IsPlayerField,
            templates
        );
    }

    public static void AddFolder(ShipBuilderTemplates templates)
    {
        if (templates == null ||
            templates.folderPrefab == null ||
            templates.templateContainer == null)
        {
            return;
        }

        List<Template> entries =
            LeviathanSegmentTemplates.GetPresetTemplates();

        GameObject folderObject = UnityEngine.Object.Instantiate(
            templates.folderPrefab,
            templates.templateContainer
        );

        FolderDisplay display =
            folderObject.GetComponent<FolderDisplay>();

        if (display == null)
        {
            UnityEngine.Object.Destroy(folderObject);
            return;
        }

        display.Init("Conclave", entries.Count, templates);

        if (FolderNameField != null)
        {
            FolderNameField.SetValue(
                display,
                LeviathanSegmentTemplates.FolderName
            );
        }

        LeviathanReflection.SetFieldTmpText(
            display,
            "nameText",
            LeviathanSegmentTemplates.FolderName
        );

        if (display.factionIcon != null)
            display.factionIcon.gameObject.SetActive(false);
    }

    public static void RenderFolder(ShipBuilderTemplates templates)
    {
        if (templates == null || templates.templateContainer == null)
            return;

        Utils.EmptyGameObjects(templates.templateContainer);

        AddBackButton(templates);

        List<Template> entries =
            LeviathanSegmentTemplates.GetPresetTemplates();

        int playerLevel = GetPrivateValue<int>(
            PlayerLevelField,
            templates
        );

        bool classOverride = GetPrivateValue<bool>(
            ClassOverrideField,
            templates
        );

        bool isPlayer = GetPrivateValue<bool>(
            IsPlayerField,
            templates
        );

        for (int i = 0; i < entries.Count; i++)
        {
            GameObject templateObject = UnityEngine.Object.Instantiate(
                templates.templatePrefab,
                templates.templateContainer
            );

            TemplateDisplay display =
                templateObject.GetComponent<TemplateDisplay>();

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
                !isPlayer
            );
        }
    }

    private static void AddBackButton(
        ShipBuilderTemplates templates)
    {
        if (templates.folderPrefab == null)
            return;

        GameObject backObject = UnityEngine.Object.Instantiate(
            templates.folderPrefab,
            templates.templateContainer
        );

        FolderDisplay display =
            backObject.GetComponent<FolderDisplay>();

        if (display != null)
            display.Init(string.Empty, 0, templates);
    }

    private static T GetPrivateValue<T>(
        FieldInfo field,
        object instance)
    {
        if (field == null || instance == null)
            return default(T);

        object value = field.GetValue(instance);

        if (value is T)
            return (T)value;

        return default(T);
    }
}

public static class LeviathanSegmentTemplates
{
    public const string FolderName = "Leviathan";

    private const string UidPrefix = "Leviathan.";

    public static bool TryNormalizeRole(
        string value,
        out string role)
    {
        role = null;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        string text = value.Trim();

        if (text.Equals("Body", StringComparison.OrdinalIgnoreCase))
        {
            role = "Body";
            return true;
        }

        if (text.Equals("Tail", StringComparison.OrdinalIgnoreCase))
        {
            role = "Tail";
            return true;
        }

        if (text.Equals("Tail_a", StringComparison.OrdinalIgnoreCase))
        {
            role = "Tail_a";
            return true;
        }

        if (text.Equals("Tail_b", StringComparison.OrdinalIgnoreCase))
        {
            role = "Tail_b";
            return true;
        }

        int segmentNumber;

        if (int.TryParse(text, out segmentNumber) &&
            IsSupportedSegmentNumber(segmentNumber))
        {
            role = segmentNumber.ToString();
            return true;
        }

        string lower = text.ToLowerInvariant();

        if (TryNormalizeNumberedRole(
                lower,
                "tail_a",
                "Tail_a",
                out role))
        {
            return true;
        }

        if (TryNormalizeNumberedRole(
                lower,
                "tail_b",
                "Tail_b",
                out role))
        {
            return true;
        }

        if (TryNormalizeNumberedRole(
                lower,
                "bifurcate",
                "Bifurcate",
                out role))
        {
            return true;
        }

        return false;
    }

    private static bool TryNormalizeNumberedRole(
        string lower,
        string prefix,
        string canonicalPrefix,
        out string role)
    {
        role = null;

        if (string.IsNullOrEmpty(lower) ||
            !lower.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string suffix = lower.Substring(prefix.Length);
        int number;

        if (!int.TryParse(suffix, out number) ||
            !IsSupportedSegmentNumber(number))
        {
            return false;
        }

        role = canonicalPrefix + number.ToString();
        return true;
    }

    private static bool IsSupportedSegmentNumber(int number)
    {
        // Deliberately larger than today's Growth tree maximum so saved anatomy
        // does not need a file-format migration when future nodes add segments.
        return number >= 1 && number <= 64;
    }

    public static string GetRolePath(string role)
    {
        return Path.Combine(
            GetDirectory(),
            role + ".template"
        );
    }

    public static bool TemplateUsesPath(
        Template template,
        string path)
    {
        if (template == null ||
            template.metaData == null ||
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
                StringComparison.OrdinalIgnoreCase
            );
        }
        catch
        {
            return false;
        }
    }

    public static bool TryGetRole(
        Template template,
        out string role)
    {
        role = null;

        if (template == null || template.metaData == null)
            return false;

        string uid = template.metaData.uid;

        if (string.IsNullOrEmpty(uid) ||
            !uid.StartsWith(
                UidPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return TryNormalizeRole(
            uid.Substring(UidPrefix.Length),
            out role
        );
    }

    public static Template SaveRole(
        string role,
        string serialized)
    {
        string normalized;

        if (!TryNormalizeRole(role, out normalized))
            throw new ArgumentException("Invalid Leviathan role.");

        Directory.CreateDirectory(GetDirectory());

        if (normalized.StartsWith(
                "Bifurcate",
                StringComparison.OrdinalIgnoreCase))
        {
            RemoveOtherBifurcationRoles(normalized);
        }

        string path = GetRolePath(normalized);
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
        template.metaData.name = normalized;
        template.metaData.uid = UidPrefix + normalized;
        template.serialized = serialized;
        template.Save(true);

        return template;
    }

    public static Dictionary<string, string> LoadSerializedBodies()
    {
        Dictionary<string, string> result =
            new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase
            );

        AddLoadedRole(result, "Body");

        for (int i = 1; i <= 64; i++)
            AddLoadedRole(result, i.ToString());

        AddLoadedRole(result, "Tail");
        AddLoadedRole(result, "Tail_a");
        AddLoadedRole(result, "Tail_b");

        for (int i = 1; i <= 64; i++)
        {
            AddLoadedRole(result, "Tail_a" + i.ToString());
            AddLoadedRole(result, "Tail_b" + i.ToString());
            AddLoadedRole(result, "Bifurcate" + i.ToString());
        }

        return result;
    }

    private static void AddLoadedRole(
        Dictionary<string, string> result,
        string role)
    {
        Template template = LoadRole(role);

        if (template != null &&
            !string.IsNullOrEmpty(template.serialized))
        {
            result[role] = template.serialized;
        }
    }

    public static void EnsureBifurcationDefaults()
    {
        string tailSerialized = null;

        Template tail = LoadRole("Tail");
        if (tail != null)
            tailSerialized = tail.serialized;

        if (string.IsNullOrEmpty(tailSerialized))
        {
            string stockBody;
            string stockTail;
            GetStockBodies(out stockBody, out stockTail);
            tailSerialized = stockTail;
        }

        if (string.IsNullOrEmpty(tailSerialized))
            return;

        if (LoadRole("Tail_a") == null)
            SaveRole("Tail_a", tailSerialized);

        if (LoadRole("Tail_b") == null)
            SaveRole("Tail_b", tailSerialized);
    }

    public static List<Template> GetPresetTemplates()
    {
        List<Template> result = new List<Template>();

        Template body = LoadRole("Body");
        Template tail = LoadRole("Tail");

        string stockBody;
        string stockTail;
        GetStockBodies(out stockBody, out stockTail);

        if (body == null && !string.IsNullOrEmpty(stockBody))
            body = CreateStockTemplate("Body", stockBody);

        if (body != null)
            result.Add(body);

        for (int i = 1; i <= 64; i++)
        {
            Template segment = LoadRole(i.ToString());

            if (segment != null)
                result.Add(segment);
        }

        // Only existing bifurcation/branch overrides are shown. Generic Tail
        // remains the final fallback for either branch.
        for (int i = 1; i <= 64; i++)
        {
            Template bifurcate =
                LoadRole("Bifurcate" + i.ToString());

            if (bifurcate != null)
                result.Add(bifurcate);
        }

        Template tailA = LoadRole("Tail_a");
        if (tailA != null)
            result.Add(tailA);

        for (int i = 1; i <= 64; i++)
        {
            Template branch =
                LoadRole("Tail_a" + i.ToString());

            if (branch != null)
                result.Add(branch);
        }

        Template tailB = LoadRole("Tail_b");
        if (tailB != null)
            result.Add(tailB);

        for (int i = 1; i <= 64; i++)
        {
            Template branch =
                LoadRole("Tail_b" + i.ToString());

            if (branch != null)
                result.Add(branch);
        }

        if (tail == null && !string.IsNullOrEmpty(stockTail))
            tail = CreateStockTemplate("Tail", stockTail);

        if (tail != null)
            result.Add(tail);

        return result;
    }

    private static void RemoveOtherBifurcationRoles(
        string keepRole)
    {
        for (int i = 1; i <= 64; i++)
        {
            string role = "Bifurcate" + i.ToString();

            if (role.Equals(
                    keepRole,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string path = GetRolePath(role);

            if (!File.Exists(path))
                continue;

            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    "[Leviathan] Could not remove old bifurcation role '" +
                    role + "': " + ex.Message
                );
            }
        }
    }

    private static Template LoadRole(string role)
    {
        string path = GetRolePath(role);

        if (!File.Exists(path))
            return null;

        try
        {
            Template template = Savable.Load<Template>(path, false);

            if (template == null ||
                template.metaData == null ||
                string.IsNullOrEmpty(template.serialized))
            {
                return null;
            }

            template.metaData.file = path;
            template.metaData.name = role;
            template.metaData.uid = UidPrefix + role;

            return template;
        }
        catch (Exception ex)
        {
            Debug.LogWarning(
                "[Leviathan] Could not load part '" +
                role + "': " + ex.Message
            );
            return null;
        }
    }

    private static Template CreateStockTemplate(
        string role,
        string serialized)
    {
        Template template = new Template();
        template.metaData.name = role;
        template.metaData.uid = UidPrefix + role;
        template.metaData.file = string.Empty;
        template.serialized = serialized;
        return template;
    }

    private static string GetDirectory()
    {
        return Path.Combine(
            Application.persistentDataPath,
            "Templates",
            FolderName
        );
    }

    private static void GetStockBodies(
        out string body,
        out string tail)
    {
        body = null;
        tail = null;

        SquadronBase leviathanBase =
            Resources.FindObjectsOfTypeAll<SquadronBase>()
                .FirstOrDefault(x => x.name == "LeviathanTest");

        if (leviathanBase == null)
            return;

        Squadron squadron = leviathanBase.GetSquadron(1);

        if (squadron == null ||
            squadron.ships == null ||
            squadron.ships.Count < 5)
        {
            return;
        }

        Ship bodyShip = squadron.ships[1].npc.GetShip();
        Ship tailShip = squadron.ships[squadron.ships.Count - 1].npc.GetShip();

        if (bodyShip != null)
            body = bodyShip.serializedBody;

        if (tailShip != null)
            tail = tailShip.serializedBody;
    }
}

public static class LeviathanReflection
{
    public static object GetFieldValue(
        object target,
        string fieldName)
    {
        if (target == null)
            return null;

        FieldInfo field = target.GetType().GetField(
            fieldName,
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic
        );

        return field == null ? null : field.GetValue(target);
    }

    public static void SetFieldTmpText(
        object target,
        string fieldName,
        string value)
    {
        object text = GetFieldValue(target, fieldName);

        if (text == null)
            return;

        PropertyInfo property = text.GetType().GetProperty("text");

        if (property != null && property.CanWrite)
            property.SetValue(text, value, null);
    }

    public static void SetFirstTmpText(
        GameObject root,
        string value)
    {
        Component[] components =
            root.GetComponentsInChildren<Component>(true);

        for (int i = 0; i < components.Length; i++)
        {
            Component component = components[i];

            if (component == null ||
                component.GetType().Namespace != "TMPro")
            {
                continue;
            }

            PropertyInfo property =
                component.GetType().GetProperty("text");

            if (property == null || !property.CanWrite)
                continue;

            property.SetValue(component, value, null);
            return;
        }
    }
}

public static class LeviathanSegmentStatusProtection
{
    private struct StatusAttemptKey
    {
        public int playerId;
        public int attackerId;
        public DamageType damageType;
        public int sourceX;
        public int sourceY;

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + playerId;
                hash = hash * 31 + attackerId;
                hash = hash * 31 + (int)damageType;
                hash = hash * 31 + sourceX;
                hash = hash * 31 + sourceY;
                return hash;
            }
        }

        public override bool Equals(object obj)
        {
            if (!(obj is StatusAttemptKey))
                return false;

            StatusAttemptKey other = (StatusAttemptKey)obj;

            return playerId == other.playerId &&
                attackerId == other.attackerId &&
                damageType == other.damageType &&
                sourceX == other.sourceX &&
                sourceY == other.sourceY;
        }
    }

    private struct DirectStatusAttemptKey
    {
        public int playerId;
        public int attackerId;
        public StatusEffect.Type statusType;
        public bool segmentProtectionEligible;

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + playerId;
                hash = hash * 31 + attackerId;
                hash = hash * 31 + (int)statusType;
                hash = hash * 31 +
                    (segmentProtectionEligible ? 1 : 0);
                return hash;
            }
        }

        public override bool Equals(object obj)
        {
            if (!(obj is DirectStatusAttemptKey))
                return false;

            DirectStatusAttemptKey other =
                (DirectStatusAttemptKey)obj;

            return playerId == other.playerId &&
                attackerId == other.attackerId &&
                statusType == other.statusType &&
                segmentProtectionEligible ==
                    other.segmentProtectionEligible;
        }
    }

    private static readonly HashSet<StatusAttemptKey> SeenThisFrame =
        new HashSet<StatusAttemptKey>();

    private static readonly HashSet<DirectStatusAttemptKey>
        DirectSeenThisFrame =
            new HashSet<DirectStatusAttemptKey>();

    private static int lastFrame = -1;

    public static float FilterStatusEffectChance(
        GameShip player,
        DamageType damageType,
        float statusEffectChance,
        Vector2 fromPosition,
        GameShip fromShip)
    {
        if (player == null || statusEffectChance <= 0f)
            return 0f;

        EnsureFrame();

        StatusAttemptKey key = new StatusAttemptKey();
        key.playerId = player.gameObject.GetInstanceID();
        key.attackerId =
            fromShip == null
                ? 0
                : fromShip.gameObject.GetInstanceID();
        key.damageType = damageType;

        // One attacker gets one segment-originated status opportunity per damage
        // type per frame. Unowned hazards are separated by source position.
        if (fromShip == null)
        {
            key.sourceX = Mathf.RoundToInt(fromPosition.x * 1000f);
            key.sourceY = Mathf.RoundToInt(fromPosition.y * 1000f);
        }

        if (!SeenThisFrame.Add(key))
            return 0f;

        return RollDiscard(player)
            ? 0f
            : statusEffectChance;
    }

    public static bool ShouldTransferDirectStatus(
        GameShip player,
        StatusEffect statusEffect,
        bool segmentProtectionEligible)
    {
        if (player == null ||
            statusEffect == null ||
            !statusEffect.IsNegative())
        {
            return false;
        }

        EnsureFrame();

        DirectStatusAttemptKey key =
            new DirectStatusAttemptKey();

        key.playerId = player.gameObject.GetInstanceID();
        key.attackerId =
            statusEffect.attacker == null
                ? 0
                : statusEffect.attacker.gameObject.GetInstanceID();
        key.statusType = statusEffect.GetStatusEffectType();
        key.segmentProtectionEligible =
            segmentProtectionEligible;

        // Direct AddStatusEffect paths such as Wildfire can otherwise apply the
        // same debuff through many physical sections in one frame. Protected
        // segments and vulnerable Heads use separate dedupe classes so a segment
        // discard can never suppress an additional-Head hit.
        if (!DirectSeenThisFrame.Add(key))
            return false;

        return !segmentProtectionEligible || !RollDiscard(player);
    }

    private static bool RollDiscard(GameShip player)
    {
        // Growth owns the chassis/Growth-tree segment debuff discard chance;
        // Behemoth contributes its own segment-specific protection. Both apply
        // only when the hit section is actually eligible for segment protection.
        float discardChance =
            LeviathanGrowth.GetSegmentDebuffDiscardChance(player) +
            LeviathanBehemoth.GetSegmentDebuffDiscardChance(player);

        return discardChance > 0f &&
            UnityEngine.Random.value < Mathf.Clamp01(discardChance);
    }

    private static void EnsureFrame()
    {
        if (Time.frameCount == lastFrame)
            return;

        lastFrame = Time.frameCount;
        SeenThisFrame.Clear();
        DirectSeenThisFrame.Clear();
    }

    public static void Reset()
    {
        SeenThisFrame.Clear();
        DirectSeenThisFrame.Clear();
        lastFrame = -1;
    }
}

public static class LeviathanSegmentDamageLimiter
{
    // Maximum redirected segment hits from one source during one simulation step.
    // Direct hits to the player/head never enter this limiter.
    public const int MaxSegmentHitsPerSourceStep = 4;

    private struct DamageWindowKey
    {
        public int playerId;
        public int attackerId;
        public DamageType damageType;
        public string weaponName;

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + playerId;
                hash = hash * 31 + attackerId;
                hash = hash * 31 + (int)damageType;
                hash = hash * 31 +
                    (weaponName == null ? 0 : weaponName.GetHashCode());
                return hash;
            }
        }

        public override bool Equals(object obj)
        {
            if (!(obj is DamageWindowKey))
                return false;

            DamageWindowKey other = (DamageWindowKey)obj;

            return playerId == other.playerId &&
                attackerId == other.attackerId &&
                damageType == other.damageType &&
                string.Equals(
                    weaponName,
                    other.weaponName,
                    StringComparison.Ordinal
                );
        }
    }

    private static readonly Dictionary<DamageWindowKey, int>
        HitsThisStep =
            new Dictionary<DamageWindowKey, int>();

    private static int lastFrame = -1;
    private static float lastFixedTime = float.NaN;

    public static bool AllowSegmentDamage(
        GameShip segment,
        GameShip player,
        DamageType damageType,
        GameShip fromShip)
    {
        if (segment == null || player == null)
            return true;

        EnsureStep();

        DamageWindowKey key = new DamageWindowKey();
        key.playerId = player.gameObject.GetInstanceID();
        key.attackerId =
            fromShip == null
                ? 0
                : fromShip.gameObject.GetInstanceID();
        key.damageType = damageType;
        key.weaponName =
            segment.lastDamagedByWeaponName ?? string.Empty;

        int hitCount;

        if (!HitsThisStep.TryGetValue(key, out hitCount))
            hitCount = 0;

        if (hitCount >= MaxSegmentHitsPerSourceStep)
            return false;

        HitsThisStep[key] = hitCount + 1;
        return true;
    }

    private static void EnsureStep()
    {
        int frame = Time.frameCount;
        float fixedTime = Time.fixedTime;

        if (frame == lastFrame && fixedTime == lastFixedTime)
            return;

        lastFrame = frame;
        lastFixedTime = fixedTime;
        HitsThisStep.Clear();
    }

    public static void Reset()
    {
        HitsThisStep.Clear();
        lastFrame = -1;
        lastFixedTime = float.NaN;
    }
}

public static class LeviathanSegmentTransferProtection
{
    // Maximum actual shield + hull damage that redirected segment hits may deal
    // during the rolling window, expressed as a fraction of max hull + max shield.
    public const float MaxCombinedHealthFractionPerWindow = 0.30f;

    // Rolling damage window in seconds.
    public const float WindowSeconds = 0.10f;

    private struct TransferEvent
    {
        public float time;
        public float damage;
    }

    private static readonly Dictionary<int, List<TransferEvent>>
        DamageEventsByPlayer =
            new Dictionary<int, List<TransferEvent>>();

    public static float CaptureCurrentCombinedHealth(GameShip player)
    {
        if (player == null)
            return 0f;

        float shield =
            player.shield == null
                ? 0f
                : Mathf.Max(0f, player.shield.shield);

        return Mathf.Max(0f, player.health) + shield;
    }

    public static DamageData[] LimitDamageData(
        GameShip player,
        DamageType damageType,
        DamageData[] source,
        GameShip fromShip)
    {
        if (player == null || source == null)
            return source;

        float remaining = GetRemainingBudget(player);

        if (remaining <= 0f)
            return ScaleDamageData(source, 0f);

        float estimatedResolvedDamage =
            EstimateResolvedDamageUpperBound(
                player,
                damageType,
                source,
                fromShip
            );

        if (estimatedResolvedDamage <= 0f ||
            estimatedResolvedDamage <= remaining)
        {
            return source;
        }

        return ScaleDamageData(
            source,
            Mathf.Clamp01(remaining / estimatedResolvedDamage)
        );
    }

    public static float LimitDirectDamage(
        GameShip player,
        DamageType damageType,
        float damage)
    {
        if (player == null || damage <= 0f)
            return Mathf.Max(0f, damage);

        float remaining = GetRemainingBudget(player);

        if (remaining <= 0f)
            return 0f;

        float resistanceMultiplier =
            GetResistanceMultiplier(
                player,
                damageType,
                null,
                false
            );

        float estimatedResolvedDamage =
            damage * resistanceMultiplier;

        if (estimatedResolvedDamage <= 0f ||
            estimatedResolvedDamage <= remaining)
        {
            return damage;
        }

        return damage *
            Mathf.Clamp01(
                remaining / estimatedResolvedDamage
            );
    }

    public static void RecordActualDamage(
        GameShip player,
        float combinedHealthBefore)
    {
        if (player == null)
            return;

        float combinedHealthAfter =
            CaptureCurrentCombinedHealth(player);

        float actualDamage =
            Mathf.Max(
                0f,
                combinedHealthBefore - combinedHealthAfter
            );

        if (actualDamage <= 0f)
            return;

        int playerId = player.gameObject.GetInstanceID();
        List<TransferEvent> events;

        if (!DamageEventsByPlayer.TryGetValue(
                playerId,
                out events))
        {
            events = new List<TransferEvent>();
            DamageEventsByPlayer[playerId] = events;
        }

        Prune(events);

        TransferEvent transferEvent = new TransferEvent();
        transferEvent.time = Time.time;
        transferEvent.damage = actualDamage;
        events.Add(transferEvent);
    }

    private static float GetRemainingBudget(GameShip player)
    {
        float maxShield =
            player.shield == null
                ? 0f
                : Mathf.Max(0f, player.shield.ShieldMax);

        float maxCombinedHealth =
            Mathf.Max(0f, player.HealthMax) + maxShield;

        float budget =
            maxCombinedHealth *
            MaxCombinedHealthFractionPerWindow;

        if (budget <= 0f)
            return 0f;

        int playerId = player.gameObject.GetInstanceID();
        List<TransferEvent> events;

        if (!DamageEventsByPlayer.TryGetValue(
                playerId,
                out events))
        {
            return budget;
        }

        Prune(events);

        float spent = 0f;

        for (int i = 0; i < events.Count; i++)
            spent += events[i].damage;

        return Mathf.Max(0f, budget - spent);
    }

    private static void Prune(List<TransferEvent> events)
    {
        float cutoff = Time.time - WindowSeconds;

        for (int i = events.Count - 1; i >= 0; i--)
        {
            if (events[i].time < cutoff)
                events.RemoveAt(i);
        }
    }

    private static float EstimateResolvedDamageUpperBound(
        GameShip player,
        DamageType damageType,
        DamageData[] damageData,
        GameShip fromShip)
    {
        float relevantDamage = 0f;

        for (int i = 0; i < damageData.Length; i++)
        {
            DamageData datum = damageData[i];

            if (datum.damage <= 0f)
                continue;

            switch (datum.modifierType)
            {
                case Modifier.Type.DamageVsBurning:
                    if (!player.HasStatusEffect(StatusEffect.Type.Burning))
                        continue;
                    break;

                case Modifier.Type.DamageVsCorroding:
                    if (!player.HasStatusEffect(StatusEffect.Type.Corroding))
                        continue;
                    break;

                case Modifier.Type.DamageVsDisabled:
                    if (!player.HasStatusEffect(StatusEffect.Type.Disabled))
                        continue;
                    break;

                case Modifier.Type.DamageVsFrozen:
                    if (!player.HasStatusEffect(StatusEffect.Type.Frozen))
                        continue;
                    break;

                case Modifier.Type.DamageVsRadioactive:
                    if (!player.HasStatusEffect(StatusEffect.Type.Radioactive))
                        continue;
                    break;
            }

            relevantDamage += datum.damage;
        }

        if (relevantDamage <= 0f)
            return 0f;

        float gameShipMultiplier =
            GetGameShipIncomingMultiplier(
                player,
                damageType,
                fromShip
            );

        float resistanceMultiplier =
            GetResistanceMultiplier(
                player,
                damageType,
                fromShip,
                true
            );

        float kineticHullMultiplier = 1f;

        if (damageType == DamageType.Kinetic &&
            fromShip != null)
        {
            int kineticHull =
                GameShip.GetPlayerSourceUpgradeValue(
                    fromShip,
                    Upgrade.Key.JuggernautKineticHull
                );

            if (kineticHull > 0)
            {
                kineticHullMultiplier +=
                    (float)kineticHull / 100f;
            }
        }

        // This is deliberately an upper bound. DamageReduction is omitted because
        // it can only reduce hull damage, while shield damage does not use it.
        return relevantDamage *
            gameShipMultiplier *
            resistanceMultiplier *
            kineticHullMultiplier;
    }

    private static float GetGameShipIncomingMultiplier(
        GameShip player,
        DamageType damageType,
        GameShip fromShip)
    {
        float multiplier = 1f;

        if (WorldController.instance != null)
        {
            Star currentStar =
                WorldController.instance.GetCurrentStar();

            if (currentStar != null)
            {
                if ((currentStar.debrisType ==
                        Star.DebrisType.FrozenAsteroids &&
                        damageType == DamageType.Cold) ||
                    (currentStar.debrisType ==
                        Star.DebrisType.MoltenAsteroids &&
                        damageType == DamageType.Thermal) ||
                    (currentStar.debrisType ==
                        Star.DebrisType.RadioactiveAsteroids &&
                        damageType == DamageType.Radiation) ||
                    (currentStar.debrisType ==
                        Star.DebrisType.CorrosiveAsteroids &&
                        damageType == DamageType.Corrosive))
                {
                    multiplier *= 1.5f;
                }
            }
        }

        if (fromShip == null ||
            fromShip.pilot == null ||
            player.pilot == null)
        {
            return multiplier;
        }

        int levelDifference =
            fromShip.pilot.GetLevel(0) -
            player.pilot.GetLevel(0);

        if (levelDifference < -9)
            multiplier *= 0.25f;
        else if (levelDifference < -4)
            multiplier *= 0.50f;
        else if (levelDifference < -3)
            multiplier *= 0.75f;
        else if (levelDifference > 9)
            multiplier *= 4.00f;
        else if (levelDifference > 4)
            multiplier *= 2.00f;
        else if (levelDifference > 3)
            multiplier *= 1.50f;

        return multiplier;
    }

    private static float GetResistanceMultiplier(
        GameShip player,
        DamageType damageType,
        GameShip fromShip,
        bool includePenetration)
    {
        float resistance = 0f;

        switch (damageType)
        {
            case DamageType.Kinetic:
                resistance = player.ResistanceKinetic;
                break;
            case DamageType.Cold:
                resistance = player.ResistanceCold;
                break;
            case DamageType.Corrosive:
                resistance = player.ResistanceCorrosive;
                break;
            case DamageType.Electric:
                resistance = player.ResistanceElectric;
                break;
            case DamageType.Thermal:
                resistance = player.ResistanceThermal;
                break;
            case DamageType.Radiation:
                resistance = player.ResistanceRadiation;
                break;
        }

        if (includePenetration && fromShip != null)
        {
            Upgrade.Key penetrationKey;
            bool hasPenetrationKey = true;

            switch (damageType)
            {
                case DamageType.Kinetic:
                    penetrationKey =
                        Upgrade.Key.JuggernautKineticPenetration;
                    break;
                case DamageType.Electric:
                    penetrationKey =
                        Upgrade.Key.TempestElectricPenetration;
                    break;
                case DamageType.Thermal:
                    penetrationKey =
                        Upgrade.Key.ArsonistThermalPenetration;
                    break;
                case DamageType.Cold:
                    penetrationKey =
                        Upgrade.Key.CryonicColdPenetration;
                    break;
                case DamageType.Corrosive:
                    penetrationKey =
                        Upgrade.Key.VitriolicCorrosivePenetration;
                    break;
                case DamageType.Radiation:
                    penetrationKey =
                        Upgrade.Key.ContaminatorRadiationPenetration;
                    break;
                default:
                    penetrationKey = default(Upgrade.Key);
                    hasPenetrationKey = false;
                    break;
            }

            if (hasPenetrationKey)
            {
                int penetration =
                    GameShip.GetPlayerSourceUpgradeValue(
                        fromShip,
                        penetrationKey
                    );

                if (penetration > 0)
                {
                    resistance -=
                        (float)penetration / 100f;
                }
            }
        }

        return Mathf.Max(0f, 1f - resistance);
    }

    private static DamageData[] ScaleDamageData(
        DamageData[] source,
        float multiplier)
    {
        if (source == null)
            return null;

        DamageData[] scaled =
            new DamageData[source.Length];

        for (int i = 0; i < source.Length; i++)
        {
            DamageData datum = source[i];
            datum.damage *= multiplier;
            datum.dps *= multiplier;
            scaled[i] = datum;
        }

        return scaled;
    }

    public static void Reset()
    {
        DamageEventsByPlayer.Clear();
    }
}

[HarmonyPatch(typeof(GameShip), "AddStatusEffect")]
public static class LeviathanSectionAddStatusEffectPatch
{
    public static bool Prefix(
        GameShip __instance,
        StatusEffect __0)
    {
        if (__instance == null ||
            __0 == null ||
            !__0.IsNegative())
        {
            return true;
        }

        GameShip player;
        LeviathanGrowth.AnatomyRole role;

        if (!LeviathanGrowth.TryGetSectionContext(
                __instance,
                out player,
                out role) ||
            player == null ||
            ReferenceEquals(__instance, player))
        {
            return true;
        }

        bool segmentProtectionEligible =
            role == LeviathanGrowth.AnatomyRole.Body ||
            role == LeviathanGrowth.AnatomyRole.Tail;

        // Follower sections never retain their own negative statuses. Body/Tail
        // transfers receive segment discard protection; additional Heads transfer
        // the status without segment protection so head hits remain dangerous.
        if (LeviathanSegmentStatusProtection.ShouldTransferDirectStatus(
                player,
                __0,
                segmentProtectionEligible))
        {
            StatusEffect transferred = __0.Clone() as StatusEffect;

            if (transferred != null)
                player.AddStatusEffect(transferred);
        }

        return false;
    }
}

public static class LeviathanSegmentSourceTuning
{
    // Extra multiplier applied only when a Halo damages a Leviathan segment and
    // that segment damage is redirected to the head. Direct head hits are untouched.
    public const float EnemyHaloTransferredDamageMultiplier = 0.37f;

    // Enable temporarily to print the exact recorded source of redirected hits.
    public const bool LogSegmentDamageSources = false;

    public static bool IsHaloSource(
        GameShip segment,
        GameShip attacker)
    {
        if (segment == null || attacker == null)
            return false;

        string weaponName =
            segment.lastDamagedByWeaponName ?? string.Empty;

        if (string.IsNullOrEmpty(weaponName) ||
            attacker.slots == null)
        {
            return false;
        }

        for (int i = 0; i < attacker.slots.Length; i++)
        {
            Slot slot = attacker.slots[i];

            if (slot == null)
                continue;

            Halo halo = slot.equippable as Halo;

            if (halo == null)
                continue;

            if (string.Equals(
                    halo.GetName(false, false),
                    weaponName,
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static void LogSource(
        GameShip segment,
        GameShip attacker,
        DamageType damageType,
        DamageData[] damageData,
        float finalMultiplier,
        bool haloSource)
    {
        if (!LogSegmentDamageSources || segment == null)
            return;

        float rawDamage = 0f;
        float rawDps = 0f;

        if (damageData != null)
        {
            for (int i = 0; i < damageData.Length; i++)
            {
                rawDamage += damageData[i].damage;
                rawDps += damageData[i].dps;
            }
        }

        Debug.Log(
            "[Leviathan] Segment hit source='" +
            (segment.lastDamagedByWeaponName ?? "Unknown") +
            "' ship='" +
            (segment.lastDamagedByShipName ?? "Unknown") +
            "' faction='" +
            (segment.lastDamagedByFaction ?? "Unknown") +
            "' type=" + damageType +
            " rawDamage=" + rawDamage +
            " rawDps=" + rawDps +
            " multiplier=" + finalMultiplier +
            " halo=" + haloSource
        );
    }
}

[HarmonyPatch]
public static class LeviathanSectionDamagePatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(GameShip),
            "Damage",
            new Type[]
            {
                typeof(DamageType),
                typeof(DamageData[]),
                typeof(float),
                typeof(int),
                typeof(Vector2),
                typeof(GameShip),
                typeof(bool)
            }
        );
    }

    public static bool Prefix(
        GameShip __instance,
        DamageType __0,
        DamageData[] __1,
        float __2,
        int __3,
        Vector2 __4,
        GameShip __5,
        bool __6,
        ref bool __result)
    {
        GameShip player;
        LeviathanGrowth.AnatomyRole role;

        if (!LeviathanGrowth.TryGetSectionContext(
                __instance,
                out player,
                out role) ||
            player == null ||
            ReferenceEquals(__instance, player))
        {
            return true;
        }

        bool segmentProtectionEligible =
            role == LeviathanGrowth.AnatomyRole.Body ||
            role == LeviathanGrowth.AnatomyRole.Tail;

        DamageData[] redirectedDamage = __1;
        float finalMultiplier = 1f;
        bool haloSource = false;

        if (segmentProtectionEligible)
        {
            if (!LeviathanSegmentDamageLimiter.AllowSegmentDamage(
                    __instance,
                    player,
                    __0,
                    __5))
            {
                __result = false;
                return false;
            }

            finalMultiplier =
                LeviathanBehemoth.GetSegmentDamageMultiplier(player);

            redirectedDamage = ScaleDamageData(
                redirectedDamage,
                finalMultiplier
            );

            haloSource =
                LeviathanSegmentSourceTuning.IsHaloSource(
                    __instance,
                    __5
                );

            if (haloSource)
            {
                finalMultiplier *=
                    LeviathanSegmentSourceTuning
                        .EnemyHaloTransferredDamageMultiplier;

                redirectedDamage = ScaleDamageData(
                    redirectedDamage,
                    LeviathanSegmentSourceTuning
                        .EnemyHaloTransferredDamageMultiplier
                );
            }

            redirectedDamage =
                LeviathanSegmentTransferProtection.LimitDamageData(
                    player,
                    __0,
                    redirectedDamage,
                    __5
                );

            LeviathanSegmentSourceTuning.LogSource(
                __instance,
                __5,
                __0,
                __1,
                finalMultiplier,
                haloSource
            );
        }

        float statusEffectChance = segmentProtectionEligible
            ? LeviathanSegmentStatusProtection.FilterStatusEffectChance(
                player,
                __0,
                __2,
                __4,
                __5
            )
            : __2;

        CopyDamageAttribution(__instance, player);

        float combinedHealthBefore = segmentProtectionEligible
            ? LeviathanSegmentTransferProtection
                .CaptureCurrentCombinedHealth(player)
            : 0f;

        __result = CoreNativeCriticalHits.Damage(
            player,
            __0,
            redirectedDamage,
            statusEffectChance,
            __3,
            __4,
            __5,
            true
        );

        if (segmentProtectionEligible)
        {
            LeviathanSegmentTransferProtection.RecordActualDamage(
                player,
                combinedHealthBefore
            );
        }

        return false;
    }

    private static void CopyDamageAttribution(
        GameShip section,
        GameShip player)
    {
        if (section == null || player == null)
            return;

        player.lastDamagedByWeaponName =
            section.lastDamagedByWeaponName;
        player.lastDamagedByShipName =
            section.lastDamagedByShipName;
        player.lastDamagedByFaction =
            section.lastDamagedByFaction;
    }

    private static DamageData[] ScaleDamageData(
        DamageData[] source,
        float multiplier)
    {
        if (source == null || Mathf.Approximately(multiplier, 1f))
            return source;

        DamageData[] scaled = new DamageData[source.Length];

        for (int i = 0; i < source.Length; i++)
        {
            DamageData datum = source[i];
            datum.damage *= multiplier;
            datum.dps *= multiplier;
            scaled[i] = datum;
        }

        return scaled;
    }
}

[HarmonyPatch]
public static class LeviathanSectionDirectDamagePatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(GameShip),
            "DirectDamage",
            new Type[]
            {
                typeof(DamageType),
                typeof(float),
                typeof(bool)
            }
        );
    }

    public static bool Prefix(
        GameShip __instance,
        DamageType __0,
        float __1,
        bool __2,
        ref bool __result)
    {
        GameShip player;
        LeviathanGrowth.AnatomyRole role;

        if (!LeviathanGrowth.TryGetSectionContext(
                __instance,
                out player,
                out role) ||
            player == null ||
            ReferenceEquals(__instance, player))
        {
            return true;
        }

        bool segmentProtectionEligible =
            role == LeviathanGrowth.AnatomyRole.Body ||
            role == LeviathanGrowth.AnatomyRole.Tail;

        float redirectedDamage = __1;

        if (segmentProtectionEligible)
        {
            redirectedDamage *=
                LeviathanBehemoth.GetSegmentDamageMultiplier(player);

            redirectedDamage =
                LeviathanSegmentTransferProtection.LimitDirectDamage(
                    player,
                    __0,
                    redirectedDamage
                );
        }

        player.lastDirectDamageSourceName =
            __instance.lastDirectDamageSourceName;

        float combinedHealthBefore = segmentProtectionEligible
            ? LeviathanSegmentTransferProtection
                .CaptureCurrentCombinedHealth(player)
            : 0f;

        __result = player.DirectDamage(
            __0,
            redirectedDamage,
            true
        );

        if (segmentProtectionEligible)
        {
            LeviathanSegmentTransferProtection.RecordActualDamage(
                player,
                combinedHealthBefore
            );
        }

        return false;
    }
}

[HarmonyPatch(typeof(GameShip), "SetRemoteEntity")]
public static class LeviathanRemoteEntityRenderOrderPatch
{
    public static void Postfix(
        GameShip __instance,
        uint __0)
    {
        LeviathanAttachmentNormalizer.NormalizeRemoteRenderOrder(
            __instance,
            __0
        );
    }
}

[HarmonyPatch(typeof(GameShip), "SetRemotePlayer")]
public static class LeviathanRemotePlayerRenderOrderPatch
{
    public static void Postfix(
        GameShip __instance,
        NetPlayer __0)
    {
        if (__0 == null)
            return;

        LeviathanAttachmentNormalizer.RefreshRemoteRenderOrder(
            __0.playerId
        );
    }
}

public static class LeviathanWhipTuning
{
    // Geometry defaults are a strict passthrough: when these three values remain
    // at their defaults, vanilla AttachedAIShip.UpdateAttachPosition(float) runs.
    //
    // Fraction of prior rear-point motion retained into the next simulation step.
    // 0.00 = current/vanilla behavior.
    // Recommended first test: 0.85f
    public const float MomentumRetention = 0.00f;

    // Native AttachedAIShip hardcodes 1.5f here.
    // Lower values let the rear of each section preserve its lateral direction
    // longer before aligning back toward the section in front.
    // Recommended first test: 0.45f
    public const float AlignmentStrength = 1.50f;

    // 0 = no angular-speed cap, matching current behavior.
    // Positive values cap section rotation in degrees per second.
    // Recommended first test: 720f
    public const float MaxAngularSpeedDegreesPerSecond = 0.00f;

    // 0 = keep native Rigidbody behavior: each attached section inherits the
    // preceding section's Rigidbody velocity.
    // 1 = use the section's measured world-space center velocity instead.
    //
    // Recommended first movement test: leave at 0.00f.
    // Recommended second test, after geometry feels right: 1.00f.
    public const float SimulatedVelocityInfluence = 0.00f;

    // Reserved for the contact-damage owner (LeviathanConstrictor).
    // This file records rear/tail-point velocity but deliberately does not alter
    // contact damage by itself.
    //
    // 0 = current damage behavior.
    // Recommended only after movement is tuned: 1.00f.
    public const float CollisionWhipVelocityScale = 0.00f;

    public static bool UsesCustomGeometry
    {
        get
        {
            return MomentumRetention != 0.00f ||
                AlignmentStrength != 1.50f ||
                MaxAngularSpeedDegreesPerSecond > 0.00f;
        }
    }
}

[HarmonyPatch]
public static class LeviathanAttachedAIShipPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(AttachedAIShip),
            "UpdateAttachPosition",
            new Type[] { typeof(float) }
        );
    }

    public static bool Prefix(
        AttachedAIShip __instance,
        float __0,
        GameShip ___gameShip,
        GameShip ___attachedShip,
        float ___attachedTime,
        bool ___initialAttach,
        ref Vector2 ___lastThrusterWorldPos,
        bool ___hasLastThruster,
        float ___segmentLength,
        float ___localThrusterAngle,
        ref LeviathanAttachmentNormalizer.State __state)
    {
        __state = LeviathanAttachmentNormalizer.Begin(__instance);

        GameShip anatomyOwner;
        LeviathanGrowth.AnatomyRole anatomyRole;

        if (__state == null &&
            ___gameShip != null &&
            LeviathanGrowth.TryGetSectionContext(
                ___gameShip,
                out anatomyOwner,
                out anatomyRole) &&
            anatomyRole != LeviathanGrowth.AnatomyRole.Unknown &&
            anatomyOwner != null &&
            !ReferenceEquals(___gameShip, anatomyOwner))
        {
            __state =
                LeviathanAttachmentNormalizer.CreateTrackingState(
                    ___gameShip
                );
        }

        if (__state == null)
            return true;

        __state.dt = __0;

        if (!LeviathanWhipTuning.UsesCustomGeometry)
            return true;

        bool handled =
            LeviathanWhipDynamics.TryUpdateAttachPosition(
                __state.ship,
                __0,
                ___attachedShip,
                ___attachedTime,
                ___initialAttach,
                ref ___lastThrusterWorldPos,
                ___hasLastThruster,
                ___segmentLength,
                ___localThrusterAngle
            );

        // If the custom solver cannot safely reproduce the exact native branch
        // for this tick (initial attach, rigid attachment, etc.), run vanilla.
        return !handled;
    }

    public static void Postfix(
        Vector2 ___lastThrusterWorldPos,
        bool ___hasLastThruster,
        LeviathanAttachmentNormalizer.State __state)
    {
        LeviathanAttachmentNormalizer.End(__state);

        LeviathanWhipDynamics.FinalizeFrame(
            __state,
            ___lastThrusterWorldPos,
            ___hasLastThruster
        );
    }
}

public static class LeviathanWhipDynamics
{
    private static readonly Dictionary<GameShip, Vector2>
        MomentumPreviousRearByShip =
            new Dictionary<GameShip, Vector2>();

    private static readonly Dictionary<GameShip, Vector2>
        RearSampleByShip =
            new Dictionary<GameShip, Vector2>();

    private static readonly Dictionary<GameShip, Vector2>
        RearVelocityByShip =
            new Dictionary<GameShip, Vector2>();

    private static readonly Dictionary<GameShip, Vector2>
        CenterSampleByShip =
            new Dictionary<GameShip, Vector2>();

    private static readonly Dictionary<GameShip, Vector2>
        CenterVelocityByShip =
            new Dictionary<GameShip, Vector2>();

    public static void Reset()
    {
        MomentumPreviousRearByShip.Clear();
        RearSampleByShip.Clear();
        RearVelocityByShip.Clear();
        CenterSampleByShip.Clear();
        CenterVelocityByShip.Clear();
    }

    public static bool TryUpdateAttachPosition(
        GameShip ship,
        float dt,
        GameShip attachedShip,
        float attachedTime,
        bool initialAttach,
        ref Vector2 lastThrusterWorldPos,
        bool hasLastThruster,
        float segmentLength,
        float localThrusterAngle)
    {
        if (ship == null ||
            ship.squadron == null ||
            attachedShip == null ||
            attachedShip.gameObject == null ||
            dt <= 0f)
        {
            return false;
        }

        SquadronBase squadronBase =
            ship.squadron.GetSquadronBase();

        if (squadronBase == null ||
            squadronBase.attachRotation ||
            squadronBase.rigidAttachment ||
            ship.IsMindControlled() ||
            initialAttach ||
            attachedTime > 0f ||
            !hasLastThruster ||
            segmentLength < 0.01f)
        {
            return false;
        }

        var parentThrusterEquipment = attachedShip.GetThruster();

        if (parentThrusterEquipment == null ||
            parentThrusterEquipment.gameObject == null)
        {
            return false;
        }

        Vector2 jointWorld =
            parentThrusterEquipment.gameObject.transform.position;

        Vector2 currentRear = lastThrusterWorldPos;
        Vector2 previousRear;

        Vector2 predictedRear = currentRear;

        if (MomentumPreviousRearByShip.TryGetValue(
                ship,
                out previousRear))
        {
            Vector2 retainedMotion =
                currentRear - previousRear;

            predictedRear +=
                retainedMotion *
                Mathf.Max(
                    0f,
                    LeviathanWhipTuning.MomentumRetention
                );
        }

        // Save the pre-solve rear point. On the next tick it becomes the
        // previous sample used to derive inertial motion.
        MomentumPreviousRearByShip[ship] = currentRear;

        Vector2 jointToPredicted =
            predictedRear - jointWorld;

        Vector2 constrainedRear;

        if (jointToPredicted.sqrMagnitude > 0.000001f)
        {
            constrainedRear =
                jointWorld +
                jointToPredicted.normalized * segmentLength;
        }
        else
        {
            Vector2 fallbackDirection =
                attachedShip.transform.rotation * Vector2.left;

            constrainedRear =
                jointWorld +
                fallbackDirection * segmentLength;
        }

        Vector2 rearVector =
            constrainedRear - jointWorld;

        float inertialRearAngle =
            Mathf.Atan2(
                rearVector.y,
                rearVector.x
            ) * Mathf.Rad2Deg;

        float parentRearAngle =
            attachedShip.transform.rotation.eulerAngles.z +
            localThrusterAngle;

        float alignmentStrength =
            Mathf.Max(
                0f,
                LeviathanWhipTuning.AlignmentStrength
            );

        float solvedRearAngle =
            Mathf.LerpAngle(
                inertialRearAngle,
                parentRearAngle,
                alignmentStrength * dt
            );

        float maxAngularSpeed =
            LeviathanWhipTuning
                .MaxAngularSpeedDegreesPerSecond;

        if (maxAngularSpeed > 0f)
        {
            float currentRearAngle =
                ship.transform.rotation.eulerAngles.z +
                localThrusterAngle;

            solvedRearAngle =
                Mathf.MoveTowardsAngle(
                    currentRearAngle,
                    solvedRearAngle,
                    maxAngularSpeed * dt
                );
        }

        float radians =
            solvedRearAngle * Mathf.Deg2Rad;

        Vector2 solvedDirection =
            new Vector2(
                Mathf.Cos(radians),
                Mathf.Sin(radians)
            );

        Vector2 solvedRear =
            jointWorld +
            solvedDirection * segmentLength;

        ship.transform.rotation =
            Quaternion.Euler(
                0f,
                0f,
                solvedRearAngle - localThrusterAngle
            );

        lastThrusterWorldPos = solvedRear;

        // This is the exact stable flexible-attachment position/velocity branch
        // used by native AttachedAIShip once attachedTime reaches zero.
        Vector3 position = ship.transform.position;

        ship.transform.position =
            new Vector3(
                jointWorld.x,
                jointWorld.y,
                position.z
            );

        Rigidbody2D body = ship.GetRigidBody();
        Rigidbody2D parentBody = attachedShip.GetRigidBody();

        if (body != null && parentBody != null)
            body.velocity = parentBody.velocity;

        return true;
    }

    public static void FinalizeFrame(
        LeviathanAttachmentNormalizer.State state,
        Vector2 rearWorldPosition,
        bool hasRearSample)
    {
        if (state == null ||
            state.ship == null ||
            state.dt <= 0f)
        {
            return;
        }

        GameShip ship = state.ship;
        float dt = state.dt;

        if (hasRearSample)
        {
            Vector2 previousRear;

            if (RearSampleByShip.TryGetValue(
                    ship,
                    out previousRear))
            {
                RearVelocityByShip[ship] =
                    (rearWorldPosition - previousRear) / dt;
            }
            else
            {
                Rigidbody2D body = ship.GetRigidBody();

                RearVelocityByShip[ship] =
                    body == null
                        ? Vector2.zero
                        : body.velocity;
            }

            RearSampleByShip[ship] = rearWorldPosition;
        }

        Vector2 center =
            new Vector2(
                ship.transform.position.x,
                ship.transform.position.y
            );

        Vector2 previousCenter;
        bool hasPreviousCenter =
            CenterSampleByShip.TryGetValue(
                ship,
                out previousCenter
            );

        Vector2 centerVelocity;

        if (hasPreviousCenter)
        {
            centerVelocity =
                (center - previousCenter) / dt;
        }
        else
        {
            Rigidbody2D body = ship.GetRigidBody();

            centerVelocity =
                body == null
                    ? Vector2.zero
                    : body.velocity;
        }

        CenterSampleByShip[ship] = center;
        CenterVelocityByShip[ship] = centerVelocity;

        float velocityInfluence =
            Mathf.Clamp01(
                LeviathanWhipTuning
                    .SimulatedVelocityInfluence
            );

        if (velocityInfluence <= 0f ||
            !hasPreviousCenter)
        {
            return;
        }

        Rigidbody2D rigidBody = ship.GetRigidBody();

        if (rigidBody == null)
            return;

        rigidBody.velocity =
            Vector2.Lerp(
                rigidBody.velocity,
                centerVelocity,
                velocityInfluence
            );
    }

    // Logical rear/tail-point velocity. For the final Leviathan section this is
    // the useful "tail whip" velocity to feed into contact damage later.
    public static Vector2 GetRearPointVelocity(GameShip ship)
    {
        Vector2 velocity;

        if (ship != null &&
            RearVelocityByShip.TryGetValue(
                ship,
                out velocity))
        {
            return velocity;
        }

        return Vector2.zero;
    }

    // Measured center velocity after attachment positioning.
    public static Vector2 GetCenterVelocity(GameShip ship)
    {
        Vector2 velocity;

        if (ship != null &&
            CenterVelocityByShip.TryGetValue(
                ship,
                out velocity))
        {
            return velocity;
        }

        return Vector2.zero;
    }
}

public static class LeviathanAttachmentNormalizer
{
    private const float OverlapFraction = 0.10f;

    // Head is highest, then each body/tail steps downward.
    // Thruster roots are pulled out of their parent ship SortingGroup and
    // placed well underneath the entire Leviathan chain.
    private const int ThrusterBottomOffset = 1000;

    private static readonly PropertyInfo SortingGroupSortAtRootProperty =
        typeof(UnityEngine.Rendering.SortingGroup).GetProperty(
            "sortAtRoot",
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic
        );

    private static readonly FieldInfo GameShipField =
        AccessTools.Field(typeof(AIShip), "gameShip");

    private static readonly FieldInfo SegmentLengthField =
        AccessTools.Field(typeof(AttachedAIShip), "segmentLength");

    private static readonly FieldInfo HasLastThrusterField =
        AccessTools.Field(typeof(AttachedAIShip), "hasLastThruster");

    private static readonly Dictionary<GameShip, HullAnchors> AnchorCache =
        new Dictionary<GameShip, HullAnchors>();

    private static readonly HashSet<int> InitializedAttachedShips =
        new HashSet<int>();

    private static HashSet<string> leviathanSegmentNameKeys;

    public sealed class State
    {
        public GameShip ship;
        public Transform currentThruster;
        public Vector3 currentThrusterLocalPosition;
        public Transform parentThruster;
        public Vector3 parentThrusterPosition;
        public Vector2 frontLocal;
        public float dt;
    }

    private struct HullAnchors
    {
        public Vector2 frontLocal;
        public Vector2 rearLocal;
        public Vector2 forwardLocal;
        public float length;
    }

    public static void Reset()
    {
        AnchorCache.Clear();
        InitializedAttachedShips.Clear();
        leviathanSegmentNameKeys = null;
        LeviathanWhipDynamics.Reset();
    }

    public static State CreateTrackingState(GameShip ship)
    {
        if (ship == null)
            return null;

        State state = new State();
        state.ship = ship;
        return state;
    }

    public static State Begin(AttachedAIShip attachedAI)
    {
        if (attachedAI == null || GameShipField == null)
            return null;

        GameShip ship = GameShipField.GetValue(attachedAI) as GameShip;

        if (ship == null ||
            LeviathanMod.Controller == null ||
            !LeviathanMod.Controller.ShouldNormalizeAttachment(ship) ||
            ship.squadron == null)
        {
            return null;
        }

        GameShip parent;

        if (LeviathanMod.Controller == null ||
            !LeviathanMod.Controller.TryGetAttachmentParent(
                ship,
                out parent) ||
            parent == null)
        {
            return null;
        }

        HullAnchors shipAnchors;
        HullAnchors parentAnchors;

        if (!TryGetAnchors(ship, out shipAnchors) ||
            !TryGetAnchors(parent, out parentAnchors))
        {
            return null;
        }

        var currentThrusterEquipment = ship.GetThruster();
        var parentThrusterEquipment = parent.GetThruster();

        if (currentThrusterEquipment == null ||
            parentThrusterEquipment == null ||
            currentThrusterEquipment.gameObject == null ||
            parentThrusterEquipment.gameObject == null)
        {
            return null;
        }

        Transform currentThruster =
            currentThrusterEquipment.gameObject.transform;

        Transform parentThruster =
            parentThrusterEquipment.gameObject.transform;

        if (currentThruster.parent != ship.transform)
            return null;

        State state = new State();
        state.ship = ship;
        state.currentThruster = currentThruster;
        state.currentThrusterLocalPosition =
            currentThruster.localPosition;
        state.parentThruster = parentThruster;
        state.parentThrusterPosition = parentThruster.position;
        state.frontLocal = shipAnchors.frontLocal;

        Vector3 frontWorld = ship.transform.TransformPoint(
            new Vector3(
                shipAnchors.frontLocal.x,
                shipAnchors.frontLocal.y,
                0f
            )
        );

        Vector3 parentRearWorld = parent.transform.TransformPoint(
            new Vector3(
                parentAnchors.rearLocal.x,
                parentAnchors.rearLocal.y,
                0f
            )
        );

        Vector3 parentForwardWorld = parent.transform.TransformDirection(
            new Vector3(
                parentAnchors.forwardLocal.x,
                parentAnchors.forwardLocal.y,
                0f
            )
        );

        parentForwardWorld.z = 0f;

        if (parentForwardWorld.sqrMagnitude > 0.0001f)
            parentForwardWorld.Normalize();

        float overlap =
            shipAnchors.length * OverlapFraction;

        Vector3 jointWorld =
            parentRearWorld + parentForwardWorld * overlap;

        jointWorld.z = state.parentThrusterPosition.z;
        frontWorld.z = ship.transform.position.z;

        ship.transform.position = frontWorld;

        Vector2 rearVector =
            shipAnchors.rearLocal - shipAnchors.frontLocal;

        currentThruster.localPosition = new Vector3(
            rearVector.x,
            rearVector.y,
            state.currentThrusterLocalPosition.z
        );

        parentThruster.position = jointWorld;

        int instanceId = ship.gameObject.GetInstanceID();

        if (InitializedAttachedShips.Add(instanceId))
        {
            NormalizeSquadronRenderOrder(ship);

            if (SegmentLengthField != null)
                SegmentLengthField.SetValue(attachedAI, -1f);

            if (HasLastThrusterField != null)
                HasLastThrusterField.SetValue(attachedAI, false);
        }

        return state;
    }

    public static void End(State state)
    {
        if (state == null || state.ship == null)
            return;

        Transform shipTransform = state.ship.transform;
        Vector3 virtualRoot = shipTransform.position;
        Quaternion rotation = shipTransform.rotation;

        if (state.currentThruster != null)
        {
            state.currentThruster.localPosition =
                state.currentThrusterLocalPosition;
        }

        if (state.parentThruster != null)
            state.parentThruster.position = state.parentThrusterPosition;

        Vector3 frontOffset = rotation * new Vector3(
            state.frontLocal.x,
            state.frontLocal.y,
            0f
        );

        Vector3 correctedPosition = virtualRoot - frontOffset;
        correctedPosition.z = virtualRoot.z;
        shipTransform.position = correctedPosition;
    }

    public static void NormalizeRemoteRenderOrder(
        GameShip ship,
        uint netId)
    {
        if (ship == null ||
            !NetIds.IsPlayerEntityNetId(netId) ||
            !IsRemoteLeviathanSegment(ship))
        {
            return;
        }

        int ownerId = NetIds.PlayerEntityOwnerOf(netId);
        uint firstEntityId = NetIds.FirstPlayerEntityId(ownerId);

        // Player-entity IDs preserve creation order. Gaps from other owned
        // entities do not matter; later Leviathan sections still sort lower.
        int entityOrdinal = (int)(netId - firstEntityId);

        if (entityOrdinal < 1)
            entityOrdinal = 1;

        UnityEngine.Rendering.SortingGroup shipGroup;

        if (!ship.TryGetComponent<
                UnityEngine.Rendering.SortingGroup>(out shipGroup) ||
            shipGroup == null)
        {
            return;
        }

        GameShip headShip =
            FindRemotePlayerShip(ownerId);

        UnityEngine.Rendering.SortingGroup headGroup = null;

        if (headShip != null)
        {
            headShip.TryGetComponent<
                UnityEngine.Rendering.SortingGroup>(out headGroup);
        }

        if (headGroup != null)
        {
            shipGroup.sortingLayerID = headGroup.sortingLayerID;

            // Head on top; domino downward toward the tail.
            shipGroup.sortingOrder =
                headGroup.sortingOrder - entityOrdinal;

            NormalizeThrusterRenderOrder(
                headShip,
                headGroup.sortingLayerID,
                headGroup.sortingOrder - ThrusterBottomOffset
            );

            NormalizeThrusterRenderOrder(
                ship,
                headGroup.sortingLayerID,
                headGroup.sortingOrder - ThrusterBottomOffset
            );
        }
        else
        {
            // Temporary deterministic fallback until the remote head exists.
            shipGroup.sortingOrder = -entityOrdinal;

            NormalizeThrusterRenderOrder(
                ship,
                shipGroup.sortingLayerID,
                shipGroup.sortingOrder - ThrusterBottomOffset
            );
        }
    }

    public static void RefreshRemoteRenderOrder(int ownerId)
    {
        if (WorldController.instance == null)
            return;

        List<GameShip> gameShips =
            WorldController.instance.GetGameShips();

        if (gameShips == null)
            return;

        for (int i = 0; i < gameShips.Count; i++)
        {
            GameShip candidate = gameShips[i];

            if (candidate == null ||
                !candidate.isRemoteEntity ||
                !NetIds.IsPlayerEntityNetId(candidate.netId) ||
                NetIds.PlayerEntityOwnerOf(candidate.netId) != ownerId)
            {
                continue;
            }

            NormalizeRemoteRenderOrder(
                candidate,
                candidate.netId
            );
        }
    }

    private static GameShip FindRemotePlayerShip(int ownerId)
    {
        if (WorldController.instance == null)
            return null;

        List<GameShip> gameShips =
            WorldController.instance.GetGameShips();

        if (gameShips == null)
            return null;

        for (int i = 0; i < gameShips.Count; i++)
        {
            GameShip candidate = gameShips[i];

            if (candidate == null ||
                candidate.isRemoteEntity ||
                candidate.netOwnerPlayerId != ownerId)
            {
                continue;
            }

            return candidate;
        }

        return null;
    }

    private static bool IsRemoteLeviathanSegment(GameShip ship)
    {
        if (ship == null ||
            ship.originalShip == null ||
            ship.originalShip.aiBehaviour != Ship.AiBehaviour.Attached)
        {
            return false;
        }

        EnsureLeviathanSegmentNameKeys();

        // If the authored base cannot be resolved, Attached is still a better
        // fallback than leaving a known Leviathan replica unsorted.
        if (leviathanSegmentNameKeys == null ||
            leviathanSegmentNameKeys.Count == 0)
        {
            return true;
        }

        string nameKey =
            ship.originalShip.GetNameLanguageKey();

        return !string.IsNullOrEmpty(nameKey) &&
            leviathanSegmentNameKeys.Contains(nameKey);
    }

    private static void EnsureLeviathanSegmentNameKeys()
    {
        if (leviathanSegmentNameKeys != null)
            return;

        leviathanSegmentNameKeys =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase
            );

        SquadronBase leviathanBase =
            Resources.FindObjectsOfTypeAll<SquadronBase>()
                .FirstOrDefault(x => x.name == "LeviathanTest");

        if (leviathanBase == null)
            return;

        Squadron squadron = leviathanBase.GetSquadron(1);

        if (squadron == null || squadron.ships == null)
            return;

        for (int i = 1; i < squadron.ships.Count; i++)
        {
            Squadron.SquadronShip slot = squadron.ships[i];

            if (slot == null || slot.npc == null)
                continue;

            Ship definition = slot.npc.GetShip();

            if (definition == null)
                continue;

            string nameKey = definition.GetNameLanguageKey();

            if (!string.IsNullOrEmpty(nameKey))
                leviathanSegmentNameKeys.Add(nameKey);
        }
    }

    private static void NormalizeSquadronRenderOrder(GameShip ship)
    {
        if (ship == null ||
            ship.squadron == null ||
            ship.squadron.ships == null ||
            ship.squadron.ships.Count < 2)
        {
            return;
        }

        List<Squadron.SquadronShip> ships =
            ship.squadron.ships;

        if (ships[0] == null || ships[0].ship == null)
            return;

        GameShip headShip = ships[0].ship;

        UnityEngine.Rendering.SortingGroup headGroup;

        if (!headShip.TryGetComponent<
                UnityEngine.Rendering.SortingGroup>(out headGroup) ||
            headGroup == null)
        {
            return;
        }

        int sortingLayerId = headGroup.sortingLayerID;
        int headOrder = headGroup.sortingOrder;
        int thrusterOrder =
            headOrder - ThrusterBottomOffset;

        // Head stays on top. Every following body/tail is exactly one
        // SortingGroup step below the preceding section.
        for (int i = 0; i < ships.Count; i++)
        {
            Squadron.SquadronShip slot = ships[i];

            if (slot == null || slot.ship == null)
                continue;

            GameShip section = slot.ship;

            UnityEngine.Rendering.SortingGroup sectionGroup;

            if (section.TryGetComponent<
                    UnityEngine.Rendering.SortingGroup>(out sectionGroup) &&
                sectionGroup != null)
            {
                sectionGroup.sortingLayerID = sortingLayerId;
                sectionGroup.sortingOrder = headOrder - i;
            }

            NormalizeThrusterRenderOrder(
                section,
                sortingLayerId,
                thrusterOrder
            );
        }
    }

    private static void NormalizeThrusterRenderOrder(
        GameShip ship,
        int sortingLayerId,
        int sortingOrder)
    {
        if (ship == null)
            return;

        // Thruster.Equip creates the primary/mirror emissions as children of
        // their thruster roots. Vanity thrusters use the same ThrusterEmission
        // child pattern on separate roots. Boost trails are later instantiated
        // beneath those same roots, so one root SortingGroup covers all of them.
        ThrusterEmission[] emissions =
            ship.GetComponentsInChildren<ThrusterEmission>(true);

        if (emissions == null || emissions.Length == 0)
            return;

        HashSet<int> handledRoots = new HashSet<int>();

        for (int i = 0; i < emissions.Length; i++)
        {
            ThrusterEmission emission = emissions[i];

            if (emission == null ||
                emission.transform == null ||
                emission.transform.parent == null)
            {
                continue;
            }

            GameObject root =
                emission.transform.parent.gameObject;

            if (root == null ||
                root == ship.gameObject ||
                !handledRoots.Add(root.GetInstanceID()))
            {
                continue;
            }

            UnityEngine.Rendering.SortingGroup thrusterGroup =
                root.GetComponent<
                    UnityEngine.Rendering.SortingGroup>();

            if (thrusterGroup == null)
            {
                thrusterGroup =
                    root.AddComponent<
                        UnityEngine.Rendering.SortingGroup>();
            }

            thrusterGroup.sortingLayerID = sortingLayerId;
            thrusterGroup.sortingOrder = sortingOrder;

            // A nested SortingGroup normally remains inside its ship's group.
            // sortAtRoot makes this thruster root participate in global sorting,
            // allowing it to sit underneath every Leviathan body.
            if (SortingGroupSortAtRootProperty != null &&
                SortingGroupSortAtRootProperty.CanWrite)
            {
                try
                {
                    SortingGroupSortAtRootProperty.SetValue(
                        thrusterGroup,
                        true,
                        null
                    );
                }
                catch
                {
                    // Older Unity versions may expose the property differently.
                    // The explicit group/order still provides the best fallback.
                }
            }
        }
    }

    private static bool TryGetAnchors(
        GameShip ship,
        out HullAnchors anchors)
    {
        if (ship == null)
        {
            anchors = default(HullAnchors);
            return false;
        }

        if (AnchorCache.TryGetValue(ship, out anchors))
            return true;

        float classRadius =
            Ship.GetClassShieldRadius(ship.GetShipClass());

        if (classRadius <= 0f)
        {
            anchors = default(HullAnchors);
            return false;
        }

        // Leviathan spacing uses only the class-size circle.
        // Right edge is front; left edge is rear.
        anchors = new HullAnchors();
        anchors.frontLocal = new Vector2(
            classRadius,
            0f
        );
        anchors.rearLocal = new Vector2(
            -classRadius,
            0f
        );
        anchors.forwardLocal = Vector2.right;
        anchors.length = classRadius * 2f;

        AnchorCache[ship] = anchors;
        return true;
    }
}
