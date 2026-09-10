using System.Collections.Generic;
using HarmonyLib;
using StarVortex;
using UnityEngine;

/// <summary>
/// Turns every TemporalDrive in the game into the proximity-falloff version.
///
/// ---------------------------------------------------------------------------
/// WHY PATCH THE ITEM INSTEAD OF ADDING ONE
/// ---------------------------------------------------------------------------
/// TemporalDrive already owns everything except the physics: loot generation,
/// rarity and legendary rolls, per-level cooldown and duration scaling
/// (TemporalDriveItemBase.UpdateStats), activation and loop audio, HUD
/// activatable state, cooldown gating, and the AI's own ShouldActivate /
/// ShouldSwitch logic for enemies that carry one.
///
/// Rebuilding that to add a new item would be a lot of work to arrive back at
/// the same place. Patching keeps all of it and replaces only the dilation.
///
/// ---------------------------------------------------------------------------
/// HOW ACTIVATION REACHES THE OTHER PLAYERS
/// ---------------------------------------------------------------------------
/// Vanilla's own message already carries what the field needs:
///
///     MsgTemporalActivate { starId, timeScale, duration, ownerPlayerId, cancel }
///
/// AddEffect calls NetSession.RequestTemporalDilation, which routes through the
/// star authority, validates the floats, and relays to every peer in the star.
/// Each peer lands in NetWorldBridge.OnTemporalActivate with the owner's id.
///
/// So the interception point is OnTemporalActivate: skip the vanilla global
/// window entirely and register a field instead. timeScale carries the strength
/// floor, duration carries the duration, ownerPlayerId identifies the centre.
/// No new protocol, no new LeviathanNetwork slot, and the relay path is one the
/// game already hardens against malformed input.
///
/// Radius is not on the wire. ModsMatch guarantees identical builds and your
/// tree ranks already replicate through the LeviathanNetwork spec block, so a
/// receiver derives radius locally. See ResolveRadii below for where to hook
/// that up once the knobs exist.
///
/// ---------------------------------------------------------------------------
/// WHAT RUNS WHERE
/// ---------------------------------------------------------------------------
///   singleplayer        TemporalDriveAddEffectPatch handles it directly
///   co-op               OnTemporalActivate on every peer; each machine then
///                       applies the field only to ships it simulates, which
///                       LeviathanTemporalField.Step enforces via IsNetRemote
///
/// Skipping the vanilla body also leaves temporalContributions empty, so
/// UpdateTemporalDilation, SendTemporalDilation and RecomputeTemporalWindow all
/// no-op on their own. Nothing needs to be unwound.
/// </summary>
public static class LeviathanTemporalActivation
{
    /// <summary>
    /// Where per-owner radius and curve come from. Constants for now; swap the
    /// body for a lookup against the owner's replicated tree ranks when the
    /// Temporal Dive knobs exist. Do NOT read local configuration here: this
    /// runs for remote owners too, and every peer must derive the same field.
    /// </summary>
    private static void ResolveRadii(
        GameShip owner,
        out float innerRadius,
        out float outerRadius,
        out float allyFloor,
        out float exponent)
    {
        innerRadius = LeviathanTemporalField.DefaultInnerRadius;
        outerRadius = LeviathanTemporalField.DefaultOuterRadius;
        allyFloor = LeviathanTemporalField.DefaultAllyFloor;
        exponent = LeviathanTemporalField.DefaultExponent;
    }

    private static void Begin(GameShip owner, float duration, float enemyFloor)
    {
        if (!owner || duration <= 0f)
        {
            return;
        }

        float innerRadius;
        float outerRadius;
        float allyFloor;
        float exponent;
        ResolveRadii(owner, out innerRadius, out outerRadius, out allyFloor, out exponent);

        LeviathanTemporalField.Activate(
            owner,
            duration,
            innerRadius,
            outerRadius,
            enemyFloor,
            allyFloor,
            exponent);
    }

    // =========================================================================
    // CO-OP: replace the global temporal window with a field
    // =========================================================================

    [HarmonyPatch(typeof(NetWorldBridge), "OnTemporalActivate")]
    public static class NetWorldBridgeOnTemporalActivatePatch
    {
        private static readonly AccessTools.FieldRef<NetWorldBridge, Dictionary<int, RemoteShipDriver>> Reps =
            AccessTools.FieldRefAccess<NetWorldBridge, Dictionary<int, RemoteShipDriver>>("reps");

        /// <summary>Returning false skips the entire vanilla body.</summary>
        public static bool Prefix(NetWorldBridge __instance, MsgTemporalActivate __0)
        {
            if (__0 == null)
            {
                return false;
            }

            GameShip owner = ResolveOwner(__instance, __0.ownerPlayerId);
            if (!owner)
            {
                // Owner rep has not spawned yet, or already despawned. Dropping
                // is correct: a field with no centre has no meaning, and the
                // dive will expire on its own timer everywhere else.
                return false;
            }

            // Same clamps vanilla applied, kept so a hostile or corrupt packet
            // cannot hand the field a pathological value.
            float duration = Mathf.Clamp(__0.duration, 0f, 30f);
            float enemyFloor = Mathf.Clamp(__0.timeScale, 0.1f, 1f);

            if (__0.cancel || duration <= 0f)
            {
                LeviathanTemporalField.Cancel(owner);
            }
            else
            {
                Begin(owner, duration, enemyFloor);
            }

            return false;
        }

        private static GameShip ResolveOwner(NetWorldBridge bridge, int ownerPlayerId)
        {
            if (NetSession.instance != null && ownerPlayerId == NetSession.instance.localPlayerId)
            {
                return WorldController.instance
                    ? WorldController.instance.GetCurrentPlayerShip()
                    : null;
            }

            Dictionary<int, RemoteShipDriver> reps = Reps(bridge);
            RemoteShipDriver driver;

            if (reps != null && reps.TryGetValue(ownerPlayerId, out driver) && driver != null)
            {
                return driver.gameShip;
            }

            return null;
        }
    }

    // =========================================================================
    // SINGLEPLAYER: intercept the local status path
    // =========================================================================

    /// <summary>
    /// Out of session, AddEffect skips the network entirely and applies
    /// TimeWarpedStatusEffect to its own ship, which drives the global
    /// WorldController timescale through GameShip.SetTimeScale
    /// (GameShip.cs:4704). Remove that and start a field instead.
    ///
    /// This also covers an enemy that carries a temporal drive: vanilla takes
    /// the same branch for them, so they get a field centred on themselves and
    /// you are the one who slows down. That is the consistent reading of the
    /// item, and the AI's existing ShouldActivate logic still governs when.
    /// </summary>
    [HarmonyPatch(typeof(TemporalDrive), "AddEffect")]
    public static class TemporalDriveAddEffectPatch
    {
        public static void Postfix(TemporalDrive __instance)
        {
            if (NetSession.InSession)
            {
                // Handled by the OnTemporalActivate patch on every peer,
                // including this one.
                return;
            }

            GameShip ship = __instance.parentShip;
            if (!ship)
            {
                return;
            }

            // Undo vanilla's dilation. RemoveStatusEffect triggers
            // TimeWarpedStatusEffect.SetRemoved, which restores both the ship's
            // speedScale and the global timescale.
            ship.RemoveStatusEffect(StatusEffect.Type.TimeWarped);

            Begin(ship, __instance.Duration, Mathf.Clamp(__instance.timeScale, 0.1f, 1f));
        }
    }

    /// <summary>
    /// Early deactivation. The field expires on its own duration, so this only
    /// matters when the drive is toggled off, the ship dies, or the item is
    /// swapped mid-effect.
    /// </summary>
    [HarmonyPatch(typeof(TemporalDrive), "RemoveEffect")]
    public static class TemporalDriveRemoveEffectPatch
    {
        public static void Postfix(TemporalDrive __instance)
        {
            if (NetSession.InSession)
            {
                // RequestTemporalCancel has already gone out and comes back
                // through OnTemporalActivate with cancel set.
                return;
            }

            if (__instance.parentShip)
            {
                LeviathanTemporalField.Cancel(__instance.parentShip);
            }
        }
    }

    // =========================================================================
    // TEARDOWN
    // =========================================================================

    /// <summary>
    /// Leaving a ship in the field's dictionary with speedScale below 1 makes it
    /// permanently slow. Star changes and session teardown both have to clear.
    /// </summary>
    [HarmonyPatch(typeof(WorldController), "SetTimeScale")]
    public static class WorldControllerGuardPatch
    {
        /// <summary>
        /// Nothing should be writing the global timescale while a field is up.
        /// If something does, the field's per-ship compensation is silently
        /// wrong, and it is worth knowing rather than debugging by feel.
        /// </summary>
        public static void Postfix(float __0)
        {
            if (LeviathanTemporalField.AnyActive && !Mathf.Approximately(__0, 1f))
            {
                Debug.LogWarning(
                    "[LeviathanTemporal] Global timescale set to " + __0 +
                    " while a Temporal Dive field is active. Something is still " +
                    "using the vanilla dilation path.");
            }
        }
    }
}
