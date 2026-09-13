using HarmonyLib;
using StarVortex;
using UnityEngine;

/// <summary>
/// Orrery spell-lifetime orchestration boundary.
///
/// This layer owns WHEN persistent spell runtimes are ticked or torn down.
/// Individual spells continue to own WHAT their runtime state means and how it
/// behaves while alive. Keep this dispatcher deliberately explicit; it is not a
/// generic spell engine or registry.
///
/// Migration note: Shatterbolt is now owned by this lifetime boundary. Plasma
/// Bolt intentionally remains on its existing bridge until its own migration
/// chunk, so it must not be dispatched or torn down here yet.
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

        if (object.ReferenceEquals(lastLocalOwner, owner))
            lastLocalOwner = null;
    }

    /// <summary>
    /// World-global teardown for migrated persistent Orrery spell runtimes. Shared
    /// Orrery services remain owned by their class/runtime boundaries rather than
    /// being hidden inside an arbitrary spell cleanup path.
    /// </summary>
    public static void ResetWorld()
    {
        OrreryShatterbolt.Reset();
        lastLocalOwner = null;
    }
}

/// <summary>
/// Single fixed-step bridge for persistent Orrery spell lifetime orchestration.
/// Individual spell files do not own their own controller FixedUpdate patches.
/// </summary>
[HarmonyPatch(typeof(OrreryController), "FixedUpdate")]
public static class OrrerySpellLifetimeFixedTickPatch
{
    public static void Postfix()
    {
        OrrerySpellLifetime.FixedTickLocal(Time.fixedDeltaTime);
    }
}

[HarmonyPatch(typeof(WorldController), "OnDestroy")]
public static class OrrerySpellLifetimeWorldDestroyedPatch
{
    public static void Prefix()
    {
        OrrerySpellLifetime.ResetWorld();
    }
}

[HarmonyPatch(typeof(GameShip), "OnDestroy")]
public static class OrrerySpellLifetimeOwnerDestroyedPatch
{
    public static void Prefix(GameShip __instance)
    {
        OrrerySpellLifetime.ForgetOwner(__instance);
    }
}
