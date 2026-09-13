using StarVortex;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Fire + Lightning mixed Orrery spell.
///
/// Fires one wide Electric stroke. The first valid hostile intersected by the
/// swept circle takes the direct hit. Once CoreCombat confirms the authoritative
/// amount actually lost by that target, that exact amount becomes the authored
/// Thermal budget of Plasma Burn. Plasma Burn lasts five seconds, recursively
/// spreads to nearby hostiles, and uses a separate ten-second reinfection lockout.
///
/// Gameplay is source-owner authoritative. Presentation spawned here is local;
/// remote bolt/burn presentation uses bounded snapshots and never applies damage.
/// </summary>
public static class OrreryPlasmaBolt
{
    // Hidden source supplies native slot context only. Actual focus stats come
    // from OrreryFocusProfile; no native projectile is fired.
    private const string LightningDonorPath =
        "Base/Items/SecondaryWeapon/Lightning Orb Launcher";

    private struct DamageProfile
    {
        public float DamageDps;
        public float NeutralDamage;
        public float CritChance;
        public float CritModifier;
        public float StatusChance;
        public bool BypassDamageLimit;
        public Activatable Source;
    }

    private struct ResolvedGeometry
    {
        public float LengthMeters;
        public float WidthMeters;
        public float SpreadRadiusMeters;
    }

    private struct PendingImpact
    {
        public bool Active;
        public uint EventId;
        public GameShip Target;
        public float ExpiresAtUnscaled;
        public Vector2 Position;
        public float SpreadRadiusMeters;
        public ushort CastId;
        public CoreCombat.ContributorKey FireContributor;
    }

    private sealed class Infection
    {
        public bool Active;
        public bool VisualRetiring;
        public GameShip Target;
        public float TotalBudget;
        public float RemainingBudget;
        public float AppliedAt;
        public float ExpiresAt;
        public float NextTickAt;
        public float NextSpreadAt;
        public int TicksApplied;
        public float SpreadRadiusMeters;
        public ushort CastId;
        public CoreCombat.ContributorKey FireContributor;
        public StatusEffectLayer Visual;
        public float ReleaseVisualAt;
    }

    internal sealed class BoltVisualState
    {
        public GameObject BoltVisualObject;
        public LineRenderer BoltOuter, BoltCore;
        public ParticleSystem[] ZapParticles;
        public ParticleSystemRenderer[] ZapRenderers;
        public readonly Vector3[] BoltPoints = new Vector3[OrrerySpellCompendium.PlasmaBolt.BoltVisualPointCount];
        public float BoltVisibleUntil;
    }

    private sealed class OwnerState
    {
        public bool CompletionPending;
        public OrreryCastInvocation CompletionInvocation;

        public bool HistoryPrimed;
        public CoreCombat.CombatEntityKey OwnerKey;
        public ulong HistoryCursor;

        public readonly PendingImpact[] PendingImpacts =
            new PendingImpact[OrrerySpellCompendium.PlasmaBolt.MaxPendingImpacts];
        public readonly Infection[] Infections =
            new Infection[OrrerySpellCompendium.PlasmaBolt.MaxActiveInfections];

        public readonly Damageable.DamageData[] DirectDamageScratch =
            new Damageable.DamageData[1];
        public readonly Damageable.DamageData[] BurnDamageScratch =
            new Damageable.DamageData[1];

        public readonly BoltVisualState Bolt = new BoltVisualState();
        public ushort BoltSequence;
        public Vector2 BoltStart, BoltEnd;
        public float BoltWidthMeters, BoltPublishedUntil;
        public int PresentationCursor;

        public OwnerState()
        {
            // Infection objects are fixed-capacity runtime state. Preallocate them
            // so a large contagion wave cannot turn the spread hot path into a
            // burst of managed allocations.
            for (int i = 0; i < Infections.Length; i++)
                Infections[i] = new Infection();
        }
    }

    private static readonly Dictionary<GameShip, OwnerState> owners =
        new Dictionary<GameShip, OwnerState>(4);
    private static Material boltMaterial;
    private static GameObject zapPrefab;
    private static bool warnedMissingZap;
    private static bool warnedMissingMeaningfulHistory;

    // Six active targets per actual network send. At 20Hz even a full 64-target
    // population refreshes in <=0.55s. No per-tick event log or unbounded queue.
    internal static bool ReadPresentation(uint[] targets, byte[] remaining,
        out ushort sequence, out Vector2 start, out Vector2 end, out float width,
        out bool bolt, out int count)
    {
        sequence = 0; start = end = Vector2.zero; width = 0f; bolt = false; count = 0;
        CoreOwnerContext context = CoreClassRuntime.CurrentContext;
        OwnerState state;
        if (context == null || !context.IsValid || !OrreryRuntime.IsActive(context.Ship) ||
            !owners.TryGetValue(context.Ship, out state))
            return false;
        sequence = state.BoltSequence;
        start = state.BoltStart; end = state.BoltEnd; width = state.BoltWidthMeters;
        bolt = Time.time < state.BoltPublishedUntil;
        for (int visited = 0; visited < state.Infections.Length && count < targets.Length; visited++)
        {
            Infection infection = state.Infections[state.PresentationCursor];
            state.PresentationCursor = (state.PresentationCursor + 1) % state.Infections.Length;
            if (!infection.Active || infection.Target == null || infection.Target.netId == 0 ||
                Time.time >= infection.ExpiresAt)
                continue;
            targets[count] = infection.Target.netId;
            remaining[count] = (byte)Mathf.Clamp(
                Mathf.CeilToInt((infection.ExpiresAt - Time.time) * 20f), 1, 100);
            count++;
        }
        return bolt || count > 0;
    }

    public static void ForgetTarget(GameShip target)
    {
        foreach (OwnerState state in owners.Values)
        {
            for (int i = 0; i < state.Infections.Length; i++)
            {
                if (object.ReferenceEquals(state.Infections[i].Target, target))
                    ClearInfection(null, state.Infections[i], true);
            }
        }
    }

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

        OwnerState state = GetOrCreateState(owner);
        if (state == null || state.CompletionPending)
            return false;

        PrimeHistory(owner, state);

        OrreryFocusProfile.Resolved lightningFocus;
        if (!OrreryFocusProfile.TryResolve(
                owner,
                OrreryElement.Lightning,
                out lightningFocus) ||
            !lightningFocus.IsValid)
        {
            return false;
        }

        // Lightning owns strike power/length; Fire range owns spread reach.
        // The confirmed burn budget is never damage-scaled a second time.
        OrreryFocusProfile.Resolved fireFocus;
        OrreryFocusProfile.TryResolve(owner, OrreryElement.Fire, out fireFocus);

        Launcher source;
        if (!TryCreateLightningSource(owner, lightningFocus.Focus, out source))
            return false;

        bool committed = false;
        try
        {
            DamageProfile damage = BuildDamageProfile(lightningFocus, source);
            ResolvedGeometry geometry = ResolveGeometry(
                owner,
                fireFocus,
                lightningFocus);

            Vector2 origin = owner.transform.position;
            Vector2 aimPoint = GetAimPoint(owner);
            Vector2 direction = aimPoint - origin;
            if (direction.sqrMagnitude <= 0.000001f)
                direction = owner.transform.right;
            direction.Normalize();

            float lengthWorld = OrreryUnits.MetersToWorld(
                Mathf.Max(0f, geometry.LengthMeters));
            float radiusWorld = OrreryUnits.MetersToWorld(
                Mathf.Max(0f, geometry.WidthMeters * 0.5f));

            Vector2 visualEnd = origin + direction * lengthWorld;
            Vector2 impactPoint;
            GameShip target = FindFirstTarget(
                owner,
                origin,
                direction,
                radiusWorld,
                lengthWorld,
                out impactPoint);

            if (target != null)
                visualEnd = impactPoint;

            ShowBoltVisual(state.Bolt, origin, visualEnd, geometry.WidthMeters);
            state.BoltSequence++;
            state.BoltStart = origin;
            state.BoltEnd = visualEnd;
            state.BoltWidthMeters = geometry.WidthMeters;
            state.BoltPublishedUntil = Time.time + 0.75f;

            if (target != null && !target.IsDodging())
                RouteInitialHit(owner, state, target, impactPoint, damage, invocation, geometry.SpreadRadiusMeters);

            // Completion cannot happen from inside SpellRegistry.TryCommit():
            // ending the CoreAbilityExecution while TryCommit is still on-stack
            // makes TryCommit report failure. Defer only the formula completion
            // and shuffle to the next fixed tick; the spell itself already fired.
            state.CompletionInvocation = invocation;
            state.CompletionPending = true;
            committed = true;
            return true;
        }
        finally
        {
            DisposeSource(source);

            if (!committed)
            {
                state.CompletionInvocation = default(OrreryCastInvocation);
                state.CompletionPending = false;
            }
        }
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

        float now = Time.time;
        float nowUnscaled = Time.unscaledTime;

        HideExpiredBoltVisual(state.Bolt, now);
        DrainInitialHitOutcomes(owner, state);
        PrunePendingImpacts(state, nowUnscaled);
        TickInfections(owner, state, now);
        CompleteCommittedCast(owner, state);
    }

    public static void Forget(GameShip owner)
    {
        if (object.ReferenceEquals(owner, null))
            return;

        OwnerState state;
        if (owners.TryGetValue(owner, out state) && state != null)
        {
            for (int i = 0; i < state.Infections.Length; i++)
                ClearInfection(owner, state.Infections[i], true);

            DestroyBoltVisual(state.Bolt);
            owners.Remove(owner);
        }
    }

    public static void Reset()
    {
        GameShip[] keys = new GameShip[owners.Count];
        owners.Keys.CopyTo(keys, 0);
        for (int i = 0; i < keys.Length; i++)
            Forget(keys[i]);
        owners.Clear();

        if (boltMaterial != null)
        {
            Object.Destroy(boltMaterial);
            boltMaterial = null;
        }
        zapPrefab = null;
        warnedMissingZap = false;
    }

    // ---------------------------------------------------------------------
    // Initial stroke
    // ---------------------------------------------------------------------

    private static void RouteInitialHit(
        GameShip owner,
        OwnerState state,
        GameShip target,
        Vector2 impactPoint,
        DamageProfile profile,
        OrreryCastInvocation invocation,
        float spreadRadiusMeters)
    {
        bool crit = CoreNativeCriticalHits.CritRoll(profile.CritChance, target);
        float damage = crit
            ? profile.NeutralDamage * (1f + profile.CritModifier)
            : profile.NeutralDamage;

        state.DirectDamageScratch[0] = new Damageable.DamageData(
            damage,
            profile.DamageDps);

        CoreCombat.DamageScope scope = CoreCombat.BeginDamage(
            owner,
            target,
            OrreryCombat.PlasmaBolt,
            OrreryCombat.SatelliteFor(invocation, OrreryElement.Lightning),
            CoreCombat.AcknowledgementMode.NativeResult,
            CoreCombat.TrackingFlags.MeaningfulOutcome,
            (ushort)invocation.Sequence, owner);

        try
        {
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
        finally
        {
            CoreCombat.EndDamage(scope);
        }

        if (scope.EventId != 0U)
            AddPendingImpact(state, scope.EventId, target, impactPoint, spreadRadiusMeters, invocation);
        else if (!warnedMissingMeaningfulHistory)
        {
            warnedMissingMeaningfulHistory = true;
            Debug.LogWarning(
                "[Orrery] Plasma Bolt could not track its direct-hit outcome; " +
                "Plasma Burn was not guessed from pre-mitigation damage.");
        }
    }

    private static GameShip FindFirstTarget(
        GameShip owner,
        Vector2 origin,
        Vector2 direction,
        float radiusWorld,
        float lengthWorld,
        out Vector2 impactPoint)
    {
        impactPoint = origin + direction * lengthWorld;
        if (PhysicsController.instance == null || radiusWorld < 0f || lengthWorld <= 0f)
            return null;

        RaycastHit2D[] hits = PhysicsController.instance.CircleCast(
            origin,
            radiusWorld,
            direction,
            lengthWorld);

        GameShip best = null;
        float bestDistance = float.PositiveInfinity;
        Vector2 bestPoint = impactPoint;

        for (int i = 0; i < hits.Length; i++)
        {
            RaycastHit2D hit = hits[i];
            if (hit.collider == null)
                break;

            GameShip candidate = ResolveShip(hit.collider.gameObject);
            if (!IsValidHostile(owner, candidate))
                continue;

            // CircleCastNonAlloc does not promise useful ordering here. Choose the
            // nearest valid hostile explicitly rather than trusting array order.
            if (hit.distance < bestDistance)
            {
                bestDistance = hit.distance;
                best = candidate;
                bestPoint = hit.point;
            }
        }

        if (best != null)
            impactPoint = bestPoint;
        return best;
    }

    private static DamageProfile BuildDamageProfile(
        OrreryFocusProfile.Resolved focus,
        Launcher source)
    {
        DamageProfile result = default(DamageProfile);
        if (source == null || !focus.IsValid)
            return result;

        float referenceDps = focus.GetReferenceDps(OrrerySpellPower.ReferenceMode.Mean);
        result.DamageDps = focus.ApplySpellDamageBonus(referenceDps *
            OrrerySpellCompendium.PlasmaBolt.LightningDamageMultiplier);
        result.NeutralDamage = result.DamageDps *
            OrrerySpellCompendium.PlasmaBolt.IntegratedReferenceSeconds;
        result.CritChance = focus.CritChance;
        result.CritModifier = focus.CritModifier;
        result.StatusChance = focus.StatusChance;
        result.BypassDamageLimit = focus.BypassDamageLimit;
        result.Source = focus.HasFocus ? focus.Donor : source;
        return result;
    }

    private static ResolvedGeometry ResolveGeometry(
        GameShip owner,
        OrreryFocusProfile.Resolved fireFocus,
        OrreryFocusProfile.Resolved lightningFocus)
    {
        ResolvedGeometry geometry = new ResolvedGeometry();
        geometry.LengthMeters = lightningFocus.ApplyRangeBonus(OrrerySpellCompendium.PlasmaBolt.BoltLengthMeters);
        geometry.WidthMeters = OrrerySpellCompendium.PlasmaBolt.BoltWidthMeters;
        geometry.SpreadRadiusMeters =
            fireFocus.ApplyRangeBonus(OrrerySpellCompendium.PlasmaBolt.SpreadRadiusMeters);

        return geometry;
    }

    // ---------------------------------------------------------------------
    // Exact initial-hit snapshot -> Plasma Burn
    // ---------------------------------------------------------------------

    private static void PrimeHistory(GameShip owner, OwnerState state)
    {
        if (state.HistoryPrimed)
            return;

        if (!CoreCombat.TryGetEntityKey(owner, out state.OwnerKey) ||
            !state.OwnerKey.IsValid)
        {
            return;
        }
        state.HistoryPrimed = true;

        // Start at the current tail so Plasma Bolt never mistakes an old retained
        // event for a new impact. This one-time drain is bounded by the shared
        // 1024-event owner ring and does not run in the steady-state hit path.
        CoreCombatHistory.MeaningfulEvent ignored;
        while (CoreCombatHistory.TryReadNextMeaningfulEvent(
            state.OwnerKey,
            ref state.HistoryCursor,
            out ignored))
        {
        }
    }

    private static void DrainInitialHitOutcomes(GameShip owner, OwnerState state)
    {
        if (!state.HistoryPrimed || !state.OwnerKey.IsValid)
            return;

        CoreCombatHistory.MeaningfulEvent evt;
        while (CoreCombatHistory.TryReadNextMeaningfulEvent(
            state.OwnerKey,
            ref state.HistoryCursor,
            out evt))
        {
            if (evt.Kind != CoreCombatHistory.MeaningfulEventKind.CombatOutcome ||
                !evt.Semantic.Equals(OrreryCombat.PlasmaBolt) ||
                evt.EventId == 0U)
            {
                continue;
            }

            int pendingIndex = FindPendingImpact(state, evt.EventId);
            if (pendingIndex < 0)
                continue;

            PendingImpact pending = state.PendingImpacts[pendingIndex];
            GameShip target = pending.Target;
            state.PendingImpacts[pendingIndex] = default(PendingImpact);

            float confirmedDamage = Mathf.Max(0f, evt.HealthDamage) +
                Mathf.Max(0f, evt.ShieldDamage);
            bool damaged =
                (evt.Outcomes & CoreCombat.OutcomeFlags.Damaged) != 0;
            bool destroyed =
                (evt.Outcomes & CoreCombat.OutcomeFlags.Destroyed) != 0;

            if (!damaged || confirmedDamage <= 0f)
                continue;
            // Captured position survives destruction of the original target.
            if (destroyed)
                SpreadAt(owner, state, pending.Position, null, confirmedDamage,
                    evt.OccurredAt, pending.SpreadRadiusMeters, pending.CastId, pending.FireContributor);
            else if (IsValidHostile(owner, target))
                TryApplyBurn(owner, state, target, confirmedDamage,
                    evt.OccurredAt, evt.EventId, pending.SpreadRadiusMeters,
                    pending.CastId, pending.FireContributor);
        }
    }

    private static void AddPendingImpact(
        OwnerState state,
        uint eventId,
        GameShip target, Vector2 position, float spreadRadiusMeters,
        OrreryCastInvocation invocation)
    {
        int free = -1;
        int oldest = -1;
        float oldestExpiry = float.PositiveInfinity;

        for (int i = 0; i < state.PendingImpacts.Length; i++)
        {
            PendingImpact pending = state.PendingImpacts[i];
            if (!pending.Active)
            {
                free = i;
                break;
            }

            if (pending.ExpiresAtUnscaled < oldestExpiry)
            {
                oldestExpiry = pending.ExpiresAtUnscaled;
                oldest = i;
            }
        }

        int index = free >= 0 ? free : oldest;
        if (index < 0)
            return;

        PendingImpact next = new PendingImpact();
        next.Active = true;
        next.EventId = eventId;
        next.Target = target;
        next.Position = position;
        next.SpreadRadiusMeters = spreadRadiusMeters;
        next.CastId = (ushort)invocation.Sequence;
        next.FireContributor = OrreryCombat.SatelliteFor(invocation, OrreryElement.Fire);
        next.ExpiresAtUnscaled = Time.unscaledTime +
            OrrerySpellCompendium.PlasmaBolt.PendingOutcomeTimeoutSeconds;
        state.PendingImpacts[index] = next;
    }

    private static int FindPendingImpact(OwnerState state, uint eventId)
    {
        for (int i = 0; i < state.PendingImpacts.Length; i++)
        {
            if (state.PendingImpacts[i].Active &&
                state.PendingImpacts[i].EventId == eventId)
            {
                return i;
            }
        }
        return -1;
    }

    private static void PrunePendingImpacts(OwnerState state, float nowUnscaled)
    {
        for (int i = 0; i < state.PendingImpacts.Length; i++)
        {
            if (state.PendingImpacts[i].Active &&
                nowUnscaled >= state.PendingImpacts[i].ExpiresAtUnscaled)
            {
                state.PendingImpacts[i] = default(PendingImpact);
            }
        }
    }

    // ---------------------------------------------------------------------
    // Plasma Burn lifecycle / spread
    // ---------------------------------------------------------------------

    private static bool TryApplyBurn(
        GameShip owner,
        OwnerState state,
        GameShip target,
        float frozenBudget,
        float occurredAt,
        uint authoredEventId, float spreadRadiusMeters, ushort castId,
        CoreCombat.ContributorKey fireContributor)
    {
        if (!IsValidHostile(owner, target) || frozenBudget <= 0f)
            return false;

        if (CoreCombatState.Has(
            owner,
            target,
            OrreryCombat.PlasmaBurnRecent,
            CoreCombatState.Scope.OwnerTarget))
        {
            return false;
        }

        float age = Mathf.Max(0f, Time.time - occurredAt);
        float burnDuration = OrrerySpellCompendium.PlasmaBolt.BurnDurationSeconds;
        float lockoutDuration =
            OrrerySpellCompendium.PlasmaBolt.ReinfectionLockoutSeconds;

        // A very late confirmation can arrive after the five-second burn window.
        // Preserve the ten-second "has had Plasma Burn" history without inventing
        // catch-up gameplay after the debuff itself should already be gone.
        if (age >= burnDuration)
        {
            if (age < lockoutDuration)
            {
                CoreCombatState.ApplyAt(
                    owner,
                    target,
                    OrreryCombat.PlasmaBurnRecent,
                    CoreCombatState.Scope.OwnerTarget,
                    occurredAt,
                    authoredEventId,
                    lockoutDuration,
                    1,
                    frozenBudget);
            }
            return false;
        }

        Infection infection = FindFreeInfection(state);
        if (infection == null)
            return false;

        if (!CoreCombatState.ApplyAt(
            owner,
            target,
            OrreryCombat.PlasmaBurn,
            CoreCombatState.Scope.OwnerTarget,
            occurredAt,
            authoredEventId,
            burnDuration,
            1,
            frozenBudget))
        {
            return false;
        }

        if (!CoreCombatState.ApplyAt(
            owner,
            target,
            OrreryCombat.PlasmaBurnRecent,
            CoreCombatState.Scope.OwnerTarget,
            occurredAt,
            authoredEventId,
            lockoutDuration,
            1,
            frozenBudget))
        {
            CoreCombatState.Remove(
                owner,
                target,
                OrreryCombat.PlasmaBurn,
                CoreCombatState.Scope.OwnerTarget);
            return false;
        }

        infection.Active = true;
        infection.VisualRetiring = false;
        infection.Target = target;
        infection.TotalBudget = frozenBudget;
        infection.RemainingBudget = frozenBudget;
        infection.AppliedAt = occurredAt;
        infection.ExpiresAt = occurredAt +
            OrrerySpellCompendium.PlasmaBolt.BurnDurationSeconds;
        infection.NextTickAt = occurredAt +
            OrrerySpellCompendium.PlasmaBolt.BurnTickIntervalSeconds;
        infection.NextSpreadAt = Mathf.Max(Time.time, occurredAt) +
            OrrerySpellCompendium.PlasmaBolt.SpreadScanIntervalSeconds;
        infection.TicksApplied = 0;
        infection.SpreadRadiusMeters = spreadRadiusMeters;
        infection.CastId = castId;
        infection.FireContributor = fireContributor;
        infection.Visual = SpawnFauxBurnVisual(target);
        infection.ReleaseVisualAt = 0f;
        return true;
    }

    private static Infection FindFreeInfection(OwnerState state)
    {
        for (int i = 0; i < state.Infections.Length; i++)
        {
            Infection infection = state.Infections[i];
            if (infection != null && !infection.Active &&
                !infection.VisualRetiring)
            {
                return infection;
            }
        }
        return null;
    }

    private static void TickInfections(
        GameShip owner,
        OwnerState state,
        float now)
    {
        // Phase 1: damage and expiry. New infections created by spread below never
        // get a burn tick in the same phase that created them.
        for (int i = 0; i < state.Infections.Length; i++)
        {
            Infection infection = state.Infections[i];
            if (infection == null)
                continue;

            if (infection.VisualRetiring)
            {
                TickRetiringVisual(infection, now);
                continue;
            }

            if (!infection.Active)
                continue;

            if (!IsValidHostile(owner, infection.Target))
            {
                ClearInfection(owner, infection, true);
                continue;
            }

            TickBurnDamage(owner, state, infection, now);

            if (infection.TicksApplied >=
                    OrrerySpellCompendium.PlasmaBolt.BurnTickCount ||
                now >= infection.ExpiresAt)
            {
                ClearInfection(owner, infection, false);
            }
        }

        // Phase 2: spread. New infections have a future NextSpreadAt, so they
        // cannot recurse through the entire graph in this same scan loop.
        for (int i = 0; i < state.Infections.Length; i++)
        {
            Infection infection = state.Infections[i];
            if (infection == null || !infection.Active ||
                now < infection.NextSpreadAt)
            {
                continue;
            }

            infection.NextSpreadAt = now +
                OrrerySpellCompendium.PlasmaBolt.SpreadScanIntervalSeconds;
            SpreadFrom(owner, state, infection, now);
        }
    }

    private static void TickBurnDamage(
        GameShip owner,
        OwnerState state,
        Infection infection,
        float now)
    {
        int maxTicks = OrrerySpellCompendium.PlasmaBolt.BurnTickCount;
        float interval = OrrerySpellCompendium.PlasmaBolt.BurnTickIntervalSeconds;
        if (maxTicks <= 0 || interval <= 0f)
            return;

        // Catch up authored ticks after ordinary network jitter rather than
        // extending the debuff duration on the source owner. The loop is hard
        // bounded by BurnTickCount (10 by default).
        while (infection.Active && infection.TicksApplied < maxTicks &&
            now >= infection.NextTickAt)
        {
            float tickDamage;
            if (infection.TicksApplied >= maxTicks - 1)
            {
                tickDamage = infection.RemainingBudget;
            }
            else
            {
                tickDamage = Mathf.Min(
                    infection.RemainingBudget,
                    infection.TotalBudget / maxTicks);
            }

            if (tickDamage > 0f)
            {
                float burnDps = infection.TotalBudget /
                    Mathf.Max(0.01f,
                        OrrerySpellCompendium.PlasmaBolt.BurnDurationSeconds);
                state.BurnDamageScratch[0] = new Damageable.DamageData(
                    tickDamage,
                    burnDps);

                CoreCombat.DamageScope scope = CoreCombat.BeginDamage(
                    owner,
                    infection.Target,
                    OrreryCombat.PlasmaBurnTick,
                    infection.FireContributor,
                    CoreCombat.AcknowledgementMode.NativeResult,
                    CoreCombat.TrackingFlags.Summary, infection.CastId, owner);
                try
                {
                    // Faux burn never rerolls the original crit/status/proc state.
                    // The frozen amount is merely divided into Thermal packets.
                    OrreryDamageRouter.Route(
                        owner,
                        infection.Target,
                        Damageable.DamageType.Thermal,
                        state.BurnDamageScratch,
                        0f,
                        false,
                        infection.Target.transform.position,
                        false,
                        0f,
                        null);
                }
                finally
                {
                    CoreCombat.EndDamage(scope);
                }

                infection.RemainingBudget = Mathf.Max(
                    0f,
                    infection.RemainingBudget - tickDamage);
            }

            infection.TicksApplied++;
            infection.NextTickAt += interval;
        }
    }

    private static void SpreadFrom(
        GameShip owner,
        OwnerState state,
        Infection source,
        float now)
    {
        if (PhysicsController.instance == null || source == null ||
            !source.Active || source.Target == null)
        {
            return;
        }

        SpreadAt(owner, state, source.Target.transform.position, source.Target,
            source.TotalBudget, now, source.SpreadRadiusMeters, source.CastId, source.FireContributor);
    }

    private static void SpreadAt(GameShip owner, OwnerState state, Vector2 center,
        GameShip sourceTarget, float totalBudget, float now, float spreadRadiusMeters,
        ushort castId, CoreCombat.ContributorKey fireContributor)
    {
        if (PhysicsController.instance == null)
            return;
        float radiusWorld = OrreryUnits.MetersToWorld(spreadRadiusMeters);
        Collider2D[] overlaps = PhysicsController.instance.OverlapCircle(
            center,
            radiusWorld);

        for (int i = 0; i < overlaps.Length; i++)
        {
            Collider2D collider = overlaps[i];
            if (collider == null)
                break;

            GameShip candidate = ResolveShip(collider.gameObject);
            if (!IsValidHostile(owner, candidate) ||
                object.ReferenceEquals(candidate, sourceTarget))
            {
                continue;
            }

            // Every descendant receives the ORIGINAL frozen payload, not whatever
            // happens to remain on the source infection at the moment of spread.
            TryApplyBurn(
                owner,
                state,
                candidate,
                totalBudget,
                now,
                0U, spreadRadiusMeters, castId, fireContributor);
        }
    }

    private static void ClearInfection(
        GameShip owner,
        Infection infection,
        bool immediateVisual)
    {
        if (infection == null)
            return;

        if (infection.Active && owner != null && infection.Target != null)
        {
            CoreCombatState.Remove(
                owner,
                infection.Target,
                OrreryCombat.PlasmaBurn,
                CoreCombatState.Scope.OwnerTarget);
        }

        infection.Active = false;
        infection.TotalBudget = 0f;
        infection.RemainingBudget = 0f;
        infection.AppliedAt = 0f;
        infection.ExpiresAt = 0f;
        infection.NextTickAt = 0f;
        infection.NextSpreadAt = 0f;
        infection.TicksApplied = 0;

        if (infection.Visual != null)
        {
            if (immediateVisual)
            {
                infection.Visual.Deactivate();
                infection.Visual.PoolDestroy();
                infection.Visual = null;
                infection.VisualRetiring = false;
                infection.ReleaseVisualAt = 0f;
                infection.Target = null;
            }
            else
            {
                infection.Visual.Deactivate();
                infection.VisualRetiring = true;
                infection.ReleaseVisualAt = Time.time + StatusEffectLayer.totalFadeTime;
            }
        }
        else
        {
            infection.VisualRetiring = false;
            infection.ReleaseVisualAt = 0f;
            infection.Target = null;
        }
    }

    private static void TickRetiringVisual(Infection infection, float now)
    {
        if (infection == null || !infection.VisualRetiring)
            return;

        if (infection.Visual == null || now >= infection.ReleaseVisualAt)
        {
            if (infection.Visual != null)
            {
                infection.Visual.Deactivate();
                infection.Visual.PoolDestroy();
            }
            infection.Visual = null;
            infection.VisualRetiring = false;
            infection.ReleaseVisualAt = 0f;
            infection.Target = null;
        }
    }

    // ---------------------------------------------------------------------
    // Presentation
    // ---------------------------------------------------------------------

    internal static void ShowBoltVisual(
        BoltVisualState state,
        Vector2 start,
        Vector2 end,
        float widthMeters)
    {
        if (widthMeters > 0f)
        {
            CoreAudioRuntime.PlayPositionalOneShot(
                OrrerySpellCompendium.PlasmaBolt.ThunderClipName,
                start,
                OrrerySpellCompendium.PlasmaBolt.ThunderVolume,
                "Orrery Plasma Bolt Thunder");
        }

        if (ShowZapVisual(state, start, end, widthMeters))
            return;

        if (!EnsureBoltVisual(state))
            return;

        Vector2 delta = end - start;
        Vector2 direction = delta.sqrMagnitude <= 0.000001f
            ? Vector2.right
            : delta.normalized;
        Vector2 perpendicular = new Vector2(-direction.y, direction.x);
        float jitterWorld = OrreryUnits.MetersToWorld(
            OrrerySpellCompendium.PlasmaBolt.BoltVisualJitterMeters);

        int count = state.BoltPoints.Length;
        for (int i = 0; i < count; i++)
        {
            float t = count <= 1 ? 0f : (float)i / (count - 1);
            float envelope = Mathf.Sin(t * Mathf.PI);
            float jitter = Random.Range(-jitterWorld, jitterWorld) * envelope;
            Vector2 point = Vector2.Lerp(start, end, t) + perpendicular * jitter;
            state.BoltPoints[i] = new Vector3(point.x, point.y, 0f);
        }

        float widthWorld = OrreryUnits.MetersToWorld(Mathf.Max(0f, widthMeters));
        state.BoltOuter.widthMultiplier = widthWorld;
        state.BoltCore.widthMultiplier = widthWorld *
            OrrerySpellCompendium.PlasmaBolt.BoltCoreWidthFraction;
        state.BoltOuter.SetPositions(state.BoltPoints);
        state.BoltCore.SetPositions(state.BoltPoints);
        state.BoltOuter.enabled = true;
        state.BoltCore.enabled = true;
        state.BoltVisibleUntil = Time.time +
            OrrerySpellCompendium.PlasmaBolt.BoltVisualLifetimeSeconds;


    }

    private static bool ShowZapVisual(BoltVisualState state, Vector2 start,
        Vector2 end, float widthMeters)
    {
        if (state == null)
            return false;

        if (state.ZapParticles == null || state.BoltVisualObject == null)
        {
            if (zapPrefab == null)
                zapPrefab = ModContent.Load<GameObject>(
                    OrrerySpellCompendium.PlasmaBolt.ZapPrefabPath);
            if (zapPrefab == null)
            {
                if (!warnedMissingZap)
                {
                    warnedMissingZap = true;
                    Debug.LogWarning("[Orrery] Plasma Bolt Zap bundle is missing. Build AssetBundles and Package Mod to include leviathanplasmavfx.bundle. Using the fallback bolt.");
                }
                return false;
            }

            DestroyBoltVisual(state);
            state.BoltVisualObject = Object.Instantiate(zapPrefab);
            state.BoltVisualObject.name = "Orrery Plasma Bolt Zap";
            state.ZapParticles = state.BoltVisualObject.GetComponentsInChildren<ParticleSystem>(true);
            state.ZapRenderers = new ParticleSystemRenderer[state.ZapParticles.Length];
            for (int i = 0; i < state.ZapParticles.Length; i++)
                state.ZapRenderers[i] = state.ZapParticles[i].GetComponent<ParticleSystemRenderer>();
        }

        Vector2 delta = end - start;
        float length = delta.magnitude;
        if (length <= 0.0001f)
        {
            state.BoltVisibleUntil = Time.time;
            HideExpiredBoltVisual(state, Time.time);
            return true;
        }

        // The imported _H atlas runs horizontally. A local XY billboard has
        // explicit endpoints; Stretch mode offsets its quad along velocity.
        Transform root = state.BoltVisualObject.transform;
        root.position = (Vector3)((start + end) * 0.5f);
        root.rotation = Quaternion.Euler(0f, 0f,
            Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
        root.localScale = Vector3.one;
        float width = Mathf.Max(0.01f, OrreryUnits.MetersToWorld(widthMeters) *
            OrrerySpellCompendium.PlasmaBolt.ZapWidthMultiplier);
        float lifetime = Mathf.Max(0.01f,
            OrrerySpellCompendium.PlasmaBolt.ZapVisualLifetimeSeconds);

        for (int i = 0; i < state.ZapParticles.Length; i++)
        {
            ParticleSystem particles = state.ZapParticles[i];
            particles.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = particles.main;
            main.loop = false;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.scalingMode = ParticleSystemScalingMode.Local;
            main.startSpeed = 0f;
            main.startDelay = 0f;
            main.startLifetime = lifetime;
            main.startSize3D = true;
            main.startSizeX = length;
            main.startSizeY = width;
            main.startSizeZ = 1f;
            main.startRotation3D = false;
            main.startRotation = 0f;
            main.gravityModifier = 0f;
            main.stopAction = ParticleSystemStopAction.None;
            var shape = particles.shape;
            shape.enabled = false;
            var emission = particles.emission;
            emission.enabled = false;
            ParticleSystemRenderer renderer = state.ZapRenderers[i];
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.alignment = ParticleSystemRenderSpace.Local;
            renderer.velocityScale = 0f;
            renderer.cameraVelocityScale = 0f;
            renderer.lengthScale = length / width;
            renderer.pivot = Vector3.zero;
            renderer.sortingOrder = OrrerySpellCompendium.PlasmaBolt.ZapSortingOrder;
            // One stationary particle per layer. The authored color, erosion
            // and atlas curves now run across the configured cast lifetime.
            particles.Play(false);
            particles.Emit(1);
            particles.Simulate(0.001f, false, false, false);
            particles.Play(false);
        }
        state.BoltVisibleUntil = Time.time + lifetime;
        return true;
    }

    private static bool EnsureBoltVisual(BoltVisualState state)
    {
        if (state.BoltVisualObject != null && state.BoltOuter != null &&
            state.BoltCore != null)
        {
            return true;
        }

        GameObject root = new GameObject("Orrery Plasma Bolt Visual");
        LineRenderer outer = root.AddComponent<LineRenderer>();
        GameObject coreObject = new GameObject("Core");
        coreObject.transform.SetParent(root.transform, false);
        LineRenderer core = coreObject.AddComponent<LineRenderer>();
        Material material = GetBoltMaterial();

        ConfigureLine(outer, material);
        ConfigureLine(core, material);
        outer.positionCount = state.BoltPoints.Length;
        core.positionCount = state.BoltPoints.Length;
        outer.startColor = new Color(1f, 0.10f, 0.03f, 0.72f);
        outer.endColor = new Color(0.85f, 0.02f, 0.02f, 0.45f);
        core.startColor = new Color(1f, 0.92f, 0.88f, 1f);
        core.endColor = new Color(1f, 0.28f, 0.18f, 0.92f);
        outer.enabled = false;
        core.enabled = false;

        state.BoltVisualObject = root;
        state.BoltOuter = outer;
        state.BoltCore = core;
        return true;
    }

    private static void ConfigureLine(LineRenderer line, Material material)
    {
        line.useWorldSpace = true;
        line.numCapVertices = 2;
        line.numCornerVertices = 2;
        line.textureMode = LineTextureMode.Stretch;
        if (material != null)
            line.sharedMaterial = material;
    }

    private static Material GetBoltMaterial()
    {
        if (boltMaterial != null)
            return boltMaterial;

        Shader shader = Shader.Find("Sprites/Default");
        if (shader == null)
            shader = Shader.Find("Legacy Shaders/Particles/Alpha Blended");
        if (shader == null)
            return null;

        boltMaterial = new Material(shader);
        boltMaterial.name = "Orrery Plasma Bolt Material";
        return boltMaterial;
    }

    internal static void HideExpiredBoltVisual(BoltVisualState state, float now)
    {
        if (state == null || now < state.BoltVisibleUntil)
            return;

        if (state.ZapParticles != null && state.BoltVisibleUntil > 0f)
        {
            for (int i = 0; i < state.ZapParticles.Length; i++)
                state.ZapParticles[i].Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
        }
        state.BoltVisibleUntil = 0f;
        if (state.BoltOuter != null)
            state.BoltOuter.enabled = false;
        if (state.BoltCore != null)
            state.BoltCore.enabled = false;
    }

    internal static void DestroyBoltVisual(BoltVisualState state)
    {
        if (state == null)
            return;

        if (state.BoltVisualObject != null)
            Object.Destroy(state.BoltVisualObject);
        state.BoltVisualObject = null;
        state.BoltOuter = null;
        state.BoltCore = null;
        state.ZapParticles = null;
        state.ZapRenderers = null;
        state.BoltVisibleUntil = 0f;
    }

    internal static StatusEffectLayer SpawnFauxBurnVisual(GameShip target)
    {
        if (target == null || target.statusEffectLayers == null ||
            PoolController.instance == null)
        {
            return null;
        }

        for (int i = 0; i < target.statusEffectLayers.Count; i++)
        {
            GameShip.AssignedLayer assigned = target.statusEffectLayers[i];
            if (assigned == null || assigned.type != StatusEffect.Type.Burning ||
                assigned.layerPrefab == null)
            {
                continue;
            }

            GameObject visualObject = PoolController.instance.GetObject(
                assigned.layerPrefab.gameObject,
                target.transform.position,
                target.transform.rotation,
                false);
            if (visualObject == null)
                return null;

            visualObject.transform.SetParent(target.transform, false);
            visualObject.transform.localPosition = Vector3.zero;
            visualObject.transform.localRotation = Quaternion.identity;

            StatusEffectLayer layer;
            if (!visualObject.TryGetComponent<StatusEffectLayer>(out layer) ||
                layer == null)
            {
                PoolableObject poolable;
                if (visualObject.TryGetComponent<PoolableObject>(out poolable))
                    poolable.PoolDestroy();
                else
                    Object.Destroy(visualObject);
                return null;
            }

            // IMPORTANT: do not assign this to assigned.layer and do not add a
            // BurningStatusEffect. RemoteShipDriver considers the ship's assigned
            // native layer/status state when building status masks; this object is
            // presentation-only and must remain outside that gameplay channel.
            layer.Activate();
            return layer;
        }

        return null;
    }

    // ---------------------------------------------------------------------
    // Cast completion / helpers
    // ---------------------------------------------------------------------

    private static void CompleteCommittedCast(GameShip owner, OwnerState state)
    {
        if (!state.CompletionPending)
            return;

        OrreryCastInvocation invocation = state.CompletionInvocation;
        state.CompletionInvocation = default(OrreryCastInvocation);
        state.CompletionPending = false;

        if (invocation.Execution != null && invocation.Execution.IsValid)
        {
            OrreryCasting.CompleteInvocation(owner, invocation.Execution, 0);
        }
        else
        {
            OrreryCasting.Cancel(owner);
        }

        OrreryNetwork.PublishLocal(owner);
        OrreryController.StartShuffle(owner);
    }

    private static OwnerState GetOrCreateState(GameShip owner)
    {
        OwnerState state;
        if (owners.TryGetValue(owner, out state) && state != null)
            return state;

        state = new OwnerState();
        owners[owner] = state;
        return state;
    }

    private static bool TryCreateLightningSource(
        GameShip owner,
        OrreryFocusResolver.Focus focus,
        out Launcher launcher)
    {
        launcher = null;
        ItemBase itemBase = Resources.Load<ItemBase>(LightningDonorPath);
        if (itemBase == null)
        {
            Debug.LogError(
                "[Orrery] Plasma Bolt native donor not found: " +
                LightningDonorPath);
            return false;
        }

        launcher = itemBase.GetItem(Item.Rarity.Common, 1, 0) as Launcher;
        if (launcher == null)
        {
            Debug.LogError(
                "[Orrery] Plasma Bolt donor is not a Launcher: " +
                LightningDonorPath);
            return false;
        }

        launcher.heatPerSecond = 0f;
        launcher.SetFaction(owner.faction);
        launcher.Equip(owner, Placement.zero, focus.SlotIndex, false, false);
        launcher.ToggleVisbility(false);
        return true;
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
            Debug.LogError("[Orrery] Failed to dispose Plasma Bolt donor: " + ex);
        }
    }

    private static GameShip ResolveShip(GameObject targetObject)
    {
        if (targetObject == null)
            return null;

        if (targetObject.CompareTag("Shield") && targetObject.transform.parent != null)
            targetObject = targetObject.transform.parent.gameObject;

        GameShip ship;
        return targetObject.TryGetComponent<GameShip>(out ship) ? ship : null;
    }

    private static bool IsValidHostile(GameShip owner, GameShip target)
    {
        return owner != null && target != null && target.gameObject != null &&
            target.gameObject.activeInHierarchy && target.health > 0f &&
            !object.ReferenceEquals(owner, target) &&
            target.CanBeDamagedBy(owner, false);
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
                (Vector2)owner.transform.right *
                OrreryUnits.MetersToWorld(
                    OrrerySpellCompendium.PlasmaBolt.BoltLengthMeters);
    }
}
