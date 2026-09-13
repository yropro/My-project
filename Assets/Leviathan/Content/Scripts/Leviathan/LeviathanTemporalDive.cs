using HarmonyLib;
using StarVortex;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

/// <summary>
/// Localized Temporal Drive replacement with distance falloff.
///
/// One field registry answers "what time factor applies here?". Native object
/// families then consume that answer through their own verified seams:
/// GameShip speedScale, AI budget, native timers, projectile motion/subclass
/// clocks, and beam fixed-time damage cadence.
///
/// Co-op rule: only locally simulated gameplay objects are modified. Remote
/// GameShips (IsNetRemote) and remote-rendered projectiles (netRendered) are
/// observers of their owner's already-slowed authoritative state.
/// </summary>
public static class LeviathanTemporalDive
{
    public static class Tuning
    {
        // Test convenience requested for the current iteration. Set false when
        // the keystone should be the only activation gate.
        public static bool ForceTransformPlayerTemporalDriveForTesting = true;

        // Testing-only short cooldown. Set false alongside the force-transform
        // switch for balance testing/release behavior.
        public static bool ShortCooldownForTesting = true;
        public const float TestingCooldownSeconds = 2f;

        public const float NetworkMarkerTimeScale = 1.0f;
        public const float WorldUnitsPerMeter = 1f / 20f;
        public const float MinimumFactor = 0.05f;
        public const float RestoreThreshold = 0.9995f;
        public const float MaximumNetworkDurationSeconds = 30f;
    }

    private sealed class Dive
    {
        public int OwnerPlayerId = -1;
        public GameShip Owner;
        public float Remaining;
        public float InnerRadius;
        public float OuterRadius;
        public float OuterRadiusSqr;
        public float EnemyFloor;
        public float AllyFloor;
        public float Exponent;
    }

    private struct ShipState
    {
        public float Factor;
    }

    private sealed class LocalClock
    {
        public float Global;
        public float Local;
    }

    private static readonly List<Dive> dives = new List<Dive>(4);
    private static readonly Dictionary<GameShip, ShipState> shipStates =
        new Dictionary<GameShip, ShipState>(128);
    private static readonly List<GameShip> shipScratch = new List<GameShip>(32);
    private static readonly Dictionary<object, LocalClock> fixedClocks =
        new Dictionary<object, LocalClock>(48);

    private static NetWorldBridge bridge;
    private static TemporalDrive activatingDrive;

    private static readonly AccessTools.FieldRef<Beam, GameShip> BeamParentShip =
        AccessTools.FieldRefAccess<Beam, GameShip>("parentShip");
    private static readonly AccessTools.FieldRef<BlackHole, GameShip> BlackHoleParentShip =
        AccessTools.FieldRefAccess<BlackHole, GameShip>("parentShip");
    private static readonly AccessTools.FieldRef<Projectile, string> ProjectileFaction =
        AccessTools.FieldRefAccess<Projectile, string>("cachedFaction");

    public static bool AnyActive { get { return dives.Count > 0; } }
    public static int ActiveDiveCount { get { return dives.Count; } }

    // ---------------------------------------------------------------------
    // Activation / native Temporal Drive carrier
    // ---------------------------------------------------------------------

    public static bool ShouldTransform(TemporalDrive drive)
    {
        if (drive == null || !drive.parentShip || WorldController.instance == null)
            return false;

        GameShip local = WorldController.instance.GetCurrentPlayerShip();
        if (!local || !ReferenceEquals(local, drive.parentShip))
            return false;

        if (Tuning.ForceTransformPlayerTemporalDriveForTesting)
            return true;

        LeviathanBehemoth.ResolvedState state = LeviathanBehemoth.GetResolvedState(local);
        return state != null && state.Active && state.TemporalDive;
    }

    public static bool BeginActivation(TemporalDrive drive)
    {
        if (!ShouldTransform(drive))
            return false;

        activatingDrive = drive;
        return true;
    }

    public static void EndActivation(TemporalDrive drive)
    {
        if (ReferenceEquals(activatingDrive, drive))
            activatingDrive = null;
    }

    public static void RewriteNetworkRequest(ref float timeScale, ref float duration)
    {
        TemporalDrive drive = activatingDrive;
        if (drive == null || !ShouldTransform(drive))
            return;

        timeScale = Tuning.NetworkMarkerTimeScale;
        duration = Mathf.Clamp(drive.Duration, 0.05f, Tuning.MaximumNetworkDurationSeconds);
    }

    public static bool TryHandleNetworkActivation(NetWorldBridge sourceBridge, MsgTemporalActivate msg)
    {
        if (sourceBridge == null || msg == null)
            return false;

        bridge = sourceBridge;

        bool marker = !msg.cancel && msg.duration > 0f &&
            Mathf.Approximately(msg.timeScale, Tuning.NetworkMarkerTimeScale);

        if (marker)
        {
            Activate(
                msg.ownerPlayerId,
                sourceBridge.ResolvePlayerShip(msg.ownerPlayerId),
                Mathf.Clamp(msg.duration, 0.05f, Tuning.MaximumNetworkDurationSeconds)
            );
            return true;
        }

        if (msg.cancel && Remove(msg.ownerPlayerId, null))
            return true;

        return false;
    }

    public static void HandleSinglePlayerStart(TemporalDrive drive)
    {
        if (drive == null || !ShouldTransform(drive))
            return;

        // AddEffect runs with a neutral native timeScale for transformed drives.
        // Remove its carrier status and replace it with the local field.
        drive.parentShip.RemoveStatusEffect(StatusEffect.Type.TimeWarped);
        Activate(-1, drive.parentShip, Mathf.Max(0.05f, drive.Duration));
    }

    public static void HandleSinglePlayerStop(TemporalDrive drive)
    {
        if (drive != null && !NetSession.InSession && ShouldTransform(drive))
            Remove(-1, drive.parentShip);
    }

    public static float TransformDuration(TemporalDrive drive, float nativeResolved)
    {
        if (!ShouldTransform(drive))
            return nativeResolved;

        float ratio = nativeResolved / Mathf.Max(0.001f, drive.BaseDuration);
        LeviathanBehemoth.ResolvedState state = LeviathanBehemoth.GetResolvedState(drive.parentShip);
        float baseline = state != null && state.Active
            ? state.TemporalDiveDurationSeconds
            : LeviathanBehemoth.Tuning.TemporalDiveDurationSeconds;

        return Mathf.Clamp(baseline * ratio, 0.05f, Tuning.MaximumNetworkDurationSeconds);
    }

    public static float TransformCooldown(TemporalDrive drive, float nativeResolved)
    {
        if (!ShouldTransform(drive))
            return nativeResolved;

        float ratio = nativeResolved / Mathf.Max(0.001f, drive.BaseCooldown);
        LeviathanBehemoth.ResolvedState state = LeviathanBehemoth.GetResolvedState(drive.parentShip);
        float baseline = state != null && state.Active
            ? state.TemporalDiveCooldownSeconds
            : LeviathanBehemoth.Tuning.TemporalDiveCooldownSeconds;

        if (Tuning.ShortCooldownForTesting)
            baseline = Tuning.TestingCooldownSeconds;

        return Mathf.Max(0f, baseline * ratio);
    }

    private static void Activate(int ownerPlayerId, GameShip owner, float duration)
    {
        Dive dive = Find(ownerPlayerId, owner);
        if (dive == null)
        {
            dive = new Dive();
            dives.Add(dive);
        }

        dive.OwnerPlayerId = ownerPlayerId;
        dive.Owner = owner;
        dive.Remaining = duration;
        ResolveShape(owner, dive);
    }

    private static void ResolveShape(GameShip owner, Dive dive)
    {
        LeviathanBehemoth.ResolvedState state = owner
            ? LeviathanBehemoth.GetResolvedState(owner)
            : null;

        float innerMeters = state != null && state.Active
            ? state.TemporalDiveInnerRadiusMeters
            : LeviathanBehemoth.Tuning.TemporalDiveInnerRadiusMeters;
        float outerMeters = state != null && state.Active
            ? state.TemporalDiveOuterRadiusMeters
            : LeviathanBehemoth.Tuning.TemporalDiveOuterRadiusMeters;

        dive.InnerRadius = Mathf.Max(0f, innerMeters * Tuning.WorldUnitsPerMeter);
        dive.OuterRadius = Mathf.Max(
            dive.InnerRadius + 0.001f,
            outerMeters * Tuning.WorldUnitsPerMeter
        );
        dive.OuterRadiusSqr = dive.OuterRadius * dive.OuterRadius;
        dive.EnemyFloor = Mathf.Clamp(
            state != null && state.Active
                ? state.TemporalDiveEnemyMinimumFactor
                : LeviathanBehemoth.Tuning.TemporalDiveEnemyMinimumFactor,
            Tuning.MinimumFactor,
            1f
        );
        dive.AllyFloor = Mathf.Clamp(
            state != null && state.Active
                ? state.TemporalDiveAllyMinimumFactor
                : LeviathanBehemoth.Tuning.TemporalDiveAllyMinimumFactor,
            Tuning.MinimumFactor,
            1f
        );
        dive.Exponent = Mathf.Max(
            0.01f,
            state != null && state.Active
                ? state.TemporalDiveFalloffExponent
                : LeviathanBehemoth.Tuning.TemporalDiveFalloffExponent
        );
    }

    public static void TickNetwork(NetWorldBridge sourceBridge, float dt)
    {
        if (sourceBridge == null)
            return;

        if (bridge == null)
            bridge = sourceBridge;
        if (!ReferenceEquals(bridge, sourceBridge))
            return;

        Tick(dt, sourceBridge);
    }

    public static void TickSinglePlayer(float dt)
    {
        if (!NetSession.InSession)
            Tick(dt, null);
    }

    private static void Tick(float dt, NetWorldBridge sourceBridge)
    {
        for (int i = dives.Count - 1; i >= 0; i--)
        {
            Dive dive = dives[i];

            // Native TemporaryEffect duration advances in the activating ship's
            // local time. The activator is exempt from its own Dive, but another
            // player's overlapping Dive may slow it. Tick the replicated field
            // by that same derived owner factor so the field and the native
            // TemporalDrive cancel stay aligned on every peer.
            float ownerTimeFactor = dive.Owner ? GetShipFactor(dive.Owner) : 1f;
            dive.Remaining -= Mathf.Max(0f, dt) * ownerTimeFactor;

            if (sourceBridge != null && dive.OwnerPlayerId >= 0)
            {
                GameShip resolved = sourceBridge.ResolvePlayerShip(dive.OwnerPlayerId);
                if (resolved && !ReferenceEquals(resolved, dive.Owner))
                {
                    dive.Owner = resolved;
                    ResolveShape(resolved, dive);
                }
            }

            if (dive.Remaining <= 0f || (dive.Owner && dive.Owner.health <= 0f))
                dives.RemoveAt(i);
        }
    }

    private static Dive Find(int playerId, GameShip owner)
    {
        for (int i = 0; i < dives.Count; i++)
        {
            if (playerId >= 0 && dives[i].OwnerPlayerId == playerId)
                return dives[i];
            if (playerId < 0 && owner && ReferenceEquals(dives[i].Owner, owner))
                return dives[i];
        }
        return null;
    }

    private static bool Remove(int playerId, GameShip owner)
    {
        bool removed = false;
        for (int i = dives.Count - 1; i >= 0; i--)
        {
            bool match = playerId >= 0
                ? dives[i].OwnerPlayerId == playerId
                : owner && ReferenceEquals(dives[i].Owner, owner);
            if (match)
            {
                dives.RemoveAt(i);
                removed = true;
            }
        }
        return removed;
    }

    // ---------------------------------------------------------------------
    // Field math
    // ---------------------------------------------------------------------

    public static float GetShipFactor(GameShip ship)
    {
        if (!ship || dives.Count == 0)
            return 1f;

        GameShip logicalOwner = GetLogicalOwner(ship);
        float result = 1f;

        for (int i = 0; i < dives.Count; i++)
        {
            Dive dive = dives[i];
            if (!dive.Owner)
                continue;

            // A Leviathan's primary Head and all published anatomy sections are
            // body-exempt from its own Dive, matching "all but your ship".
            if (logicalOwner && ReferenceEquals(logicalOwner, dive.Owner))
                continue;

            result = Mathf.Min(result, Evaluate(dive, ship.transform.position, logicalOwner));
        }

        return Mathf.Clamp(result, Tuning.MinimumFactor, 1f);
    }

    public static float GetEffectFactor(Vector2 position, GameShip sourceShip)
    {
        if (!sourceShip || dives.Count == 0)
            return 1f;

        GameShip logicalOwner = GetLogicalOwner(sourceShip);
        float result = 1f;
        for (int i = 0; i < dives.Count; i++)
        {
            Dive dive = dives[i];
            if (dive.Owner)
                result = Mathf.Min(result, Evaluate(dive, position, logicalOwner));
        }
        return Mathf.Clamp(result, Tuning.MinimumFactor, 1f);
    }

    private static float Evaluate(Dive dive, Vector2 position, GameShip subjectOwner)
    {
        if (!subjectOwner)
            return 1f;

        return EvaluateFaction(dive, position, subjectOwner.faction);
    }

    private static float EvaluateFaction(Dive dive, Vector2 position, string faction)
    {
        if (string.IsNullOrEmpty(faction))
            return 1f;

        Vector2 delta = position - (Vector2)dive.Owner.transform.position;
        float distanceSqr = delta.sqrMagnitude;
        if (distanceSqr >= dive.OuterRadiusSqr)
            return 1f;

        bool hostile = Faction.IsHostile(dive.Owner.faction, faction);
        float floor = hostile ? dive.EnemyFloor : dive.AllyFloor;

        float innerSqr = dive.InnerRadius * dive.InnerRadius;
        if (distanceSqr <= innerSqr)
            return floor;

        float distance = Mathf.Sqrt(distanceSqr);
        float t = Mathf.Clamp01(
            (distance - dive.InnerRadius) / (dive.OuterRadius - dive.InnerRadius)
        );
        t = t * t * (3f - 2f * t); // SmoothStep.
        if (!Mathf.Approximately(dive.Exponent, 1f))
            t = Mathf.Pow(t, dive.Exponent);

        return Mathf.Lerp(floor, 1f, t);
    }

    private static GameShip GetLogicalOwner(GameShip ship)
    {
        if (!ship)
            return ship;

        GameShip owner;
        if (LeviathanGrowth.TryGetAnatomyOwner(ship, out owner) && owner)
            return owner;
        return ship;
    }

    // ---------------------------------------------------------------------
    // Persistent ship-body factor (status-like behavior)
    // ---------------------------------------------------------------------

    public static void StepShips()
    {
        if (WorldController.instance == null || (dives.Count == 0 && shipStates.Count == 0))
            return;

        List<GameShip> ships = WorldController.instance.GetGameShips();
        if (ships != null)
        {
            for (int i = 0; i < ships.Count; i++)
            {
                GameShip ship = ships[i];
                if (!ship || ship.isBeingDestroyed || ship.IsNetRemote())
                    continue;

                ApplyShipFactor(ship, dives.Count == 0 ? 1f : GetShipFactor(ship));
            }
        }

        PruneShipStates();
    }

    private static void ApplyShipFactor(GameShip ship, float target)
    {
        target = Mathf.Clamp(target, Tuning.MinimumFactor, 1f);
        // Never discard tracking while leaving a small residual slowdown behind.
        if (target >= Tuning.RestoreThreshold)
            target = 1f;

        ShipState state;
        bool tracked = shipStates.TryGetValue(ship, out state);
        float old = tracked ? Mathf.Max(Tuning.MinimumFactor, state.Factor) : 1f;
        if (Mathf.Abs(target - old) < 0.0001f)
            return;

        Rigidbody2D body = ship.GetRigidBody();
        Vector2 before = body ? body.velocity : Vector2.zero;

        // speedScale already contains our old factor. Divide it out so native or
        // other-mod speed-scale state composes instead of being overwritten.
        float baseSpeedScale = ship.speedScale / old;
        float nextSpeedScale = Mathf.Max(Tuning.MinimumFactor, baseSpeedScale * target);
        ship.SetTimeScale(1f / nextSpeedScale);

        // SetTimeScale multiplies velocity by the new absolute speedScale; local
        // field transitions need the new/old temporal ratio instead.
        if (body)
            body.velocity = before * (target / old);

        if (target >= Tuning.RestoreThreshold)
            shipStates.Remove(ship);
        else
        {
            state.Factor = target;
            shipStates[ship] = state;
        }
    }

    private static void PruneShipStates()
    {
        if (shipStates.Count == 0)
            return;

        shipScratch.Clear();
        foreach (KeyValuePair<GameShip, ShipState> pair in shipStates)
        {
            if (!pair.Key || pair.Key.isBeingDestroyed || pair.Key.health <= 0f)
                shipScratch.Add(pair.Key);
        }
        for (int i = 0; i < shipScratch.Count; i++)
            shipStates.Remove(shipScratch[i]);
        shipScratch.Clear();
    }

    private static void RestoreShips()
    {
        if (shipStates.Count == 0)
            return;

        shipScratch.Clear();
        foreach (KeyValuePair<GameShip, ShipState> pair in shipStates)
            shipScratch.Add(pair.Key);
        for (int i = 0; i < shipScratch.Count; i++)
        {
            GameShip ship = shipScratch[i];
            if (ship && !ship.isBeingDestroyed)
                ApplyShipFactor(ship, 1f);
        }
        shipScratch.Clear();
        shipStates.Clear();
    }

    // ---------------------------------------------------------------------
    // Shared native-time adapters
    // ---------------------------------------------------------------------

    public static float ScaleDeltaTime(float dt, object instance)
    {
        if (instance == null || dives.Count == 0)
            return dt;

        GameShip ship = instance as GameShip;
        if (ship)
            return ship.IsNetRemote() ? dt : dt * GetShipFactor(ship);

        Projectile projectile = instance as Projectile;
        if (projectile)
            return dt * GetProjectileFactor(projectile);

        Beam beamInstance = instance as Beam;
        if (beamInstance != null)
        {
            GameShip source = BeamParentShip(beamInstance);
            return !source || source.IsNetRemote() ? dt : dt * GetShipFactor(source);
        }

        BlackHole blackHole = instance as BlackHole;
        if (blackHole != null)
        {
            GameShip source = BlackHoleParentShip(blackHole);
            return !source ? dt : dt * GetEffectFactor(blackHole.transform.position, source);
        }

        Equippable equippable = instance as Equippable;
        if (equippable != null && equippable.parentShip)
            return equippable.parentShip.IsNetRemote()
                ? dt
                : dt * GetShipFactor(equippable.parentShip);

        Damageable damageable = instance as Damageable;
        GameShip damageableShip = damageable as GameShip;
        return damageableShip && !damageableShip.IsNetRemote()
            ? dt * GetShipFactor(damageableShip)
            : dt;
    }

    public static float ScaleStatusDeltaTime(float dt, GameShip ship)
    {
        return !ship || ship.IsNetRemote() || dives.Count == 0
            ? dt
            : dt * GetShipFactor(ship);
    }

    public static float ScaleAIBudget(AIShip ai, float dt)
    {
        GameShip ship = ai == null ? null : ai.GetGameShip();
        return !ship || ship.IsNetRemote() || dives.Count == 0
            ? dt
            : dt * GetShipFactor(ship);
    }

    // ---------------------------------------------------------------------
    // Projectiles
    // ---------------------------------------------------------------------

    public static void ApplyProjectileVelocity(Projectile projectile)
    {
        if (!projectile || projectile.netRendered)
            return;

        LeviathanTemporalProjectileState state =
            projectile.GetComponent<LeviathanTemporalProjectileState>();

        if (state == null)
        {
            if (dives.Count == 0)
                return;
            state = projectile.gameObject.AddComponent<LeviathanTemporalProjectileState>();
        }

        if (!state.Body)
            return;

        float target = GetProjectileFactor(projectile);
        float old = Mathf.Clamp(state.Factor, Tuning.MinimumFactor, 1f);
        target = Mathf.Clamp(target, Tuning.MinimumFactor, 1f);

        if (Mathf.Abs(target - old) > 0.0001f)
        {
            state.Body.velocity *= target / old;
            state.Factor = target;
        }
    }

    public static void ResetProjectile(Projectile projectile)
    {
        if (!projectile)
            return;
        LeviathanTemporalProjectileState state =
            projectile.GetComponent<LeviathanTemporalProjectileState>();
        if (state != null)
        {
            state.Factor = 1f;
            state.FuzzyStartFactor = 1f;
        }
    }

    public static void ClampMissile(Missile missile)
    {
        if (!missile || missile.netRendered || dives.Count == 0)
            return;

        LeviathanTemporalProjectileState state =
            missile.GetComponent<LeviathanTemporalProjectileState>();
        if (state == null || !state.Body)
            return;

        float factor = GetProjectileFactor(missile);
        float max = Mathf.Max(0f, missile.MaxVelocity * factor);
        if (state.Body.velocity.sqrMagnitude > max * max)
            state.Body.velocity = Vector2.ClampMagnitude(state.Body.velocity, max);
    }

    public static float GetProjectileFactor(Projectile projectile)
    {
        if (!projectile || projectile.netRendered || dives.Count == 0)
            return 1f;

        GameShip source = GetLogicalOwner(projectile.GetParentShip());
        // Native Init and reflection refresh this cache, including pooled shots.
        // It remains valid when the firing ship's Unity object is destroyed.
        string faction = source ? source.faction : ProjectileFaction(projectile);
        float result = 1f;
        for (int i = 0; i < dives.Count; i++)
        {
            Dive dive = dives[i];
            if (dive.Owner)
                result = Mathf.Min(result, EvaluateFaction(dive, projectile.transform.position, faction));
        }
        return Mathf.Clamp(result, Tuning.MinimumFactor, 1f);
    }

    public static float ScaleMissileAccelerationDelta(float dt, object instance)
    {
        float factor = GetProjectileFactor(instance as Projectile);
        // World velocity already includes one factor; its local acceleration
        // needs another for the slowed clock (dv/dt = a * factor squared).
        return dt * factor * factor;
    }

    // ---------------------------------------------------------------------
    // Beam/Laser local fixed-time clock
    // ---------------------------------------------------------------------

    public static float ScaleFixedTime(float globalTime, object instance)
    {
        if (instance == null)
            return globalTime;

        float factor = 1f;
        Beam beamInstance = instance as Beam;
        if (beamInstance != null)
        {
            GameShip source = BeamParentShip(beamInstance);
            if (!source || source.IsNetRemote())
                return globalTime;
            factor = dives.Count == 0 ? 1f : GetShipFactor(source);
        }
        else
        {
            LaserSpinner spinner = instance as LaserSpinner;
            if (spinner == null || !spinner.parentShip || spinner.parentShip.IsNetRemote())
                return globalTime;
            factor = dives.Count == 0 ? 1f : GetShipFactor(spinner.parentShip);
        }

        LocalClock clock;
        if (!fixedClocks.TryGetValue(instance, out clock))
        {
            if (factor >= Tuning.RestoreThreshold)
                return globalTime;
            clock = new LocalClock { Global = globalTime, Local = globalTime };
            fixedClocks[instance] = clock;
            return globalTime;
        }

        float elapsed = globalTime - clock.Global;
        if (elapsed < 0f || elapsed > 1f)
        {
            clock.Global = globalTime;
            clock.Local = globalTime;
        }
        else if (elapsed > 0f)
        {
            clock.Local += elapsed * Mathf.Clamp(factor, Tuning.MinimumFactor, 1f);
            clock.Global = globalTime;
        }
        return clock.Local;
    }

    public static void ForgetClock(object instance)
    {
        if (instance != null)
            fixedClocks.Remove(instance);
    }

    public static void Reset()
    {
        dives.Clear();
        bridge = null;
        activatingDrive = null;
        RestoreShips();
        fixedClocks.Clear();
    }
}

public sealed class LeviathanTemporalProjectileState : MonoBehaviour
{
    public float Factor = 1f;
    public float FuzzyStartFactor = 1f;
    public Rigidbody2D Body;
    private void Awake() { Body = GetComponent<Rigidbody2D>(); }
}

// =============================================================================
// Activation, lifetime, cleanup
// =============================================================================

[HarmonyPatch(typeof(TemporalDrive), "AddEffect")]
public static class LeviathanTemporalDiveAddEffectPatch
{
    public struct ActivationState
    {
        public bool Transformed;
        public float NativeTimeScale;
    }

    public static void Prefix(TemporalDrive __instance, out ActivationState __state)
    {
        __state = new ActivationState
        {
            Transformed = LeviathanTemporalDive.BeginActivation(__instance),
            NativeTimeScale = __instance.timeScale
        };
        // Applying then removing vanilla TimeWarped does not undo its velocity
        // multiplication. Prevent that multiplication in the first place.
        if (__state.Transformed && !NetSession.InSession)
            __instance.timeScale = 1f;
    }

    public static void Postfix(TemporalDrive __instance, ActivationState __state)
    {
        if (__state.Transformed && !NetSession.InSession)
            LeviathanTemporalDive.HandleSinglePlayerStart(__instance);
    }

    public static void Finalizer(TemporalDrive __instance, ActivationState __state)
    {
        if (!__state.Transformed)
            return;
        __instance.timeScale = __state.NativeTimeScale;
        LeviathanTemporalDive.EndActivation(__instance);
    }
}

[HarmonyPatch(typeof(TemporalDrive), "RemoveEffect")]
public static class LeviathanTemporalDiveRemoveEffectPatch
{
    public static void Postfix(TemporalDrive __instance)
    {
        LeviathanTemporalDive.HandleSinglePlayerStop(__instance);
    }
}

[HarmonyPatch(typeof(NetSession), "RequestTemporalDilation")]
public static class LeviathanTemporalDiveRequestPatch
{
    public static void Prefix(ref float timeScale, ref float duration)
    {
        LeviathanTemporalDive.RewriteNetworkRequest(ref timeScale, ref duration);
    }
}

[HarmonyPatch(typeof(NetWorldBridge), "OnTemporalActivate")]
public static class LeviathanTemporalDiveNetworkPatch
{
    public static bool Prefix(NetWorldBridge __instance, MsgTemporalActivate temporal)
    {
        return !LeviathanTemporalDive.TryHandleNetworkActivation(__instance, temporal);
    }
}

[HarmonyPatch(typeof(NetWorldBridge), "Update")]
public static class LeviathanTemporalDiveNetTickPatch
{
    public static void Postfix(NetWorldBridge __instance, float unscaledDeltaTime)
    {
        LeviathanTemporalDive.TickNetwork(__instance, Time.deltaTime);
    }
}

[HarmonyPatch(typeof(WorldController), "Update")]
public static class LeviathanTemporalDiveSingleTickPatch
{
    public static void Postfix() { LeviathanTemporalDive.TickSinglePlayer(Time.deltaTime); }
}

[HarmonyPatch(typeof(WorldController), "FixedUpdate")]
public static class LeviathanTemporalDiveShipStepPatch
{
    public static void Postfix() { LeviathanTemporalDive.StepShips(); }
}

[HarmonyPatch(typeof(NetWorldBridge), "Teardown")]
public static class LeviathanTemporalDiveTeardownPatch
{
    public static void Postfix() { LeviathanTemporalDive.Reset(); }
}

[HarmonyPatch(typeof(WorldController), "OnDestroy")]
public static class LeviathanTemporalDiveWorldDestroyPatch
{
    public static void Postfix() { LeviathanTemporalDive.Reset(); }
}

[HarmonyPatch(typeof(TemporaryEffect), "get_Duration")]
public static class LeviathanTemporalDiveDurationPatch
{
    public static void Postfix(TemporaryEffect __instance, ref float __result)
    {
        TemporalDrive drive = __instance as TemporalDrive;
        if (drive != null)
            __result = LeviathanTemporalDive.TransformDuration(drive, __result);
    }
}

[HarmonyPatch(typeof(TemporaryEffect), "get_LocalDuration")]
public static class LeviathanTemporalDiveLocalDurationPatch
{
    public static void Postfix(TemporaryEffect __instance, ref float __result)
    {
        TemporalDrive drive = __instance as TemporalDrive;
        if (drive != null)
            __result = LeviathanTemporalDive.TransformDuration(drive, __result);
    }
}

[HarmonyPatch(typeof(TemporaryEffect), "get_Cooldown")]
public static class LeviathanTemporalDiveCooldownPatch
{
    public static void Postfix(TemporaryEffect __instance, ref float __result)
    {
        TemporalDrive drive = __instance as TemporalDrive;
        if (drive != null)
            __result = LeviathanTemporalDive.TransformCooldown(drive, __result);
    }
}

[HarmonyPatch(typeof(TemporaryEffect), "get_LocalCooldown")]
public static class LeviathanTemporalDiveLocalCooldownPatch
{
    public static void Postfix(TemporaryEffect __instance, ref float __result)
    {
        TemporalDrive drive = __instance as TemporalDrive;
        if (drive != null)
            __result = LeviathanTemporalDive.TransformCooldown(drive, __result);
    }
}

// =============================================================================
// Native local-time integration
// =============================================================================

[HarmonyPatch(typeof(AIShip), "RunGambits")]
public static class LeviathanTemporalDiveAIPatch
{
    public static void Prefix(AIShip __instance, out float __state)
    {
        __state = __instance.deltaTime;
        __instance.deltaTime = LeviathanTemporalDive.ScaleAIBudget(__instance, __state);
    }
    public static void Postfix(AIShip __instance, float __state) { __instance.deltaTime = __state; }
}

/// <summary>
/// All listed methods have their object in arg0. The same adapter handles ships,
/// equipment, projectile subclasses, AutoDestroy, BlackHole and beam state.
/// Projectile.UpdateCollision is intentionally excluded because its cast length
/// already consumes the factor-scaled rigidbody velocity.
/// </summary>
[HarmonyPatch]
public static class LeviathanTemporalDiveDeltaTimePatch
{
    private static readonly MethodInfo Scale = AccessTools.Method(
        typeof(LeviathanTemporalDive), "ScaleDeltaTime");

    public static IEnumerable<MethodBase> TargetMethods()
    {
        TypeMethod[] targets =
        {
            TM(typeof(GameShip), "FixedUpdate"),
            TM(typeof(GameShip), "UpdateContamination"),
            TM(typeof(GameShip), "UpdateRecoilRotation"),
            TM(typeof(GameShip), "UpdateRecharge"),
            TM(typeof(GameShip), "UpdateBoostInvulnerability"),
            TM(typeof(GameShip), "UpdateHeat"),
            TM(typeof(GameShip), "UpdateThreatGeneration"),
            TM(typeof(GameShip), "UpdateDamagePerSecond"),
            TM(typeof(Damageable), "UpdateHealthRegen"),
            TM(typeof(Activatable), "UpdateDuration"),
            TM(typeof(Activatable), "UpdateCooldown"),
            TM(typeof(Activatable), "UpdateCharges"),
            TM(typeof(Launcher), "UpdateActivation"),
            TM(typeof(Launcher), "UpdateReload"),
            TM(typeof(Launcher), "UpdateMuzzleFlash"),
            TM(typeof(Shield), "FixedUpdate"),
            TM(typeof(AutoDestroy), "Update"),
            TM(typeof(HomingMissile), "ResetTurn"),
            TM(typeof(HomingMissile), "UpdateHoming"),
            TM(typeof(HomingMissile), "UpdateRotation"),
            TM(typeof(SeekingProjectile), "UpdateHeading"),
            TM(typeof(Mine), "FixedUpdate"),
            TM(typeof(Mine), "UpdateHeading"),
            TM(typeof(Mine), "UpdateScale"),
            TM(typeof(Mine), "UpdateRandomMovement"),
            TM(typeof(LauncherProjectile), "FixedUpdate"),
            TM(typeof(LauncherProjectile), "UpdateRotation"),
            TM(typeof(LauncherProjectile), "UpdateLauncher"),
            // Fuzzy collision sweeps use velocity * hitInterval. Keep the
            // sampling interval in world time so consecutive sweeps still meet.
            TM(typeof(LaserProjectile), "UpdateRotation"),
            TM(typeof(LaserProjectile), "ResetTurn"),
            TM(typeof(LaserProjectile), "UpdateHoming"),
            TM(typeof(LaserProjectile), "UpdateTargets"),
            TM(typeof(CapturedProjectile), "UpdateTractorBeam"),
            TM(typeof(CapturedProjectile), "UpdateOrbitMotion"),
            TM(typeof(CapturedProjectile), "UpdateDrawMotion"),
            TM(typeof(ScatterMissile), "ResetTurn"),
            TM(typeof(ScatterMissile), "UpdateHoming"),
            TM(typeof(ScatterMissile), "UpdateRotation"),
            TM(typeof(BlackHole), "Pull"),
            TM(typeof(Beam), "AdjustCurrentState"),
            TM(typeof(Beam), "UpdateLineRenderer"),
            TM(typeof(TickDamageBeam), "UpdateState"),
            TM(typeof(LaserSpinner), "FixedUpdate")
        };

        for (int i = 0; i < targets.Length; i++)
        {
            MethodBase method = AccessTools.DeclaredMethod(targets[i].Type, targets[i].Name);
            if (method != null)
                yield return method;
        }
    }

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        return LeviathanTemporalIL.ScaleDeltaReads(instructions, Scale, 0, true);
    }

    private struct TypeMethod
    {
        public Type Type;
        public string Name;
    }
    private static TypeMethod TM(Type type, string name)
    {
        TypeMethod value = new TypeMethod();
        value.Type = type;
        value.Name = name;
        return value;
    }
}

/// <summary>StatusEffect.Tick has the affected GameShip in arg1 instead of arg0.</summary>
[HarmonyPatch]
public static class LeviathanTemporalDiveStatusPatch
{
    private static readonly MethodInfo Scale = AccessTools.Method(
        typeof(LeviathanTemporalDive), "ScaleStatusDeltaTime");

    public static IEnumerable<MethodBase> TargetMethods()
    {
        Type[] types =
        {
            typeof(StatusEffect), typeof(BurningStatusEffect), typeof(FrozenStatusEffect),
            typeof(HealthRegenStatusEffect), typeof(IceBlockStatusEffect),
            typeof(ImpaledStatusEffect), typeof(PhasedStatusEffect),
            typeof(RadioactiveStatusEffect), typeof(ShieldRegenStatusEffect),
            typeof(TimeWarpedStatusEffect)
        };
        for (int i = 0; i < types.Length; i++)
        {
            MethodBase method = AccessTools.DeclaredMethod(types[i], "Tick");
            if (method != null)
                yield return method;
        }
    }

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        return LeviathanTemporalIL.ScaleDeltaReads(instructions, Scale, 1, false);
    }
}

[HarmonyPatch(typeof(Projectile), "FixedUpdate")]
public static class LeviathanTemporalDiveProjectileVelocityPatch
{
    public static void Prefix(Projectile __instance)
    {
        LeviathanTemporalDive.ApplyProjectileVelocity(__instance);
    }
}

[HarmonyPatch(typeof(Projectile), "ResetObject")]
public static class LeviathanTemporalDiveProjectileResetPatch
{
    public static void Postfix(Projectile __instance)
    {
        LeviathanTemporalDive.ResetProjectile(__instance);
    }
}

[HarmonyPatch]
public static class LeviathanTemporalDiveMissileCapPatch
{
    private static readonly MethodInfo ScaleAcceleration = AccessTools.Method(
        typeof(LeviathanTemporalDive), "ScaleMissileAccelerationDelta");

    public static IEnumerable<MethodBase> TargetMethods()
    {
        MethodBase missile = AccessTools.DeclaredMethod(typeof(Missile), "UpdateVelocity");
        if (missile != null)
            yield return missile;

        // CapturedProjectile overrides UpdateVelocity and can apply its own
        // absolute max-velocity clamp after calling the Missile implementation.
        // Clamp again at the outermost override so it cannot erase the field.
        MethodBase captured = AccessTools.DeclaredMethod(typeof(CapturedProjectile), "UpdateVelocity");
        if (captured != null)
            yield return captured;
    }

    public static void Postfix(Missile __instance)
    {
        LeviathanTemporalDive.ClampMissile(__instance);
    }

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        return LeviathanTemporalIL.ScaleDeltaReads(instructions, ScaleAcceleration, 0, true);
    }
}

[HarmonyPatch(typeof(FuzzyProjectile), "UpdateEndOfLife")]
public static class LeviathanTemporalDiveFuzzyVelocityPatch
{
    private static readonly AccessTools.FieldRef<FuzzyProjectile, bool> Slowing =
        AccessTools.FieldRefAccess<FuzzyProjectile, bool>("slowing");
    private static readonly AccessTools.FieldRef<FuzzyProjectile, Vector2> StartVelocity =
        AccessTools.FieldRefAccess<FuzzyProjectile, Vector2>("slowStartVelocity");

    public static void Prefix(FuzzyProjectile __instance)
    {
        if (__instance.netRendered)
            return;
        LeviathanTemporalProjectileState state =
            __instance.GetComponent<LeviathanTemporalProjectileState>();
        if (state == null)
            return;

        // Native decay rewrites body velocity from this cached vector. Carry
        // field transitions into the cache as well as the body, exactly once.
        float factor = state.Factor;
        if (Slowing(__instance))
            StartVelocity(__instance) *= factor / Mathf.Max(
                LeviathanTemporalDive.Tuning.MinimumFactor, state.FuzzyStartFactor);
        state.FuzzyStartFactor = factor;
    }
}

[HarmonyPatch(typeof(SeekingProjectile), "UpdateHeading")]
public static class LeviathanTemporalDiveSeekingVelocityPatch
{
    private static readonly AccessTools.FieldRef<SeekingProjectile, float> Velocity =
        AccessTools.FieldRefAccess<SeekingProjectile, float>("velocity");

    public static void Prefix(SeekingProjectile __instance, out float __state)
    {
        __state = Velocity(__instance);

        // SeekingProjectile.UpdateHeading can overwrite rigidbody.velocity with
        // transform.right * velocity. Scale the scalar it uses, rather than
        // multiplying the rigidbody afterward: the method has several early
        // returns, and an unconditional postfix multiplier would compound the
        // slowdown whenever no target was present.
        Velocity(__instance) = __state * LeviathanTemporalDive.GetProjectileFactor(__instance);
    }

    public static void Postfix(SeekingProjectile __instance, float __state)
    {
        Velocity(__instance) = __state;
    }
}

/// <summary>
/// Beam damage/status cadence uses fixedTime, so give beams/spinners a local
/// clock instead of scaling damage packets. This preserves native packet,
/// crit/status and repair semantics while reducing real-time tick frequency.
/// </summary>
[HarmonyPatch]
public static class LeviathanTemporalDiveBeamClockPatch
{
    private static readonly MethodInfo Scale = AccessTools.Method(
        typeof(LeviathanTemporalDive), "ScaleFixedTime");

    public static IEnumerable<MethodBase> TargetMethods()
    {
        TypeMethod[] targets =
        {
            TM(typeof(Beam), "DoDamageTick"),
            TM(typeof(Beam), "GetLastHitTime"),
            TM(typeof(Beam), "UpdateLastHitTime"),
            TM(typeof(Beam), "FixedUpdatePiercing"),
            TM(typeof(Beam), "GetAllPiercingHits"),
            TM(typeof(Beam), "GetLastHitTimeForTransform"),
            TM(typeof(Beam), "UpdateLastHitTimeForTransform"),
            TM(typeof(IceBeam), "DoDamageTick"),
            TM(typeof(LaserSpinner), "PruneLastTickTimes"),
            TM(typeof(LaserSpinner), "UpdateLastTickTime")
        };
        for (int i = 0; i < targets.Length; i++)
        {
            MethodBase method = AccessTools.DeclaredMethod(targets[i].Type, targets[i].Name);
            if (method != null)
                yield return method;
        }
    }

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        return LeviathanTemporalIL.ScaleFixedTimeReads(instructions, Scale, 0);
    }

    private struct TypeMethod { public Type Type; public string Name; }
    private static TypeMethod TM(Type type, string name)
    {
        TypeMethod value = new TypeMethod(); value.Type = type; value.Name = name; return value;
    }
}

[HarmonyPatch(typeof(Beam), "OnDestroy")]
public static class LeviathanTemporalDiveBeamCleanupPatch
{
    public static void Prefix(Beam __instance) { LeviathanTemporalDive.ForgetClock(__instance); }
}

[HarmonyPatch(typeof(LaserSpinner), "Unequip")]
public static class LeviathanTemporalDiveSpinnerCleanupPatch
{
    public static void Prefix(LaserSpinner __instance) { LeviathanTemporalDive.ForgetClock(__instance); }
}

/// <summary>Shared IL transform; patch registration allocates, combat execution does not.</summary>
public static class LeviathanTemporalIL
{
    private static readonly MethodInfo Delta = AccessTools.PropertyGetter(typeof(Time), "deltaTime");
    private static readonly MethodInfo FixedDelta = AccessTools.PropertyGetter(typeof(Time), "fixedDeltaTime");
    private static readonly MethodInfo FixedTime = AccessTools.PropertyGetter(typeof(Time), "fixedTime");

    public static IEnumerable<CodeInstruction> ScaleDeltaReads(
        IEnumerable<CodeInstruction> instructions,
        MethodInfo scale,
        int ownerArgument,
        bool includeFixedDelta)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            yield return instruction;
            bool match = (Delta != null && instruction.Calls(Delta)) ||
                (includeFixedDelta && FixedDelta != null && instruction.Calls(FixedDelta));
            if (match)
            {
                yield return LoadArgument(ownerArgument);
                yield return new CodeInstruction(OpCodes.Call, scale);
            }
        }
    }

    public static IEnumerable<CodeInstruction> ScaleFixedTimeReads(
        IEnumerable<CodeInstruction> instructions,
        MethodInfo scale,
        int ownerArgument)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            yield return instruction;
            if (FixedTime != null && instruction.Calls(FixedTime))
            {
                yield return LoadArgument(ownerArgument);
                yield return new CodeInstruction(OpCodes.Call, scale);
            }
        }
    }

    private static CodeInstruction LoadArgument(int index)
    {
        if (index == 0) return new CodeInstruction(OpCodes.Ldarg_0);
        if (index == 1) return new CodeInstruction(OpCodes.Ldarg_1);
        if (index == 2) return new CodeInstruction(OpCodes.Ldarg_2);
        if (index == 3) return new CodeInstruction(OpCodes.Ldarg_3);
        return new CodeInstruction(OpCodes.Ldarg, index);
    }
}
