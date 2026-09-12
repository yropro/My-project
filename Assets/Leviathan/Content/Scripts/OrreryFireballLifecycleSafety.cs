using HarmonyLib;
using StarVortex;
using System.Collections.Generic;

/// <summary>
/// Non-gameplay cleanup boundary for live Orrery FF projectiles.
///
/// ExplosiveProjectile.TimedDestroy intentionally explodes. That is correct for
/// RMB release / authored expiry, but wrong for execution cancellation, class
/// replacement and world teardown. Managed FF projectiles are therefore captured
/// with Projectile.CaptureDestroy(), which preserves native launcher removal and
/// network-despawn/pool lifecycle without authoring an explosion.
/// </summary>
public static class OrreryFireballLifecycleSafety
{
    private sealed class Entry
    {
        public GameShip OriginalOwner;
        public Launcher Launcher;
    }

    private static readonly Dictionary<Projectile, Entry> managed =
        new Dictionary<Projectile, Entry>(4);
    private static readonly List<Projectile> cleanupScratch =
        new List<Projectile>(4);

    public static void RegisterIfManagedFireball(
        Launcher launcher,
        Projectile projectile)
    {
        if (launcher == null || projectile == null ||
            !(projectile is ExplosiveProjectile))
        {
            return;
        }

        GameShip owner = launcher.parentShip;
        if (owner == null || !OrreryRuntime.IsActive(owner) ||
            OrreryWeaponSuppression.ShouldSuppress(launcher) ||
            launcher.damageType != Damageable.DamageType.Thermal)
        {
            return;
        }

        // V0 has exactly one hidden thermal explosive Orrery adapter: FF. Keep
        // this detection deliberately narrow to hidden Orrery-owned launchers;
        // when more thermal projectile spells exist, runtime should publish an
        // explicit semantic projectile identity instead of widening heuristics.
        managed[projectile] = new Entry
        {
            OriginalOwner = owner,
            Launcher = launcher
        };
    }

    public static bool TryGetOriginalOwner(
        Projectile projectile,
        out GameShip owner)
    {
        owner = null;
        Entry entry;
        if (projectile == null || !managed.TryGetValue(projectile, out entry) ||
            entry == null)
        {
            return false;
        }

        owner = entry.OriginalOwner;
        return owner != null;
    }

    public static void Unregister(Projectile projectile)
    {
        if (projectile != null)
            managed.Remove(projectile);
    }

    public static void CaptureForOwner(GameShip owner)
    {
        if (owner == null || managed.Count == 0)
            return;

        cleanupScratch.Clear();
        foreach (KeyValuePair<Projectile, Entry> pair in managed)
        {
            if (pair.Key != null && pair.Value != null &&
                object.ReferenceEquals(pair.Value.OriginalOwner, owner))
            {
                cleanupScratch.Add(pair.Key);
            }
        }

        CaptureScratch();
    }

    public static void CaptureForLauncher(Launcher launcher)
    {
        if (launcher == null || managed.Count == 0)
            return;

        cleanupScratch.Clear();
        foreach (KeyValuePair<Projectile, Entry> pair in managed)
        {
            if (pair.Key != null && pair.Value != null &&
                object.ReferenceEquals(pair.Value.Launcher, launcher))
            {
                cleanupScratch.Add(pair.Key);
            }
        }

        CaptureScratch();
    }

    public static void CaptureAll()
    {
        if (managed.Count == 0)
            return;

        cleanupScratch.Clear();
        foreach (Projectile projectile in managed.Keys)
        {
            if (projectile != null)
                cleanupScratch.Add(projectile);
        }

        CaptureScratch();
    }

    private static void CaptureScratch()
    {
        for (int i = 0; i < cleanupScratch.Count; i++)
        {
            Projectile projectile = cleanupScratch[i];
            if (projectile == null)
                continue;

            managed.Remove(projectile);
            if (projectile.gameObject != null &&
                projectile.gameObject.activeInHierarchy)
            {
                projectile.CaptureDestroy();
            }
        }
        cleanupScratch.Clear();
    }
}

/// <summary>
/// Register FF before Projectile.Init can perform its spawn-frame collision.
/// </summary>
[HarmonyPatch(typeof(Projectile), "Init")]
public static class OrreryFireballLifecycleInitPatch
{
    public static void Prefix(
        Projectile __instance,
        Launcher parentLauncher)
    {
        OrreryFireballLifecycleSafety.RegisterIfManagedFireball(
            parentLauncher,
            __instance);
    }
}

/// <summary>
/// Any logical cast cancellation invalidates its live FF interaction. Remove the
/// physical projectile without triggering explosive gameplay, then let normal
/// shuffle/rearm proceed.
/// </summary>
[HarmonyPatch(typeof(OrreryCasting), "Cancel")]
public static class OrreryFireballCastCancelPatch
{
    public static void Postfix(GameShip owner)
    {
        OrreryFireballLifecycleSafety.CaptureForOwner(owner);
    }
}

/// <summary>
/// Launcher.Unequip/StatsChanged can TimedDestroy active projectiles. Capture FF
/// first so class/spec/world teardown cannot author an explosion as a side effect.
/// </summary>
[HarmonyPatch(typeof(Launcher), "Unequip")]
public static class OrreryFireballLauncherUnequipPatch
{
    public static void Prefix(Launcher __instance)
    {
        OrreryFireballLifecycleSafety.CaptureForLauncher(__instance);
    }
}

[HarmonyPatch(typeof(Projectile), "PoolDestroy")]
public static class OrreryFireballLifecyclePoolPatch
{
    public static void Postfix(Projectile __instance)
    {
        OrreryFireballLifecycleSafety.Unregister(__instance);
    }
}

/// <summary>
/// World teardown must remove live FF projectiles before any hidden launcher can
/// be disposed. CaptureDestroy keeps native removal/despawn semantics intact.
/// </summary>
[HarmonyPatch(typeof(WorldController), "OnDestroy")]
[HarmonyPriority(Priority.First)]
public static class OrreryFireballWorldDestroyPatch
{
    public static void Prefix()
    {
        OrreryFireballLifecycleSafety.CaptureAll();
    }
}
