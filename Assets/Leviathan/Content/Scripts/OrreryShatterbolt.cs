using HarmonyLib;
using StarVortex;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Ice + Lightning mixed Orrery spell.
///
/// A presentation-only Lightning Orb visual physically travels to the hostile
/// nearest the cursor, then through up to three more chain legs. Each impact owns
/// one Electric direct packet plus one independently expanding Cold explosion.
/// Chain selection prefers ships not yet directly hit, then permits revisits while
/// never selecting the ship just struck.
/// </summary>
public static class OrreryShatterbolt
{
    private const string LightningOrbPath =
        "Base/Items/SecondaryWeapon/Lightning Orb Launcher";
    private const string FrozenOrbPath =
        "Base/Items/SecondaryWeapon/Frozen Orb Launcher";
    private const string FrostNovaPath =
        "Base/Items/Special/Frost Nova Pulse";

    public struct PresentationSnapshot
    {
        public bool Present;
        public bool OrbActive;
        public byte CastSequence;
        public byte ImpactCount;
        public Vector2 OrbPosition;
        public Vector2 Impact0;
        public Vector2 Impact1;
        public Vector2 Impact2;
        public Vector2 Impact3;

        public Vector2 GetImpact(int index)
        {
            switch (index)
            {
                case 0: return Impact0;
                case 1: return Impact1;
                case 2: return Impact2;
                case 3: return Impact3;
                default: return Vector2.zero;
            }
        }

        public void SetImpact(int index, Vector2 position)
        {
            switch (index)
            {
                case 0: Impact0 = position; break;
                case 1: Impact1 = position; break;
                case 2: Impact2 = position; break;
                case 3: Impact3 = position; break;
            }
        }
    }

    private struct DamageProfile
    {
        public float DamageDps;
        public float NeutralDamage;
        public float CritChance;
        public float CritModifier;
        public float StatusChance;
        public bool BypassDamageLimit;
        public Launcher Source;
    }

    private sealed class ExplosionState
    {
        public bool Active;
        public Vector2 Center;
        public float RadiusWorld;
        public DamageProfile Damage;
        public readonly Damageable[] HitTargets =
            new Damageable[OrrerySpellCompendium.Shatterbolt.MaxTargetsPerExplosion];
        public int HitCount;

        public Wave Visual;
        public bool VisualWasEnabled;
        public CircleCollider2D VisualCollider;
        public bool VisualColliderWasEnabled;
        public float VisualColliderRadius;
        public Vector3 VisualBaseScale;
    }

    private sealed class OrbVisualState
    {
        public Projectile Projectile;
        public bool ProjectileWasEnabled;
        public Rigidbody2D Body;
        public bool BodyWasSimulated;
        public Collider2D[] Colliders;
        public bool[] ColliderEnabled;
        public Vector3 BaseScale;
    }

    private sealed class OwnerState
    {
        public OrreryCastInvocation Invocation;
        public bool CastActive;
        public GameShip CurrentTarget;
        public Vector2 ProjectilePosition;
        public float LegElapsedSeconds;
        public int ImpactCount;
        public readonly Vector2[] ImpactPositions =
            new Vector2[OrrerySpellCompendium.Shatterbolt.MaximumImpacts];
        public readonly GameShip[] DirectHistory =
            new GameShip[OrrerySpellCompendium.Shatterbolt.MaximumImpacts];
        public int DirectHistoryCount;
        public readonly GameShip[] CandidateShips =
            new GameShip[OrrerySpellCompendium.Shatterbolt.MaxCandidateShipsPerQuery];
        public int CandidateCount;

        public OrreryFocusResolver.Focus LightningFocus;
        public OrreryFocusResolver.Focus IceFocus;
        public Launcher LightningSource;
        public Launcher IceSource;
        public DamageProfile LightningDamage;
        public DamageProfile IceDamage;
        public readonly Damageable.DamageData[] DirectDamageScratch =
            new Damageable.DamageData[1];
        public readonly Damageable.DamageData[] ExplosionDamageScratch =
            new Damageable.DamageData[1];

        public OrbVisualState OrbVisual;
        public readonly ExplosionState[] Explosions =
            new ExplosionState[OrrerySpellCompendium.Shatterbolt.MaximumImpacts];

        public OwnerState()
        {
            for (int i = 0; i < Explosions.Length; i++)
                Explosions[i] = new ExplosionState();
        }
    }

    private static readonly Dictionary<GameShip, OwnerState> owners =
        new Dictionary<GameShip, OwnerState>(4);
    private static GameShip lastTickOwner;

    public static bool Execute(
        GameShip owner,
        OrreryCastInvocation invocation,
        OrrerySpellRegistry.SpellDefinition spell)
    {
        if (owner == null || spell == null || invocation.Execution == null ||
            !invocation.Execution.IsValid || !OrreryRuntime.IsActive(owner) ||
            OrreryController.IsShuffling(owner) || PhysicsController.instance == null)
        {
            return false;
        }

        OwnerState state;
        if (!owners.TryGetValue(owner, out state) || state == null)
        {
            state = new OwnerState();
            owners[owner] = state;
        }

        if (state.CastActive || HasActiveExplosions(state))
            return false;

        CleanupOrbVisual(state);
        DisposeSources(state);
        ResetCastFields(state);

        OrreryFocusResolver.Focus lightningFocus =
            default(OrreryFocusResolver.Focus);
        OrreryFocusResolver.Focus iceFocus =
            default(OrreryFocusResolver.Focus);
        Launcher lightningSource = null;
        Launcher iceSource = null;
        if (!TryCreateSource(
                owner,
                OrreryElement.Lightning,
                LightningOrbPath,
                out lightningFocus,
                out lightningSource) ||
            !TryCreateSource(
                owner,
                OrreryElement.Ice,
                FrozenOrbPath,
                out iceFocus,
                out iceSource))
        {
            DisposeSource(lightningSource);
            DisposeSource(iceSource);
            return false;
        }

        state.LightningFocus = lightningFocus;
        state.IceFocus = iceFocus;
        state.LightningSource = lightningSource;
        state.IceSource = iceSource;
        state.LightningDamage = BuildDamageProfile(
            lightningFocus,
            lightningSource,
            OrrerySpellCompendium.Shatterbolt.LightningDamageMultiplier);
        state.IceDamage = BuildDamageProfile(
            iceFocus,
            iceSource,
            OrrerySpellCompendium.Shatterbolt.IceExplosionDamageMultiplier);

        Vector2 aimPoint = GetAimPoint(owner);
        GameShip initialTarget = FindInitialTarget(owner, state, aimPoint);
        if (initialTarget == null)
        {
            DisposeSources(state);
            return false;
        }

        Vector2 spawnPosition = owner.transform.position;
        if (!SpawnOrbVisual(state, spawnPosition))
        {
            DisposeSources(state);
            return false;
        }

        state.Invocation = invocation;
        state.CastActive = true;
        state.CurrentTarget = initialTarget;
        state.ProjectilePosition = spawnPosition;
        state.LegElapsedSeconds = 0f;
        OrreryNetwork.PublishLocal(owner);
        return true;
    }

    /// <summary>
    /// Called from the small fixed-step Harmony bridge below. It tracks the one
    /// locally authoritative Orrery owner and also guarantees old-owner cleanup if
    /// class context changes without another Shatterbolt cast occurring.
    /// </summary>
    public static void TickLocal(float deltaTime)
    {
        CoreOwnerContext context = CoreClassRuntime.CurrentContext;
        GameShip owner = context != null && context.IsValid &&
            context.ClassId == CoreClassId.Orrery
                ? context.Ship
                : null;

        if (!object.ReferenceEquals(owner, lastTickOwner))
        {
            if (lastTickOwner != null)
                Forget(lastTickOwner);
            lastTickOwner = owner;
        }

        if (owner != null)
            FixedTick(owner, deltaTime);
    }

    public static void FixedTick(GameShip owner, float deltaTime)
    {
        OwnerState state;
        if (owner == null || !owners.TryGetValue(owner, out state) || state == null)
            return;

        if (!OrreryRuntime.IsActive(owner))
        {
            Forget(owner);
            return;
        }

        TickExplosions(owner, state, deltaTime);

        if (!state.CastActive)
        {
            if (HasActiveExplosions(state))
            {
                OrreryNetwork.PublishLocal(owner);
            }
            else
            {
                CleanupIdleState(owner, state);
                // Publish once after removing the spell state so remote observers
                // can clear their orb/persistent-presentation flag deterministically.
                OrreryNetwork.PublishLocal(owner);
            }
            return;
        }

        if (state.Invocation.Execution == null ||
            !state.Invocation.Execution.IsValid)
        {
            AbortCast(owner, state);
            return;
        }

        GameShip target = state.CurrentTarget;
        if (!IsValidTarget(owner, target))
        {
            target = FindChainTarget(owner, state, state.ProjectilePosition, null);
            if (target == null)
            {
                CompleteCast(owner, state);
                return;
            }
            state.CurrentTarget = target;
            state.LegElapsedSeconds = 0f;
        }

        float dt = Mathf.Max(0f, deltaTime);
        state.LegElapsedSeconds += dt;
        if (state.LegElapsedSeconds >
            Mathf.Max(0.1f, OrrerySpellCompendium.Shatterbolt.MaximumLegSeconds))
        {
            CompleteCast(owner, state);
            return;
        }

        Vector2 targetPosition = target.transform.position;
        Vector2 toTarget = targetPosition - state.ProjectilePosition;
        float speedWorld = OrreryUnits.MetersToWorld(
            Mathf.Max(0f,
                OrrerySpellCompendium.Shatterbolt.ProjectileSpeedMetersPerSecond));
        float step = speedWorld * dt;

        if (toTarget.sqrMagnitude <= step * step || toTarget.sqrMagnitude <= 0.000001f)
        {
            state.ProjectilePosition = targetPosition;
            UpdateOrbVisual(state, dt);
            HandleImpact(owner, state, target, targetPosition);
            OrreryNetwork.PublishLocal(owner);
            return;
        }

        if (step > 0f)
            state.ProjectilePosition += toTarget.normalized * step;
        UpdateOrbVisual(state, dt);
        OrreryNetwork.PublishLocal(owner);
    }

    public static void Forget(GameShip owner)
    {
        if (owner == null)
            return;

        OwnerState state;
        if (owners.TryGetValue(owner, out state) && state != null)
        {
            CleanupOrbVisual(state);
            ClearExplosions(state);
            DisposeSources(state);
            owners.Remove(owner);
        }

        OrreryDamageRouter.Forget(owner);
        if (object.ReferenceEquals(lastTickOwner, owner))
            lastTickOwner = null;
    }

    public static void Reset()
    {
        GameShip[] keys = new GameShip[owners.Count];
        owners.Keys.CopyTo(keys, 0);
        for (int i = 0; i < keys.Length; i++)
            Forget(keys[i]);
        owners.Clear();
        lastTickOwner = null;
        OrreryDamageRouter.Reset();
    }

    public static bool TryGetPresentation(
        GameShip owner,
        out PresentationSnapshot snapshot)
    {
        snapshot = default(PresentationSnapshot);

        OwnerState state;
        if (owner == null || !owners.TryGetValue(owner, out state) || state == null)
            return false;

        bool hasExplosions = HasActiveExplosions(state);
        if (!state.CastActive && !hasExplosions && state.ImpactCount <= 0)
            return false;

        snapshot.Present = true;
        snapshot.OrbActive = state.CastActive && state.OrbVisual != null;
        snapshot.CastSequence = (byte)(state.Invocation.Sequence & 0xFF);
        snapshot.ImpactCount = (byte)Mathf.Clamp(
            state.ImpactCount,
            0,
            OrrerySpellCompendium.Shatterbolt.MaximumImpacts);
        snapshot.OrbPosition = state.ProjectilePosition;

        int count = Mathf.Min(snapshot.ImpactCount, state.ImpactPositions.Length);
        for (int i = 0; i < count; i++)
            snapshot.SetImpact(i, state.ImpactPositions[i]);

        return true;
    }

    private static void HandleImpact(
        GameShip owner,
        OwnerState state,
        GameShip target,
        Vector2 impactPoint)
    {
        if (IsValidTarget(owner, target) && !target.IsDodging())
            ApplyDirectDamage(owner, state, target, impactPoint);

        RecordDirectTarget(state, target);
        SpawnExplosion(state, impactPoint);
        if (state.ImpactCount >= 0 &&
            state.ImpactCount < state.ImpactPositions.Length)
        {
            state.ImpactPositions[state.ImpactCount] = impactPoint;
        }
        state.ImpactCount++;

        int maxImpacts = Mathf.Max(
            1,
            OrrerySpellCompendium.Shatterbolt.MaximumImpacts);
        if (state.ImpactCount >= maxImpacts)
        {
            CompleteCast(owner, state);
            return;
        }

        GameShip next = FindChainTarget(owner, state, impactPoint, target);
        if (next == null)
        {
            CompleteCast(owner, state);
            return;
        }

        state.CurrentTarget = next;
        state.LegElapsedSeconds = 0f;
    }

    private static void ApplyDirectDamage(
        GameShip owner,
        OwnerState state,
        Damageable target,
        Vector2 impactPoint)
    {
        DamageProfile profile = state.LightningDamage;
        GameShip targetShip = target as GameShip;
        bool crit = Modifier.CritRoll(profile.CritChance, targetShip);
        float damage = crit
            ? profile.NeutralDamage * (1f + profile.CritModifier)
            : profile.NeutralDamage;
        state.DirectDamageScratch[0] = new Damageable.DamageData(
            damage,
            profile.DamageDps);

        OrreryDamageRouter.Route(
            owner,
            target,
            Damageable.DamageType.Electric,
            state.DirectDamageScratch,
            profile.StatusChance,
            crit,
            impactPoint,
            profile.BypassDamageLimit,
            0f,
            profile.Source);
    }

    private static void SpawnExplosion(OwnerState state, Vector2 center)
    {
        ExplosionState explosion = null;
        for (int i = 0; i < state.Explosions.Length; i++)
        {
            if (!state.Explosions[i].Active)
            {
                explosion = state.Explosions[i];
                break;
            }
        }

        if (explosion == null)
            return;

        CleanupExplosionVisual(explosion);
        explosion.Active = true;
        explosion.Center = center;
        explosion.RadiusWorld = 0f;
        explosion.Damage = state.IceDamage;
        explosion.HitCount = 0;
        System.Array.Clear(
            explosion.HitTargets,
            0,
            explosion.HitTargets.Length);
        SpawnExplosionVisual(state, explosion);
    }

    private static void TickExplosions(
        GameShip owner,
        OwnerState state,
        float deltaTime)
    {
        if (PhysicsController.instance == null)
            return;

        float finalRadius = OrreryUnits.MetersToWorld(
            Mathf.Max(0f, OrrerySpellCompendium.Shatterbolt.ExplosionRadiusMeters));
        float expansionPerSecond = OrreryUnits.MetersToWorld(
            Mathf.Max(0f,
                OrrerySpellCompendium.Shatterbolt.ExplosionExpansionMetersPerSecond));
        float deltaRadius = expansionPerSecond * Mathf.Max(0f, deltaTime);

        for (int i = 0; i < state.Explosions.Length; i++)
        {
            ExplosionState explosion = state.Explosions[i];
            if (!explosion.Active)
                continue;

            explosion.RadiusWorld = Mathf.Min(
                finalRadius,
                explosion.RadiusWorld + deltaRadius);
            UpdateExplosionVisual(explosion);
            DamageExplosionTargets(owner, state, explosion);

            if (explosion.RadiusWorld >= finalRadius)
                FinishExplosion(explosion);
        }
    }

    private static void DamageExplosionTargets(
        GameShip owner,
        OwnerState state,
        ExplosionState explosion)
    {
        if (explosion.RadiusWorld <= 0f)
            return;

        Collider2D[] overlaps = PhysicsController.instance.OverlapCircle(
            explosion.Center,
            explosion.RadiusWorld);
        for (int i = 0; i < overlaps.Length; i++)
        {
            Collider2D collider = overlaps[i];
            if (collider == null)
                break;

            GameObject targetObject = collider.gameObject;
            if (targetObject.CompareTag("Shield") && targetObject.transform.parent != null)
                targetObject = targetObject.transform.parent.gameObject;

            Damageable damageable;
            if (!targetObject.TryGetComponent<Damageable>(out damageable) ||
                damageable == null || object.ReferenceEquals(damageable, owner) ||
                !damageable.CanBeDamagedBy(owner, false) ||
                HasExplosionHit(explosion, damageable))
            {
                continue;
            }

            if (explosion.HitCount >= explosion.HitTargets.Length)
                continue;

            GameShip targetShip = damageable as GameShip;
            if (targetShip != null && targetShip.IsDodging())
                continue;

            explosion.HitTargets[explosion.HitCount++] = damageable;
            DamageProfile profile = explosion.Damage;
            bool crit = Modifier.CritRoll(profile.CritChance, targetShip);
            float damage = crit
                ? profile.NeutralDamage * (1f + profile.CritModifier)
                : profile.NeutralDamage;
            state.ExplosionDamageScratch[0] = new Damageable.DamageData(
                damage,
                profile.DamageDps);

            OrreryDamageRouter.Route(
                owner,
                damageable,
                Damageable.DamageType.Cold,
                state.ExplosionDamageScratch,
                profile.StatusChance,
                crit,
                targetObject.transform.position,
                profile.BypassDamageLimit,
                0f,
                profile.Source);
        }
    }

    private static bool HasExplosionHit(
        ExplosionState explosion,
        Damageable target)
    {
        for (int i = 0; i < explosion.HitCount; i++)
        {
            if (object.ReferenceEquals(explosion.HitTargets[i], target))
                return true;
        }
        return false;
    }

    private static GameShip FindInitialTarget(
        GameShip owner,
        OwnerState state,
        Vector2 cursorPosition)
    {
        float range = OrreryUnits.MetersToWorld(
            Mathf.Max(0f,
                OrrerySpellCompendium.Shatterbolt.InitialAcquisitionRangeMeters));
        CollectCandidateShips(owner, state, owner.transform.position, range);

        GameShip best = null;
        float bestDistanceSquared = float.PositiveInfinity;
        for (int i = 0; i < state.CandidateCount; i++)
        {
            GameShip candidate = state.CandidateShips[i];
            if (!IsValidTarget(owner, candidate))
                continue;

            float distanceSquared =
                ((Vector2)candidate.transform.position - cursorPosition).sqrMagnitude;
            if (distanceSquared < bestDistanceSquared)
            {
                bestDistanceSquared = distanceSquared;
                best = candidate;
            }
        }

        ClearCandidates(state);
        return best;
    }

    private static GameShip FindChainTarget(
        GameShip owner,
        OwnerState state,
        Vector2 origin,
        GameShip currentTarget)
    {
        float range = OrreryUnits.MetersToWorld(
            Mathf.Max(0f, OrrerySpellCompendium.Shatterbolt.ChainRangeMeters));
        CollectCandidateShips(owner, state, origin, range);

        GameShip bestUnhit = null;
        float bestUnhitDistance = float.PositiveInfinity;
        GameShip bestRepeat = null;
        float bestRepeatDistance = float.PositiveInfinity;

        for (int i = 0; i < state.CandidateCount; i++)
        {
            GameShip candidate = state.CandidateShips[i];
            if (!IsValidTarget(owner, candidate) ||
                object.ReferenceEquals(candidate, currentTarget))
            {
                continue;
            }

            float distanceSquared =
                ((Vector2)candidate.transform.position - origin).sqrMagnitude;
            if (!HasDirectlyHit(state, candidate))
            {
                if (distanceSquared < bestUnhitDistance)
                {
                    bestUnhitDistance = distanceSquared;
                    bestUnhit = candidate;
                }
            }
            else if (distanceSquared < bestRepeatDistance)
            {
                bestRepeatDistance = distanceSquared;
                bestRepeat = candidate;
            }
        }

        ClearCandidates(state);
        return bestUnhit != null ? bestUnhit : bestRepeat;
    }

    private static void CollectCandidateShips(
        GameShip owner,
        OwnerState state,
        Vector2 center,
        float radiusWorld)
    {
        ClearCandidates(state);
        if (PhysicsController.instance == null || radiusWorld <= 0f)
            return;

        Collider2D[] overlaps = PhysicsController.instance.OverlapCircle(
            center,
            radiusWorld);
        for (int i = 0; i < overlaps.Length; i++)
        {
            Collider2D collider = overlaps[i];
            if (collider == null)
                break;

            GameObject targetObject = collider.gameObject;
            if (targetObject.CompareTag("Shield") && targetObject.transform.parent != null)
                targetObject = targetObject.transform.parent.gameObject;

            GameShip ship;
            if (!targetObject.TryGetComponent<GameShip>(out ship) ||
                !IsValidTarget(owner, ship) || ContainsCandidate(state, ship))
            {
                continue;
            }

            if (state.CandidateCount >= state.CandidateShips.Length)
                break;
            state.CandidateShips[state.CandidateCount++] = ship;
        }
    }

    private static bool ContainsCandidate(OwnerState state, GameShip ship)
    {
        for (int i = 0; i < state.CandidateCount; i++)
        {
            if (object.ReferenceEquals(state.CandidateShips[i], ship))
                return true;
        }
        return false;
    }

    private static void ClearCandidates(OwnerState state)
    {
        for (int i = 0; i < state.CandidateCount; i++)
            state.CandidateShips[i] = null;
        state.CandidateCount = 0;
    }

    private static bool IsValidTarget(GameShip owner, GameShip target)
    {
        return owner != null && target != null && target.gameObject != null &&
            target.gameObject.activeInHierarchy &&
            !object.ReferenceEquals(owner, target) &&
            target.CanBeDamagedBy(owner, false);
    }

    private static void RecordDirectTarget(OwnerState state, GameShip target)
    {
        if (target == null || HasDirectlyHit(state, target) ||
            state.DirectHistoryCount >= state.DirectHistory.Length)
        {
            return;
        }

        state.DirectHistory[state.DirectHistoryCount++] = target;
    }

    private static bool HasDirectlyHit(OwnerState state, GameShip target)
    {
        for (int i = 0; i < state.DirectHistoryCount; i++)
        {
            if (object.ReferenceEquals(state.DirectHistory[i], target))
                return true;
        }
        return false;
    }

    private static DamageProfile BuildDamageProfile(
        OrreryFocusResolver.Focus focus,
        Launcher source,
        float damageMultiplier)
    {
        DamageProfile result = default(DamageProfile);
        if (source == null || !focus.IsValid)
            return result;

        float referenceDps = OrrerySpellPower.GetReferenceDps(
            focus.EffectiveItemLevel,
            OrrerySpellPower.ReferenceMode.Mean);
        result.DamageDps = referenceDps * Mathf.Max(0f, damageMultiplier);
        float integratedDamage = result.DamageDps * Mathf.Max(
            0f,
            OrrerySpellCompendium.Shatterbolt.IntegratedReferenceSeconds);
        result.CritChance = Mathf.Clamp01(source.GetCritChance());
        result.CritModifier = Mathf.Max(0f, source.GetCritModifier());
        result.StatusChance = Mathf.Clamp01(source.GetStatusEffectChance());
        result.NeutralDamage = integratedDamage /
            Mathf.Max(0.01f, 1f + result.CritChance * result.CritModifier);
        result.BypassDamageLimit =
            source.HasCustomizer(Customizer.Type.BypassDamageLimit);
        result.Source = source;
        return result;
    }

    private static bool TryCreateSource(
        GameShip owner,
        OrreryElement element,
        string resourcePath,
        out OrreryFocusResolver.Focus focus,
        out Launcher launcher)
    {
        focus = default(OrreryFocusResolver.Focus);
        launcher = null;

        if (!OrreryFocusResolver.TryResolve(owner, element, out focus) ||
            !focus.IsValid)
        {
            return false;
        }

        ItemBase itemBase = Resources.Load<ItemBase>(resourcePath);
        if (itemBase == null)
        {
            Debug.LogError("[Orrery] Shatterbolt native donor not found: " + resourcePath);
            return false;
        }

        launcher = itemBase.GetItem(Item.Rarity.Common, 1, 0) as Launcher;
        if (launcher == null)
        {
            Debug.LogError("[Orrery] Shatterbolt donor is not a Launcher: " + resourcePath);
            return false;
        }

        launcher.heatPerSecond = 0f;
        launcher.SetFaction(owner.faction);
        launcher.Equip(owner, Placement.zero, focus.SlotIndex, false, false);
        launcher.ToggleVisbility(false);
        return true;
    }

    private static bool SpawnOrbVisual(OwnerState state, Vector2 position)
    {
        if (state.LightningSource == null || PoolController.instance == null)
            return false;

        GameObject prefab = state.LightningSource.GetProjectile();
        if (prefab == null)
            return false;

        GameObject visualObject = PoolController.instance.GetObject(
            prefab,
            position,
            Quaternion.identity,
            false);
        if (visualObject == null)
            return false;

        Projectile projectile;
        if (!visualObject.TryGetComponent<Projectile>(out projectile) ||
            projectile == null)
        {
            ReturnUnexpectedVisual(visualObject);
            return false;
        }

        OrbVisualState visual = new OrbVisualState();
        visual.Projectile = projectile;
        visual.ProjectileWasEnabled = projectile.enabled;
        visual.Body = projectile.rigidBody;
        visual.BodyWasSimulated = visual.Body != null && visual.Body.simulated;
        visual.Colliders = projectile.GetComponentsInChildren<Collider2D>(true);
        visual.ColliderEnabled = new bool[visual.Colliders.Length];
        visual.BaseScale = projectile.transform.localScale;

        projectile.enabled = false;
        if (visual.Body != null)
        {
            visual.Body.velocity = Vector2.zero;
            visual.Body.angularVelocity = 0f;
            visual.Body.simulated = false;
        }

        for (int i = 0; i < visual.Colliders.Length; i++)
        {
            Collider2D collider = visual.Colliders[i];
            if (collider == null)
                continue;
            visual.ColliderEnabled[i] = collider.enabled;
            collider.enabled = false;
        }

        projectile.transform.localScale = visual.BaseScale * Mathf.Max(
            0.01f,
            OrrerySpellCompendium.Shatterbolt.ProjectileVisualScale);
        state.OrbVisual = visual;
        return true;
    }

    private static void UpdateOrbVisual(OwnerState state, float deltaTime)
    {
        OrbVisualState visual = state.OrbVisual;
        if (visual == null || visual.Projectile == null)
            return;

        Transform transform = visual.Projectile.transform;
        Vector3 position = transform.position;
        transform.position = new Vector3(
            state.ProjectilePosition.x,
            state.ProjectilePosition.y,
            position.z);
        transform.Rotate(
            0f,
            0f,
            OrrerySpellCompendium.Shatterbolt.ProjectileSpinDegreesPerSecond *
                Mathf.Max(0f, deltaTime));
    }

    private static void CleanupOrbVisual(OwnerState state)
    {
        OrbVisualState visual = state == null ? null : state.OrbVisual;
        if (visual == null)
            return;

        Projectile projectile = visual.Projectile;
        if (projectile != null)
        {
            projectile.transform.localScale = visual.BaseScale;
            if (visual.Body != null)
            {
                visual.Body.velocity = Vector2.zero;
                visual.Body.angularVelocity = 0f;
                visual.Body.simulated = visual.BodyWasSimulated;
            }

            if (visual.Colliders != null && visual.ColliderEnabled != null)
            {
                int count = Mathf.Min(
                    visual.Colliders.Length,
                    visual.ColliderEnabled.Length);
                for (int i = 0; i < count; i++)
                {
                    Collider2D collider = visual.Colliders[i];
                    if (collider != null)
                        collider.enabled = visual.ColliderEnabled[i];
                }
            }

            projectile.enabled = visual.ProjectileWasEnabled;
            projectile.PoolDestroy();
        }

        state.OrbVisual = null;
    }

    private static void SpawnExplosionVisual(
        OwnerState state,
        ExplosionState explosion)
    {
        if (PoolController.instance == null)
            return;

        PulseItemBase frostNova = Resources.Load<PulseItemBase>(FrostNovaPath);
        GameObject visualPrefab = frostNova == null ? null : frostNova.wave;
        if (visualPrefab == null)
        {
            Debug.LogError(
                "[Orrery] Shatterbolt Frost Nova presentation prefab was not found.");
            return;
        }

        GameObject visualObject = PoolController.instance.GetObject(
            visualPrefab,
            explosion.Center,
            Utils.RandomRotation(),
            false);
        if (visualObject == null)
            return;

        Wave wave;
        if (!visualObject.TryGetComponent<Wave>(out wave) || wave == null)
        {
            ReturnUnexpectedVisual(visualObject);
            return;
        }

        CircleCollider2D circle;
        if (!visualObject.TryGetComponent<CircleCollider2D>(out circle) ||
            circle == null || circle.radius <= 0f)
        {
            ReturnUnexpectedVisual(visualObject);
            return;
        }

        explosion.Visual = wave;
        explosion.VisualWasEnabled = wave.enabled;
        explosion.VisualCollider = circle;
        explosion.VisualColliderWasEnabled = circle.enabled;
        explosion.VisualColliderRadius = circle.radius;
        explosion.VisualBaseScale = wave.transform.localScale;

        // This is presentation only. Native Wave.FixedUpdate performs its own
        // collision/damage, so disable both native behavior and collider and let
        // Shatterbolt's bounded mechanical expansion own gameplay.
        wave.enabled = false;
        circle.enabled = false;
        wave.transform.localScale = Vector3.zero;
    }

    private static void ReturnUnexpectedVisual(GameObject visualObject)
    {
        if (visualObject == null)
            return;

        PoolableObject poolable;
        if (visualObject.TryGetComponent<PoolableObject>(out poolable) &&
            poolable != null)
        {
            poolable.PoolDestroy();
            return;
        }

        Object.Destroy(visualObject);
    }

    private static void UpdateExplosionVisual(ExplosionState explosion)
    {
        if (explosion.Visual == null || explosion.VisualColliderRadius <= 0f)
            return;

        float visualScale = Mathf.Max(
            0.01f,
            OrrerySpellCompendium.Shatterbolt.ExplosionVisualScale);
        float scale = Mathf.Max(
            0.001f,
            explosion.RadiusWorld /
                explosion.VisualColliderRadius *
                visualScale);
        explosion.Visual.transform.localScale =
            new Vector3(scale, scale, scale);
    }

    private static void FinishExplosion(ExplosionState explosion)
    {
        CleanupExplosionVisual(explosion);
        explosion.Active = false;
        explosion.RadiusWorld = 0f;
        explosion.HitCount = 0;
        System.Array.Clear(
            explosion.HitTargets,
            0,
            explosion.HitTargets.Length);
    }

    private static void CleanupExplosionVisual(ExplosionState explosion)
    {
        if (explosion == null || explosion.Visual == null)
        {
            if (explosion != null)
            {
                explosion.VisualCollider = null;
                explosion.VisualColliderRadius = 0f;
            }
            return;
        }

        explosion.Visual.transform.localScale = explosion.VisualBaseScale;
        if (explosion.VisualCollider != null)
            explosion.VisualCollider.enabled = explosion.VisualColliderWasEnabled;
        explosion.Visual.enabled = explosion.VisualWasEnabled;
        explosion.Visual.PoolDestroy();
        explosion.Visual = null;
        explosion.VisualCollider = null;
        explosion.VisualColliderRadius = 0f;
    }

    private static void ClearExplosions(OwnerState state)
    {
        for (int i = 0; i < state.Explosions.Length; i++)
        {
            ExplosionState explosion = state.Explosions[i];
            CleanupExplosionVisual(explosion);
            explosion.Active = false;
            explosion.RadiusWorld = 0f;
            explosion.HitCount = 0;
            System.Array.Clear(
                explosion.HitTargets,
                0,
                explosion.HitTargets.Length);
        }
    }

    private static bool HasActiveExplosions(OwnerState state)
    {
        for (int i = 0; i < state.Explosions.Length; i++)
        {
            if (state.Explosions[i].Active)
                return true;
        }
        return false;
    }

    private static void CompleteCast(GameShip owner, OwnerState state)
    {
        if (!state.CastActive)
            return;

        state.CastActive = false;
        state.CurrentTarget = null;
        CleanupOrbVisual(state);

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

        OrreryNetwork.PublishLocal(owner);
        OrreryController.StartShuffle(owner);

        if (!HasActiveExplosions(state))
            CleanupIdleState(owner, state);
    }

    private static void AbortCast(GameShip owner, OwnerState state)
    {
        state.CastActive = false;
        state.CurrentTarget = null;
        CleanupOrbVisual(state);
        ClearExplosions(state);
        OrreryCasting.Cancel(owner);
        OrreryNetwork.PublishLocal(owner);
        OrreryController.StartShuffle(owner);
        CleanupIdleState(owner, state);
    }

    private static void CleanupIdleState(GameShip owner, OwnerState state)
    {
        if (state.CastActive || HasActiveExplosions(state))
            return;

        DisposeSources(state);
        OrreryDamageRouter.Forget(owner);
        owners.Remove(owner);
    }

    private static void ResetCastFields(OwnerState state)
    {
        state.Invocation = default(OrreryCastInvocation);
        state.CastActive = false;
        state.CurrentTarget = null;
        state.ProjectilePosition = Vector2.zero;
        state.LegElapsedSeconds = 0f;
        state.ImpactCount = 0;
        for (int i = 0; i < state.ImpactPositions.Length; i++)
            state.ImpactPositions[i] = Vector2.zero;
        for (int i = 0; i < state.DirectHistoryCount; i++)
            state.DirectHistory[i] = null;
        state.DirectHistoryCount = 0;
        ClearCandidates(state);
    }

    private static void DisposeSources(OwnerState state)
    {
        if (state == null)
            return;
        DisposeSource(state.LightningSource);
        DisposeSource(state.IceSource);
        state.LightningSource = null;
        state.IceSource = null;
        state.LightningFocus = default(OrreryFocusResolver.Focus);
        state.IceFocus = default(OrreryFocusResolver.Focus);
        state.LightningDamage = default(DamageProfile);
        state.IceDamage = default(DamageProfile);
    }

    private static void DisposeSource(Launcher launcher)
    {
        if (launcher == null)
            return;

        try
        {
            launcher.Deactivate();
            launcher.Unequip();
        }
        catch (System.Exception ex)
        {
            Debug.LogError("[Orrery] Failed to dispose Shatterbolt donor: " + ex);
        }
    }

    private static Vector2 GetAimPoint(GameShip owner)
    {
        InputController input = InputController.instance;
        if (input != null && owner != null &&
            object.ReferenceEquals(input.controlShip, owner))
        {
            return input.GetCursorWorldPoint();
        }

        return owner == null
            ? Vector2.zero
            : (Vector2)owner.transform.position +
                (Vector2)owner.transform.right * 10f;
    }
}

/// <summary>
/// Temporary bridge while the original FF/II/LL runtime is still monolithic.
/// New per-spell files use this fixed-step hook; when the old spells migrate the
/// bridge can become the ordinary Orrery spell driver instead of proliferating
/// one MonoBehaviour per spell.
/// </summary>
[HarmonyPatch(typeof(OrreryController), "FixedUpdate")]
public static class OrreryShatterboltFixedTickPatch
{
    public static void Postfix()
    {
        OrreryShatterbolt.TickLocal(Time.fixedDeltaTime);
    }
}

[HarmonyPatch(typeof(WorldController), "OnDestroy")]
public static class OrreryShatterboltWorldDestroyedPatch
{
    public static void Prefix()
    {
        OrreryShatterbolt.Reset();
    }
}
