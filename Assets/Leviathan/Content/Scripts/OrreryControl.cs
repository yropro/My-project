using StarVortex;

/// <summary>
/// Semantic local-owner control surface for Orrery casting.
///
/// Input bindings deliberately do not live here. Rewired/UI/debug input can call
/// the same operations without teaching casting about buttons, mouse state or
/// lock-order policy.
/// </summary>
public static class OrreryControl
{
    public enum InvokeResult : byte
    {
        Rejected = 0,
        UnimplementedRecipe = 1,
        ExecutionRejected = 2,
        Started = 3
    }

    public static bool TryLock(
        GameShip owner,
        byte satelliteId,
        out OrreryElement capturedElement,
        out bool formulaComplete)
    {
        capturedElement = OrreryElement.None;
        formulaComplete = false;

        if (!IsLocalOrreryOwner(owner) || OrreryController.IsShuffling(owner))
            return false;

        OrrerySatellites.SatelliteContext satellite;
        if (!OrrerySatellites.TryGetSatellite(owner, satelliteId, out satellite) ||
            satellite == null || !satellite.IsValid)
        {
            return false;
        }

        float orbitalAngleDegrees = OrreryOrbit.GetAngleDegrees(owner, satelliteId);
        if (!OrreryCasting.TryLock(
                owner,
                satelliteId,
                orbitalAngleDegrees,
                out capturedElement,
                out formulaComplete))
        {
            return false;
        }

        OrreryNetwork.PublishLocal(owner);
        return true;
    }

    /// <summary>
    /// Baseline invocation requires a complete formula. Unknown/unimplemented
    /// recipes are consumed and released cleanly, then use the same physical
    /// shuffle/rearm path as a successful cast rather than trapping cast state.
    /// </summary>
    public static InvokeResult TryInvoke(GameShip owner)
    {
        if (!IsLocalOrreryOwner(owner) || OrreryController.IsShuffling(owner))
            return InvokeResult.Rejected;

        OrreryCastInvocation invocation;
        if (!OrreryCasting.TryInvoke(owner, false, out invocation))
            return InvokeResult.Rejected;

        OrreryNetwork.PublishLocal(owner);

        OrrerySpellRegistry.SpellDefinition spell;
        if (!OrrerySpellRegistry.TryResolve(invocation.Recipe, out spell) ||
            spell == null || !spell.Implemented)
        {
            OrreryCasting.CompleteInvocation(owner, invocation.Execution, 0);
            OrreryNetwork.PublishLocal(owner);
            OrreryController.StartShuffle(owner);
            return InvokeResult.UnimplementedRecipe;
        }

        if (!OrrerySpellRegistry.TryExecute(owner, invocation))
        {
            OrreryCasting.Cancel(owner);
            OrreryNetwork.PublishLocal(owner);
            OrreryController.StartShuffle(owner);
            return InvokeResult.ExecutionRejected;
        }

        // The executor owns completion timing. II completes after its one visual
        // emission tick; FF persists until hit/expiry/release; LL persists until
        // the invoke input is released.
        return InvokeResult.Started;
    }

    public static bool ReleaseInvoke(GameShip owner)
    {
        return IsLocalOrreryOwner(owner) &&
            OrrerySpellRuntime.ReleaseInvoke(owner);
    }

    public static bool Shuffle(GameShip owner)
    {
        if (!IsLocalOrreryOwner(owner))
            return false;

        bool hadFormula = OrreryCasting.GetLockedCount(owner) > 0;
        OrreryCasting.Cancel(owner);
        OrreryNetwork.PublishLocal(owner);
        bool started = OrreryController.StartShuffle(owner);
        return hadFormula || started;
    }

    public static bool TryGetCapturedElement(
        GameShip owner,
        byte satelliteId,
        out OrreryElement element)
    {
        element = OrreryElement.None;
        OrrerySatellites.SatelliteContext satellite;
        if (!OrrerySatellites.TryGetSatellite(owner, satelliteId, out satellite) ||
            satellite == null || !satellite.IsValid || !satellite.Locked)
        {
            return false;
        }

        element = satellite.CapturedElement;
        return element != OrreryElement.None;
    }

    private static bool IsLocalOrreryOwner(GameShip owner)
    {
        CoreOwnerContext context = CoreClassRuntime.CurrentContext;
        return owner != null && context != null && context.IsValid &&
            context.ClassId == CoreClassId.Orrery &&
            object.ReferenceEquals(context.Ship, owner) &&
            OrreryRuntime.IsActive(owner);
    }
}
