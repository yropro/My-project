using HarmonyLib;
using StarVortex;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

/// <summary>
/// Owner capture and compact network codecs for the original Orrery spell trio.
///
/// Magma Cannon, Tesla Coil and Cone of Cold are executed through hidden native
/// adapters that deliberately do not occupy replicated player weapon slots. This
/// layer publishes only irreducible presentation results. Remote reconstruction
/// lives in OrreryLegacySpellRemotePresentation and never authors gameplay.
/// </summary>
public static class OrreryLegacySpellPresentation
{
    public static class Tuning
    {
        // A discrete cast stays present long enough to survive several dropped
        // 20 Hz ship-state packets. This is event redundancy, not gameplay state.
        public const float DiscreteEventPublishSeconds = 0.75f;
        public const int MaxTeslaSegments = 3;
    }

    private const int MagmaSpellId = 1;
    private const int TeslaSpellId = 2;

    private static readonly OrreryNetwork.Channel<MagmaWireState> MagmaChannel =
        new OrreryNetwork.Channel<MagmaWireState>(
            OrreryPresentationNetwork.CodecMagmaCannon, WireMagma);

    private static readonly OrreryNetwork.Channel<CryoWireState> CryoChannel =
        new OrreryNetwork.Channel<CryoWireState>(
            OrreryPresentationNetwork.CodecConeOfCold, WireCryo);

    private static readonly OrreryNetwork.Channel<TeslaWireState> TeslaChannel =
        new OrreryNetwork.Channel<TeslaWireState>(
            OrreryPresentationNetwork.CodecTeslaCoil, WireTesla);

    internal static void WireCryo(ref CoreWire wire, ref CryoWireState state)
    {
        wire.Position(ref state.Origin);
        wire.Float(ref state.AimDegrees);
    }

    /// <summary>
    /// Segment count leads, so both directions take the same branches. Width is
    /// Positive rather than Float because a zero or negative beam width was
    /// already rejected by the hand-written reader; the constraint now lives in
    /// the declaration instead of being restated on each side.
    /// </summary>
    internal static void WireTesla(ref CoreWire wire, ref TeslaWireState state)
    {
        wire.Count(ref state.SegmentCount);
        if (!wire.Ok)
            return;
        if (state.SegmentCount < 1 || state.SegmentCount > Tuning.MaxTeslaSegments)
        {
            wire.Fail();
            return;
        }

        wire.Positive(ref state.Width);
        wire.Position(ref state.Start0);
        wire.Position(ref state.End0);
        if (state.SegmentCount > 1)
        {
            wire.Position(ref state.Start1);
            wire.Position(ref state.End1);
        }
        if (state.SegmentCount > 2)
        {
            wire.Position(ref state.Start2);
            wire.Position(ref state.End2);
        }
    }

    internal static void WireMagma(ref CoreWire wire, ref MagmaWireState state)
    {
        wire.Flags(ref state.ProjectilePresent, ref state.ExplosionPresent);
        if (state.ProjectilePresent)
        {
            wire.Position(ref state.ProjectilePosition);
            wire.Float(ref state.ProjectileAngleDegrees);
        }
        if (state.ExplosionPresent)
        {
            wire.Position(ref state.ExplosionPosition);
            wire.Positive(ref state.ExplosionRadiusWorld);
        }
    }

    internal struct MagmaWireState
    {
        public uint Generation;
        public bool ProjectilePresent;
        public Vector2 ProjectilePosition;
        public float ProjectileAngleDegrees;
        public bool ExplosionPresent;
        public Vector2 ExplosionPosition;
        public float ExplosionRadiusWorld;
    }

    internal struct CryoWireState
    {
        public uint Generation;
        public Vector2 Origin;
        public float AimDegrees;
    }

    internal struct TeslaWireState
    {
        public uint Generation;
        public int SegmentCount;
        public float Width;
        public Vector2 Start0, End0;
        public Vector2 Start1, End1;
        public Vector2 Start2, End2;

        public Vector2 GetStart(int index)
        {
            switch (index)
            {
                case 0: return Start0;
                case 1: return Start1;
                case 2: return Start2;
                default: return Vector2.zero;
            }
        }

        public Vector2 GetEnd(int index)
        {
            switch (index)
            {
                case 0: return End0;
                case 1: return End1;
                case 2: return End2;
                default: return Vector2.zero;
            }
        }

        public void SetSegment(int index, Vector2 start, Vector2 end)
        {
            switch (index)
            {
                case 0: Start0 = start; End0 = end; break;
                case 1: Start1 = start; End1 = end; break;
                case 2: Start2 = start; End2 = end; break;
            }
        }
    }

    private sealed class OwnerCapture
    {
        public uint MagmaGeneration;
        public bool MagmaPendingProjectile;
        public Projectile MagmaProjectile;
        public Vector2 MagmaExplosionPosition;
        public float MagmaExplosionRadiusWorld;
        public float MagmaExplosionPublishUntil;

        public uint TeslaGeneration;
        public BeamWeapon TeslaWeapon;

        public uint CryoGeneration;
        public Vector2 CryoOrigin;
        public float CryoAimDegrees;
        public float CryoPublishUntil;
    }

    private static readonly Dictionary<GameShip, OwnerCapture> owners =
        new Dictionary<GameShip, OwnerCapture>(4);
    private static readonly Dictionary<Projectile, GameShip> magmaOwners =
        new Dictionary<Projectile, GameShip>(4);

    private static readonly Vector2[] teslaStarts =
        new Vector2[Tuning.MaxTeslaSegments];
    private static readonly Vector2[] teslaEnds =
        new Vector2[Tuning.MaxTeslaSegments];
    private static float teslaWidth;

    private static readonly FieldInfo RuntimeOwnersField =
        AccessTools.Field(typeof(OrrerySpellRuntime), "owners");
    private static readonly Type RuntimeOwnerStateType =
        typeof(OrrerySpellRuntime).GetNestedType(
            "OwnerState",
            BindingFlags.NonPublic);
    private static readonly Type RuntimeVirtualWeaponType =
        typeof(OrrerySpellRuntime).GetNestedType(
            "VirtualWeapon",
            BindingFlags.NonPublic);
    private static readonly FieldInfo RuntimeTeslaField =
        RuntimeOwnerStateType == null
            ? null
            : AccessTools.Field(RuntimeOwnerStateType, "Tesla");
    private static readonly FieldInfo RuntimeVirtualWeaponField =
        RuntimeVirtualWeaponType == null
            ? null
            : AccessTools.Field(RuntimeVirtualWeaponType, "Weapon");
    private static readonly FieldInfo BeamScriptField =
        AccessTools.Field(typeof(BeamWeapon), "beamScript");
    private static readonly FieldInfo BeamLineField =
        AccessTools.Field(typeof(Beam), "beamLineRenderer");
    private static readonly FieldInfo BeamSubBeamField =
        AccessTools.Field(typeof(Beam), "subBeam");

    private static bool warnedRuntimeReflection;
    private static uint magmaGenerationCounter;
    private static uint teslaGenerationCounter;
    private static uint cryoGenerationCounter;

    public static void BeginMagma(GameShip owner)
    {
        if (!IsLocalOrreryOwner(owner))
            return;

        OwnerCapture capture = GetOrCreateOwner(owner);
        if (capture.MagmaProjectile != null)
            magmaOwners.Remove(capture.MagmaProjectile);

        capture.MagmaGeneration = NextGeneration(ref magmaGenerationCounter);
        capture.MagmaPendingProjectile = true;
        capture.MagmaProjectile = null;
        capture.MagmaExplosionPosition = Vector2.zero;
        capture.MagmaExplosionRadiusWorld = 0f;
        capture.MagmaExplosionPublishUntil = 0f;
    }

    public static void CompleteMagmaAttempt(GameShip owner, bool succeeded)
    {
        OwnerCapture capture;
        if (owner == null || !owners.TryGetValue(owner, out capture) ||
            capture == null)
        {
            return;
        }

        if (succeeded)
            return;

        capture.MagmaPendingProjectile = false;
        if (capture.MagmaProjectile != null)
            magmaOwners.Remove(capture.MagmaProjectile);
        capture.MagmaProjectile = null;
    }

    public static void ObserveProjectile(Launcher launcher, Projectile projectile)
    {
        if (launcher == null || projectile == null ||
            !(projectile is ExplosiveProjectile) ||
            launcher.damageType != Damageable.DamageType.Thermal ||
            !OrreryWeaponSuppression.IsRuntimeAdapter(launcher))
        {
            return;
        }

        GameShip owner = launcher.parentShip;
        OwnerCapture capture;
        if (owner == null || !owners.TryGetValue(owner, out capture) ||
            capture == null || !capture.MagmaPendingProjectile)
        {
            return;
        }

        if (capture.MagmaProjectile != null &&
            !object.ReferenceEquals(capture.MagmaProjectile, projectile))
        {
            magmaOwners.Remove(capture.MagmaProjectile);
        }

        capture.MagmaPendingProjectile = false;
        capture.MagmaProjectile = projectile;
        capture.MagmaExplosionRadiusWorld = Mathf.Max(
            0f,
            launcher.ExplosiveRadius);
        magmaOwners[projectile] = owner;
    }

    public static void RecordMagmaExplosion(
        ExplosiveProjectile projectile,
        Vector2 position)
    {
        GameShip owner;
        OwnerCapture capture;
        if (projectile == null ||
            !magmaOwners.TryGetValue(projectile, out owner) ||
            owner == null ||
            !owners.TryGetValue(owner, out capture) ||
            capture == null)
        {
            return;
        }

        if (!OrreryNetwork.IsFinite(position))
            position = projectile.transform.position;

        capture.MagmaExplosionPosition = position;
        capture.MagmaExplosionPublishUntil =
            Time.unscaledTime + Tuning.DiscreteEventPublishSeconds;
        if (object.ReferenceEquals(capture.MagmaProjectile, projectile))
            capture.MagmaProjectile = null;
        capture.MagmaPendingProjectile = false;
        magmaOwners.Remove(projectile);
    }

    public static void ForgetMagmaProjectile(Projectile projectile)
    {
        GameShip owner;
        if (projectile == null || !magmaOwners.TryGetValue(projectile, out owner))
            return;

        OwnerCapture capture;
        if (owner != null && owners.TryGetValue(owner, out capture) &&
            capture != null &&
            object.ReferenceEquals(capture.MagmaProjectile, projectile))
        {
            capture.MagmaProjectile = null;
            capture.MagmaPendingProjectile = false;
        }
        magmaOwners.Remove(projectile);
    }

    public static void RecordCryo(GameShip owner)
    {
        if (!IsLocalOrreryOwner(owner))
            return;

        OwnerCapture capture = GetOrCreateOwner(owner);
        capture.CryoGeneration = NextGeneration(ref cryoGenerationCounter);
        capture.CryoOrigin = owner.transform.position;
        capture.CryoAimDegrees = ResolveLocalAimDegrees(owner);
        capture.CryoPublishUntil =
            Time.unscaledTime + Tuning.DiscreteEventPublishSeconds;
    }

    public static void RecordTesla(GameShip owner)
    {
        if (!IsLocalOrreryOwner(owner))
            return;

        OwnerCapture capture = GetOrCreateOwner(owner);
        capture.TeslaGeneration = NextGeneration(ref teslaGenerationCounter);
        capture.TeslaWeapon = ResolveRuntimeTesla(owner);
    }

    public static void ForgetOwner(GameShip owner)
    {
        if (object.ReferenceEquals(owner, null))
            return;

        OwnerCapture capture;
        if (owners.TryGetValue(owner, out capture) && capture != null &&
            capture.MagmaProjectile != null)
        {
            magmaOwners.Remove(capture.MagmaProjectile);
        }
        owners.Remove(owner);
    }

    public static void Reset()
    {
        owners.Clear();
        magmaOwners.Clear();
        warnedRuntimeReflection = false;
        magmaGenerationCounter = 0u;
        teslaGenerationCounter = 0u;
        cryoGenerationCounter = 0u;
    }

    public static void Publish(GameShip owner)
    {
        if (!IsLocalOrreryOwner(owner))
            return;

        OwnerCapture capture;
        if (!owners.TryGetValue(owner, out capture) || capture == null)
            return;

        OrreryNetwork.PresentationState baseState;
        bool hasBase = OrreryNetwork.TryBuildLocalState(owner, out baseState);
        ushort activeSpellId = hasBase &&
            baseState.Phase == OrreryCastPhase.Invoking
                ? baseState.SpellId
                : (ushort)0;

        // A reflected Magma projectile deliberately outlives its original Orrery
        // cast. Keep publishing that native projectile from the original owner's
        // custom presentation stream until it actually retires; hidden virtual
        // launchers cannot register it in Star Vortex's native NetProjectile lane.
        bool hasLiveMagmaProjectile =
            capture.MagmaProjectile != null &&
            capture.MagmaProjectile.gameObject != null &&
            capture.MagmaProjectile.gameObject.activeInHierarchy &&
            !capture.MagmaProjectile.IsDestroying() &&
            !capture.MagmaProjectile.hasExploded;
        bool hasMagmaExplosionTail =
            capture.MagmaGeneration != 0u &&
            Time.unscaledTime < capture.MagmaExplosionPublishUntil;

        // Current active presentation gets first claim on the multiplex bank.
        // Reflected Magma is independent, so a later Tesla channel can coexist
        // with the still-flying reflected projectile instead of suppressing it.
        if (activeSpellId == TeslaSpellId)
            PublishTesla(owner, capture);

        if (activeSpellId == MagmaSpellId ||
            hasLiveMagmaProjectile ||
            hasMagmaExplosionTail)
        {
            PublishMagma(capture);
        }

        if (capture.CryoGeneration != 0u &&
            Time.unscaledTime < capture.CryoPublishUntil)
        {
            PublishCryo(capture);
        }
    }

    private static void PublishMagma(OwnerCapture capture)
    {
        if (capture == null || capture.MagmaGeneration == 0u)
            return;

        bool projectilePresent =
            capture.MagmaProjectile != null &&
            capture.MagmaProjectile.gameObject != null &&
            capture.MagmaProjectile.gameObject.activeInHierarchy &&
            !capture.MagmaProjectile.IsDestroying() &&
            !capture.MagmaProjectile.hasExploded;
        bool explosionPresent =
            Time.unscaledTime < capture.MagmaExplosionPublishUntil;

        if (!projectilePresent && !explosionPresent)
            return;

        MagmaWireState state = new MagmaWireState
        {
            ProjectilePresent = projectilePresent,
            ExplosionPresent = explosionPresent,
            ExplosionPosition = capture.MagmaExplosionPosition,
            ExplosionRadiusWorld = capture.MagmaExplosionRadiusWorld
        };
        if (projectilePresent)
        {
            state.ProjectilePosition = capture.MagmaProjectile.transform.position;
            state.ProjectileAngleDegrees = capture.MagmaProjectile.transform.eulerAngles.z;
        }
        MagmaChannel.Publish(capture.MagmaGeneration, ref state);
    }

    private static void PublishCryo(OwnerCapture capture)
    {
        // Finite checks are not repeated here: Position and Float refuse
        // non-finite values, so the encode fails and nothing is published.
        if (capture == null || capture.CryoGeneration == 0u)
            return;

        CryoWireState state = new CryoWireState
        {
            Origin = capture.CryoOrigin,
            AimDegrees = capture.CryoAimDegrees
        };
        CryoChannel.Publish(capture.CryoGeneration, ref state);
    }

    private static void PublishTesla(GameShip owner, OwnerCapture capture)
    {
        if (capture == null || capture.TeslaGeneration == 0u)
            return;

        if (capture.TeslaWeapon == null ||
            !object.ReferenceEquals(capture.TeslaWeapon.parentShip, owner))
        {
            capture.TeslaWeapon = ResolveRuntimeTesla(owner);
        }

        int segmentCount = CaptureTeslaSegments(capture.TeslaWeapon);
        if (segmentCount <= 0)
            return;

        // Width and segment bounds are enforced by WireTesla; the channel picks
        // the record count and refuses a payload that will not fit.
        TeslaWireState state = default(TeslaWireState);
        state.SegmentCount = segmentCount;
        state.Width = teslaWidth;
        for (int i = 0; i < segmentCount; i++)
            state.SetSegment(i, teslaStarts[i], teslaEnds[i]);

        TeslaChannel.Publish(capture.TeslaGeneration, ref state);
    }

    private static int CaptureTeslaSegments(BeamWeapon weapon)
    {
        if (weapon == null || BeamScriptField == null ||
            BeamLineField == null || BeamSubBeamField == null)
        {
            return 0;
        }

        Beam beam = BeamScriptField.GetValue(weapon) as Beam;
        int count = 0;
        teslaWidth = 0f;

        while (beam != null && count < Tuning.MaxTeslaSegments)
        {
            LineRenderer line = BeamLineField.GetValue(beam) as LineRenderer;
            if (beam.active && line != null && line.enabled &&
                line.positionCount >= 2)
            {
                Vector3 start3 = line.GetPosition(0);
                Vector3 end3 = line.GetPosition(line.positionCount - 1);
                if (!line.useWorldSpace)
                {
                    start3 = line.transform.TransformPoint(start3);
                    end3 = line.transform.TransformPoint(end3);
                }

                Vector2 start = start3;
                Vector2 end = end3;
                if (OrreryNetwork.IsFinite(start) &&
                    OrreryNetwork.IsFinite(end))
                {
                    if (count == 0)
                        teslaWidth = Mathf.Max(0.001f, line.widthMultiplier);
                    teslaStarts[count] = start;
                    teslaEnds[count] = end;
                    count++;
                }
            }

            beam = BeamSubBeamField.GetValue(beam) as Beam;
        }

        return count;
    }

    internal static bool TryReadMagma(
        GameShip owner,
        out MagmaWireState state)
    {
        state = default(MagmaWireState);

        uint generation;
        if (!MagmaChannel.TryRead(owner, ref state, out generation) ||
            (!state.ProjectilePresent && !state.ExplosionPresent))
        {
            state = default(MagmaWireState);
            return false;
        }
        state.Generation = generation;
        return true;
    }

    internal static bool TryReadCryo(
        GameShip owner,
        out CryoWireState state)
    {
        state = default(CryoWireState);

        uint generation;
        if (!CryoChannel.TryRead(owner, ref state, out generation) ||
            generation == 0u)
        {
            state = default(CryoWireState);
            return false;
        }
        state.Generation = generation;
        return true;
    }

    internal static bool TryReadTesla(
        GameShip owner,
        out TeslaWireState state)
    {
        state = default(TeslaWireState);

        uint generation;
        if (!TeslaChannel.TryRead(owner, ref state, out generation) ||
            generation == 0u)
        {
            state = default(TeslaWireState);
            return false;
        }
        state.Generation = generation;
        return true;
    }

    private static OwnerCapture GetOrCreateOwner(GameShip owner)
    {
        OwnerCapture capture;
        if (!owners.TryGetValue(owner, out capture) || capture == null)
        {
            capture = new OwnerCapture();
            owners[owner] = capture;
        }
        return capture;
    }

    private static bool IsLocalOrreryOwner(GameShip owner)
    {
        CoreOwnerContext context = CoreClassRuntime.CurrentContext;
        return owner != null && context != null && context.IsValid &&
            context.ClassId == CoreClassId.Orrery &&
            object.ReferenceEquals(context.Ship, owner) &&
            OrreryRuntime.IsActive(owner);
    }

    private static float ResolveLocalAimDegrees(GameShip owner)
    {
        Vector2 direction = owner == null
            ? Vector2.right
            : (Vector2)owner.transform.right;
        InputController input = InputController.instance;
        if (input != null && owner != null &&
            object.ReferenceEquals(input.controlShip, owner))
        {
            Vector2 delta = input.GetCursorWorldPoint() -
                (Vector2)owner.transform.position;
            if (delta.sqrMagnitude > 0.0001f)
                direction = delta.normalized;
        }
        return Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg;
    }

    private static BeamWeapon ResolveRuntimeTesla(GameShip owner)
    {
        if (owner == null || RuntimeOwnersField == null ||
            RuntimeTeslaField == null || RuntimeVirtualWeaponField == null)
        {
            WarnRuntimeReflection();
            return null;
        }

        try
        {
            IDictionary runtimeOwners = RuntimeOwnersField.GetValue(null) as IDictionary;
            if (runtimeOwners == null || !runtimeOwners.Contains(owner))
                return null;

            object ownerState = runtimeOwners[owner];
            object virtualWeapon = ownerState == null
                ? null
                : RuntimeTeslaField.GetValue(ownerState);
            return virtualWeapon == null
                ? null
                : RuntimeVirtualWeaponField.GetValue(virtualWeapon) as BeamWeapon;
        }
        catch (Exception)
        {
            WarnRuntimeReflection();
            return null;
        }
    }

    private static void WarnRuntimeReflection()
    {
        if (warnedRuntimeReflection)
            return;
        warnedRuntimeReflection = true;
        Debug.LogWarning(
            "[Orrery] Tesla presentation could not resolve the hidden runtime " +
            "adapter. Gameplay remains authoritative; remote Tesla VFX may be absent.");
    }

    private static uint NextGeneration(ref uint counter)
    {
        counter++;
        if (counter == 0u)
            counter++;
        return counter;
    }

}

[HarmonyPatch(typeof(OrrerySpellRuntime), nameof(OrrerySpellRuntime.ExecuteMagma))]
public static class OrreryMagmaPresentationCapturePatch
{
    public static void Prefix(GameShip owner)
    {
        OrreryLegacySpellPresentation.BeginMagma(owner);
    }

    public static void Postfix(GameShip owner, bool __result)
    {
        OrreryLegacySpellPresentation.CompleteMagmaAttempt(owner, __result);
    }
}

[HarmonyPatch(typeof(OrrerySpellRuntime), nameof(OrrerySpellRuntime.ExecuteCryo))]
public static class OrreryCryoPresentationCapturePatch
{
    public static void Postfix(GameShip owner, bool __result)
    {
        if (__result)
            OrreryLegacySpellPresentation.RecordCryo(owner);
    }
}

[HarmonyPatch(typeof(OrrerySpellRuntime), nameof(OrrerySpellRuntime.ExecuteTesla))]
public static class OrreryTeslaPresentationCapturePatch
{
    public static void Postfix(GameShip owner, bool __result)
    {
        if (__result)
            OrreryLegacySpellPresentation.RecordTesla(owner);
    }
}

[HarmonyPatch(typeof(OrrerySpellRuntime), nameof(OrrerySpellRuntime.OnProjectileAdded))]
public static class OrreryMagmaPresentationProjectilePatch
{
    public static void Postfix(Launcher launcher, Projectile projectile)
    {
        OrreryLegacySpellPresentation.ObserveProjectile(launcher, projectile);
    }
}

[HarmonyPatch(typeof(OrrerySpellRuntime), nameof(OrrerySpellRuntime.OnExplosiveProjectileHit))]
public static class OrreryMagmaPresentationHitPatch
{
    public static void Postfix(ExplosiveProjectile projectile)
    {
        if (projectile == null)
            return;

        // Native ExplosiveProjectile.Explode anchors its area at the projectile,
        // not the collider contact point. Publish that exact presentation pose.
        OrreryLegacySpellPresentation.RecordMagmaExplosion(
            projectile,
            projectile.transform.position);
    }
}

[HarmonyPatch(typeof(ExplosiveProjectile), nameof(ExplosiveProjectile.TimedDestroy))]
public static class OrreryMagmaPresentationExpiryPatch
{
    public static void Prefix(ExplosiveProjectile __instance)
    {
        if (__instance != null && __instance.explodeOnExpiry &&
            !__instance.netRendered)
        {
            OrreryLegacySpellPresentation.RecordMagmaExplosion(
                __instance,
                __instance.transform.position);
        }
    }
}

[HarmonyPatch(typeof(Projectile), nameof(Projectile.PoolDestroy))]
public static class OrreryMagmaPresentationPoolPatch
{
    public static void Postfix(Projectile __instance)
    {
        OrreryLegacySpellPresentation.ForgetMagmaProjectile(__instance);
    }
}

[HarmonyPatch(typeof(OrrerySpellRuntime), nameof(OrrerySpellRuntime.Forget))]
public static class OrreryLegacyPresentationOwnerForgetPatch
{
    public static void Postfix(GameShip owner)
    {
        OrreryLegacySpellPresentation.ForgetOwner(owner);
    }
}

[HarmonyPatch(typeof(OrrerySpellRuntime), nameof(OrrerySpellRuntime.Reset))]
public static class OrreryLegacyPresentationOwnerResetPatch
{
    public static void Postfix()
    {
        OrreryLegacySpellPresentation.Reset();
    }
}
