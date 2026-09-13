using HarmonyLib;
using StarVortex;
using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>
/// Fire + Ice Orrery support spell.
///
/// The cast resolves its complete gameplay profile once, then applies that exact
/// snapshot to the allied player nearest the cursor. If no eligible ally is near
/// the cursor, the caster receives the buff. Remote allies are never mutated via
/// replicas: their owning client receives the immutable resolved profile through
/// Core's reliable cross-owner gameplay-intent lane.
/// </summary>
public static class OrreryColdFusion
{
    // Stable Core-wide effect id: high byte mirrors CoreClassId.Orrery.
    public const ushort TimedEffectId = 0x0201;
    public const ushort CrossOwnerEffectId = TimedEffectId;

    private struct Snapshot
    {
        public float EffectiveLevel;
        public float DurationSeconds;
        public float MovementMultiplier;
        public float HeatGenerationMultiplier;
        public float WeaponCadenceMultiplier;
        public float ShieldGrantPerSecond;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct FloatBits
    {
        [FieldOffset(0)] public float Float;
        [FieldOffset(0)] public uint UInt;
    }

    private static bool handlerRegistered;
    private static readonly GameShip[] targetScratch =
        new GameShip[OrrerySpellCompendium.ColdFusion.MaxCandidateShips];

    public static bool Execute(
        GameShip owner,
        OrreryCastInvocation invocation,
        OrrerySpellRegistry.SpellDefinition spell)
    {
        if (owner == null || spell == null || invocation.Execution == null ||
            !invocation.Execution.IsValid || !OrreryRuntime.IsActive(owner) ||
            OrreryController.IsShuffling(owner))
        {
            return false;
        }

        EnsureInitialized();

        Snapshot snapshot;
        if (!TryResolveCast(owner, out snapshot))
            return false;

        GameShip target = FindTarget(owner);
        if (target == null)
            target = owner;

        bool accepted;
        if (object.ReferenceEquals(target, owner))
        {
            accepted = ApplySnapshot(target, snapshot);
        }
        else
        {
            int targetPlayerId;
            if (!CoreNetwork.TryGetPlayerId(target, out targetPlayerId))
                return false;

            accepted = CoreCrossOwnerEffects.RequestGrant(
                targetPlayerId,
                CrossOwnerEffectId,
                PackSnapshot(snapshot));
        }

        if (!accepted)
            return false;

        OrreryCasting.CompleteInvocation(owner, invocation.Execution, 0);
        OrreryController.StartShuffle(owner);
        return true;
    }

    public static void EnsureInitialized()
    {
        if (handlerRegistered)
            return;

        CoreCrossOwnerEffects.RegisterHandler(
            CrossOwnerEffectId,
            ApplyRemoteGrant);
        handlerRegistered = true;
    }

    private static bool ApplyRemoteGrant(
        GameShip localTarget,
        int sourcePlayerId,
        CoreCrossOwnerEffects.GrantPayload payload)
    {
        if (localTarget == null || !localTarget.IsPlayer() ||
            sourcePlayerId < 0)
        {
            return false;
        }

        Snapshot snapshot;
        if (!TryUnpackSnapshot(payload, out snapshot))
            return false;

        return ApplySnapshot(localTarget, snapshot);
    }

    private static bool ApplySnapshot(GameShip target, Snapshot snapshot)
    {
        if (target == null || !target.IsPlayer() ||
            snapshot.DurationSeconds <= 0f)
        {
            return false;
        }

        CoreTimedShipEffects.Profile profile =
            CoreTimedShipEffects.Profile.Identity;
        profile.MovementMultiplier = snapshot.MovementMultiplier;
        profile.HeatGenerationMultiplier = snapshot.HeatGenerationMultiplier;
        profile.WeaponCadenceMultiplier = snapshot.WeaponCadenceMultiplier;
        profile.ShieldGrantPerSecond = snapshot.ShieldGrantPerSecond;

        return CoreTimedShipEffects.ApplyOrRefresh(
            target,
            TimedEffectId,
            snapshot.DurationSeconds,
            profile);
    }

    private static bool TryResolveCast(GameShip owner, out Snapshot snapshot)
    {
        snapshot = default(Snapshot);

        OrreryFocusProfile.Resolved fire =
            default(OrreryFocusProfile.Resolved);
        OrreryFocusProfile.Resolved ice =
            default(OrreryFocusProfile.Resolved);
        if (!OrreryFocusProfile.TryResolve(owner, OrreryElement.Fire, out fire) ||
            !fire.IsValid ||
            !OrreryFocusProfile.TryResolve(owner, OrreryElement.Ice, out ice) ||
            !ice.IsValid)
        {
            return false;
        }

        // FocusResolver stores unfocused player level as a negative sentinel.
        // Magnitude is always the actual level participating in spell scaling.
        float fireLevel = Mathf.Max(1f, Mathf.Abs(fire.EffectiveItemLevel));
        float iceLevel = Mathf.Max(1f, Mathf.Abs(ice.EffectiveItemLevel));
        float weight = Mathf.Clamp01(
            OrrerySpellCompendium.ColdFusion.MixedFocusIceWeight);
        float effectiveLevel = Mathf.Lerp(fireLevel, iceLevel, weight);

        float durationBonus = Mathf.Lerp(
            fire.DurationBonus,
            ice.DurationBonus,
            weight);
        durationBonus *=
            OrrerySpellCompendium.ColdFusion.DurationModifierScale;

        float baseDuration = Mathf.Max(
            0.01f,
            OrrerySpellCompendium.ColdFusion.BaseDurationSeconds);
        float finalDuration = baseDuration *
            Mathf.Max(0f, 1f + durationBonus);
        if (finalDuration <= 0f)
            return false;

        float baseShieldTotal = ShieldBatteryEquivalent(effectiveLevel) *
            Mathf.Max(0f, OrrerySpellCompendium.ColdFusion.ShieldBatteryMultiplier);

        snapshot.EffectiveLevel = effectiveLevel;
        snapshot.DurationSeconds = finalDuration;
        snapshot.MovementMultiplier = Mathf.Max(
            0f,
            1f + OrrerySpellCompendium.ColdFusion.MovementBonusFraction);
        snapshot.HeatGenerationMultiplier = Mathf.Max(
            0f,
            1f - OrrerySpellCompendium.ColdFusion.HeatGenerationReductionFraction);
        snapshot.WeaponCadenceMultiplier = Mathf.Max(
            0.01f,
            1f + OrrerySpellCompendium.ColdFusion.WeaponCadenceBonusFraction);
        snapshot.ShieldGrantPerSecond = Mathf.Max(0f, baseShieldTotal / baseDuration);
        return true;
    }

    private static GameShip FindTarget(GameShip owner)
    {
        if (owner == null || PhysicsController.instance == null)
            return owner;

        Vector2 cursor = GetAimPoint(owner);
        float cursorRadius = OrreryUnits.MetersToWorld(Mathf.Max(
            0f,
            OrrerySpellCompendium.ColdFusion.CursorTargetRadiusMeters));
        if (cursorRadius <= 0f)
            return owner;

        Collider2D[] overlaps = PhysicsController.instance.OverlapCircle(
            cursor,
            cursorRadius);
        if (overlaps == null)
            return owner;

        float maxRangeMeters =
            OrrerySpellCompendium.ColdFusion.MaximumTargetRangeMeters;
        float maxRangeWorld = maxRangeMeters <= 0f
            ? float.PositiveInfinity
            : OrreryUnits.MetersToWorld(maxRangeMeters);
        float maxRangeSquared = maxRangeWorld * maxRangeWorld;

        GameShip best = null;
        float bestCursorDistanceSquared = float.PositiveInfinity;
        int candidateCount = 0;
        int maxCandidates = Mathf.Min(
            targetScratch.Length,
            Mathf.Max(0, OrrerySpellCompendium.ColdFusion.MaxCandidateShips));

        for (int i = 0; i < overlaps.Length && candidateCount < maxCandidates; i++)
        {
            Collider2D collider = overlaps[i];
            if (collider == null)
                break;

            GameObject targetObject = collider.gameObject;
            if (targetObject.CompareTag("Shield") &&
                targetObject.transform.parent != null)
            {
                targetObject = targetObject.transform.parent.gameObject;
            }

            GameShip candidate;
            if (!targetObject.TryGetComponent<GameShip>(out candidate) ||
                !IsEligibleAlly(owner, candidate) ||
                ContainsTarget(candidate, candidateCount))
            {
                continue;
            }

            targetScratch[candidateCount++] = candidate;

            Vector2 candidatePosition = candidate.transform.position;
            if ((candidatePosition - (Vector2)owner.transform.position).sqrMagnitude >
                maxRangeSquared)
            {
                continue;
            }

            float cursorDistanceSquared =
                (candidatePosition - cursor).sqrMagnitude;
            if (cursorDistanceSquared < bestCursorDistanceSquared)
            {
                bestCursorDistanceSquared = cursorDistanceSquared;
                best = candidate;
            }
        }

        for (int i = 0; i < candidateCount; i++)
            targetScratch[i] = null;

        return best != null ? best : owner;
    }

    private static bool ContainsTarget(GameShip candidate, int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (object.ReferenceEquals(targetScratch[i], candidate))
                return true;
        }
        return false;
    }

    private static bool IsEligibleAlly(GameShip owner, GameShip candidate)
    {
        return owner != null && candidate != null &&
            candidate.gameObject != null &&
            candidate.gameObject.activeInHierarchy &&
            !object.ReferenceEquals(owner, candidate) &&
            candidate.IsAnyPlayerShip() &&
            !Faction.IsHostile(owner.faction, candidate.faction);
    }

    private static Vector2 GetAimPoint(GameShip owner)
    {
        InputController input = InputController.instance;
        if (input != null && owner != null &&
            object.ReferenceEquals(input.controlShip, owner))
        {
            return input.GetCursorWorldPoint();
        }

        return owner == null
            ? Vector2.zero
            : (Vector2)owner.transform.position;
    }

    public static float ShieldBatteryEquivalent(float level)
    {
        level = Mathf.Max(1f, level);
        if (level <= OrrerySpellCompendium.ColdFusion.ShieldCurveBreakpointLevel)
        {
            float span = Mathf.Max(
                1f,
                OrrerySpellCompendium.ColdFusion.ShieldCurveBreakpointLevel - 1f);
            return OrrerySpellCompendium.ColdFusion.ShieldCurveLevel1Amount +
                (level - 1f) *
                ((OrrerySpellCompendium.ColdFusion.ShieldCurveBreakpointAmount -
                  OrrerySpellCompendium.ColdFusion.ShieldCurveLevel1Amount) / span);
        }

        float secondSpan = Mathf.Max(
            1f,
            OrrerySpellCompendium.ColdFusion.ShieldCurveSecondReferenceLevel -
                OrrerySpellCompendium.ColdFusion.ShieldCurveBreakpointLevel);
        return OrrerySpellCompendium.ColdFusion.ShieldCurveBreakpointAmount +
            (level - OrrerySpellCompendium.ColdFusion.ShieldCurveBreakpointLevel) *
            ((OrrerySpellCompendium.ColdFusion.ShieldCurveSecondReferenceAmount -
              OrrerySpellCompendium.ColdFusion.ShieldCurveBreakpointAmount) /
             secondSpan);
    }

    private static CoreCrossOwnerEffects.GrantPayload PackSnapshot(
        Snapshot snapshot)
    {
        return new CoreCrossOwnerEffects.GrantPayload(
            PackFloat(snapshot.DurationSeconds),
            PackFloat(snapshot.MovementMultiplier),
            PackFloat(snapshot.HeatGenerationMultiplier),
            PackFloat(snapshot.WeaponCadenceMultiplier),
            PackFloat(snapshot.ShieldGrantPerSecond),
            PackFloat(snapshot.EffectiveLevel));
    }

    private static bool TryUnpackSnapshot(
        CoreCrossOwnerEffects.GrantPayload payload,
        out Snapshot snapshot)
    {
        snapshot = default(Snapshot);
        snapshot.DurationSeconds = UnpackFloat(payload.A);
        snapshot.MovementMultiplier = UnpackFloat(payload.B);
        snapshot.HeatGenerationMultiplier = UnpackFloat(payload.C);
        snapshot.WeaponCadenceMultiplier = UnpackFloat(payload.D);
        snapshot.ShieldGrantPerSecond = UnpackFloat(payload.E);
        snapshot.EffectiveLevel = UnpackFloat(payload.F);

        if (!IsFinite(snapshot.DurationSeconds) ||
            !IsFinite(snapshot.MovementMultiplier) ||
            !IsFinite(snapshot.HeatGenerationMultiplier) ||
            !IsFinite(snapshot.WeaponCadenceMultiplier) ||
            !IsFinite(snapshot.ShieldGrantPerSecond) ||
            !IsFinite(snapshot.EffectiveLevel) ||
            snapshot.DurationSeconds <= 0f || snapshot.EffectiveLevel < 1f)
        {
            return false;
        }

        // Remote payloads are source-authored gameplay snapshots, but sanitize
        // impossible negative values before entering shared stat composition.
        snapshot.MovementMultiplier = Mathf.Max(0f, snapshot.MovementMultiplier);
        snapshot.HeatGenerationMultiplier = Mathf.Max(
            0f,
            snapshot.HeatGenerationMultiplier);
        snapshot.WeaponCadenceMultiplier = Mathf.Max(
            0.01f,
            snapshot.WeaponCadenceMultiplier);
        snapshot.ShieldGrantPerSecond = Mathf.Max(
            0f,
            snapshot.ShieldGrantPerSecond);
        return true;
    }

    private static uint PackFloat(float value)
    {
        return new FloatBits { Float = value }.UInt;
    }

    private static float UnpackFloat(uint value)
    {
        return new FloatBits { UInt = value }.Float;
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }
}

/// <summary>
/// Register the cross-owner handler for every client, including clients whose
/// local player is using a vanilla/other class and can only be the buff target.
/// </summary>
[HarmonyPatch(typeof(WorldController), "PostInit")]
public static class OrreryColdFusionWorldInitPatch
{
    public static void Postfix()
    {
        OrreryColdFusion.EnsureInitialized();
    }
}
