using HarmonyLib;
using StarVortex;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

public static class LeviathanBehemoth
{
    // =========================================================================
    // BALANCE TUNING
    // =========================================================================
    // Arrays are Rank 1 -> Rank 5.

    public const float BaseSegmentDamageMultiplier = 0.40f;

    private static readonly float[] SegmentDamageMultiplierByRank =
    {
        0.35f,
        0.30f,
        0.25f,
        0.23f,
        0.20f
    };

    private static readonly float[] SegmentDebuffDiscardChanceByRank =
    {
        0.20f,
        0.25f,
        0.30f,
        0.35f,
        0.40f
    };

    private static readonly float[] ResistanceMultiplierByRank =
    {
        1.00f,
        1.00f,
        1.00f,
        1.00f,
        1.00f
    };

    private static readonly float[] ArmorMultiplierByRank =
    {
        1.00f,
        1.00f,
        1.00f,
        1.00f,
        1.00f
    };

    private static readonly float[] MaxHullMultiplierByRank =
    {
        1.00f,
        1.10f,
        1.10f,
        1.13f,
        1.15f
    };

    private static readonly float[] MaxHullFlatBonusByRank =
    {
        60.00f,
        70.00f,
        80.00f,
        90.00f,
        100.00f
    };

    private static readonly float[] MaxHullFlatBonusPerSegmentByRank =
    {
        0.00f,
        0.00f,
        0.00f,
        0.00f,
        0.00f
    };

    private static readonly float[] StaticHullRegenPerSecondByRank =
    {
        0.00f,
        0.00f,
        0.00f,
        0.00f,
        0.00f
    };

    private static readonly float[] StaticHullRegenPerSegmentPerSecondByRank =
    {
        2.50f,
        3.00f,
        3.50f,
        4.50f,
        6.50f
    };

    private static readonly float[] MaxHullRegenPercentPerSecondByRank =
    {
        0.30f,
        0.50f,
        0.60f,
        0.80f,
        1.00f
    };

    public const int MaxRank = 5;

    // Temporal Dive defaults. These are intentionally gentle for the first pass.
    public const float BaseTemporalDiveRadius = 180f;
    public const float BaseTemporalDiveMinimumFactor = 0.65f;
    public const float BaseTemporalDiveFalloffPower = 1f;
    public const float BaseTemporalDiveDuration = 20f;
    public const float BaseTemporalDiveCooldown = 30f;

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
    // RUNTIME
    // =========================================================================

    private static readonly MethodInfo HealthMaxGetter =
        FindStatGetter("get_HealthMax");

    public static float GetSegmentDamageMultiplier(GameShip player)
    {
        int rank = Mathf.Clamp(GetRank(player), 0, MaxRank);

        if (rank < 1)
            return BaseSegmentDamageMultiplier;

        return Mathf.Clamp01(GetRankValue(SegmentDamageMultiplierByRank, rank));
    }

    public static int GetRank(GameShip player)
    {
        if (player == null)
            return 0;

        Pilot pilot = GameShip.GetPlayerSourcePilot(player);

        return pilot == null
            ? 0
            : pilot.GetUpgradeLevel(LeviathanMod.BehemothUpgrade);
    }

    public static float GetSegmentDebuffDiscardChance(GameShip player)
    {
        int rank = Mathf.Clamp(GetRank(player), 0, MaxRank);

        if (rank < 1)
            return 0f;

        return GetRankValue(SegmentDebuffDiscardChanceByRank, rank);
    }

    internal static bool TryGetActiveRank(object instance, out GameShip player, out int rank)
    {
        player = instance as GameShip;
        rank = 0;

        if (player == null)
            return false;

        if (LeviathanMod.Controller != null &&
            LeviathanMod.Controller.IsLeviathanSegment(player))
        {
            return false;
        }

        GameShip currentPlayer =
            WorldController.instance == null
                ? null
                : WorldController.instance.GetCurrentPlayerShip();

        if (currentPlayer != null && currentPlayer != player)
            return false;

        if (LeviathanGrowth.GetRank(player) < 1)
            return false;

        rank = Mathf.Clamp(GetRank(player), 0, MaxRank);
        return rank >= 1;
    }

    internal static float GetResistanceMultiplier(int rank)
    {
        return GetRankValue(ResistanceMultiplierByRank, rank);
    }

    internal static float GetArmorMultiplier(int rank)
    {
        return GetRankValue(ArmorMultiplierByRank, rank);
    }

    internal static float GetMaxHullMultiplier(int rank)
    {
        return GetRankValue(MaxHullMultiplierByRank, rank);
    }

    internal static float GetMaxHullFlatBonus(int rank)
    {
        return GetRankValue(MaxHullFlatBonusByRank, rank);
    }

    internal static float GetMaxHullFlatBonusPerSegment(int rank)
    {
        return GetRankValue(MaxHullFlatBonusPerSegmentByRank, rank);
    }

    internal static float GetStaticHullRegen(int rank)
    {
        return GetRankValue(StaticHullRegenPerSecondByRank, rank);
    }

    internal static float GetStaticHullRegenPerSegment(int rank)
    {
        return GetRankValue(StaticHullRegenPerSegmentPerSecondByRank, rank);
    }

    internal static float GetMaxHullRegenPercent(int rank)
    {
        return GetRankValue(MaxHullRegenPercentPerSecondByRank, rank);
    }

    internal static float GetAdjustedMaxHull(GameShip player)
    {
        if (player == null || HealthMaxGetter == null)
            return 0f;

        object value = HealthMaxGetter.Invoke(player, null);

        if (value is int)
            return (float)(int)value;

        if (value is float)
            return (float)value;

        return 0f;
    }

    internal static MethodInfo FindStatGetter(string name)
    {
        return AccessTools.Method(typeof(GameShip), name) ??
            AccessTools.Method(typeof(Damageable), name);
    }

    private static float GetRankValue(float[] values, int rank)
    {
        int index = Mathf.Clamp(rank, 1, values.Length) - 1;
        return values[index];
    }

    internal static float ResolveTemporalDiveKnob(
        Pilot pilot,
        LeviathanSpecializationKnob knob,
        float baseValue)
    {
        return pilot == null
            ? baseValue
            : LeviathanSpecializationRuntime.ApplyKnob(pilot, knob, baseValue);
    }
}

// =============================================================================
// TEMPORAL DIVE
// =============================================================================

public static class LeviathanTemporalDiveRuntime
{
    // TemporalActivate is already a reliable, owner-tagged, star-scoped message.
    // A timeScale of exactly 1 with positive duration has no useful vanilla effect,
    // so it is reserved here as the Temporal Dive carrier and intercepted before
    // NetWorldBridge can create a global time-dilation contribution.
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
        public float AppliedFactor;
    }

    private static readonly Dictionary<int, DiveState> ActiveDives =
        new Dictionary<int, DiveState>();

    private static readonly Dictionary<int, float> ShipFactors =
        new Dictionary<int, float>();

    private static readonly Dictionary<int, float> ProjectileFactors =
        new Dictionary<int, float>();

    private static NetWorldBridge currentBridge;
    private static float localCooldownRemaining;
    private static float localSinglePlayerRemaining;
    private static GameShip localSinglePlayerOwner;

    public static bool IsNetworkCarrier(MsgTemporalActivate temporal)
    {
        return temporal != null &&
            !temporal.cancel &&
            temporal.duration > 0f &&
            Mathf.Approximately(temporal.timeScale, NetworkMarkerTimeScale);
    }

    public static bool TryActivate(GameShip player)
    {
        if (player == null || player != WorldController.instance?.GetCurrentPlayerShip())
            return false;

        if (LeviathanGrowth.GetRank(player) < 1 || LeviathanBehemoth.GetRank(player) < 1)
            return false;

        Pilot pilot = GameShip.GetPlayerSourcePilot(player);
        if (pilot == null ||
            !LeviathanSpecializationRuntime.HasFlag(
                pilot,
                LeviathanBehemoth.Flags.TemporalDive))
        {
            return false;
        }

        if (localCooldownRemaining > 0f || IsLocalDiveActive())
            return false;

        float duration = Mathf.Max(
            0.1f,
            LeviathanBehemoth.ResolveTemporalDiveKnob(
                pilot,
                LeviathanBehemoth.Knobs.TemporalDiveDuration,
                LeviathanBehemoth.BaseTemporalDiveDuration));

        if (NetSession.InSession && NetSession.instance != null)
        {
            NetSession.instance.RequestTemporalDilation(
                NetworkMarkerTimeScale,
                Mathf.Min(duration, 30f));
        }
        else
        {
            localSinglePlayerOwner = player;
            localSinglePlayerRemaining = duration;
        }

        return true;
    }

    public static void OnNetworkDive(NetWorldBridge bridge, MsgTemporalActivate temporal)
    {
        if (bridge == null || temporal == null)
            return;

        currentBridge = bridge;

        GameShip owner = bridge.ResolvePlayerShip(temporal.ownerPlayerId);
        Pilot pilot = owner == null ? null : GameShip.GetPlayerSourcePilot(owner);

        DiveState state = new DiveState();
        state.OwnerPlayerId = temporal.ownerPlayerId;
        state.Remaining = Mathf.Clamp(temporal.duration, 0.1f, 30f);

        // Current Behemoth tree does not modify these per player yet. Resolve the
        // local player's configured values when available; remote owners use the
        // shared baseline so every client remains deterministic for this version.
        bool localOwner = NetSession.instance != null &&
            temporal.ownerPlayerId == NetSession.instance.localPlayerId;

        state.Radius = Mathf.Max(1f,
            localOwner
                ? LeviathanBehemoth.ResolveTemporalDiveKnob(
                    pilot,
                    LeviathanBehemoth.Knobs.TemporalDiveRadius,
                    LeviathanBehemoth.BaseTemporalDiveRadius)
                : LeviathanBehemoth.BaseTemporalDiveRadius);

        state.MinimumFactor = Mathf.Clamp(
            localOwner
                ? LeviathanBehemoth.ResolveTemporalDiveKnob(
                    pilot,
                    LeviathanBehemoth.Knobs.TemporalDiveMinimumFactor,
                    LeviathanBehemoth.BaseTemporalDiveMinimumFactor)
                : LeviathanBehemoth.BaseTemporalDiveMinimumFactor,
            0.05f,
            1f);

        state.FalloffPower = Mathf.Max(
            0.05f,
            localOwner
                ? LeviathanBehemoth.ResolveTemporalDiveKnob(
                    pilot,
                    LeviathanBehemoth.Knobs.TemporalDiveFalloffPower,
                    LeviathanBehemoth.BaseTemporalDiveFalloffPower)
                : LeviathanBehemoth.BaseTemporalDiveFalloffPower);

        ActiveDives[temporal.ownerPlayerId] = state;
    }

    public static void UpdateNetwork(NetWorldBridge bridge, float unscaledDeltaTime)
    {
        currentBridge = bridge;
        // Native TemporaryEffect duration runs on Time.deltaTime. Matching that
        // here keeps the network field aligned if another global Temporal Drive
        // is active in the same star.
        Tick(Time.deltaTime);
    }

    public static void UpdateSinglePlayer(float unscaledDeltaTime)
    {
        if (NetSession.InSession)
            return;

        Tick(unscaledDeltaTime);

        if (localSinglePlayerRemaining > 0f)
        {
            localSinglePlayerRemaining -= unscaledDeltaTime;
            if (localSinglePlayerRemaining <= 0f)
            {
                localSinglePlayerRemaining = 0f;
                localCooldownRemaining = GetLocalCooldown(localSinglePlayerOwner);
                localSinglePlayerOwner = null;
            }
        }
    }

    private static void Tick(float unscaledDeltaTime)
    {
        if (localCooldownRemaining > 0f)
            localCooldownRemaining = Mathf.Max(0f, localCooldownRemaining - unscaledDeltaTime);

        if (ActiveDives.Count == 0)
            return;

        List<int> expired = null;

        foreach (KeyValuePair<int, DiveState> pair in ActiveDives)
        {
            DiveState state = pair.Value;
            state.Remaining -= unscaledDeltaTime;

            if (state.Remaining <= 0f)
            {
                if (expired == null)
                    expired = new List<int>();
                expired.Add(pair.Key);
            }
        }

        if (expired == null)
            return;

        for (int i = 0; i < expired.Count; i++)
        {
            int ownerPlayerId = expired[i];
            ActiveDives.Remove(ownerPlayerId);

            if (NetSession.instance != null &&
                ownerPlayerId == NetSession.instance.localPlayerId)
            {
                GameShip owner = currentBridge == null
                    ? null
                    : currentBridge.ResolvePlayerShip(ownerPlayerId);
                localCooldownRemaining = GetLocalCooldown(owner);
            }
        }
    }

    private static float GetLocalCooldown(GameShip player)
    {
        Pilot pilot = player == null ? null : GameShip.GetPlayerSourcePilot(player);
        return Mathf.Max(
            0f,
            LeviathanBehemoth.ResolveTemporalDiveKnob(
                pilot,
                LeviathanBehemoth.Knobs.TemporalDiveCooldown,
                LeviathanBehemoth.BaseTemporalDiveCooldown));
    }

    public static bool IsLocalDiveActive()
    {
        if (NetSession.InSession && NetSession.instance != null)
            return ActiveDives.ContainsKey(NetSession.instance.localPlayerId);

        return localSinglePlayerRemaining > 0f;
    }

    public static float GetLocalCooldownRemaining()
    {
        return localCooldownRemaining;
    }

    // Temporal Dive transforms the locally controlled equipped Temporal Drive.
    // The native item remains responsible for input, active state, audio,
    // duration/cooldown timers, charges, linked-slot behavior and UI state.
    public static bool ShouldTransformTemporalDrive(TemporalDrive drive)
    {
        if (drive == null || !NetSession.InSession || WorldController.instance == null)
            return false;

        GameShip player = drive.parentShip;
        if (player == null || player != WorldController.instance.GetCurrentPlayerShip())
            return false;

        if (LeviathanGrowth.GetRank(player) < 1 || LeviathanBehemoth.GetRank(player) < 1)
            return false;

        Pilot pilot = GameShip.GetPlayerSourcePilot(player);
        return pilot != null &&
            LeviathanSpecializationRuntime.HasFlag(
                pilot,
                LeviathanBehemoth.Flags.TemporalDive);
    }

    public static float TransformTemporalDriveDuration(
        TemporalDrive drive,
        float nativeModifiedDuration,
        bool useLocalModifiers)
    {
        if (!ShouldTransformTemporalDrive(drive))
            return nativeModifiedDuration;

        float nativeBase = Mathf.Max(0.001f, drive.BaseDuration);
        float modifierRatio = nativeModifiedDuration / nativeBase;

        Pilot pilot = GameShip.GetPlayerSourcePilot(drive.parentShip);
        float diveBase = Mathf.Max(
            0.1f,
            LeviathanBehemoth.ResolveTemporalDiveKnob(
                pilot,
                LeviathanBehemoth.Knobs.TemporalDiveDuration,
                LeviathanBehemoth.BaseTemporalDiveDuration));

        return Mathf.Max(0.1f, diveBase * modifierRatio);
    }

    public static float TransformTemporalDriveCooldown(
        TemporalDrive drive,
        float nativeModifiedCooldown,
        bool useLocalModifiers)
    {
        if (!ShouldTransformTemporalDrive(drive))
            return nativeModifiedCooldown;

        float nativeBase = Mathf.Max(0.001f, drive.BaseCooldown);
        float modifierRatio = nativeModifiedCooldown / nativeBase;

        Pilot pilot = GameShip.GetPlayerSourcePilot(drive.parentShip);
        float diveBase = Mathf.Max(
            0f,
            LeviathanBehemoth.ResolveTemporalDiveKnob(
                pilot,
                LeviathanBehemoth.Knobs.TemporalDiveCooldown,
                LeviathanBehemoth.BaseTemporalDiveCooldown));

        return Mathf.Max(0f, diveBase * modifierRatio);
    }

    private static TemporalDrive temporalDriveActivationSource;

    public static bool BeginTemporalDriveActivation(TemporalDrive drive)
    {
        if (!ShouldTransformTemporalDrive(drive))
            return false;

        temporalDriveActivationSource = drive;
        return true;
    }

    public static void EndTemporalDriveActivation(TemporalDrive drive)
    {
        if (ReferenceEquals(temporalDriveActivationSource, drive))
            temporalDriveActivationSource = null;
    }

    public static void RewriteTemporalDriveNetworkRequest(
        ref float timeScale,
        ref float duration)
    {
        TemporalDrive drive = temporalDriveActivationSource;
        if (drive == null || !ShouldTransformTemporalDrive(drive))
            return;

        // Keep the native network route, but turn its global Temporal Drive
        // request into the reserved Temporal Dive carrier. OnTemporalActivate
        // consumes this marker before vanilla global time dilation is applied.
        timeScale = NetworkMarkerTimeScale;
        duration = Mathf.Clamp(drive.Duration, 0.1f, 30f);
    }

    public static bool TryCancelNetworkDive(MsgTemporalActivate temporal)
    {
        if (temporal == null || !temporal.cancel)
            return false;

        return ActiveDives.Remove(temporal.ownerPlayerId);
    }

    private static float GetTemporalFactor(Vector2 position, string targetFaction)
    {
        float strongest = 1f;

        if (NetSession.InSession)
        {
            if (currentBridge == null)
                return 1f;

            foreach (KeyValuePair<int, DiveState> pair in ActiveDives)
            {
                DiveState state = pair.Value;
                GameShip owner = currentBridge.ResolvePlayerShip(state.OwnerPlayerId);
                if (!owner || owner.health <= 0f)
                    continue;

                if (!Faction.IsHostile(owner.faction, targetFaction))
                    continue;

                strongest = Mathf.Min(
                    strongest,
                    EvaluateField(owner.transform.position, position, state));
            }
        }
        else if (localSinglePlayerRemaining > 0f && localSinglePlayerOwner)
        {
            if (Faction.IsHostile(localSinglePlayerOwner.faction, targetFaction))
            {
                DiveState state = BuildLocalSinglePlayerState(localSinglePlayerOwner);
                strongest = EvaluateField(
                    localSinglePlayerOwner.transform.position,
                    position,
                    state);
            }
        }

        return strongest;
    }

    private static DiveState BuildLocalSinglePlayerState(GameShip owner)
    {
        Pilot pilot = GameShip.GetPlayerSourcePilot(owner);
        DiveState state = new DiveState();
        state.Radius = Mathf.Max(1f,
            LeviathanBehemoth.ResolveTemporalDiveKnob(
                pilot,
                LeviathanBehemoth.Knobs.TemporalDiveRadius,
                LeviathanBehemoth.BaseTemporalDiveRadius));
        state.MinimumFactor = Mathf.Clamp(
            LeviathanBehemoth.ResolveTemporalDiveKnob(
                pilot,
                LeviathanBehemoth.Knobs.TemporalDiveMinimumFactor,
                LeviathanBehemoth.BaseTemporalDiveMinimumFactor),
            0.05f,
            1f);
        state.FalloffPower = Mathf.Max(0.05f,
            LeviathanBehemoth.ResolveTemporalDiveKnob(
                pilot,
                LeviathanBehemoth.Knobs.TemporalDiveFalloffPower,
                LeviathanBehemoth.BaseTemporalDiveFalloffPower));
        return state;
    }

    private static float EvaluateField(Vector2 center, Vector2 position, DiveState state)
    {
        float distance = Vector2.Distance(center, position);
        if (distance >= state.Radius)
            return 1f;

        float normalized = Mathf.Clamp01(distance / state.Radius);
        float smooth = normalized * normalized * (3f - 2f * normalized);
        smooth = Mathf.Pow(smooth, state.FalloffPower);
        return Mathf.Lerp(state.MinimumFactor, 1f, smooth);
    }

    public static ShipTimeState BeginShipFrame(GameShip ship)
    {
        ShipTimeState state = new ShipTimeState();

        if (!ship || ship.IsAnyPlayerShip())
            return state;

        // NPC ship physics belong to the star authority. Remote clients receive
        // the already-slowed authoritative movement through normal replication.
        if (NetSession.InSession && currentBridge != null && !currentBridge.IsAuthority)
            return state;

        float factor = GetTemporalFactor(ship.transform.position, ship.faction);
        ApplyVelocityFactor(ship.gameObject, factor, ShipFactors);

        if (factor < 0.9999f)
        {
            state.Changed = true;
            state.OriginalSpeedScale = ship.speedScale;
            state.AppliedFactor = factor;

            // GameShip caches speedScaleSquared and uses it for max-speed caps.
            // Changing speedScale without rebuilding that cache lets an affected
            // ship accelerate back toward its normal max speed while inside the
            // field, making the Dive feel much weaker than its configured factor.
            ship.speedScale *= factor;
            ship.GenerateSquaredValues();
        }

        return state;
    }

    public static void EndShipFrame(GameShip ship, ShipTimeState state)
    {
        if (!ship || !state.Changed)
            return;

        ship.speedScale = state.OriginalSpeedScale;
        ship.GenerateSquaredValues();
    }

    public static void ApplyProjectileFrame(Projectile projectile)
    {
        if (!projectile)
            return;

        GameShip parent = projectile.GetParentShip();
        if (!parent)
        {
            RestoreVelocityFactor(projectile.gameObject, ProjectileFactors);
            return;
        }

        float factor = GetTemporalFactor(projectile.transform.position, parent.faction);
        ApplyVelocityFactor(projectile.gameObject, factor, ProjectileFactors);
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
                body.velocity *= newFactor / Mathf.Max(0.05f, oldFactor);
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

[HarmonyPatch]
public static class LeviathanBehemothResistancePatch
{
    public static IEnumerable<MethodBase> TargetMethods()
    {
        string[] getters =
        {
            "get_ResistanceKinetic",
            "get_ResistanceElectric",
            "get_ResistanceCold",
            "get_ResistanceCorrosive",
            "get_ResistanceThermal",
            "get_ResistanceRadiation"
        };

        for (int i = 0; i < getters.Length; i++)
        {
            MethodBase method = LeviathanBehemoth.FindStatGetter(getters[i]);
            if (method != null)
                yield return method;
        }
    }

    public static void Postfix(object __instance, ref float __result)
    {
        GameShip player;
        int rank;

        if (!LeviathanBehemoth.TryGetActiveRank(__instance, out player, out rank))
            return;

        __result *= LeviathanBehemoth.GetResistanceMultiplier(rank);
    }
}

[HarmonyPatch]
public static class LeviathanBehemothArmorPatch
{
    public static MethodBase TargetMethod()
    {
        return LeviathanBehemoth.FindStatGetter("get_DamageReduction");
    }

    public static void Postfix(object __instance, ref float __result)
    {
        GameShip player;
        int rank;

        if (!LeviathanBehemoth.TryGetActiveRank(__instance, out player, out rank))
            return;

        __result *= LeviathanBehemoth.GetArmorMultiplier(rank);
    }
}

[HarmonyPatch]
public static class LeviathanBehemothHullRegenPatch
{
    public static MethodBase TargetMethod()
    {
        return LeviathanBehemoth.FindStatGetter("get_HealthRegen");
    }

    public static void Postfix(object __instance, ref float __result)
    {
        GameShip player;
        int rank;

        if (!LeviathanBehemoth.TryGetActiveRank(__instance, out player, out rank))
            return;

        int segmentCount = LeviathanGrowth.GetSegmentCount(player);
        float maxHull = LeviathanBehemoth.GetAdjustedMaxHull(player);
        float flatRegenPerSecond =
            LeviathanBehemoth.GetStaticHullRegen(rank) +
            segmentCount * LeviathanBehemoth.GetStaticHullRegenPerSegment(rank);

        if (maxHull > 0f)
            __result += flatRegenPerSecond / maxHull;

        __result += LeviathanBehemoth.GetMaxHullRegenPercent(rank) / 100f;
    }
}

// Intercept the marker after NetSession has already routed/relayed it. Returning
// false here prevents vanilla Temporal Drive from touching global timeScale.
[HarmonyPatch(typeof(NetWorldBridge), "OnTemporalActivate")]
public static class LeviathanTemporalDiveNetworkPatch
{
    public static bool Prefix(NetWorldBridge __instance, MsgTemporalActivate temporal)
    {
        if (LeviathanTemporalDiveRuntime.IsNetworkCarrier(temporal))
        {
            LeviathanTemporalDiveRuntime.OnNetworkDive(__instance, temporal);
            return false;
        }

        // Native Temporal Drive sends a cancel when its TemporaryEffect ends or
        // is toggled off. If this owner currently has a Dive, consume that same
        // cancel as the Dive's stop signal instead of touching global time.
        if (LeviathanTemporalDiveRuntime.TryCancelNetworkDive(temporal))
            return false;

        return true;
    }
}

[HarmonyPatch(typeof(NetWorldBridge), "Update")]
public static class LeviathanTemporalDiveBridgeUpdatePatch
{
    public static void Postfix(NetWorldBridge __instance, float unscaledDeltaTime)
    {
        LeviathanTemporalDiveRuntime.UpdateNetwork(__instance, unscaledDeltaTime);
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
        LeviathanTemporalDiveRuntime.EndShipFrame(__instance, __state);

        if (WorldController.instance != null &&
            __instance == WorldController.instance.GetCurrentPlayerShip())
        {
            LeviathanTemporalDiveRuntime.UpdateSinglePlayer(Time.fixedUnscaledDeltaTime);
        }
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

// Temporal Drive is the source/trigger for Temporal Dive. Its native
// TemporaryEffect lifecycle stays intact; only the global dilation request is
// rewritten into the local-field network carrier.
[HarmonyPatch(typeof(TemporalDrive), "AddEffect")]
public static class LeviathanTemporalDiveTemporalDriveAddEffectPatch
{
    public static void Prefix(TemporalDrive __instance, out bool __state)
    {
        __state = LeviathanTemporalDiveRuntime.BeginTemporalDriveActivation(__instance);
    }

    public static void Postfix(TemporalDrive __instance, bool __state)
    {
        if (__state)
            LeviathanTemporalDiveRuntime.EndTemporalDriveActivation(__instance);
    }
}

// Preserve the native request/relay path, but replace only this transformed
// Temporal Drive's global timeScale with the Temporal Dive carrier marker.
[HarmonyPatch(typeof(NetSession), "RequestTemporalDilation")]
public static class LeviathanTemporalDiveTemporalRequestPatch
{
    public static void Prefix(ref float timeScale, ref float duration)
    {
        LeviathanTemporalDiveRuntime.RewriteTemporalDriveNetworkRequest(
            ref timeScale,
            ref duration);
    }
}

// Temporal Dive has its own 20s baseline, but inherits the exact EffectDuration
// modifier ratio currently applied to the equipped Temporal Drive. This includes
// the Drive's item/legendary modifiers and applicable parent-ship modifiers.
[HarmonyPatch(typeof(TemporaryEffect), "get_Duration")]
public static class LeviathanTemporalDiveDurationPatch
{
    public static void Postfix(TemporaryEffect __instance, ref float __result)
    {
        TemporalDrive drive = __instance as TemporalDrive;
        if (drive == null)
            return;

        __result = LeviathanTemporalDiveRuntime.TransformTemporalDriveDuration(
            drive,
            __result,
            false);
    }
}

[HarmonyPatch(typeof(TemporaryEffect), "get_LocalDuration")]
public static class LeviathanTemporalDiveLocalDurationPatch
{
    public static void Postfix(TemporaryEffect __instance, ref float __result)
    {
        TemporalDrive drive = __instance as TemporalDrive;
        if (drive == null)
            return;

        __result = LeviathanTemporalDiveRuntime.TransformTemporalDriveDuration(
            drive,
            __result,
            true);
    }
}

// Same treatment for cooldown: 30s Temporal Dive baseline multiplied by the
// equipped Temporal Drive's native Cooldown modifier ratio. Native SetCooldown
// and UI timers remain responsible for the actual cooldown lifecycle.
[HarmonyPatch(typeof(TemporaryEffect), "get_Cooldown")]
public static class LeviathanTemporalDiveCooldownPatch
{
    public static void Postfix(TemporaryEffect __instance, ref float __result)
    {
        TemporalDrive drive = __instance as TemporalDrive;
        if (drive == null)
            return;

        __result = LeviathanTemporalDiveRuntime.TransformTemporalDriveCooldown(
            drive,
            __result,
            false);
    }
}

[HarmonyPatch(typeof(TemporaryEffect), "get_LocalCooldown")]
public static class LeviathanTemporalDiveLocalCooldownPatch
{
    public static void Postfix(TemporaryEffect __instance, ref float __result)
    {
        TemporalDrive drive = __instance as TemporalDrive;
        if (drive == null)
            return;

        __result = LeviathanTemporalDiveRuntime.TransformTemporalDriveCooldown(
            drive,
            __result,
            true);
    }
}
