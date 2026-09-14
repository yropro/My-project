using HarmonyLib;
using StarVortex;

/// <summary>
/// Captures Magma Cannon's native impact explosion before Projectile.ScheduleDestroy
/// is allowed to return the projectile to its pool.
///
/// ExplosiveProjectile.HitObject sets hasExploded before calling the base hit path.
/// The base path may synchronously ScheduleDestroy/PoolDestroy on a prefab without
/// a lingering trail or emitter. Orrery's ordinary PoolDestroy cleanup would then
/// forget the projectile before the later spell-hit postfix could publish the
/// explosion presentation. Sampling at this native destruction boundary preserves
/// the event without changing projectile lifetime or gameplay.
/// </summary>
[HarmonyPatch(typeof(Projectile), nameof(Projectile.ScheduleDestroy))]
public static class OrreryMagmaPresentationImpactOrderingPatch
{
    [HarmonyPriority(Priority.First)]
    public static void Prefix(Projectile __instance)
    {
        ExplosiveProjectile explosive = __instance as ExplosiveProjectile;
        if (explosive == null || !explosive.hasExploded)
            return;

        // RecordMagmaExplosion is already narrowly gated by the live
        // Orrery-owned Magma projectile map, so ordinary explosive projectiles
        // pass through untouched.
        OrreryLegacySpellPresentation.RecordMagmaExplosion(
            explosive,
            explosive.transform.position);
    }
}
