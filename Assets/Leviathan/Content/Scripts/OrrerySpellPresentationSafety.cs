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
/// for their short native lifetime and denied only mechanical interactions while
/// native projectile teardown/pooling/despawn lifecycle remains intact.
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

/// <summary>
/// Native Launcher.ShootProjectile calls Projectile.Init before AddProjectile,
/// and Projectile.Init may immediately perform collision. Capture Orrery's FF
/// projectile and register II presentation projectiles before that Init body so
/// spawn-frame hits obey the same semantics as later-frame hits.
/// </summary>
[HarmonyPatch(typeof(Projectile), "Init")]
public static class OrreryPresentationProjectileInitPatch
{
    public static void Prefix(
        Projectile __instance,
        Launcher parentLauncher)
    {
        OrrerySpellRuntime.OnProjectileAdded(parentLauncher, __instance);
        OrrerySpellPresentationSafety.RegisterIfPresentationOnly(
            parentLauncher,
            __instance);
    }
}

/// <summary>
/// Keep AddProjectile registration as a defensive fallback for future native
/// projectile paths that may initialize or pool in a different order.
/// </summary>
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
/// Shield Ward reflection happens in Projectile.UpdateCollision before
/// Projectile.HitObject. A presentation-only Cryo shard must not become a real
/// mechanical reflected projectile, so block only the ward interaction while
/// leaving the rest of native collision/lifetime processing untouched.
/// </summary>
[HarmonyPatch(typeof(Projectile), "TryReflectOffShieldWard")]
public static class OrreryPresentationProjectileWardReflectionPatch
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
/// Base Projectile.HitObject is the common mechanical damage/status boundary.
/// Specialized projectile classes in the supplied game assembly call through it;
/// returning false also tells those derived classes that no valid hit occurred.
/// Native movement, expiry, launcher removal and pooling still run normally.
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
/// FuzzyProjectile can author a BurningSpace secondary effect both during its
/// end-of-life update and during ScheduleDestroy. Block only that mechanical
/// effect and leave native ScheduleDestroy intact so launcher membership and
/// network despawn bookkeeping are still cleaned up normally.
/// </summary>
[HarmonyPatch(typeof(FuzzyProjectile), "RollBurningSpace")]
public static class OrreryPresentationFuzzySecondaryPatch
{
    public static bool Prefix(FuzzyProjectile __instance)
    {
        return !OrrerySpellPresentationSafety.IsPresentationOnly(__instance);
    }
}

/// <summary>
/// Defensive coverage if a future Cryo visual prefab is explosive. Block only
/// the private mechanical AoE routine; native TimedDestroy/HitObject lifecycle,
/// visuals, launcher removal and despawn bookkeeping remain untouched.
/// </summary>
[HarmonyPatch(typeof(ExplosiveProjectile), "Explode")]
public static class OrreryPresentationExplosiveMechanicalPatch
{
    public static bool Prefix(ExplosiveProjectile __instance)
    {
        return !OrrerySpellPresentationSafety.IsPresentationOnly(__instance);
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

/// <summary>
/// Clear presentation bookkeeping only after native/Orrery world teardown has
/// had a chance to destroy pooled projectiles. Clearing in a Prefix could remove
/// the guard before another OnDestroy Prefix disposes hidden Cryo weapons.
/// </summary>
[HarmonyPatch(typeof(WorldController), "OnDestroy")]
public static class OrreryPresentationProjectileWorldDestroyPatch
{
    public static void Postfix()
    {
        OrrerySpellPresentationSafety.Reset();
    }
}
