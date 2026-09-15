using HarmonyLib;
using StarVortex;
using System;
using UnityEngine;

/// <summary>
/// Projectile.Init performs a collision sweep BEFORE Launcher adds/registers the
/// projectile. A barrier contact there must stop the sweep but cannot despawn yet.
/// Hold it until the enclosing launcher call finishes (or the next shared tick for
/// other native spawn paths). Recheck the live field before authorizing capture.
/// </summary>
public static class CoreProjectileSpawnGuard
{
    private const int Limit = 64;
    private struct Pending
    {
        public Projectile Projectile;
        public bool Enabled, Simulated;
        public Vector2 Velocity;
        public float AngularVelocity, Radius;
    }
    private static readonly Projectile[] initializing = new Projectile[Limit];
    private static readonly Pending[] pending = new Pending[Limit];
    private static int depth;
    public static int Begin(Projectile projectile)
    {
        int token = depth++;
        if (token < Limit) initializing[token] = projectile;
        return token;
    }
    public static void End(int token)
    {
        if (token < 0) return;
        if (token < Limit) initializing[token] = null;
        depth = Math.Max(0, token);
    }
    public static bool IsInitializing(Projectile projectile)
    {
        // Overflow is extremely deep synchronous spawning; defer conservatively.
        if (depth > Limit) return true;
        for (int i = 0; i < depth; i++)
            if (ReferenceEquals(initializing[i], projectile)) return true;
        return false;
    }
    public static bool IsHeld(Projectile projectile)
    {
        for (int i = 0; i < pending.Length; i++)
            if (!ReferenceEquals(pending[i].Projectile, null) &&
                ReferenceEquals(pending[i].Projectile, projectile)) return true;
        return false;
    }
    public static bool Defer(Projectile projectile, Vector2 point, float radius)
    {
        for (int i = 0; i < pending.Length; i++)
        {
            if (!ReferenceEquals(pending[i].Projectile, null)) continue;
            pending[i] = new Pending { Projectile = projectile, Enabled = projectile.enabled,
                Simulated = projectile.rigidBody.simulated, Velocity = projectile.rigidBody.velocity,
                AngularVelocity = projectile.rigidBody.angularVelocity, Radius = radius };
            projectile.rigidBody.position = point;
            projectile.transform.position = new Vector3(point.x, point.y, projectile.transform.position.z);
            projectile.enabled = false;
            projectile.rigidBody.simulated = false;
            return true;
        }
        return false;
    }
    private static void Restore(Pending p)
    {
        if (p.Projectile == null) return;
        p.Projectile.enabled = p.Enabled;
        if (p.Projectile.rigidBody == null) return;
        p.Projectile.rigidBody.simulated = p.Simulated;
        p.Projectile.rigidBody.velocity = p.Velocity;
        p.Projectile.rigidBody.angularVelocity = p.AngularVelocity;
    }
    public static void Flush(Launcher launcher = null)
    {
        for (int i = 0; i < pending.Length; i++)
        {
            Pending p = pending[i];
            if (ReferenceEquals(p.Projectile, null) || IsInitializing(p.Projectile)) continue;
            if (launcher != null && p.Projectile != null &&
                !ReferenceEquals(p.Projectile.GetParentLauncher(), launcher)) continue;
            // Remove before callbacks/pooling can reuse this slot.
            pending[i] = default(Pending);
            Restore(p);
            if (p.Projectile == null || p.Projectile.IsDestroying() || p.Projectile.netRendered ||
                p.Projectile.rigidBody == null) continue;
            float at;
            // A tiny contact tolerance compensates only float roundoff on the
            // analytic circle edge; it is not additional gameplay field padding.
            CoreProjectileCapture.TrySweep(p.Projectile, p.Projectile.rigidBody.position,
                Vector2.zero, 0f, p.Radius + 0.0001f, out at);
        }
    }
    public static void Forget(Projectile projectile)
    {
        for (int i = 0; i < pending.Length; i++)
        {
            if (ReferenceEquals(pending[i].Projectile, null) ||
                !ReferenceEquals(pending[i].Projectile, projectile)) continue;
            Pending p = pending[i];
            pending[i] = default(Pending);
            Restore(p);
        }
    }
    public static void Reset()
    {
        for (int i = 0; i < pending.Length; i++)
        { Restore(pending[i]); pending[i] = default(Pending); }
        Array.Clear(initializing, 0, initializing.Length);
        depth = 0;
    }
}

[HarmonyPatch(typeof(Projectile), "Init")]
public static class CoreProjectileSpawnInitPatch
{
    public static void Prefix(Projectile __instance, out int __state)
    { __state = CoreProjectileSpawnGuard.Begin(__instance) + 1; }
    public static Exception Finalizer(Exception __exception, Projectile __instance, int __state)
    {
        CoreProjectileSpawnGuard.End(__state - 1);
        if (__exception != null) CoreProjectileSpawnGuard.Forget(__instance);
        return __exception;
    }
}
[HarmonyPatch(typeof(Launcher), "ShootProjectile")]
public static class CoreProjectileSpawnCompletePatch
{
    public static void Postfix(Launcher __instance) { CoreProjectileSpawnGuard.Flush(__instance); }
}
