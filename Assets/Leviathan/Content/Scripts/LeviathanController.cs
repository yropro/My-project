using HarmonyLib;
using StarVortex;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

/// <summary>
/// Local Leviathan physical builder/runtime coordinator.
///
/// Growth owns anatomy intent, live role classification and public anatomy
/// queries. The controller owns construction and the private attachment graph.
/// It publishes one atomic live anatomy snapshot after a successful build and
/// invalidates that snapshot before any topology is torn down or replaced.
/// </summary>
public class LeviathanController : MonoBehaviour
{
    private const int MaxFollowerSections = 64;

    private static readonly FieldInfo AttachedShipField =
        AccessTools.Field(typeof(AttachedAIShip), "attachedShip");

    private static readonly FieldInfo AttachedTimeField =
        AccessTools.Field(typeof(AttachedAIShip), "attachedTime");

    private static readonly FieldInfo InitialAttachField =
        AccessTools.Field(typeof(AttachedAIShip), "initialAttach");

    private static readonly FieldInfo HasLastThrusterField =
        AccessTools.Field(typeof(AttachedAIShip), "hasLastThruster");

    private static readonly FieldInfo SegmentLengthField =
        AccessTools.Field(typeof(AttachedAIShip), "segmentLength");

    private struct BuildSignature
    {
        public int HeadCount;
        public int BodySegmentCount;
        public int TailCount;
        public int TotalSectionCount;
        public bool UsesBifurcationTemplates;

        public bool Matches(BuildSignature other)
        {
            return HeadCount == other.HeadCount &&
                BodySegmentCount == other.BodySegmentCount &&
                TailCount == other.TailCount &&
                TotalSectionCount == other.TotalSectionCount &&
                UsesBifurcationTemplates == other.UsesBifurcationTemplates;
        }
    }

    /// <summary>
    /// One build-time connection plan. Roles remain Growth-owned; this only
    /// decides which Body sections form the shared trunk versus rear branches.
    /// BranchBodyCounts has one entry per terminal Tail.
    /// </summary>
    private sealed class TopologyPlan
    {
        public int SharedBodyCount;
        public int[] BranchBodyCounts;
    }

    // Builder-owned runtime collections. These are construction state only;
    // public anatomy ownership/counts live exclusively in LeviathanGrowth.
    private readonly List<GameShip> liveBodies =
        new List<GameShip>();
    private readonly List<GameShip> liveTails =
        new List<GameShip>();
    private readonly Dictionary<GameShip, GameShip> attachmentParentBySection =
        new Dictionary<GameShip, GameShip>();
    private readonly HashSet<GameShip> customTemplateSections =
        new HashSet<GameShip>();

    // Temporary build-slot metadata. Slots do not become anatomy authority.
    private readonly List<bool> customTemplateSlots =
        new List<bool>();

    private GameShip currentPlayer;
    private GameShip builtForPlayer;
    private BuildSignature builtSignature;
    private Squadron activeLeviathanSquadron;
    private Coroutine growthRefreshCoroutine;

    private GameShip massAdjustedPlayer;
    private Rigidbody2D massAdjustedBody;
    private float originalPlayerMass;
    private bool hasOriginalPlayerMass;

    public void SetPlayerShip(GameShip player)
    {
        if (player == null)
        {
            ClearPlayerShip();
            return;
        }

        if (ReferenceEquals(player, builtForPlayer))
        {
            RequestGrowthRefreshIfNeeded(player);
            return;
        }

        CancelPendingGrowthRefresh();
        LeviathanPredatorRuntime.Cancel();
        LeviathanConstrictor.Reset();

        if (builtForPlayer != null || activeLeviathanSquadron != null)
            TearDownCurrentBuild(true);
        else
            ResetBuildState();

        currentPlayer = player;

        Pilot pilot = GameShip.GetPlayerSourcePilot(player);
        if (pilot == null)
        {
            Debug.LogWarning(
                "[Leviathan] Player ship has no player Pilot."
            );
            return;
        }

        LeviathanGrowth.AnatomyIntent intent =
            LeviathanGrowth.GetAnatomyIntent(player);

        if (!intent.Active)
        {
            Debug.Log(
                "[Leviathan] Evolution rank is 0; Leviathan inactive."
            );
            return;
        }

        if (!ValidateBuildIntent(intent))
            return;

        // This pre-build resolve is used only for non-anatomy flags such as the
        // current Bifurcation template family. Live-count-derived stats are
        // resolved again after PublishLiveAnatomy succeeds.
        LeviathanGrowth.ResolvedState preBuildGrowth =
            LeviathanGrowth.GetResolvedState(player);

        bool usesBifurcationTemplates =
            preBuildGrowth != null && preBuildGrowth.Bifurcation;

        BuildSignature signature = CreateBuildSignature(
            intent,
            usesBifurcationTemplates
        );

        Debug.Log(
            "[Leviathan] Building anatomy: heads=" + intent.HeadCount +
            ", bodies=" + intent.BodySegmentCount +
            ", tails=" + intent.TailCount +
            ", sections=" + intent.TotalSectionCount + "."
        );

        if (!TryCreateLeviathan(
                player,
                intent,
                usesBifurcationTemplates))
        {
            TearDownCurrentBuild(true);
            currentPlayer = player;
            return;
        }

        LeviathanGrowth.ResolvedState growth =
            LeviathanGrowth.GetResolvedState(player);

        if (growth == null || !growth.Active)
        {
            Debug.LogError(
                "[Leviathan] Growth became inactive after anatomy publication."
            );
            TearDownCurrentBuild(true);
            currentPlayer = player;
            return;
        }

        ApplyPlayerMass(player, growth);

        builtForPlayer = player;
        builtSignature = signature;

        LeviathanGrowth.AnatomySnapshot anatomy =
            LeviathanGrowth.GetAnatomy(player);

        Debug.Log(
            "[Leviathan] Build complete. Live heads=" + anatomy.HeadCount +
            ", bodies=" + anatomy.BodySegmentCount +
            ", tails=" + anatomy.TailCount +
            ", scaling segments=" + anatomy.ScalingSegmentCount +
            ", mass multiplier=" + growth.MassMultiplier.ToString("0.00") +
            ", air resistance strength=" +
            growth.AirResistanceStrength.ToString("0.000") + "."
        );
    }

    public void RequestGrowthRefreshIfNeeded(GameShip player)
    {
        if (player == null || !IsCurrentPlayerShip(player))
            return;

        LeviathanGrowth.AnatomyIntent intent =
            LeviathanGrowth.GetAnatomyIntent(player);

        if (!intent.Active)
        {
            if (ReferenceEquals(player, builtForPlayer))
                RequestGrowthRefresh(player);
            return;
        }

        LeviathanGrowth.ResolvedState growth =
            LeviathanGrowth.GetResolvedState(player);

        bool usesBifurcationTemplates =
            growth != null && growth.Bifurcation;

        BuildSignature desired = CreateBuildSignature(
            intent,
            usesBifurcationTemplates
        );

        LeviathanGrowth.AnatomySnapshot anatomy =
            LeviathanGrowth.GetAnatomy(player);

        if (!ReferenceEquals(player, builtForPlayer) ||
            !builtSignature.Matches(desired) ||
            anatomy == null ||
            !anatomy.PublishedByBuilder)
        {
            RequestGrowthRefresh(player);
            return;
        }

        // Stat-only Growth changes do not reconstruct physical anatomy. Mass is
        // a stored Rigidbody value rather than a native getter, so refresh it.
        if (growth != null && growth.Active)
            ApplyPlayerMass(player, growth);
    }

    /// <summary>
    /// Rebuild the local Leviathan after Growth changes. Multiple changes in
    /// quick succession coalesce into one final reconstruction.
    /// </summary>
    public void RequestGrowthRefresh(GameShip player)
    {
        if (player == null || !IsCurrentPlayerShip(player))
            return;

        CancelPendingGrowthRefresh();
        growthRefreshCoroutine = StartCoroutine(
            RefreshGrowthRoutine(player)
        );
    }

    private IEnumerator RefreshGrowthRoutine(GameShip player)
    {
        // Let the native specialization transaction finish and coalesce rapid
        // edits before touching physical topology.
        yield return null;

        if (!IsCurrentPlayerShip(player))
        {
            growthRefreshCoroutine = null;
            yield break;
        }

        LeviathanPredatorRuntime.CancelForPlayer(player);
        LeviathanConstrictor.Reset();
        TearDownCurrentBuild(true);
        currentPlayer = player;

        // Unity Destroy is deferred; wait one frame before constructing the new
        // follower set so stale objects cannot participate in the new graph.
        yield return null;

        if (!IsCurrentPlayerShip(player))
        {
            growthRefreshCoroutine = null;
            yield break;
        }

        growthRefreshCoroutine = null;
        SetPlayerShip(player);
    }

    private static BuildSignature CreateBuildSignature(
        LeviathanGrowth.AnatomyIntent intent,
        bool usesBifurcationTemplates)
    {
        BuildSignature signature = new BuildSignature();
        signature.HeadCount = intent.HeadCount;
        signature.BodySegmentCount = intent.BodySegmentCount;
        signature.TailCount = intent.TailCount;
        signature.TotalSectionCount = intent.TotalSectionCount;
        signature.UsesBifurcationTemplates = usesBifurcationTemplates;
        return signature;
    }

    private static bool ValidateBuildIntent(
        LeviathanGrowth.AnatomyIntent intent)
    {
        if (!intent.TopologyFitsBudget)
        {
            Debug.LogError(
                "[Leviathan] Refusing to build invalid Growth anatomy intent."
            );
            return false;
        }

        // The canonical anatomy system already supports arbitrary Head counts,
        // but the physical builder currently has no verified forward-branch
        // attachment solver for additional Heads. Fail explicitly instead of
        // constructing a follower and falsely publishing it as a forward Head.
        if (intent.HeadCount != 1)
        {
            Debug.LogError(
                "[Leviathan] The current physical builder supports exactly one " +
                "Head. Growth requested " + intent.HeadCount +
                ". Add a verified multi-Head topology builder before enabling " +
                "this morphology."
            );
            return false;
        }

        if (intent.TailCount < 1)
        {
            Debug.LogError(
                "[Leviathan] The current physical builder requires at least one Tail."
            );
            return false;
        }

        int followerCount = intent.TotalSectionCount - intent.HeadCount;
        if (followerCount < 1 || followerCount > MaxFollowerSections)
        {
            Debug.LogError(
                "[Leviathan] Requested follower count " + followerCount +
                " is outside the supported build range 1-" +
                MaxFollowerSections + "."
            );
            return false;
        }

        return true;
    }

    private static bool IsCurrentPlayerShip(GameShip player)
    {
        return player != null &&
            WorldController.instance != null &&
            ReferenceEquals(
                WorldController.instance.GetCurrentPlayerShip(),
                player
            );
    }

    private void FixedUpdate()
    {
        LeviathanConstrictor.Tick(currentPlayer);
        LeviathanPredatorRuntime.FixedTick();
        LeviathanGrowth.TickOwnerResources(currentPlayer);
        ApplyHighSpeedResistance();
    }

    public void ClearPlayerShip()
    {
        LeviathanPredatorRuntime.Cancel();
        LeviathanConstrictor.Reset();
        CancelPendingGrowthRefresh();
        TearDownCurrentBuild(true);
        currentPlayer = null;
    }

    private void CancelPendingGrowthRefresh()
    {
        if (growthRefreshCoroutine == null)
            return;

        StopCoroutine(growthRefreshCoroutine);
        growthRefreshCoroutine = null;
    }

    /// <summary>
    /// Private build-graph query used by attachment normalization. This is not an
    /// anatomy-count API; role/membership consumers must query LeviathanGrowth.
    /// </summary>
    internal bool TryGetAttachmentParent(
        GameShip section,
        out GameShip parent)
    {
        parent = null;
        return section != null &&
            attachmentParentBySection.TryGetValue(section, out parent) &&
            parent != null;
    }

    internal bool ShouldNormalizeAttachment(GameShip ship)
    {
        if (ship == null || activeLeviathanSquadron == null)
            return false;

        if (ship.squadron != activeLeviathanSquadron)
            return false;

        GameShip parent;
        if (!attachmentParentBySection.TryGetValue(ship, out parent))
            return false;

        return customTemplateSections.Contains(ship) ||
            (parent != null && customTemplateSections.Contains(parent));
    }

    private bool TryCreateLeviathan(
        GameShip player,
        LeviathanGrowth.AnatomyIntent intent,
        bool usesBifurcationTemplates)
    {
        SquadronBase leviathanBase = FindLeviathanBase();
        if (leviathanBase == null)
        {
            Debug.LogError(
                "[Leviathan] Could not find SquadronBase 'LeviathanTest'."
            );
            return false;
        }

        Squadron squadron = leviathanBase.GetSquadron(1);
        if (squadron == null)
        {
            Debug.LogError(
                "[Leviathan] GetSquadron(1) returned null."
            );
            return false;
        }

        List<Squadron.SquadronShip> ships = squadron.ships;
        if (!PrepareSquadronForAnatomy(ships, intent))
            return false;

        if (usesBifurcationTemplates)
            LeviathanSegmentTemplates.EnsureBifurcationDefaults();

        Dictionary<string, string> templates =
            LeviathanSegmentTemplates.LoadSerializedBodies();

        TopologyPlan topology = CreateTopologyPlan(
            intent,
            usesBifurcationTemplates,
            templates
        );

        ApplySegmentTemplates(
            ships,
            intent,
            usesBifurcationTemplates,
            topology,
            templates
        );

        Squadron.SquadronShip headSlot = ships[0];
        headSlot.ship = player;
        headSlot.spawned = true;
        headSlot.temporary = false;

        activeLeviathanSquadron = squadron;
        LeviathanAttachmentNormalizer.Reset();
        player.SetSquadron(squadron);

        squadron.InvalidateShipCaches();
        squadron.Build(true);

        ClearRuntimeBuildCollections();

        int bodyStartIndex = 1;
        int tailStartIndex = bodyStartIndex + intent.BodySegmentCount;

        // Validate the entire spawned follower set before publishing anything.
        for (int i = 1; i < ships.Count; i++)
        {
            Squadron.SquadronShip slot = ships[i];
            GameShip section = slot == null ? null : slot.ship;

            if (section == null)
            {
                Debug.LogError(
                    "[Leviathan] Section failed to spawn at slot " + i + "."
                );
                return false;
            }
        }

        for (int i = 1; i < ships.Count; i++)
        {
            GameShip section = ships[i].ship;
            ConfigureFollowerForPlayer(section, player);

            if (i < customTemplateSlots.Count && customTemplateSlots[i])
                customTemplateSections.Add(section);

            if (i < tailStartIndex)
                liveBodies.Add(section);
            else
                liveTails.Add(section);
        }

        if (!BuildAttachmentGraph(player, topology))
        {
            Debug.LogError(
                "[Leviathan] Could not build the requested attachment graph."
            );
            return false;
        }

        if (!ApplyNativeAttachmentGraph())
        {
            Debug.LogError(
                "[Leviathan] Could not apply the requested attachment topology."
            );
            return false;
        }

        if (!LeviathanGrowth.PublishLiveAnatomy(
                player,
                null,
                liveBodies,
                liveTails))
        {
            Debug.LogError(
                "[Leviathan] Growth rejected the completed live anatomy."
            );
            return false;
        }

        LeviathanGrowth.AnatomySnapshot anatomy =
            LeviathanGrowth.GetAnatomy(player);

        if (anatomy.HeadCount != intent.HeadCount ||
            anatomy.BodySegmentCount != intent.BodySegmentCount ||
            anatomy.TailCount != intent.TailCount ||
            anatomy.TotalSectionCount != intent.TotalSectionCount)
        {
            Debug.LogError(
                "[Leviathan] Published live anatomy does not match the completed " +
                "build intent. Intended H/B/T=" + intent.HeadCount + "/" +
                intent.BodySegmentCount + "/" + intent.TailCount +
                ", live=" + anatomy.HeadCount + "/" +
                anatomy.BodySegmentCount + "/" + anatomy.TailCount + "."
            );
            LeviathanGrowth.InvalidateLiveAnatomy(player);
            return false;
        }

        return true;
    }

    private static SquadronBase FindLeviathanBase()
    {
        SquadronBase[] bases =
            Resources.FindObjectsOfTypeAll<SquadronBase>();

        for (int i = 0; i < bases.Length; i++)
        {
            SquadronBase candidate = bases[i];
            if (candidate != null && candidate.name == "LeviathanTest")
                return candidate;
        }

        return null;
    }

    private bool PrepareSquadronForAnatomy(
        List<Squadron.SquadronShip> ships,
        LeviathanGrowth.AnatomyIntent intent)
    {
        if (ships == null)
            return false;

        const int authoredBodyCount =
            LeviathanGrowth.Tuning.AuthoredSeedBodySlots;
        const int expectedShipCount =
            1 + authoredBodyCount + 1;

        if (ships.Count != expectedShipCount)
        {
            Debug.LogError(
                "[Leviathan] LeviathanTest must contain exactly " +
                expectedShipCount +
                " authored ships (head + 15 body + tail). Found " +
                ships.Count + "."
            );
            return false;
        }

        Squadron.SquadronShip headSlot = ships[0];
        Squadron.SquadronShip tailPrototype = ships[ships.Count - 1];

        List<Squadron.SquadronShip> authoredBodies =
            new List<Squadron.SquadronShip>(authoredBodyCount);

        for (int i = 0; i < authoredBodyCount; i++)
            authoredBodies.Add(ships[i + 1]);

        Squadron.SquadronShip bodyPrototype =
            authoredBodies[authoredBodies.Count - 1];

        ships.Clear();
        ships.Add(headSlot);

        for (int bodyIndex = 0;
            bodyIndex < intent.BodySegmentCount;
            bodyIndex++)
        {
            Squadron.SquadronShip bodySlot;

            if (bodyIndex < authoredBodies.Count)
            {
                bodySlot = authoredBodies[bodyIndex];
            }
            else
            {
                bodySlot = CloneSlotDefinition(bodyPrototype);
                if (bodySlot == null)
                {
                    Debug.LogError(
                        "[Leviathan] Could not clone body slot " +
                        (bodyIndex + 1) + "."
                    );
                    return false;
                }
            }

            ships.Add(bodySlot);
        }

        for (int tailIndex = 0;
            tailIndex < intent.TailCount;
            tailIndex++)
        {
            Squadron.SquadronShip tailSlot =
                tailIndex == 0
                    ? tailPrototype
                    : CloneSlotDefinition(tailPrototype);

            if (tailSlot == null)
            {
                Debug.LogError(
                    "[Leviathan] Could not clone tail slot " +
                    (tailIndex + 1) + "."
                );
                return false;
            }

            ships.Add(tailSlot);
        }

        int expectedResolvedCount =
            1 + intent.BodySegmentCount + intent.TailCount;

        if (ships.Count != expectedResolvedCount)
        {
            Debug.LogError(
                "[Leviathan] Internal anatomy slot build mismatch. Expected " +
                expectedResolvedCount + ", built " + ships.Count + "."
            );
            return false;
        }

        return true;
    }

    private static Squadron.SquadronShip CloneSlotDefinition(
        Squadron.SquadronShip source)
    {
        if (source == null)
            return null;

        Squadron.SquadronShip result =
            source.Clone() as Squadron.SquadronShip;
        if (result == null)
            return null;

        // SquadronShip.Clone intentionally shares its NPC reference. Every
        // runtime-added slot needs an independent Ship definition so template
        // overrides cannot mutate a sibling slot through shared NPC state.
        NPC sourceNpc = source.npc as NPC;
        if (sourceNpc == null)
            return null;

        NPCBase npcBase = sourceNpc.GetNPCBase();
        if (npcBase == null)
            return null;

        int level = 1;
        if (sourceNpc.ship != null && sourceNpc.ship.pilot != null)
        {
            level = Mathf.Max(
                1,
                sourceNpc.ship.pilot.GetLevel(0)
            );
        }

        result.npc = npcBase.GetNPC(level);
        if (result.npc == null)
            return null;

        result.ship = null;
        result.spawned = false;
        result.temporary = true;
        return result;
    }

    private static TopologyPlan CreateTopologyPlan(
        LeviathanGrowth.AnatomyIntent intent,
        bool usesBifurcationTemplates,
        Dictionary<string, string> templates)
    {
        TopologyPlan plan = new TopologyPlan();
        int tailCount = Mathf.Max(1, intent.TailCount);
        plan.BranchBodyCounts = new int[tailCount];
        plan.SharedBodyCount = intent.BodySegmentCount;

        // BifurcateN names the final shared Body before rear branches diverge.
        // No BifurcateN means all Bodies remain in the shared trunk and the
        // terminal Tails fan directly from its end.
        if (usesBifurcationTemplates &&
            intent.TailCount > 1 &&
            intent.BodySegmentCount > 0 &&
            templates != null)
        {
            int selected = 0;

            for (int i = 1; i <= 64; i++)
            {
                string serialized;
                if (!templates.TryGetValue(
                        "Bifurcate" + i,
                        out serialized) ||
                    string.IsNullOrEmpty(serialized))
                {
                    continue;
                }

                if (i <= intent.BodySegmentCount)
                {
                    if (selected == 0)
                        selected = i;
                    else
                    {
                        Debug.LogWarning(
                            "[Leviathan] Multiple BifurcateN templates exist; " +
                            "using Bifurcate" + selected + "."
                        );
                        break;
                    }
                }
            }

            if (selected > 0)
                plan.SharedBodyCount = selected;
        }

        int branchBodies = Mathf.Max(
            0,
            intent.BodySegmentCount - plan.SharedBodyCount
        );

        if (plan.BranchBodyCounts.Length > 0 && branchBodies > 0)
        {
            int even = branchBodies / plan.BranchBodyCounts.Length;
            int remainder = branchBodies % plan.BranchBodyCounts.Length;

            for (int i = 0; i < plan.BranchBodyCounts.Length; i++)
            {
                plan.BranchBodyCounts[i] =
                    even + (i < remainder ? 1 : 0);
            }
        }

        return plan;
    }

    private void ApplySegmentTemplates(
        List<Squadron.SquadronShip> ships,
        LeviathanGrowth.AnatomyIntent intent,
        bool usesBifurcationTemplates,
        TopologyPlan topology,
        Dictionary<string, string> templates)
    {
        ResizeCustomTemplateSlots(ships == null ? 0 : ships.Count);

        if (ships == null || ships.Count < 2 || topology == null)
            return;

        if (templates == null)
        {
            templates = new Dictionary<string, string>(
                System.StringComparer.OrdinalIgnoreCase
            );
        }

        string genericBody;
        templates.TryGetValue("Body", out genericBody);

        // Shared trunk Bodies retain the historical numbered Body overrides.
        for (int bodyOrdinal = 1;
            bodyOrdinal <= topology.SharedBodyCount;
            bodyOrdinal++)
        {
            string serialized = null;

            if (usesBifurcationTemplates &&
                intent.TailCount > 1 &&
                bodyOrdinal == topology.SharedBodyCount)
            {
                templates.TryGetValue(
                    "Bifurcate" + bodyOrdinal,
                    out serialized
                );
            }

            if (string.IsNullOrEmpty(serialized))
            {
                templates.TryGetValue(
                    bodyOrdinal.ToString(),
                    out serialized
                );
            }

            if (string.IsNullOrEmpty(serialized))
                serialized = genericBody;

            ApplySerializedTemplate(
                ships,
                bodyOrdinal,
                serialized
            );
        }

        // Branch Body sections are still anatomically Body. Tail_aN/Tail_bN
        // describe branch position, not Tail role; only the terminal section in
        // each branch is classified as Tail by Growth.
        int bodyCursor = topology.SharedBodyCount;

        for (int branch = 0;
            branch < topology.BranchBodyCounts.Length;
            branch++)
        {
            int branchBodyCount = topology.BranchBodyCounts[branch];
            string branchPrefix = GetBranchTemplatePrefix(
                branch,
                usesBifurcationTemplates
            );

            for (int position = 1;
                position <= branchBodyCount;
                position++)
            {
                bodyCursor++;
                string serialized = ResolveBranchTemplate(
                    templates,
                    branchPrefix,
                    position,
                    false,
                    bodyCursor,
                    genericBody,
                    null
                );

                ApplySerializedTemplate(
                    ships,
                    bodyCursor,
                    serialized
                );
            }
        }

        int tailStart = 1 + intent.BodySegmentCount;
        string genericTail;
        templates.TryGetValue("Tail", out genericTail);

        for (int branch = 0; branch < intent.TailCount; branch++)
        {
            int branchBodyCount =
                branch < topology.BranchBodyCounts.Length
                    ? topology.BranchBodyCounts[branch]
                    : 0;

            string branchPrefix = GetBranchTemplatePrefix(
                branch,
                usesBifurcationTemplates
            );

            int terminalPosition = branchBodyCount + 1;
            string serialized = ResolveBranchTemplate(
                templates,
                branchPrefix,
                terminalPosition,
                true,
                0,
                null,
                genericTail
            );

            ApplySerializedTemplate(
                ships,
                tailStart + branch,
                serialized
            );
        }
    }

    private static string GetBranchTemplatePrefix(
        int branch,
        bool usesBifurcationTemplates)
    {
        if (!usesBifurcationTemplates)
            return null;

        if (branch == 0)
            return "Tail_a";

        if (branch == 1)
            return "Tail_b";

        // Current saved-template grammar intentionally defines only a/b.
        // Additional future Tail branches remain mechanically valid and use
        // ordinary Body/Tail fallbacks until their UI naming contract is added.
        return null;
    }

    private static string ResolveBranchTemplate(
        Dictionary<string, string> templates,
        string branchPrefix,
        int branchPosition,
        bool terminalTail,
        int globalBodyOrdinal,
        string genericBody,
        string genericTail)
    {
        string serialized = null;

        if (!string.IsNullOrEmpty(branchPrefix))
        {
            templates.TryGetValue(
                branchPrefix + branchPosition,
                out serialized
            );

            if (string.IsNullOrEmpty(serialized))
                templates.TryGetValue(branchPrefix, out serialized);
        }

        if (!terminalTail &&
            string.IsNullOrEmpty(serialized) &&
            globalBodyOrdinal > 0)
        {
            templates.TryGetValue(
                globalBodyOrdinal.ToString(),
                out serialized
            );
        }

        if (string.IsNullOrEmpty(serialized))
            serialized = terminalTail ? genericTail : genericBody;

        return serialized;
    }

    private void ApplySerializedTemplate(
        List<Squadron.SquadronShip> ships,
        int slotIndex,
        string serialized)
    {
        if (ships == null ||
            slotIndex < 0 ||
            slotIndex >= ships.Count ||
            string.IsNullOrEmpty(serialized) ||
            ships[slotIndex] == null ||
            ships[slotIndex].npc == null)
        {
            return;
        }

        Ship definition = ships[slotIndex].npc.GetShip();
        if (definition == null)
            return;

        definition.serializedBody = serialized;
        customTemplateSlots[slotIndex] = true;
    }

    private static void ConfigureFollowerForPlayer(
        GameShip section,
        GameShip player)
    {
        section.faction = player.faction;
        section.disableMinibars = true;

        if (section.originalShip != null)
            section.originalShip.disableMinibars = true;

        section.CheckAttachMinibars();

        // Squadron.SpawnShip creates star-owned entities. Leviathan followers
        // belong to the local player and use the game's native player-entity
        // replication path so every peer receives their complete Ship JSON and
        // transform stream.
        if (NetSession.InSession)
        {
            section.netStarEntity = false;
            section.netPlayerEntity = true;
        }
    }

    private bool BuildAttachmentGraph(
        GameShip primaryHead,
        TopologyPlan topology)
    {
        attachmentParentBySection.Clear();

        if (primaryHead == null ||
            topology == null ||
            topology.BranchBodyCounts == null ||
            topology.BranchBodyCounts.Length != liveTails.Count ||
            topology.SharedBodyCount < 0 ||
            topology.SharedBodyCount > liveBodies.Count)
        {
            return false;
        }

        int bodyCursor = 0;
        GameShip parent = primaryHead;

        for (int i = 0; i < topology.SharedBodyCount; i++)
        {
            GameShip body = liveBodies[bodyCursor++];
            if (body == null)
                return false;

            attachmentParentBySection[body] = parent;
            parent = body;
        }

        GameShip fork = parent;

        for (int branch = 0;
            branch < topology.BranchBodyCounts.Length;
            branch++)
        {
            GameShip branchParent = fork;
            int branchBodyCount = topology.BranchBodyCounts[branch];

            for (int i = 0; i < branchBodyCount; i++)
            {
                if (bodyCursor >= liveBodies.Count)
                    return false;

                GameShip body = liveBodies[bodyCursor++];
                if (body == null)
                    return false;

                attachmentParentBySection[body] = branchParent;
                branchParent = body;
            }

            GameShip tail = liveTails[branch];
            if (tail == null)
                return false;

            attachmentParentBySection[tail] = branchParent;
        }

        return bodyCursor == liveBodies.Count &&
            attachmentParentBySection.Count ==
                liveBodies.Count + liveTails.Count;
    }

    private bool ApplyNativeAttachmentGraph()
    {
        if (attachmentParentBySection.Count == 0)
            return true;

        if (AIController.instance == null ||
            AttachedShipField == null ||
            AttachedTimeField == null ||
            InitialAttachField == null ||
            HasLastThrusterField == null ||
            SegmentLengthField == null)
        {
            Debug.LogError(
                "[Leviathan] Could not resolve the verified native " +
                "AttachedAIShip attachment-state fields."
            );
            return false;
        }

        foreach (KeyValuePair<GameShip, GameShip> pair in
            attachmentParentBySection)
        {
            GameShip section = pair.Key;
            GameShip parent = pair.Value;

            if (section == null || parent == null)
                return false;

            AttachedAIShip attached =
                AIController.instance.GetAIShip(section) as AttachedAIShip;

            if (attached == null)
                return false;

            // Reparent as a fresh native attachment. These are the same
            // parent-dependent caches native resets when it discovers a new
            // predecessor: no stale rear point/segment length may survive from
            // the temporary flat Squadron order built earlier this frame.
            AttachedShipField.SetValue(attached, parent);

            AttachedTimeField.SetValue(attached, 0f);
            InitialAttachField.SetValue(attached, true);
            HasLastThrusterField.SetValue(attached, false);
            SegmentLengthField.SetValue(attached, -1f);
        }

        return true;
    }

    private void TearDownCurrentBuild(bool destroyFollowers)
    {
        GameShip owner = builtForPlayer != null
            ? builtForPlayer
            : currentPlayer;

        if (owner != null)
            LeviathanGrowth.InvalidateLiveAnatomy(owner);

        RestorePlayerMass();

        Squadron oldSquadron = activeLeviathanSquadron;

        if (owner != null &&
            oldSquadron != null &&
            ReferenceEquals(owner.squadron, oldSquadron))
        {
            owner.SetSquadron(null);
        }

        List<Squadron.SquadronShip> oldSlots =
            oldSquadron == null ? null : oldSquadron.ships;

        if (oldSlots != null)
        {
            for (int i = 0; i < oldSlots.Count; i++)
            {
                Squadron.SquadronShip slot = oldSlots[i];
                GameShip section = slot == null ? null : slot.ship;

                if (section == null || ReferenceEquals(section, owner))
                    continue;

                if (ReferenceEquals(section.squadron, oldSquadron))
                    section.SetSquadron(null);

                if (!destroyFollowers || section.gameObject == null)
                    continue;

                section.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(section.gameObject);
            }
        }

        ResetBuildState();
    }

    private void ResetBuildState()
    {
        builtForPlayer = null;
        builtSignature = default(BuildSignature);
        activeLeviathanSquadron = null;
        ClearRuntimeBuildCollections();
        ClearCustomTemplateSlots();
        LeviathanAttachmentNormalizer.Reset();
    }

    private void ClearRuntimeBuildCollections()
    {
        liveBodies.Clear();
        liveTails.Clear();
        attachmentParentBySection.Clear();
        customTemplateSections.Clear();
    }

    private void ApplyPlayerMass(
        GameShip player,
        LeviathanGrowth.ResolvedState growth)
    {
        if (player == null || growth == null || !growth.Active)
            return;

        Rigidbody2D body = player.GetRigidBody();
        if (body == null)
        {
            Debug.LogWarning(
                "[Leviathan] Player has no Rigidbody2D; mass unchanged."
            );
            return;
        }

        if (!ReferenceEquals(massAdjustedPlayer, player) ||
            !ReferenceEquals(massAdjustedBody, body) ||
            !hasOriginalPlayerMass)
        {
            RestorePlayerMass();

            massAdjustedPlayer = player;
            massAdjustedBody = body;
            originalPlayerMass = body.mass;
            hasOriginalPlayerMass = true;
        }

        body.mass =
            originalPlayerMass * Mathf.Max(0.01f, growth.MassMultiplier);
    }

    private void RestorePlayerMass()
    {
        if (hasOriginalPlayerMass && massAdjustedBody != null)
            massAdjustedBody.mass = originalPlayerMass;

        massAdjustedPlayer = null;
        massAdjustedBody = null;
        originalPlayerMass = 0f;
        hasOriginalPlayerMass = false;
    }

    private void ApplyHighSpeedResistance()
    {
        if (currentPlayer == null ||
            !ReferenceEquals(builtForPlayer, currentPlayer))
        {
            return;
        }

        LeviathanGrowth.ResolvedState growth =
            LeviathanGrowth.GetResolvedState(currentPlayer);

        if (growth == null ||
            !growth.Active ||
            growth.AirResistanceStrength <= 0f)
        {
            return;
        }

        // Native weapon lunges should not be damped by Leviathan cruising
        // resistance. Predator explicitly uses GameShip.Lunge.
        if (LeviathanPredatorRuntime.IsPredatorLunging(currentPlayer))
            return;

        Rigidbody2D body = currentPlayer.GetRigidBody();
        if (body == null)
            return;

        float maxSpeed = currentPlayer.MaxSpeed;
        if (maxSpeed <= 0.001f)
            return;

        Vector2 velocity = body.velocity;
        float speed = velocity.magnitude;
        float resistanceStart =
            maxSpeed * LeviathanGrowth.Tuning.AirResistanceStartFraction;

        if (speed <= resistanceStart)
            return;

        float t = Mathf.InverseLerp(
            resistanceStart,
            maxSpeed,
            speed
        );

        float resistanceFactor = t * t;
        float decelerationPerSecond =
            maxSpeed * growth.AirResistanceStrength * resistanceFactor;

        body.velocity = Vector2.MoveTowards(
            velocity,
            Vector2.zero,
            decelerationPerSecond * Time.fixedDeltaTime
        );
    }

    private void ResizeCustomTemplateSlots(int count)
    {
        customTemplateSlots.Clear();

        for (int i = 0; i < count; i++)
            customTemplateSlots.Add(false);
    }

    private void ClearCustomTemplateSlots()
    {
        customTemplateSlots.Clear();
    }
}
