using HarmonyLib;
using StarVortex;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;
using static StarVortex.Damageable;

// Stellar Converter: first Primary Laser becomes a charged burst beam.
// Native damage type, chaining, leech, piercing and attribution are preserved.
// Release during charge cancels; committed output always finishes.

public static class LeviathanStellarConverter
{
    // =========================================================================
    // BALANCE TUNING
    // =========================================================================
    // Arrays are Rank 1 -> Rank 5.

    public const int MaxRank = 5;

    // Time the source Laser must be held before the burst commits.
    private static readonly float[] ChargeDurationByRank =
    {
        1.50f, // Rank 1
        1.50f, // Rank 2
        1.50f, // Rank 3
        1.50f, // Rank 4
        1.50f  // Rank 5
    };

    // Once committed, native beam firing continues for this full duration.
    private static readonly float[] OutputDurationByRank =
    {
        1.00f, // Rank 1
        1.00f, // Rank 2
        1.00f, // Rank 3
        1.00f, // Rank 4
        1.00f  // Rank 5
    };

    // Multiplier on the source Laser's complete native DamageData packet.
    private static readonly float[] DamageMultiplierByRank =
    {
        3.000f, // Rank 1
        3.225f, // Rank 2
        3.450f, // Rank 3
        3.675f, // Rank 4
        3.900f  // Rank 5
    };

    // Multiplier on native crit chance while the burst is firing.
    private static readonly float[] CritChanceMultiplierByRank =
    {
        1.00f, // Rank 1
        1.00f, // Rank 2
        1.00f, // Rank 3
        1.00f, // Rank 4
        1.00f  // Rank 5
    };

    // Multiplier on the native crit BONUS portion while the burst is firing.
    private static readonly float[] CritDamageMultiplierByRank =
    {
        1.00f, // Rank 1
        1.00f, // Rank 2
        1.00f, // Rank 3
        1.00f, // Rank 4
        1.00f  // Rank 5
    };

    // Multiplier on native status/debuff chance while the burst is firing.
    private static readonly float[] DebuffChanceMultiplierByRank =
    {
        1.00f, // Rank 1
        1.00f, // Rank 2
        1.00f, // Rank 3
        1.00f, // Rank 4
        1.00f  // Rank 5
    };

    // Multiplier on the source Laser's native MaxRange. Beam.DrawBeam reads the
    // same value, so this scales both mechanical range and visual length.
    private static readonly float[] LengthMultiplierByRank =
    {
        1.000f, // Rank 1
        1.075f, // Rank 2
        1.150f, // Rank 3
        1.225f, // Rank 4
        1.300f  // Rank 5
    };

    // Multiplier on visual beam width and the matching CircleCast radius.
    private static readonly float[] WidthMultiplierByRank =
    {
        1.000f, // Rank 1
        1.075f, // Rank 2
        1.150f, // Rank 3
        1.225f, // Rank 4
        1.300f  // Rank 5
    };

    // =========================================================================
    // NATIVE MEMBERS
    // =========================================================================

    private static readonly FieldInfo ActivatableActiveField =
        AccessTools.Field(typeof(Activatable), "active");

    private static readonly FieldInfo BeamParentWeaponField =
        AccessTools.Field(typeof(Beam), "parentBeamWeapon");

    private static readonly FieldInfo BeamMaxWidthField =
        AccessTools.Field(typeof(Beam), "maxWidth");

    private static readonly FieldInfo BeamAdditionalMaxWidthField =
        AccessTools.Field(typeof(Beam), "additionalMaxWidth");

    private static readonly FieldInfo BeamMaxEndWidthField =
        AccessTools.Field(typeof(Beam), "maxEndWidth");

    private static readonly FieldInfo BeamLineRendererField =
        AccessTools.Field(typeof(Beam), "beamLineRenderer");

    private static readonly FieldInfo BeamEndLineRendererField =
        AccessTools.Field(typeof(Beam), "beamEndLineRenderer");

    private static readonly FieldInfo BeamSnapChangeField =
        AccessTools.Field(typeof(Beam), "snapChange");

    private static readonly MethodInfo NativePhysicsRaycastMethod =
        AccessTools.Method(
            typeof(PhysicsController),
            "Raycast",
            new Type[]
            {
                typeof(Vector2),
                typeof(Vector2),
                typeof(float)
            }
        );

    private static readonly MethodInfo StellarPhysicsRaycastMethod =
        AccessTools.Method(
            typeof(LeviathanStellarConverter),
            "RaycastForStellarBeam",
            new Type[]
            {
                typeof(PhysicsController),
                typeof(Vector2),
                typeof(Vector2),
                typeof(float)
            }
        );

    // =========================================================================
    // RUNTIME STATE
    // =========================================================================

    private enum Phase
    {
        Idle,
        Charging,
        Firing
    }

    private static Laser sourceLaser;
    private static Phase phase;
    private static float phaseTimer;
    private static bool inputHeld;

    // Distinguishes activation input from native lifecycle calls.
    [ThreadStatic]
    private static int inputStartDepth;

    [ThreadStatic]
    private static GameShip inputStartShip;

    [ThreadStatic]
    private static int inputStopDepth;

    [ThreadStatic]
    private static GameShip inputStopShip;

    // Scoped only to native Beam raycast methods.
    [ThreadStatic]
    private static Beam currentCastBeam;

    public struct BeamCastState
    {
        public Beam previous;
    }

    // =========================================================================
    // SOURCE / RANK
    // =========================================================================

    public static bool TryGetRank(GameShip player, out int rank)
    {
        rank = 0;

        if (player == null ||
            LeviathanMod.Controller == null ||
            WorldController.instance == null ||
            WorldController.instance.GetCurrentPlayerShip() != player)
        {
            return false;
        }

        Pilot pilot = GameShip.GetPlayerSourcePilot(player);

        if (pilot == null ||
            pilot.GetUpgradeLevel(LeviathanMod.GrowthUpgrade) < 1 ||
            LeviathanMod.Controller.GetActiveSectionCount(player) < 5)
        {
            return false;
        }

        rank = Mathf.Clamp(
            pilot.GetUpgradeLevel(LeviathanMod.StellarConverterUpgrade),
            0,
            MaxRank
        );

        return rank >= 1;
    }

    public static Laser FindSourceLaser(GameShip player)
    {
        if (player == null || player.slots == null)
            return null;

        for (int i = 0; i < player.slots.Length; i++)
        {
            Slot slot = player.slots[i];

            if (slot == null ||
                slot.type != Item.Type.PrimaryWeapon ||
                slot.equippable == null)
            {
                continue;
            }

            Laser laser = slot.equippable as Laser;

            if (laser == null || laser.type != Item.Type.PrimaryWeapon)
                continue;

            return laser;
        }

        return null;
    }

    private static bool TryGetContext(
        Laser laser,
        out GameShip player,
        out int rank)
    {
        player = laser == null ? null : laser.parentShip;
        rank = 0;

        if (laser == null ||
            player == null ||
            laser.type != Item.Type.PrimaryWeapon ||
            !TryGetRank(player, out rank))
        {
            return false;
        }

        return ReferenceEquals(FindSourceLaser(player), laser);
    }

    private static bool IsFiringSource(
        BeamWeapon beamWeapon,
        out int rank)
    {
        rank = 0;

        Laser laser = beamWeapon as Laser;
        GameShip player;

        return laser != null &&
            ReferenceEquals(sourceLaser, laser) &&
            phase == Phase.Firing &&
            TryGetContext(laser, out player, out rank);
    }

    // =========================================================================
    // INPUT / STATE MACHINE
    // =========================================================================

    public static void EnterInputStart(GameShip ship)
    {
        if (inputStartDepth == 0)
            inputStartShip = ship;

        inputStartDepth++;
    }

    public static void ExitInputStart()
    {
        if (inputStartDepth <= 0)
            return;

        inputStartDepth--;

        if (inputStartDepth == 0)
            inputStartShip = null;
    }

    public static void EnterInputStop(GameShip ship)
    {
        if (inputStopDepth == 0)
            inputStopShip = ship;

        inputStopDepth++;
    }

    public static void ExitInputStop()
    {
        if (inputStopDepth <= 0)
            return;

        inputStopDepth--;

        if (inputStopDepth == 0)
            inputStopShip = null;
    }

    public static bool InterceptNativeActivate(Activatable activatable)
    {
        if (inputStartDepth <= 0)
            return false;

        Laser laser = activatable as Laser;
        GameShip player;
        int rank;

        if (laser == null ||
            !TryGetContext(laser, out player, out rank) ||
            player != inputStartShip)
        {
            return false;
        }

        SelectSource(laser);
        inputHeld = true;

        if (phase == Phase.Idle)
        {
            phase = Phase.Charging;
            phaseTimer = 0f;
        }

        SetNativeActive(laser, false);
        return true;
    }

    public static bool InterceptNativeDeactivate(Activatable activatable)
    {
        if (inputStopDepth <= 0)
            return false;

        Laser laser = activatable as Laser;
        GameShip player;
        int rank;

        if (laser == null ||
            !TryGetContext(laser, out player, out rank) ||
            player != inputStopShip)
        {
            return false;
        }

        SelectSource(laser);
        inputHeld = false;

        // Releasing before charge completes cancels it. Once firing, the shot
        // remains committed until OutputDurationByRank expires.
        if (phase == Phase.Charging)
        {
            phase = Phase.Idle;
            phaseTimer = 0f;
            SetNativeActive(laser, false);
        }

        return true;
    }

    public static void PrepareFixedUpdate(Laser laser)
    {
        GameShip player;
        int rank;

        if (!TryGetContext(laser, out player, out rank))
        {
            if (ReferenceEquals(sourceLaser, laser))
                Reset();

            return;
        }

        SelectSource(laser);

        if (phase == Phase.Charging && !inputHeld)
        {
            phase = Phase.Idle;
            phaseTimer = 0f;
        }

        SetNativeActive(laser, phase == Phase.Firing);
    }

    public static void CompleteFixedUpdate(Laser laser)
    {
        GameShip player;
        int rank;

        if (!ReferenceEquals(sourceLaser, laser) ||
            !TryGetContext(laser, out player, out rank))
        {
            return;
        }

        if (phase == Phase.Charging)
        {
            if (!inputHeld)
            {
                phase = Phase.Idle;
                phaseTimer = 0f;
                return;
            }

            if (!laser.CanActivate())
            {
                // Native activation restrictions still gate charging.
                phaseTimer = 0f;
                return;
            }

            phaseTimer += Time.fixedDeltaTime;

            if (phaseTimer >= GetRankValue(ChargeDurationByRank, rank))
            {
                phase = Phase.Firing;
                phaseTimer = 0f;
            }

            return;
        }

        if (phase != Phase.Firing)
            return;

        if (!laser.CanActivate())
        {
            SetNativeActive(laser, false);
            phase = inputHeld ? Phase.Charging : Phase.Idle;
            phaseTimer = 0f;
            return;
        }

        phaseTimer += Time.fixedDeltaTime;

        if (phaseTimer >= GetRankValue(OutputDurationByRank, rank))
        {
            // Finish the current native update before ending output.
            phase = inputHeld ? Phase.Charging : Phase.Idle;
            phaseTimer = 0f;
        }
    }

    private static void SelectSource(Laser laser)
    {
        if (ReferenceEquals(sourceLaser, laser))
            return;

        if (sourceLaser != null)
            SetNativeActive(sourceLaser, false);

        sourceLaser = laser;
        phase = Phase.Idle;
        phaseTimer = 0f;
        inputHeld = false;
    }

    public static void Reset()
    {
        if (sourceLaser != null)
            SetNativeActive(sourceLaser, false);

        sourceLaser = null;
        phase = Phase.Idle;
        phaseTimer = 0f;
        inputHeld = false;
    }

    private static void SetNativeActive(Laser laser, bool active)
    {
        if (laser == null || ActivatableActiveField == null)
            return;

        ActivatableActiveField.SetValue(laser, active);
    }

    // =========================================================================
    // NATIVE STAT SCALING
    // =========================================================================

    public static void ScaleDamagePacket(
        BeamWeapon beamWeapon,
        ref DamageData[] damageData)
    {
        int rank;

        if (!IsFiringSource(beamWeapon, out rank) ||
            damageData == null ||
            damageData.Length == 0)
        {
            return;
        }

        float multiplier = GetRankValue(DamageMultiplierByRank, rank);

        if (Mathf.Approximately(multiplier, 1f))
            return;

        DamageData[] scaled = new DamageData[damageData.Length];

        for (int i = 0; i < damageData.Length; i++)
        {
            DamageData datum = damageData[i];
            datum.damage *= multiplier;
            datum.dps *= multiplier;
            scaled[i] = datum;
        }

        damageData = scaled;
    }

    public static void ScaleCritChance(BeamWeapon beamWeapon, ref float value)
    {
        int rank;

        if (!IsFiringSource(beamWeapon, out rank))
            return;

        value = Mathf.Clamp01(
            value * GetRankValue(CritChanceMultiplierByRank, rank)
        );
    }

    public static void ScaleCritModifier(BeamWeapon beamWeapon, ref float value)
    {
        int rank;

        if (!IsFiringSource(beamWeapon, out rank))
            return;

        value *= GetRankValue(CritDamageMultiplierByRank, rank);
    }

    public static void ScaleDebuffChance(BeamWeapon beamWeapon, ref float value)
    {
        int rank;

        if (!IsFiringSource(beamWeapon, out rank))
            return;

        value = Mathf.Clamp01(
            value * GetRankValue(DebuffChanceMultiplierByRank, rank)
        );
    }

    public static void ScaleBeamMaxRange(Beam beam, ref float value)
    {
        if (beam == null || BeamParentWeaponField == null)
            return;

        BeamWeapon beamWeapon =
            BeamParentWeaponField.GetValue(beam) as BeamWeapon;

        int rank;

        if (!IsFiringSource(beamWeapon, out rank))
            return;

        value *= GetRankValue(LengthMultiplierByRank, rank);
    }

    // =========================================================================
    // BEAM WIDTH / CAST
    // =========================================================================

    public static BeamCastState BeginBeamCast(Beam beam)
    {
        BeamCastState state = new BeamCastState
        {
            previous = currentCastBeam
        };

        currentCastBeam = beam;
        return state;
    }

    public static void EndBeamCast(BeamCastState state)
    {
        currentCastBeam = state.previous;
    }

    // Replaces the two native Beam raycasts only.
    public static RaycastHit2D[] RaycastForStellarBeam(
        PhysicsController physics,
        Vector2 origin,
        Vector2 direction,
        float maxRange)
    {
        if (physics == null)
            return new RaycastHit2D[0];

        float radius;

        if (TryGetMechanicalBeamRadius(currentCastBeam, out radius) &&
            radius > 0f)
        {
            return physics.CircleCast(origin, radius, direction, maxRange);
        }

        return physics.Raycast(origin, direction, maxRange);
    }

    public static void ScaleVisualWidth(Beam beam)
    {
        Laser laser;
        int rank;

        if (!TryGetBeamContext(beam, out laser, out rank))
            return;

        float multiplier = GetRankValue(WidthMultiplierByRank, rank);
        float visualState = GetVisualState(beam);

        LineRenderer line =
            BeamLineRendererField == null
                ? null
                : BeamLineRendererField.GetValue(beam) as LineRenderer;

        LineRenderer end =
            BeamEndLineRendererField == null
                ? null
                : BeamEndLineRendererField.GetValue(beam) as LineRenderer;

        float maxWidth = GetFloat(BeamMaxWidthField, beam);
        float maxEndWidth = GetFloat(BeamMaxEndWidthField, beam);
        float additionalMaxWidth = GetFloat(BeamAdditionalMaxWidthField, beam);

        if (line != null)
            line.widthMultiplier = visualState * maxWidth * multiplier;

        if (end != null)
            end.widthMultiplier = visualState * maxEndWidth * multiplier;

        if (beam.additionalBeam != null)
        {
            beam.additionalBeam.widthMultiplier =
                visualState * additionalMaxWidth * multiplier;
        }
    }

    private static bool TryGetMechanicalBeamRadius(
        Beam beam,
        out float radius)
    {
        radius = 0f;

        Laser laser;
        int rank;

        if (!TryGetBeamContext(beam, out laser, out rank))
            return false;

        float baseWidth = GetFloat(BeamMaxWidthField, beam);

        if (beam.additionalBeam != null)
        {
            baseWidth = Mathf.Max(
                baseWidth,
                GetFloat(BeamAdditionalMaxWidthField, beam)
            );
        }

        radius = 0.5f *
            baseWidth *
            GetVisualState(beam) *
            GetRankValue(WidthMultiplierByRank, rank);

        return radius > 0f;
    }

    private static bool TryGetBeamContext(
        Beam beam,
        out Laser laser,
        out int rank)
    {
        laser = null;
        rank = 0;

        if (beam == null || BeamParentWeaponField == null)
            return false;

        laser = BeamParentWeaponField.GetValue(beam) as Laser;

        return laser != null &&
            ReferenceEquals(sourceLaser, laser) &&
            phase == Phase.Firing &&
            TryGetBeamSourceContext(laser, out rank);
    }

    private static bool TryGetBeamSourceContext(Laser laser, out int rank)
    {
        GameShip player;
        return TryGetContext(laser, out player, out rank);
    }

    private static float GetVisualState(Beam beam)
    {
        float state = Mathf.Clamp01(beam.GetCurrentState());

        bool snapChange =
            BeamSnapChangeField != null &&
            (bool)BeamSnapChangeField.GetValue(beam);

        if (snapChange && state > 0f && state < 1f)
            state = 0.1f;

        return state;
    }

    private static float GetFloat(FieldInfo field, object target)
    {
        if (field == null || target == null)
            return 0f;

        object value = field.GetValue(target);
        return value is float ? (float)value : 0f;
    }

    public static IEnumerable<CodeInstruction> ReplaceNativeBeamRaycasts(
        IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            MethodInfo called = instruction.operand as MethodInfo;

            if (called == NativePhysicsRaycastMethod &&
                StellarPhysicsRaycastMethod != null)
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = StellarPhysicsRaycastMethod;
            }

            yield return instruction;
        }
    }

    private static float GetRankValue(float[] values, int rank)
    {
        if (values == null || values.Length == 0)
            return 0f;

        int index = Mathf.Clamp(rank - 1, 0, values.Length - 1);
        return values[index];
    }
}

// =============================================================================
// INPUT CONTEXT
// =============================================================================

[HarmonyPatch]
public static class LeviathanStellarConverterStartByTypePatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(GameShip),
            "StartActivating",
            new Type[] { typeof(Item.Type) }
        );
    }

    public static void Prefix(GameShip __instance)
    {
        LeviathanStellarConverter.EnterInputStart(__instance);
    }

    public static void Postfix()
    {
        LeviathanStellarConverter.ExitInputStart();
    }
}

[HarmonyPatch]
public static class LeviathanStellarConverterStartByIndexPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(GameShip),
            "StartActivating",
            new Type[] { typeof(int) }
        );
    }

    public static void Prefix(GameShip __instance)
    {
        LeviathanStellarConverter.EnterInputStart(__instance);
    }

    public static void Postfix()
    {
        LeviathanStellarConverter.ExitInputStart();
    }
}

[HarmonyPatch]
public static class LeviathanStellarConverterStopByTypePatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(GameShip),
            "StopActivating",
            new Type[] { typeof(Item.Type?) }
        );
    }

    public static void Prefix(GameShip __instance)
    {
        LeviathanStellarConverter.EnterInputStop(__instance);
    }

    public static void Postfix()
    {
        LeviathanStellarConverter.ExitInputStop();
    }
}

[HarmonyPatch]
public static class LeviathanStellarConverterStopByIndexPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(GameShip),
            "StopActivating",
            new Type[] { typeof(int) }
        );
    }

    public static void Prefix(GameShip __instance)
    {
        LeviathanStellarConverter.EnterInputStop(__instance);
    }

    public static void Postfix()
    {
        LeviathanStellarConverter.ExitInputStop();
    }
}

[HarmonyPatch(typeof(Activatable), "Activate")]
public static class LeviathanStellarConverterActivatePatch
{
    public static bool Prefix(Activatable __instance)
    {
        return !LeviathanStellarConverter.InterceptNativeActivate(__instance);
    }
}

[HarmonyPatch(typeof(Activatable), "Deactivate")]
public static class LeviathanStellarConverterDeactivatePatch
{
    public static bool Prefix(Activatable __instance)
    {
        return !LeviathanStellarConverter.InterceptNativeDeactivate(__instance);
    }
}

// =============================================================================
// SOURCE LASER STATE
// =============================================================================

[HarmonyPatch(typeof(BeamWeapon), "FixedUpdate")]
public static class LeviathanStellarConverterBeamWeaponFixedUpdatePatch
{
    public static void Prefix(BeamWeapon __instance)
    {
        Laser laser = __instance as Laser;

        if (laser != null)
            LeviathanStellarConverter.PrepareFixedUpdate(laser);
    }

    public static void Postfix(BeamWeapon __instance)
    {
        Laser laser = __instance as Laser;

        if (laser != null)
            LeviathanStellarConverter.CompleteFixedUpdate(laser);
    }
}

[HarmonyPatch(typeof(BeamWeapon), "Unequip")]
public static class LeviathanStellarConverterBeamWeaponUnequipPatch
{
    public static void Prefix(BeamWeapon __instance)
    {
        Laser source = __instance as Laser;

        if (source != null &&
            ReferenceEquals(
                LeviathanStellarConverter.FindSourceLaser(source.parentShip),
                source
            ))
        {
            LeviathanStellarConverter.Reset();
        }
    }
}

[HarmonyPatch(typeof(WorldController), "OnDestroy")]
public static class LeviathanStellarConverterWorldDestroyPatch
{
    public static void Prefix()
    {
        LeviathanStellarConverter.Reset();
    }
}

// =============================================================================
// DAMAGE / ROLLS / RANGE
// =============================================================================

[HarmonyPatch(typeof(BeamWeapon), "GetDamageData")]
public static class LeviathanStellarConverterDamagePatch
{
    public static void Postfix(
        BeamWeapon __instance,
        ref DamageData[] __result)
    {
        LeviathanStellarConverter.ScaleDamagePacket(
            __instance,
            ref __result
        );
    }
}

[HarmonyPatch(typeof(BeamWeapon), "GetCritChance")]
public static class LeviathanStellarConverterCritChancePatch
{
    public static void Postfix(BeamWeapon __instance, ref float __result)
    {
        LeviathanStellarConverter.ScaleCritChance(__instance, ref __result);
    }
}

[HarmonyPatch(typeof(BeamWeapon), "GetCritModifier")]
public static class LeviathanStellarConverterCritModifierPatch
{
    public static void Postfix(BeamWeapon __instance, ref float __result)
    {
        LeviathanStellarConverter.ScaleCritModifier(__instance, ref __result);
    }
}

[HarmonyPatch(typeof(BeamWeapon), "GetStatusEffectChance")]
public static class LeviathanStellarConverterDebuffChancePatch
{
    public static void Postfix(BeamWeapon __instance, ref float __result)
    {
        LeviathanStellarConverter.ScaleDebuffChance(__instance, ref __result);
    }
}

[HarmonyPatch(typeof(Beam), "GetMaxRange")]
public static class LeviathanStellarConverterRangePatch
{
    public static void Postfix(Beam __instance, ref float __result)
    {
        LeviathanStellarConverter.ScaleBeamMaxRange(
            __instance,
            ref __result
        );
    }
}

// =============================================================================
// WIDTH: NATIVE BEAM RAYCAST -> MATCHING CIRCLECAST
// =============================================================================

[HarmonyPatch]
public static class LeviathanStellarConverterBeamRaycastPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(Beam),
            "GetRaycastHit",
            new Type[]
            {
                typeof(bool),
                typeof(PhysicsController.Hit)
            }
        );
    }

    public static void Prefix(
        Beam __instance,
        ref LeviathanStellarConverter.BeamCastState __state)
    {
        __state = LeviathanStellarConverter.BeginBeamCast(__instance);
    }

    public static void Postfix(
        LeviathanStellarConverter.BeamCastState __state)
    {
        LeviathanStellarConverter.EndBeamCast(__state);
    }

    public static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions)
    {
        return LeviathanStellarConverter.ReplaceNativeBeamRaycasts(instructions);
    }
}

[HarmonyPatch]
public static class LeviathanStellarConverterBeamPiercingRaycastPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(Beam),
            "GetAllPiercingHits",
            Type.EmptyTypes
        );
    }

    public static void Prefix(
        Beam __instance,
        ref LeviathanStellarConverter.BeamCastState __state)
    {
        __state = LeviathanStellarConverter.BeginBeamCast(__instance);
    }

    public static void Postfix(
        LeviathanStellarConverter.BeamCastState __state)
    {
        LeviathanStellarConverter.EndBeamCast(__state);
    }

    public static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions)
    {
        return LeviathanStellarConverter.ReplaceNativeBeamRaycasts(instructions);
    }
}

[HarmonyPatch(typeof(Beam), "DrawBeam")]
public static class LeviathanStellarConverterBeamVisualWidthPatch
{
    public static void Postfix(Beam __instance)
    {
        LeviathanStellarConverter.ScaleVisualWidth(__instance);
    }
}
