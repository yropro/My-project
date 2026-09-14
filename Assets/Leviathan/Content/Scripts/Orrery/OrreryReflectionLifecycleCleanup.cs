using HarmonyLib;
using StarVortex;

/// <summary>
/// Reflection's release-block set exists only to bridge one fixed tick between
/// native ownership transfer and the Orrery runtime consuming its cancelled cast.
/// Class exit or ship destruction can remove the original owner before that tick
/// runs, so clear the optional guard at teardown instead of retaining the wrapper
/// until world reset.
///
/// Reflected Magma presentation deliberately remains on the original caster's
/// Orrery transport stream. Hidden virtual launchers do not occupy a real native
/// player slot, so Star Vortex cannot register their projectiles in the native
/// NetProjectile stream; the custom Orrery stream must continue carrying the
/// reflected projectile's pose and eventual explosion for peers.
/// </summary>
[HarmonyPatch(typeof(OrrerySpellRuntime), nameof(OrrerySpellRuntime.Forget))]
public static class OrreryReflectionRuntimeForgetPatch
{
    public static void Postfix(GameShip owner)
    {
        OrreryFireballReflectionSafety.ClearReleaseBlock(owner);
    }
}

[HarmonyPatch(typeof(GameShip), "OnDestroy")]
public static class OrreryReflectionOwnerDestroyedPatch
{
    public static void Prefix(GameShip __instance)
    {
        OrreryFireballReflectionSafety.ClearReleaseBlock(__instance);
    }
}
