using HarmonyLib;
using StarVortex;
using UnityEngine;

/// <summary>
/// Every tunable for Temporal Dive in one place, plus the patches that make the
/// duration and cooldown knobs real.
///
/// ---------------------------------------------------------------------------
/// WHERE THESE COME FROM
/// ---------------------------------------------------------------------------
/// Plain static fields with sane defaults, so the mod runs before any tree
/// exists. Feed them from your specialization resolver in Refresh() and call
/// that whenever the configuration revision changes, the same trigger
/// LeviathanNetwork already watches for its spec-block burst.
///
/// LOCAL vs PER-OWNER matters, and the two knob groups differ:
///
///   Duration and cooldown are LOCAL. They modify the activating player's own
///   item, and the resulting duration travels to every peer inside
///   MsgTemporalActivate.duration. A remote peer never needs to know your
///   duration knob; it reads the number off the wire.
///
///   Field shape (radius, floors, curve) is PER-OWNER. Every peer evaluates
///   every active dive locally, including dives owned by other players, so
///   these cannot be read from local configuration when the owner is someone
///   else. Until LeviathanTemporalActivation.ResolveRadii derives them from the
///   owner's replicated ranks, they are shared constants, which is correct
///   under ModsMatch because every peer runs the identical build.
///
/// ---------------------------------------------------------------------------
/// THE 30 SECOND CEILING
/// ---------------------------------------------------------------------------
/// NetWorldBridge clamps MsgTemporalActivate.duration to 0-30 on receipt, and
/// so does the temporal dilation stream. A duration knob that produced 45s
/// would give you 45s in singleplayer and 30s in co-op. Worse, the item's own
/// durationTimer would still run to 45 while the field died at 30, so the drive
/// would sit "active" with nothing happening and refuse to re-fire.
///
/// ResolveDuration therefore clamps to MaxNetworkDuration in both cases.
/// Raising that constant past 30 only works if you also patch the receive-side
/// clamps, and it is not worth it.
/// </summary>
public static class LeviathanTemporalKnobs
{
    // =========================================================================
    // DURATION AND COOLDOWN (local to the activating player)
    // =========================================================================

    /// <summary>
    /// Multiplier on the item's rolled duration. 1.25 is +25%. Applied after
    /// DurationFlatAdd, so the order is (rolled + flat) * multiplier.
    /// </summary>
    public static float DurationMultiplier = 3f;

    /// <summary>Seconds added to the item's rolled duration before the multiplier.</summary>
    public static float DurationFlatAdd = 0f;

    /// <summary>
    /// Multiplier on the item's rolled cooldown. Below 1 is a reduction, so
    /// 0.8 is a 20% shorter cooldown.
    /// </summary>
    public static float CooldownMultiplier = 0.1f;

    /// <summary>
    /// Seconds added to the item's rolled cooldown before the multiplier.
    /// Negative reduces. The result is floored at zero, matching how
    /// TemporalDriveItemBase.UpdateStats treats BaseCooldown.
    /// </summary>
    public static float CooldownFlatAdd = 0f;

    /// <summary>
    /// Hard ceiling imposed by the network layer. See the class comment. Do not
    /// raise this without also patching NetWorldBridge's receive-side clamps.
    /// </summary>
    public const float MaxNetworkDuration = 30f;

    // =========================================================================
    // FIELD SHAPE (must resolve identically on every peer)
    // =========================================================================

    /// <summary>
    /// Full slowdown inside this radius, in world units. Star.playAreaSize
    /// defaults to 100 and sandbox spans 50-300, so these are small numbers.
    /// </summary>
    public static float InnerRadius = 5f;

    /// <summary>Field reaches 1.0x here. Keep it near the visible screen.</summary>
    public static float OuterRadius = 200f;

    /// <summary>
    /// Strongest slowdown at the centre, for hostiles. 0.25 is 75% slower.
    /// This is the value that rides the wire in MsgTemporalActivate.timeScale,
    /// so it is the one field-shape knob that does not need local derivation.
    /// </summary>
    public static float EnemyFloor = 0.01f;

    /// <summary>1.0 leaves teammates untouched. Lower it to slow them too.</summary>
    public static float AllyFloor = 1f;

    /// <summary>
    /// Curve shaping on top of SmoothStep. Above 1 holds the deep slowdown
    /// further out; below 1 recovers faster. 1.0 is plain SmoothStep.
    /// </summary>
    public static float Exponent = 1f;

    // =========================================================================
    // RESOLVERS
    // =========================================================================

    /// <summary>
    /// Knob-modified duration for a drive. Returns the unmodified value for any
    /// drive that is not the local player's, so enemy drives and remote reps
    /// keep vanilla numbers and display honest tooltips.
    /// </summary>
    public static float ResolveDuration(TemporalDrive drive, float rolled)
    {
        if (!AppliesTo(drive))
        {
            return rolled;
        }

        float value = (rolled + DurationFlatAdd) * DurationMultiplier;
        return Mathf.Clamp(value, 0f, MaxNetworkDuration);
    }

    /// <summary>Knob-modified cooldown for a drive, floored at zero.</summary>
    public static float ResolveCooldown(TemporalDrive drive, float rolled)
    {
        if (!AppliesTo(drive))
        {
            return rolled;
        }

        return Mathf.Max(0f, (rolled + CooldownFlatAdd) * CooldownMultiplier);
    }

    /// <summary>
    /// Knobs are the local player's tree, so they only touch the local player's
    /// equipment. IsPlayer() is false for remote reps and for AI ships that
    /// happen to carry a temporal drive.
    /// </summary>
    private static bool AppliesTo(TemporalDrive drive)
    {
        return drive != null && drive.parentShip && drive.parentShip.IsPlayer();
    }

    /// <summary>
    /// Pull every knob from the specialization tree. Called on configuration
    /// change; safe to call every frame if that is simpler.
    ///
    /// Duration and cooldown read from the local build. Field shape has to be
    /// build-invariant until ResolveRadii derives it per owner, so anything
    /// assigned here that changes the field shape will desync the field between
    /// peers with different trees.
    /// </summary>
    public static void Refresh()
    {
        // Wire to your resolver, e.g.
        //
        //   DurationMultiplier = 1f + 0.10f * RankOf("temporal.dive.duration");
        //   CooldownMultiplier = 1f - 0.08f * RankOf("temporal.dive.recovery");
        //
        // Leave the field-shape knobs alone here until ResolveRadii is
        // per-owner, or two players with different trees will disagree about
        // where the field ends.
    }
}

/// <summary>
/// TemporaryEffect declares four getters that all read the protected `duration`
/// and `cooldown` fields through ApplyModifier: Duration, LocalDuration,
/// Cooldown and LocalCooldown. Patching all four keeps every consumer
/// consistent.
///
/// Duration in particular is read by more than the field:
///
///   TemporaryEffect.DurationRunning   when the buff ends and RemoveEffect fires
///   Activatable HUD bar               the drain animation on the icon
///   GetItemStats                      the tooltip number
///   TemporalDrive.AddEffect           the value sent in RequestTemporalDilation
///
/// Modifying only the field's duration would leave all four on the old number,
/// and the item would cancel the field at its original expiry.
///
/// Each patch gates on `is TemporalDrive`, because these getters serve every
/// TemporaryEffect in the game.
/// </summary>
public static class LeviathanTemporalKnobPatches
{
    [HarmonyPatch(typeof(TemporaryEffect), "Duration", MethodType.Getter)]
    public static class DurationPatch
    {
        public static void Postfix(TemporaryEffect __instance, ref float __result)
        {
            TemporalDrive drive = __instance as TemporalDrive;
            if (drive != null)
            {
                __result = LeviathanTemporalKnobs.ResolveDuration(drive, __result);
            }
        }
    }

    [HarmonyPatch(typeof(TemporaryEffect), "LocalDuration", MethodType.Getter)]
    public static class LocalDurationPatch
    {
        public static void Postfix(TemporaryEffect __instance, ref float __result)
        {
            TemporalDrive drive = __instance as TemporalDrive;
            if (drive != null)
            {
                __result = LeviathanTemporalKnobs.ResolveDuration(drive, __result);
            }
        }
    }

    [HarmonyPatch(typeof(TemporaryEffect), "Cooldown", MethodType.Getter)]
    public static class CooldownPatch
    {
        public static void Postfix(TemporaryEffect __instance, ref float __result)
        {
            TemporalDrive drive = __instance as TemporalDrive;
            if (drive != null)
            {
                __result = LeviathanTemporalKnobs.ResolveCooldown(drive, __result);
            }
        }
    }

    [HarmonyPatch(typeof(TemporaryEffect), "LocalCooldown", MethodType.Getter)]
    public static class LocalCooldownPatch
    {
        public static void Postfix(TemporaryEffect __instance, ref float __result)
        {
            TemporalDrive drive = __instance as TemporalDrive;
            if (drive != null)
            {
                __result = LeviathanTemporalKnobs.ResolveCooldown(drive, __result);
            }
        }
    }
}
