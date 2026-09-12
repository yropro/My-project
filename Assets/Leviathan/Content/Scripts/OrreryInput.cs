using HarmonyLib;
using Rewired;
using StarVortex;
using UnityEngine;

/// <summary>
/// Native-input adapter for the local Orrery owner.
///
/// FirePrimary (LMB by default) locks the next available formula satellite in the
/// canonical live-snapshot order. ActivateActivatable (RMB by default) invokes
/// the completed formula; its release edge is forwarded to live spells that own
/// post-commit interaction (guided fireball detonation / held channels).
/// </summary>
public static class OrreryInput
{
    public static class Tuning
    {
        public const bool LogInputEvents = true;
    }

    public static bool TryLockNext(GameShip owner)
    {
        if (owner == null || !OrreryRuntime.IsActive(owner))
            return false;

        if (OrreryController.IsShuffling(owner))
        {
            Log("LMB lock rejected: satellites are rearming/shuffling.");
            return false;
        }

        OrrerySatellites.SatelliteSnapshot snapshot =
            OrrerySatellites.GetSnapshot(owner);
        if (snapshot == null)
        {
            Log("LMB lock rejected: no live satellite snapshot.");
            return false;
        }

        for (int i = 0; i < snapshot.Count; i++)
        {
            OrrerySatellites.SatelliteContext satellite = snapshot.Get(i);
            if (satellite == null || !satellite.IsValid || satellite.Disabled ||
                satellite.Locked ||
                satellite.Kind != OrrerySatellites.SatelliteKind.Formula)
            {
                continue;
            }

            OrreryElement element;
            bool complete;
            if (!OrreryControl.TryLock(
                    owner,
                    satellite.SatelliteId,
                    out element,
                    out complete))
            {
                continue;
            }

            Log("Locked satellite " + satellite.SatelliteId +
                " as " + element +
                " at " + OrreryOrbit.GetAngleDegrees(owner, satellite.SatelliteId).ToString("0.0") +
                " deg. Recipe=" + OrreryCasting.GetCurrentRecipe(owner) +
                (complete ? " (ready)" : ""));
            return true;
        }

        Log("LMB lock rejected: no available formula satellite.");
        return false;
    }

    public static OrreryControl.InvokeResult Invoke(GameShip owner)
    {
        OrreryRecipeKey recipe = OrreryCasting.GetCurrentRecipe(owner);
        OrreryControl.InvokeResult result = OrreryControl.TryInvoke(owner);
        Log("Invoke " + recipe + " -> " + result + ".");
        return result;
    }

    public static bool ReleaseInvoke(GameShip owner)
    {
        bool handled = OrreryControl.ReleaseInvoke(owner);
        if (handled)
            Log("Invoke release handled by active spell.");
        return handled;
    }

    private static bool IsLocalOrreryOwner(GameShip owner)
    {
        CoreOwnerContext context = CoreClassRuntime.CurrentContext;
        return owner != null && context != null && context.IsValid &&
            context.ClassId == CoreClassId.Orrery &&
            object.ReferenceEquals(context.Ship, owner) &&
            OrreryRuntime.IsActive(owner);
    }

    private static void Log(string message)
    {
        if (Tuning.LogInputEvents)
            Debug.Log("[Orrery/Input] " + message);
    }

    [HarmonyPatch(typeof(InputController), "UpdateFirePrimary")]
    private static class FirePrimaryPatch
    {
        public static bool Prefix(InputController __instance)
        {
            GameShip owner = __instance == null ? null : __instance.controlShip;
            if (!IsLocalOrreryOwner(owner))
                return true;

            Player input = __instance.rewiredPlayer;
            if (input != null && input.GetButtonDown("FirePrimary"))
                TryLockNext(owner);

            // Orrery owns primary-fire semantics while the class is active.
            // PrimaryAndActivatable is intentionally not overloaded because
            // Orrery requires two distinct semantic inputs.
            return false;
        }
    }

    [HarmonyPatch(typeof(InputController), "UpdateActivateActivatable")]
    private static class ActivateActivatablePatch
    {
        public static bool Prefix(InputController __instance)
        {
            GameShip owner = __instance == null ? null : __instance.controlShip;
            if (!IsLocalOrreryOwner(owner))
                return true;

            Player input = __instance.rewiredPlayer;
            if (input != null)
            {
                if (input.GetButtonDown("ActivateActivatable"))
                    Invoke(owner);
                if (input.GetButtonUp("ActivateActivatable"))
                    ReleaseInvoke(owner);
            }

            // Suppresses normal selected/numbered activatable weapon handling.
            // Dedicated utility inputs live elsewhere in InputController and are
            // intentionally left alone.
            return false;
        }
    }
}
