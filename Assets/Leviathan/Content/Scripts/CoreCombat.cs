using HarmonyLib;
using StarVortex;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

/// <summary>
/// Shared Core combat provenance/correlation foundation.
///
/// This layer answers "which semantic mechanic authored this exact damage
/// transaction?" while leaving native Star Vortex damage authoritative.  It is
/// source-owner local for retained gameplay history; target authorities only
/// hold transient metadata long enough to return authoritative outcomes.
/// </summary>
public static class CoreCombat
{
    // =====================================================================
    // STABLE SEMANTIC IDS
    // =====================================================================

    public static class SkillIds
    {
        public const byte None = 0;
        public const byte Growth = 1;
        public const byte Predator = 2;
        public const byte Constrictor = 3;
        public const byte Behemoth = 4;
        public const byte Starfire = 5;
        public const byte StellarConverter = 6;
        public const byte DronesTurretsBeacons = 7;
        public const byte Orrery = 8;
    }

    public static class EffectIds
    {
        public static class Predator
        {
            public const byte DirectLunge = 1;
            public const byte VenomTick = 2;
            public const byte Digestion = 3;
            public const byte SpitImpact = 4;

            // State/event ranges are deliberately separate from damage effects.
            public const byte PreyState = 64;
            public const byte PreyKillEvent = 128;
            public const byte LungeKillEvent = 129;
            public const byte TargetDeathObservedEvent = 130;
        }

        public static class Constrictor
        {
            public const byte BodyContact = 1;
            public const byte Crush = 2;
            public const byte Whip = 3;
        }

        public static class Starfire
        {
            public const byte Breath = 1;
            public const byte BlastWave = 2;
        }

        public static class StellarConverter
        {
            public const byte Beam = 1;
            public const byte SingularityHalo = 2;
        }

        public static class DronesTurretsBeacons
        {
            public const byte Laser = 1;
        }
    }

    public struct SemanticKey : IEquatable<SemanticKey>
    {
        public ushort Value;

        public byte SkillId { get { return (byte)(Value >> 8); } }
        public byte EffectId { get { return (byte)(Value & 0xFF); } }
        public bool IsValid { get { return Value != 0; } }

        public static SemanticKey Create(byte skillId, byte effectId)
        {
            SemanticKey key = new SemanticKey();
            key.Value = (ushort)((skillId << 8) | effectId);
            return key;
        }

        public static SemanticKey FromPacked(ushort value)
        {
            SemanticKey key = new SemanticKey();
            key.Value = value;
            return key;
        }

        public bool Equals(SemanticKey other) { return Value == other.Value; }
        public override bool Equals(object obj)
        {
            return obj is SemanticKey && Equals((SemanticKey)obj);
        }
        public override int GetHashCode() { return Value; }
        public override string ToString()
        {
            return SkillId + ":" + EffectId;
        }
    }

    public static class Semantics
    {
        public static readonly SemanticKey PredatorDirectLunge =
            SemanticKey.Create(SkillIds.Predator, EffectIds.Predator.DirectLunge);
        public static readonly SemanticKey PredatorPrey =
            SemanticKey.Create(SkillIds.Predator, EffectIds.Predator.PreyState);
        public static readonly SemanticKey PredatorPreyKill =
            SemanticKey.Create(SkillIds.Predator, EffectIds.Predator.PreyKillEvent);
        public static readonly SemanticKey PredatorLungeKill =
            SemanticKey.Create(SkillIds.Predator, EffectIds.Predator.LungeKillEvent);
        public static readonly SemanticKey PredatorTargetDeathObserved =
            SemanticKey.Create(
                SkillIds.Predator,
                EffectIds.Predator.TargetDeathObservedEvent);
        public static readonly SemanticKey ConstrictorBodyContact =
            SemanticKey.Create(SkillIds.Constrictor, EffectIds.Constrictor.BodyContact);
        public static readonly SemanticKey StarfireBreath =
            SemanticKey.Create(SkillIds.Starfire, EffectIds.Starfire.Breath);
        public static readonly SemanticKey StarfireBlastWave =
            SemanticKey.Create(SkillIds.Starfire, EffectIds.Starfire.BlastWave);
        public static readonly SemanticKey ConverterBeam =
            SemanticKey.Create(SkillIds.StellarConverter, EffectIds.StellarConverter.Beam);
        public static readonly SemanticKey ConverterSingularityHalo =
            SemanticKey.Create(SkillIds.StellarConverter, EffectIds.StellarConverter.SingularityHalo);
        public static readonly SemanticKey DroneLaser =
            SemanticKey.Create(SkillIds.DronesTurretsBeacons, EffectIds.DronesTurretsBeacons.Laser);
    }

    // =====================================================================
    // IDENTITY
    // =====================================================================

    public enum CombatEntityKind : byte
    {
        None = 0,
        Player = 1,
        PlayerOwnedNetEntity = 2,
        StarNetEntity = 3,
        LocalObject = 4
    }

    /// <summary>
    /// Session-scoped semantic identity.  Network ids retain their native
    /// namespace; local Unity instance ids are never confused with network ids.
    /// </summary>
    public struct CombatEntityKey : IEquatable<CombatEntityKey>
    {
        public CombatEntityKind Kind;
        public uint Id;
        public uint SessionGeneration;

        public bool IsValid
        {
            get { return Kind != CombatEntityKind.None && SessionGeneration != 0U; }
        }

        public bool Equals(CombatEntityKey other)
        {
            return Kind == other.Kind && Id == other.Id &&
                SessionGeneration == other.SessionGeneration;
        }

        public override bool Equals(object obj)
        {
            return obj is CombatEntityKey && Equals((CombatEntityKey)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = (int)Kind;
                hash = (hash * 397) ^ (int)Id;
                hash = (hash * 397) ^ (int)SessionGeneration;
                return hash;
            }
        }

        public override string ToString()
        {
            return Kind + ":" + Id + "@" + SessionGeneration;
        }
    }

    public enum ContributorKind : byte
    {
        None = 0,
        Section = 1,
        Head = 2,
        Tail = 3,
        Drone = 4,
        Turret = 5,
        Beacon = 6,
        Clone = 7,
        WeaponSlot = 8,
        Satellite = 9,
        Other = 255
    }

    public struct ContributorKey : IEquatable<ContributorKey>
    {
        public uint Value;
        public ContributorKind Kind { get { return (ContributorKind)(Value >> 24); } }
        public uint LocalId { get { return Value & 0x00FFFFFFU; } }
        public bool IsValid { get { return Value != 0U; } }

        /// <summary>
        /// Constructs a contributor key from an ID owned by the contributor's
        /// build/runtime subsystem. This is the preferred path when Growth, a
        /// drone manager, turret manager, etc. already has a canonical local ID.
        /// IDs larger than the 24-bit contributor field are rejected rather than
        /// silently truncated into an alias.
        /// </summary>
        public static ContributorKey Create(ContributorKind kind, uint localId)
        {
            if (kind == ContributorKind.None || localId > 0x00FFFFFFU)
                return default(ContributorKey);

            ContributorKey key = new ContributorKey();
            key.Value = ((uint)kind << 24) | localId;
            return key;
        }

        /// <summary>
        /// Fallback for contributors that do not yet expose their own canonical
        /// build-local ID. The shared layer assigns a monotonic 24-bit local ID
        /// to this exact object reference; Unity instance-id bits are never packed
        /// into ContributorKey. Prefer Create(kind, canonicalLocalId) when the
        /// owning subsystem already has such an ID.
        /// </summary>
        public static ContributorKey FromUnityObject(ContributorKind kind, UnityEngine.Object value)
        {
            return GetOrAssignContributorKey(kind, value);
        }

        public bool Equals(ContributorKey other) { return Value == other.Value; }
        public override bool Equals(object obj)
        {
            return obj is ContributorKey && Equals((ContributorKey)obj);
        }
        public override int GetHashCode() { return unchecked((int)Value); }
    }

    private struct ContributorObjectKey : IEquatable<ContributorObjectKey>
    {
        public ContributorKind Kind;
        public UnityEngine.Object Object;

        public bool Equals(ContributorObjectKey other)
        {
            return Kind == other.Kind && ReferenceEquals(Object, other.Object);
        }

        public override bool Equals(object obj)
        {
            return obj is ContributorObjectKey && Equals((ContributorObjectKey)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int objectHash = ReferenceEquals(Object, null)
                    ? 0
                    : RuntimeHelpers.GetHashCode(Object);
                return (objectHash * 397) ^ (int)Kind;
            }
        }
    }

    // Object-backed contributor IDs are a fallback only. They are assigned
    // monotonically per contributor kind for the whole session and are never
    // recycled within that session, so a destroyed section/drone cannot alias
    // an older history entry merely because Unity reused an instance ID.
    private const int MaxContributorObjectMappings = 4096;

    private static readonly Dictionary<ContributorObjectKey, ContributorKey>
        ContributorKeysByObject =
            new Dictionary<ContributorObjectKey, ContributorKey>(128);
    private static readonly uint[] NextContributorIdByKind = new uint[256];
    private static readonly List<ContributorObjectKey> ContributorObjectScratch =
        new List<ContributorObjectKey>(16);
    private static bool warnedContributorIdExhaustion;
    private static bool warnedContributorObjectCapacity;

    public static ContributorKey GetOrAssignContributorKey(
        ContributorKind kind,
        UnityEngine.Object value)
    {
        if (value == null || kind == ContributorKind.None)
            return default(ContributorKey);

        ContributorObjectKey objectKey = new ContributorObjectKey();
        objectKey.Kind = kind;
        objectKey.Object = value;

        ContributorKey existing;
        if (ContributorKeysByObject.TryGetValue(objectKey, out existing))
            return existing;

        // This fallback table is intentionally bounded.  Canonical contributor
        // IDs supplied by the owning subsystem through ContributorKey.Create do
        // not consume this table at all.  Object-backed IDs are only a bridge for
        // systems that have not exposed their own build/runtime-local identity.
        if (ContributorKeysByObject.Count >= MaxContributorObjectMappings)
        {
            PruneDestroyedContributorObjects();
            if (ContributorKeysByObject.Count >= MaxContributorObjectMappings)
            {
                if (!warnedContributorObjectCapacity)
                {
                    warnedContributorObjectCapacity = true;
                    Debug.LogWarning(
                        "[CoreCombat] Contributor object mapping capacity " +
                        MaxContributorObjectMappings +
                        " reached; dropping optional object-backed contributor " +
                        "attribution. Prefer ContributorKey.Create with the " +
                        "owning subsystem's canonical local ID.");
                }
                return default(ContributorKey);
            }
        }

        int kindIndex = (byte)kind;
        uint next = NextContributorIdByKind[kindIndex] + 1U;
        if (next == 0U || next > 0x00FFFFFFU)
        {
            if (!warnedContributorIdExhaustion)
            {
                warnedContributorIdExhaustion = true;
                Debug.LogWarning(
                    "[CoreCombat] Contributor ID space exhausted for kind " +
                    kind + "; dropping optional contributor attribution.");
            }
            return default(ContributorKey);
        }

        ContributorKey assigned = ContributorKey.Create(kind, next);
        if (!assigned.IsValid)
            return assigned;

        NextContributorIdByKind[kindIndex] = next;
        ContributorKeysByObject.Add(objectKey, assigned);
        return assigned;
    }

    /// <summary>
    /// Releases fallback object-to-contributor associations for a destroyed or
    /// rebuilt contributor.  Numeric ContributorKey values are never recycled
    /// during the session, so removing this lookup cannot alias retained history.
    /// Owning systems that use FromUnityObject for non-GameShip contributors
    /// should call this at their own teardown boundary.
    /// </summary>
    public static void ReleaseContributorObject(UnityEngine.Object value)
    {
        if (ReferenceEquals(value, null) || ContributorKeysByObject.Count == 0)
            return;

        ContributorObjectScratch.Clear();
        foreach (KeyValuePair<ContributorObjectKey, ContributorKey> pair
            in ContributorKeysByObject)
        {
            if (ReferenceEquals(pair.Key.Object, value))
                ContributorObjectScratch.Add(pair.Key);
        }

        for (int i = 0; i < ContributorObjectScratch.Count; i++)
            ContributorKeysByObject.Remove(ContributorObjectScratch[i]);

        ContributorObjectScratch.Clear();
    }

    private static void PruneDestroyedContributorObjects()
    {
        if (ContributorKeysByObject.Count == 0)
            return;

        ContributorObjectScratch.Clear();
        foreach (KeyValuePair<ContributorObjectKey, ContributorKey> pair
            in ContributorKeysByObject)
        {
            // Unity's overloaded null comparison becomes true after the native
            // object is destroyed even while the managed wrapper still exists.
            if (pair.Key.Object == null)
                ContributorObjectScratch.Add(pair.Key);
        }

        for (int i = 0; i < ContributorObjectScratch.Count; i++)
            ContributorKeysByObject.Remove(ContributorObjectScratch[i]);

        ContributorObjectScratch.Clear();
    }

    private static uint sessionGeneration = 1U;

    public static bool TryGetEntityKey(GameShip ship, out CombatEntityKey key)
    {
        key = default(CombatEntityKey);
        if (ship == null)
            return false;

        if (NetSession.InSession && NetSession.instance != null)
        {
            if (ship.IsRemotePlayer())
            {
                int remotePlayerId;
                if (CoreNetwork.TryGetPlayerId(ship, out remotePlayerId))
                {
                    key = ForPlayerId(remotePlayerId);
                    return key.IsValid;
                }
            }

            if (ship.IsPlayer())
            {
                key = ForPlayerId(NetSession.instance.localPlayerId);
                return key.IsValid;
            }

            if (ship.netId != 0U)
            {
                key.Kind = NetIds.IsPlayerEntityNetId(ship.netId)
                    ? CombatEntityKind.PlayerOwnedNetEntity
                    : CombatEntityKind.StarNetEntity;
                key.Id = ship.netId;
                key.SessionGeneration = sessionGeneration;
                return true;
            }
        }

        key.Kind = CombatEntityKind.LocalObject;
        key.Id = unchecked((uint)ship.GetInstanceID());
        key.SessionGeneration = sessionGeneration;
        return true;
    }

    public static CombatEntityKey ForPlayerId(int playerId)
    {
        CombatEntityKey key = default(CombatEntityKey);
        if (playerId < 0)
            return key;

        key.Kind = CombatEntityKind.Player;
        key.Id = unchecked((uint)playerId);
        key.SessionGeneration = sessionGeneration;
        return key;
    }

    public static CombatEntityKey ForNetworkEntity(uint netId)
    {
        CombatEntityKey key = default(CombatEntityKey);
        if (netId == 0U)
            return key;

        key.Kind = NetIds.IsPlayerEntityNetId(netId)
            ? CombatEntityKind.PlayerOwnedNetEntity
            : CombatEntityKind.StarNetEntity;
        key.Id = netId;
        key.SessionGeneration = sessionGeneration;
        return key;
    }

    // =====================================================================
    // OUTCOMES / TRACKING
    // =====================================================================

    public enum AcknowledgementMode : byte
    {
        None = 0,
        NativeResult = 1,
        GuaranteedOutcome = 2
    }

    /// <summary>
    /// Protocol value 2 is reserved, but this implementation does not yet emit
    /// synthetic zero-damage acknowledgements. Callers requesting it are
    /// explicitly downgraded to NativeResult until the native-safe result path
    /// is verified and implemented.
    /// </summary>
    public static bool SupportsGuaranteedOutcome
    {
        get { return false; }
    }

    [Flags]
    public enum TrackingFlags : byte
    {
        None = 0,

        /// <summary>
        /// Record both source-side attempts and confirmed authoritative outcomes
        /// in cumulative relationship/owner summaries. Without this flag neither
        /// side mutates summary counters or contributor-window state.
        /// </summary>
        Summary = 1 << 0,

        /// <summary>
        /// Retain the confirmed outcome in the bounded meaningful-event ring.
        /// Independent from Summary; discrete history can be requested without
        /// allocating/updating cumulative relationship summaries.
        /// </summary>
        MeaningfulOutcome = 1 << 1
    }

    [Flags]
    public enum OutcomeFlags : byte
    {
        None = 0,
        Processed = 1 << 0,
        Damaged = 1 << 1,
        Destroyed = 1 << 2,
        StatusInflicted = 1 << 3
    }

    public enum StatusDisposition : byte
    {
        None = 0,
        New = 1,
        Merged = 2
    }

    public struct CombatOutcome
    {
        public uint EventId;
        public SemanticKey Semantic;
        public CombatEntityKey SourceOwner;
        public CombatEntityKey Target;
        public ContributorKey Contributor;
        public ushort AttackInstanceId;
        public float OccurredAt;
        public float ConfirmedAt;
        public OutcomeFlags Outcomes;
        public float HealthDamage;
        public float ShieldDamage;
        public byte NativeStatusType;
        public StatusDisposition StatusDisposition;

        public bool Processed { get { return (Outcomes & OutcomeFlags.Processed) != 0; } }
        public bool Damaged { get { return (Outcomes & OutcomeFlags.Damaged) != 0; } }
        public bool Destroyed { get { return (Outcomes & OutcomeFlags.Destroyed) != 0; } }
        public bool StatusInflicted { get { return (Outcomes & OutcomeFlags.StatusInflicted) != 0; } }
    }

    /// <summary>
    /// Token for one authored scope.  The scope can be claimed by exactly one
    /// matching RouteDamage transaction.  It must be ended in a finally block.
    /// </summary>
    public struct DamageScope
    {
        internal int Index;
        internal bool Overflow;
        public uint EventId;
        public bool IsTracked { get { return Index >= 0 && EventId != 0U; } }
    }

    private struct AuthoredScopeFrame
    {
        public bool Valid;
        public bool Claimed;
        public GameShip SourceOwnerShip;
        public GameShip PhysicalSourceShip;
        public GameShip TargetShip;
        public CombatEntityKey SourceOwner;
        public CombatEntityKey Target;
        public ContributorKey Contributor;
        public SemanticKey Semantic;
        public AcknowledgementMode Acknowledgement;
        public TrackingFlags Tracking;
        public uint EventId;
        public ushort AttackInstanceId;
        public float OccurredAt;
        public uint OwnerGeneration;
    }

    private struct RouteContext
    {
        public bool Valid;
        public bool AuthorityClaimed;
        public AuthoredScopeFrame Frame;
        public int SourceSlot;
        public uint TargetNetId;
    }

    internal struct RoutePatchState
    {
        public int Index;
        public bool Overflow;
    }

    private struct PendingKey : IEquatable<PendingKey>
    {
        public CombatEntityKey SourceOwner;
        public uint EventId;

        public bool Equals(PendingKey other)
        {
            return SourceOwner.Equals(other.SourceOwner) && EventId == other.EventId;
        }
        public override bool Equals(object obj)
        {
            return obj is PendingKey && Equals((PendingKey)obj);
        }
        public override int GetHashCode()
        {
            unchecked { return (SourceOwner.GetHashCode() * 397) ^ (int)EventId; }
        }
    }

    private struct PendingEvent
    {
        public CombatEntityKey SourceOwner;
        public CombatEntityKey Target;
        public ContributorKey Contributor;
        public SemanticKey Semantic;
        public AcknowledgementMode Acknowledgement;
        public TrackingFlags Tracking;
        public uint EventId;
        public ushort AttackInstanceId;
        public uint TargetNetId;
        public int SourceSlot;
        public uint OwnerGeneration;
        public float OccurredAt;
        public float CreatedAtUnscaled;
    }

    /// <summary>
    /// One fixed pending-correlation slot.  Slots are linked in creation order
    /// for timeout pruning and, when best-effort, in a second creation-order
    /// list for constant-time overflow eviction.  This avoids a saturated
    /// dictionary turning every new high-frequency hit into an O(N) scan.
    /// </summary>
    private struct PendingSlot
    {
        public bool Used;
        public PendingKey Key;
        public PendingEvent Event;
        public int Older;
        public int Newer;
        public int BestEffortOlder;
        public int BestEffortNewer;
    }

    internal struct CombatEventMetadata
    {
        public uint EventId;
        public SemanticKey Semantic;
        public AcknowledgementMode Acknowledgement;
        public ushort AttackInstanceId;
        public bool HasAttackInstance;
        public float AttachedAtUnscaled;
    }

    internal struct CombatResultMetadata
    {
        public uint EventId;
        public OutcomeFlags Outcomes;
        public byte NativeStatusType;
        public StatusDisposition StatusDisposition;
        public float AttachedAtUnscaled;
    }

    private const int MaxScopeDepth = 16;
    private const int MaxPendingEvents = 4096;
    private const float PendingTimeoutSeconds = 4f;
    private const float PendingPruneIntervalSeconds = 0.5f;

    private static readonly AuthoredScopeFrame[] ScopeStack =
        new AuthoredScopeFrame[MaxScopeDepth];
    private static int scopeDepth;
    private static int scopeOverflowDepth;

    private static readonly RouteContext[] RouteStack =
        new RouteContext[MaxScopeDepth];
    private static int routeDepth;
    private static int routeOverflowDepth;

    // Event ids are session-monotonic per canonical source owner.  Do NOT
    // clear an owner's sequence when its ship/runtime is replaced: the owner
    // generation is intentionally local-only and therefore cannot disambiguate
    // a late wire result from a newly reused EventId.  Full session/world Reset
    // advances sessionGeneration and is the only lifecycle boundary that clears
    // these counters.
    private static readonly Dictionary<CombatEntityKey, uint> NextEventByOwner =
        new Dictionary<CombatEntityKey, uint>(8);
    private static readonly Dictionary<CombatEntityKey, uint> OwnerGenerations =
        new Dictionary<CombatEntityKey, uint>(8);
    // Exact EventId lookup remains a dictionary, but event age/eviction order
    // is carried by fixed intrusive lists over preallocated slots.  Steady-state
    // insertion/removal/oldest eviction therefore allocates nothing and never
    // scans all 4096 pending entries merely because the table is saturated.
    private static readonly Dictionary<PendingKey, int> Pending =
        new Dictionary<PendingKey, int>(MaxPendingEvents);
    private static readonly PendingSlot[] PendingSlots =
        new PendingSlot[MaxPendingEvents];
    private static readonly int[] PendingFreeSlots =
        new int[MaxPendingEvents];
    private static int pendingFreeCount;
    private static int pendingOldest = -1;
    private static int pendingNewest = -1;
    private static int pendingBestEffortOldest = -1;
    private static int pendingBestEffortNewest = -1;
    private static bool pendingStoreInitialized;

    // Lifecycle-only owner reset still uses scratch keys.  It is deliberately
    // not part of the per-hit path.
    private static readonly List<PendingKey> PendingScratch =
        new List<PendingKey>(MaxPendingEvents);
    private static float nextPendingPruneAt;
    private static int commitDepth;
    private static bool warnedGuaranteedOutcomeUnsupported;

    private struct DeferredTargetCleanup
    {
        public float DueAtUnscaled;
        public int RuntimeInstanceId;
    }

    private const int MaxDeferredTargetCleanups = 2048;
    private const float DeferredTargetCleanupDelaySeconds = PendingTimeoutSeconds + 1f;
    private const float LifecycleMaintenanceIntervalSeconds = 0.5f;

    private static readonly Dictionary<CombatEntityKey, DeferredTargetCleanup>
        DeferredTargetCleanups =
            new Dictionary<CombatEntityKey, DeferredTargetCleanup>(128);
    private static readonly List<CombatEntityKey> DeferredTargetScratch =
        new List<CombatEntityKey>(128);
    private static float nextLifecycleMaintenanceAt;

    public static bool IsCommittingOutcome { get { return commitDepth > 0; } }

    /// <summary>
    /// Opens an authored damage scope.  SourceOwner is the canonical player /
    /// Core owner; physicalSource is the ship that native RouteDamage will
    /// actually see (drone/section/etc.). Contributor remains local and never
    /// travels on the damage wire by default.
    /// </summary>
    public static DamageScope BeginDamage(
        GameShip sourceOwner,
        GameShip target,
        SemanticKey semantic,
        ContributorKey contributor,
        AcknowledgementMode acknowledgement,
        TrackingFlags tracking,
        ushort attackInstanceId = 0,
        GameShip physicalSource = null)
    {
        RunLifecycleMaintenance(Time.unscaledTime, false);

        DamageScope token = default(DamageScope);
        token.Index = -1;

        if (scopeDepth >= MaxScopeDepth)
        {
            scopeOverflowDepth++;
            token.Overflow = true;
            return token;
        }

        CombatEntityKey ownerKey;
        CombatEntityKey targetKey;
        if (!semantic.IsValid ||
            !TryGetEntityKey(sourceOwner, out ownerKey) ||
            !TryGetEntityKey(target, out targetKey))
        {
            // Still push an invalid frame: nested unscoped damage must not claim
            // a parent scope that happens to be below this call.
            token.Index = scopeDepth;
            ScopeStack[scopeDepth++] = default(AuthoredScopeFrame);
            return token;
        }

        PrepareTargetForNewInteraction(target, targetKey);

        acknowledgement = NormalizeAcknowledgementMode(acknowledgement);

        if (physicalSource == null)
            physicalSource = sourceOwner;

        AuthoredScopeFrame frame = new AuthoredScopeFrame();
        frame.Valid = true;
        frame.SourceOwnerShip = sourceOwner;
        frame.PhysicalSourceShip = physicalSource;
        frame.TargetShip = target;
        frame.SourceOwner = ownerKey;
        frame.Target = targetKey;
        frame.Contributor = contributor;
        frame.Semantic = semantic;
        frame.Acknowledgement = acknowledgement;
        frame.Tracking = tracking;
        frame.EventId = acknowledgement == AcknowledgementMode.None
            ? 0U : NextEventId(ownerKey);
        frame.AttackInstanceId = attackInstanceId;
        frame.OccurredAt = Time.time;
        frame.OwnerGeneration = GetOwnerGeneration(ownerKey);

        token.Index = scopeDepth;
        token.EventId = frame.EventId;
        ScopeStack[scopeDepth++] = frame;
        return token;
    }

    public static DamageScope BeginDamage(
        GameShip sourceOwner,
        GameShip target,
        SemanticKey semantic,
        ContributorKey contributor)
    {
        return BeginDamage(sourceOwner, target, semantic, contributor,
            AcknowledgementMode.NativeResult, TrackingFlags.Summary, 0, sourceOwner);
    }

    public static void EndDamage(DamageScope token)
    {
        if (token.Overflow)
        {
            if (scopeOverflowDepth > 0)
                scopeOverflowDepth--;
            return;
        }

        if (token.Index < 0)
            return;

        if (scopeDepth <= 0 || token.Index != scopeDepth - 1)
        {
            // Mismatched scope lifetimes are more dangerous than lost optional
            // provenance. Fail closed and allow native gameplay to continue.
            scopeDepth = 0;
            scopeOverflowDepth = 0;
            return;
        }

        scopeDepth--;
        ScopeStack[scopeDepth] = default(AuthoredScopeFrame);
    }

    /// <summary>Cheap explicit physical-contact observation; not a fake hit.</summary>
    public static void RecordContact(
        GameShip sourceOwner,
        GameShip target,
        SemanticKey semantic,
        ContributorKey contributor,
        bool retainMeaningful = false)
    {
        RecordSemanticEvent(sourceOwner, target, semantic, contributor,
            CoreCombatHistory.SemanticObservationKind.Contact,
            retainMeaningful);
    }

    /// <summary>
    /// Cheap local semantic observation for non-damage mechanics such as gravity,
    /// aura entry, beam overlap or mark application.  The caller must choose an
    /// explicit observation kind; this never sets Processed/Damaged/Destroyed.
    /// </summary>
    public static void RecordSemanticEvent(
        GameShip sourceOwner,
        GameShip target,
        SemanticKey semantic,
        ContributorKey contributor,
        CoreCombatHistory.SemanticObservationKind kind,
        bool retainMeaningful = false)
    {
        RunLifecycleMaintenance(Time.unscaledTime, false);

        CombatEntityKey ownerKey;
        CombatEntityKey targetKey;
        if (!TryGetEntityKey(sourceOwner, out ownerKey) ||
            !TryGetEntityKey(target, out targetKey))
        {
            return;
        }

        PrepareTargetForNewInteraction(target, targetKey);

        CoreCombatHistory.RecordObservation(ownerKey, targetKey, semantic,
            contributor, kind, Time.time, retainMeaningful);
    }

    // =====================================================================
    // ROUTE / PENDING CORRELATION
    // =====================================================================

    internal static RoutePatchState BeginRouteDamage(
        object damageable,
        GameShip physicalSource,
        Activatable sourceSlot)
    {
        RoutePatchState patchState = default(RoutePatchState);
        patchState.Index = -1;

        if (routeDepth >= MaxScopeDepth)
        {
            routeOverflowDepth++;
            patchState.Overflow = true;
            return patchState;
        }

        RouteContext context = default(RouteContext);
        GameShip target = damageable as GameShip;

        // Only the top authored scope is eligible.  A nested/mismatched route
        // never searches downward and therefore cannot steal its parent scope.
        if (scopeOverflowDepth == 0 && scopeDepth > 0)
        {
            int authoredIndex = scopeDepth - 1;
            AuthoredScopeFrame frame = ScopeStack[authoredIndex];
            if (frame.Valid && !frame.Claimed &&
                ReferenceEquals(frame.TargetShip, target) &&
                ReferenceEquals(frame.PhysicalSourceShip, physicalSource))
            {
                frame.Claimed = true;
                ScopeStack[authoredIndex] = frame;

                context.Valid = true;
                context.Frame = frame;
                context.SourceSlot = sourceSlot == null ? -1 : sourceSlot.GetSlotIndex();
                context.TargetNetId = target == null ? 0U : target.netId;

                if ((frame.Tracking & TrackingFlags.Summary) != 0)
                {
                    CoreCombatHistory.RecordAttempt(frame.SourceOwner,
                        frame.Target, frame.Semantic, frame.Contributor,
                        frame.EventId, frame.OccurredAt);
                }
            }
        }

        patchState.Index = routeDepth;
        RouteStack[routeDepth++] = context;
        return patchState;
    }

    internal static void EndRouteDamage(RoutePatchState state)
    {
        if (state.Overflow)
        {
            if (routeOverflowDepth > 0)
                routeOverflowDepth--;
            return;
        }

        if (state.Index < 0)
            return;

        if (routeDepth <= 0 || state.Index != routeDepth - 1)
        {
            routeDepth = 0;
            routeOverflowDepth = 0;
            return;
        }

        routeDepth--;
        RouteStack[routeDepth] = default(RouteContext);
    }

    internal static void AttachOutgoingDamageEvent(MsgDamageEvent message)
    {
        if (message == null || message.isHeal || routeOverflowDepth > 0 || routeDepth <= 0)
            return;

        RouteContext route = RouteStack[routeDepth - 1];
        if (!route.Valid)
            return;

        if (route.TargetNetId != 0U && message.targetNetId != route.TargetNetId)
            return;
        if (route.SourceSlot >= 0 && message.sourceSlot != route.SourceSlot)
            return;

        CombatEventMetadata metadata = new CombatEventMetadata();
        metadata.EventId = route.Frame.EventId;
        metadata.Semantic = route.Frame.Semantic;
        metadata.Acknowledgement = route.Frame.Acknowledgement;
        metadata.AttackInstanceId = route.Frame.AttackInstanceId;
        metadata.HasAttackInstance = route.Frame.AttackInstanceId != 0;
        metadata.AttachedAtUnscaled = Time.unscaledTime;

        if (metadata.EventId != 0U &&
            route.Frame.Acknowledgement != AcknowledgementMode.None)
        {
            PendingEvent pending = new PendingEvent();
            pending.SourceOwner = route.Frame.SourceOwner;
            pending.Target = route.Frame.Target;
            pending.Contributor = route.Frame.Contributor;
            pending.Semantic = route.Frame.Semantic;
            pending.Acknowledgement = route.Frame.Acknowledgement;
            pending.Tracking = route.Frame.Tracking;
            pending.EventId = route.Frame.EventId;
            pending.AttackInstanceId = route.Frame.AttackInstanceId;
            pending.TargetNetId = message.targetNetId;
            pending.SourceSlot = message.sourceSlot;
            pending.OwnerGeneration = route.Frame.OwnerGeneration;
            pending.OccurredAt = route.Frame.OccurredAt;
            pending.CreatedAtUnscaled = Time.unscaledTime;

            if (!TryAddPending(pending))
                metadata.EventId = 0U;
        }

        CoreNetwork.SetCombatEventMetadata(message, metadata);
    }

    internal static void ReceiveDamageResult(MsgDamageResult message)
    {
        if (message == null)
            return;

        CombatResultMetadata metadata;
        if (!CoreNetwork.TryGetCombatResultMetadata(message, out metadata) ||
            metadata.EventId == 0U)
        {
            return;
        }

        CombatEntityKey ownerKey = ForPlayerId(message.attackerPlayerId);
        if (!ownerKey.IsValid)
            return;

        PendingKey key = new PendingKey();
        key.SourceOwner = ownerKey;
        key.EventId = metadata.EventId;

        EnsurePendingStoreInitialized();

        int pendingSlotIndex;
        if (!Pending.TryGetValue(key, out pendingSlotIndex) ||
            pendingSlotIndex < 0 || pendingSlotIndex >= MaxPendingEvents ||
            !PendingSlots[pendingSlotIndex].Used)
        {
            return;
        }

        PendingEvent pending = PendingSlots[pendingSlotIndex].Event;

        // Remove before commit.  Any nested gameplay triggered after this point
        // cannot consume or mutate the transaction currently being committed.
        RemovePendingAt(pendingSlotIndex);

        if (pending.OwnerGeneration != GetOwnerGeneration(pending.SourceOwner) ||
            pending.TargetNetId != message.targetNetId ||
            pending.SourceSlot != message.sourceSlot)
        {
            return;
        }

        CombatOutcome outcome = new CombatOutcome();
        outcome.EventId = pending.EventId;
        outcome.Semantic = pending.Semantic;
        outcome.SourceOwner = pending.SourceOwner;
        outcome.Target = pending.Target;
        outcome.Contributor = pending.Contributor;
        outcome.AttackInstanceId = pending.AttackInstanceId;
        outcome.OccurredAt = pending.OccurredAt;
        outcome.ConfirmedAt = Time.time;
        outcome.Outcomes = metadata.Outcomes | OutcomeFlags.Processed;
        outcome.HealthDamage = message.healthDamage;
        outcome.ShieldDamage = message.shieldDamage;

        if (message.healthDamage > 0f || message.shieldDamage > 0f)
            outcome.Outcomes |= OutcomeFlags.Damaged;
        if (message.destroyed)
            outcome.Outcomes |= OutcomeFlags.Destroyed;

        outcome.NativeStatusType = metadata.NativeStatusType;
        outcome.StatusDisposition = metadata.StatusDisposition;
        if (metadata.StatusDisposition != StatusDisposition.None)
            outcome.Outcomes |= OutcomeFlags.StatusInflicted;

        CommitOutcome(outcome, pending.Tracking);
    }

    private static bool TryAddPending(PendingEvent pending)
    {
        EnsurePendingStoreInitialized();
        PrunePending(false);

        PendingKey key = new PendingKey();
        key.SourceOwner = pending.SourceOwner;
        key.EventId = pending.EventId;

        int existingIndex;
        if (Pending.TryGetValue(key, out existingIndex))
            RemovePendingAt(existingIndex);

        if (pendingFreeCount <= 0)
        {
            // Force only age-head expiration work; this never scans the table.
            PrunePending(true);

            if (pendingFreeCount <= 0)
            {
                // GuaranteedOutcome entries are intentionally protected from
                // ordinary saturation eviction.  Best-effort NativeResult is
                // evicted oldest-first in O(1).
                if (pendingBestEffortOldest < 0)
                    return false;

                RemovePendingAt(pendingBestEffortOldest);
            }
        }

        if (pendingFreeCount <= 0)
            return false;

        int slotIndex = PendingFreeSlots[--pendingFreeCount];

        PendingSlot slot = default(PendingSlot);
        slot.Used = true;
        slot.Key = key;
        slot.Event = pending;
        slot.Older = pendingNewest;
        slot.Newer = -1;
        slot.BestEffortOlder = -1;
        slot.BestEffortNewer = -1;

        if (pendingNewest >= 0)
        {
            PendingSlot previous = PendingSlots[pendingNewest];
            previous.Newer = slotIndex;
            PendingSlots[pendingNewest] = previous;
        }
        else
        {
            pendingOldest = slotIndex;
        }

        pendingNewest = slotIndex;

        if (pending.Acknowledgement != AcknowledgementMode.GuaranteedOutcome)
        {
            slot.BestEffortOlder = pendingBestEffortNewest;

            if (pendingBestEffortNewest >= 0)
            {
                PendingSlot previousBest = PendingSlots[pendingBestEffortNewest];
                previousBest.BestEffortNewer = slotIndex;
                PendingSlots[pendingBestEffortNewest] = previousBest;
            }
            else
            {
                pendingBestEffortOldest = slotIndex;
            }

            pendingBestEffortNewest = slotIndex;
        }

        PendingSlots[slotIndex] = slot;
        Pending.Add(key, slotIndex);
        return true;
    }

    /// <summary>
    /// Death/reconciliation helper for sparse lifecycle events.  This is not a
    /// hot-path query: it intentionally scans the bounded pending lookup table
    /// rather than maintaining another index that every damage transaction would
    /// need to update.
    /// </summary>
    public static bool HasPendingEvent(
        CombatEntityKey sourceOwner,
        CombatEntityKey target,
        SemanticKey semantic)
    {
        if (!sourceOwner.IsValid || !target.IsValid || !semantic.IsValid)
            return false;

        EnsurePendingStoreInitialized();
        PrunePending(false);
        uint generation = GetOwnerGeneration(sourceOwner);

        foreach (KeyValuePair<PendingKey, int> pair in Pending)
        {
            int slotIndex = pair.Value;
            if (slotIndex < 0 || slotIndex >= MaxPendingEvents ||
                !PendingSlots[slotIndex].Used)
            {
                continue;
            }

            PendingEvent pending = PendingSlots[slotIndex].Event;
            if (pending.OwnerGeneration == generation &&
                pending.SourceOwner.Equals(sourceOwner) &&
                pending.Target.Equals(target) &&
                pending.Semantic.Equals(semantic))
            {
                return true;
            }
        }

        return false;
    }

    public static bool HasPendingEvent(
        GameShip sourceOwner,
        GameShip target,
        SemanticKey semantic)
    {
        CombatEntityKey ownerKey;
        CombatEntityKey targetKey;
        return TryGetEntityKey(sourceOwner, out ownerKey) &&
            TryGetEntityKey(target, out targetKey) &&
            HasPendingEvent(ownerKey, targetKey, semantic);
    }

    private static void EnsurePendingStoreInitialized()
    {
        if (pendingStoreInitialized)
            return;

        ResetPendingStore();
    }

    private static void ResetPendingStore()
    {
        Pending.Clear();
        Array.Clear(PendingSlots, 0, PendingSlots.Length);

        // Reverse fill so the first pop returns slot zero, which makes debugger
        // inspection deterministic without changing semantics.
        for (int i = 0; i < MaxPendingEvents; i++)
            PendingFreeSlots[i] = MaxPendingEvents - 1 - i;

        pendingFreeCount = MaxPendingEvents;
        pendingOldest = -1;
        pendingNewest = -1;
        pendingBestEffortOldest = -1;
        pendingBestEffortNewest = -1;
        pendingStoreInitialized = true;
    }

    private static void RemovePending(PendingKey key)
    {
        int slotIndex;
        if (Pending.TryGetValue(key, out slotIndex))
            RemovePendingAt(slotIndex);
    }

    private static void RemovePendingAt(int slotIndex)
    {
        if (slotIndex < 0 || slotIndex >= MaxPendingEvents)
            return;

        PendingSlot slot = PendingSlots[slotIndex];
        if (!slot.Used)
            return;

        Pending.Remove(slot.Key);

        if (slot.Older >= 0)
        {
            PendingSlot older = PendingSlots[slot.Older];
            older.Newer = slot.Newer;
            PendingSlots[slot.Older] = older;
        }
        else
        {
            pendingOldest = slot.Newer;
        }

        if (slot.Newer >= 0)
        {
            PendingSlot newer = PendingSlots[slot.Newer];
            newer.Older = slot.Older;
            PendingSlots[slot.Newer] = newer;
        }
        else
        {
            pendingNewest = slot.Older;
        }

        if (slot.Event.Acknowledgement != AcknowledgementMode.GuaranteedOutcome)
        {
            if (slot.BestEffortOlder >= 0)
            {
                PendingSlot olderBest = PendingSlots[slot.BestEffortOlder];
                olderBest.BestEffortNewer = slot.BestEffortNewer;
                PendingSlots[slot.BestEffortOlder] = olderBest;
            }
            else
            {
                pendingBestEffortOldest = slot.BestEffortNewer;
            }

            if (slot.BestEffortNewer >= 0)
            {
                PendingSlot newerBest = PendingSlots[slot.BestEffortNewer];
                newerBest.BestEffortOlder = slot.BestEffortOlder;
                PendingSlots[slot.BestEffortNewer] = newerBest;
            }
            else
            {
                pendingBestEffortNewest = slot.BestEffortOlder;
            }
        }

        PendingSlots[slotIndex] = default(PendingSlot);
        PendingFreeSlots[pendingFreeCount++] = slotIndex;
    }

    private static void PrunePending(bool force)
    {
        EnsurePendingStoreInitialized();

        float now = Time.unscaledTime;
        RunLifecycleMaintenance(now, force);
        if (!force && now < nextPendingPruneAt)
            return;

        nextPendingPruneAt = now + PendingPruneIntervalSeconds;

        // Every pending event has the same timeout, so creation order is also
        // expiration order.  Only expired head entries are visited; over time
        // each entry is linked once and unlinked once (amortized O(1)).
        while (pendingOldest >= 0)
        {
            PendingSlot slot = PendingSlots[pendingOldest];
            if (!slot.Used)
            {
                // This should be impossible because every removal repairs both
                // intrusive lists.  Fail closed by dropping optional pending
                // correlation rather than trying to follow invalid link data.
                ResetPendingStore();
                return;
            }

            PendingEvent pending = slot.Event;
            bool generationExpired =
                pending.OwnerGeneration != GetOwnerGeneration(pending.SourceOwner);
            bool timedOut =
                now - pending.CreatedAtUnscaled >= PendingTimeoutSeconds;

            if (!generationExpired && !timedOut)
                break;

            RemovePendingAt(pendingOldest);
        }
    }

    private static AcknowledgementMode NormalizeAcknowledgementMode(
        AcknowledgementMode acknowledgement)
    {
        if (acknowledgement != AcknowledgementMode.GuaranteedOutcome ||
            SupportsGuaranteedOutcome)
        {
            return acknowledgement;
        }

        if (!warnedGuaranteedOutcomeUnsupported)
        {
            warnedGuaranteedOutcomeUnsupported = true;
            Debug.LogWarning(
                "[CoreCombat] GuaranteedOutcome is reserved but not yet " +
                "implemented. Downgrading requests to NativeResult until a " +
                "native-safe synthetic acknowledgement path is verified.");
        }

        return AcknowledgementMode.NativeResult;
    }

    /// <summary>
    /// Called from GameShip lifecycle patches while the dying/despawning object
    /// still has a resolvable identity. Cleanup is deferred beyond the pending
    /// result timeout so a lethal routed transaction can commit before the
    /// target's lifetime-scoped history is recycled.
    /// </summary>
    internal static void NotifyTargetLifecycleEnd(GameShip target)
    {
        // Contributor-object association is no longer needed once this runtime
        // object reaches death/despawn. The assigned numeric ID itself is never
        // reused during the session, so pending/history records remain safe.
        ReleaseContributorObject(target);

        CombatEntityKey targetKey;
        if (!TryGetEntityKey(target, out targetKey))
            return;

        float nowUnscaled = Time.unscaledTime;
        RunLifecycleMaintenance(nowUnscaled, false);

        if (DeferredTargetCleanups.ContainsKey(targetKey))
            return;

        if (DeferredTargetCleanups.Count >= MaxDeferredTargetCleanups)
        {
            RunLifecycleMaintenance(nowUnscaled, true);
            if (DeferredTargetCleanups.Count >= MaxDeferredTargetCleanups)
            {
                // Lifecycle tracking is optional cleanup infrastructure. Never
                // disturb the in-flight damage transaction merely to free a
                // bookkeeping slot.
                return;
            }
        }

        DeferredTargetCleanup cleanup = new DeferredTargetCleanup();
        cleanup.DueAtUnscaled = nowUnscaled + DeferredTargetCleanupDelaySeconds;
        cleanup.RuntimeInstanceId = target == null ? 0 : target.GetInstanceID();
        DeferredTargetCleanups.Add(targetKey, cleanup);
    }

    /// <summary>
    /// A new authored interaction with a semantic target key is proof that the
    /// key is live again. If an older runtime using that same key was awaiting
    /// deferred cleanup, clear the old lifetime immediately before recording the
    /// replacement interaction so old history/state cannot bleed across lives.
    /// </summary>
    private static void PrepareTargetForNewInteraction(
        GameShip targetShip,
        CombatEntityKey target)
    {
        DeferredTargetCleanup cleanup;
        if (!DeferredTargetCleanups.TryGetValue(target, out cleanup))
            return;

        // Damage/status cascades can still run against the dying object while
        // GameShip.Destroyed is unwinding. Only a different runtime object using
        // the same semantic key proves that this is a replacement lifetime.
        if (targetShip != null &&
            cleanup.RuntimeInstanceId != 0 &&
            targetShip.GetInstanceID() == cleanup.RuntimeInstanceId &&
            targetShip.health <= 0f)
        {
            return;
        }

        DeferredTargetCleanups.Remove(target);

        // Establishing a replacement lifetime invalidates every outstanding
        // transaction authored against the previous lifetime of this semantic
        // target. EventId is session-monotonic, but a late result still carries
        // the same stable target identity; without removing the old pending
        // record it could otherwise repopulate freshly-cleared history/state.
        // Replacement is a rare lifecycle boundary, so a bounded O(N) scan is
        // intentional here and never occurs on the ordinary damage hot path.
        InvalidatePendingForTarget(target);

        CoreCombatHistory.ResetTarget(target);
        CoreCombatState.ResetTarget(target);
    }

    private static void InvalidatePendingForTarget(CombatEntityKey target)
    {
        if (!target.IsValid)
            return;

        EnsurePendingStoreInitialized();
        PendingScratch.Clear();

        foreach (KeyValuePair<PendingKey, int> pair in Pending)
        {
            int slotIndex = pair.Value;
            if (slotIndex < 0 || slotIndex >= MaxPendingEvents ||
                !PendingSlots[slotIndex].Used)
            {
                continue;
            }

            if (PendingSlots[slotIndex].Event.Target.Equals(target))
                PendingScratch.Add(pair.Key);
        }

        for (int i = 0; i < PendingScratch.Count; i++)
            RemovePending(PendingScratch[i]);

        PendingScratch.Clear();
    }

    private static void RunLifecycleMaintenance(float nowUnscaled, bool force)
    {
        CoreCombatHistory.RunMaintenance(force);

        if (!force && nowUnscaled < nextLifecycleMaintenanceAt)
            return;

        nextLifecycleMaintenanceAt =
            nowUnscaled + LifecycleMaintenanceIntervalSeconds;

        DeferredTargetScratch.Clear();
        foreach (KeyValuePair<CombatEntityKey, DeferredTargetCleanup> pair
            in DeferredTargetCleanups)
        {
            if (nowUnscaled >= pair.Value.DueAtUnscaled)
                DeferredTargetScratch.Add(pair.Key);
        }

        for (int i = 0; i < DeferredTargetScratch.Count; i++)
        {
            CombatEntityKey target = DeferredTargetScratch[i];
            DeferredTargetCleanup cleanup;
            if (!DeferredTargetCleanups.TryGetValue(target, out cleanup))
                continue;

            // Both stores are lifetime-scoped for this dead/despawned target.
            // The grace period already exceeds the supported pending timeout, so
            // correlation that is still outstanding now belongs to the expired
            // target lifetime and must be invalidated before state/history are
            // reclaimed. An ultra-late result then fails lookup harmlessly.
            InvalidatePendingForTarget(target);
            CoreCombatHistory.ResetTarget(target);
            CoreCombatState.ResetTarget(target);
            DeferredTargetCleanups.Remove(target);
        }
        DeferredTargetScratch.Clear();
    }

    // =====================================================================
    // TARGET-AUTHORITY RECEIVE / STATUS CAPTURE
    // =====================================================================

    private struct ReceivedContext
    {
        public bool Valid;
        public bool AuthorityClaimed;
        public MsgDamageEvent Event;
        public GameShip Attacker;
        public CombatEventMetadata Metadata;
        public OutcomeFlags Outcomes;
        public byte NativeStatusType;
        public StatusDisposition StatusDisposition;
    }

    internal struct ReceivedPatchState
    {
        public int Index;
        public bool Overflow;
    }

    private struct AuthorityContext
    {
        public bool Valid;
        public bool LocalSource;
        public int ReceivedIndex;
        public AuthoredScopeFrame LocalFrame;
        public GameShip Target;
        public GameShip PhysicalSource;

        // Local-authority RouteDamage does not clear lastHealthDamage /
        // lastShieldDamage before GameShip.Damage, while the native remote
        // ApplyDamageEvent path does.  Capture the actual resource baseline and
        // clear those native scratch values when this context claims a local
        // transaction.  The postfix can then distinguish a rejected early exit
        // from real damage without inheriting an earlier attack's values.
        public float HealthBefore;
        public float ShieldBefore;

        public StatusEffect DirectStatusCandidate;
        public byte DirectStatusType;
        public StatusDisposition StatusDisposition;
    }

    internal struct AuthorityPatchState
    {
        public int Index;
        public bool Overflow;
    }

    internal struct DirectStatusAddState
    {
        public bool Matched;
        public int AuthorityIndex;
        public StatusEffect Existing;
        public StatusEffect.Type Type;
    }

    private static readonly ReceivedContext[] ReceivedStack =
        new ReceivedContext[MaxScopeDepth];
    private static int receivedDepth;
    private static int receivedOverflowDepth;

    private static readonly AuthorityContext[] AuthorityStack =
        new AuthorityContext[MaxScopeDepth];
    private static int authorityDepth;
    private static int authorityOverflowDepth;

    internal static ReceivedPatchState BeginReceivedDamage(
        MsgDamageEvent message,
        GameShip attacker)
    {
        ReceivedPatchState state = default(ReceivedPatchState);
        state.Index = -1;

        if (receivedDepth >= MaxScopeDepth)
        {
            receivedOverflowDepth++;
            state.Overflow = true;
            return state;
        }

        ReceivedContext context = default(ReceivedContext);
        CombatEventMetadata metadata;
        if (message != null && !message.isHeal &&
            CoreNetwork.TryGetCombatEventMetadata(message, out metadata) &&
            metadata.Semantic.IsValid)
        {
            context.Valid = true;
            context.Event = message;
            context.Attacker = attacker;
            context.Metadata = metadata;
        }

        state.Index = receivedDepth;
        ReceivedStack[receivedDepth++] = context;
        return state;
    }

    internal static void EndReceivedDamage(ReceivedPatchState state, MsgDamageEvent message)
    {
        if (state.Overflow)
        {
            if (receivedOverflowDepth > 0)
                receivedOverflowDepth--;
            CoreNetwork.ReleaseCombatEventMetadata(message);
            return;
        }

        if (state.Index >= 0 && receivedDepth > 0 && state.Index == receivedDepth - 1)
        {
            receivedDepth--;
            ReceivedStack[receivedDepth] = default(ReceivedContext);
        }
        else if (state.Index >= 0)
        {
            receivedDepth = 0;
            receivedOverflowDepth = 0;
        }

        CoreNetwork.ReleaseCombatEventMetadata(message);
    }

    internal static AuthorityPatchState BeginAuthorityDamage(
        GameShip target,
        GameShip physicalSource)
    {
        AuthorityPatchState state = default(AuthorityPatchState);
        state.Index = -1;

        if (authorityDepth >= MaxScopeDepth)
        {
            authorityOverflowDepth++;
            state.Overflow = true;
            return state;
        }

        AuthorityContext context = default(AuthorityContext);
        context.ReceivedIndex = -1;

        // Received routed damage takes precedence on target authority.
        if (receivedOverflowDepth == 0 && receivedDepth > 0)
        {
            int receivedIndex = receivedDepth - 1;
            ReceivedContext received = ReceivedStack[receivedIndex];
            if (received.Valid && !received.AuthorityClaimed &&
                received.Event != null &&
                received.Event.targetNetId == target.netId &&
                ReferenceEquals(received.Attacker, physicalSource))
            {
                // One routed native transaction may claim this received
                // provenance exactly once. Direct/nested GameShip.Damage calls
                // that happen while ApplyDamageEvent is still on the stack do
                // not inherit the parent semantic.
                received.AuthorityClaimed = true;
                ReceivedStack[receivedIndex] = received;

                context.Valid = true;
                context.LocalSource = false;
                context.ReceivedIndex = receivedIndex;
                context.Target = target;
                context.PhysicalSource = physicalSource;
            }
        }

        // Otherwise this may be a locally-authoritative RouteDamage transaction.
        if (!context.Valid && routeOverflowDepth == 0 && routeDepth > 0)
        {
            int routeIndex = routeDepth - 1;
            RouteContext route = RouteStack[routeIndex];
            if (route.Valid && !route.AuthorityClaimed &&
                ReferenceEquals(route.Frame.TargetShip, target) &&
                ReferenceEquals(route.Frame.PhysicalSourceShip, physicalSource))
            {
                // The native authoritative Damage call is also single-claim.
                // This closes the DirectDamage/reaction path that can bypass a
                // nested RouteDamage prefix while the parent route is active.
                route.AuthorityClaimed = true;
                RouteStack[routeIndex] = route;

                context.Valid = true;
                context.LocalSource = true;
                context.LocalFrame = route.Frame;
                context.Target = target;
                context.PhysicalSource = physicalSource;

                context.HealthBefore = target.health;
                context.ShieldBefore = GetCurrentShieldValue(target);

                // Match the freshness guarantee native ApplyDamageEvent gives
                // remote-authority damage.  This is scoped only to a claimed
                // Core local transaction, so an immune/dead/invisible
                // early return cannot leave a previous attack looking current.
                target.lastHealthDamage = 0f;
                target.lastShieldDamage = 0f;
            }
        }

        state.Index = authorityDepth;
        AuthorityStack[authorityDepth++] = context;
        return state;
    }

    internal static void CompleteAuthorityDamage(
        AuthorityPatchState state,
        GameShip target,
        bool destroyed)
    {
        if (state.Overflow || state.Index < 0 ||
            state.Index >= authorityDepth || target == null)
        {
            return;
        }

        AuthorityContext context = AuthorityStack[state.Index];
        if (!context.Valid)
            return;

        OutcomeFlags outcomes = OutcomeFlags.Processed;
        float healthDamage;
        float shieldDamage;
        bool damaged;

        if (context.LocalSource)
        {
            // Local-authority Damage() has early exits before native refreshes
            // lastHealthDamage/lastShieldDamage. Those scratch fields were
            // cleared at claim time, but the authoritative Damaged decision is
            // deliberately based on actual pre/post resources, not on mutable
            // native scratch output. This prevents both stale-value false
            // positives and confirmation semantics that differ with whatever
            // native helper happened to write last*Damage on a particular path.
            healthDamage = Mathf.Max(0f, context.HealthBefore - target.health);
            shieldDamage = Mathf.Max(
                0f,
                context.ShieldBefore - GetCurrentShieldValue(target));
            damaged = healthDamage > 0f || shieldDamage > 0f;
        }
        else
        {
            // Native ApplyDamageEvent clears both fields immediately before
            // authoritative Damage(), so these values are known-fresh here and
            // are also the exact values native MsgDamageResult uses.
            healthDamage = Mathf.Max(0f, target.lastHealthDamage);
            shieldDamage = Mathf.Max(0f, target.lastShieldDamage);
            damaged = healthDamage > 0f || shieldDamage > 0f;
        }

        if (damaged)
            outcomes |= OutcomeFlags.Damaged;
        if (destroyed)
            outcomes |= OutcomeFlags.Destroyed;
        if (context.StatusDisposition != StatusDisposition.None)
            outcomes |= OutcomeFlags.StatusInflicted;

        if (context.LocalSource)
        {
            AuthoredScopeFrame frame = context.LocalFrame;
            if (frame.OwnerGeneration != GetOwnerGeneration(frame.SourceOwner))
                return;

            CombatOutcome outcome = new CombatOutcome();
            outcome.EventId = frame.EventId;
            outcome.Semantic = frame.Semantic;
            outcome.SourceOwner = frame.SourceOwner;
            outcome.Target = frame.Target;
            outcome.Contributor = frame.Contributor;
            outcome.AttackInstanceId = frame.AttackInstanceId;
            outcome.OccurredAt = frame.OccurredAt;
            outcome.ConfirmedAt = Time.time;
            outcome.Outcomes = outcomes;
            outcome.HealthDamage = healthDamage;
            outcome.ShieldDamage = shieldDamage;
            outcome.NativeStatusType = context.DirectStatusType;
            outcome.StatusDisposition = context.StatusDisposition;

            CommitOutcome(outcome, frame.Tracking);
        }
        else if (context.ReceivedIndex >= 0 &&
            context.ReceivedIndex < receivedDepth)
        {
            ReceivedContext received = ReceivedStack[context.ReceivedIndex];
            if (received.Valid)
            {
                received.Outcomes = outcomes;
                received.NativeStatusType = context.DirectStatusType;
                received.StatusDisposition = context.StatusDisposition;
                ReceivedStack[context.ReceivedIndex] = received;
            }
        }
    }

    private static float GetCurrentShieldValue(GameShip target)
    {
        return target != null && target.shield != null
            ? Mathf.Max(0f, target.shield.shield)
            : 0f;
    }

    internal static void EndAuthorityDamage(AuthorityPatchState state)
    {
        if (state.Overflow)
        {
            if (authorityOverflowDepth > 0)
                authorityOverflowDepth--;
            return;
        }

        if (state.Index < 0)
            return;

        if (authorityDepth > 0 && state.Index == authorityDepth - 1)
        {
            authorityDepth--;
            AuthorityStack[authorityDepth] = default(AuthorityContext);
        }
        else
        {
            authorityDepth = 0;
            authorityOverflowDepth = 0;
        }
    }

    internal static void CaptureGeneratedDirectStatus(StatusEffect effect, GameShip attacker)
    {
        if (effect == null || authorityOverflowDepth > 0 || authorityDepth <= 0)
            return;

        int index = authorityDepth - 1;
        AuthorityContext context = AuthorityStack[index];
        if (!context.Valid || context.DirectStatusCandidate != null ||
            !ReferenceEquals(context.PhysicalSource, attacker))
        {
            return;
        }

        context.DirectStatusCandidate = effect;
        context.DirectStatusType = (byte)effect.GetStatusEffectType();
        AuthorityStack[index] = context;
    }

    internal static DirectStatusAddState BeginDirectStatusAdd(
        GameShip target,
        StatusEffect effect)
    {
        DirectStatusAddState state = default(DirectStatusAddState);
        state.AuthorityIndex = -1;

        if (target == null || effect == null || authorityOverflowDepth > 0 || authorityDepth <= 0)
            return state;

        int index = authorityDepth - 1;
        AuthorityContext context = AuthorityStack[index];
        if (!context.Valid || !ReferenceEquals(context.Target, target) ||
            !ReferenceEquals(context.DirectStatusCandidate, effect))
        {
            return state;
        }

        state.Matched = true;
        state.AuthorityIndex = index;
        state.Type = effect.GetStatusEffectType();
        state.Existing = target.GetStatusEffect(state.Type);
        return state;
    }

    internal static void CompleteDirectStatusAdd(
        GameShip target,
        StatusEffect effect,
        DirectStatusAddState state)
    {
        if (!state.Matched || state.AuthorityIndex < 0 ||
            state.AuthorityIndex >= authorityDepth || target == null || effect == null)
        {
            return;
        }

        StatusDisposition disposition = StatusDisposition.None;
        StatusEffect current = target.GetStatusEffect(state.Type);

        if (state.Existing != null)
        {
            if (ReferenceEquals(current, state.Existing))
                disposition = StatusDisposition.Merged;
        }
        else if (ReferenceEquals(current, effect))
        {
            disposition = StatusDisposition.New;
        }

        if (disposition == StatusDisposition.None)
            return;

        AuthorityContext context = AuthorityStack[state.AuthorityIndex];
        if (!context.Valid || !ReferenceEquals(context.DirectStatusCandidate, effect))
            return;

        context.DirectStatusType = (byte)state.Type;
        context.StatusDisposition = disposition;
        AuthorityStack[state.AuthorityIndex] = context;
    }

    internal static void AttachOutgoingDamageResult(MsgDamageResult result)
    {
        if (result == null || receivedOverflowDepth > 0 || receivedDepth <= 0)
            return;

        ReceivedContext received = ReceivedStack[receivedDepth - 1];
        if (!received.Valid || received.Event == null ||
            result.targetNetId != received.Event.targetNetId ||
            result.attackerPlayerId != received.Event.attackerPlayerId ||
            result.attackerNetId != received.Event.attackerNetId ||
            result.sourceSlot != received.Event.sourceSlot)
        {
            return;
        }

        CombatResultMetadata metadata = new CombatResultMetadata();
        metadata.EventId = received.Metadata.EventId;
        metadata.Outcomes = received.Outcomes | OutcomeFlags.Processed;
        if (result.healthDamage > 0f || result.shieldDamage > 0f)
            metadata.Outcomes |= OutcomeFlags.Damaged;
        if (result.destroyed)
            metadata.Outcomes |= OutcomeFlags.Destroyed;
        metadata.NativeStatusType = received.NativeStatusType;
        metadata.StatusDisposition = received.StatusDisposition;
        if (metadata.StatusDisposition != StatusDisposition.None)
            metadata.Outcomes |= OutcomeFlags.StatusInflicted;
        metadata.AttachedAtUnscaled = Time.unscaledTime;

        CoreNetwork.SetCombatResultMetadata(result, metadata);
    }

    private static void CommitOutcome(CombatOutcome outcome, TrackingFlags tracking)
    {
        bool recordSummary = (tracking & TrackingFlags.Summary) != 0;
        bool retainMeaningful =
            (tracking & TrackingFlags.MeaningfulOutcome) != 0;

        if (!recordSummary && !retainMeaningful)
            return;

        commitDepth++;
        try
        {
            CoreCombatHistory.RecordOutcome(
                outcome, recordSummary, retainMeaningful);
        }
        finally
        {
            commitDepth--;
        }
    }

    // =====================================================================
    // LIFECYCLE / EVENT IDS
    // =====================================================================

    /// <summary>
    /// Invalidates one skill runtime for an owner without disturbing other
    /// Core skills owned by the same player. EventIds remain session-
    /// monotonic; removing the old pending records is sufficient to make any
    /// delayed results harmless because those ids are never reused this session.
    /// This is a lifecycle path and may scan the bounded pending table.
    /// </summary>
    public static void ResetOwnerSkillRuntime(
        GameShip sourceOwner,
        byte skillId)
    {
        CombatEntityKey ownerKey;
        if (!TryGetEntityKey(sourceOwner, out ownerKey))
            return;

        ResetOwnerSkillRuntime(ownerKey, skillId);
    }

    public static void ResetOwnerSkillRuntime(
        CombatEntityKey ownerKey,
        byte skillId)
    {
        if (!ownerKey.IsValid || skillId == SkillIds.None)
            return;

        EnsurePendingStoreInitialized();
        PendingScratch.Clear();
        foreach (KeyValuePair<PendingKey, int> pair in Pending)
        {
            int slotIndex = pair.Value;
            if (slotIndex < 0 || slotIndex >= MaxPendingEvents ||
                !PendingSlots[slotIndex].Used)
            {
                continue;
            }

            PendingEvent pending = PendingSlots[slotIndex].Event;
            if (pending.SourceOwner.Equals(ownerKey) &&
                pending.Semantic.SkillId == skillId)
            {
                PendingScratch.Add(pair.Key);
            }
        }
        for (int i = 0; i < PendingScratch.Count; i++)
            RemovePending(PendingScratch[i]);
        PendingScratch.Clear();

        CoreCombatState.ResetOwnerSkill(ownerKey, skillId);
        CoreCombatHistory.ResetOwnerSkill(ownerKey, skillId);
    }

    public static void ResetOwnerRuntime(GameShip sourceOwner)
    {
        CombatEntityKey ownerKey;
        if (!TryGetEntityKey(sourceOwner, out ownerKey))
            return;
        ResetOwnerRuntime(ownerKey);
    }

    public static void ResetOwnerRuntime(CombatEntityKey ownerKey)
    {
        if (!ownerKey.IsValid)
            return;

        uint generation = GetOwnerGeneration(ownerKey);
        generation++;
        if (generation == 0U) generation = 1U;
        OwnerGenerations[ownerKey] = generation;

        EnsurePendingStoreInitialized();
        PendingScratch.Clear();
        foreach (KeyValuePair<PendingKey, int> pair in Pending)
            if (pair.Key.SourceOwner.Equals(ownerKey)) PendingScratch.Add(pair.Key);
        for (int i = 0; i < PendingScratch.Count; i++) RemovePending(PendingScratch[i]);
        PendingScratch.Clear();

        // Keep NextEventByOwner intact across owner-runtime replacement.
        // EventId is on the wire but OwnerGeneration is not, so reusing low ids
        // here could let a delayed result from the old runtime consume a new
        // pending transaction with the same owner/target/source-slot identity.
        CoreCombatState.ResetOwner(ownerKey);
        CoreCombatHistory.ResetOwner(ownerKey);
    }

    public static void Reset()
    {
        sessionGeneration++;
        if (sessionGeneration == 0U) sessionGeneration = 1U;

        scopeDepth = 0;
        scopeOverflowDepth = 0;
        routeDepth = 0;
        routeOverflowDepth = 0;
        receivedDepth = 0;
        receivedOverflowDepth = 0;
        authorityDepth = 0;
        authorityOverflowDepth = 0;
        commitDepth = 0;

        Array.Clear(ScopeStack, 0, ScopeStack.Length);
        Array.Clear(RouteStack, 0, RouteStack.Length);
        Array.Clear(ReceivedStack, 0, ReceivedStack.Length);
        Array.Clear(AuthorityStack, 0, AuthorityStack.Length);

        ResetPendingStore();
        PendingScratch.Clear();

        // Full world/session teardown is the one boundary where EventId counters
        // may restart.  The native transport/session is torn down with this reset,
        // and CombatEntityKey.SessionGeneration advances for all retained local
        // state.  Owner-runtime replacement inside a live session must never clear
        // these counters because OwnerGeneration is intentionally not on the wire.
        NextEventByOwner.Clear();
        OwnerGenerations.Clear();
        nextPendingPruneAt = 0f;
        warnedGuaranteedOutcomeUnsupported = false;

        ContributorKeysByObject.Clear();
        ContributorObjectScratch.Clear();
        Array.Clear(NextContributorIdByKind, 0, NextContributorIdByKind.Length);
        warnedContributorIdExhaustion = false;
        warnedContributorObjectCapacity = false;

        DeferredTargetCleanups.Clear();
        DeferredTargetScratch.Clear();
        nextLifecycleMaintenanceAt = 0f;

        CoreCombatState.Reset();
        CoreCombatHistory.Reset();
    }

    private static uint NextEventId(CombatEntityKey owner)
    {
        uint value;
        if (!NextEventByOwner.TryGetValue(owner, out value))
            value = 0U;
        value++;
        if (value == 0U) value++;
        NextEventByOwner[owner] = value;
        return value;
    }

    private static uint GetOwnerGeneration(CombatEntityKey owner)
    {
        uint generation;
        if (!OwnerGenerations.TryGetValue(owner, out generation))
        {
            generation = 1U;
            OwnerGenerations[owner] = generation;
        }
        return generation;
    }
}

// =============================================================================
// HARMONY INTEGRATION
// =============================================================================

/// <summary>
/// Claims a single authored scope at the exact native RouteDamage transaction.
/// The internal IDamageable type is intentionally not named in the patch method.
/// </summary>
[HarmonyPatch]
public static class CoreCombatRouteDamagePatch
{
    public static MethodBase TargetMethod()
    {
        MethodInfo[] methods = typeof(NetCombat).GetMethods(
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        for (int i = 0; i < methods.Length; i++)
        {
            MethodInfo method = methods[i];
            if (method.Name != "RouteDamage") continue;
            ParameterInfo[] p = method.GetParameters();
            if (p.Length == 14 &&
                p[2].ParameterType == typeof(Damageable.DamageData[]))
            {
                return method;
            }
        }
        return null;
    }

    internal static void Prefix(
        object __0,
        GameShip __6,
        Activatable __9,
        out CoreCombat.RoutePatchState __state)
    {
        __state = CoreCombat.BeginRouteDamage(__0, __6, __9);
    }

    internal static Exception Finalizer(
        Exception __exception,
        CoreCombat.RoutePatchState __state)
    {
        CoreCombat.EndRouteDamage(__state);
        return __exception;
    }
}

[HarmonyPatch(typeof(NetSession), "SendDamageEvent")]
public static class CoreCombatSendDamageEventPatch
{
    internal static void Prefix(MsgDamageEvent __0)
    {
        CoreCombat.AttachOutgoingDamageEvent(__0);
    }
}

[HarmonyPatch(typeof(NetCombat), "ApplyDamageEvent")]
public static class CoreCombatApplyDamageEventPatch
{
    internal static void Prefix(
        MsgDamageEvent __0,
        object __1,
        GameShip __2,
        out CoreCombat.ReceivedPatchState __state)
    {
        __state = CoreCombat.BeginReceivedDamage(__0, __2);
    }

    internal static Exception Finalizer(
        Exception __exception,
        MsgDamageEvent __0,
        CoreCombat.ReceivedPatchState __state)
    {
        CoreCombat.EndReceivedDamage(__state, __0);
        return __exception;
    }
}

[HarmonyPatch(typeof(GameShip), "Damage")]
public static class CoreCombatGameShipDamagePatch
{
    internal static void Prefix(
        GameShip __instance,
        GameShip __5,
        out CoreCombat.AuthorityPatchState __state)
    {
        __state = CoreCombat.BeginAuthorityDamage(__instance, __5);
    }

    internal static void Postfix(
        GameShip __instance,
        bool __result,
        CoreCombat.AuthorityPatchState __state)
    {
        CoreCombat.CompleteAuthorityDamage(__state, __instance, __result);
    }

    internal static Exception Finalizer(
        Exception __exception,
        CoreCombat.AuthorityPatchState __state)
    {
        CoreCombat.EndAuthorityDamage(__state);
        return __exception;
    }
}

[HarmonyPatch(typeof(StatusEffect), "GetEffectForDamageType")]
public static class CoreCombatDirectStatusCreatePatch
{
    internal static void Postfix(GameShip __2, StatusEffect __result)
    {
        CoreCombat.CaptureGeneratedDirectStatus(__result, __2);
    }
}

[HarmonyPatch(typeof(GameShip), "AddStatusEffect")]
public static class CoreCombatDirectStatusAddPatch
{
    internal static void Prefix(
        GameShip __instance,
        StatusEffect __0,
        out CoreCombat.DirectStatusAddState __state)
    {
        __state = CoreCombat.BeginDirectStatusAdd(__instance, __0);
    }

    internal static void Postfix(
        GameShip __instance,
        StatusEffect __0,
        CoreCombat.DirectStatusAddState __state)
    {
        CoreCombat.CompleteDirectStatusAdd(__instance, __0, __state);
    }
}

[HarmonyPatch(typeof(NetSession), "SendDamageResult")]
public static class CoreCombatSendDamageResultPatch
{
    internal static void Prefix(MsgDamageResult __0)
    {
        CoreCombat.AttachOutgoingDamageResult(__0);
    }
}

[HarmonyPatch(typeof(NetWorldBridge), "OnDamageResult")]
public static class CoreCombatDamageResultPatch
{
    internal static void Postfix(MsgDamageResult __0)
    {
        try
        {
            CoreCombat.ReceiveDamageResult(__0);
        }
        finally
        {
            CoreNetwork.ReleaseCombatResultMetadata(__0);
        }
    }
}

/// <summary>
/// Capture the target identity before native destruction tears down or replaces
/// the runtime object. Cleanup is deferred by CoreCombat so the lethal
/// transaction and any in-flight native result can finish first.
/// </summary>
[HarmonyPatch(typeof(GameShip), "Destroyed")]
public static class CoreCombatTargetDestroyedLifecyclePatch
{
    internal static void Prefix(GameShip __instance)
    {
        CoreCombat.NotifyTargetLifecycleEnd(__instance);
    }
}

/// <summary>
/// Also cover despawn/rebuild paths that destroy a GameShip without passing
/// through gameplay death. Duplicate notifications are coalesced by target key.
/// </summary>
[HarmonyPatch(typeof(GameShip), "OnDestroy")]
public static class CoreCombatTargetOnDestroyLifecyclePatch
{
    internal static void Prefix(GameShip __instance)
    {
        CoreCombat.NotifyTargetLifecycleEnd(__instance);
    }
}
