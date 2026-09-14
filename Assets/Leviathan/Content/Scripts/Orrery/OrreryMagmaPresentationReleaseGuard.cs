using HarmonyLib;
using StarVortex;

/// <summary>
/// Keeps Magma Cannon presentation aligned with the live release-only explosion
/// contract. Managed contact hits deliberately suppress ExplosiveProjectile.Explode;
/// presentation must suppress the corresponding legacy contact callback too.
///
/// The guard sits at the canonical presentation-record boundary so every current
/// capture path (spell-hit postfix, TimedDestroy and ScheduleDestroy fallback) uses
/// the same decision without duplicating gameplay or transport state.
/// </summary>
[HarmonyPatch(
    typeof(OrreryLegacySpellPresentation),
    nameof(OrreryLegacySpellPresentation.RecordMagmaExplosion))]
public static class OrreryMagmaPresentationReleaseGuard
{
    [HarmonyPriority(Priority.First)]
    public static bool Prefix(ExplosiveProjectile projectile)
    {
        if (projectile == null)
            return true;

        // A managed Magma orb with explodeOnExpiry=false is still in its held /
        // contact-only phase. OrreryFireballLifecycleSafety flips that state for
        // the authored RMB release before native TimedDestroy/Explode runs.
        return !OrreryFireballLifecycleSafety.ShouldSuppressExplosion(projectile);
    }
}
