using HarmonyLib;
using StarVortex;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Explicit safety boundary for Orrery's presentation-only Cryo burst.
///
/// Setting the hidden Cryo launcher's authored damage/status to zero is not by
/// itself sufficient because global player modifiers can still be applied to an
/// equipped Activatable. Projectiles emitted by the V0 visual adapter are tracked
/// for their short native lifetime and denied mechanical hit processing.
/// </summary>
public static class OrrerySpellPresentationSafety
{
    private static readonly HashSet<Projectile> presentationOnly =
        new HashSet<Projectile>();

    public static bool IsPresentationOnly(Projectile projectile)
    {
        return projectile != null && presentationOnly.Contains(projectile);
    }

    public static void RegisterIfPresentationOnly(
        Launcher launcher,
        Projectile projectile)
    {
        if (projectile == null)
            return;

        // A pooled projectile may previously have belonged to the visual set.
        presentationOnly.Remove(projectile);

        ChargingLauncher cryo = launcher as ChargingLauncher;
        if (cryo == null || cryo.parentShip == null ||
            !OrreryRuntime.IsActive(cryo.parentShip) ||
            OrreryWeaponSuppression.ShouldSuppress(cryo) ||
            cryo.damageType != Damageable.DamageType.Cold ||
            !Mathf.Approximately(cryo.BaseDamage, 0f) ||
            !Mathf.Approximately(cryo.BaseStatusEffectChance, 0f) ||
            cryo.BaseShotCount != OrrerySpellRuntime.Tuning.CryoVisualProjectileCount ||
            cryo.shotAngle != OrrerySpellRuntime.Tuning.CryoVisualSpreadDegrees)
        {
            return;
        }

        presentationOnly.Add(projectile);
    }

    public static void Unregister(Projectile projectile)
    {
        if (projectile != null)
            presentationOnly.Remove(projectile);
    }

    public static void Reset()
    {
        presentationOnly.Clear();
    }
}

[HarmonyPatch(typeof(Launcher), "AddProjectile")]
public static class OrreryPresentationProjectileCapturePatch
{
    public static void Postfix(Launcher __instance, Projectile projectile)
    {
        OrrerySpellPresentationSafety.RegisterIfPresentationOnly(
            __instance,
            projectile);
    }
}

/// <summary>
/// Base Projectile.HitObject is the common mechanical damage/status boundary.
/// Specialized projectile classes in the supplied game assembly call through it;
/// returning false also tells those derived classes that no valid hit occurred.
/// </summary>
[HarmonyPatch(typeof(Projectile), "HitObject")]
public static class OrreryPresentationProjectileHitPatch
{
    public static bool Prefix(Projectile __instance, ref bool __result)
    {
        if (!OrrerySpellPresentationSafety.IsPresentationOnly(__instance))
            return true;

        __result = false;
        return false;
    }
}

/// <summary>
/// FuzzyProjectile can author an end-of-life secondary space effect before its
/// base cleanup. Cryo presentation projectiles are not allowed to create any
/// mechanical secondary effect, so they return directly to the native pool.
/// </summary>
[HarmonyPatch(typeof(FuzzyProjectile), "ScheduleDestroy")]
public static class OrreryPresentationFuzzyDestroyPatch
{
    public static bool Prefix(FuzzyProjectile __instance)
    {
        if (!OrrerySpellPresentationSafety.IsPresentationOnly(__instance))
            return true;

        __instance.PoolDestroy();
        return false;
    }
}

/// <summary>
/// Defensive coverage if a future Cryo visual prefab is explosive: expiry must
/// still remain presentation-only rather than invoking ExplosiveProjectile AoE.
/// </summary>
[HarmonyPatch(typeof(ExplosiveProjectile), "TimedDestroy")]
public static class OrreryPresentationExplosiveExpiryPatch
{
    public static bool Prefix(ExplosiveProjectile __instance)
    {
        if (!OrrerySpellPresentationSafety.IsPresentationOnly(__instance))
            return true;

        __instance.PoolDestroy();
        return false;
    }
}

[HarmonyPatch(typeof(Projectile), "PoolDestroy")]
public static class OrreryPresentationProjectilePoolPatch
{
    public static void Postfix(Projectile __instance)
    {
        OrrerySpellPresentationSafety.Unregister(__instance);
    }
}

[HarmonyPatch(typeof(WorldController), "OnDestroy")]
public static class OrreryPresentationProjectileWorldDestroyPatch
{
    public static void Prefix()
    {
        OrrerySpellPresentationSafety.Reset();
    }
}
