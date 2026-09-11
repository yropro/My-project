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
        public const int BaseSatelliteCount = 3;
        public const int BaseFormulaSatelliteCount = 3;
        public const float SatelliteIncomingDamageMultiplier = 0.50f;

        // The class spec intentionally leaves recovery tuning unresolved. The
        // resolved shape supports both contributions without inventing balance.
        public const float DisabledRecoveryFlatHullPerSecond = 0f;
        public const float DisabledRecoveryMaxHullFractionPerSecond = 0f;

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
        {
            Active = active;
            SatelliteCount = Mathf.Max(0, satelliteCount);
            FormulaSatelliteCount = Mathf.Clamp(
                formulaSatelliteCount,
                0,
                SatelliteCount);
            SatelliteIncomingDamageMultiplier = Mathf.Max(
                0f,
                satelliteIncomingDamageMultiplier);
            DisabledRecoveryFlatHullPerSecond = Mathf.Max(
                0f,
                disabledRecoveryFlatHullPerSecond);
            DisabledRecoveryMaxHullFractionPerSecond = Mathf.Max(
                0f,
                disabledRecoveryMaxHullFractionPerSecond);
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

    /// <summary>
    /// Activates or refreshes one owner with already-resolved stable build data.
    /// Class-selection/progression code owns when this is called.
    /// </summary>
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

        owners.Remove(owner);
        OrreryCasting.Forget(owner);
        OrrerySatellites.InvalidateLiveSatellites(owner);
        OrrerySatellites.InvalidateIntent(owner);
        OrreryCombat.ResetOwner(owner);
        Revision++;
    }

    public static void Reset()
    {
        // World/session teardown also resets the shared combat core through its
        // own verified transport lifecycle. Do not repopulate combat owner state
        // here by attempting per-owner resets after that global reset has run.
        owners.Clear();
        OrreryCasting.Reset();
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
