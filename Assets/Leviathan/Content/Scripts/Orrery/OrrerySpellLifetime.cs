using HarmonyLib;
using StarVortex;
using UnityEngine;

/// <summary>
/// Orrery spell-lifetime orchestration boundary.
///
/// This layer owns WHEN persistent spell runtimes are ticked or torn down.
/// Individual spells own WHAT their state means. Accretion is unusual only in
/// that its authoritative runtime may live on a non-Orrery recipient; the same
/// shared lifetime bridge therefore ticks both the local Orrery owner and the
/// personally authoritative local player target.
/// </summary>
public static class OrrerySpellLifetime
{
    private static GameShip lastLocalOwner;

    public static void FixedTickLocal(float deltaTime)
    {
        GameShip localPlayer = WorldController.instance == null
            ? null
            : WorldController.instance.GetCurrentPlayerShip();

        // Recipient-authoritative support effects must tick even when this peer's
        // local class is Leviathan/vanilla rather than Orrery.
        OrreryAccretionDisk.FixedTickRecipient(localPlayer, deltaTime);
        CoreProjectileCapture.Tick();

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

    private static void FixedTickOwner(GameShip owner, float deltaTime)
    {
        OrreryShatterbolt.FixedTick(owner, deltaTime);
        OrreryPlasmaBolt.FixedTick(owner, deltaTime);
    }

    public static void ForgetOwner(GameShip owner)
    {
        if (object.ReferenceEquals(owner, null))
            return;

        OrreryShatterbolt.Forget(owner);
        OrreryPlasmaBolt.Forget(owner);
        OrreryAccretionDisk.ForgetOrreryOwner(owner);

        if (object.ReferenceEquals(lastLocalOwner, owner))
            lastLocalOwner = null;
    }

    public static void ForgetShip(GameShip ship)
    {
        if (object.ReferenceEquals(ship, null))
            return;

        OrreryAccretionDisk.ForgetTarget(ship);
        OrreryPlasmaBolt.ForgetTarget(ship);
        ForgetOwner(ship);
        OrreryPlasmaBoltPresentation.ForgetTarget(ship);
        OrreryPlasmaBoltPresentation.Forget(ship);
        OrreryAccretionDiskPresentation.Hide(ship);
    }

    public static void ResetWorld()
    {
        OrreryShatterbolt.Reset();
        OrreryPlasmaBolt.Reset();
        OrreryAccretionDisk.Reset();
        OrreryDamageRouter.Reset();
        lastLocalOwner = null;
    }
}

/// <summary>
/// One fixed-step bridge for persistent Orrery lifetimes. WorldController is used
/// rather than the Orrery controller because recipient-owned support effects must
/// continue on peers whose local player is using another class.
/// </summary>
[HarmonyPatch(typeof(WorldController), "FixedUpdate")]
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
        OrrerySpellLifetime.ForgetShip(__instance);
    }
}

[HarmonyPatch(typeof(GameShip), "Destroyed")]
public static class OrrerySpellLifetimeShipDyingPatch
{
    public static void Prefix(GameShip __instance)
    {
        OrrerySpellLifetime.ForgetShip(__instance);
    }
}
