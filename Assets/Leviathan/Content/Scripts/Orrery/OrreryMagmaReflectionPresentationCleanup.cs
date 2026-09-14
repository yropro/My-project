using HarmonyLib;
using StarVortex;

/// <summary>
/// Presentation cleanup for reflected Magma Cannon projectiles.
///
/// Native Shield Ward reflection transfers projectile ownership before Orrery
/// detaches the authored cast. Once that detach succeeds, the original Orrery
/// presentation must stop tracking the projectile too; otherwise observers can
/// retain a ghost Magma orb and later attribute its ordinary reflected explosion
/// to the original caster.
/// </summary>
[HarmonyPatch(typeof(OrreryFireballLifecycleSafety),
    nameof(OrreryFireballLifecycleSafety.DetachAfterReflection))]
public static class OrreryMagmaReflectionPresentationCleanupPatch
{
    public static void Postfix(Projectile projectile, bool __result)
    {
        if (__result)
            OrreryLegacySpellPresentation.ForgetMagmaProjectile(projectile);
    }
}

/// <summary>
/// The release-block set is only a one-fixed-tick bridge, but class exit/death can
/// remove the owner before that tick runs. Clear the optional guard at the same
/// runtime teardown boundary so destroyed/replaced player wrappers are not retained
/// until world teardown.
/// </summary>
[HarmonyPatch(typeof(OrrerySpellRuntime), nameof(OrrerySpellRuntime.Forget))]
public static class OrreryMagmaReflectionRuntimeForgetPatch
{
    public static void Postfix(GameShip owner)
    {
        OrreryFireballReflectionSafety.ClearReleaseBlock(owner);
    }
}

[HarmonyPatch(typeof(GameShip), "OnDestroy")]
public static class OrreryMagmaReflectionOwnerDestroyedPatch
{
    public static void Prefix(GameShip __instance)
    {
        OrreryFireballReflectionSafety.ClearReleaseBlock(__instance);
    }
}
