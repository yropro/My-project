using HarmonyLib;
using StarVortex;

/// <summary>
/// Reflection correctness guard for Orrery FF.
///
/// Native Projectile reflection transfers parentShip to the reflecting ship, so
/// native direct/AoE packets are attributed correctly. Orrery's extra packet for
/// the directly struck target currently resolves through the original cast owner.
/// Until reflected-FF steering/ownership is explicitly designed, suppress only
/// that extra packet after ownership has changed rather than misattribute damage.
/// </summary>
[HarmonyPatch(typeof(OrrerySpellRuntime), "OnExplosiveProjectileHit")]
public static class OrreryFireballReflectedBonusGuardPatch
{
    public static bool Prefix(ExplosiveProjectile projectile)
    {
        if (projectile == null)
            return true;

        GameShip originalOwner;
        if (!OrreryFireballLifecycleSafety.TryGetOriginalOwner(
                projectile,
                out originalOwner) ||
            originalOwner == null)
        {
            return true;
        }

        GameShip currentOwner = projectile.GetParentShip();
        return currentOwner == null || object.ReferenceEquals(currentOwner, originalOwner);
    }
}
