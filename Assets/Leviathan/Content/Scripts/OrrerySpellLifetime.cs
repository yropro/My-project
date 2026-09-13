using StarVortex;

/// <summary>
/// Orrery spell-lifetime orchestration boundary.
///
/// This layer owns WHEN persistent spell runtimes are ticked or torn down.
/// Individual spells continue to own WHAT their runtime state means and how it
/// behaves while alive. Keep this dispatcher deliberately explicit; it is not a
/// generic spell engine or registry.
///
/// Migration note: this coordinator is intentionally not wired to a Harmony or
/// MonoBehaviour tick yet. Shatterbolt and Plasma Bolt still own their existing
/// live bridges, so invoking this fixed-step entry point before those bridges are
/// removed would double-tick gameplay. The next migration chunks can move those
/// bridges here one spell at a time.
/// </summary>
public static class OrrerySpellLifetime
{
    private static GameShip lastLocalOwner;

    /// <summary>
    /// Resolves the one locally authoritative Orrery owner, tears down the prior
    /// owner when that identity changes, then dispatches one bounded fixed step to
    /// each migrated persistent spell runtime.
    /// </summary>
    public static void FixedTickLocal(float deltaTime)
    {
        CoreOwnerContext context = CoreClassRuntime.CurrentContext;
        GameShip owner = context != null && context.IsValid &&
            context.ClassId == CoreClassId.Orrery
                ? context.Ship
                : null;

        if (!object.ReferenceEquals(owner, lastLocalOwner))
        {
            if (!object.ReferenceEquals(lastLocalOwner, null))
                ForgetOwner(lastLocalOwner);

            lastLocalOwner = owner;
        }

        if (owner == null)
            return;

        FixedTickOwner(owner, deltaTime);
    }

    /// <summary>
    /// Explicit fixed-step dispatcher. Spell-specific mechanics and state stay in
    /// their owning runtime; this method only decides that the runtimes get a tick.
    /// </summary>
    private static void FixedTickOwner(GameShip owner, float deltaTime)
    {
        OrreryShatterbolt.FixedTick(owner, deltaTime);
        OrreryPlasmaBolt.FixedTick(owner, deltaTime);
    }

    /// <summary>
    /// Tears down persistent spell state owned by one Orrery ship. Every child
    /// cleanup remains idempotent so callers may safely converge here from class
    /// exit, owner replacement, destruction, or controller teardown.
    /// </summary>
    public static void ForgetOwner(GameShip owner)
    {
        if (object.ReferenceEquals(owner, null))
            return;

        OrreryShatterbolt.Forget(owner);
        OrreryPlasmaBolt.Forget(owner);

        if (object.ReferenceEquals(lastLocalOwner, owner))
            lastLocalOwner = null;
    }

    /// <summary>
    /// World-global teardown for persistent Orrery spell runtimes. Shared Orrery
    /// services remain owned by their class/runtime boundaries rather than being
    /// hidden inside an arbitrary spell cleanup path.
    /// </summary>
    public static void ResetWorld()
    {
        OrreryShatterbolt.Reset();
        OrreryPlasmaBolt.Reset();
        lastLocalOwner = null;
    }
}
