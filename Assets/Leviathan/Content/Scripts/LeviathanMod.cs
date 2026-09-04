using HarmonyLib;
using StarVortex;
using System;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static StarVortex.Damageable;

public class LeviathanMod : IStarVortexMod
{
    public const Upgrade.Key TriggerUpgrade =
        Upgrade.Key.GladiatorHullLeech;

    private static GameObject controllerObject;

    public static LeviathanController Controller { get; private set; }

    public void Init(ModInfo modInfo, Harmony harmony)
    {
        Debug.Log("[Leviathan] Init");

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
    }
}

// Install one native-looking button next to the game's existing Save As button.
// ShipBuilderTemplates.Init is called by ShipBuilder.Init after the real builder
// has enabled its proper Rewired input map, cursor behavior, and modal UI.
[HarmonyPatch(typeof(ShipBuilderTemplates), "Init")]
public static class LeviathanShipBuilderTemplatesInitPatch
{
    public static void Postfix(
        ShipBuilderTemplates __instance,
        ShipBuilder shipBuilder,
        bool isPlayer)
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
            shipBuilder,
            isPlayer && PlayerHasLeviathanUpgrade()
        );
    }

    private static bool PlayerHasLeviathanUpgrade()
    {
        if (Core.instance == null ||
            Core.instance.player == null ||
            Core.instance.player.pilot == null)
        {
            return false;
        }

        return Core.instance.player.pilot.GetUpgradeLevel(
            LeviathanMod.TriggerUpgrade
        ) >= 1;
    }
}

// The extra button deliberately reuses the game's real Save As popup. This
// prefix only translates the role the player typed into a reserved template
// display name, then lets Star Vortex's own SaveAsTemplate method continue.
[HarmonyPatch(typeof(ShipBuilderTemplates), "SaveAsTemplate")]
public static class LeviathanShipBuilderSaveAsTemplatePatch
{
    public static bool Prefix(ShipBuilderTemplates __instance)
    {
        LeviathanShipBuilderIntegration integration =
            __instance.GetComponent<LeviathanShipBuilderIntegration>();

        if (integration == null || !integration.IsLeviathanSaveMode)
            return true;

        return integration.PrepareLeviathanTemplateName();
    }
}

// Native Save As, successful saves, Cancel, etc. all funnel through
// ClosePanels. Restoring the popup text here prevents our temporary Leviathan
// wording/state from leaking into ordinary template saves.
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
    private ShipBuilderTemplates templates;
    private ShipBuilder shipBuilder;
    private Button leviathanButton;

    private bool leviathanSaveMode;

    private TMP_Text saveAsHeader;
    private string originalHeaderText;
    private bool headerCaptured;

    public bool IsLeviathanSaveMode
    {
        get { return leviathanSaveMode; }
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

        // Replace the cloned Button's persistent SaveAsClicked event entirely.
        // Keeping the cloned GameObject preserves the game's visuals, sounds,
        // navigation components, and hover behavior.
        leviathanButton.onClick = new Button.ButtonClickedEvent();
        leviathanButton.onClick.AddListener(BeginLeviathanSaveMode);

        TMP_Text label =
            clone.GetComponentInChildren<TMP_Text>(true);

        if (label != null)
            label.text = "Save Leviathan Part";

        PlaceNextToSaveAs(clone);

        Debug.Log(
            "[Leviathan] Added Ship Builder 'Save Leviathan Part' button."
        );
    }

    private void PlaceNextToSaveAs(GameObject clone)
    {
        Transform parent = templates.saveAsButton.transform.parent;

        // If the native UI uses a LayoutGroup, inserting immediately after
        // Save As lets Unity do the positioning exactly as the game does.
        LayoutGroup layout = parent.GetComponent<LayoutGroup>();

        if (layout != null)
        {
            clone.transform.SetSiblingIndex(
                templates.saveAsButton.transform.GetSiblingIndex() + 1
            );
            return;
        }

        // Fallback for manually positioned buttons: continue the spacing from
        // Save -> Save As. If that spacing is unusable, put ours just below.
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
        if (templates == null)
            return;

        // Let Star Vortex perform all of its normal validation and open the
        // real Save As modal. SaveAsClicked also establishes proper focus.
        templates.SaveAsClicked();

        if (templates.saveAsObject == null ||
            !templates.saveAsObject.activeSelf ||
            templates.saveAsTemplateName == null)
        {
            return;
        }

        string suggestedRole = "Body";
        string currentName = templates.saveAsTemplateName.text;
        string currentRole;

        if (LeviathanSegmentTemplates.TryGetRoleFromTemplateName(
                currentName,
                out currentRole))
        {
            suggestedRole = currentRole;
        }

        leviathanSaveMode = true;
        CaptureAndChangeHeader();

        // The player only has to type the role, not a magic full template name.
        templates.saveAsTemplateName.text = suggestedRole;

        Debug.Log(
            "[Leviathan] Save As entered Leviathan-part mode. " +
            "Valid roles: Body, 1, 2, 3, Tail."
        );
    }

    public bool PrepareLeviathanTemplateName()
    {
        if (!leviathanSaveMode ||
            templates == null ||
            templates.saveAsTemplateName == null)
        {
            return true;
        }

        string role;

        if (!LeviathanSegmentTemplates.TryNormalizeRole(
                templates.saveAsTemplateName.text,
                out role))
        {
            if (shipBuilder != null)
            {
                shipBuilder.ShowError(
                    "Leviathan part must be Body, 1, 2, 3, or Tail."
                );
            }

            return false;
        }

        string templateName =
            LeviathanSegmentTemplates.GetTemplateName(role);

        if (string.IsNullOrEmpty(templateName))
            return false;

        // From here on, native SaveAsTemplate handles everything: duplicate
        // detection, the Replace confirmation, random uid/file creation,
        // SerializeParts(), Savable.Save(), currentTemplate, and button state.
        templates.saveAsTemplateName.text = templateName;

        Debug.Log(
            "[Leviathan] Saving Leviathan role '" +
            role +
            "' as native template '" +
            templateName +
            "'."
        );

        return true;
    }

    public void EndLeviathanSaveMode()
    {
        leviathanSaveMode = false;
        RestoreHeader();
    }

    private void CaptureAndChangeHeader()
    {
        if (templates == null || templates.saveAsObject == null)
            return;

        if (saveAsHeader == null)
        {
            TMP_Text[] texts =
                templates.saveAsObject.GetComponentsInChildren<TMP_Text>(true);

            for (int i = 0; i < texts.Length; i++)
            {
                TMP_Text candidate = texts[i];

                if (candidate == null ||
                    candidate == templates.saveAsTemplateName.textComponent)
                {
                    continue;
                }

                string objectName =
                    candidate.gameObject.name.ToLowerInvariant();

                if (objectName.Contains("header") ||
                    objectName.Contains("title"))
                {
                    saveAsHeader = candidate;
                    break;
                }
            }
        }

        if (saveAsHeader == null)
            return;

        if (!headerCaptured)
        {
            originalHeaderText = saveAsHeader.text;
            headerCaptured = true;
        }

        saveAsHeader.text =
            "Leviathan Part: Body / 1 / 2 / 3 / Tail";
    }

    private void RestoreHeader()
    {
        if (saveAsHeader != null && headerCaptured)
            saveAsHeader.text = originalHeaderText;
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

        __result = player.Damage(
            __0,
            __1,
            __2,
            __3,
            __4,
            __5,
            __6
        );

        return false;
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

        __result = player.DirectDamage(
            __0,
            __1,
            __2
        );

        return false;
    }
}
