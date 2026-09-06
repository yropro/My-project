using HarmonyLib;
using StarVortex;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

[HarmonyPatch]
public static class LeviathanFlipYAxisShipBuilderInitPatch
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
        LeviathanShipBuilderFlipYAxisIntegration integration =
            __instance.GetComponent<
                LeviathanShipBuilderFlipYAxisIntegration>();

        if (integration == null)
        {
            integration =
                __instance.gameObject.AddComponent<
                    LeviathanShipBuilderFlipYAxisIntegration>();
        }

        integration.Configure(
            __instance,
            __4,
            __5 && LeviathanMod.PlayerHasLeviathanUpgrade()
        );
    }
}

public class LeviathanShipBuilderFlipYAxisIntegration : MonoBehaviour
{
    private static readonly MethodInfo AddHistoryMethod =
        AccessTools.Method(
            typeof(ShipBuilder),
            "AddHistory",
            new Type[]
            {
                typeof(ShipBuilder.History.Type),
                typeof(List<ShipBuilderPart>)
            }
        );

    private ShipBuilderTemplates templates;
    private ShipBuilder shipBuilder;
    private Button flipYAxisButton;

    public void Configure(
        ShipBuilderTemplates shipBuilderTemplates,
        ShipBuilder builder,
        bool enabledForPlayer)
    {
        templates = shipBuilderTemplates;
        shipBuilder = builder;

        if (templates == null || templates.saveAsButton == null)
            return;

        if (flipYAxisButton == null)
            CreateButton();

        if (flipYAxisButton != null)
            flipYAxisButton.gameObject.SetActive(enabledForPlayer);
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

        clone.name = "LeviathanFlipYAxisButton";

        flipYAxisButton = clone.GetComponent<Button>();

        if (flipYAxisButton == null)
        {
            UnityEngine.Object.Destroy(clone);
            return;
        }

        flipYAxisButton.onClick = new Button.ButtonClickedEvent();
        flipYAxisButton.onClick.AddListener(FlipYAxis);

        LeviathanReflection.SetFirstTmpText(
            clone,
            "Flip Y Axis"
        );

        PlaceButton(clone);
    }

    private void PlaceButton(GameObject clone)
    {
        Transform parent = templates.saveAsButton.transform.parent;
        LayoutGroup layout = parent.GetComponent<LayoutGroup>();

        if (layout != null)
        {
            // Save As -> Save Leviathan Part -> Flip Y Axis
            clone.transform.SetSiblingIndex(
                templates.saveAsButton.transform.GetSiblingIndex() + 2
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
            saveAsRect.anchoredPosition + step * 2f;
    }

    private void FlipYAxis()
    {
        if (shipBuilder == null ||
            shipBuilder.targetRectTransform == null)
        {
            return;
        }

        bool changed = false;

        foreach (Transform child in shipBuilder.targetRectTransform)
        {
            ShipBuilderPart part;

            if (!child.TryGetComponent<ShipBuilderPart>(out part) ||
                part == null)
            {
                continue;
            }

            Vector3 position = child.localPosition;
            position.x = -position.x;
            child.localPosition = position;

            float rotation =
                Mathf.Repeat(
                    360f - child.localRotation.eulerAngles.z,
                    360f
                );

            child.localRotation =
                Quaternion.Euler(0f, 0f, rotation);

            Vector3 scale = child.localScale;
            scale.x = -scale.x;
            child.localScale = scale;

            changed = true;
        }

        if (!changed)
            return;

        shipBuilder.MarkPartsChanged();

        if (AddHistoryMethod != null)
        {
            AddHistoryMethod.Invoke(
                shipBuilder,
                new object[]
                {
                    ShipBuilder.History.Type.Unique,
                    new List<ShipBuilderPart>()
                }
            );
        }

        shipBuilder.ShowSuccess("Flipped ship over Y axis.");
    }
}
