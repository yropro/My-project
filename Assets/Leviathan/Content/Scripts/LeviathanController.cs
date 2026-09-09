using System.Collections;
using System.Collections.Generic;
using System.Linq;
using StarVortex;
using UnityEngine;

public class LeviathanController : MonoBehaviour
{
    // Growth balance knobs live in LeviathanGrowth.cs.

    private readonly HashSet<GameShip> segments =
        new HashSet<GameShip>();

    private readonly List<bool> customTemplateSlots =
        new List<bool>();

    private GameShip currentPlayer;
    private GameShip builtForPlayer;
    private int builtForNonHeadSegments;
    private bool builtForBifurcation;
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

        // Preserve the existing controller's "already built" fast path,
        // but rebuild when the resolved Growth anatomy changes. Ordinary stat
        // node changes do not reconstruct the physical chain.
        if (player == builtForPlayer)
        {
            RequestGrowthRefreshIfNeeded(player);
            return;
        }

        // From this point onward, retain the original SetPlayerShip ordering:
        // a different player ship always clears the old Leviathan state before
        // we attempt to resolve the new Pilot.
        LeviathanPredatorRuntime.Cancel();
        LeviathanConstrictor.Reset();
        RestorePlayerMass();

        currentPlayer = player;
        builtForPlayer = null;
        builtForNonHeadSegments = 0;
        builtForBifurcation = false;
        activeLeviathanSquadron = null;
        segments.Clear();
        ClearCustomTemplateSlots();
        LeviathanAttachmentNormalizer.Reset();

        Pilot pilot = GameShip.GetPlayerSourcePilot(player);

        if (pilot == null)
        {
            Debug.LogWarning(
                "[Leviathan] Player ship has no player Pilot."
            );
            return;
        }

        LeviathanSpecializationCurrency.MigrateLegacyGrowth(pilot);


        LeviathanGrowth.ResolvedState growth =
            LeviathanGrowth.GetResolvedState(player);

        if (growth == null || !growth.Active)
        {
            RestorePlayerMass();

            Debug.Log(
                "[Leviathan] Evolution rank is 0; Leviathan inactive."
            );
            return;
        }

        Debug.Log(
            "[Leviathan] Leviathan chassis active. Growth tree active = " +
            growth.TreeActive +
            ", Segment budget = " +
            growth.NonHeadSegments
        );

        if (!TryCreateLeviathan(player, growth))
            return;

        ApplyPlayerMass(player, growth);

        builtForPlayer = player;
        builtForNonHeadSegments = growth.NonHeadSegments;
        builtForBifurcation = growth.Bifurcation;

        Debug.Log(
            "[Leviathan] Build complete. Segments = " +
            segments.Count +
            ", Mass multiplier = " +
            growth.MassMultiplier.ToString("0.00") +
            ", Air resistance strength = " +
            growth.AirResistanceStrength.ToString("0.000")
        );
    }

    public void RequestGrowthRefreshIfNeeded(GameShip player)
    {
        if (player == null || !IsCurrentPlayerShip(player))
            return;

        LeviathanGrowth.ResolvedState growth =
            LeviathanGrowth.GetResolvedState(player);

        int desiredSegments =
            growth == null || !growth.Active
                ? 0
                : growth.NonHeadSegments;

        bool desiredBifurcation =
            growth != null && growth.Bifurcation;

        if (player != builtForPlayer ||
            desiredSegments != builtForNonHeadSegments ||
            desiredBifurcation != builtForBifurcation)
        {
            RequestGrowthRefresh(player);
            return;
        }

        // Stat-only Growth changes (Higgs/Ancient Wyrm, etc.) do not need to
        // destroy/rebuild the chain, but mass is a stored Rigidbody value rather
        // than a property getter, so refresh it explicitly.
        if (growth != null && growth.Active)
            ApplyPlayerMass(player, growth);
    }

    /// <summary>
    /// Rebuild the live Leviathan after Growth changes. Multiple changes in
    /// quick succession are coalesced so only the final resolved anatomy is constructed.
    /// </summary>
    public void RequestGrowthRefresh(GameShip player)
    {
        if (player == null || !IsCurrentPlayerShip(player))
            return;

        if (growthRefreshCoroutine != null)
            StopCoroutine(growthRefreshCoroutine);

        growthRefreshCoroutine = StartCoroutine(
            RefreshGrowthRoutine(player)
        );
    }

    private IEnumerator RefreshGrowthRoutine(GameShip player)
    {
        // Let the native upgrade/specialization transaction finish first.
        // This also coalesces several rapid changes into one reconstruction.
        yield return null;

        if (!IsCurrentPlayerShip(player))
        {
            growthRefreshCoroutine = null;
            yield break;
        }

        TearDownLeviathanForRefresh(player);

        // UnityEngine.Object.Destroy is deferred. Give the old attached ship
        // objects a frame to disappear before building the replacement chain.
        yield return null;

        if (!IsCurrentPlayerShip(player))
        {
            growthRefreshCoroutine = null;
            yield break;
        }

        growthRefreshCoroutine = null;
        SetPlayerShip(player);
    }

    private void TearDownLeviathanForRefresh(GameShip player)
    {
        LeviathanPredatorRuntime.CancelForPlayer(player);
        LeviathanConstrictor.Reset();

        List<GameShip> oldSegments = segments
            .Where(x => x != null && x != player)
            .ToList();

        RestorePlayerMass();

        // SetSquadron is a simple assignment in the native GameShip code.
        // Detach the live head and followers from the old Squadron before the
        // follower GameObjects are retired so no stale chain can participate
        // in the next build.
        if (player != null &&
            activeLeviathanSquadron != null &&
            player.squadron == activeLeviathanSquadron)
        {
            player.SetSquadron(null);
        }

        for (int i = 0; i < oldSegments.Count; i++)
        {
            GameShip segment = oldSegments[i];

            if (segment == null)
                continue;

            if (segment.squadron == activeLeviathanSquadron)
                segment.SetSquadron(null);
        }

        activeLeviathanSquadron = null;
        segments.Clear();
        ClearCustomTemplateSlots();
        LeviathanAttachmentNormalizer.Reset();

        currentPlayer = player;
        builtForPlayer = null;
        builtForNonHeadSegments = 0;
        builtForBifurcation = false;

        for (int i = 0; i < oldSegments.Count; i++)
        {
            GameShip segment = oldSegments[i];

            if (segment == null || segment.gameObject == null)
                continue;

            // Hide/collide no further immediately; actual destruction occurs
            // at the normal Unity end-of-frame boundary.
            segment.gameObject.SetActive(false);
            UnityEngine.Object.Destroy(segment.gameObject);
        }
    }

    private static bool IsCurrentPlayerShip(GameShip player)
    {
        return player != null &&
            WorldController.instance != null &&
            WorldController.instance.GetCurrentPlayerShip() == player;
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

        if (growthRefreshCoroutine != null)
        {
            StopCoroutine(growthRefreshCoroutine);
            growthRefreshCoroutine = null;
        }

        RestorePlayerMass();

        currentPlayer = null;
        builtForPlayer = null;
        builtForNonHeadSegments = 0;
        builtForBifurcation = false;
        activeLeviathanSquadron = null;
        segments.Clear();
        ClearCustomTemplateSlots();
        LeviathanAttachmentNormalizer.Reset();
    }

    public GameShip GetDamageRedirectTarget(GameShip target)
    {
        if (currentPlayer == null)
            return null;

        if (!segments.Contains(target))
            return null;

        return currentPlayer;
    }

    public bool ShouldNormalizeAttachment(GameShip ship)
    {
        if (ship == null || activeLeviathanSquadron == null)
            return false;

        if (ship.squadron != activeLeviathanSquadron)
            return false;

        List<Squadron.SquadronShip> ships =
            activeLeviathanSquadron.ships;

        if (ships == null)
            return false;

        int index = -1;

        for (int i = 1; i < ships.Count; i++)
        {
            if (ships[i].ship == ship)
            {
                index = i;
                break;
            }
        }

        if (index < 1 || index >= customTemplateSlots.Count)
            return false;

        if (customTemplateSlots[index])
            return true;

        return index > 1 && customTemplateSlots[index - 1];
    }

    private bool TryCreateLeviathan(
        GameShip player,
        LeviathanGrowth.ResolvedState growth)
    {
        SquadronBase leviathanBase =
            Resources.FindObjectsOfTypeAll<SquadronBase>()
                .FirstOrDefault(x => x.name == "LeviathanTest");

        if (leviathanBase == null)
        {
            Debug.LogError(
                "[Leviathan] Could not find SquadronBase 'LeviathanTest'."
            );
            return false;
        }

        // LeviathanTest is the authored seed pool: head + 15 body
        // definitions + tail. Runtime anatomy is resolved entirely from Growth;
        // extra body definitions are cloned when the specialization exceeds it.
        Squadron squadron =
            leviathanBase.GetSquadron(1);

        if (squadron == null)
        {
            Debug.LogError(
                "[Leviathan] GetSquadron(1) returned null."
            );
            return false;
        }

        List<Squadron.SquadronShip> ships =
            squadron.ships;

        if (ships == null || ships.Count < 5)
        {
            Debug.LogError(
                "[Leviathan] Unexpected squadron ship count: " +
                (ships == null ? -1 : ships.Count)
            );
            return false;
        }

        if (!PrepareSquadronForSegmentBudget(ships, growth.NonHeadSegments))
            return false;

        if (growth.Bifurcation)
            LeviathanSegmentTemplates.EnsureBifurcationDefaults();

        ApplySegmentTemplates(ships);

        Squadron.SquadronShip headSlot =
            ships[0];

        headSlot.ship = player;
        headSlot.spawned = true;
        headSlot.temporary = false;

        activeLeviathanSquadron = squadron;
        LeviathanAttachmentNormalizer.Reset();

        player.SetSquadron(squadron);

        squadron.InvalidateShipCaches();
        squadron.Build(true);

        segments.Clear();

        for (int i = 1; i < ships.Count; i++)
        {
            GameShip segment = ships[i].ship;

            if (segment == null)
            {
                Debug.LogError(
                    "[Leviathan] Segment failed to spawn at slot " +
                    i
                );

                activeLeviathanSquadron = null;
                segments.Clear();
                return false;
            }

            segment.faction = player.faction;

            // Leviathan followers are structural sections, not independent
            // NPCs. Hide their floating NPC name/health minibars locally and
            // persist the flag in the Ship payload so remote replicas hide
            // them too.
            segment.disableMinibars = true;

            if (segment.originalShip != null)
                segment.originalShip.disableMinibars = true;

            segment.CheckAttachMinibars();

            // Squadron.SpawnShip creates these as star-owned entities. In a
            // multiplayer session the Leviathan chain is actually owned by the
            // local player, so hand each segment to Star Vortex's native
            // player-entity replication path. NetWorldBridge will allocate a
            // player-owned netId, announce the full Ship JSON, and stream the
            // segment transform to every other peer.
            if (NetSession.InSession)
            {
                segment.netStarEntity = false;
                segment.netPlayerEntity = true;
            }

            segments.Add(segment);
        }

        return true;
    }

    private bool PrepareSquadronForSegmentBudget(
        List<Squadron.SquadronShip> ships,
        int desiredNonHeadSegments)
    {
        if (ships == null)
            return false;

        // LeviathanTest remains the authored seed pool:
        // [head, body1 ... body15, tail].
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
                ships.Count +
                "."
            );
            return false;
        }

        int desiredSegments = Mathf.Clamp(
            desiredNonHeadSegments,
            2,
            64
        );

        int desiredBodyCount = desiredSegments - 1;

        if (desiredBodyCount < authoredBodyCount)
        {
            ships.RemoveRange(
                1 + desiredBodyCount,
                authoredBodyCount - desiredBodyCount
            );
        }
        else if (desiredBodyCount > authoredBodyCount)
        {
            Squadron.SquadronShip prototype = ships[authoredBodyCount];

            for (int bodyNumber = authoredBodyCount + 1;
                bodyNumber <= desiredBodyCount;
                bodyNumber++)
            {
                Squadron.SquadronShip clone =
                    CloneBodySlotDefinition(prototype);

                if (clone == null)
                {
                    Debug.LogError(
                        "[Leviathan] Could not clone a body definition for " +
                        "segment " + bodyNumber + "."
                    );
                    return false;
                }

                // Keep the authored tail as the final list item.
                ships.Insert(ships.Count - 1, clone);
            }
        }

        Debug.Log(
            "[Leviathan] Using " +
            desiredBodyCount +
            " body segments + tail (" +
            desiredSegments +
            " non-head pieces)."
        );

        return true;
    }

    private static Squadron.SquadronShip CloneBodySlotDefinition(
        Squadron.SquadronShip source)
    {
        if (source == null)
            return null;

        Squadron.SquadronShip result =
            source.Clone() as Squadron.SquadronShip;

        if (result == null)
            return null;

        // SquadronShip.Clone intentionally shares its NPC reference. Extra
        // Growth slots need independent Ship definitions so Tail/Body numbered
        // template overrides cannot mutate another slot that shares the NPC.
        NPC sourceNpc = source.npc as NPC;

        if (sourceNpc == null)
            return null;

        NPCBase npcBase = sourceNpc.GetNPCBase();

        if (npcBase == null)
            return null;

        int level = 1;

        if (sourceNpc.ship != null &&
            sourceNpc.ship.pilot != null)
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

    private void ApplySegmentTemplates(
        List<Squadron.SquadronShip> ships)
    {
        ResizeCustomTemplateSlots(ships == null ? 0 : ships.Count);

        if (ships == null || ships.Count < 2)
            return;

        Dictionary<string, string> templates =
            LeviathanSegmentTemplates.LoadSerializedBodies();

        string body;
        templates.TryGetValue("Body", out body);

        int tailIndex = ships.Count - 1;

        for (int i = 1; i < tailIndex; i++)
        {
            // Numbered templates map one-to-one to resolved physical
            // body positions; Body remains the generic fallback.
            int templateNumber = i;

            string serialized;
            bool hasNumbered =
                templates.TryGetValue(
                    templateNumber.ToString(),
                    out serialized
                );

            if (!hasNumbered)
                serialized = body;

            if (string.IsNullOrEmpty(serialized) ||
                ships[i] == null ||
                ships[i].npc == null)
            {
                continue;
            }

            Ship shipDefinition = ships[i].npc.GetShip();

            if (shipDefinition != null)
            {
                shipDefinition.serializedBody = serialized;
                customTemplateSlots[i] = true;
            }
        }

        string tail;

        if (templates.TryGetValue("Tail", out tail) &&
            !string.IsNullOrEmpty(tail) &&
            ships[tailIndex] != null &&
            ships[tailIndex].npc != null)
        {
            Ship tailDefinition = ships[tailIndex].npc.GetShip();

            if (tailDefinition != null)
            {
                tailDefinition.serializedBody = tail;
                customTemplateSlots[tailIndex] = true;
            }
        }
    }

    private void ApplyPlayerMass(
        GameShip player,
        LeviathanGrowth.ResolvedState growth)
    {
        if (player == null ||
            growth == null ||
            !growth.Active)
        {
            return;
        }

        Rigidbody2D body = player.GetRigidBody();

        if (body == null)
        {
            Debug.LogWarning(
                "[Leviathan] Player has no Rigidbody2D; mass unchanged."
            );
            return;
        }

        if (massAdjustedPlayer != player ||
            massAdjustedBody != body ||
            !hasOriginalPlayerMass)
        {
            RestorePlayerMass();

            massAdjustedPlayer = player;
            massAdjustedBody = body;
            originalPlayerMass = body.mass;
            hasOriginalPlayerMass = true;
        }

        body.mass = originalPlayerMass * Mathf.Max(0.01f, growth.MassMultiplier);
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
        // This is intentionally player-only and Leviathan-only.
        if (currentPlayer == null || builtForPlayer != currentPlayer)
            return;

        LeviathanGrowth.ResolvedState growth =
            LeviathanGrowth.GetResolvedState(currentPlayer);

        if (growth == null ||
            !growth.Active ||
            growth.AirResistanceStrength <= 0f)
        {
            return;
        }

        // Native weapon lunges should not be damped by the Leviathan cruising
        // resistance curve. Predator explicitly uses GameShip.Lunge.
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
        float resistanceStart = maxSpeed * LeviathanGrowth.Tuning.AirResistanceStartFraction;

        if (speed <= resistanceStart)
            return;

        float t = Mathf.InverseLerp(
            resistanceStart,
            maxSpeed,
            speed
        );

        // Quadratic curve: very light near the start, increasingly severe
        // as actual speed approaches the ship's theoretical MaxSpeed.
        float resistanceFactor = t * t;
        float decelerationPerSecond =
            maxSpeed * growth.AirResistanceStrength * resistanceFactor;

        body.velocity = Vector2.MoveTowards(
            velocity,
            Vector2.zero,
            decelerationPerSecond * Time.fixedDeltaTime
        );
    }

    public bool IsLeviathanSegment(GameShip ship)
    {
        return ship != null && segments.Contains(ship);
    }

    public int GetActiveSectionCount(GameShip player)
    {
        if (player == null ||
            player != currentPlayer ||
            builtForPlayer != player)
        {
            return 0;
        }

        // Head/player + every active body segment + tail.
        return 1 + segments.Count;
    }

    public int GetActiveLeviathanSegmentCount(GameShip player)
    {
        if (player == null ||
            player != currentPlayer ||
            builtForPlayer != player)
        {
            return 0;
        }

        // Every attached Leviathan section after the head: bodies + tail.
        return segments.Count;
    }

    public int GetActiveBodySegmentCount(GameShip player)
    {
        int segmentCount = GetActiveLeviathanSegmentCount(player);

        // Until forked Bifurcation topology is physically instantiated, the
        // active squadron contains one terminal tail. Report the live physical
        // body count rather than the future logical branch allocation.
        return segmentCount > 0
            ? segmentCount - 1
            : 0;
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
