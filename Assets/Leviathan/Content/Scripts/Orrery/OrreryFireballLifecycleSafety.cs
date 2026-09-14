using HarmonyLib;
using StarVortex;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

/// <summary>
/// Lifecycle and contact semantics for live Orrery FF/Magma Cannon projectiles.
///
/// A managed Magma Cannon orb deals its normal native direct-hit packet on
/// contact, survives the contact, and may hit the same target again after that
/// target has been out of its collision path for the configured re-arm interval.
/// Its explosive-area packet is reserved for the authored RMB release edge.
/// Ordinary lifetime expiry, execution cancellation, class replacement and world
/// teardown therefore remove the orb without detonating it.
///
/// Reflected FF projectiles remain registered until they return to the pool so we
/// can restore projectile scale and preserve original-owner provenance, but are
/// marked detached and immediately return to ordinary native projectile behavior.
/// </summary>
public static class OrreryFireballLifecycleSafety
{
    private sealed class Entry
    {
        public GameShip OriginalOwner;
        public Launcher Launcher;
        public Vector3 OriginalScale;
        public bool OriginalExplodeOnExpiry;
        public bool Detached;
        public int ContactCount;
        public readonly GameObject[] ContactTargets =
            new GameObject[OrrerySpellCompendium.MagmaCannon.MaxTrackedContactTargets];
        public readonly float[] LastContactAttemptTimes =
            new float[OrrerySpellCompendium.MagmaCannon.MaxTrackedContactTargets];
    }

    private static readonly Dictionary<Projectile, Entry> managed =
        new Dictionary<Projectile, Entry>(4);
    private static readonly List<Projectile> cleanupScratch =
        new List<Projectile>(4);

    private static readonly FieldInfo projectilePiercingField =
        AccessTools.Field(typeof(Projectile), "piercing");
    private static readonly FieldInfo projectileIgnoreField =
        AccessTools.Field(typeof(Projectile), "ignore");

    public static void RegisterIfManagedFireball(
        Launcher launcher,
        Projectile projectile)
    {
        ExplosiveProjectile explosive = projectile as ExplosiveProjectile;
        if (launcher == null || projectile == null || explosive == null)
            return;

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
            OriginalExplodeOnExpiry = explosive.explodeOnExpiry,
            Detached = false
        };
        managed[projectile] = entry;

        // Contact is no longer a detonation trigger and natural expiry is cleanup,
        // not an authored explosion. DetonateFireball flips this back to true just
        // before its release-owned TimedDestroy call.
        explosive.explodeOnExpiry = false;

        float projectileScale = Mathf.Max(
            0.01f,
            OrrerySpellSizing.Tuning.FireballProjectileScaleMultiplier);
        projectile.transform.localScale = entry.OriginalScale * projectileScale;
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
    /// Returns true only while the projectile still belongs to the authored local
    /// Magma Cannon cast. Detached/reflected projectiles deliberately stop using
    /// Orrery contact/detonation overrides.
    /// </summary>
    private static bool TryGetInteractiveEntry(
        Projectile projectile,
        out Entry entry)
    {
        entry = null;
        return projectile != null &&
            managed.TryGetValue(projectile, out entry) &&
            entry != null && !entry.Detached;
    }

    /// <summary>
    /// Temporarily makes the managed projectile piercing for one native HitObject
    /// call. That preserves the exact native direct-damage/status/crit packet while
    /// preventing Projectile.HitObject from scheduling destruction on contact.
    /// Native piercing also records the target in Projectile.ignore, which is what
    /// prevents damage every fixed tick while the orb is still crossing a target.
    /// </summary>
    public static bool BeginContactHit(
        ExplosiveProjectile projectile,
        GameObject hitObject,
        out bool originalPiercing)
    {
        originalPiercing = false;

        Entry entry;
        if (!TryGetInteractiveEntry(projectile, out entry) ||
            projectilePiercingField == null || projectileIgnoreField == null)
        {
            return false;
        }

        int contactIndex = FindContact(entry, hitObject);
        if (contactIndex >= 0)
        {
            float now = Time.time;
            float separationSeconds = now - entry.LastContactAttemptTimes[contactIndex];
            if (separationSeconds >= Mathf.Max(
                    0f,
                    OrrerySpellCompendium.MagmaCannon.ContactRearmSeconds))
            {
                RemoveFromNativeIgnore(projectile, hitObject);
            }
            entry.LastContactAttemptTimes[contactIndex] = now;
        }

        object rawPiercing = projectilePiercingField.GetValue(projectile);
        if (!(rawPiercing is bool))
            return false;

        originalPiercing = (bool)rawPiercing;
        projectilePiercingField.SetValue(projectile, true);
        return true;
    }

    public static void EndContactHit(
        ExplosiveProjectile projectile,
        GameObject hitObject,
        bool originalPiercing,
        bool hitSucceeded)
    {
        Entry entry;
        if (!TryGetInteractiveEntry(projectile, out entry))
            return;

        if (projectilePiercingField != null)
            projectilePiercingField.SetValue(projectile, originalPiercing);

        if (!hitSucceeded)
            return;

        // ExplosiveProjectile marks itself exploded before delegating to the base
        // direct-hit routine. Contact AoE is suppressed below, so restore the live
        // projectile state after the successful direct packet.
        projectile.hasExploded = false;
        RecordContact(entry, hitObject, Time.time);
    }

    /// <summary>
    /// Contact calls ExplosiveProjectile.Explode directly. A managed orb suppresses
    /// that call while explodeOnExpiry is false. RMB release is the one path that
    /// deliberately sets explodeOnExpiry true before TimedDestroy, so its native
    /// explosion remains intact.
    /// </summary>
    public static bool ShouldSuppressExplosion(ExplosiveProjectile projectile)
    {
        Entry entry;
        return TryGetInteractiveEntry(projectile, out entry) &&
            projectile != null && !projectile.explodeOnExpiry;
    }

    private static int FindContact(Entry entry, GameObject target)
    {
        if (entry == null || target == null)
            return -1;

        for (int i = 0; i < entry.ContactCount; i++)
        {
            if (object.ReferenceEquals(entry.ContactTargets[i], target))
                return i;
        }
        return -1;
    }

    private static void RecordContact(Entry entry, GameObject target, float now)
    {
        if (entry == null || target == null)
            return;

        int existing = FindContact(entry, target);
        if (existing >= 0)
        {
            entry.LastContactAttemptTimes[existing] = now;
            return;
        }

        int capacity = entry.ContactTargets.Length;
        if (capacity <= 0)
            return;

        if (entry.ContactCount < capacity)
        {
            int index = entry.ContactCount++;
            entry.ContactTargets[index] = target;
            entry.LastContactAttemptTimes[index] = now;
            return;
        }

        // Storage is deliberately bounded. Prefer keeping the most recently used
        // contact set useful rather than growing an unbounded collection over a
        // long/high-density encounter. A displaced target remains safely ignored
        // by the native projectile for this cast; it simply loses repeat-hit rearm.
        int oldestIndex = 0;
        float oldestTime = entry.LastContactAttemptTimes[0];
        for (int i = 1; i < capacity; i++)
        {
            if (entry.LastContactAttemptTimes[i] < oldestTime)
            {
                oldestIndex = i;
                oldestTime = entry.LastContactAttemptTimes[i];
            }
        }

        entry.ContactTargets[oldestIndex] = target;
        entry.LastContactAttemptTimes[oldestIndex] = now;
    }

    private static void RemoveFromNativeIgnore(
        Projectile projectile,
        GameObject target)
    {
        if (projectile == null || target == null || projectileIgnoreField == null)
            return;

        List<GameObject> ignore =
            projectileIgnoreField.GetValue(projectile) as List<GameObject>;
        if (ignore != null)
            ignore.Remove(target);
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

        ExplosiveProjectile explosive = projectile as ExplosiveProjectile;
        if (explosive != null)
            explosive.explodeOnExpiry = entry.OriginalExplodeOnExpiry;

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

        ExplosiveProjectile explosive = projectile as ExplosiveProjectile;
        if (explosive != null)
            explosive.explodeOnExpiry = entry.OriginalExplodeOnExpiry;
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
/// Preserve native direct contact damage without consuming the Magma Cannon orb.
/// The base hit runs as temporarily piercing so native damage/status/crit/relay
/// semantics remain authoritative, then the original piercing state is restored.
/// </summary>
[HarmonyPatch(typeof(ExplosiveProjectile), "HitObject")]
[HarmonyPriority(Priority.First)]
public static class OrreryFireballContactPersistencePatch
{
    public struct State
    {
        public bool Managed;
        public bool OriginalPiercing;
    }

    public static void Prefix(
        ExplosiveProjectile __instance,
        GameObject hitObject,
        ref State __state)
    {
        bool originalPiercing;
        __state.Managed = OrreryFireballLifecycleSafety.BeginContactHit(
            __instance,
            hitObject,
            out originalPiercing);
        __state.OriginalPiercing = originalPiercing;
    }

    public static void Postfix(
        ExplosiveProjectile __instance,
        GameObject hitObject,
        bool __result,
        State __state)
    {
        if (!__state.Managed)
            return;

        OrreryFireballLifecycleSafety.EndContactHit(
            __instance,
            hitObject,
            __state.OriginalPiercing,
            __result);
    }
}

/// <summary>
/// ExplosiveProjectile.HitObject normally detonates immediately after its direct
/// packet. Managed Magma Cannon contacts suppress only that AoE call. Release sets
/// explodeOnExpiry true first, so the existing native TimedDestroy detonation path
/// remains the single authored explosion trigger.
/// </summary>
[HarmonyPatch(typeof(ExplosiveProjectile), "Explode")]
public static class OrreryFireballContactExplosionSuppressionPatch
{
    public static bool Prefix(ExplosiveProjectile __instance)
    {
        return !OrreryFireballLifecycleSafety.ShouldSuppressExplosion(__instance);
    }
}

/// <summary>
/// Defensive network-path guard: a live managed orb is not allowed to acquire an
/// unsolicited native NetExplode while held. Detached/reflected projectiles and
/// the release-owned destroy path keep native behavior.
/// </summary>
[HarmonyPatch(typeof(ExplosiveProjectile), "NetExplode")]
public static class OrreryFireballNetExplosionSuppressionPatch
{
    public static bool Prefix(ExplosiveProjectile __instance)
    {
        return !OrreryFireballLifecycleSafety.ShouldSuppressExplosion(__instance);
    }
}

/// <summary>
/// The old FF behavior added a second explosion-equivalent packet to the directly
/// struck target on every contact. Contact is now direct damage only; the actual
/// release detonation supplies the explosion packet once, at the release position.
/// Reflected projectiles remain covered by their dedicated provenance guard.
/// </summary>
[HarmonyPatch(typeof(OrrerySpellRuntime), "OnExplosiveProjectileHit")]
[HarmonyPriority(Priority.First)]
public static class OrreryFireballDirectExplosionBonusSuppressionPatch
{
    public static bool Prefix(ExplosiveProjectile projectile)
    {
        return !OrreryFireballLifecycleSafety.ShouldSuppressExplosion(projectile);
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
