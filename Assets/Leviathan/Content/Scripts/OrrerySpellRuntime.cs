using HarmonyLib;
using StarVortex;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

/// <summary>
/// Bounded native-backed spell execution for Orrery V0.
///
/// Native item families remain responsible where they are genuinely useful:
/// Inferno supplies an explosive thermal projectile, Cryo supplies the visual
/// projectile family, and Tesla supplies native beam/chain behavior. Orrery owns
/// formula lifetime, reference-DPS normalization, custom cone selection, guided
/// steering and post-cast completion.
/// </summary>
public static class OrrerySpellRuntime
{
    public static class Tuning
    {
        public const float FireballIntegratedReferenceSeconds = 1f;
        public const float FireballDamageMultiplier = 1f;
        public const float FireballVelocityMultiplier = 0.65f;
        public const float FireballTurnDegreesPerSecond = 120f;
        public const float FireballProjectileVisualScale = 2f;
        public const float FireballExplosionRadiusMeters = 40f;
        public const float FireballLifetimeSeconds = 2f;
        public const float FireballSpawnGraceSeconds = 0.15f;

        public const float CryoIntegratedReferenceSeconds = 1f;
        public const float CryoDamageMultiplier = 1f;
        public const float CryoConeRangeMeters = 7.5f;
        public const float CryoConeAngleDegrees = 70f;
        public const float CryoFreezeChanceAdditive = 0.50f;
        public const int CryoVisualProjectileCount = 9;
        public const int CryoVisualSpreadDegrees = 60;
        public const float CryoVisualVelocityMultiplier = 1.30f;
        public const float CryoVisualProjectileScale = 1f;

        public const float TeslaInitialDpsMultiplier = 2f;
        public const float TeslaMinimumDpsMultiplier = 1f;
        public const float TeslaFadeSeconds = 1f;
        public const float TeslaRangeMultiplier = 1f;
        public const float TeslaBeamWidthMultiplier = 1f;
        public const float TeslaChainRangeMultiplier = 1f;
        public const int TeslaChainCountAdjustment = 0;
    }

    private const string InfernoCannonPath = "Base/Items/PrimaryWeapon/Inferno Cannon";
    private const string CryoGunPath = "Base/Items/PrimaryWeapon/Cryo Gun";
    private const string TeslaCoilPath = "Base/Items/PrimaryWeapon/Tesla Coil";

    private enum ActiveSpellKind : byte
    {
        None = 0,
        Fireball = 1,
        CryoVisualBurst = 2,
        TeslaChannel = 3
    }

    private sealed class VirtualWeapon
    {
        public Activatable Weapon;
        public Activatable FocusSource;
        public int FocusSlotIndex;
        public int EffectiveItemLevel;
        public OrreryElement Element;
        public float LogicalCritChance;
        public float LogicalCritModifier;
        public float LogicalStatusEffectChance;
    }

    private sealed class ActiveCast
    {
        public OrreryCastInvocation Invocation;
        public VirtualWeapon VirtualWeapon;
        public ActiveSpellKind Kind;
        public Projectile Projectile;
        public float ElapsedSeconds;
        public bool EmitterStopped;
        public bool ReleaseRequested;
        public float AppliedTeslaMultiplier = 1f;
    }

    private sealed class OwnerState
    {
        public VirtualWeapon Inferno;
        public VirtualWeapon Cryo;
        public VirtualWeapon Tesla;
        public ActiveCast Active;
        public readonly HashSet<Damageable> ConeTargets =
            new HashSet<Damageable>();
        public readonly Damageable.DamageData[] DamageScratch =
            new Damageable.DamageData[1];
        public readonly object[] RouteDamageArguments = new object[14];
    }

    private static readonly Dictionary<GameShip, OwnerState> owners =
        new Dictionary<GameShip, OwnerState>(4);

    private static MethodInfo routeDamageMethod;
    private static bool routeDamageMethodResolved;

    public static bool ExecuteMagma(
        GameShip owner,
        OrreryCastInvocation invocation,
        OrrerySpellRegistry.SpellDefinition spell)
    {
        OwnerState state;
        VirtualWeapon virtualWeapon;
        if (!TryPrepare(
                owner,
                invocation,
                OrreryElement.Fire,
                spell,
                InfernoCannonPath,
                out state,
                out virtualWeapon))
        {
            return false;
        }

        Launcher launcher = virtualWeapon.Weapon as Launcher;
        if (launcher == null || !launcher.CanActivate())
            return false;

        AimAtCursor(owner, virtualWeapon);
        state.Active = new ActiveCast
        {
            Invocation = invocation,
            VirtualWeapon = virtualWeapon,
            Kind = ActiveSpellKind.Fireball
        };
        launcher.Activate();
        return true;
    }

    public static bool ExecuteCryo(
        GameShip owner,
        OrreryCastInvocation invocation,
        OrrerySpellRegistry.SpellDefinition spell)
    {
        OwnerState state;
        VirtualWeapon virtualWeapon;
        if (!TryPrepare(
                owner,
                invocation,
                OrreryElement.Ice,
                spell,
                CryoGunPath,
                out state,
                out virtualWeapon))
        {
            return false;
        }

        ChargingLauncher cryo = virtualWeapon.Weapon as ChargingLauncher;
        if (cryo == null || !cryo.CanActivate() || !EnsureNativeDamageRouter())
            return false;

        if (!ApplyCryoCone(owner, state, virtualWeapon))
            return false;

        AimAtCursor(owner, virtualWeapon);
        state.Active = new ActiveCast
        {
            Invocation = invocation,
            VirtualWeapon = virtualWeapon,
            Kind = ActiveSpellKind.CryoVisualBurst
        };
        cryo.Activate();
        return true;
    }

    public static bool ExecuteTesla(
        GameShip owner,
        OrreryCastInvocation invocation,
        OrrerySpellRegistry.SpellDefinition spell)
    {
        OwnerState state;
        VirtualWeapon virtualWeapon;
        if (!TryPrepare(
                owner,
                invocation,
                OrreryElement.Lightning,
                spell,
                TeslaCoilPath,
                out state,
                out virtualWeapon))
        {
            return false;
        }

        BeamWeapon beam = virtualWeapon.Weapon as BeamWeapon;
        if (beam == null || !beam.CanActivate())
            return false;

        float initialMultiplier = Mathf.Max(0f, Tuning.TeslaInitialDpsMultiplier);
        if (initialMultiplier <= 0f)
            return false;

        AimAtCursor(owner, virtualWeapon);
        beam.ScaleDamage(initialMultiplier);
        state.Active = new ActiveCast
        {
            Invocation = invocation,
            VirtualWeapon = virtualWeapon,
            Kind = ActiveSpellKind.TeslaChannel,
            AppliedTeslaMultiplier = initialMultiplier
        };
        beam.Activate();
        return true;
    }

    /// <summary>
    /// Handles the semantic RMB-release edge after a cast has committed. Cryo is
    /// instantaneous and therefore has no release interaction. Fireball release
    /// detonates; Tesla release ends its otherwise unbounded channel.
    /// </summary>
    public static bool ReleaseInvoke(GameShip owner)
    {
        OwnerState state;
        if (owner == null || !owners.TryGetValue(owner, out state) ||
            state == null || state.Active == null)
        {
            return false;
        }

        ActiveCast active = state.Active;
        if (active.Kind == ActiveSpellKind.Fireball)
        {
            active.ReleaseRequested = true;
            if (active.Projectile != null)
                DetonateFireball(owner, state, active);
            return true;
        }

        if (active.Kind == ActiveSpellKind.TeslaChannel)
        {
            CompleteAndShuffle(owner, state, active);
            return true;
        }

        return false;
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

        ActiveCast active = state.Active;
        if (active != null && active.VirtualWeapon != null)
            AimAtCursor(owner, active.VirtualWeapon);

        if (active != null && active.Kind == ActiveSpellKind.TeslaChannel)
            UpdateTeslaDamage(active);

        TickWeapon(state.Inferno);
        TickWeapon(state.Cryo);
        TickWeapon(state.Tesla);

        active = state.Active;
        if (active == null)
            return;

        if (active.Invocation.Execution == null ||
            !active.Invocation.Execution.IsValid)
        {
            AbortAndShuffle(owner, state, active);
            return;
        }

        active.ElapsedSeconds += Mathf.Max(0f, deltaTime);

        switch (active.Kind)
        {
            case ActiveSpellKind.Fireball:
                TickFireball(owner, state, active, deltaTime);
                break;

            case ActiveSpellKind.CryoVisualBurst:
                if (!active.EmitterStopped)
                {
                    Deactivate(active.VirtualWeapon);
                    active.EmitterStopped = true;
                }
                CompleteAndShuffle(owner, state, active);
                break;

            case ActiveSpellKind.TeslaChannel:
                // Held indefinitely. RMB release owns completion.
                break;
        }
    }

    public static void LateTick(GameShip owner)
    {
        OwnerState state;
        if (owner == null || !owners.TryGetValue(owner, out state) || state == null)
            return;

        BeamWeapon beam = state.Tesla == null
            ? null
            : state.Tesla.Weapon as BeamWeapon;
        if (beam != null)
            beam.LateUpdate();
    }

    /// <summary>
    /// Launcher.AddProjectile postfix entry point. The native launcher remains the
    /// projectile factory/lifecycle authority; Orrery only captures the one live
    /// FF projectile so it can steer/detonate it after launch. Hidden adapters do
    /// not occupy real native slots, so remote projectile presentation is a
    /// separate Orrery networking concern.
    /// </summary>
    public static void OnProjectileAdded(Launcher launcher, Projectile projectile)
    {
        if (launcher == null || projectile == null)
            return;

        foreach (KeyValuePair<GameShip, OwnerState> pair in owners)
        {
            OwnerState state = pair.Value;
            ActiveCast active = state == null ? null : state.Active;
            if (active == null || active.Kind != ActiveSpellKind.Fireball ||
                active.VirtualWeapon == null ||
                !object.ReferenceEquals(active.VirtualWeapon.Weapon, launcher))
            {
                continue;
            }

            if (active.Projectile == null)
                active.Projectile = projectile;
            return;
        }
    }

    /// <summary>
    /// Native ExplosiveProjectile intentionally excludes the directly struck
    /// object from its AoE loop. FF deliberately wants both components on that
    /// target, so this adds one explosion-equivalent packet after the native hit.
    /// </summary>
    public static void OnExplosiveProjectileHit(
        ExplosiveProjectile projectile,
        GameObject hitObject,
        Vector2 hitPoint)
    {
        if (projectile == null || hitObject == null)
            return;

        foreach (KeyValuePair<GameShip, OwnerState> pair in owners)
        {
            GameShip owner = pair.Key;
            OwnerState state = pair.Value;
            ActiveCast active = state == null ? null : state.Active;
            if (active == null || active.Kind != ActiveSpellKind.Fireball ||
                !object.ReferenceEquals(active.Projectile, projectile))
            {
                continue;
            }

            Launcher launcher = active.VirtualWeapon == null
                ? null
                : active.VirtualWeapon.Weapon as Launcher;
            if (launcher == null || launcher.ExplosiveRadius <= 0f ||
                !EnsureNativeDamageRouter())
            {
                return;
            }

            GameObject targetObject = hitObject;
            if (targetObject.CompareTag("Shield") && targetObject.transform.parent != null)
                targetObject = targetObject.transform.parent.gameObject;

            Damageable damageable;
            if (!targetObject.TryGetComponent<Damageable>(out damageable) ||
                damageable == null || !damageable.CanBeDamagedBy(owner, false))
            {
                return;
            }

            GameShip targetShip = damageable as GameShip;
            if (targetShip != null && targetShip.IsDodging())
                return;

            bool crit = Modifier.CritRoll(launcher.GetCritChance(), targetShip);
            RouteNativeDamage(
                state,
                damageable,
                launcher.damageType,
                launcher.GetDamageData(crit, false),
                launcher.GetStatusEffectChance(),
                crit,
                hitPoint,
                owner,
                launcher.HasCustomizer(Customizer.Type.BypassDamageLimit),
                launcher.ApplyModifierToPercentage(Modifier.Type.Knockback, 0f, true),
                launcher);
            return;
        }
    }

    public static void Forget(GameShip owner)
    {
        if (owner == null)
            return;

        OwnerState state;
        if (!owners.TryGetValue(owner, out state) || state == null)
            return;

        if (state.Active != null)
            StopActiveWeapon(state.Active);

        Dispose(state.Inferno);
        Dispose(state.Cryo);
        Dispose(state.Tesla);
        owners.Remove(owner);
    }

    public static void Reset()
    {
        GameShip[] keys = new GameShip[owners.Count];
        owners.Keys.CopyTo(keys, 0);
        for (int i = 0; i < keys.Length; i++)
            Forget(keys[i]);
        owners.Clear();
    }

    private static bool TryPrepare(
        GameShip owner,
        OrreryCastInvocation invocation,
        OrreryElement element,
        OrrerySpellRegistry.SpellDefinition spell,
        string resourcePath,
        out OwnerState state,
        out VirtualWeapon virtualWeapon)
    {
        state = null;
        virtualWeapon = null;

        if (owner == null || spell == null || invocation.Execution == null ||
            !invocation.Execution.IsValid || !OrreryRuntime.IsActive(owner) ||
            OrreryController.IsShuffling(owner))
        {
            return false;
        }

        OrreryFocusResolver.Focus focus;
        if (!OrreryFocusResolver.TryResolve(owner, element, out focus) ||
            !focus.IsValid)
        {
            Debug.LogWarning("[Orrery] " + spell.Name +
                " requires an equipped " + element + " damage focus.");
            return false;
        }

        if (!owners.TryGetValue(owner, out state) || state == null)
        {
            state = new OwnerState();
            owners[owner] = state;
        }

        if (state.Active != null)
            return false;

        return TryEnsureVirtualWeapon(
            owner,
            state,
            spell.Id,
            element,
            focus,
            resourcePath,
            out virtualWeapon);
    }

    private static bool TryEnsureVirtualWeapon(
        GameShip owner,
        OwnerState state,
        ushort spellId,
        OrreryElement element,
        OrreryFocusResolver.Focus focus,
        string resourcePath,
        out VirtualWeapon result)
    {
        result = GetVirtualWeapon(state, spellId);
        if (Matches(result, focus, element))
            return true;

        Dispose(result);
        result = null;

        ItemBase itemBase = Resources.Load<ItemBase>(resourcePath);
        if (itemBase == null)
        {
            Debug.LogError("[Orrery] Native spell reference not found: " + resourcePath);
            SetVirtualWeapon(state, spellId, null);
            return false;
        }

        Activatable weapon = itemBase.GetItem(Item.Rarity.Common, 1, 0) as Activatable;
        if (weapon == null)
        {
            Debug.LogError("[Orrery] Native spell reference is not Activatable: " + resourcePath);
            SetVirtualWeapon(state, spellId, null);
            return false;
        }

        weapon.heatPerSecond = 0f;
        weapon.SetFaction(owner.faction);
        weapon.Equip(owner, Placement.zero, focus.SlotIndex, false, false);
        weapon.ToggleVisbility(false);

        result = new VirtualWeapon
        {
            Weapon = weapon,
            FocusSource = focus.Source,
            FocusSlotIndex = focus.SlotIndex,
            EffectiveItemLevel = focus.EffectiveItemLevel,
            Element = element
        };
        CaptureLogicalCombatStats(result);

        if (!ConfigureOutput(spellId, result, focus))
        {
            Dispose(result);
            result = null;
            SetVirtualWeapon(state, spellId, null);
            return false;
        }

        // Preserve native pool sizing before Orrery removes/changes cadence and
        // presentation shot-count knobs. Cryo's native pool is already large due
        // to its 30 Hz baseline, so the one-frame 9-projectile burst stays bounded.
        weapon.PopulatePool();
        NormalizeSpellCadenceAndPresentation(spellId, weapon);
        SetVirtualWeapon(state, spellId, result);
        return true;
    }

    private static void CaptureLogicalCombatStats(VirtualWeapon virtualWeapon)
    {
        Launcher launcher = virtualWeapon == null
            ? null
            : virtualWeapon.Weapon as Launcher;
        if (launcher == null)
            return;

        virtualWeapon.LogicalCritChance = launcher.GetCritChance();
        virtualWeapon.LogicalCritModifier = launcher.GetCritModifier();
        virtualWeapon.LogicalStatusEffectChance = launcher.GetStatusEffectChance();
    }

    private static bool ConfigureOutput(
        ushort spellId,
        VirtualWeapon virtualWeapon,
        OrreryFocusResolver.Focus focus)
    {
        float referenceDps = OrrerySpellPower.GetReferenceDps(
            focus.EffectiveItemLevel,
            OrrerySpellPower.ReferenceMode.Mean);

        if (spellId == 1)
        {
            Launcher launcher = virtualWeapon.Weapon as Launcher;
            if (launcher == null)
                return false;

            float expectedLocalHit = launcher.LocalDamage *
                Mathf.Max(1, launcher.LocalShotCount) *
                (1f + launcher.LocalCritChance * launcher.LocalCritModifier);
            if (expectedLocalHit <= 0f)
                return false;

            float targetIntegratedDamage = referenceDps *
                Tuning.FireballIntegratedReferenceSeconds *
                Tuning.FireballDamageMultiplier;
            launcher.ScaleDamage(targetIntegratedDamage / expectedLocalHit);
            return true;
        }

        if (spellId == 2)
        {
            BeamWeapon beam = virtualWeapon.Weapon as BeamWeapon;
            if (beam == null)
                return false;

            float localDps = beam.CalculateDPS(Activatable.Modified.Local);
            if (localDps <= 0f)
                return false;

            // Cache remains normalized at 1.0x reference DPS. Active LL casts
            // apply their 2.0 -> 1.0 envelope multiplicatively and restore 1.0x
            // when the channel ends.
            beam.ScaleDamage(referenceDps / localDps);
            return true;
        }

        if (spellId == 3)
        {
            ChargingLauncher cryo = virtualWeapon.Weapon as ChargingLauncher;
            if (cryo == null)
                return false;

            // Cryo projectiles are presentation only. The one mechanical cone hit
            // is routed separately through native NetCombat using a bounded custom
            // packet, so these visuals must carry neither damage nor status.
            cryo.unchargedMulitplier = 1f;
            cryo.BaseDamage = 0f;
            cryo.BaseStatusEffectChance = 0f;
            cryo.BaseCritChance = 0f;
            return true;
        }

        return false;
    }

    private static void NormalizeSpellCadenceAndPresentation(
        ushort spellId,
        Activatable weapon)
    {
        if (spellId == 1)
        {
            Launcher launcher = weapon as Launcher;
            if (launcher == null)
                return;

            launcher.BaseReloadTime = 0f;
            launcher.BaseVelocity *= Tuning.FireballVelocityMultiplier;
            // ExplosiveProjectile.Explode uses this same radius for both the
            // mechanical overlap and native ExplosiveArea prefab scale, so the
            // 40 m gameplay radius and visible explosion stay coupled by design.
            launcher.BaseExplosiveRadius =
                OrreryUnits.MetersToWorld(Tuning.FireballExplosionRadiusMeters);
            launcher.BaseAutoDestroyTime = Tuning.FireballLifetimeSeconds;
            return;
        }

        if (spellId == 2)
        {
            BeamWeapon beam = weapon as BeamWeapon;
            if (beam == null)
                return;

            beam.BaseMaxRange *= Mathf.Max(0f, Tuning.TeslaRangeMultiplier);
            beam.BaseChainRange *= Mathf.Max(0f, Tuning.TeslaChainRangeMultiplier);
            beam.BaseChainTargets = Mathf.Max(
                0,
                beam.BaseChainTargets + Tuning.TeslaChainCountAdjustment);
            return;
        }

        if (spellId == 3)
        {
            ChargingLauncher cryo = weapon as ChargingLauncher;
            if (cryo == null)
                return;

            cryo.BaseReloadTime = 0f;
            cryo.BaseShotCount = Mathf.Max(1, Tuning.CryoVisualProjectileCount);
            cryo.shotAngle = Mathf.Max(0, Tuning.CryoVisualSpreadDegrees);
            cryo.BaseVelocity *= Tuning.CryoVisualVelocityMultiplier;
        }
    }

    private static void TickFireball(
        GameShip owner,
        OwnerState state,
        ActiveCast active,
        float deltaTime)
    {
        if (!active.EmitterStopped)
        {
            Deactivate(active.VirtualWeapon);
            active.EmitterStopped = true;
        }

        Projectile projectile = active.Projectile;
        if (projectile == null)
        {
            if (active.ElapsedSeconds >= Tuning.FireballSpawnGraceSeconds)
                AbortAndShuffle(owner, state, active);
            return;
        }

        if (active.ReleaseRequested)
        {
            DetonateFireball(owner, state, active);
            return;
        }

        if (projectile.hasExploded || projectile.gameObject == null ||
            !projectile.gameObject.activeInHierarchy)
        {
            CompleteAndShuffle(owner, state, active);
            return;
        }

        Rigidbody2D body = projectile.rigidBody;
        if (body == null)
            return;

        Vector2 aimPoint = GetAimPoint(owner);
        Vector2 toAim = aimPoint - body.position;
        if (toAim.sqrMagnitude <= 0.0001f)
            return;

        Vector2 currentVelocity = body.velocity;
        float speed = Mathf.Max(
            0.1f,
            (active.VirtualWeapon.Weapon as Launcher).Velocity);
        float currentAngle = currentVelocity.sqrMagnitude > 0.0001f
            ? Mathf.Atan2(currentVelocity.y, currentVelocity.x) * Mathf.Rad2Deg
            : projectile.transform.rotation.eulerAngles.z;
        float targetAngle = Mathf.Atan2(toAim.y, toAim.x) * Mathf.Rad2Deg;
        float nextAngle = Mathf.MoveTowardsAngle(
            currentAngle,
            targetAngle,
            Mathf.Max(0f, Tuning.FireballTurnDegreesPerSecond) *
                Mathf.Max(0f, deltaTime));
        float radians = nextAngle * Mathf.Deg2Rad;
        body.velocity = new Vector2(Mathf.Cos(radians), Mathf.Sin(radians)) * speed;
    }

    private static void DetonateFireball(
        GameShip owner,
        OwnerState state,
        ActiveCast active)
    {
        Projectile projectile = active.Projectile;
        if (projectile != null && !projectile.hasExploded)
        {
            ExplosiveProjectile explosive = projectile as ExplosiveProjectile;
            if (explosive != null)
                explosive.explodeOnExpiry = true;
            projectile.TimedDestroy();
        }

        CompleteAndShuffle(owner, state, active);
    }

    private static bool ApplyCryoCone(
        GameShip owner,
        OwnerState state,
        VirtualWeapon virtualWeapon)
    {
        if (PhysicsController.instance == null || virtualWeapon == null)
            return false;

        Launcher source = virtualWeapon.Weapon as Launcher;
        if (source == null)
            return false;

        float referenceDps = OrrerySpellPower.GetReferenceDps(
            virtualWeapon.EffectiveItemLevel,
            OrrerySpellPower.ReferenceMode.Mean);
        float damageDps = referenceDps * Tuning.CryoDamageMultiplier;
        float expectedIntegratedDamage = damageDps *
            Tuning.CryoIntegratedReferenceSeconds;
        float critChance = Mathf.Clamp01(virtualWeapon.LogicalCritChance);
        float critModifier = Mathf.Max(0f, virtualWeapon.LogicalCritModifier);
        float neutralDamage = expectedIntegratedDamage /
            Mathf.Max(0.01f, 1f + critChance * critModifier);
        float statusChance = Mathf.Clamp01(
            virtualWeapon.LogicalStatusEffectChance +
            Tuning.CryoFreezeChanceAdditive);

        Vector2 origin = owner.transform.position;
        Vector2 forward = GetAimPoint(owner) - origin;
        if (forward.sqrMagnitude <= 0.0001f)
            forward = owner.transform.right;
        forward.Normalize();

        float rangeWorldUnits =
            OrreryUnits.MetersToWorld(Tuning.CryoConeRangeMeters);
        float halfAngle = Mathf.Clamp(
            Tuning.CryoConeAngleDegrees,
            0f,
            360f) * 0.5f;
        Collider2D[] overlaps = PhysicsController.instance.OverlapCircle(
            origin,
            rangeWorldUnits);
        state.ConeTargets.Clear();

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
                damageable == null || !state.ConeTargets.Add(damageable) ||
                object.ReferenceEquals(damageable, owner) ||
                !damageable.CanBeDamagedBy(owner, false))
            {
                continue;
            }

            GameShip targetShip = damageable as GameShip;
            if (targetShip != null && targetShip.IsDodging())
                continue;

            Vector2 toTarget =
                (Vector2)targetObject.transform.position - origin;
            if (toTarget.sqrMagnitude > 0.0001f &&
                Vector2.Angle(forward, toTarget) > halfAngle)
            {
                continue;
            }

            bool crit = Modifier.CritRoll(critChance, targetShip);
            float damage = crit
                ? neutralDamage * (1f + critModifier)
                : neutralDamage;
            state.DamageScratch[0] = new Damageable.DamageData(
                damage,
                damageDps);

            RouteNativeDamage(
                state,
                damageable,
                Damageable.DamageType.Cold,
                state.DamageScratch,
                statusChance,
                crit,
                targetObject.transform.position,
                owner,
                source.HasCustomizer(Customizer.Type.BypassDamageLimit),
                0f,
                source);
        }

        state.ConeTargets.Clear();
        return true;
    }

    private static void UpdateTeslaDamage(ActiveCast active)
    {
        BeamWeapon beam = active == null || active.VirtualWeapon == null
            ? null
            : active.VirtualWeapon.Weapon as BeamWeapon;
        if (beam == null)
            return;

        float fadeSeconds = Mathf.Max(0.001f, Tuning.TeslaFadeSeconds);
        float t = Mathf.Clamp01(active.ElapsedSeconds / fadeSeconds);
        float desired = Mathf.Lerp(
            Tuning.TeslaInitialDpsMultiplier,
            Tuning.TeslaMinimumDpsMultiplier,
            t);
        desired = Mathf.Max(0f, desired);

        float applied = Mathf.Max(0.0001f, active.AppliedTeslaMultiplier);
        if (!Mathf.Approximately(applied, desired))
        {
            beam.ScaleDamage(desired / applied);
            active.AppliedTeslaMultiplier = desired;
        }
    }

    private static void CompleteAndShuffle(
        GameShip owner,
        OwnerState state,
        ActiveCast active)
    {
        if (state == null || active == null ||
            !object.ReferenceEquals(state.Active, active))
        {
            return;
        }

        StopActiveWeapon(active);
        state.Active = null;

        if (active.Invocation.Execution != null &&
            active.Invocation.Execution.IsValid)
        {
            OrreryCasting.CompleteInvocation(owner, active.Invocation.Execution, 0);
        }
        else
        {
            OrreryCasting.Cancel(owner);
        }

        OrreryNetwork.PublishLocal(owner);
        OrreryController.StartShuffle(owner);
    }

    private static void AbortAndShuffle(
        GameShip owner,
        OwnerState state,
        ActiveCast active)
    {
        if (state == null || active == null ||
            !object.ReferenceEquals(state.Active, active))
        {
            return;
        }

        StopActiveWeapon(active);
        state.Active = null;
        OrreryCasting.Cancel(owner);
        OrreryNetwork.PublishLocal(owner);
        OrreryController.StartShuffle(owner);
    }

    private static void StopActiveWeapon(ActiveCast active)
    {
        if (active == null)
            return;

        if (active.Kind == ActiveSpellKind.TeslaChannel &&
            active.VirtualWeapon != null)
        {
            BeamWeapon beam = active.VirtualWeapon.Weapon as BeamWeapon;
            if (beam != null && active.AppliedTeslaMultiplier > 0f &&
                !Mathf.Approximately(active.AppliedTeslaMultiplier, 1f))
            {
                beam.ScaleDamage(1f / active.AppliedTeslaMultiplier);
            }
            active.AppliedTeslaMultiplier = 1f;
        }

        Deactivate(active.VirtualWeapon);
    }

    private static void TickWeapon(VirtualWeapon virtualWeapon)
    {
        if (virtualWeapon != null && virtualWeapon.Weapon != null)
            virtualWeapon.Weapon.FixedUpdate();
    }

    private static void AimAtCursor(GameShip owner, VirtualWeapon virtualWeapon)
    {
        if (owner == null || virtualWeapon == null || virtualWeapon.Weapon == null)
            return;

        Vector2 delta = GetAimPoint(owner) - (Vector2)owner.transform.position;
        Quaternion rotation;
        if (delta.sqrMagnitude > 0.0001f)
        {
            rotation = Quaternion.Euler(
                0f,
                0f,
                Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
        }
        else if (virtualWeapon.FocusSource != null &&
            virtualWeapon.FocusSource.gameObject != null)
        {
            rotation = virtualWeapon.FocusSource.gameObject.transform.rotation;
        }
        else
        {
            rotation = owner.transform.rotation;
        }

        virtualWeapon.Weapon.AimAt(rotation);
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

    private static void Deactivate(VirtualWeapon virtualWeapon)
    {
        if (virtualWeapon != null && virtualWeapon.Weapon != null)
            virtualWeapon.Weapon.Deactivate();
    }

    private static void Dispose(VirtualWeapon virtualWeapon)
    {
        if (virtualWeapon == null || virtualWeapon.Weapon == null)
            return;

        try
        {
            virtualWeapon.Weapon.Deactivate();
            virtualWeapon.Weapon.Unequip();
        }
        catch (System.Exception ex)
        {
            Debug.LogError("[Orrery] Failed to dispose virtual spell weapon: " + ex);
        }
        virtualWeapon.Weapon = null;
    }

    private static bool Matches(
        VirtualWeapon virtualWeapon,
        OrreryFocusResolver.Focus focus,
        OrreryElement element)
    {
        return virtualWeapon != null && virtualWeapon.Weapon != null &&
            virtualWeapon.Element == element &&
            object.ReferenceEquals(virtualWeapon.FocusSource, focus.Source) &&
            virtualWeapon.FocusSlotIndex == focus.SlotIndex &&
            virtualWeapon.EffectiveItemLevel == focus.EffectiveItemLevel;
    }

    private static VirtualWeapon GetVirtualWeapon(OwnerState state, ushort spellId)
    {
        if (spellId == 1) return state.Inferno;
        if (spellId == 2) return state.Tesla;
        if (spellId == 3) return state.Cryo;
        return null;
    }

    private static void SetVirtualWeapon(
        OwnerState state,
        ushort spellId,
        VirtualWeapon virtualWeapon)
    {
        if (spellId == 1) state.Inferno = virtualWeapon;
        else if (spellId == 2) state.Tesla = virtualWeapon;
        else if (spellId == 3) state.Cryo = virtualWeapon;
    }

    private static bool EnsureNativeDamageRouter()
    {
        if (routeDamageMethodResolved)
            return routeDamageMethod != null;

        routeDamageMethodResolved = true;
        MethodInfo[] methods = typeof(NetCombat).GetMethods(
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        for (int i = 0; i < methods.Length; i++)
        {
            MethodInfo method = methods[i];
            if (method.Name != "RouteDamage")
                continue;

            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length == 14 &&
                parameters[0].ParameterType.IsAssignableFrom(typeof(Damageable)) &&
                parameters[1].ParameterType == typeof(Damageable.DamageType) &&
                parameters[2].ParameterType == typeof(Damageable.DamageData[]))
            {
                routeDamageMethod = method;
                break;
            }
        }

        if (routeDamageMethod == null)
            Debug.LogError("[Orrery] Could not resolve native NetCombat.RouteDamage signature.");
        return routeDamageMethod != null;
    }

    private static bool RouteNativeDamage(
        OwnerState state,
        Damageable damageable,
        Damageable.DamageType damageType,
        Damageable.DamageData[] damageData,
        float statusEffectChance,
        bool crit,
        Vector2 fromPosition,
        GameShip fromShip,
        bool bypassDamageLimit,
        float knockbackPower,
        Activatable slotSource)
    {
        if (state == null || damageable == null ||
            !EnsureNativeDamageRouter())
        {
            return false;
        }

        object[] args = state.RouteDamageArguments;
        args[0] = damageable;
        args[1] = damageType;
        args[2] = damageData;
        args[3] = statusEffectChance;
        args[4] = crit;
        args[5] = fromPosition;
        args[6] = fromShip;
        args[7] = bypassDamageLimit;
        args[8] = knockbackPower;
        args[9] = slotSource;
        args[10] = 0f;
        args[11] = 0f;
        args[12] = false;
        args[13] = 0f;

        try
        {
            object result = routeDamageMethod.Invoke(null, args);
            return result is bool && (bool)result;
        }
        catch (System.Exception ex)
        {
            Debug.LogError("[Orrery] Native damage routing failed: " + ex);
            return false;
        }
    }
}

/// <summary>
/// Capture projectiles after the native launcher has fully initialized and added
/// them to its own lifecycle list. No projectile spawning path is duplicated.
/// </summary>
[HarmonyPatch(typeof(Launcher), "AddProjectile")]
public static class OrreryLauncherProjectileCapturePatch
{
    public static void Postfix(Launcher __instance, Projectile projectile)
    {
        OrrerySpellRuntime.OnProjectileAdded(__instance, projectile);
    }
}

/// <summary>
/// FF wants its directly struck target to receive the native direct packet and an
/// explosion packet. ExplosiveProjectile's own AoE intentionally skips that one
/// object, so Orrery adds only the missing packet and leaves every other target to
/// the native explosion implementation.
/// </summary>
[HarmonyPatch(typeof(ExplosiveProjectile), "HitObject")]
public static class OrreryFireballDirectExplosionPatch
{
    public static void Postfix(
        ExplosiveProjectile __instance,
        GameObject hitObject,
        Vector2 hitPoint,
        ref bool __result)
    {
        if (__result)
            OrrerySpellRuntime.OnExplosiveProjectileHit(
                __instance,
                hitObject,
                hitPoint);
    }
}