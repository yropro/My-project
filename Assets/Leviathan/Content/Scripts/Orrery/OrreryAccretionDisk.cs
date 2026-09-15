using StarVortex;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>Recipient-authoritative Accretion Disk. Core supplies application
/// vetoes and confirmed projectile transactions; this spell owns the pool,
/// recovery, live footprint, cast snapshot and generation.</summary>
public static class OrreryAccretionDisk
{
    public const ushort ApplyEffectId = 0x0202;
    private const byte CaptureProvider = 1;
    private const float TerminalSnapshotSeconds = 8f;
    private const int MaximumSections = 512;
    private struct Snapshot
    {
        public float Duration, Capacity, HealFraction, ExtensionMeters;
    }
    private sealed class LocalState
    {
        public GameShip Target;
        public uint Generation;
        public float Duration, HealFraction, ExtensionMeters, RadiusWorld;
        public readonly OrreryAccretionReservoir Pool = new OrreryAccretionReservoir();
    }
    [StructLayout(LayoutKind.Explicit)]
    private struct FloatBits
    {
        [FieldOffset(0)] public float Float;
        [FieldOffset(0)] public uint UInt;
    }
    private static readonly GameShip[] targetScratch =
        new GameShip[OrrerySpellCompendium.AccretionDisk.MaxCandidateShips];
    private static readonly List<GameShip> sections = new List<GameShip>(MaximumSections);
    private static LocalState active;
    private static uint nextGeneration, lastGeneration;
    private static GameShip lastTarget;
    private static float terminalUntil, nextTestCast;
    private static bool initialized;
    public static float LastSpent { get; private set; }
    public static float TotalSpent { get; private set; }

    public static bool Execute(GameShip owner, OrreryCastInvocation invocation,
        OrrerySpellRegistry.SpellDefinition spell)
    {
        if (spell == null || invocation.Execution == null || !invocation.Execution.IsValid ||
            !TryCast(owner)) return false;
        OrreryCasting.CompleteInvocation(owner, invocation.Execution, 0);
        OrreryController.StartShuffle(owner);
        return true;
    }
    private static bool TryCast(GameShip owner)
    {
        if (owner == null || !owner.IsPlayer() || !OrreryRuntime.IsActive(owner) ||
            OrreryController.IsShuffling(owner)) return false;
        EnsureInitialized();
        Snapshot snapshot;
        if (!Resolve(owner, out snapshot)) return false;
        GameShip target = FindTarget(owner);
        if (target == null || ReferenceEquals(target, owner)) return ApplyLocal(owner, snapshot);
        int targetPlayerId;
        return CoreNetwork.TryGetPlayerId(target, out targetPlayerId) &&
            CoreCrossOwnerEffects.RequestGrant(targetPlayerId, ApplyEffectId, Pack(snapshot));
    }
    /// <summary>Temporary opt-in gesture for this playtest branch, not a recipe
    /// change: Ctrl+Shift+F8 casts using the same focus/target/grant path. The
    /// Compendium switch removes it without changing the three-rune definition.</summary>
    public static void TickPlaytestInput(GameShip owner)
    {
        if (!OrrerySpellCompendium.AccretionDisk.PlaytestShortcutEnabled ||
            owner == null || !owner.IsPlayer() || !OrreryRuntime.IsActive(owner) ||
            Time.timeScale <= 0f || !Input.GetKeyDown(KeyCode.F8) ||
            !(Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) ||
            !(Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) ||
            Time.unscaledTime < nextTestCast) return;
        nextTestCast = Time.unscaledTime + OrrerySpellCompendium.AccretionDisk.PlaytestShortcutCooldownSeconds;
        bool dispatched = TryCast(owner);
        Debug.Log("[Orrery/AccretionDisk] PLAYTEST cast " +
            (dispatched ? "applied locally or dispatched (remote application is not yet acknowledged)." : "rejected."));
        if (dispatched) OrreryController.StartShuffle(owner);
    }
    public static void EnsureInitialized()
    {
        if (initialized) return;
        CoreCrossOwnerEffects.RegisterHandler(ApplyEffectId, ReceiveCast);
        CoreIncomingDamage.Register("Orrery/AccretionDisk", HasLocalPool,
            BlockPacket, BlockDirect);
        CoreProjectileCapture.Register(CaptureProvider, ReadField, ReserveCapture,
            SettleCapture, CaptureFault, EligibleProjectile);
        initialized = true;
    }
    private static bool ReceiveCast(GameShip target, int sourcePlayerId,
        CoreCrossOwnerEffects.GrantPayload payload)
    {
        if (sourcePlayerId < 0 || payload.E != 0u || payload.F != 0u) return false;
        Snapshot snapshot = new Snapshot { Duration = Unpack(payload.A), Capacity = Unpack(payload.B),
            HealFraction = Unpack(payload.C), ExtensionMeters = Unpack(payload.D) };
        return ApplyLocal(target, snapshot);
    }
    private static bool ApplyLocal(GameShip target, Snapshot snapshot)
    {
        if (target == null || !target.IsPlayer() || target.health <= 0f || !Valid(snapshot)) return false;
        EndActive();
        do { unchecked { nextGeneration++; } } while (nextGeneration == 0u);
        var state = new LocalState { Target = target, Generation = nextGeneration,
            Duration = snapshot.Duration, HealFraction = snapshot.HealFraction,
            ExtensionMeters = snapshot.ExtensionMeters };
        state.Pool.Begin(state.Generation, snapshot.Capacity, Time.time + snapshot.Duration);
        active = state;
        lastTarget = target;
        lastGeneration = state.Generation;
        TotalSpent = LastSpent = 0f;
        RefreshGeometry(state);
        RefreshPresentation(state);
        CoreProjectileCapture.FieldChanged();
        return true;
    }
    public static void FixedTickRecipient(GameShip localPlayer, float deltaTime)
    {
        LocalState state = active;
        if (state == null) return;
        if (localPlayer == null || !localPlayer.IsPlayer() ||
            !ReferenceEquals(localPlayer, state.Target) || localPlayer.health <= 0f ||
            !localPlayer.gameObject.activeInHierarchy)
        { EndActive(); return; }
        if (Time.time >= state.Pool.ExpiresAt)
        {
            // No new applications after expiry. Already-authorized exchanges
            // remain earmarked until confirmed/aborted; uncertainty cannot refund.
            OrreryAccretionDiskPresentation.Hide(state.Target);
            if (state.Pool.Reserved <= 0f) EndActive();
            return;
        }
        RefreshGeometry(state);
        RefreshPresentation(state);
    }
    private static bool HasLocalPool(GameShip target)
    {
        return active != null && ReferenceEquals(active.Target, target) &&
            active.Pool.CanAbsorb(Time.time);
    }
    private static bool BlockPacket(GameShip target, Damageable.DamageType type,
        Damageable.DamageData[] packet)
    {
        float value;
        return HasLocalPool(target) && TryGetPacketValue(packet, out value) && Absorb(active, value);
    }
    internal static bool TryGetPacketValue(Damageable.DamageData[] packet, out float value)
    {
        value = 0f;
        if (packet == null || packet.Length == 0) return false;
        double total = 0d;
        for (int i = 0; i < packet.Length; i++)
        {
            float part = packet[i].damage;
            if (!OrreryAccretionReservoir.Finite(part)) return false;
            // Gross positive incoming components at the external-barrier boundary.
            // Negative modifier adjustments cannot create capacity or free healing.
            if (part > 0f) total += part;
        }
        if (total <= 0d) return false;
        value = (float)Math.Min(total, float.MaxValue);
        return true;
    }
    private static bool BlockDirect(GameShip target, Damageable.DamageType type,
        float value, bool destroy)
    {
        return HasLocalPool(target) && Absorb(active, value);
    }
    private static bool Absorb(LocalState state, float value)
    {
        float spent;
        if (state == null || !state.Pool.TryAbsorb(value, Time.time, out spent)) return false;
        CompleteConsumption(state, spent);
        return true; // deliberately NOT the remaining fraction of the hit
    }
    private static bool ReserveCapture(uint generation, float value, out ulong ticket)
    {
        ticket = 0ul;
        LocalState state = active;
        GameShip local = WorldController.instance == null ? null : WorldController.instance.GetCurrentPlayerShip();
        return state != null && ReferenceEquals(state.Target, local) && local != null &&
            local.IsPlayer() && local.health > 0f && state.Generation == generation &&
            state.Pool.TryReserve(generation, value, Time.time, out ticket);
    }
    private static void SettleCapture(uint generation, ulong ticket, bool captured)
    {
        LocalState state = active;
        float spent;
        if (state == null || state.Generation != generation ||
            !state.Pool.Settle(generation, ticket, captured, out spent)) return;
        if (captured) CompleteConsumption(state, spent);
        else if (Time.time >= state.Pool.ExpiresAt && state.Pool.Reserved <= 0f) EndActive();
        else RefreshPresentation(state);
    }
    private static void CaptureFault(uint generation)
    {
        if (active == null || active.Generation != generation) return;
        Debug.LogWarning("[Orrery/AccretionDisk] Unresolved capture retired generation " + generation +
            ". No uncertain refund or healing was granted.");
        EndActive();
    }
    private static void CompleteConsumption(LocalState state, float spent)
    {
        LastSpent = spent;
        TotalSpent += spent;
        // Collapse gameplay before healing, which may reenter damage/cast code.
        if (state.Pool.Remaining <= 0f && ReferenceEquals(active, state)) EndActive();
        else if (ReferenceEquals(active, state)) RefreshPresentation(state);
        if (state.Target != null && state.Target.health > 0f && spent > 0f && state.HealFraction > 0f)
            CoreNativeCriticalHits.Heal(state.Target, spent * state.HealFraction, 0,
                state.Target.transform.position, null, false, false);
    }
    private static bool EligibleProjectile(GameShip recipient, Projectile projectile)
    {
        GameShip source = projectile == null ? null : projectile.GetParentShip();
        return recipient != null && source != null && recipient.gameObject.activeInHierarchy &&
            !ReferenceEquals(recipient, source) && recipient.CanBeDamagedBy(source, false);
    }
    private static bool ReadField(out CoreProjectileCapture.Field field)
    {
        field = default(CoreProjectileCapture.Field);
        LocalState state = active;
        if (state == null)
        {
            if (lastGeneration == 0u || Time.unscaledTime >= terminalUntil) return false;
            field.Generation = lastGeneration;
            field.Ship = lastTarget;
            return true;
        }
        field.Generation = state.Generation;
        field.Ship = state.Target;
        field.RemainingSeconds = Mathf.Max(0f, state.Pool.ExpiresAt - Time.time);
        field.RadiusWorld = state.RadiusWorld;
        field.Active = state.Target != null && state.Target.health > 0f &&
            state.Pool.Remaining > 0f && field.RemainingSeconds > 0f;
        return true;
    }
    public static bool TryGetPresentation(out OrreryAccretionDiskPresentationLease.Snapshot snapshot,
        out uint generation)
    {
        snapshot = default(OrreryAccretionDiskPresentationLease.Snapshot);
        generation = lastGeneration;
        LocalState state = active;
        if (state == null) return generation != 0u && Time.unscaledTime < terminalUntil;
        generation = state.Generation;
        float remaining = Mathf.Max(0f, state.Pool.ExpiresAt - Time.time);
        snapshot.Active = remaining > 0f && state.Pool.Remaining > 0f;
        if (!snapshot.Active) return true;
        snapshot.RemainingSeconds = remaining;
        snapshot.DurationSeconds = state.Duration;
        snapshot.RadiusWorld = state.RadiusWorld;
        snapshot.Capacity = (byte)Mathf.Clamp(Mathf.RoundToInt(state.Pool.Remaining / state.Pool.Maximum * 255f), 0, 255);
        return true;
    }
    private static void RefreshPresentation(LocalState state)
    {
        if (!ReferenceEquals(active, state) || Time.time >= state.Pool.ExpiresAt) return;
        OrreryAccretionDiskPresentation.Show(state.Target, state.Pool.ExpiresAt - Time.time,
            OrreryUnits.WorldToMeters(state.RadiusWorld), state.Pool.Remaining / state.Pool.Maximum,
            state.Generation, Mathf.Clamp01((state.Pool.ExpiresAt - Time.time) / state.Duration));
    }
    private static void RefreshGeometry(LocalState state)
    {
        Vector2 center = state.Target.transform.position;
        float radius = Extent(state.Target, center);
        LeviathanGrowth.CollectScalingShips(state.Target, sections);
        int limit = Mathf.Min(sections.Count, MaximumSections);
        for (int i = 0; i < limit; i++) radius = Mathf.Max(radius, Extent(sections[i], center));
        sections.Clear();
        state.RadiusWorld = radius + OrreryUnits.MetersToWorld(state.ExtensionMeters);
    }
    private static float Extent(GameShip ship, Vector2 center)
    {
        if (ship == null || !ship.gameObject.activeInHierarchy) return 0f;
        float result = Vector2.Distance(center, ship.transform.position) + Mathf.Max(0f, ship.GetShieldWorldRadius());
        Collider2D hull = ship.GetComponent<CompositeCollider2D>();
        if (hull == null || !hull.enabled) hull = ship.GetComponent<CircleCollider2D>();
        if (hull != null && hull.enabled)
        {
            Bounds b = hull.bounds;
            Vector2 far = new Vector2(Mathf.Max(Mathf.Abs(b.min.x - center.x), Mathf.Abs(b.max.x - center.x)),
                Mathf.Max(Mathf.Abs(b.min.y - center.y), Mathf.Abs(b.max.y - center.y)));
            result = Mathf.Max(result, far.magnitude);
        }
        return result;
    }
    private static GameShip CanonicalPlayer(GameShip ship)
    {
        GameShip owner;
        return ship != null && LeviathanGrowth.TryGetAnatomyOwner(ship, out owner) ? owner : ship;
    }
    public static void ForgetTarget(GameShip target)
    {
        CoreProjectileCapture.ForgetShip(target);
        if (active != null && ReferenceEquals(active.Target, target)) EndActive();
        if (ReferenceEquals(lastTarget, target)) lastTarget = null;
    }
    public static void ForgetOrreryOwner(GameShip owner)
    {
        // Class membership is not the recipient-owned effect's lifetime.
    }
    private static void EndActive()
    {
        LocalState state = active;
        if (state == null) return;
        active = null;
        state.Pool.Invalidate();
        CoreProjectileCapture.Retire(CaptureProvider, state.Generation);
        lastGeneration = state.Generation;
        lastTarget = state.Target;
        terminalUntil = Time.unscaledTime + TerminalSnapshotSeconds;
        OrreryAccretionDiskPresentation.Hide(state.Target);
    }
    public static void Reset()
    {
        EndActive();
        lastGeneration = 0u;
        lastTarget = null;
        terminalUntil = 0f;
        sections.Clear();
        CoreProjectileCapture.ResetWorld();
        // nextGeneration is never reset: delayed callbacks cannot alias a new cast.
    }
    private static bool Resolve(GameShip owner, out Snapshot snapshot)
    {
        snapshot = default(Snapshot);
        OrreryFocusProfile.Resolved stellar, barrier;
        if (!OrreryFocusProfile.TryResolve(owner, OrreryElement.Fire, out stellar) || !stellar.IsValid ||
            !OrreryFocusProfile.TryResolve(owner, OrreryElement.Ice, out barrier) || !barrier.IsValid) return false;
        snapshot.Duration = Mathf.Clamp(OrrerySpellCompendium.AccretionDisk.BaseDurationSeconds *
            Mathf.Max(0f, 1f + barrier.DurationBonus * OrrerySpellCompendium.AccretionDisk.DurationModifierScale), 0.01f, 3600f);
        snapshot.Capacity = barrier.ApplySpellDamageBonus(barrier.GetReferenceDps()) *
            OrrerySpellCompendium.AccretionDisk.CapacityReferenceSeconds * OrrerySpellCompendium.AccretionDisk.CapacityMultiplier;
        snapshot.ExtensionMeters = Mathf.Max(0f, OrrerySpellCompendium.AccretionDisk.ExtensionMeters *
            Mathf.Max(0f, 1f + barrier.RangeBonus * OrrerySpellCompendium.AccretionDisk.RadiusModifierScale));
        snapshot.HealFraction = Mathf.Clamp(OrrerySpellCompendium.AccretionDisk.HealFraction *
            Mathf.Max(0f, 1f + stellar.SpellDamageBonus * OrrerySpellCompendium.AccretionDisk.HealingModifierScale),
            0f, OrrerySpellCompendium.AccretionDisk.MaximumHealFraction);
        return Valid(snapshot);
    }
    private static bool Valid(Snapshot s)
    {
        return OrreryAccretionReservoir.Positive(s.Duration) && s.Duration <= 3600f &&
            OrreryAccretionReservoir.Positive(s.Capacity) &&
            OrreryAccretionReservoir.Finite(s.HealFraction) && s.HealFraction >= 0f &&
            s.HealFraction <= OrrerySpellCompendium.AccretionDisk.MaximumHealFraction &&
            OrreryAccretionReservoir.Finite(s.ExtensionMeters) && s.ExtensionMeters >= 0f && s.ExtensionMeters <= 100000f;
    }
    private static CoreCrossOwnerEffects.GrantPayload Pack(Snapshot s)
    {
        return new CoreCrossOwnerEffects.GrantPayload(PackFloat(s.Duration), PackFloat(s.Capacity),
            PackFloat(s.HealFraction), PackFloat(s.ExtensionMeters), 0u, 0u);
    }
    private static uint PackFloat(float f) { return new FloatBits { Float = f }.UInt; }
    private static float Unpack(uint u) { return new FloatBits { UInt = u }.Float; }

    private static GameShip FindTarget(GameShip owner)
    {
        if (owner == null || PhysicsController.instance == null)
            return owner;

        Vector2 cursor = GetAimPoint(owner);
        float cursorRadius = OrreryUnits.MetersToWorld(Mathf.Max(
            0f,
            OrrerySpellCompendium.AccretionDisk.CursorTargetRadiusMeters));
        if (cursorRadius <= 0f)
            return owner;

        Collider2D[] overlaps = PhysicsController.instance.OverlapCircle(
            cursor,
            cursorRadius);
        if (overlaps == null)
            return owner;

        float maxRangeMeters =
            OrrerySpellCompendium.AccretionDisk.MaximumTargetRangeMeters;
        float maxRangeWorld = maxRangeMeters <= 0f
            ? float.PositiveInfinity
            : OrreryUnits.MetersToWorld(maxRangeMeters);
        float maxRangeSquared = maxRangeWorld * maxRangeWorld;

        GameShip best = null;
        float bestCursorDistanceSquared = float.PositiveInfinity;
        int candidateCount = 0;
        int maxCandidates = Mathf.Min(
            targetScratch.Length,
            Mathf.Max(
                0,
                OrrerySpellCompendium.AccretionDisk.MaxCandidateShips));

        for (int i = 0; i < overlaps.Length && candidateCount < maxCandidates; i++)
        {
            Collider2D collider = overlaps[i];
            if (collider == null)
                break;

            GameObject targetObject = collider.gameObject;
            if (targetObject.CompareTag("Shield") &&
                targetObject.transform.parent != null)
            {
                targetObject = targetObject.transform.parent.gameObject;
            }

            GameShip candidate;
            if (!targetObject.TryGetComponent<GameShip>(out candidate) ||
                !IsEligibleAlly(owner, CanonicalPlayer(candidate)) ||
                ContainsTarget(CanonicalPlayer(candidate), candidateCount))
            {
                continue;
            }

            candidate = CanonicalPlayer(candidate);
            targetScratch[candidateCount++] = candidate;
            Vector2 candidatePosition = candidate.transform.position;
            if ((candidatePosition - (Vector2)owner.transform.position).sqrMagnitude >
                maxRangeSquared)
            {
                continue;
            }

            float cursorDistanceSquared =
                (candidatePosition - cursor).sqrMagnitude;
            if (cursorDistanceSquared < bestCursorDistanceSquared)
            {
                bestCursorDistanceSquared = cursorDistanceSquared;
                best = candidate;
            }
        }

        for (int i = 0; i < candidateCount; i++)
            targetScratch[i] = null;

        return best != null ? best : owner;
    }

    private static bool ContainsTarget(GameShip candidate, int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (object.ReferenceEquals(targetScratch[i], candidate))
                return true;
        }
        return false;
    }

    private static bool IsEligibleAlly(GameShip owner, GameShip candidate)
    {
        return owner != null && candidate != null &&
            candidate.gameObject != null &&
            candidate.gameObject.activeInHierarchy &&
            !object.ReferenceEquals(owner, candidate) &&
            candidate.IsAnyPlayerShip() &&
            !Faction.IsHostile(owner.faction, candidate.faction);
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
            : (Vector2)owner.transform.position;
    }

}
