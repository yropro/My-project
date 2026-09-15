using HarmonyLib;
using StarVortex;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

/// <summary>
/// Inserts barrier contacts into the existing native sweep, not an overlap-only
/// scan or a fake physics collider. The game's actual query supplies geometry;
/// earlier accepted native collisions retain priority. Fuzzy's distinct query
/// cadence/range is preserved and Mine/Captured keep their virtual overrides.
/// </summary>
public static class CoreProjectileSweep
{
    private const int ScopeLimit = 8;
    private const int NativeHitLimit = 1000; // installed PhysicsController buffer
    private const int CaptureAttemptsPerSweep = 64;
    private sealed class Scope
    {
        public Projectile Projectile;
        public bool Active, Queried, Stopped;
        public Vector2 Origin, Direction;
        public float Distance, Radius, Progress, CurrentHit;
        public int HitCount, Cursor, Attempts;
        public readonly RaycastHit2D[] Hits = new RaycastHit2D[NativeHitLimit];
    }
    private static readonly Scope[] scopes = CreateScopes();
    private static int depth;
    private delegate bool ReflectCall(Projectile p, GameObject target, Vector2 point);
    private static readonly ReflectCall reflect = (ReflectCall)Delegate.CreateDelegate(
        typeof(ReflectCall), AccessTools.DeclaredMethod(typeof(Projectile), "TryReflectOffShieldWard"));
    private static Scope[] CreateScopes()
    {
        var result = new Scope[ScopeLimit];
        for (int i = 0; i < result.Length; i++) result[i] = new Scope();
        return result;
    }
    private static Scope Current(Projectile p)
    {
        if (depth <= 0 || depth > ScopeLimit) return null;
        Scope s = scopes[depth - 1];
        return s.Active && ReferenceEquals(s.Projectile, p) ? s : null;
    }
    public static bool Begin(Projectile p, out int token)
    {
        token = depth++;
        if (token < ScopeLimit)
        {
            Scope s = scopes[token];
            s.Projectile = p;
            s.Active = p != null && !p.netRendered && !p.IsDestroying() &&
                !CoreProjectileCapture.IsHeld(p) && CoreProjectileCapture.HasFields;
            s.Queried = s.Stopped = false;
            s.HitCount = s.Cursor = s.Attempts = 0;
            s.Progress = 0f;
        }
        return !CoreProjectileCapture.IsHeld(p);
    }
    public static void End(int token, bool successful)
    {
        if (token < 0) return;
        try
        {
            if (token < ScopeLimit)
            {
                Scope s = scopes[token];
                if (successful && s.Active && s.Queried && !s.Stopped)
                    TryUntil(s, s.Distance);
                s.Projectile = null;
                s.Active = false;
                // Hit structs contain Collider references; release them between
                // sweeps rather than retaining destroyed scene objects in scratch.
                Array.Clear(s.Hits, 0, s.HitCount);
                s.HitCount = 0;
            }
        }
        finally { depth = Math.Max(0, token); }
    }

    public static RaycastHit2D[] Raycast(PhysicsController physics, Vector2 origin,
        Vector2 direction, float distance, Projectile projectile)
    {
        return Record(projectile, origin, direction, distance, 0f,
            physics.Raycast(origin, direction, distance));
    }
    public static RaycastHit2D[] CircleCast(PhysicsController physics, Vector2 origin,
        float radius, Vector2 direction, float distance, Projectile projectile)
    {
        return Record(projectile, origin, direction, distance, radius,
            physics.CircleCast(origin, radius, direction, distance));
    }
    private static RaycastHit2D[] Record(Projectile p, Vector2 origin, Vector2 direction,
        float distance, float radius, RaycastHit2D[] hits)
    {
        Scope s = Current(p);
        if (s == null || hits == null) return hits;
        if (hits.Length > NativeHitLimit)
            throw new InvalidOperationException("Native projectile hit buffer grew; re-audit sweep adapter.");
        s.Origin = origin;
        s.Direction = direction.sqrMagnitude > 0f ? direction.normalized : Vector2.zero;
        s.Distance = distance;
        s.Radius = radius;
        s.Progress = 0f;
        s.Cursor = s.HitCount = 0;
        s.Queried = true;
        // The native PhysicsController owns one shared query buffer. Nested
        // damage/capture callbacks may query again; this scope must own its copy.
        Array.Clear(s.Hits, 0, s.Hits.Length);
        for (int i = 0; i < hits.Length; i++)
        {
            if (!hits[i]) break;
            s.Hits[s.HitCount++] = hits[i];
        }
        return s.Hits;
    }

    public static bool BarrierOrReflect(Projectile projectile, GameObject target, Vector2 point)
    {
        Scope s = Current(projectile);
        if (s != null && s.Queried)
        {
            bool matched = false;
            for (int i = s.Cursor; i < s.HitCount; i++)
            {
                RaycastHit2D hit = s.Hits[i];
                if (hit.collider == null) continue;
                GameObject nativeTarget = hit.collider.gameObject;
                if (nativeTarget.CompareTag("Shield") && nativeTarget.transform.parent != null)
                    nativeTarget = nativeTarget.transform.parent.gameObject;
                if (nativeTarget != target || hit.point != point) continue;
                s.Cursor = i + 1;
                s.CurrentHit = Mathf.Clamp(hit.distance, s.Progress, s.Distance);
                matched = true;
                break;
            }
            if (!matched)
                throw new InvalidOperationException("Native collision point no longer matches its sweep.");
            if (TryUntil(s, s.CurrentHit)) return true;
        }
        bool result = reflect(projectile, target, point);
        if (s != null && result) s.Stopped = true;
        return result;
    }
    public static bool NativeHit(Projectile projectile, GameObject target, Vector2 point, Vector2? normal)
    {
        bool result = projectile.HitObject(target, point, normal);
        Scope s = Current(projectile);
        if (s != null)
        {
            s.Progress = s.CurrentHit;
            if (result) s.Stopped = true;
        }
        return result;
    }
    private static bool TryUntil(Scope s, float limit)
    {
        if (s.Stopped || s.Attempts++ >= CaptureAttemptsPerSweep || limit < s.Progress) return false;
        float at;
        if (!CoreProjectileCapture.TrySweep(s.Projectile,
                s.Origin + s.Direction * s.Progress, s.Direction,
                limit - s.Progress, s.Radius, out at)) return false;
        s.Stopped = true;
        return true;
    }
    public static bool CaptureStartingInside(Projectile p)
    {
        if (p == null || p.netRendered || p.IsDestroying() || p.rigidBody == null) return false;
        if (CoreProjectileCapture.IsHeld(p)) return true;
        if (!CoreProjectileCapture.HasFields) return false;
        float radius = Mathf.Max(0f, p.GetHitRange());
        if (radius <= 0f && p.circleCollider != null)
            radius = p.circleCollider.radius * Mathf.Max(Mathf.Abs(p.transform.lossyScale.x),
                Mathf.Abs(p.transform.lossyScale.y));
        float at;
        return CoreProjectileCapture.TrySweep(p, p.rigidBody.position, Vector2.zero, 0f, radius, out at);
    }

    internal static IEnumerable<CodeInstruction> Adapt(IEnumerable<CodeInstruction> instructions)
    {
        var code = new List<CodeInstruction>(instructions);
        int queries = 0, reflects = 0, hits = 0;
        for (int i = 0; i < code.Count; i++)
        {
            var method = code[i].operand as MethodInfo;
            if (method == null || (code[i].opcode != OpCodes.Call && code[i].opcode != OpCodes.Callvirt)) continue;
            if (method.DeclaringType == typeof(PhysicsController) &&
                (method.Name == "Raycast" || method.Name == "CircleCast"))
            {
                var owner = new CodeInstruction(OpCodes.Ldarg_0);
                owner.labels.AddRange(code[i].labels);
                code[i].labels.Clear();
                code.Insert(i++, owner);
                code[i].opcode = OpCodes.Call;
                code[i].operand = AccessTools.Method(typeof(CoreProjectileSweep), method.Name);
                queries++;
            }
            else if (method.DeclaringType == typeof(Projectile) && method.Name == "TryReflectOffShieldWard")
            {
                code[i].opcode = OpCodes.Call;
                code[i].operand = AccessTools.Method(typeof(CoreProjectileSweep), nameof(BarrierOrReflect));
                reflects++;
            }
            else if (method.Name == "HitObject" && typeof(Projectile).IsAssignableFrom(method.DeclaringType))
            {
                code[i].opcode = OpCodes.Call;
                code[i].operand = AccessTools.Method(typeof(CoreProjectileSweep), nameof(NativeHit));
                hits++;
            }
        }
        if (queries < 1 || queries > 2 || reflects != 1 || hits != 1)
            throw new InvalidOperationException("Installed projectile sweep control flow changed.");
        return code;
    }
}

[HarmonyPatch]
public static class CoreProjectileSweepPatch
{
    public static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.DeclaredMethod(typeof(Projectile), "UpdateCollision");
        yield return AccessTools.DeclaredMethod(typeof(FuzzyProjectile), "UpdateCollision");
    }
    public static bool Prefix(Projectile __instance, out int __state)
    {
        int token;
        bool run = CoreProjectileSweep.Begin(__instance, out token);
        __state = token + 1;
        return run;
    }
    public static void Postfix(ref int __state)
    {
        int token = __state - 1;
        __state = 0;
        CoreProjectileSweep.End(token, true);
    }
    public static Exception Finalizer(Exception __exception, int __state)
    {
        CoreProjectileSweep.End(__state - 1, false);
        return __exception;
    }
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    { return CoreProjectileSweep.Adapt(instructions); }
}

[HarmonyPatch(typeof(Projectile), "FixedUpdate")]
public static class CoreProjectileInsideFieldPatch
{
    public static bool Prefix(Projectile __instance)
    { return !CoreProjectileSweep.CaptureStartingInside(__instance); }
}
