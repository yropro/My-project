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

    public const Upgrade.Key GrowthUpgrade =
        (Upgrade.Key)81;

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

        harmony.PatchAll();

        if (controllerObject != null)
            return;

        controllerObject = new GameObject("Leviathan Runtime");
        UnityEngine.Object.DontDestroyOnLoad(controllerObject);

        Controller = controllerObject.AddComponent<LeviathanController>();

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

    public static bool PlayerHasLeviathanUpgrade()
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
            pilot.GetUpgradeLevel(GrowthUpgrade) >= 1;
    }
}

[HarmonyPatch(typeof(WorldController), "PostInit")]
public static class LeviathanWorldPostInitPatch
{
    public static void Postfix(WorldController __instance)
    {
        LeviathanMod.Controller?.SetPlayerShip(
            __instance.GetCurrentPlayerShip()
        );
    }
}

[HarmonyPatch(typeof(WorldController), "SetCurrentPlayerShip")]
public static class LeviathanPlayerShipChangedPatch
{
    public static void Postfix(GameShip __0)
    {
        LeviathanMod.Controller?.SetPlayerShip(__0);
    }
}

[HarmonyPatch(typeof(WorldController), "OnDestroy")]
public static class LeviathanWorldDestroyedPatch
{
    public static void Prefix()
    {
        LeviathanMod.Controller?.ClearPlayerShip();
        LeviathanSegmentStatusProtection.Reset();
        LeviathanSegmentDamageLimiter.Reset();
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
            __5 && LeviathanMod.PlayerHasLeviathanUpgrade()
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

        bool enabled = LeviathanMod.PlayerHasLeviathanUpgrade() &&
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
            !LeviathanMod.PlayerHasLeviathanUpgrade() ||
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
                "Leviathan part must be Body, 1-15, or Tail."
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

        int segmentNumber;

        if (int.TryParse(text, out segmentNumber) &&
            segmentNumber >= 1 &&
            segmentNumber <= 15)
        {
            role = segmentNumber.ToString();
            return true;
        }

        return false;
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

        Template body = LoadRole("Body");

        if (body != null && !string.IsNullOrEmpty(body.serialized))
            result["Body"] = body.serialized;

        for (int i = 1; i <= 15; i++)
        {
            string role = i.ToString();
            Template template = LoadRole(role);

            if (template == null ||
                string.IsNullOrEmpty(template.serialized))
            {
                continue;
            }

            result[role] = template.serialized;
        }

        Template tail = LoadRole("Tail");

        if (tail != null && !string.IsNullOrEmpty(tail.serialized))
            result["Tail"] = tail.serialized;

        return result;
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

        for (int i = 1; i <= 15; i++)
        {
            Template segment = LoadRole(i.ToString());

            if (segment != null)
                result.Add(segment);
        }

        if (tail == null && !string.IsNullOrEmpty(stockTail))
            tail = CreateStockTemplate("Tail", stockTail);

        if (tail != null)
            result.Add(tail);

        return result;
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

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + playerId;
                hash = hash * 31 + attackerId;
                hash = hash * 31 + (int)statusType;
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
                statusType == other.statusType;
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
        StatusEffect statusEffect)
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

        // Direct AddStatusEffect paths such as Wildfire can otherwise put the same
        // debuff on many segments at once. Collapse those to one head opportunity.
        if (!DirectSeenThisFrame.Add(key))
            return false;

        return !RollDiscard(player);
    }

    private static bool RollDiscard(GameShip player)
    {
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

[HarmonyPatch(typeof(GameShip), "AddStatusEffect")]
public static class LeviathanSegmentAddStatusEffectPatch
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

        GameShip player =
            LeviathanMod.Controller?.GetDamageRedirectTarget(__instance);

        if (player == null)
            return true;

        // Negative statuses never live on a Leviathan segment. Direct status
        // application paths get one deduped/discardable opportunity on the head.
        if (LeviathanSegmentStatusProtection.ShouldTransferDirectStatus(
                player,
                __0))
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
public static class LeviathanSegmentDamagePatch
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
                typeof(bool),
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
        bool __3,
        Vector2 __4,
        GameShip __5,
        bool __6,
        ref bool __result)
    {
        GameShip player =
            LeviathanMod.Controller?.GetDamageRedirectTarget(__instance);

        if (player == null)
            return true;

        if (!LeviathanSegmentDamageLimiter.AllowSegmentDamage(
                __instance,
                player,
                __0,
                __5))
        {
            __result = false;
            return false;
        }

        float multiplier =
            LeviathanBehemoth.GetSegmentDamageMultiplier(player);

        DamageData[] scaledDamage =
            ScaleDamageData(__1, multiplier);

        bool haloSource =
            LeviathanSegmentSourceTuning.IsHaloSource(
                __instance,
                __5
            );

        // Final transfer check: after all normal segment scaling has already
        // been applied, halve the resulting redirected damage if the source
        // was a Halo. Direct head hits never pass through this path.
        if (haloSource)
        {
            scaledDamage = ScaleDamageData(
                scaledDamage,
                LeviathanSegmentSourceTuning
                    .EnemyHaloTransferredDamageMultiplier
            );
        }

        LeviathanSegmentSourceTuning.LogSource(
            __instance,
            __5,
            __0,
            __1,
            haloSource
                ? multiplier *
                    LeviathanSegmentSourceTuning
                        .EnemyHaloTransferredDamageMultiplier
                : multiplier,
            haloSource
        );

        float statusEffectChance =
            LeviathanSegmentStatusProtection.FilterStatusEffectChance(
                player,
                __0,
                __2,
                __4,
                __5
            );

        CopyDamageAttribution(__instance, player);

        __result = player.Damage(
            __0,
            scaledDamage,
            statusEffectChance,
            __3,
            __4,
            __5,
            true
        );

        return false;
    }

    private static void CopyDamageAttribution(
        GameShip segment,
        GameShip player)
    {
        if (segment == null || player == null)
            return;

        player.lastDamagedByWeaponName =
            segment.lastDamagedByWeaponName;
        player.lastDamagedByShipName =
            segment.lastDamagedByShipName;
        player.lastDamagedByFaction =
            segment.lastDamagedByFaction;
    }

    private static DamageData[] ScaleDamageData(
        DamageData[] source,
        float multiplier)
    {
        if (source == null)
            return null;

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
public static class LeviathanSegmentDirectDamagePatch
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
        GameShip player =
            LeviathanMod.Controller?.GetDamageRedirectTarget(__instance);

        if (player == null)
            return true;

        float multiplier =
            LeviathanBehemoth.GetSegmentDamageMultiplier(player);

        player.lastDirectDamageSourceName =
            __instance.lastDirectDamageSourceName;

        __result = player.DirectDamage(
            __0,
            __1 * multiplier,
            true
        );

        return false;
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

    public static void Prefix(
        AttachedAIShip __instance,
        ref LeviathanAttachmentNormalizer.State __state)
    {
        __state = LeviathanAttachmentNormalizer.Begin(__instance);
    }

    public static void Postfix(
        LeviathanAttachmentNormalizer.State __state)
    {
        LeviathanAttachmentNormalizer.End(__state);
    }
}

public static class LeviathanAttachmentNormalizer
{
    private const float OverlapFraction = 0.10f;

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

    public sealed class State
    {
        public GameShip ship;
        public Transform currentThruster;
        public Vector3 currentThrusterLocalPosition;
        public Transform parentThruster;
        public Vector3 parentThrusterPosition;
        public Vector2 frontLocal;
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

        GameShip parent = ship.squadron.GetNextShipUp(ship);

        if (parent == null)
            return null;

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

    private static void NormalizeSquadronRenderOrder(GameShip ship)
    {
        if (ship == null ||
            ship.squadron == null ||
            ship.squadron.ships == null ||
            ship.squadron.ships.Count < 2)
        {
            return;
        }

        List<Squadron.SquadronShip> ships = ship.squadron.ships;
        int shipIndex = -1;

        for (int i = 1; i < ships.Count; i++)
        {
            if (ships[i] != null && ships[i].ship == ship)
            {
                shipIndex = i;
                break;
            }
        }

        if (shipIndex < 1 || ships[0] == null || ships[0].ship == null)
            return;

        UnityEngine.Rendering.SortingGroup headGroup;
        UnityEngine.Rendering.SortingGroup shipGroup;

        if (!ships[0].ship.TryGetComponent<
                UnityEngine.Rendering.SortingGroup>(out headGroup) ||
            !ship.TryGetComponent<
                UnityEngine.Rendering.SortingGroup>(out shipGroup) ||
            headGroup == null ||
            shipGroup == null)
        {
            return;
        }

        // Draw the chain from head toward tail. Each following body sits
        // above the previous ship so its body covers the previous thruster.
        shipGroup.sortingLayerID = headGroup.sortingLayerID;
        shipGroup.sortingOrder = headGroup.sortingOrder + shipIndex;
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
