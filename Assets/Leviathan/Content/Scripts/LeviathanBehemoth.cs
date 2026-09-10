using HarmonyLib;
using StarVortex;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

/// <summary>
/// Behemoth mechanics and specialization runtime.
///
/// Ownership:
/// - native five-rank Behemoth passive tuning,
/// - segment-transfer reduction/discard values consumed by LeviathanMod,
/// - ordinary per-segment Hull regeneration using Growth ScalingSegmentCount,
/// - Temporal Dive knobs/flag, Temporal Drive transformation and field runtime,
/// - Behemoth resolved-state caching and lifecycle cleanup.
///
/// Growth is the sole anatomy authority. Behemoth never reconstructs anatomy
/// from Squadron/controller state. Segment-specific protection is selected by
/// LeviathanMod from the exact hit role; Behemoth only supplies the protection
/// values after that semantic gate has already been passed.
/// </summary>
public static class LeviathanBehemoth
{
    public const int MaxRank = 5;

    // =========================================================================
    // TUNING
    // =========================================================================

    public static class Tuning
    {
        // Leviathan's redirected Body/Tail damage baseline before Behemoth.
        public const float BaseSegmentDamageMultiplier = 0.40f;

        // Rank 1 -> Rank 5. Preserved from the current LIVE Behemoth source.
        internal static readonly float[] SegmentDamageMultiplierByRank =
        {
            0.35f,
            0.30f,
            0.25f,
            0.23f,
            0.20f
        };

        internal static readonly float[] SegmentDebuffDiscardChanceByRank =
        {
            0.20f,
            0.25f,
            0.30f,
            0.35f,
            0.40f
        };

        internal static readonly float[] StaticHullRegenPerSecondByRank =
        {
            0.00f,
            0.00f,
            0.00f,
            0.00f,
            0.00f
        };

        // Ordinary player-facing "per segment" scaling. Under the canonical
        // anatomy contract this means ScalingSegmentCount: every live section
        // except the primary Head, including future additional Heads.
        internal static readonly float[] StaticHullRegenPerScalingSegmentPerSecondByRank =
        {
            2.50f,
            3.00f,
            3.50f,
            4.50f,
            6.50f
        };

        // Authored as percent of max Hull regenerated per second.
        internal static readonly float[] MaxHullRegenPercentPerSecondByRank =
        {
            0.30f,
            0.50f,
            0.60f,
            0.80f,
            1.00f
        };

        // Temporal Dive baseline. Preserved from the existing Behemoth-owned
        // implementation. Tree modifiers, when added, apply to these baselines.
        //
        // IMPORTANT: the legacy implementation compares this authored 180
        // directly against Star Vortex position-space distance. Do not insert a
        // WorldUnitsPerMeter conversion as part of an architectural refactor;
        // that is a deliberate balance/units migration if we choose it later.
        public const float TemporalDiveRadiusMeters = 180f;
        public const float TemporalDiveMinimumFactor = 0.65f;
        public const float TemporalDiveFalloffPower = 1f;
        public const float TemporalDiveDurationSeconds = 20f;
        public const float TemporalDiveCooldownSeconds = 30f;
    }

    // =========================================================================
    // SPECIALIZATION INTERFACE
    // =========================================================================

    public static class Knobs
    {
        public static readonly LeviathanSpecializationKnob TemporalDiveRadius =
            LeviathanSpecializationKnob.Flat(
                "behemoth.temporal_dive.radius",
                "Temporal Dive Radius",
                "m"
            );

        public static readonly LeviathanSpecializationKnob TemporalDiveMinimumFactor =
            LeviathanSpecializationKnob.Flat(
                "behemoth.temporal_dive.minimum_factor",
                "Temporal Dive Minimum Time Factor",
                "x"
            );

        public static readonly LeviathanSpecializationKnob TemporalDiveFalloffPower =
            LeviathanSpecializationKnob.Flat(
                "behemoth.temporal_dive.falloff_power",
                "Temporal Dive Falloff Power"
            );

        public static readonly LeviathanSpecializationKnob TemporalDiveDuration =
            LeviathanSpecializationKnob.Flat(
                "behemoth.temporal_dive.duration",
                "Temporal Dive Duration",
                "s"
            );

        public static readonly LeviathanSpecializationKnob TemporalDiveCooldown =
            LeviathanSpecializationKnob.Flat(
                "behemoth.temporal_dive.cooldown",
                "Temporal Dive Cooldown",
                "s"
            );
    }

    public static class Flags
    {
        public static readonly LeviathanSpecializationFlag TemporalDive =
            LeviathanSpecializationFlag.Create(
                "behemoth.temporal_dive",
                "Temporal Dive"
            );
    }

    // =========================================================================
    // RESOLVED STATE
    // =========================================================================

    /// <summary>
    /// Single resolved source of truth for Behemoth runtime behavior.
    ///
    /// Passive rank behavior is valid whenever native Behemoth is active on an
    /// active Leviathan chassis. Specialization-dependent fields are resolved
    /// only for the local owner or an exact remote replica whose specialization
    /// has been synchronized by LeviathanNetwork.
    /// </summary>
    public sealed class ResolvedState
    {
        public bool Active;
        public int Rank;

        public int AnatomyRevision;
        public int ScalingSegmentCount;

        public float SegmentDamageMultiplier;
        public float SegmentDebuffDiscardChance;

        public float FlatHullRegenPerSecond;
        public float MaxHullRegenFractionPerSecond;

        public bool TemporalDiveEnabled;
        public float TemporalDiveRadiusMeters;
        public float TemporalDiveMinimumFactor;
        public float TemporalDiveFalloffPower;
        public float TemporalDiveDurationSeconds;
        public float TemporalDiveCooldownSeconds;
    }

    private sealed class ResolvedCacheEntry
    {
        public GameShip Ship;
        public int Rank;
        public bool GrowthActive;
        public int AnatomyRevision;
        public int ScalingSegmentCount;
        public bool SpecializationAvailable;
        public int ConfigurationRevision;
        public int RegistryRevision;
        public ResolvedState State;
    }

    private static readonly Dictionary<Pilot, ResolvedCacheEntry> resolvedByPilot =
        new Dictionary<Pilot, ResolvedCacheEntry>();

    private static readonly ResolvedState inactiveState = CreateInactiveState();

    // =========================================================================
    // PUBLIC API
    // =========================================================================

    public static ResolvedState GetResolvedState(GameShip ship)
    {
        if (ship == null)
            return inactiveState;

        Pilot pilot = GameShip.GetPlayerSourcePilot(ship);
        if (pilot == null)
            return inactiveState;

        int rank = Mathf.Clamp(
            pilot.GetUpgradeLevel(LeviathanMod.BehemothUpgrade),
            0,
            MaxRank
        );

        LeviathanGrowth.ResolvedState growth =
            LeviathanGrowth.GetResolvedState(ship);

        bool growthActive = growth != null && growth.Active;

        LeviathanGrowth.AnatomySnapshot anatomy =
            LeviathanGrowth.GetAnatomy(ship);

        int anatomyRevision = anatomy == null ? -1 : anatomy.Revision;
        int scalingSegmentCount =
            growthActive && anatomy != null
                ? Mathf.Max(0, anatomy.ScalingSegmentCount)
                : 0;

        bool specializationAvailable = CanResolveSpecialization(ship);

        // Ensure the compiled specialization registry is current before taking
        // the revision stamps used by this cache entry. Otherwise the first
        // HasFlag/ApplyKnob call could register defaults after we stamped it.
        if (specializationAvailable)
            LeviathanSpecializationRuntime.RegisterDefaults();

        int configurationRevision = specializationAvailable
            ? LeviathanSpecializationRuntime.ConfigurationRevision
            : -1;
        int registryRevision = specializationAvailable
            ? LeviathanSpecializationRegistry.Revision
            : -1;

        ResolvedCacheEntry cached;
        if (resolvedByPilot.TryGetValue(pilot, out cached) &&
            cached != null &&
            object.ReferenceEquals(cached.Ship, ship) &&
            cached.Rank == rank &&
            cached.GrowthActive == growthActive &&
            cached.AnatomyRevision == anatomyRevision &&
            cached.ScalingSegmentCount == scalingSegmentCount &&
            cached.SpecializationAvailable == specializationAvailable &&
            cached.ConfigurationRevision == configurationRevision &&
            cached.RegistryRevision == registryRevision &&
            cached.State != null)
        {
            return cached.State;
        }

        ResolvedState state = BuildResolvedState(
            pilot,
            rank,
            growthActive,
            anatomyRevision,
            scalingSegmentCount,
            specializationAvailable
        );

        if (cached == null)
        {
            cached = new ResolvedCacheEntry();
            resolvedByPilot[pilot] = cached;
        }

        cached.Ship = ship;
        cached.Rank = rank;
        cached.GrowthActive = growthActive;
        cached.AnatomyRevision = anatomyRevision;
        cached.ScalingSegmentCount = scalingSegmentCount;
        cached.SpecializationAvailable = specializationAvailable;
        cached.ConfigurationRevision = configurationRevision;
        cached.RegistryRevision = registryRevision;
        cached.State = state;

        return state;
    }

    /// <summary>
    /// Current semantic API consumed by Body/Tail damage transfer.
    /// Rank 0 intentionally returns the Leviathan chassis baseline of 0.40x.
    /// The caller is responsible for checking that the exact hit section is
    /// segment-protection eligible.
    /// </summary>
    public static float GetSegmentDamageMultiplier(GameShip ship)
    {
        return Mathf.Clamp01(GetResolvedState(ship).SegmentDamageMultiplier);
    }

    /// <summary>
    /// Current semantic API consumed by Body/Tail negative-status transfer.
    /// The caller is responsible for exact hit-role eligibility.
    /// </summary>
    public static float GetSegmentDebuffDiscardChance(GameShip ship)
    {
        ResolvedState state = GetResolvedState(ship);
        return state.Active
            ? Mathf.Clamp01(state.SegmentDebuffDiscardChance)
            : 0f;
    }

    public static int GetRank(GameShip ship)
    {
        if (ship == null)
            return 0;

        Pilot pilot = GameShip.GetPlayerSourcePilot(ship);
        return pilot == null
            ? 0
            : Mathf.Clamp(
                pilot.GetUpgradeLevel(LeviathanMod.BehemothUpgrade),
                0,
                MaxRank
            );
    }

    public static void Reset()
    {
        resolvedByPilot.Clear();
    }

    /// <summary>
    /// Release cached state for one replica/Pilot. RemoteShipDriver can rebuild
    /// a player's replica without destroying the whole world, so a per-Pilot
    /// cache must not wait for world teardown to release the replaced Pilot.
    /// </summary>
    public static void Forget(GameShip ship)
    {
        if (ship == null)
            return;

        Pilot pilot = GameShip.GetPlayerSourcePilot(ship);
        if (pilot != null)
            resolvedByPilot.Remove(pilot);
    }

    // =========================================================================
    // RESOLUTION
    // =========================================================================

    private static ResolvedState BuildResolvedState(
        Pilot pilot,
        int rank,
        bool growthActive,
        int anatomyRevision,
        int scalingSegmentCount,
        bool specializationAvailable)
    {
        ResolvedState state = CreateInactiveState();
        state.AnatomyRevision = anatomyRevision;

        rank = Mathf.Clamp(rank, 0, MaxRank);

        // Behemoth belongs to the Leviathan chassis. A stale native Behemoth
        // rank without an active Growth/Evolution chassis must not affect a
        // normal ship.
        if (!growthActive || rank < 1)
            return state;

        state.Active = true;
        state.Rank = rank;
        state.ScalingSegmentCount = Mathf.Max(0, scalingSegmentCount);

        state.SegmentDamageMultiplier = Mathf.Clamp01(
            GetRankValue(Tuning.SegmentDamageMultiplierByRank, rank)
        );

        state.SegmentDebuffDiscardChance = Mathf.Clamp01(
            GetRankValue(Tuning.SegmentDebuffDiscardChanceByRank, rank)
        );

        state.FlatHullRegenPerSecond =
            GetRankValue(Tuning.StaticHullRegenPerSecondByRank, rank) +
            state.ScalingSegmentCount *
            GetRankValue(
                Tuning.StaticHullRegenPerScalingSegmentPerSecondByRank,
                rank
            );

        state.MaxHullRegenFractionPerSecond = Mathf.Max(
            0f,
            GetRankValue(Tuning.MaxHullRegenPercentPerSecondByRank, rank) /
            100f
        );

        // Tree state is trusted only for the local owner or an exact remote
        // replica whose transient specialization has been synchronized.
        if (!specializationAvailable || pilot == null)
            return state;

        state.TemporalDiveEnabled =
            LeviathanSpecializationRuntime.HasFlag(pilot, Flags.TemporalDive);

        if (!state.TemporalDiveEnabled)
            return state;

        state.TemporalDiveRadiusMeters = Mathf.Max(
            1f,
            LeviathanSpecializationRuntime.ApplyKnob(
                pilot,
                Knobs.TemporalDiveRadius,
                Tuning.TemporalDiveRadiusMeters
            )
        );

        state.TemporalDiveMinimumFactor = Mathf.Clamp(
            LeviathanSpecializationRuntime.ApplyKnob(
                pilot,
                Knobs.TemporalDiveMinimumFactor,
                Tuning.TemporalDiveMinimumFactor
            ),
            0.05f,
            1f
        );

        state.TemporalDiveFalloffPower = Mathf.Max(
            0.05f,
            LeviathanSpecializationRuntime.ApplyKnob(
                pilot,
                Knobs.TemporalDiveFalloffPower,
                Tuning.TemporalDiveFalloffPower
            )
        );

        state.TemporalDiveDurationSeconds = Mathf.Max(
            0.1f,
            LeviathanSpecializationRuntime.ApplyKnob(
                pilot,
                Knobs.TemporalDiveDuration,
                Tuning.TemporalDiveDurationSeconds
            )
        );

        state.TemporalDiveCooldownSeconds = Mathf.Max(
            0f,
            LeviathanSpecializationRuntime.ApplyKnob(
                pilot,
                Knobs.TemporalDiveCooldown,
                Tuning.TemporalDiveCooldownSeconds
            )
        );

        return state;
    }

    private static ResolvedState CreateInactiveState()
    {
        ResolvedState state = new ResolvedState();
        state.AnatomyRevision = -1;
        state.SegmentDamageMultiplier = Tuning.BaseSegmentDamageMultiplier;
        state.TemporalDiveRadiusMeters = Tuning.TemporalDiveRadiusMeters;
        state.TemporalDiveMinimumFactor = Tuning.TemporalDiveMinimumFactor;
        state.TemporalDiveFalloffPower = Tuning.TemporalDiveFalloffPower;
        state.TemporalDiveDurationSeconds = Tuning.TemporalDiveDurationSeconds;
        state.TemporalDiveCooldownSeconds = Tuning.TemporalDiveCooldownSeconds;
        return state;
    }

    private static float GetRankValue(float[] values, int rank)
    {
        if (values == null || values.Length == 0 || rank < 1)
            return 0f;

        int index = Mathf.Clamp(rank, 1, values.Length) - 1;
        return values[index];
    }

    private static bool CanResolveSpecialization(GameShip ship)
    {
        if (ship == null)
            return false;

        GameShip localPlayer = WorldController.instance == null
            ? null
            : WorldController.instance.GetCurrentPlayerShip();

        if (localPlayer != null && object.ReferenceEquals(localPlayer, ship))
            return true;

        return NetSession.InSession &&
            LeviathanNetwork.HasSynchronizedSpecialization(ship);
    }

    // =========================================================================
    // NATIVE STAT BOUNDARY
    // =========================================================================

    internal static bool TryGetLocalActiveState(
        object instance,
        out GameShip ship,
        out ResolvedState state)
    {
        ship = instance as GameShip;
        state = null;

        if (ship == null)
            return false;

        // Do not infer "segment" membership here. This patch belongs only to
        // the exact local player ship (the primary Head), so equality with the
        // local player is the complete and future-safe ownership gate.
        GameShip localPlayer = WorldController.instance == null
            ? null
            : WorldController.instance.GetCurrentPlayerShip();

        if (localPlayer == null ||
            !object.ReferenceEquals(localPlayer, ship))
        {
            return false;
        }

        state = GetResolvedState(ship);
        return state != null && state.Active;
    }

    internal static MethodBase FindHealthRegenGetter()
    {
        return AccessTools.Method(typeof(GameShip), "get_HealthRegen") ??
            AccessTools.Method(typeof(Damageable), "get_HealthRegen");
    }
}

// =============================================================================
// TEMPORAL DIVE
// =============================================================================

/// <summary>
/// Behemoth's Temporal Dive transforms the locally controlled equipped
/// Temporal Drive instead of replacing its native activatable lifecycle.
///
/// Native Temporal Drive remains responsible for input, active state, charges,
/// audio, EffectDuration/Cooldown modifiers, SetCooldowns and cancellation.
/// Leviathan rewrites only the multiplayer global-dilation request into a
/// reliable owner-tagged carrier, consumes that carrier at NetWorldBridge, and
/// applies the local hostile-time field.
///
/// No Behemoth dynamic slot is required by the current implementation: the
/// native TemporalActivate message is the verified irreducible activation/cancel
/// event, while build-dependent field parameters are derived from synchronized
/// specialization whenever the exact remote replica is ready.
/// </summary>
public static class LeviathanTemporalDiveRuntime
{
    // A TemporalActivate request with exactly 1x factor and positive duration
    // has no useful vanilla dilation effect. It is reserved as the Dive carrier
    // and intercepted before NetWorldBridge creates a global contribution.
    private const float NetworkMarkerTimeScale = 1f;

    private sealed class DiveState
    {
        public int OwnerPlayerId;
        public float Remaining;
        public float Radius;
        public float MinimumFactor;
        public float FalloffPower;
    }

    public struct ShipTimeState
    {
        public bool Changed;
        public float OriginalSpeedScale;
    }

    private static readonly Dictionary<int, DiveState> activeDives =
        new Dictionary<int, DiveState>();

    private static readonly Dictionary<int, float> shipFactors =
        new Dictionary<int, float>();

    private static readonly Dictionary<int, float> projectileFactors =
        new Dictionary<int, float>();

    private static readonly List<int> expiredOwnerScratch =
        new List<int>();

    private static NetWorldBridge currentBridge;
    private static TemporalDrive temporalDriveActivationSource;

    public static void Reset()
    {
        activeDives.Clear();
        shipFactors.Clear();
        projectileFactors.Clear();
        expiredOwnerScratch.Clear();
        currentBridge = null;
        temporalDriveActivationSource = null;
    }

    // -------------------------------------------------------------------------
    // Native Temporal Drive transformation
    // -------------------------------------------------------------------------

    public static bool ShouldTransformTemporalDrive(TemporalDrive drive)
    {
        // The current Temporal Dive carrier is the verified multiplayer route.
        // Do not partially transform single-player Temporal Drive behavior: in
        // single player native AddEffect applies TimeWarped directly to the ship.
        if (drive == null ||
            !NetSession.InSession ||
            WorldController.instance == null)
        {
            return false;
        }

        GameShip player = drive.parentShip;
        GameShip localPlayer = WorldController.instance.GetCurrentPlayerShip();

        if (player == null ||
            localPlayer == null ||
            !object.ReferenceEquals(player, localPlayer))
        {
            return false;
        }

        LeviathanBehemoth.ResolvedState state =
            LeviathanBehemoth.GetResolvedState(player);

        return state != null &&
            state.Active &&
            state.TemporalDiveEnabled;
    }

    public static float TransformTemporalDriveDuration(
        TemporalDrive drive,
        float nativeModifiedDuration)
    {
        if (!ShouldTransformTemporalDrive(drive))
            return nativeModifiedDuration;

        // Preserve the exact native EffectDuration modifier ratio and apply it
        // to Temporal Dive's own authored baseline.
        float nativeBase = Mathf.Max(0.001f, drive.BaseDuration);
        float modifierRatio = nativeModifiedDuration / nativeBase;

        LeviathanBehemoth.ResolvedState state =
            LeviathanBehemoth.GetResolvedState(drive.parentShip);

        return Mathf.Max(
            0.1f,
            state.TemporalDiveDurationSeconds * modifierRatio
        );
    }

    public static float TransformTemporalDriveCooldown(
        TemporalDrive drive,
        float nativeModifiedCooldown)
    {
        if (!ShouldTransformTemporalDrive(drive))
            return nativeModifiedCooldown;

        // Preserve the exact native Cooldown modifier ratio and apply it to
        // Temporal Dive's own authored baseline. Native RemoveEffect/SetCooldowns
        // remains the source of truth for the timer itself.
        float nativeBase = Mathf.Max(0.001f, drive.BaseCooldown);
        float modifierRatio = nativeModifiedCooldown / nativeBase;

        LeviathanBehemoth.ResolvedState state =
            LeviathanBehemoth.GetResolvedState(drive.parentShip);

        return Mathf.Max(
            0f,
            state.TemporalDiveCooldownSeconds * modifierRatio
        );
    }

    public static bool BeginTemporalDriveActivation(TemporalDrive drive)
    {
        if (!ShouldTransformTemporalDrive(drive))
            return false;

        temporalDriveActivationSource = drive;
        return true;
    }

    public static void EndTemporalDriveActivation(TemporalDrive drive)
    {
        if (object.ReferenceEquals(temporalDriveActivationSource, drive))
            temporalDriveActivationSource = null;
    }

    public static void RewriteTemporalDriveNetworkRequest(
        ref float timeScale,
        ref float duration)
    {
        TemporalDrive drive = temporalDriveActivationSource;
        if (drive == null || !ShouldTransformTemporalDrive(drive))
            return;

        timeScale = NetworkMarkerTimeScale;
        duration = Mathf.Clamp(drive.Duration, 0.1f, 30f);
    }

    // -------------------------------------------------------------------------
    // Carrier / network lifecycle
    // -------------------------------------------------------------------------

    public static bool IsNetworkCarrier(MsgTemporalActivate temporal)
    {
        return temporal != null &&
            !temporal.cancel &&
            temporal.duration > 0f &&
            Mathf.Approximately(
                temporal.timeScale,
                NetworkMarkerTimeScale
            );
    }

    public static bool TryBeginNetworkDive(
        NetWorldBridge bridge,
        MsgTemporalActivate temporal)
    {
        if (bridge == null || !IsNetworkCarrier(temporal))
            return false;

        currentBridge = bridge;

        GameShip owner = bridge.ResolvePlayerShip(temporal.ownerPlayerId);

        // A 1x native Temporal Drive request is normally harmless, so do not
        // reinterpret one as Behemoth when this exact owner build is already
        // known and says Temporal Dive is disabled. If a remote replica/spec is
        // not ready yet, the owner-tagged carrier is still the best available
        // authoritative signal and we accept it with baseline field parameters.
        if (owner != null && HasKnownSpecialization(owner))
        {
            LeviathanBehemoth.ResolvedState resolved =
                LeviathanBehemoth.GetResolvedState(owner);

            if (resolved == null ||
                !resolved.Active ||
                !resolved.TemporalDiveEnabled)
            {
                return false;
            }
        }

        DiveState state = new DiveState();
        state.OwnerPlayerId = temporal.ownerPlayerId;
        state.Remaining = Mathf.Clamp(temporal.duration, 0.1f, 30f);

        ResolveFieldParameters(
            owner,
            out state.Radius,
            out state.MinimumFactor,
            out state.FalloffPower
        );

        activeDives[temporal.ownerPlayerId] = state;
        return true;
    }

    public static bool TryCancelNetworkDive(MsgTemporalActivate temporal)
    {
        if (temporal == null || !temporal.cancel)
            return false;

        return activeDives.Remove(temporal.ownerPlayerId);
    }

    public static void UpdateNetwork(NetWorldBridge bridge)
    {
        currentBridge = bridge;

        // NetWorldBridge's native temporal contributions also consume
        // Time.deltaTime rather than the supplied unscaled delta. Match that
        // verified lifecycle so Dive duration stays aligned if vanilla global
        // temporal dilation is simultaneously active in the star.
        Tick(Time.deltaTime);
    }

    private static void Tick(float deltaTime)
    {
        if (activeDives.Count == 0)
            return;

        expiredOwnerScratch.Clear();

        foreach (KeyValuePair<int, DiveState> pair in activeDives)
        {
            DiveState state = pair.Value;
            state.Remaining -= deltaTime;

            if (state.Remaining <= 0f)
                expiredOwnerScratch.Add(pair.Key);
        }

        for (int i = 0; i < expiredOwnerScratch.Count; i++)
            activeDives.Remove(expiredOwnerScratch[i]);

        expiredOwnerScratch.Clear();
    }

    private static bool HasKnownSpecialization(GameShip owner)
    {
        if (owner == null)
            return false;

        GameShip localPlayer = WorldController.instance == null
            ? null
            : WorldController.instance.GetCurrentPlayerShip();

        if (localPlayer != null && object.ReferenceEquals(localPlayer, owner))
            return true;

        return NetSession.InSession &&
            LeviathanNetwork.HasSynchronizedSpecialization(owner);
    }

    private static void ResolveFieldParameters(
        GameShip owner,
        out float radius,
        out float minimumFactor,
        out float falloffPower)
    {
        // The carrier itself proves an authoritative Dive activation. If the
        // exact remote specialization has not arrived yet, use the shared
        // baseline for this activation rather than consulting local persistence
        // or guessing remote tree state. Once synchronization exists, derive
        // future activations through the ordinary shared resolver.
        if (HasKnownSpecialization(owner))
        {
            LeviathanBehemoth.ResolvedState resolved =
                LeviathanBehemoth.GetResolvedState(owner);

            if (resolved != null &&
                resolved.Active &&
                resolved.TemporalDiveEnabled)
            {
                radius = resolved.TemporalDiveRadiusMeters;
                minimumFactor = resolved.TemporalDiveMinimumFactor;
                falloffPower = resolved.TemporalDiveFalloffPower;
                return;
            }
        }

        radius = LeviathanBehemoth.Tuning.TemporalDiveRadiusMeters;
        minimumFactor = LeviathanBehemoth.Tuning.TemporalDiveMinimumFactor;
        falloffPower = LeviathanBehemoth.Tuning.TemporalDiveFalloffPower;
    }

    // -------------------------------------------------------------------------
    // Field evaluation
    // -------------------------------------------------------------------------

    private static float GetTemporalFactor(
        Vector2 position,
        string targetFaction)
    {
        if (!NetSession.InSession || currentBridge == null)
            return 1f;

        float strongest = 1f;

        foreach (KeyValuePair<int, DiveState> pair in activeDives)
        {
            DiveState state = pair.Value;
            GameShip owner = currentBridge.ResolvePlayerShip(state.OwnerPlayerId);

            if (!owner || owner.health <= 0f)
                continue;

            if (!Faction.IsHostile(owner.faction, targetFaction))
                continue;

            strongest = Mathf.Min(
                strongest,
                EvaluateField(
                    owner.transform.position,
                    position,
                    state
                )
            );
        }

        return strongest;
    }

    private static float EvaluateField(
        Vector2 center,
        Vector2 position,
        DiveState state)
    {
        float distance = Vector2.Distance(center, position);
        if (distance >= state.Radius)
            return 1f;

        float normalized = Mathf.Clamp01(distance / state.Radius);
        float smooth = normalized * normalized * (3f - 2f * normalized);
        smooth = Mathf.Pow(smooth, state.FalloffPower);

        return Mathf.Lerp(state.MinimumFactor, 1f, smooth);
    }

    // -------------------------------------------------------------------------
    // Ship / projectile application
    // -------------------------------------------------------------------------

    public static ShipTimeState BeginShipFrame(GameShip ship)
    {
        ShipTimeState state = new ShipTimeState();

        if (!ship || ship.IsAnyPlayerShip())
            return state;

        // NPC ship physics belongs to the star authority. Remote clients receive
        // the already-slowed movement through native replication.
        if (NetSession.InSession &&
            currentBridge != null &&
            !currentBridge.IsAuthority)
        {
            return state;
        }

        float factor = GetTemporalFactor(
            ship.transform.position,
            ship.faction
        );

        ApplyVelocityFactor(ship.gameObject, factor, shipFactors);

        if (factor < 0.9999f)
        {
            state.Changed = true;
            state.OriginalSpeedScale = ship.speedScale;

            // GameShip caches speedScaleSquared for its max-speed cap.
            ship.speedScale *= factor;
            ship.GenerateSquaredValues();
        }

        return state;
    }

    public static void EndShipFrame(
        GameShip ship,
        ShipTimeState state)
    {
        if (!ship || !state.Changed)
            return;

        ship.speedScale = state.OriginalSpeedScale;
        ship.GenerateSquaredValues();
    }

    public static void ForgetShip(GameShip ship)
    {
        if (!ship)
            return;

        // GameShip instance ids can be reused after destruction. A stale factor
        // would make an unrelated future ship compensate for velocity scaling it
        // never received. The object is being destroyed, so only remove the key.
        shipFactors.Remove(ship.gameObject.GetInstanceID());
    }

    public static void ApplyProjectileFrame(Projectile projectile)
    {
        if (!projectile)
            return;

        GameShip parent = projectile.GetParentShip();
        if (!parent)
        {
            RestoreVelocityFactor(
                projectile.gameObject,
                projectileFactors
            );
            return;
        }

        float factor = GetTemporalFactor(
            projectile.transform.position,
            parent.faction
        );

        ApplyVelocityFactor(
            projectile.gameObject,
            factor,
            projectileFactors
        );
    }

    public static void ForgetProjectile(Projectile projectile)
    {
        if (!projectile)
            return;

        // Projectile instances are pooled. Remove the factor entry at the pool
        // boundary so a later reuse of the same instance id cannot inherit stale
        // Dive state. Restore first so the pool receives neutral velocity state.
        RestoreVelocityFactor(
            projectile.gameObject,
            projectileFactors
        );
    }

    private static void ApplyVelocityFactor(
        GameObject obj,
        float newFactor,
        Dictionary<int, float> factors)
    {
        if (!obj)
            return;

        int id = obj.GetInstanceID();

        float oldFactor;
        if (!factors.TryGetValue(id, out oldFactor))
            oldFactor = 1f;

        newFactor = Mathf.Clamp(newFactor, 0.05f, 1f);

        if (!Mathf.Approximately(oldFactor, newFactor))
        {
            Rigidbody2D body = obj.GetComponent<Rigidbody2D>();
            if (body)
            {
                body.velocity *=
                    newFactor / Mathf.Max(0.05f, oldFactor);
            }
        }

        if (newFactor < 0.9999f)
            factors[id] = newFactor;
        else
            factors.Remove(id);
    }

    private static void RestoreVelocityFactor(
        GameObject obj,
        Dictionary<int, float> factors)
    {
        if (!obj)
            return;

        int id = obj.GetInstanceID();

        float oldFactor;
        if (!factors.TryGetValue(id, out oldFactor))
            return;

        Rigidbody2D body = obj.GetComponent<Rigidbody2D>();
        if (body)
            body.velocity *= 1f / Mathf.Max(0.05f, oldFactor);

        factors.Remove(id);
    }
}

// =============================================================================
// HARMONY PATCHES
// =============================================================================

/// <summary>
/// Native HealthRegen is a fraction of max Hull per second. Behemoth's flat
/// regeneration is authored in Hull HP/sec, so convert only at this verified
/// native stat boundary and add the separately-authored max-Hull fraction.
/// </summary>
[HarmonyPatch]
public static class LeviathanBehemothHullRegenPatch
{
    public static MethodBase TargetMethod()
    {
        return LeviathanBehemoth.FindHealthRegenGetter();
    }

    public static void Postfix(object __instance, ref float __result)
    {
        GameShip ship;
        LeviathanBehemoth.ResolvedState state;

        if (!LeviathanBehemoth.TryGetLocalActiveState(
                __instance,
                out ship,
                out state))
        {
            return;
        }

        float maxHull = Mathf.Max(0f, ship.HealthMax);

        if (maxHull > 0f && state.FlatHullRegenPerSecond != 0f)
            __result += state.FlatHullRegenPerSecond / maxHull;

        __result += state.MaxHullRegenFractionPerSecond;
    }
}

// Intercept the reserved carrier after NetSession has already routed/relayed it.
// Returning false prevents vanilla Temporal Drive from changing global timeScale.
[HarmonyPatch(typeof(NetWorldBridge), "OnTemporalActivate")]
public static class LeviathanTemporalDiveNetworkPatch
{
    public static bool Prefix(
        NetWorldBridge __instance,
        MsgTemporalActivate temporal)
    {
        if (LeviathanTemporalDiveRuntime.IsNetworkCarrier(temporal))
        {
            // Consume only when the marker is actually accepted as a Dive. A
            // known non-Behemoth 1x Temporal Drive request is left to vanilla.
            return !LeviathanTemporalDiveRuntime.TryBeginNetworkDive(
                __instance,
                temporal
            );
        }

        // Native Temporal Drive sends a cancel when its TemporaryEffect ends or
        // is toggled off. Consume the same owner-tagged cancel if that owner has
        // a Dive rather than letting vanilla global dilation handle it.
        if (LeviathanTemporalDiveRuntime.TryCancelNetworkDive(temporal))
            return false;

        return true;
    }
}

[HarmonyPatch(typeof(NetWorldBridge), "Update")]
public static class LeviathanTemporalDiveBridgeUpdatePatch
{
    public static void Postfix(NetWorldBridge __instance)
    {
        LeviathanTemporalDiveRuntime.UpdateNetwork(__instance);
    }
}

[HarmonyPatch(typeof(GameShip), "FixedUpdate")]
public static class LeviathanTemporalDiveShipPatch
{
    public static void Prefix(
        GameShip __instance,
        out LeviathanTemporalDiveRuntime.ShipTimeState __state)
    {
        __state = LeviathanTemporalDiveRuntime.BeginShipFrame(__instance);
    }

    public static void Postfix(
        GameShip __instance,
        LeviathanTemporalDiveRuntime.ShipTimeState __state)
    {
        LeviathanTemporalDiveRuntime.EndShipFrame(
            __instance,
            __state
        );
    }
}

[HarmonyPatch(typeof(GameShip), "OnDestroy")]
public static class LeviathanTemporalDiveShipDestroyedPatch
{
    public static void Prefix(GameShip __instance)
    {
        LeviathanTemporalDiveRuntime.ForgetShip(__instance);
    }
}

[HarmonyPatch(typeof(Projectile), "FixedUpdate")]
public static class LeviathanTemporalDiveProjectilePatch
{
    public static void Prefix(Projectile __instance)
    {
        LeviathanTemporalDiveRuntime.ApplyProjectileFrame(__instance);
    }
}

[HarmonyPatch(typeof(Projectile), "PoolDestroy")]
public static class LeviathanTemporalDiveProjectilePoolDestroyPatch
{
    public static void Prefix(Projectile __instance)
    {
        LeviathanTemporalDiveRuntime.ForgetProjectile(__instance);
    }
}

// Temporal Drive remains the actual source/trigger. AddEffect still owns audio,
// activatable state and the native request; this scope only tags that request.
[HarmonyPatch(typeof(TemporalDrive), "AddEffect")]
public static class LeviathanTemporalDiveTemporalDriveAddEffectPatch
{
    public static void Prefix(
        TemporalDrive __instance,
        out bool __state)
    {
        __state =
            LeviathanTemporalDiveRuntime.BeginTemporalDriveActivation(
                __instance
            );
    }

    public static void Postfix(
        TemporalDrive __instance,
        bool __state)
    {
        if (__state)
        {
            LeviathanTemporalDiveRuntime.EndTemporalDriveActivation(
                __instance
            );
        }
    }
}

// Preserve the native reliable request/relay path, replacing only this
// transformed drive's global factor with the reserved Temporal Dive marker.
[HarmonyPatch(typeof(NetSession), "RequestTemporalDilation")]
public static class LeviathanTemporalDiveTemporalRequestPatch
{
    public static void Prefix(
        ref float timeScale,
        ref float duration)
    {
        LeviathanTemporalDiveRuntime.RewriteTemporalDriveNetworkRequest(
            ref timeScale,
            ref duration
        );
    }
}

// Temporal Dive has a 20s authored baseline but inherits the equipped Temporal
// Drive's exact native EffectDuration modifier ratio.
[HarmonyPatch(typeof(TemporaryEffect), "get_Duration")]
public static class LeviathanTemporalDiveDurationPatch
{
    public static void Postfix(
        TemporaryEffect __instance,
        ref float __result)
    {
        TemporalDrive drive = __instance as TemporalDrive;
        if (drive == null)
            return;

        __result =
            LeviathanTemporalDiveRuntime.TransformTemporalDriveDuration(
                drive,
                __result
            );
    }
}

[HarmonyPatch(typeof(TemporaryEffect), "get_LocalDuration")]
public static class LeviathanTemporalDiveLocalDurationPatch
{
    public static void Postfix(
        TemporaryEffect __instance,
        ref float __result)
    {
        TemporalDrive drive = __instance as TemporalDrive;
        if (drive == null)
            return;

        __result =
            LeviathanTemporalDiveRuntime.TransformTemporalDriveDuration(
                drive,
                __result
            );
    }
}

// Same treatment for cooldown: a 30s authored Dive baseline multiplied by the
// equipped Temporal Drive's native Cooldown modifier ratio. Native SetCooldowns
// remains authoritative for the actual timer/UI lifecycle.
[HarmonyPatch(typeof(TemporaryEffect), "get_Cooldown")]
public static class LeviathanTemporalDiveCooldownPatch
{
    public static void Postfix(
        TemporaryEffect __instance,
        ref float __result)
    {
        TemporalDrive drive = __instance as TemporalDrive;
        if (drive == null)
            return;

        __result =
            LeviathanTemporalDiveRuntime.TransformTemporalDriveCooldown(
                drive,
                __result
            );
    }
}

[HarmonyPatch(typeof(TemporaryEffect), "get_LocalCooldown")]
public static class LeviathanTemporalDiveLocalCooldownPatch
{
    public static void Postfix(
        TemporaryEffect __instance,
        ref float __result)
    {
        TemporalDrive drive = __instance as TemporalDrive;
        if (drive == null)
            return;

        __result =
            LeviathanTemporalDiveRuntime.TransformTemporalDriveCooldown(
                drive,
                __result
            );
    }
}

// Per-Pilot Behemoth state follows the exact remote replica lifecycle.
[HarmonyPatch(typeof(RemoteShipDriver), "DestroyRep")]
public static class LeviathanBehemothDestroyRepPatch
{
    public static void Prefix(GameShip __0)
    {
        LeviathanBehemoth.Forget(__0);
    }
}

// A bridge can tear down/rebuild during session/star transitions without a full
// WorldController destruction. Clear bridge-scoped Dive state and remote caches
// at that boundary; the new replica/specialization will resolve lazily.
[HarmonyPatch(typeof(NetWorldBridge), "Teardown")]
public static class LeviathanBehemothBridgeTeardownPatch
{
    public static void Postfix()
    {
        LeviathanBehemoth.Reset();
        LeviathanTemporalDiveRuntime.Reset();
    }
}

[HarmonyPatch(typeof(WorldController), "OnDestroy")]
public static class LeviathanBehemothWorldDestroyedPatch
{
    public static void Postfix()
    {
        LeviathanBehemoth.Reset();
        LeviathanTemporalDiveRuntime.Reset();
    }
}
