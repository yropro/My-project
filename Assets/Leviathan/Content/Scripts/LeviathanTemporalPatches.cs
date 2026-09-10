using HarmonyLib;
using StarVortex;
using UnityEngine;

/// <summary>
/// The four patches that turn LeviathanTemporalField from "enemies move slowly"
/// into something that reads as time dilation.
///
/// Every patch opens with LeviathanTemporalField.AnyActive, so with no dive
/// running the whole file costs one static bool read per call site.
///
/// Ordering note: all of these read the registry directly rather than depending
/// on the driver having run first. A one-physics-step lag on the smoothed ship
/// factor is invisible and buys immunity from Unity's undefined script order.
/// </summary>
public static class LeviathanTemporalPatches
{
    // =========================================================================
    // 1. AI COGNITION
    // =========================================================================

    /// <summary>
    /// AIShip.deltaTime (public field, AIShip.cs:1770) is a per-ship time budget
    /// that AIController accumulates and consumes when it runs gambits. It
    /// drives dodge timers, boost cooldown, timeSinceLastFire, attack phase,
    /// path raycast cadence, aim lerp rate and target velocity estimation.
    ///
    /// Scaling it is what makes a slowed enemy think slowly rather than just
    /// drift slowly. Without this the ship glides at 0.35x while tracking your
    /// dodges perfectly, which looks wrong immediately.
    ///
    /// Patching the base is sufficient: every subclass override calls
    /// base.RunGambits() FIRST and only then reads this.deltaTime (see
    /// BasicAIShip.cs:19-22, RescueAIShip.cs:17-18), so the prefix lands before
    /// any consumer in the chain.
    /// </summary>
    [HarmonyPatch(typeof(AIShip), "RunGambits")]
    public static class AIShipRunGambitsPatch
    {
        public static void Prefix(AIShip __instance)
        {
            if (!LeviathanTemporalField.AnyActive)
            {
                return;
            }

            GameShip ship = __instance.GetGameShip();
            if (!ship)
            {
                return;
            }

            float factor = LeviathanTemporalField.GetShipFactor(ship);
            if (factor < 1f)
            {
                __instance.deltaTime *= factor;
            }
        }
    }

    // =========================================================================
    // 2. WEAPON RELOAD
    // =========================================================================

    /// <summary>
    /// Launcher.UpdateReload (Launcher.cs:1367) is three lines:
    ///
    ///     if (!IsReloading()) return;
    ///     reloadTimer -= Time.deltaTime;
    ///     reloadTimer = Mathf.Max(0f, reloadTimer);
    ///
    /// The prefix records the value, the postfix rewrites it as a scaled
    /// decrement from that recorded value. Recomputing from `before` rather
    /// than adding time back matters: the native line clamps at zero, and
    /// adding to a clamped zero would un-finish a reload that had already
    /// completed and confuse IsReloading().
    ///
    /// reloadTimer is protected, hence the FieldRef.
    /// </summary>
    [HarmonyPatch(typeof(Launcher), "UpdateReload")]
    public static class LauncherUpdateReloadPatch
    {
        private static readonly AccessTools.FieldRef<Launcher, float> ReloadTimer =
            AccessTools.FieldRefAccess<Launcher, float>("reloadTimer");

        public static void Prefix(Launcher __instance, out float __state)
        {
            __state = ReloadTimer(__instance);
        }

        public static void Postfix(Launcher __instance, float __state)
        {
            if (!LeviathanTemporalField.AnyActive || __state <= 0f)
            {
                return;
            }

            GameShip ship = __instance.parentShip;
            if (!ship || ship.IsNetRemote())
            {
                return;
            }

            float factor = LeviathanTemporalField.GetShipFactor(ship);
            if (factor >= 1f)
            {
                return;
            }

            ReloadTimer(__instance) = Mathf.Max(0f, __state - Time.deltaTime * factor);
        }
    }

    // =========================================================================
    // 3. PROJECTILE SPEED
    // =========================================================================

    /// <summary>
    /// Projectile motion is rigidbody-driven, and Projectile.UpdateCollision
    /// derives its cast length from velocity.magnitude * Time.deltaTime
    /// (Projectile.cs:474), so scaling velocity keeps collision self-consistent
    /// with no tunnelling.
    ///
    /// Velocity is absolute state, so the applied factor is stored per
    /// projectile and each change is applied as a RATIO. Without that, a
    /// projectile sitting in the field would have its velocity multiplied by
    /// 0.35 every physics step and stop dead within a second.
    ///
    /// netRendered projectiles are the remote render of a projectile simulated
    /// elsewhere. Touching them would fight the owning machine and desync the
    /// trajectory, so they are skipped outright. This is the same gate
    /// Projectile.FixedUpdate uses on itself.
    ///
    /// LIMIT: this catches straight-flying projectiles and anything that leaves
    /// velocity alone after spawn. Missile.FixedUpdate calls base first but then
    /// runs UpdateVelocity(), which recomputes velocity from its own thrust and
    /// overwrites the ratio. Slowing missiles properly means patching
    /// Missile.UpdateVelocity and the homing turn rate as a follow-up.
    /// </summary>
    [HarmonyPatch(typeof(Projectile), "FixedUpdate")]
    public static class ProjectileFixedUpdatePatch
    {
        public static void Prefix(Projectile __instance)
        {
            if (!LeviathanTemporalField.AnyActive || __instance.netRendered)
            {
                return;
            }

            LeviathanTemporalProjectileState state =
                LeviathanTemporalProjectileState.For(__instance);

            if (state == null || state.Body == null)
            {
                return;
            }

            float target = LeviathanTemporalField.GetFactor(
                __instance.transform.position,
                __instance.GetParentShip());

            if (target == state.Applied)
            {
                return;
            }

            state.Body.velocity *= target / state.Applied;
            state.Applied = target;
        }
    }

    /// <summary>
    /// Projectiles are pooled. ResetObject runs on reuse and clears netId and
    /// netRendered, so it is also where a stale temporal factor has to be
    /// cleared. Missing this would make a recycled projectile inherit the
    /// previous one's factor and immediately mis-scale its velocity.
    /// </summary>
    [HarmonyPatch(typeof(Projectile), "ResetObject")]
    public static class ProjectileResetObjectPatch
    {
        public static void Postfix(Projectile __instance)
        {
            LeviathanTemporalProjectileState state =
                __instance.GetComponent<LeviathanTemporalProjectileState>();

            if (state)
            {
                state.Applied = 1f;
            }
        }
    }

    // =========================================================================
    // 4. PROJECTILE LIFETIME
    // =========================================================================

    /// <summary>
    /// A projectile slowed to 0.35x that still expires on the normal clock
    /// covers a third of its usual distance and vanishes mid-flight. That reads
    /// as broken, not slow.
    ///
    /// AutoDestroy.Update (AutoDestroy.cs:68) does timeRemaining -= Time.deltaTime
    /// on a public field, and Projectile does not override Update, so this one
    /// patch covers every projectile, mine and timed object.
    ///
    /// Scoped to Projectile deliberately. AutoDestroy also backs explosions and
    /// one-shot VFX; stretching those is a taste call, and leaving them alone
    /// keeps the patch honest about what it affects.
    /// </summary>
    [HarmonyPatch(typeof(AutoDestroy), "Update")]
    public static class AutoDestroyUpdatePatch
    {
        public static void Prefix(AutoDestroy __instance, out float __state)
        {
            __state = __instance.timeRemaining;
        }

        public static void Postfix(AutoDestroy __instance, float __state)
        {
            if (!LeviathanTemporalField.AnyActive || __state <= 0f)
            {
                return;
            }

            Projectile projectile = __instance as Projectile;
            if (projectile == null || projectile.netRendered)
            {
                return;
            }

            float factor = LeviathanTemporalField.GetFactor(
                projectile.transform.position,
                projectile.GetParentShip());

            if (factor >= 1f)
            {
                return;
            }

            // Update() may have already fired TimedDestroy on the native
            // decrement. Only extend a projectile still alive.
            if (__instance.timeRemaining <= 0f && __state - Time.deltaTime * factor > 0f)
            {
                __instance.timeRemaining = __state - Time.deltaTime * factor;
                return;
            }

            __instance.timeRemaining = Mathf.Max(0f, __state - Time.deltaTime * factor);
        }
    }
}

/// <summary>
/// Per-projectile record of the factor currently baked into its velocity.
///
/// A component rather than a dictionary because projectiles are pooled: the
/// GameObject survives recycling, so the component is allocated once per pool
/// slot and never again, and there is no keyed lookup on the hot path.
/// </summary>
public class LeviathanTemporalProjectileState : MonoBehaviour
{
    public float Applied = 1f;
    public Rigidbody2D Body;

    public static LeviathanTemporalProjectileState For(Projectile projectile)
    {
        LeviathanTemporalProjectileState state =
            projectile.GetComponent<LeviathanTemporalProjectileState>();

        if (state == null)
        {
            Rigidbody2D body = projectile.GetComponent<Rigidbody2D>();
            if (body == null)
            {
                return null;
            }

            state = projectile.gameObject.AddComponent<LeviathanTemporalProjectileState>();
            state.Body = body;
            state.Applied = 1f;
        }

        return state;
    }
}
