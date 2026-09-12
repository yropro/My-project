using HarmonyLib;
using StarVortex;
using System.Collections.Generic;

/// <summary>
/// Reflection contract for Orrery FF.
///
/// Native Projectile reflection transfers parentShip, reflected trajectory and
/// lifetime to the Shield Ward owner. Orrery then severs only the original cast
/// relationship: no further cursor steering, no original-owner RMB detonation,
/// and no original-owner extra direct-target explosion packet. The reflected
/// projectile itself remains alive and follows ordinary native projectile rules.
/// </summary>
public static class OrreryFireballReflectionSafety
{
    // Very short bridge between native reflection and OrrerySpellRuntime's next
    // FixedTick, where the cancelled execution is observed and the private active
    // cast is cleared. This prevents an RMB-release edge in that interval from
    // manually detonating the already-reflected projectile.
    private static readonly HashSet<GameShip> releaseBlockedOwners =
        new HashSet<GameShip>();

    public static void OnReflected(Projectile projectile)
    {
        if (projectile == null)
            return;

        GameShip originalOwner;
        if (!OrreryFireballLifecycleSafety.TryGetOriginalOwner(
                projectile,
                out originalOwner) ||
            originalOwner == null)
        {
            return;
        }

        GameShip currentOwner = projectile.GetParentShip();
        if (currentOwner == null || object.ReferenceEquals(currentOwner, originalOwner))
            return;

        if (!OrreryFireballLifecycleSafety.DetachAfterReflection(
                projectile,
                out originalOwner) ||
            originalOwner == null)
        {
            return;
        }

        releaseBlockedOwners.Add(originalOwner);

        // Invalidate the authored cast immediately. Because the projectile has
        // already been marked detached, OrreryCasting.Cancel's lifecycle safety
        // postfix will not CaptureDestroy it. OrrerySpellRuntime sees the invalid
        // execution on its next fixed tick, clears private active state and starts
        // normal shuffle/rearm without touching the reflected projectile.
        OrreryCasting.Cancel(originalOwner);
        OrreryNetwork.PublishLocal(originalOwner);
    }

    public static bool IsReleaseBlocked(GameShip owner)
    {
        return owner != null && releaseBlockedOwners.Contains(owner);
    }

    public static void ClearReleaseBlock(GameShip owner)
    {
        if (owner != null)
            releaseBlockedOwners.Remove(owner);
    }

    public static void Reset()
    {
        releaseBlockedOwners.Clear();
    }
}

/// <summary>
/// Native reflection happens entirely inside TryReflectOffShieldWard. Observe the
/// successful result after native ownership/trajectory transfer has completed.
/// </summary>
[HarmonyPatch(typeof(Projectile), "TryReflectOffShieldWard")]
public static class OrreryFireballReflectionDetachPatch
{
    public static void Postfix(Projectile __instance, bool __result)
    {
        if (__result)
            OrreryFireballReflectionSafety.OnReflected(__instance);
    }
}

/// <summary>
/// Close the single-frame input window between native reflection and the spell
/// runtime observing the cancelled execution.
/// </summary>
[HarmonyPatch(typeof(OrrerySpellRuntime), "ReleaseInvoke")]
public static class OrreryFireballReflectedReleaseGuardPatch
{
    public static bool Prefix(GameShip owner, ref bool __result)
    {
        if (!OrreryFireballReflectionSafety.IsReleaseBlocked(owner))
            return true;

        __result = false;
        return false;
    }
}

/// <summary>
/// By the end of OrrerySpellRuntime.FixedTick the cancelled FF execution has been
/// consumed and private active state has been cleared, so future spell releases
/// for the owner are no longer blocked.
/// </summary>
[HarmonyPatch(typeof(OrrerySpellRuntime), "FixedTick")]
public static class OrreryFireballReflectionFixedTickPatch
{
    public static void Postfix(GameShip owner)
    {
        OrreryFireballReflectionSafety.ClearReleaseBlock(owner);
    }
}

/// <summary>
/// Native direct/AoE packets already use the reflected projectile's new parentShip.
/// Orrery's extra same-target explosion packet is authored from the original cast,
/// so suppress that bonus whenever ownership has changed.
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

[HarmonyPatch(typeof(WorldController), "OnDestroy")]
public static class OrreryFireballReflectionWorldDestroyPatch
{
    public static void Postfix()
    {
        OrreryFireballReflectionSafety.Reset();
    }
}
