using HarmonyLib;
using StarVortex;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

[HarmonyPatch(typeof(WorldController), "PostInit")]
public static class LeviathanSpecializationUiBootstrapPatch
{
    public static void Postfix(WorldController __instance)
    {
        LeviathanSpecializationRuntime.RegisterDefaults();

        if (__instance == null ||
            __instance.GetComponent<LeviathanSpecializationDebugUI>() != null)
        {
            return;
        }

        __instance.gameObject.AddComponent<LeviathanSpecializationDebugUI>();
    }
}

public sealed class LeviathanSpecializationDebugUI : MonoBehaviour
{
    // Prototype entry point. Once the framework is proven, this should be opened
    // from the selected Leviathan skill rather than a debug hotkey.
    private const KeyCode ToggleKey = KeyCode.F10;

    private GameObject root;
    private RectTransform graphContent;
    private Text titleText;
    private Text pointsText;
    private Text detailsText;
    private Text statusText;
    private Button spendButton;
    private Button refundButton;
    private string selectedNodeId;
    private Font font;

    private readonly Dictionary<string, Button> nodeButtons =
        new Dictionary<string, Button>(StringComparer.Ordinal);

    private void Update()
    {
        if (Input.GetKeyDown(ToggleKey))
            Toggle();
    }

    private void Toggle()
    {
        if (root == null)
            Build();

        if (root == null)
            return;

        root.SetActive(!root.activeSelf);
        if (root.activeSelf)
            Refresh();
    }

    private void Build()
    {
        font = Resources.GetBuiltinResource<Font>("Arial.ttf");

        root = new GameObject("LeviathanSpecializationPrototype");
        DontDestroyOnLoad(root);

        Canvas canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 5000;

        root.AddComponent<CanvasScaler>().uiScaleMode =
            CanvasScaler.ScaleMode.ScaleWithScreenSize;
        root.GetComponent<CanvasScaler>().referenceResolution =
            new Vector2(1920f, 1080f);
        root.AddComponent<GraphicRaycaster>();

        Image backdrop = CreateImage(root.transform, "Backdrop", new Color(0.025f, 0.035f, 0.055f, 0.97f));
        Stretch(backdrop.rectTransform, 40f, 40f, 40f, 40f);

        titleText = CreateText(backdrop.transform, "Title", 28, TextAnchor.MiddleLeft);
        SetRect(titleText.rectTransform, new Vector2(30f, -20f), new Vector2(900f, 55f), new Vector2(0f, 1f), new Vector2(0f, 1f));

        pointsText = CreateText(backdrop.transform, "Points", 20, TextAnchor.MiddleRight);
        SetRect(pointsText.rectTransform, new Vector2(-30f, -20f), new Vector2(600f, 55f), new Vector2(1f, 1f), new Vector2(1f, 1f));

        GameObject viewportObject = new GameObject("GraphViewport");
        viewportObject.transform.SetParent(backdrop.transform, false);
        RectTransform viewport = viewportObject.AddComponent<RectTransform>();
        viewport.anchorMin = new Vector2(0f, 0f);
        viewport.anchorMax = new Vector2(0.74f, 1f);
        viewport.offsetMin = new Vector2(24f, 80f);
        viewport.offsetMax = new Vector2(-12f, -90f);

        Image viewportImage = viewportObject.AddComponent<Image>();
        viewportImage.color = new Color(0.05f, 0.07f, 0.11f, 0.96f);
        viewportObject.AddComponent<Mask>().showMaskGraphic = true;

        GameObject contentObject = new GameObject("GraphContent");
        contentObject.transform.SetParent(viewportObject.transform, false);
        graphContent = contentObject.AddComponent<RectTransform>();
        graphContent.anchorMin = new Vector2(0f, 0.5f);
        graphContent.anchorMax = new Vector2(0f, 0.5f);
        graphContent.pivot = new Vector2(0f, 0.5f);

        ScrollRect scroll = viewportObject.AddComponent<ScrollRect>();
        scroll.viewport = viewport;
        scroll.content = graphContent;
        scroll.horizontal = true;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 35f;

        Image detailsPanel = CreateImage(backdrop.transform, "DetailsPanel", new Color(0.055f, 0.07f, 0.10f, 0.98f));
        RectTransform detailsRect = detailsPanel.rectTransform;
        detailsRect.anchorMin = new Vector2(0.75f, 0f);
        detailsRect.anchorMax = new Vector2(1f, 1f);
        detailsRect.offsetMin = new Vector2(8f, 80f);
        detailsRect.offsetMax = new Vector2(-24f, -90f);

        detailsText = CreateText(detailsPanel.transform, "Details", 18, TextAnchor.UpperLeft);
        Stretch(detailsText.rectTransform, 20f, 20f, 20f, 150f);

        spendButton = CreateButton(detailsPanel.transform, "Spend Point", new Vector2(20f, 68f));
        refundButton = CreateButton(detailsPanel.transform, "Refund Point", new Vector2(190f, 68f));

        spendButton.onClick.AddListener(SpendSelected);
        refundButton.onClick.AddListener(RefundSelected);

        Button close = CreateButton(backdrop.transform, "Close (F10)", new Vector2(-170f, 20f));
        RectTransform closeRect = close.GetComponent<RectTransform>();
        closeRect.anchorMin = new Vector2(1f, 0f);
        closeRect.anchorMax = new Vector2(1f, 0f);
        closeRect.anchoredPosition = new Vector2(-90f, 36f);
        close.onClick.AddListener(delegate { root.SetActive(false); });

        statusText = CreateText(backdrop.transform, "Status", 15, TextAnchor.MiddleLeft);
        statusText.color = new Color(0.85f, 0.88f, 0.92f, 1f);
        statusText.rectTransform.anchorMin = new Vector2(0f, 0f);
        statusText.rectTransform.anchorMax = new Vector2(0.72f, 0f);
        statusText.rectTransform.offsetMin = new Vector2(24f, 18f);
        statusText.rectTransform.offsetMax = new Vector2(-12f, 66f);

        root.SetActive(false);
    }

    private void Refresh()
    {
        LeviathanSpecializationRuntime.RegisterDefaults();

        LeviathanSpecializationTree tree =
            LeviathanSpecializationRegistry.Get(LeviathanStarfireSpecialization.TreeId);
        Pilot pilot = LeviathanSpecializationRuntime.GetCurrentPilot();

        if (tree == null)
            return;

        titleText.text = "STARFIRE SPECIALIZATION";

        string pointReason;
        bool canSpend = LeviathanSpecializationRuntime.CanSafelySpend(pilot, out pointReason);
        int available = canSpend
            ? LeviathanSpecializationRuntime.GetAvailablePoints(pilot)
            : 0;
        int granted = canSpend
            ? LeviathanSpecializationRuntime.GetGrantedPoints(pilot)
            : 0;
        int spent = canSpend
            ? LeviathanSpecializationRuntime.GetTotalSpentPoints(pilot)
            : 0;
        int evolutionRank = canSpend
            ? LeviathanSpecializationRuntime.GetEvolutionRank(pilot)
            : 0;

        pointsText.text = canSpend
            ? "Growth Points: " + available.ToString() +
              " available / " + granted.ToString() +
              " granted   |   Evolution " + evolutionRank.ToString() + "/5"
            : "Spending disabled";

        statusText.text = canSpend
            ? "Spend 1 Growth Point per node rank. " +
              spent.ToString() + " currently invested. F10 closes this window."
            : "PERSISTENCE SAFETY MODE: " + pointReason;

        RenderGraph(tree, pilot);
        RefreshDetails(tree, pilot);
    }

    private void RenderGraph(LeviathanSpecializationTree tree, Pilot pilot)
    {
        for (int i = graphContent.childCount - 1; i >= 0; i--)
            Destroy(graphContent.GetChild(i).gameObject);

        nodeButtons.Clear();

        LeviathanSpecializationLayout layout =
            LeviathanSpecializationAutoLayout.Build(tree);

        graphContent.sizeDelta = new Vector2(layout.Width, layout.Height);

        float left = 130f;
        float centerY = layout.Height * 0.5f;

        // Connections first so nodes render above them.
        for (int i = 0; i < layout.Edges.Count; i++)
        {
            LeviathanSpecializationLayoutEdge edge = layout.Edges[i];
            LeviathanSpecializationLayoutNode from = layout.Nodes[edge.FromNodeId];
            LeviathanSpecializationLayoutNode to = layout.Nodes[edge.ToNodeId];

            Vector2 a = new Vector2(left + from.Position.X, centerY - from.Position.Y);
            Vector2 b = new Vector2(left + to.Position.X, centerY - to.Position.Y);

            Color lineColor = edge.TargetRequirementKind == LeviathanRequirementKind.Any
                ? new Color(0.35f, 0.72f, 1f, 0.72f)
                : new Color(0.72f, 0.76f, 0.82f, 0.68f);

            DrawLine(graphContent, a, b, lineColor, 4f);
        }

        LeviathanSpecializationState state =
            LeviathanSpecializationRuntime.GetState(pilot, tree.Id);

        IList<LeviathanSpecializationNode> nodes = tree.Nodes;
        for (int i = 0; i < nodes.Count; i++)
        {
            LeviathanSpecializationNode node = nodes[i];
            LeviathanSpecializationLayoutNode nodeLayout = layout.Nodes[node.Id];

            string reason;
            bool available = state != null && state.CanInvest(tree, node.Id, out reason);
            bool invested = state != null && state.GetRank(node.Id) > 0;

            string label = node.Name + "\n" +
                (state == null ? "0" : state.GetRank(node.Id).ToString()) +
                "/" + node.MaxRank.ToString();

            Button button = CreateNodeButton(
                graphContent,
                label,
                new Vector2(left + nodeLayout.Position.X, centerY - nodeLayout.Position.Y),
                node.Type,
                invested,
                available
            );

            string captured = node.Id;
            button.onClick.AddListener(delegate
            {
                selectedNodeId = captured;
                RefreshDetails(tree, LeviathanSpecializationRuntime.GetCurrentPilot());
            });

            nodeButtons[node.Id] = button;

            // Explicit ALL/ANY gateway label for simple multi-parent relationships.
            List<string> simpleParents;
            if ((node.Requirement.Kind == LeviathanRequirementKind.All ||
                 node.Requirement.Kind == LeviathanRequirementKind.Any) &&
                node.Requirement.TryGetSimpleParents(out simpleParents) &&
                simpleParents.Count > 1)
            {
                Text badge = CreateText(graphContent, "Gateway", 12, TextAnchor.MiddleCenter);
                badge.text = node.Requirement.Kind == LeviathanRequirementKind.All
                    ? "ALL"
                    : "ANY";
                badge.color = node.Requirement.Kind == LeviathanRequirementKind.All
                    ? new Color(1f, 0.78f, 0.32f, 1f)
                    : new Color(0.35f, 0.78f, 1f, 1f);
                SetRect(
                    badge.rectTransform,
                    new Vector2(
                        left + nodeLayout.Position.X - 82f,
                        centerY - nodeLayout.Position.Y
                    ),
                    new Vector2(44f, 24f),
                    new Vector2(0f, 1f),
                    new Vector2(0f, 1f)
                );
            }
        }
    }

    private void RefreshDetails(LeviathanSpecializationTree tree, Pilot pilot)
    {
        LeviathanSpecializationState state =
            LeviathanSpecializationRuntime.GetState(pilot, tree.Id);

        if (string.IsNullOrEmpty(selectedNodeId))
        {
            detailsText.text =
                "Select a node.\n\n" +
                "The graph is generated entirely from prerequisite relationships. " +
                "ALL/ANY labels are renderer-generated; exclusivity is independent of layout.";
            spendButton.interactable = false;
            refundButton.interactable = false;
            return;
        }

        LeviathanSpecializationNode node = tree.GetNode(selectedNodeId);
        if (node == null)
            return;

        int rank = state == null ? 0 : state.GetRank(node.Id);
        string text = node.Name + "\n";
        text += node.Type.ToString().ToUpperInvariant() + "   " + rank + "/" + node.MaxRank + "\n\n";

        if (!string.IsNullOrEmpty(node.Description))
            text += node.Description + "\n\n";

        text += "Requires: " + node.Requirement.Describe(tree.GetNodeName) + "\n";

        if (!string.IsNullOrEmpty(node.ExclusiveGroup))
        {
            text += "Exclusive group: ";
            IList<LeviathanSpecializationNode> members =
                tree.GetExclusiveGroupMembers(node.ExclusiveGroup);

            for (int i = 0; i < members.Count; i++)
            {
                if (i > 0) text += ", ";
                text += members[i].Name;
            }
            text += "\n";
        }

        text += "\nEffects at next/current rank:\n";
        int displayRank = Math.Max(1, Math.Min(node.MaxRank, rank + (rank < node.MaxRank ? 1 : 0)));

        for (int i = 0; i < node.Effects.Length; i++)
        {
            text += "• " + node.Effects[i].Describe(
                displayRank,
                LeviathanStarfireSpecialization.GetStatName
            ) + "\n";
        }

        detailsText.text = text;

        string reason;
        bool safe = LeviathanSpecializationRuntime.CanSafelySpend(pilot, out reason);
        bool unlocked = LeviathanSpecializationRuntime.IsTreeUnlocked(pilot, tree);
        bool hasPoint = safe && LeviathanSpecializationRuntime.GetAvailablePoints(pilot) > 0;
        spendButton.interactable = unlocked && hasPoint && state != null &&
            state.CanInvest(tree, node.Id, out reason);
        refundButton.interactable = safe && state != null && state.CanRefund(tree, node.Id, out reason);

        if (!unlocked)
        {
            detailsText.text += "\nPurchase Evolution before spending Growth Points in specialization trees.";
        }
    }

    private void SpendSelected()
    {
        Pilot pilot = LeviathanSpecializationRuntime.GetCurrentPilot();
        string reason;
        string nodeName = selectedNodeId;

        LeviathanSpecializationTree tree =
            LeviathanSpecializationRegistry.Get(LeviathanStarfireSpecialization.TreeId);
        if (tree != null)
        {
            LeviathanSpecializationNode node = tree.GetNode(selectedNodeId);
            if (node != null)
                nodeName = node.Name;
        }

        bool success = LeviathanSpecializationRuntime.TryInvest(
            pilot,
            LeviathanStarfireSpecialization.TreeId,
            selectedNodeId,
            out reason
        );

        Refresh();

        statusText.text = success
            ? "Spent 1 Growth Point on " + nodeName + "."
            : "Could not spend Growth Point: " + reason;
    }

    private void RefundSelected()
    {
        Pilot pilot = LeviathanSpecializationRuntime.GetCurrentPilot();
        string reason;
        string nodeName = selectedNodeId;

        LeviathanSpecializationTree tree =
            LeviathanSpecializationRegistry.Get(LeviathanStarfireSpecialization.TreeId);
        if (tree != null)
        {
            LeviathanSpecializationNode node = tree.GetNode(selectedNodeId);
            if (node != null)
                nodeName = node.Name;
        }

        bool success = LeviathanSpecializationRuntime.TryRefund(
            pilot,
            LeviathanStarfireSpecialization.TreeId,
            selectedNodeId,
            out reason
        );

        Refresh();

        statusText.text = success
            ? "Refunded 1 Growth Point from " + nodeName + "."
            : "Could not refund Growth Point: " + reason;
    }

    private Button CreateNodeButton(
        Transform parent,
        string label,
        Vector2 position,
        LeviathanSpecializationNodeType type,
        bool invested,
        bool available)
    {
        Vector2 size = type == LeviathanSpecializationNodeType.Root
            ? new Vector2(178f, 86f)
            : type == LeviathanSpecializationNodeType.Keystone
                ? new Vector2(170f, 82f)
                : type == LeviathanSpecializationNodeType.Major
                    ? new Vector2(150f, 70f)
                    : new Vector2(132f, 62f);

        Color color = invested
            ? new Color(0.14f, 0.48f, 0.72f, 1f)
            : available
                ? new Color(0.18f, 0.25f, 0.34f, 1f)
                : new Color(0.10f, 0.12f, 0.16f, 1f);

        Image image = CreateImage(parent, "Node", color);
        RectTransform rect = image.rectTransform;
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = new Vector2(position.x, -position.y);
        rect.sizeDelta = size;

        Button button = image.gameObject.AddComponent<Button>();
        ColorBlock colors = button.colors;
        colors.highlightedColor = new Color(0.25f, 0.52f, 0.78f, 1f);
        colors.pressedColor = new Color(0.10f, 0.35f, 0.58f, 1f);
        button.colors = colors;

        Text text = CreateText(image.transform, "Label", 14, TextAnchor.MiddleCenter);
        Stretch(text.rectTransform, 6f, 6f, 6f, 6f);
        text.text = label;

        return button;
    }

    private Button CreateButton(Transform parent, string label, Vector2 anchored)
    {
        Image image = CreateImage(parent, "Button_" + label, new Color(0.14f, 0.28f, 0.40f, 1f));
        RectTransform rect = image.rectTransform;
        rect.anchorMin = new Vector2(0f, 0f);
        rect.anchorMax = new Vector2(0f, 0f);
        rect.pivot = new Vector2(0f, 0f);
        rect.anchoredPosition = anchored;
        rect.sizeDelta = new Vector2(155f, 46f);

        Button button = image.gameObject.AddComponent<Button>();
        Text text = CreateText(image.transform, "Label", 15, TextAnchor.MiddleCenter);
        Stretch(text.rectTransform, 4f, 4f, 4f, 4f);
        text.text = label;
        return button;
    }

    private Image CreateImage(Transform parent, string name, Color color)
    {
        GameObject obj = new GameObject(name);
        obj.transform.SetParent(parent, false);
        RectTransform rect = obj.AddComponent<RectTransform>();
        Image image = obj.AddComponent<Image>();
        image.color = color;
        return image;
    }

    private Text CreateText(
        Transform parent,
        string name,
        int size,
        TextAnchor anchor)
    {
        GameObject obj = new GameObject(name);
        obj.transform.SetParent(parent, false);
        obj.AddComponent<RectTransform>();
        Text text = obj.AddComponent<Text>();
        text.font = font;
        text.fontSize = size;
        text.alignment = anchor;
        text.color = Color.white;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        return text;
    }

    private void DrawLine(
        RectTransform parent,
        Vector2 from,
        Vector2 to,
        Color color,
        float width)
    {
        Image image = CreateImage(parent, "Edge", color);
        RectTransform rect = image.rectTransform;
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 0.5f);

        Vector2 delta = to - from;
        rect.sizeDelta = new Vector2(delta.magnitude, width);
        rect.anchoredPosition = new Vector2(from.x, -from.y);
        rect.localEulerAngles = new Vector3(
            0f,
            0f,
            -Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg
        );
    }

    private static void Stretch(
        RectTransform rect,
        float left,
        float right,
        float top,
        float bottom)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(left, bottom);
        rect.offsetMax = new Vector2(-right, -top);
    }

    private static void SetRect(
        RectTransform rect,
        Vector2 anchored,
        Vector2 size,
        Vector2 anchorMin,
        Vector2 anchorMax)
    {
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.pivot = new Vector2(
            anchorMin.x == 1f ? 1f : 0f,
            anchorMin.y == 1f ? 1f : 0f
        );
        rect.anchoredPosition = anchored;
        rect.sizeDelta = size;
    }
}
