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
// Normal release during charge cancels. If ship CC/offline interrupts an already-started
// charge, that one shot remains committed and finishes its charge + output.

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
        1.10f, // Rank 2
        1.20f, // Rank 3
        1.30f, // Rank 4
        1.40f  // Rank 5
    };

    // Multiplier on the source Laser's complete native DamageData packet.
    private static readonly float[] DamageMultiplierByRank =
    {
        4.000f, // Rank 1
        4.225f, // Rank 2
        4.450f, // Rank 3
        4.675f, // Rank 4
        4.900f  // Rank 5
    };

    // Multiplier on native crit chance while the burst is firing.
    private static readonly float[] CritChanceMultiplierByRank =
    {
        1.10f, // Rank 1
        1.10f, // Rank 2
        1.10f, // Rank 3
        1.10f, // Rank 4
        1.10f  // Rank 5
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

    // Multiplier on the source Laser's native MaxRange result, injected at the same
    // Equippable.ApplyModifier(MaxRange) boundary used by Striker. Beam raycasts,
    // chain range and drawing therefore inherit it through vanilla code.
    private static readonly float[] LengthMultiplierByRank =
    {
        1.100f, // Rank 1
        1.275f, // Rank 2
        1.350f, // Rank 3
        1.425f, // Rank 4
        1.500f  // Rank 5
    };

    // Multiplier on visual beam width and the matching CircleCast radius.
    private static readonly float[] WidthMultiplierByRank =
    {
        10.00f, // Rank 1
        15.0f, // Rank 2
        20.00f, // Rank 3
        25.0f, // Rank 4
        35.00f  // Rank 5
    };

    // Visual brightness only. 1.0 = vanilla brightness.
    private static readonly float[] BrightnessMultiplierByRank =
    {
    0.91f, // Rank 1
    0.91f, // Rank 2
    0.91f, // Rank 3
    0.91f, // Rank 4
    0.91f  // Rank 5
    };

    private static readonly bool[] PiercingByRank =
    {
    false, // Rank 1
    false, // Rank 2
    true, // Rank 3
    true, // Rank 4
    true  // Rank 5
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
    private static bool forceCompleteCurrentShot;

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

    private struct BeamColors
    {
        public Color start;
        public Color end;
    }

    private static readonly Dictionary<LineRenderer, BeamColors>
        OriginalBeamColors =
            new Dictionary<LineRenderer, BeamColors>();

    // =========================================================================
    // SOURCE / RANK
    // =========================================================================

    private static bool TryGetVisualRank(GameShip player, out int rank)
    {
        rank = 0;

        // Remote player replicas are built from the owner's complete Ship JSON,
        // including Pilot upgradeUnlocks. This lets presentation/stat hooks read
        // Stellar Converter ranks without requiring local controller ownership.
        if (player == null || !player.IsAnyPlayerShip())
            return false;

        Pilot pilot = GameShip.GetPlayerSourcePilot(player);

        if (pilot == null ||
            pilot.GetUpgradeLevel(LeviathanMod.GrowthUpgrade) < 1)
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

    public static bool TryGetRank(GameShip player, out int rank)
    {
        rank = 0;

        if (player == null ||
            LeviathanMod.Controller == null ||
            WorldController.instance == null ||
            WorldController.instance.GetCurrentPlayerShip() != player ||
            LeviathanMod.Controller.GetActiveSectionCount(player) < 5)
        {
            return false;
        }

        return TryGetVisualRank(player, out rank);
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

    private static bool TryGetRemoteSourceContext(
        Laser laser,
        out GameShip player,
        out int rank)
    {
        player = laser == null ? null : laser.parentShip;
        rank = 0;

        if (laser == null ||
            player == null ||
            !player.IsRemotePlayer() ||
            laser.type != Item.Type.PrimaryWeapon ||
            !TryGetVisualRank(player, out rank))
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

        if (laser == null)
            return false;

        // Owner-local Converter uses the custom state machine. Remote replicas
        // receive activation edges only when the owner's native Laser is actually
        // active, which corresponds exactly to Converter's Firing phase.
        if (ReferenceEquals(sourceLaser, laser) &&
            phase == Phase.Firing &&
            TryGetContext(laser, out player, out rank))
        {
            return true;
        }

        return TryGetRemoteSourceContext(laser, out player, out rank) &&
            laser.IsActive();
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

        // Disabled / weapons-offline calls StopActivating just like player release,
        // but an already-started Stellar shot is intentionally unstoppable by that
        // control loss. Mark the physical input released so it cannot queue another
        // shot, then let the current Charging/Firing sequence finish once.
        if (IsControlInterrupted(player) && phase != Phase.Idle)
        {
            inputHeld = false;
            forceCompleteCurrentShot = true;
            SetNativeActive(laser, phase == Phase.Firing);
            return true;
        }

        inputHeld = false;

        // Normal release before charge completes cancels it. Once firing, the shot
        // remains committed until OutputDurationByRank expires.
        if (phase == Phase.Charging && !forceCompleteCurrentShot)
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

        if (IsTerminalStopped(player))
        {
            if (ReferenceEquals(sourceLaser, laser))
                Reset();
            else
                SetNativeActive(laser, false);

            return;
        }

        SelectSource(laser);

        if (phase == Phase.Charging && !inputHeld && !forceCompleteCurrentShot)
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
            if (!inputHeld && !forceCompleteCurrentShot)
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
            forceCompleteCurrentShot = false;
            return;
        }

        phaseTimer += Time.fixedDeltaTime;

        if (phaseTimer >= GetRankValue(OutputDurationByRank, rank))
        {
            // Finish the current native update before ending output. A shot forced
            // through CC never auto-queues another shot after control returns.
            bool queueNext = inputHeld && !forceCompleteCurrentShot;
            phase = queueNext ? Phase.Charging : Phase.Idle;
            phaseTimer = 0f;
            forceCompleteCurrentShot = false;
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
        forceCompleteCurrentShot = false;
    }

    public static void Reset()
    {
        if (sourceLaser != null)
            SetNativeActive(sourceLaser, false);

        sourceLaser = null;
        phase = Phase.Idle;
        phaseTimer = 0f;
        inputHeld = false;
        forceCompleteCurrentShot = false;
    }

    private static void SetNativeActive(Laser laser, bool active)
    {
        if (laser == null || ActivatableActiveField == null)
            return;

        ActivatableActiveField.SetValue(laser, active);
    }

    private static bool IsTerminalStopped(GameShip player)
    {
        return player == null || player.health <= 0f;
    }

    private static bool IsControlInterrupted(GameShip player)
    {
        return player != null &&
            (player.IsDisabled() || player.IsWeaponsOffline());
    }

    // Native BeamWeapon deactivation fades the rendered Beam over several frames,
    // and BeamWeapon.FixedUpdate still calls Beam.DoDamageTick during that fade.
    // Stellar Converter's output window is exact: the selected source may deal
    // beam damage only while the converter phase itself is Firing.
    public static bool AllowBeamDamageTick(Beam beam)
    {
        if (beam == null || BeamParentWeaponField == null)
            return true;

        Laser laser = BeamParentWeaponField.GetValue(beam) as Laser;
        if (laser == null)
            return true;

        GameShip player;
        int rank;

        if (TryGetContext(laser, out player, out rank))
        {
            return ReferenceEquals(sourceLaser, laser) &&
                phase == Phase.Firing &&
                !IsTerminalStopped(player);
        }

        // Native remote beams also fade after Deactivate and would otherwise keep
        // dealing victim-side damage during that fade. Match the owner's exact
        // Converter output window by allowing remote ticks only while its active
        // slot bit is still asserted.
        if (TryGetRemoteSourceContext(laser, out player, out rank))
            return laser.IsActive() && !IsTerminalStopped(player);

        return true;
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

    // Native Beam.DoDamageTick recomputes piercing every damage tick as
    // basePiercing || GetJuggernautPiercing(). OR Stellar's rank flag into that
    // native decision so vanilla piercing damage, hit ordering, visuals, chaining
    // and attribution remain in control.
    public static void ScalePiercing(Beam beam, ref bool value)
    {
        Laser laser;
        int rank;

        if (value || !TryGetBeamContext(beam, out laser, out rank))
            return;

        int index = Mathf.Clamp(rank - 1, 0, PiercingByRank.Length - 1);
        value = PiercingByRank[index];
    }

    // Native Striker range is injected through Equippable.ApplyModifier(MaxRange).
    // Use that exact stat boundary for only the selected Primary Laser, so Beam's
    // own raycasts, chaining and rendering all consume the scaled MaxRange.
    public static void ScaleMaxRange(
        Equippable equippable,
        Modifier.Type modifierType,
        bool includeParentShip,
        ref float value)
    {
        if (modifierType != Modifier.Type.MaxRange || !includeParentShip)
            return;

        Laser laser = equippable as Laser;
        GameShip player;
        int rank;

        if (laser == null)
            return;

        bool valid = TryGetContext(laser, out player, out rank);

        if (!valid)
            valid = TryGetRemoteSourceContext(laser, out player, out rank);

        if (!valid)
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
        LineRenderer line =
            BeamLineRendererField == null
                ? null
                : BeamLineRendererField.GetValue(beam)
                    as LineRenderer;

        LineRenderer end =
            BeamEndLineRendererField == null
                ? null
                : BeamEndLineRendererField.GetValue(beam)
                    as LineRenderer;

        Laser laser;
        int rank;

        if (!TryGetBeamContext(beam, out laser, out rank))
        {
            RestoreBeamBrightness(line);
            RestoreBeamBrightness(end);
            RestoreBeamBrightness(beam.additionalBeam);
            return;
        }

        float multiplier =
            GetRankValue(WidthMultiplierByRank, rank);

        float brightness =
            GetRankValue(BrightnessMultiplierByRank, rank);

        float visualState = GetVisualState(beam);

        float maxWidth =
            GetFloat(BeamMaxWidthField, beam);

        float maxEndWidth =
            GetFloat(BeamMaxEndWidthField, beam);

        float additionalMaxWidth =
            GetFloat(BeamAdditionalMaxWidthField, beam);

        if (line != null)
        {
            line.widthMultiplier =
                visualState * maxWidth * multiplier;
        }

        if (end != null)
        {
            end.widthMultiplier =
                visualState * maxEndWidth * multiplier;
        }

        if (beam.additionalBeam != null)
        {
            beam.additionalBeam.widthMultiplier =
                visualState * additionalMaxWidth * multiplier;
        }

        ScaleBeamBrightness(line, brightness);
        ScaleBeamBrightness(end, brightness);
        ScaleBeamBrightness(
            beam.additionalBeam,
            brightness
        );
    }

    private static void ScaleBeamBrightness(
    LineRenderer renderer,
    float multiplier)
    {
        if (renderer == null)
            return;

        BeamColors original;

        if (!OriginalBeamColors.TryGetValue(renderer, out original))
        {
            original = new BeamColors
            {
                start = renderer.startColor,
                end = renderer.endColor
            };

            OriginalBeamColors[renderer] = original;
        }

        renderer.startColor = new Color(
            original.start.r * multiplier,
            original.start.g * multiplier,
            original.start.b * multiplier,
            original.start.a
        );

        renderer.endColor = new Color(
            original.end.r * multiplier,
            original.end.g * multiplier,
            original.end.b * multiplier,
            original.end.a
        );
    }

    private static void RestoreBeamBrightness(LineRenderer renderer)
    {
        if (renderer == null)
            return;

        BeamColors original;

        if (!OriginalBeamColors.TryGetValue(renderer, out original))
            return;

        renderer.startColor = original.start;
        renderer.endColor = original.end;
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
            IsFiringSource(laser, out rank);
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

            if (called != null &&
                NativePhysicsRaycastMethod != null &&
                StellarPhysicsRaycastMethod != null &&
                called.Module == NativePhysicsRaycastMethod.Module &&
                called.MetadataToken == NativePhysicsRaycastMethod.MetadataToken)
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

// Stellar output is mechanically bounded to the converter firing phase. Native
// Beam deactivation fades visually and can otherwise keep ticking damage.
[HarmonyPatch(typeof(Beam), "DoDamageTick")]
public static class LeviathanStellarConverterBeamDamageWindowPatch
{
    public static bool Prefix(Beam __instance)
    {
        return LeviathanStellarConverter.AllowBeamDamageTick(__instance);
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

// Beam.DoDamageTick asks DamageBeam.GetJuggernautPiercing every tick before
// choosing its native single-hit or piercing path. Preserve any native piercing
// result and add Stellar Converter piercing at the configured ranks.
[HarmonyPatch]
public static class LeviathanStellarConverterPiercingPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(DamageBeam),
            "GetJuggernautPiercing",
            Type.EmptyTypes
        );
    }

    public static void Postfix(DamageBeam __instance, ref bool __result)
    {
        LeviathanStellarConverter.ScalePiercing(__instance, ref __result);
    }
}

// Match the game's Striker Laser/Bolt/Torch Range implementation at the native
// MaxRange modifier boundary, while guarding to Stellar Converter's one source.
[HarmonyPatch]
public static class LeviathanStellarConverterRangePatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(Equippable),
            "ApplyModifier",
            new Type[]
            {
                typeof(Modifier.Type),
                typeof(float),
                typeof(bool),
                typeof(bool)
            }
        );
    }

    public static void Postfix(
        Equippable __instance,
        Modifier.Type __0,
        bool __3,
        ref float __result)
    {
        LeviathanStellarConverter.ScaleMaxRange(
            __instance,
            __0,
            __3,
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

// Beam.AdjustCurrentState is where vanilla writes widthMultiplier from maxWidth.
// Reapply the absolute Stellar width immediately after that native assignment,
// then again after DrawBeam as a render-time guard. ScaleVisualWidth is absolute,
// so the two hooks cannot compound.
[HarmonyPatch]
public static class LeviathanStellarConverterBeamStateWidthPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(Beam),
            "AdjustCurrentState",
            new Type[]
            {
                typeof(float),
                typeof(PhysicsController.Hit)
            }
        );
    }

    public static void Postfix(Beam __instance)
    {
        LeviathanStellarConverter.ScaleVisualWidth(__instance);
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
