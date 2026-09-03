using System.Collections;
using System.Collections.Generic;
using System.Linq;
using StarVortex;
using UnityEngine;

public class LeviathanController : MonoBehaviour
{
    // Rank progression. Five skill ranks, three new body segments per rank.
    // Rank 1 = 1-3, rank 2 = 1-6, ... rank 5 = 1-15, then tail.
    private const int BodySegmentsPerRank = 3;
    private const int MaxLeviathanRank = 5;

    // Effective mass of the player/head. +40% per Leviathan rank.
    // Rank 1 = 1.4x, rank 5 = 3.0x.
    private const float MassBonusPerRank = 0.40f;

    // Leviathan-only high-speed resistance.
    // No extra resistance below 40% of the ship's normal MaxSpeed.
    // Above that point resistance grows quadratically.
    private const float ResistanceStartFraction = 0.40f;

    // At the theoretical MaxSpeed, subtract this fraction of MaxSpeed
    // from velocity per second. This is deliberately easy to tune.
    private const float ResistanceStrength = 0.65f;

    private readonly HashSet<GameShip> segments =
        new HashSet<GameShip>();

    private readonly List<bool> customTemplateSlots =
        new List<bool>();

    private GameShip currentPlayer;
    private GameShip builtForPlayer;
    private int builtForUpgradeLevel;
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
        // but immediately schedule a clean rebuild if Growth changed on the
        // same live player ship. This also catches rank changes that reach us
        // through some path other than GameShip.SetUpgrade.
        if (player == builtForPlayer)
        {
            Pilot existingPilot = GameShip.GetPlayerSourcePilot(player);
            int existingUpgradeLevel = existingPilot == null
                ? builtForUpgradeLevel
                : existingPilot.GetUpgradeLevel(LeviathanMod.GrowthUpgrade);

            if (existingUpgradeLevel != builtForUpgradeLevel)
                RequestGrowthRefresh(player);

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
        builtForUpgradeLevel = 0;
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

        int upgradeLevel =
            pilot.GetUpgradeLevel(LeviathanMod.GrowthUpgrade);

        if (upgradeLevel < 1)
        {
            RestorePlayerMass();

            Debug.Log(
                "[Leviathan] Growth rank is 0; Leviathan inactive."
            );
            return;
        }

        Debug.Log(
            "[Leviathan] Growth detected. Rank = " +
            upgradeLevel
        );

        if (!TryCreateLeviathan(player, upgradeLevel))
            return;

        ApplyPlayerMass(player, upgradeLevel);

        builtForPlayer = player;
        builtForUpgradeLevel = upgradeLevel;

        Debug.Log(
            "[Leviathan] Build complete. Rank = " +
            upgradeLevel +
            ", Segments = " +
            segments.Count +
            ", Mass multiplier = " +
            GetMassMultiplier(upgradeLevel).ToString("0.00")
        );
    }

    /// <summary>
    /// Rebuild the live Leviathan after Growth changes. Multiple changes in
    /// quick succession are coalesced so only the final rank is constructed.
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
        // Let the native SetUpgrade/UI transaction finish first. This also
        // coalesces several rapid +/- clicks into one reconstruction.
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
        builtForUpgradeLevel = 0;

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
        builtForUpgradeLevel = 0;
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
        int upgradeLevel)
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

        // LeviathanTest now contains the complete physical pool:
        // head + 15 body slots + tail. We request that authored definition
        // and trim only the unused body slots for the current skill rank.
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

        if (!PrepareSquadronForRank(ships, upgradeLevel))
            return false;

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
            segments.Add(segment);
        }

        return true;
    }

    private bool PrepareSquadronForRank(
        List<Squadron.SquadronShip> ships,
        int upgradeLevel)
    {
        if (ships == null)
            return false;

        // LeviathanTest is authored as a fixed 17-slot pool:
        // [head, body1 ... body15, tail].
        // Skill rank simply exposes the first rank * 3 body slots.
        const int expectedShipCount =
            1 + (BodySegmentsPerRank * MaxLeviathanRank) + 1;

        if (ships.Count != expectedShipCount)
        {
            Debug.LogError(
                "[Leviathan] LeviathanTest must contain exactly " +
                expectedShipCount +
                " ships (head + 15 body + tail). Found " +
                ships.Count +
                "."
            );
            return false;
        }

        int effectiveRank = Mathf.Clamp(
            upgradeLevel,
            1,
            MaxLeviathanRank
        );

        int desiredBodyCount =
            effectiveRank * BodySegmentsPerRank;

        int maxBodyCount =
            BodySegmentsPerRank * MaxLeviathanRank;

        int firstUnusedBodyIndex = 1 + desiredBodyCount;
        int unusedBodyCount = maxBodyCount - desiredBodyCount;

        // The tail starts at the end of the list. Remove only the unused
        // body entries immediately before it; the tail then slides into the
        // correct final position for this rank.
        if (unusedBodyCount > 0)
        {
            ships.RemoveRange(
                firstUnusedBodyIndex,
                unusedBodyCount
            );
        }

        Debug.Log(
            "[Leviathan] Rank " +
            upgradeLevel +
            " using body segments 1-" +
            desiredBodyCount +
            " + tail."
        );

        return true;
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
            // Numbered templates now map one-to-one to physical body slots:
            // 1,2,3 at rank 1; 4,5,6 added at rank 2; ... up to 15.
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
        int upgradeLevel)
    {
        if (player == null || upgradeLevel < 1)
            return;

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

        body.mass = originalPlayerMass * GetMassMultiplier(upgradeLevel);
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

    private static float GetMassMultiplier(int upgradeLevel)
    {
        int effectiveRank = Mathf.Clamp(upgradeLevel, 0, MaxLeviathanRank);
        return 1f + effectiveRank * MassBonusPerRank;
    }

    private void ApplyHighSpeedResistance()
    {
        // This is intentionally player-only and Leviathan-only. NPCs and
        // players without the Growth upgrade never pass these checks.
        if (currentPlayer == null || builtForUpgradeLevel < 1)
            return;

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
        float resistanceStart = maxSpeed * ResistanceStartFraction;

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
            maxSpeed * ResistanceStrength * resistanceFactor;

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
            builtForUpgradeLevel < 1)
        {
            return 0;
        }

        // Head/player + every active body segment + tail.
        return 1 + segments.Count;
    }

    public float GetActiveSectionSizeValue(GameShip player)
    {
        if (player == null ||
            player != currentPlayer ||
            builtForUpgradeLevel < 1)
        {
            return 0f;
        }

        float value = GetSectionSizeValue(player);

        foreach (GameShip segment in segments)
        {
            if (segment != null)
                value += GetSectionSizeValue(segment);
        }

        return value;
    }

    private static float GetSectionSizeValue(GameShip ship)
    {
        if (ship == null)
            return 0f;

        // Ship.Class values in the native assembly:
        // 3 Frigate, 4 Destroyer, 5 Cruiser, 6 Battleship,
        // 7 Dreadnought, 8 Boss. Predator caps size contribution at 1.5.
        int shipClass = (int)ship.GetShipClass();

        switch (shipClass)
        {
            case 4:
                return 1.20f;
            case 5:
                return 1.30f;
            case 6:
                return 1.40f;
            case 7:
            case 8:
                return 1.50f;
            case 3:
            default:
                return 1.00f;
        }
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
