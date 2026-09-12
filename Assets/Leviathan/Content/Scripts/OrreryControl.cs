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

        if (!IsLocalOrreryOwner(owner))
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
    /// recipes are consumed and released cleanly rather than leaving the cast
    /// state stuck in Invoking.
    /// </summary>
    public static InvokeResult TryInvoke(GameShip owner)
    {
        if (!IsLocalOrreryOwner(owner))
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
            return InvokeResult.UnimplementedRecipe;
        }

        if (!OrrerySpellRegistry.TryExecute(owner, invocation))
        {
            OrreryCasting.Cancel(owner);
            OrreryNetwork.PublishLocal(owner);
            return InvokeResult.ExecutionRejected;
        }

        // The executor owns completion timing. Instant spells may complete during
        // execution; beams/streams may retain the CoreAbilityExecution until their
        // bounded spell instance ends.
        return InvokeResult.Started;
    }

    public static bool Shuffle(GameShip owner)
    {
        if (!IsLocalOrreryOwner(owner))
            return false;

        bool hadFormula = OrreryCasting.GetLockedCount(owner) > 0;
        OrreryCasting.Cancel(owner);
        OrreryNetwork.PublishLocal(owner);
        return hadFormula;
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
