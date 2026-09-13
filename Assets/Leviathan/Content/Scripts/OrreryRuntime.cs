using StarVortex;
using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Stable Orrery class configuration and owner lifecycle.
///
/// This is intentionally independent from the current Leviathan tree naming.
/// A future Orrery progression system resolves into ResolvedState; live casting,
/// satellite HP, lock state and orbit position remain runtime state elsewhere.
/// </summary>
public static class OrreryRuntime
{
    public static class Tuning
    {
        // Baseline Celestial Theurge/Orrery starts with two physical formula
        // satellites and therefore assembles two-rune recipes. Keep these as
        // independent resolved values so later progression can add satellites
        // without baking formula arity into casting code.
        public const int BaseSatelliteCount = 2;
        public const int BaseFormulaSatelliteCount = 2;

        // Persist more designs than are currently active so progression can
        // activate additional satellites without destroying player-authored
        // inactive designs.
        public const int PersistedSatelliteDesignSlots = 5;

        // First-playtest orbit values. Keep the innermost satellite intimate to
        // the player while giving adjacent orbital lanes enough separation for
        // distinct player-authored satellite bodies to read clearly.
        public const float BaseOrbitRadiusMeters = 25f;
        public const float OrbitLaneSpacingMeters = 16f;
        public const float BaseOrbitAngularSpeedDegreesPerSecond = 45f;
        public const float WheelBaseRotationOffsetDegrees = 0f;
        public const float WheelFollowSmoothTimeSeconds = 0f;

        public const float SatelliteIncomingDamageMultiplier = 0.50f;

        // Recovery balance is intentionally unresolved. The resolved shape
        // supports both contributions without inventing balance.
        public const float DisabledRecoveryFlatHullPerSecond = 0f;
        public const float DisabledRecoveryMaxHullFractionPerSecond = 0f;

        // Fire, Ice and Lightning evenly partition the baseline wheel.
        public const float BaseSectorArcDegrees = 120f;
    }

    public struct Sector
    {
        public OrreryElement Element;
        public float StartDegrees;
        public float ArcDegrees;

        public Sector(OrreryElement element, float startDegrees, float arcDegrees)
        {
            Element = element;
            StartDegrees = NormalizeDegrees(startDegrees);
            ArcDegrees = Mathf.Clamp(arcDegrees, 0f, 360f);
        }
    }

    /// <summary>
    /// Immutable casting-wheel geometry. This is deliberately more general than
    /// three equal sectors so shrink/empower, removed elements and a future
    /// fourth element do not require replacing the casting model.
    /// </summary>
    public sealed class SectorLayout
    {
        private readonly Sector[] sectors;

        public int Count { get { return sectors.Length; } }

        public SectorLayout(params Sector[] source)
        {
            if (source == null || source.Length == 0)
                throw new ArgumentException("At least one Orrery sector is required.", "source");

            sectors = new Sector[source.Length];
            Array.Copy(source, sectors, source.Length);
        }

        public Sector Get(int index)
        {
            if (index < 0 || index >= sectors.Length)
                throw new ArgumentOutOfRangeException("index");

            return sectors[index];
        }

        public OrreryElement Resolve(float worldAngleDegrees)
        {
            float angle = NormalizeDegrees(worldAngleDegrees);

            for (int i = 0; i < sectors.Length; i++)
            {
                Sector sector = sectors[i];
                if (sector.Element == OrreryElement.None || sector.ArcDegrees <= 0f)
                    continue;

                float local = NormalizeDegrees(angle - sector.StartDegrees);
                if (local < sector.ArcDegrees || Mathf.Approximately(local, sector.ArcDegrees) &&
                    Mathf.Approximately(sector.ArcDegrees, 360f))
                {
                    return sector.Element;
                }
            }

            return OrreryElement.None;
        }

        /// <summary>
        /// 0 at a sector edge and 1 at its center. Baseline V0 ignores this;
        /// future Precision Casting can consume it without changing recipe logic.
        /// </summary>
        public float GetCenterPrecision01(float worldAngleDegrees, OrreryElement element)
        {
            float angle = NormalizeDegrees(worldAngleDegrees);

            for (int i = 0; i < sectors.Length; i++)
            {
                Sector sector = sectors[i];
                if (sector.Element != element || sector.ArcDegrees <= 0f)
                    continue;

                float local = NormalizeDegrees(angle - sector.StartDegrees);
                if (local > sector.ArcDegrees)
                    continue;

                float half = sector.ArcDegrees * 0.5f;
                if (half <= 0f)
                    return 0f;

                return Mathf.Clamp01(1f - Mathf.Abs(local - half) / half);
            }

            return 0f;
        }
    }

    public sealed class ResolvedState
    {
        public readonly bool Active;
        public readonly int SatelliteCount;
        public readonly int FormulaSatelliteCount;
        public readonly float BaseOrbitRadiusMeters;
        public readonly float OrbitLaneSpacingMeters;
        public readonly float BaseOrbitAngularSpeedDegreesPerSecond;
        public readonly float WheelBaseRotationOffsetDegrees;
        public readonly float WheelFollowSmoothTimeSeconds;
        public readonly float SatelliteIncomingDamageMultiplier;
        public readonly float DisabledRecoveryFlatHullPerSecond;
        public readonly float DisabledRecoveryMaxHullFractionPerSecond;
        public readonly SectorLayout Sectors;

        public ResolvedState(
            bool active,
            int satelliteCount,
            int formulaSatelliteCount,
            float satelliteIncomingDamageMultiplier,
            float disabledRecoveryFlatHullPerSecond,
            float disabledRecoveryMaxHullFractionPerSecond,
            SectorLayout sectors)
            : this(
                active,
                satelliteCount,
                formulaSatelliteCount,
                Tuning.BaseOrbitRadiusMeters,
                Tuning.OrbitLaneSpacingMeters,
                Tuning.BaseOrbitAngularSpeedDegreesPerSecond,
                Tuning.WheelBaseRotationOffsetDegrees,
                Tuning.WheelFollowSmoothTimeSeconds,
                satelliteIncomingDamageMultiplier,
                disabledRecoveryFlatHullPerSecond,
                disabledRecoveryMaxHullFractionPerSecond,
                sectors)
        {
        }

        public ResolvedState(
            bool active,
            int satelliteCount,
            int formulaSatelliteCount,
            float baseOrbitRadiusMeters,
            float orbitLaneSpacingMeters,
            float baseOrbitAngularSpeedDegreesPerSecond,
            float wheelBaseRotationOffsetDegrees,
            float wheelFollowSmoothTimeSeconds,
            float satelliteIncomingDamageMultiplier,
            float disabledRecoveryFlatHullPerSecond,
            float disabledRecoveryMaxHullFractionPerSecond,
            SectorLayout sectors)
        {
            Active = active;
            SatelliteCount = Mathf.Max(0, satelliteCount);
            FormulaSatelliteCount = Mathf.Clamp(formulaSatelliteCount, 0, SatelliteCount);
            BaseOrbitRadiusMeters = Mathf.Max(0f, baseOrbitRadiusMeters);
            OrbitLaneSpacingMeters = Mathf.Max(0f, orbitLaneSpacingMeters);
            BaseOrbitAngularSpeedDegreesPerSecond = baseOrbitAngularSpeedDegreesPerSecond;
            WheelBaseRotationOffsetDegrees = NormalizeDegrees(wheelBaseRotationOffsetDegrees);
            WheelFollowSmoothTimeSeconds = Mathf.Max(0f, wheelFollowSmoothTimeSeconds);
            SatelliteIncomingDamageMultiplier = Mathf.Max(0f, satelliteIncomingDamageMultiplier);
            DisabledRecoveryFlatHullPerSecond = Mathf.Max(0f, disabledRecoveryFlatHullPerSecond);
            DisabledRecoveryMaxHullFractionPerSecond = Mathf.Max(0f, disabledRecoveryMaxHullFractionPerSecond);
            Sectors = sectors;
        }
    }

    private sealed class OwnerState
    {
        public ResolvedState Resolved;
        public int Revision;
    }

    private static readonly Dictionary<GameShip, OwnerState> owners =
        new Dictionary<GameShip, OwnerState>(4);
    private static readonly SectorLayout baseSectorLayout = new SectorLayout(
        new Sector(OrreryElement.Fire, 0f, Tuning.BaseSectorArcDegrees),
        new Sector(OrreryElement.Ice, 120f, Tuning.BaseSectorArcDegrees),
        new Sector(OrreryElement.Lightning, 240f, Tuning.BaseSectorArcDegrees));

    private static readonly ResolvedState inactiveState = new ResolvedState(
        false,
        0,
        0,
        Tuning.BaseOrbitRadiusMeters,
        Tuning.OrbitLaneSpacingMeters,
        Tuning.BaseOrbitAngularSpeedDegreesPerSecond,
        Tuning.WheelBaseRotationOffsetDegrees,
        Tuning.WheelFollowSmoothTimeSeconds,
        Tuning.SatelliteIncomingDamageMultiplier,
        0f,
        0f,
        baseSectorLayout);

    public static int Revision { get; private set; }

    public static ResolvedState CreateBaselineResolvedState()
    {
        return new ResolvedState(
            true,
            Tuning.BaseSatelliteCount,
            Tuning.BaseFormulaSatelliteCount,
            Tuning.BaseOrbitRadiusMeters,
            Tuning.OrbitLaneSpacingMeters,
            Tuning.BaseOrbitAngularSpeedDegreesPerSecond,
            Tuning.WheelBaseRotationOffsetDegrees,
            Tuning.WheelFollowSmoothTimeSeconds,
            Tuning.SatelliteIncomingDamageMultiplier,
            Tuning.DisabledRecoveryFlatHullPerSecond,
            Tuning.DisabledRecoveryMaxHullFractionPerSecond,
            baseSectorLayout);
    }

    public static bool IsActive(GameShip owner)
    {
        OwnerState state;
        CoreOwnerContext context = CoreClassRuntime.CurrentContext;
        return owner != null && context != null && context.IsValid && context.ClassId == CoreClassId.Orrery &&
            ReferenceEquals(context.Ship, owner) &&
            owners.TryGetValue(owner, out state) &&
            state != null &&
            state.Resolved != null &&
            state.Resolved.Active;
    }

    public static ResolvedState GetResolvedState(GameShip owner)
    {
        OwnerState state;
        if (IsActive(owner) && owners.TryGetValue(owner, out state) &&
            state != null && state.Resolved != null)
        {
            return state.Resolved;
        }

        return inactiveState;
    }

    public static void Activate(GameShip owner, ResolvedState resolved)
    {
        if (owner == null)
            return;

        if (resolved == null)
            resolved = CreateBaselineResolvedState();
        else if (!resolved.Active)
        {
            Deactivate(owner);
            return;
        }

        OwnerState state;
        if (!owners.TryGetValue(owner, out state) || state == null)
        {
            state = new OwnerState();
            owners[owner] = state;
        }

        state.Resolved = resolved;
        state.Revision++;
        Revision++;

        OrrerySpellRegistry.RegisterDefaults();
        OrreryCasting.Begin(owner, resolved.FormulaSatelliteCount);
    }

    public static void Activate(GameShip owner)
    {
        Activate(owner, CreateBaselineResolvedState());
    }

    public static void SetResolvedState(GameShip owner, ResolvedState resolved)
    {
        if (owner == null || resolved == null)
            return;

        if (!resolved.Active)
        {
            Deactivate(owner);
            return;
        }

        Activate(owner, resolved);
    }

    public static void Deactivate(GameShip owner)
    {
        if (owner == null)
            return;

        // Dispose active/hidden spell machinery synchronously with the class
        // lifecycle. Waiting for OrreryController.FixedUpdate leaves a stale
        // activation window after respec/class switch and can strand a live FF
        // projectile or Tesla beam while Core already considers the class gone.
        OrrerySpellLifetime.ForgetOwner(owner);
        OrrerySpellRuntime.Forget(owner);
        OrrerySectorPresentation.Hide(owner);

        owners.Remove(owner);
        OrreryCasting.Forget(owner);
        OrreryOrbit.Forget(owner);
        OrrerySatellites.InvalidateLiveSatellites(owner);
        OrrerySatellites.InvalidateIntent(owner);
        OrreryCombat.ResetOwner(owner);
        Revision++;
    }

    public static void Reset()
    {
        // Reset hidden spell machinery before dropping logical owner tables so
        // native projectile/beam cleanup still has its authoritative owner data.
        OrrerySpellRuntime.Reset();
        OrrerySectorPresentation.Hide();
        owners.Clear();
        OrreryCasting.Reset();
        OrreryOrbit.Reset();
        OrrerySatellites.Reset();
        Revision++;
    }

    internal static float NormalizeDegrees(float degrees)
    {
        degrees %= 360f;
        if (degrees < 0f)
            degrees += 360f;
        return degrees;
    }
}
