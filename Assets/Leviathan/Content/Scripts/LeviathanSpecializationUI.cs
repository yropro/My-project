using HarmonyLib;
using StarVortex;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

[HarmonyPatch(typeof(WorldController), "PostInit")]
public static class LeviathanSpecializationUiBootstrapPatch
{
    public static void Postfix(WorldController __instance)
    {
        CoreSpecializationRuntime.RegisterDefaults();

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
    private const KeyCode ToggleKey = KeyCode.F10;

    private GameObject root;
    private RectTransform graphContent;
    private RectTransform treeBar;
    private ScrollRect graphScroll;
    private string lastRenderedTreeId;
    private Text titleText;
    private Text pointsText;
    private Text detailsText;
    private Text statusText;
    private Button spendButton;
    private Button refundButton;
    private string selectedTreeId;
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
        {
            CoreSpecializationRuntime.RefreshTreeDefinitions();
            lastRenderedTreeId = null;
            selectedNodeId = null;
            Refresh();
        }
    }

    private void Build()
    {
        font = Resources.GetBuiltinResource<Font>("Arial.ttf");

        root = new GameObject("LeviathanSpecializationFramework");
        DontDestroyOnLoad(root);

        Canvas canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 5000;

        CanvasScaler scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        root.AddComponent<GraphicRaycaster>();

        Image backdrop = CreateImage(
            root.transform,
            "Backdrop",
            new Color(0.025f, 0.035f, 0.055f, 0.97f)
        );
        Stretch(backdrop.rectTransform, 40f, 40f, 40f, 40f);

        titleText = CreateText(backdrop.transform, "Title", 28, TextAnchor.MiddleLeft);
        SetRect(
            titleText.rectTransform,
            new Vector2(30f, -20f),
            new Vector2(900f, 55f),
            new Vector2(0f, 1f),
            new Vector2(0f, 1f)
        );

        pointsText = CreateText(backdrop.transform, "Points", 20, TextAnchor.MiddleRight);
        SetRect(
            pointsText.rectTransform,
            new Vector2(-30f, -20f),
            new Vector2(700f, 55f),
            new Vector2(1f, 1f),
            new Vector2(1f, 1f)
        );

        GameObject treeBarObject = new GameObject("TreeBar");
        treeBarObject.transform.SetParent(backdrop.transform, false);
        treeBar = treeBarObject.AddComponent<RectTransform>();
        treeBar.anchorMin = new Vector2(0f, 1f);
        treeBar.anchorMax = new Vector2(1f, 1f);
        treeBar.pivot = new Vector2(0f, 1f);
        treeBar.offsetMin = new Vector2(24f, -132f);
        treeBar.offsetMax = new Vector2(-24f, -74f);

        GameObject viewportObject = new GameObject("GraphViewport");
        viewportObject.transform.SetParent(backdrop.transform, false);
        RectTransform viewport = viewportObject.AddComponent<RectTransform>();
        viewport.anchorMin = new Vector2(0f, 0f);
        viewport.anchorMax = new Vector2(0.74f, 1f);
        viewport.offsetMin = new Vector2(24f, 80f);
        viewport.offsetMax = new Vector2(-12f, -146f);

        Image viewportImage = viewportObject.AddComponent<Image>();
        viewportImage.color = new Color(0.05f, 0.07f, 0.11f, 0.96f);
        viewportObject.AddComponent<Mask>().showMaskGraphic = true;

        GameObject contentObject = new GameObject("GraphContent");
        contentObject.transform.SetParent(viewportObject.transform, false);
        graphContent = contentObject.AddComponent<RectTransform>();
        graphContent.anchorMin = new Vector2(0f, 0.5f);
        graphContent.anchorMax = new Vector2(0f, 0.5f);
        graphContent.pivot = new Vector2(0f, 0.5f);

        graphScroll = viewportObject.AddComponent<ScrollRect>();
        graphScroll.viewport = viewport;
        graphScroll.content = graphContent;
        graphScroll.horizontal = true;
        graphScroll.vertical = true;
        graphScroll.movementType = ScrollRect.MovementType.Clamped;
        graphScroll.scrollSensitivity = 35f;

        Image detailsPanel = CreateImage(
            backdrop.transform,
            "DetailsPanel",
            new Color(0.055f, 0.07f, 0.10f, 0.98f)
        );
        RectTransform detailsRect = detailsPanel.rectTransform;
        detailsRect.anchorMin = new Vector2(0.75f, 0f);
        detailsRect.anchorMax = new Vector2(1f, 1f);
        detailsRect.offsetMin = new Vector2(8f, 80f);
        detailsRect.offsetMax = new Vector2(-24f, -146f);

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
        CoreSpecializationRuntime.RegisterDefaults();
        Pilot pilot = CoreSpecializationRuntime.GetCurrentPilot();

        IList<CoreSpecializationTree> trees =
            CoreSpecializationPolicies.GetTrees(CoreSpecializationRuntime.GetEffectiveClass(pilot));

        if (trees.Count == 0)
            return;

        CoreSpecializationTree selectedTree =
            string.IsNullOrEmpty(selectedTreeId)
                ? null
                : CoreSpecializationRegistry.Get(selectedTreeId);

        if (selectedTree == null ||
            !CoreSpecializationRuntime.IsTreeUnlocked(pilot, selectedTree))
        {
            selectedTreeId = FindFirstUnlockedTreeId(pilot, trees);
            selectedNodeId = null;
        }

        CoreSpecializationTree tree =
            CoreSpecializationRegistry.Get(selectedTreeId);

        if (tree == null)
            return;

        titleText.text = tree.Name.ToUpperInvariant() + " SPECIALIZATION";

        string pointReason;
        bool canSpend = CoreSpecializationRuntime.CanSafelySpend(pilot, out pointReason);
        int available = canSpend
            ? CoreSpecializationRuntime.GetAvailablePoints(pilot)
            : 0;
        int granted = canSpend
            ? CoreSpecializationRuntime.GetGrantedPoints(pilot)
            : 0;
        int spent = canSpend
            ? CoreSpecializationRuntime.GetTotalSpentPoints(pilot)
            : 0;
        int evolutionRank = canSpend
            ? CoreSpecializationRuntime.GetProgressionRank(pilot)
            : 0;

        pointsText.text = canSpend
            ? CoreSpecializationPoints.GetCurrencyName(pilot) + ": " +
              available.ToString() +
              " available / " + granted.ToString() +
              " granted   |   Rank " + evolutionRank.ToString()
            : "Spending disabled";

        statusText.text = canSpend
            ? spent.ToString() + " " +
              CoreSpecializationPoints.GetCurrencyName(pilot) +
              " invested. Granted roots cost 0. F10 closes."
            : "PERSISTENCE SAFETY MODE: " + pointReason;

        bool treeChanged = !string.Equals(
            lastRenderedTreeId,
            tree.Id,
            StringComparison.Ordinal
        );

        RefreshTreeTabs(pilot, trees);
        RenderGraph(tree, pilot);

        if (treeChanged)
        {
            ResetGraphView();
            lastRenderedTreeId = tree.Id;
        }

        RefreshDetails(tree, pilot);
    }

    private void ResetGraphView()
    {
        if (graphScroll != null)
            graphScroll.StopMovement();

        if (graphContent != null)
            graphContent.anchoredPosition = Vector2.zero;

        Canvas.ForceUpdateCanvases();

        if (graphScroll != null)
        {
            // Tree graphs progress left-to-right and should open at the root,
            // vertically centered. This also prevents a scroll offset from a
            // previously selected tree from hiding sibling capstones.
            graphScroll.horizontalNormalizedPosition = 0f;
            graphScroll.verticalNormalizedPosition = 0.5f;
        }
    }

    private void RefreshTreeTabs(
        Pilot pilot,
        IList<CoreSpecializationTree> trees)
    {
        for (int i = treeBar.childCount - 1; i >= 0; i--)
            Destroy(treeBar.GetChild(i).gameObject);

        float x = 0f;
        for (int i = 0; i < trees.Count; i++)
        {
            CoreSpecializationTree tree = trees[i];
            bool unlocked = CoreSpecializationRuntime.IsTreeUnlocked(pilot, tree);
            if (!unlocked)
                continue;

            bool selected = string.Equals(
                selectedTreeId,
                tree.Id,
                StringComparison.Ordinal
            );

            Button button = CreateTopButton(
                treeBar,
                tree.Name,
                new Vector2(x, 0f),
                selected,
                true
            );

            string captured = tree.Id;
            button.onClick.AddListener(delegate
            {
                selectedTreeId = captured;
                selectedNodeId = null;
                Refresh();
            });

            x += 190f;
        }
    }

    private static string FindFirstUnlockedTreeId(
        Pilot pilot,
        IList<CoreSpecializationTree> trees)
    {
        CoreSpecializationTree evolution =
            CoreSpecializationRegistry.Get(LeviathanEvolutionTree.TreeId);

        if (evolution != null &&
            CoreSpecializationRuntime.IsTreeUnlocked(pilot, evolution))
        {
            return evolution.Id;
        }

        for (int i = 0; i < trees.Count; i++)
        {
            if (CoreSpecializationRuntime.IsTreeUnlocked(pilot, trees[i]))
                return trees[i].Id;
        }

        return trees.Count > 0 ? trees[0].Id : null;
    }

    private void RenderGraph(CoreSpecializationTree tree, Pilot pilot)
    {
        for (int i = graphContent.childCount - 1; i >= 0; i--)
            Destroy(graphContent.GetChild(i).gameObject);

        nodeButtons.Clear();

        CoreSpecializationLayout layout =
            string.Equals(tree.Id, LeviathanEvolutionTree.TreeId, StringComparison.Ordinal)
                ? BuildEvolutionLayout(tree)
                : CoreSpecializationAutoLayout.Build(tree);

        int definitionNodeCount = tree.Nodes.Count;
        int layoutNodeCount = layout.Nodes.Count;

        graphContent.sizeDelta = new Vector2(layout.Width, layout.Height);

        float left = 130f;
        float centerY = layout.Height * 0.5f;

        for (int i = 0; i < layout.Edges.Count; i++)
        {
            CoreSpecializationLayoutEdge edge = layout.Edges[i];
            CoreSpecializationLayoutNode from = layout.Nodes[edge.FromNodeId];
            CoreSpecializationLayoutNode to = layout.Nodes[edge.ToNodeId];

            Vector2 a = new Vector2(left + from.Position.X, centerY - from.Position.Y);
            Vector2 b = new Vector2(left + to.Position.X, centerY - to.Position.Y);

            Color lineColor = edge.TargetRequirementKind == CoreRequirementKind.Any
                ? new Color(0.35f, 0.72f, 1f, 0.72f)
                : new Color(0.72f, 0.76f, 0.82f, 0.68f);

            DrawLine(graphContent, a, b, lineColor, 4f);
        }

        CoreSpecializationState state =
            CoreSpecializationRuntime.GetState(pilot, tree.Id);

        bool treeUnlocked = CoreSpecializationRuntime.IsTreeUnlocked(pilot, tree);

        IList<CoreSpecializationNode> nodes = tree.Nodes;
        for (int i = 0; i < nodes.Count; i++)
        {
            CoreSpecializationNode node = nodes[i];
            CoreSpecializationLayoutNode nodeLayout = layout.Nodes[node.Id];

            string reason;
            bool available = treeUnlocked &&
                CoreSpecializationRuntime.CanInvest(
                    pilot,
                    tree.Id,
                    node.Id,
                    out reason
                );
            bool invested = state != null && state.GetRank(node.Id) > 0;

            string label = node.Name + "\n" +
                (state == null ? "0" : state.GetRank(node.Id).ToString()) +
                "/" + node.MaxRank.ToString();

            if (node.AutoGranted)
                label += "\nGRANTED";

            Button button = CreateNodeButton(
                graphContent,
                label,
                new Vector2(
                    left + nodeLayout.Position.X,
                    centerY - nodeLayout.Position.Y
                ),
                node.Type,
                invested,
                available,
                treeUnlocked
            );

            string captured = node.Id;
            button.onClick.AddListener(delegate
            {
                selectedNodeId = captured;
                RefreshDetails(
                    CoreSpecializationRegistry.Get(selectedTreeId),
                    CoreSpecializationRuntime.GetCurrentPilot()
                );
            });

            nodeButtons[node.Id] = button;

            List<string> simpleParents;
            if ((node.Requirement.Kind == CoreRequirementKind.All ||
                 node.Requirement.Kind == CoreRequirementKind.Any) &&
                node.Requirement.TryGetSimpleParents(out simpleParents) &&
                simpleParents.Count > 1)
            {
                Text badge = CreateText(graphContent, "Gateway", 12, TextAnchor.MiddleCenter);
                badge.text = node.Requirement.Kind == CoreRequirementKind.All
                    ? "ALL"
                    : "ANY";
                badge.color = node.Requirement.Kind == CoreRequirementKind.All
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

        string[] renderedNames = tree.Nodes
            .Select(n => n.Name)
            .ToArray();

        titleText.text =
            tree.Name.ToUpperInvariant() +
            " SPECIALIZATION   [" +
            CoreSpecializationRuntime.DiagnosticBuildMarker +
            " | DEF " + definitionNodeCount.ToString() +
            " | LAYOUT " + layoutNodeCount.ToString() +
            " | BUTTONS " + nodeButtons.Count.ToString() +
            "]";

        Debug.Log(
            "[Leviathan SpecDiag] " +
            CoreSpecializationRuntime.DiagnosticBuildMarker +
            " tree=" + tree.Id +
            " def=" + definitionNodeCount.ToString() +
            " layout=" + layoutNodeCount.ToString() +
            " buttons=" + nodeButtons.Count.ToString() +
            " nodes=[" + string.Join(", ", renderedNames) + "]"
        );
    }

    private static CoreSpecializationLayout BuildEvolutionLayout(
        CoreSpecializationTree tree)
    {
        CoreSpecializationLayout layout =
            new CoreSpecializationLayout();

        const float childSpacing = 230f;
        const float rowSpacing = 220f;
        const float topY = 120f;

        List<CoreSpecializationNode> children =
            new List<CoreSpecializationNode>();

        IList<CoreSpecializationNode> nodes = tree.Nodes;
        CoreSpecializationNode rootNode = null;

        for (int i = 0; i < nodes.Count; i++)
        {
            if (string.Equals(nodes[i].Id, tree.RootNodeId, StringComparison.Ordinal))
                rootNode = nodes[i];
            else
                children.Add(nodes[i]);
        }

        float rowWidth = Math.Max(0f, (children.Count - 1) * childSpacing);
        float centerX = rowWidth * 0.5f;

        if (rootNode != null)
        {
            layout.Nodes[rootNode.Id] = new CoreSpecializationLayoutNode
            {
                NodeId = rootNode.Id,
                Layer = 0,
                Order = 0,
                Position = new CoreLayoutPoint(centerX, topY)
            };
        }

        for (int i = 0; i < children.Count; i++)
        {
            CoreSpecializationNode node = children[i];
            layout.Nodes[node.Id] = new CoreSpecializationLayoutNode
            {
                NodeId = node.Id,
                Layer = 1,
                Order = i,
                Position = new CoreLayoutPoint(i * childSpacing, topY - rowSpacing)
            };

            if (rootNode != null)
            {
                layout.Edges.Add(new CoreSpecializationLayoutEdge
                {
                    FromNodeId = rootNode.Id,
                    ToNodeId = node.Id,
                    TargetRequirementKind = node.Requirement.Kind
                });
            }
        }

        layout.Width = Math.Max(1200f, rowWidth + 420f);
        layout.Height = 620f;
        return layout;
    }

    private void RefreshDetails(CoreSpecializationTree tree, Pilot pilot)
    {
        if (tree == null)
            return;

        CoreSpecializationState state =
            CoreSpecializationRuntime.GetState(pilot, tree.Id);

        bool unlocked = CoreSpecializationRuntime.IsTreeUnlocked(pilot, tree);

        if (string.IsNullOrEmpty(selectedNodeId))
        {
            string layoutHelp = string.Equals(
                tree.Id,
                LeviathanEvolutionTree.TreeId,
                StringComparison.Ordinal
            )
                ? "Evolution uses a compact top-down layout; unlocked skill trees use prerequisite-driven automatic layout."
                : "The graph is generated from prerequisite relationships; node positions and lines are not hand-authored.";

            detailsText.text =
                tree.Name + "\n\n" +
                (unlocked
                    ? "Tree unlocked. Select a node."
                    : "LOCKED\n\nPurchase " + tree.Name + " in the Evolution tree to unlock this tree.") +
                "\n\n" + layoutHelp;
            spendButton.interactable = false;
            refundButton.interactable = false;
            return;
        }

        CoreSpecializationNode node = tree.GetNode(selectedNodeId);
        if (node == null)
        {
            selectedNodeId = null;
            RefreshDetails(tree, pilot);
            return;
        }

        int rank = state == null ? 0 : state.GetRank(node.Id);
        string text = node.Name + "\n";
        text += node.Type.ToString().ToUpperInvariant() +
            "   " + rank.ToString() + "/" + node.MaxRank.ToString() + "\n\n";

        if (!string.IsNullOrEmpty(node.Description))
            text += node.Description + "\n\n";

        if (node.AutoGranted)
        {
            text += "Cost: Granted automatically\n";
        }
        else
        {
            text += "Cost: " + node.PointCostPerRank.ToString() + " " +
                (node.PointCostPerRank == 1
                    ? "Evolution Point"
                    : "Evolution Points") +
                " per rank\n";
        }

        text += "Requires: " + node.Requirement.Describe(tree.GetNodeName) + "\n";

        if (!string.IsNullOrEmpty(node.ExclusiveGroup))
        {
            text += "Exclusive group: ";
            IList<CoreSpecializationNode> members =
                tree.GetExclusiveGroupMembers(node.ExclusiveGroup);

            for (int i = 0; i < members.Count; i++)
            {
                if (i > 0)
                    text += ", ";
                text += members[i].Name;
            }
            text += "\n";
        }

        if (node.Effects.Length > 0)
        {
            int displayRank = rank < node.MaxRank
                ? rank + 1
                : Math.Max(1, rank);

            text += "\nRank " + displayRank.ToString() + " adds:\n";
            for (int i = 0; i < node.Effects.Length; i++)
            {
                text += "• " + node.Effects[i].DescribeRankContribution(
                    displayRank,
                    CoreSpecializationRegistry.ResolveEffectName
                ) + "\n";
            }
        }

        if (!unlocked)
        {
            text += "\nUnlock " + tree.Name + " from Evolution first.";
        }

        detailsText.text = text;

        string reason;
        spendButton.interactable = CoreSpecializationRuntime.CanInvest(
            pilot,
            tree.Id,
            node.Id,
            out reason
        );
        refundButton.interactable = CoreSpecializationRuntime.CanRefund(
            pilot,
            tree.Id,
            node.Id,
            out reason
        );
    }

    private void SpendSelected()
    {
        Pilot pilot = CoreSpecializationRuntime.GetCurrentPilot();
        CoreSpecializationTree tree =
            CoreSpecializationRegistry.Get(selectedTreeId);

        if (tree == null || string.IsNullOrEmpty(selectedNodeId))
            return;

        CoreSpecializationNode node = tree.GetNode(selectedNodeId);
        string nodeName = node == null ? selectedNodeId : node.Name;
        int cost = node == null ? 1 : node.PointCostPerRank;

        string reason;
        bool success = CoreSpecializationRuntime.TryInvest(
            pilot,
            tree.Id,
            selectedNodeId,
            out reason
        );

        Refresh();

        statusText.text = success
            ? "Spent " + cost.ToString() + " Evolution Point" +
              (cost == 1 ? string.Empty : "s") + " on " + nodeName + "."
            : "Could not spend Evolution Point: " + reason;
    }

    private void RefundSelected()
    {
        Pilot pilot = CoreSpecializationRuntime.GetCurrentPilot();
        CoreSpecializationTree tree =
            CoreSpecializationRegistry.Get(selectedTreeId);

        if (tree == null || string.IsNullOrEmpty(selectedNodeId))
            return;

        CoreSpecializationNode node = tree.GetNode(selectedNodeId);
        string nodeName = node == null ? selectedNodeId : node.Name;
        int cost = node == null ? 1 : node.PointCostPerRank;

        string reason;
        bool success = CoreSpecializationRuntime.TryRefund(
            pilot,
            tree.Id,
            selectedNodeId,
            out reason
        );

        Refresh();

        statusText.text = success
            ? "Refunded " + cost.ToString() + " Evolution Point" +
              (cost == 1 ? string.Empty : "s") + " from " + nodeName + "."
            : "Could not refund Evolution Point: " + reason;
    }

    private Button CreateNodeButton(
        Transform parent,
        string label,
        Vector2 position,
        CoreSpecializationNodeType type,
        bool invested,
        bool available,
        bool treeUnlocked)
    {
        Vector2 size = type == CoreSpecializationNodeType.Root
            ? new Vector2(178f, 90f)
            : type == CoreSpecializationNodeType.Keystone
                ? new Vector2(170f, 82f)
                : type == CoreSpecializationNodeType.Major
                    ? new Vector2(150f, 70f)
                    : new Vector2(142f, 66f);

        Color color = !treeUnlocked
            ? new Color(0.07f, 0.08f, 0.10f, 1f)
            : invested
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

    private Button CreateTopButton(
        Transform parent,
        string label,
        Vector2 anchored,
        bool selected,
        bool unlocked)
    {
        Color color = selected
            ? new Color(0.17f, 0.42f, 0.64f, 1f)
            : unlocked
                ? new Color(0.12f, 0.22f, 0.32f, 1f)
                : new Color(0.08f, 0.09f, 0.12f, 1f);

        Image image = CreateImage(parent, "TreeTab_" + label, color);
        RectTransform rect = image.rectTransform;
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = anchored;
        rect.sizeDelta = new Vector2(180f, 46f);

        Button button = image.gameObject.AddComponent<Button>();
        Text text = CreateText(image.transform, "Label", 13, TextAnchor.MiddleCenter);
        Stretch(text.rectTransform, 4f, 4f, 4f, 4f);
        text.text = label;
        return button;
    }

    private Button CreateButton(Transform parent, string label, Vector2 anchored)
    {
        Image image = CreateImage(
            parent,
            "Button_" + label,
            new Color(0.14f, 0.28f, 0.40f, 1f)
        );
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
