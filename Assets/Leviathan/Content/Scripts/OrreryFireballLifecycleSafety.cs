using HarmonyLib;
using StarVortex;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Non-gameplay cleanup boundary for live Orrery FF projectiles.
///
/// ExplosiveProjectile.TimedDestroy intentionally explodes. That is correct for
/// RMB release / authored expiry, but wrong for execution cancellation, class
/// replacement and world teardown. Managed FF projectiles are therefore captured
/// with Projectile.CaptureDestroy(), which preserves native launcher removal and
/// network-despawn/pool lifecycle without authoring an explosion.
///
/// Reflected FF projectiles remain registered until they return to the pool so we
/// can restore presentation scale and preserve original-owner provenance, but are
/// marked detached so original-owner/class cleanup can no longer capture them.
/// </summary>
public static class OrreryFireballLifecycleSafety
{
    private sealed class Entry
    {
        public GameShip OriginalOwner;
        public Launcher Launcher;
        public Vector3 OriginalScale;
        public bool Detached;
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
        Entry entry = new Entry
        {
            OriginalOwner = owner,
            Launcher = launcher,
            OriginalScale = projectile.transform.localScale,
            Detached = false
        };
        managed[projectile] = entry;

        float visualScale = Mathf.Max(
            0.01f,
            OrrerySpellRuntime.Tuning.FireballProjectileVisualScale);
        projectile.transform.localScale = entry.OriginalScale * visualScale;
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

    public static bool IsDetached(Projectile projectile)
    {
        Entry entry;
        return projectile != null &&
            managed.TryGetValue(projectile, out entry) &&
            entry != null && entry.Detached;
    }

    /// <summary>
    /// Severs the original Orrery runtime/launcher relationship after native
    /// Shield Ward reflection without destroying the projectile. Native reflection
    /// has already transferred parentShip and reset its trajectory/lifetime.
    /// </summary>
    public static bool DetachAfterReflection(
        Projectile projectile,
        out GameShip originalOwner)
    {
        originalOwner = null;
        Entry entry;
        if (projectile == null || !managed.TryGetValue(projectile, out entry) ||
            entry == null || entry.Detached)
        {
            return false;
        }

        originalOwner = entry.OriginalOwner;
        Launcher launcher = entry.Launcher;
        entry.Detached = true;
        entry.Launcher = null;

        // Remove from the hidden adapter's active-projectile list so a later
        // adapter Unequip cannot destroy the reflected projectile. The projectile
        // intentionally keeps its parentLauncher reference; its eventual native
        // TimedDestroy can safely call RemoveProjectile again.
        if (launcher != null)
            launcher.RemoveProjectile(projectile);

        return originalOwner != null;
    }

    public static void RestoreScale(Projectile projectile)
    {
        Entry entry;
        if (projectile == null || !managed.TryGetValue(projectile, out entry) ||
            entry == null)
        {
            return;
        }

        projectile.transform.localScale = entry.OriginalScale;
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
                !pair.Value.Detached &&
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
                !pair.Value.Detached &&
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

            RestoreScale(projectile);
            managed.Remove(projectile);
            if (!projectile.IsDestroying() &&
                projectile.gameObject != null &&
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
/// shuffle/rearm proceed. Reflected/detached FF is explicitly excluded.
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
/// Reflected FF has already been removed from the hidden launcher's active list.
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
    public static void Prefix(Projectile __instance)
    {
        OrreryFireballLifecycleSafety.RestoreScale(__instance);
    }

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
