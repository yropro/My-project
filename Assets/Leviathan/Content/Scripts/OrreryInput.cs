using StarVortex;
using UnityEngine;

/// <summary>
/// Native-input adapter for the local Orrery owner.
///
/// InputController already translates configured Rewired actions into
/// GameShip.StartActivating/StopActivating calls. Orrery consumes those native
/// activation edges instead of taking a direct dependency on Rewired_Core.
///
/// PrimaryWeapon activation locks the next available formula satellite. The
/// selected activatable activation invokes the completed formula; its matching
/// stop edge is forwarded to live spells that own post-commit interaction
/// (guided fireball detonation / held channels).
/// </summary>
public static class OrreryInput
{
    public static class Tuning
    {
        public const bool LogInputEvents = true;
    }

    // Fire-on-aim can ask GameShip to StartActivating(PrimaryWeapon) repeatedly
    // while aim remains held. Preserve button-edge semantics so one continuous
    // native activation can lock at most one satellite.
    private static GameShip primaryHeldOwner;
    private static int primaryStartedFrame = -1;

    // Indexed activatable input is edge-driven natively, but retain the index so
    // only the matching stop releases FF/LL. This also prevents unrelated native
    // StopActivating calls from becoming a spell release.
    private static GameShip invokeHeldOwner;
    private static int invokeHeldIndex = -1;

    public static bool TryLockNext(GameShip owner)
    {
        if (owner == null || !OrreryRuntime.IsActive(owner))
            return false;

        if (OrreryController.IsShuffling(owner))
        {
            Log("Primary lock rejected: satellites are rearming/shuffling.");
            return false;
        }

        OrrerySatellites.SatelliteSnapshot snapshot =
            OrrerySatellites.GetSnapshot(owner);
        if (snapshot == null)
        {
            Log("Primary lock rejected: no live satellite snapshot.");
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

        Log("Primary lock rejected: no available formula satellite.");
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

    /// <summary>
    /// Called from Orrery's GameShip.StartActivating(Item.Type) patch. Returns
    /// true when this native activation belongs to Orrery input semantics.
    /// </summary>
    public static bool HandleNativeStartByType(GameShip owner, Item.Type type)
    {
        if (!IsLocalOrreryOwner(owner) || type != Item.Type.PrimaryWeapon)
            return false;

        if (!object.ReferenceEquals(primaryHeldOwner, owner))
        {
            primaryHeldOwner = owner;
            primaryStartedFrame = Time.frameCount;
            TryLockNext(owner);
        }

        return true;
    }

    /// <summary>
    /// Called from Orrery's GameShip.StartActivating(int) patch. While Orrery is
    /// active, indexed activatable input is consumed exactly as the previous
    /// InputController patch consumed UpdateActivateActivatable. Only the selected
    /// activatable index invokes; other numbered activatable presses stay inert.
    /// </summary>
    public static bool HandleNativeStartByIndex(GameShip owner, int index)
    {
        if (!IsLocalOrreryOwner(owner))
            return false;

        // Consume malformed/non-selected indexed activatable input while Orrery
        // owns the local activatable control surface.
        if (owner.activatables == null || index < 0 || index >= owner.activatables.Count ||
            index != owner.currentActivatable)
        {
            return true;
        }

        // InputController processes FirePrimary before ActivateActivatable. The
        // combined PrimaryAndActivatable binding therefore reaches both native
        // paths in one frame. Orrery requires distinct lock/invoke inputs, so the
        // second half of that combined edge is consumed without invoking.
        if (primaryStartedFrame == Time.frameCount)
            return true;

        if (!object.ReferenceEquals(invokeHeldOwner, owner) || invokeHeldIndex != index)
        {
            invokeHeldOwner = owner;
            invokeHeldIndex = index;
            Invoke(owner);
        }

        return true;
    }

    /// <summary>
    /// Mirrors native primary/global stop semantics. A global StopActivating(null)
    /// is also the game's pause/UI/offline shutdown path, so clear held input and
    /// release an Orrery channel rather than allowing FF/LL input state to stick
    /// after the one-frame Rewired release edge is lost behind UI.
    /// </summary>
    public static void HandleNativeStopByType(GameShip owner, Item.Type? type)
    {
        if (owner == null)
            return;

        if ((!type.HasValue || type.Value == Item.Type.PrimaryWeapon) &&
            object.ReferenceEquals(primaryHeldOwner, owner))
        {
            primaryHeldOwner = null;
            primaryStartedFrame = -1;
        }

        if (!type.HasValue && object.ReferenceEquals(invokeHeldOwner, owner))
        {
            invokeHeldOwner = null;
            invokeHeldIndex = -1;
            if (IsLocalOrreryOwner(owner))
                ReleaseInvoke(owner);
        }
    }

    /// <summary>
    /// Releases only the indexed activation that actually began Orrery invocation.
    /// Returns true when a held invoke edge was consumed.
    /// </summary>
    public static bool HandleNativeStopByIndex(GameShip owner, int index)
    {
        if (owner == null || !object.ReferenceEquals(invokeHeldOwner, owner) ||
            index != invokeHeldIndex)
        {
            return false;
        }

        invokeHeldOwner = null;
        invokeHeldIndex = -1;
        if (IsLocalOrreryOwner(owner))
            ReleaseInvoke(owner);
        return true;
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
}