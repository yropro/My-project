using HarmonyLib;
using StarVortex;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>
/// Pure-Void Orrery spell (legacy recipe slot: Ice + Ice).
///
/// A thick annular-sector front advances toward the cast cursor. Host-authority
/// gameplay slows hostile ships/projectiles while they are in contact. Hostile
/// projectiles are consumed against one cast-wide incoming-damage budget; after
/// that budget is exhausted they survive but continue to be slowed.
///
/// A non-host caster sends one immutable cast snapshot through
/// CoreWorldEffectIntents. The host then simulates only the world-authoritative
/// mechanics from that snapshot. Presentation remains completely separate.
/// </summary>
public static class OrreryVoidWave
{
    [StructLayout(LayoutKind.Explicit)]
    private struct FloatBits
    {
        [FieldOffset(0)] public float Float;
        [FieldOffset(0)] public uint UInt;
    }

    public struct PresentationSnapshot
    {
        public bool Active;
        public uint Generation;
        public Vector2 Origin;
        public Vector2 Direction;
        public float FrontDistanceMeters;
        public float Progress01;
    }

    private sealed class WaveState
    {
        public GameShip Owner;
        public OrreryCastInvocation Invocation;
        public uint Generation;
        public Vector2 Origin;
        public Vector2 Direction;
        public string SourceFaction;
        public float FrontDistanceMeters;
        public float RemainingAbsorptionBudget;
        public bool RunGameplay;
        public CoreMotionEffects.SourceKey MotionSource;
        public readonly Projectile[] TrackedProjectiles =
            new Projectile[OrrerySpellCompendium.VoidWave.MaxTrackedProjectiles];
        public int TrackedProjectileCount;
    }

    private sealed class HostWaveState
    {
        public bool Active;
        public int SourcePlayerId;
        public uint Generation;
        public Vector2 Origin;
        public Vector2 Direction;
        public string SourceFaction;
        public float FrontDistanceMeters;
        public float RemainingAbsorptionBudget;
        public CoreMotionEffects.SourceKey MotionSource;
        public readonly Projectile[] TrackedProjectiles =
            new Projectile[OrrerySpellCompendium.VoidWave.MaxTrackedProjectiles];
        public int TrackedProjectileCount;
    }

    private static readonly Dictionary<GameShip, WaveState> localWaves =
        new Dictionary<GameShip, WaveState>(4);
    private static readonly HostWaveState[] hostWaves = CreateHostWaves();
    private static bool initialized;
    private static uint nextGeneration;

    private static HostWaveState[] CreateHostWaves()
    {
        HostWaveState[] result = new HostWaveState[
            OrrerySpellCompendium.VoidWave.MaxHostGameplayWaves];
        for (int i = 0; i < result.Length; i++)
            result[i] = new HostWaveState();
        return result;
    }

    public static void EnsureInitialized()
    {
        if (initialized)
            return;

        CoreWorldEffectIntents.RegisterHandler(
            OrrerySpellCompendium.VoidWave.HostWorldIntentEffectId,
            ReceiveHostIntent);
        CoreWorldEffectRuntime.Register(
            "Orrery/VoidWave-host",
            FixedTickHostWorld,
            ResetHostWorld);
        OrreryVoidWavePresentation.EnsureInitialized();
        initialized = true;
    }

    public static bool Execute(
        GameShip owner,
        OrreryCastInvocation invocation,
        OrrerySpellRegistry.SpellDefinition spell)
    {
        EnsureInitialized();

        if (owner == null || spell == null || invocation.Execution == null ||
            !invocation.Execution.IsValid || !OrreryRuntime.IsActive(owner) ||
            OrreryController.IsShuffling(owner) || PhysicsController.instance == null)
        {
            return false;
        }

        if (localWaves.ContainsKey(owner))
            return false;

        OrreryFocusProfile.Resolved focus;
        if (!OrreryFocusProfile.TryResolve(owner, OrreryElement.Ice, out focus) ||
            !focus.IsValid)
        {
            return false;
        }

        Vector2 origin = owner.transform.position;
        Vector2 direction = GetAimDirection(owner, origin);
        if (direction.sqrMagnitude <= 0.000001f)
            return false;
        direction.Normalize();

        float referenceDps = focus.GetReferenceDps(
            OrrerySpellPower.ReferenceMode.Mean);
        float budget = focus.ApplySpellDamageBonus(
            Mathf.Max(0f, referenceDps) *
            Mathf.Max(0f,
                OrrerySpellCompendium.VoidWave.ProjectileAbsorptionBudgetReferenceSeconds));

        uint generation = NextGeneration();
        bool runGameplay = !NetSession.InSession || NetSession.IsHost;

        if (NetSession.InSession && !NetSession.IsHost)
        {
            if (!CoreWorldEffectIntents.RequestHost(
                    OrrerySpellCompendium.VoidWave.HostWorldIntentEffectId,
                    PackHostIntent(origin, direction, budget, generation)))
            {
                return false;
            }
        }

        WaveState state = new WaveState();
        state.Owner = owner;
        state.Invocation = invocation;
        state.Generation = generation;
        state.Origin = origin;
        state.Direction = direction;
        state.SourceFaction = owner.faction;
        state.FrontDistanceMeters =
            OrrerySpellCompendium.VoidWave.InitialFrontDistanceMeters;
        state.RemainingAbsorptionBudget = budget;
        state.RunGameplay = runGameplay;
        state.MotionSource = new CoreMotionEffects.SourceKey(
            OrrerySpellCompendium.VoidWave.MotionEffectId,
            owner.GetInstanceID(),
            generation);
        localWaves.Add(owner, state);
        return true;
    }

    public static void FixedTick(GameShip owner, float deltaTime)
    {
        WaveState state;
        if (owner == null || !localWaves.TryGetValue(owner, out state) || state == null)
            return;

        if (!OrreryRuntime.IsActive(owner) || state.Invocation.Execution == null ||
            !state.Invocation.Execution.IsValid)
        {
            Abort(owner, state);
            return;
        }

        Advance(ref state.FrontDistanceMeters, deltaTime);
        if (state.RunGameplay)
            ApplyGameplay(
                state.Owner,
                state.SourceFaction,
                state.Origin,
                state.Direction,
                state.FrontDistanceMeters,
                state.MotionSource,
                ref state.RemainingAbsorptionBudget,
                state.TrackedProjectiles,
                ref state.TrackedProjectileCount);

        if (state.FrontDistanceMeters >=
            OrrerySpellCompendium.VoidWave.MaximumFrontDistanceMeters)
        {
            Complete(owner, state);
        }
    }

    public static bool TryGetPresentation(
        GameShip owner,
        out PresentationSnapshot snapshot)
    {
        snapshot = default(PresentationSnapshot);
        WaveState state;
        if (owner == null || !localWaves.TryGetValue(owner, out state) || state == null)
            return false;

        snapshot.Active = true;
        snapshot.Generation = state.Generation;
        snapshot.Origin = state.Origin;
        snapshot.Direction = state.Direction;
        snapshot.FrontDistanceMeters = state.FrontDistanceMeters;
        snapshot.Progress01 = GetProgress(state.FrontDistanceMeters);
        return true;
    }

    public static void Forget(GameShip owner)
    {
        if (owner == null)
            return;

        WaveState state;
        if (localWaves.TryGetValue(owner, out state) && state != null)
            localWaves.Remove(owner);
    }

    public static void Reset()
    {
        localWaves.Clear();
        ResetHostWorld();
        nextGeneration = 0u;
    }

    private static void FixedTickHostWorld(float deltaTime)
    {
        if (NetSession.InSession && !NetSession.IsHost)
            return;

        for (int i = 0; i < hostWaves.Length; i++)
        {
            HostWaveState state = hostWaves[i];
            if (state == null || !state.Active)
                continue;

            Advance(ref state.FrontDistanceMeters, deltaTime);
            ApplyGameplay(
                null,
                state.SourceFaction,
                state.Origin,
                state.Direction,
                state.FrontDistanceMeters,
                state.MotionSource,
                ref state.RemainingAbsorptionBudget,
                state.TrackedProjectiles,
                ref state.TrackedProjectileCount);

            if (state.FrontDistanceMeters >=
                OrrerySpellCompendium.VoidWave.MaximumFrontDistanceMeters)
            {
                ClearHostWave(state);
            }
        }
    }

    private static bool ReceiveHostIntent(
        int sourcePlayerId,
        CoreWorldEffectIntents.Payload payload)
    {
        if (!NetSession.InSession || !NetSession.IsHost || sourcePlayerId < 0)
            return false;

        Vector2 origin = new Vector2(
            UnpackFloat(payload.A),
            UnpackFloat(payload.B));
        Vector2 direction = new Vector2(
            UnpackFloat(payload.C),
            UnpackFloat(payload.D));
        float budget = UnpackFloat(payload.E);
        uint generation = payload.F;

        if (!IsFinite(origin) || !IsFinite(direction) ||
            !IsFinite(budget) || direction.sqrMagnitude <= 0.000001f ||
            budget < 0f || generation == 0u)
        {
            return false;
        }

        NetSession session = NetSession.instance;
        NetPlayer sourcePlayer = session == null ? null : session.GetPlayer(sourcePlayerId);
        Ship sourceShip = sourcePlayer == null ? null : sourcePlayer.GetShipObject();
        if (sourceShip == null || string.IsNullOrEmpty(sourceShip.faction))
            return false;

        HostWaveState state = FindFreeHostWave();
        if (state == null)
            return false;

        state.Active = true;
        state.SourcePlayerId = sourcePlayerId;
        state.Generation = generation;
        state.Origin = origin;
        state.Direction = direction.normalized;
        state.SourceFaction = sourceShip.faction;
        state.FrontDistanceMeters =
            OrrerySpellCompendium.VoidWave.InitialFrontDistanceMeters;
        state.RemainingAbsorptionBudget = budget;
        state.MotionSource = new CoreMotionEffects.SourceKey(
            OrrerySpellCompendium.VoidWave.MotionEffectId,
            sourcePlayerId + 1,
            generation);
        state.TrackedProjectileCount = 0;
        System.Array.Clear(
            state.TrackedProjectiles,
            0,
            state.TrackedProjectiles.Length);
        return true;
    }

    private static HostWaveState FindFreeHostWave()
    {
        for (int i = 0; i < hostWaves.Length; i++)
        {
            if (!hostWaves[i].Active)
                return hostWaves[i];
        }
        return null;
    }

    private static void ApplyGameplay(
        GameShip sourceShip,
        string sourceFaction,
        Vector2 origin,
        Vector2 direction,
        float frontMeters,
        CoreMotionEffects.SourceKey motionSource,
        ref float remainingBudget,
        Projectile[] trackedProjectiles,
        ref int trackedProjectileCount)
    {
        if (PhysicsController.instance == null || string.IsNullOrEmpty(sourceFaction))
            return;

        PruneTrackedProjectiles(trackedProjectiles, ref trackedProjectileCount);

        float outerWorld = CoreSpatial.MetersToWorldUnits(
            Mathf.Max(0f, frontMeters));
        float padding = Mathf.Max(
            1f,
            OrrerySpellCompendium.VoidWave.BroadphasePaddingMultiplier);
        Collider2D[] overlaps = PhysicsController.instance.OverlapCircle(
            origin,
            outerWorld * padding);
        if (overlaps == null)
            return;

        float innerMeters = Mathf.Max(
            0f,
            frontMeters - OrrerySpellCompendium.VoidWave.WaveDepthMeters);
        float innerWorld = CoreSpatial.MetersToWorldUnits(innerMeters);
        float halfAngle = GetHalfAngleDegrees(frontMeters);
        int limit = Mathf.Min(
            overlaps.Length,
            OrrerySpellCompendium.VoidWave.MaxColliderCandidatesPerTick);

        for (int i = 0; i < limit; i++)
        {
            Collider2D collider = overlaps[i];
            if (collider == null)
                break;

            GameObject candidate = collider.gameObject;
            if (candidate == null)
                continue;

            GameObject rootCandidate = candidate;
            if (candidate.CompareTag("Shield") && candidate.transform.parent != null)
                rootCandidate = candidate.transform.parent.gameObject;

            GameShip ship;
            if (rootCandidate.TryGetComponent<GameShip>(out ship) && ship != null)
            {
                if (object.ReferenceEquals(ship, sourceShip) ||
                    !Faction.IsHostile(sourceFaction, ship.faction) ||
                    !IsInsideWave(
                        ship.transform.position,
                        origin,
                        direction,
                        innerWorld,
                        outerWorld,
                        halfAngle))
                {
                    continue;
                }

                if (sourceShip != null && !ship.CanBeDamagedBy(sourceShip, false))
                    continue;

                CoreMotionEffects.GrantShipSlow(
                    ship,
                    motionSource,
                    OrrerySpellCompendium.VoidWave.ShipSpeedMultiplier,
                    OrrerySpellCompendium.VoidWave.ContactLeaseSeconds);
                continue;
            }

            Projectile projectile;
            if (!candidate.TryGetComponent<Projectile>(out projectile) ||
                projectile == null || projectile.netRendered || projectile.IsDestroying())
            {
                continue;
            }

            GameShip projectileOwner = projectile.GetParentShip();
            if (projectileOwner == null ||
                !Faction.IsHostile(sourceFaction, projectileOwner.faction) ||
                !IsInsideWave(
                    projectile.transform.position,
                    origin,
                    direction,
                    innerWorld,
                    outerWorld,
                    halfAngle))
            {
                continue;
            }

            CoreMotionEffects.GrantProjectileSlow(
                projectile,
                motionSource,
                OrrerySpellCompendium.VoidWave.ProjectileSpeedMultiplier,
                OrrerySpellCompendium.VoidWave.ContactLeaseSeconds);

            if (HasTrackedProjectile(
                    trackedProjectiles,
                    trackedProjectileCount,
                    projectile))
            {
                continue;
            }

            if (trackedProjectileCount >= trackedProjectiles.Length)
                continue;

            trackedProjectiles[trackedProjectileCount++] = projectile;
            if (remainingBudget <= 0f)
                continue;

            float cost = EstimateProjectileCost(projectile);
            if (cost <= remainingBudget)
            {
                remainingBudget -= cost;
                projectile.CaptureDestroy();
            }
            else
            {
                // Partial interception exhausts the barrier but cannot partially
                // destroy one projectile. It survives and remains slowed.
                remainingBudget = 0f;
            }
        }
    }

    private static float EstimateProjectileCost(Projectile projectile)
    {
        Launcher launcher = projectile == null ? null : projectile.GetParentLauncher();
        if (launcher == null)
            return OrrerySpellCompendium.VoidWave.MinimumProjectileBudgetCost;

        Damageable.DamageData[] damageData = launcher.GetDamageData(false, false);
        float baseDamage = Damageable.DamageData.GetDamageData(
            Modifier.Type.Damage,
            damageData).damage;
        return Mathf.Max(
            OrrerySpellCompendium.VoidWave.MinimumProjectileBudgetCost,
            Mathf.Max(0f, baseDamage) *
            Mathf.Max(0f,
                OrrerySpellCompendium.VoidWave.ProjectileBudgetCostMultiplier));
    }

    private static bool IsInsideWave(
        Vector2 point,
        Vector2 origin,
        Vector2 direction,
        float innerWorld,
        float outerWorld,
        float halfAngleDegrees)
    {
        return CoreSpatial.IsPointInAnnularSector(
            point,
            origin,
            direction,
            innerWorld,
            outerWorld,
            halfAngleDegrees);
    }

    internal static float GetHalfAngleDegrees(float frontMeters)
    {
        float radius = Mathf.Max(0.001f, frontMeters);
        float width = GetArcWidthMeters(frontMeters);
        float halfChord = Mathf.Min(radius, Mathf.Max(0f, width) * 0.5f);
        return Mathf.Asin(Mathf.Clamp01(halfChord / radius)) * Mathf.Rad2Deg;
    }

    internal static float GetArcWidthMeters(float frontMeters)
    {
        float progress = GetProgress(frontMeters);
        return Mathf.Lerp(
            OrrerySpellCompendium.VoidWave.InitialArcWidthMeters,
            OrrerySpellCompendium.VoidWave.FinalArcWidthMeters,
            progress);
    }

    internal static float GetProgress(float frontMeters)
    {
        float start = OrrerySpellCompendium.VoidWave.InitialFrontDistanceMeters;
        float end = Mathf.Max(
            start + 0.001f,
            OrrerySpellCompendium.VoidWave.MaximumFrontDistanceMeters);
        return Mathf.Clamp01((frontMeters - start) / (end - start));
    }

    private static void Advance(ref float frontMeters, float deltaTime)
    {
        frontMeters = Mathf.Min(
            OrrerySpellCompendium.VoidWave.MaximumFrontDistanceMeters,
            frontMeters +
            Mathf.Max(0f, OrrerySpellCompendium.VoidWave.TravelSpeedMetersPerSecond) *
            Mathf.Max(0f, deltaTime));
    }

    private static Vector2 GetAimDirection(GameShip owner, Vector2 origin)
    {
        InputController input = InputController.instance;
        if (input != null && owner != null &&
            object.ReferenceEquals(input.controlShip, owner))
        {
            Vector2 toCursor = input.GetCursorWorldPoint() - origin;
            if (toCursor.sqrMagnitude > 0.000001f)
                return toCursor.normalized;
        }

        return owner == null
            ? Vector2.right
            : (Vector2)owner.transform.right;
    }

    private static bool HasTrackedProjectile(
        Projectile[] tracked,
        int count,
        Projectile projectile)
    {
        for (int i = 0; i < count; i++)
        {
            if (object.ReferenceEquals(tracked[i], projectile))
                return true;
        }
        return false;
    }

    private static void PruneTrackedProjectiles(
        Projectile[] tracked,
        ref int count)
    {
        int write = 0;
        for (int i = 0; i < count; i++)
        {
            Projectile projectile = tracked[i];
            if (projectile == null || projectile.gameObject == null ||
                !projectile.gameObject.activeInHierarchy)
            {
                continue;
            }

            tracked[write++] = projectile;
        }

        for (int i = write; i < count; i++)
            tracked[i] = null;
        count = write;
    }

    private static void Complete(GameShip owner, WaveState state)
    {
        if (state.Invocation.Execution != null &&
            state.Invocation.Execution.IsValid)
        {
            OrreryCasting.CompleteInvocation(
                owner,
                state.Invocation.Execution,
                0);
        }
        else
        {
            OrreryCasting.Cancel(owner);
        }

        localWaves.Remove(owner);
        OrreryController.StartShuffle(owner);
    }

    private static void Abort(GameShip owner, WaveState state)
    {
        localWaves.Remove(owner);
        OrreryCasting.Cancel(owner);
        OrreryController.StartShuffle(owner);
    }

    private static void ResetHostWorld()
    {
        for (int i = 0; i < hostWaves.Length; i++)
            ClearHostWave(hostWaves[i]);
    }

    private static void ClearHostWave(HostWaveState state)
    {
        if (state == null)
            return;

        state.Active = false;
        state.SourcePlayerId = -1;
        state.Generation = 0u;
        state.Origin = Vector2.zero;
        state.Direction = Vector2.zero;
        state.SourceFaction = null;
        state.FrontDistanceMeters = 0f;
        state.RemainingAbsorptionBudget = 0f;
        state.MotionSource = default(CoreMotionEffects.SourceKey);
        state.TrackedProjectileCount = 0;
        System.Array.Clear(
            state.TrackedProjectiles,
            0,
            state.TrackedProjectiles.Length);
    }

    private static CoreWorldEffectIntents.Payload PackHostIntent(
        Vector2 origin,
        Vector2 direction,
        float budget,
        uint generation)
    {
        return new CoreWorldEffectIntents.Payload(
            PackFloat(origin.x),
            PackFloat(origin.y),
            PackFloat(direction.x),
            PackFloat(direction.y),
            PackFloat(budget),
            generation,
            0u, 0u, 0u, 0u, 0u, 0u);
    }

    private static uint PackFloat(float value)
    {
        FloatBits bits = default(FloatBits);
        bits.Float = value;
        return bits.UInt;
    }

    private static float UnpackFloat(uint value)
    {
        FloatBits bits = default(FloatBits);
        bits.UInt = value;
        return bits.Float;
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static bool IsFinite(Vector2 value)
    {
        return IsFinite(value.x) && IsFinite(value.y);
    }

    private static uint NextGeneration()
    {
        unchecked
        {
            nextGeneration++;
            if (nextGeneration == 0u)
                nextGeneration++;
            return nextGeneration;
        }
    }
}

/// <summary>
/// World entry is the first lifecycle point after Harmony is active and before
/// encounter simulation/network presentation begins. Register the shared host
/// intent/runtime and the Void Wave presentation exactly once here.
/// </summary>
[HarmonyPatch(typeof(WorldController), "PostInit")]
public static class OrreryVoidWaveBootstrapPatch
{
    public static void Postfix()
    {
        OrreryVoidWave.EnsureInitialized();
    }
}
