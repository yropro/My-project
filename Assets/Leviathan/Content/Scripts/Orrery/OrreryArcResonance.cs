using StarVortex;
using UnityEngine;
using Profile = OrrerySpellCompendium.ArcResonance.Profile;
using Schedule = OrreryLightningRodState<StarVortex.GameShip>;

/// <summary>
/// Owner-authoritative Arc Resonance. Acquisition happens once; all subsequent
/// checks refer to the same GameShip AND CoreCombat entity incarnation. This
/// file does no packet encoding, audio playback, rendering or engine patching.
/// </summary>
public static class OrreryArcResonance
{
    private const string ContextSourcePath = "Base/Items/SecondaryWeapon/Lightning Orb Launcher";
    // Transport/retention bounds, deliberately not balance knobs.
    internal const float PresentationTailSeconds = 1f;
    internal const float MaximumBoltLifetimeSeconds = 5f;

    public struct PresentationSnapshot
    {
        public uint Generation, TargetNetId;
        public byte RecipeSize, StrikeIndex;
        public bool Active;
        public float StrikeAgeSeconds;
        public Vector2 TargetPosition;
    }

    private sealed class OwnerState
    {
        public GameShip Owner;
        public OrreryCastInvocation Invocation;
        public Schedule Clock;
        public CoreCombat.CombatEntityKey TargetKey;
        public uint TargetNetId, Generation;
        public byte RecipeSize, StrikeIndex;
        public Profile Tuning;
        public OrreryFocusProfile.Resolved Focus;
        public Equippable ContextSlotItem;
        public Launcher UnfocusedSource;
        public float RangeWorldSquared, LastStrikeAt, PresentUntil;
        public Vector2 LastTargetPosition;
        public bool CommitInProgress, RoutingDamage, Teardown, Ended;
        public readonly Damageable.DamageData[] Damage = new Damageable.DamageData[1];
    }

    // There is exactly one authoritative local Orrery owner on this peer.
    // Remote presentation keeps its own bounded, presentation-only state.
    private static OwnerState local;
    private static uint generationCounter;
    private static bool warnedUnreachableCount;

    public static bool Execute(GameShip owner, OrreryCastInvocation invocation,
        OrrerySpellRegistry.SpellDefinition spell)
    {
        CoreOwnerContext context = CoreClassRuntime.CurrentContext;
        if (owner == null || spell == null || context == null || !context.IsValid ||
            context.ClassId != CoreClassId.Orrery || !object.ReferenceEquals(context.Ship, owner) ||
            invocation.Execution == null || !invocation.Execution.IsValid ||
            !object.ReferenceEquals(invocation.Execution.Owner, context) ||
            !OrreryRuntime.IsActive(owner) || OrreryController.IsShuffling(owner) ||
            PhysicsController.instance == null || !OrreryDamageRouter.EnsureAvailable()) return false;
        if (local != null && !local.Ended) return false;

        bool twoRunes = invocation.Recipe.Equals(OrrerySpellCompendium.ArcResonance.Recipe);
        if (!twoRunes && !invocation.Recipe.Equals(OrreryRecipeKey.Pure(OrreryElement.Lightning, 3)))
            return false;
        int size = twoRunes ? 2 : 3;
        Profile tuning = GetProfile((byte)size);
        OrreryFocusProfile.Resolved focus;
        if (!OrreryFocusProfile.TryResolve(owner, OrreryElement.Lightning, out focus) || !focus.IsValid)
            return false;

        Schedule.Settings settings;
        float rangeWorld;
        if (!TryResolve(tuning, focus, out settings, out rangeWorld))
        {
            Debug.LogError("[Orrery] Arc Resonance has invalid Compendium tuning; cast rejected.");
            return false;
        }
        if (!warnedUnreachableCount && settings.FirstStrikeDelaySeconds +
            (settings.MaxStrikes - 1) * settings.StrikeIntervalSeconds >= settings.DurationSeconds)
        {
            warnedUnreachableCount = true;
            Debug.LogWarning("[Orrery] Arc Resonance's duration cannot fit its strike cap. " +
                "Increase duration or lower count/interval. Duration and count were not silently rewritten.");
        }

        InputController input = InputController.instance;
        if (input == null || !object.ReferenceEquals(input.controlShip, owner)) return false;
        GameShip target = Acquire(owner, input.GetCursorWorldPoint(), rangeWorld, tuning);
        CoreCombat.CombatEntityKey targetKey;
        if (target == null || !CoreCombat.TryGetEntityKey(target, out targetKey) || !targetKey.IsValid)
            return false;
        Schedule clock;
        if (!Schedule.TryStart(target, Time.timeAsDouble, settings, out clock)) return false;

        OwnerState state = new OwnerState();
        state.Owner = owner;
        state.Invocation = invocation;
        state.Clock = clock;
        state.TargetKey = targetKey;
        state.TargetNetId = target.netId;
        state.RecipeSize = (byte)size;
        state.Tuning = tuning;
        state.Focus = focus;
        state.RangeWorldSquared = rangeWorld * rangeWorld;
        state.LastTargetPosition = TargetPoint(target, tuning);
        int contextSlot = focus.Focus.SlotIndex;
        if (owner.slots == null || contextSlot < 0 || contextSlot >= owner.slots.Length ||
            owner.slots[contextSlot] == null) return false;
        state.ContextSlotItem = owner.slots[contextSlot].equippable;
        state.LastStrikeAt = -1f;
        if (!focus.HasFocus && !CreateUnfocusedSource(state)) return false;
        generationCounter++;
        if (generationCounter == 0u) generationCounter = 1u;
        state.Generation = generationCounter;
        local = state;

        // An immediate lethal hit can call ForgetTarget reentrantly. Do not end
        // CoreAbilityExecution or dispose its source while TryCommit is on stack.
        state.CommitInProgress = true;
        bool accepted = false;
        try
        {
            Advance(state);
            accepted = true;
            return true;
        }
        finally
        {
            state.CommitInProgress = false;
            if (!accepted) state.Teardown = true;
            if (state.Teardown) Release(state);
        }
    }

    public static void FixedTick(GameShip owner, float deltaTime)
    {
        OwnerState state = local;
        if (state == null || !object.ReferenceEquals(state.Owner, owner)) return;
        if (state.Teardown) { Release(state); return; }
        if (state.Ended)
        {
            if (Time.time >= state.PresentUntil) local = null;
            return;
        }
        if (owner == null || owner.health <= 0f || !OrreryRuntime.IsActive(owner) ||
            state.Invocation.Execution == null || !state.Invocation.Execution.IsValid ||
            !ContextStillEquipped(state))
            state.Clock.Cancel();
        if (state.Clock.IsActive) Advance(state);
        if (!object.ReferenceEquals(local, state) || state.Clock == null) return;
        if (!state.Clock.IsActive) Finish(state);
    }

    private static void Advance(OwnerState state)
    {
        GameShip target = state.Clock.Target;
        bool valid = ExactTargetValid(state);
        if (valid) state.LastTargetPosition = TargetPoint(target, state.Tuning);
        bool within = valid && ((Vector2)target.transform.position -
            (Vector2)state.Owner.transform.position).sqrMagnitude <= state.RangeWorldSquared;
        Schedule.Step step = state.Clock.Tick(Time.timeAsDouble, valid, within);
        if (step != Schedule.Step.Strike) return;

        // Consume the schedule before calling damage. The bolt is one attack
        // occurrence, including a dodged/absorbed attack, never a half-second DoT.
        state.StrikeIndex = (byte)state.Clock.StrikesPerformed;
        state.LastStrikeAt = Time.time;
        if (target.IsDodging()) return;
        state.RoutingDamage = true;
        try { Strike(state, target); }
        finally
        {
            state.RoutingDamage = false;
            if (state.Teardown && !state.CommitInProgress) Release(state);
        }
    }

    private static void Strike(OwnerState state, GameShip target)
    {
        Profile p = state.Tuning;
        float scale = GetStrikeDamageMultiplier(p, state.StrikeIndex - 1);
        float dps = state.Focus.GetReferenceDps() * p.StrikeDamageMultiplier * scale *
            Mathf.Max(0f, 1f + state.Focus.SpellDamageBonus * p.ImplementDamageBonusScale);
        float chance = Mathf.Clamp01(state.Focus.CritChance + p.CritChanceBonusPoints * 0.01f);
        bool crit = CoreNativeCriticalHits.CritRoll(chance, target);
        float critBonus = Mathf.Max(0f, state.Focus.CritModifier + p.CritDamageBonusPercent * 0.01f);
        float amount = dps * p.IntegratedSecondsPerStrike * (crit ? 1f + critBonus : 1f);
        if (!Finite(dps) || !Finite(amount))
        {
            state.Clock.Cancel();
            Debug.LogError("[Orrery] Arc Resonance damage overflow; execution cancelled.");
            return;
        }
        state.Damage[0] = new Damageable.DamageData(amount, dps);
        Activatable source = state.Focus.HasFocus ? state.Focus.Donor : state.UnfocusedSource;
        CoreCombat.DamageScope scope = CoreCombat.BeginDamage(state.Owner, target,
            OrreryCombat.ArcResonance,
            OrreryCombat.SatelliteFor(state.Invocation, OrreryElement.Lightning),
            CoreCombat.AcknowledgementMode.NativeResult,
            CoreCombat.TrackingFlags.MeaningfulOutcome,
            (ushort)state.Invocation.Sequence, state.Owner);
        try
        {
            OrreryDamageRouter.Route(state.Owner, target, Damageable.DamageType.Electric,
                state.Damage, Mathf.Clamp01(state.Focus.StatusChance + p.StatusChanceBonusPoints * 0.01f),
                crit, state.Owner.transform.position, state.Focus.BypassDamageLimit, 0f, source);
        }
        finally { CoreCombat.EndDamage(scope); }
    }

    // Zero-based strike index; never multiplies a prior crit or mitigated hit.
    internal static float GetStrikeDamageMultiplier(Profile profile, int index)
    {
        float scale = Mathf.Max(0f, 1f + profile.AdditionalDamagePercentPerStrike * 0.01f * index);
        return index == profile.MaxStrikes - 1 ? scale * profile.FinalStrikeDamageMultiplier : scale;
    }

    private static GameShip Acquire(GameShip owner, Vector2 cursor, float rangeWorld, Profile p)
    {
        if (!Finite(cursor.x) || !Finite(cursor.y)) return null;
        Collider2D[] hits = PhysicsController.instance.OverlapCircle(owner.transform.position, rangeWorld);
        GameShip nearest = null;
        float best = float.PositiveInfinity;
        float rangeSquared = rangeWorld * rangeWorld;
        float cursorRange = p.CursorAcquisitionRadiusMeters < 0f ? float.PositiveInfinity :
            OrreryUnits.MetersToWorld(p.CursorAcquisitionRadiusMeters);
        float cursorSquared = cursorRange * cursorRange;
        for (int i = 0; i < hits.Length && hits[i] != null; i++)
        {
            GameShip candidate = hits[i].GetComponentInParent<GameShip>();
            if (!ValidHostile(owner, candidate)) continue;
            Vector2 point = candidate.transform.position;
            if ((point - (Vector2)owner.transform.position).sqrMagnitude > rangeSquared) continue;
            float distance = (point - cursor).sqrMagnitude;
            if (distance > cursorSquared || distance > best) continue;
            // Stable owner-side tie break; observers never run this search.
            if (distance == best && nearest != null && candidate.GetInstanceID() >= nearest.GetInstanceID()) continue;
            best = distance;
            nearest = candidate;
        }
        return nearest;
    }

    private static bool ExactTargetValid(OwnerState state)
    {
        if (state.Clock == null) return false;
        GameShip target = state.Clock.Target;
        CoreCombat.CombatEntityKey key;
        return ValidHostile(state.Owner, target) && target.netId == state.TargetNetId &&
            CoreCombat.TryGetEntityKey(target, out key) && key.Equals(state.TargetKey);
    }

    private static bool ValidHostile(GameShip owner, GameShip target)
    {
        return owner != null && target != null && target.gameObject.activeInHierarchy &&
            target.health > 0f && !object.ReferenceEquals(owner, target) && target.CanBeDamagedBy(owner, false);
    }

    private static bool ContextStillEquipped(OwnerState state)
    {
        int slot = state.Focus.Focus.SlotIndex;
        return state.Owner.slots != null && slot >= 0 && slot < state.Owner.slots.Length &&
            state.Owner.slots[slot] != null &&
            object.ReferenceEquals(state.Owner.slots[slot].equippable, state.ContextSlotItem);
    }

    private static bool CreateUnfocusedSource(OwnerState state)
    {
        ItemBase item = ModContent.Load<ItemBase>(ContextSourcePath);
        Launcher source = item == null ? null : item.GetItem(Item.Rarity.Common, 1, 0) as Launcher;
        if (source == null) return false;
        bool equipped = false;
        try
        {
            source.heatPerSecond = 0f;
            source.SetFaction(state.Owner.faction);
            source.Equip(state.Owner, Placement.zero, state.Focus.Focus.SlotIndex, false, false);
            source.ToggleVisbility(false);
            state.UnfocusedSource = source;
            equipped = true;
            return true;
        }
        finally
        {
            if (!equipped) { source.Deactivate(); source.Unequip(); }
        }
    }

    private static void Finish(OwnerState state)
    {
        if (state.Ended || state.CommitInProgress || state.RoutingDamage) return;
        state.Ended = true;
        state.PresentUntil = Time.time + Mathf.Max(PresentationTailSeconds, BoltLifetime(state.Tuning));
        OrreryCastInvocation invocation = state.Invocation;
        state.Clock = null; // Release the immutable target and its incarnation.
        DisposeSource(state);
        state.Focus = default(OrreryFocusProfile.Resolved);
        state.ContextSlotItem = null;
        state.Invocation = default(OrreryCastInvocation);

        // Completion must not cancel/shuffle a replacement invocation.
        OrreryCastPhase phase;
        int required, locked, sequence;
        ushort spellId, mask;
        byte last;
        uint elements;
        if (invocation.Execution != null && invocation.Execution.IsValid &&
            OrreryCasting.TryGetPresentation(state.Owner, out phase, out required, out locked,
                out sequence, out spellId, out last, out mask, out elements) &&
            phase == OrreryCastPhase.Invoking && sequence == invocation.Sequence)
        {
            OrreryCasting.CompleteInvocation(state.Owner, invocation.Execution, 0);
            OrreryNetwork.PublishLocal(state.Owner);
            OrreryController.StartShuffle(state.Owner);
        }
    }

    private static void DisposeSource(OwnerState state)
    {
        Launcher source = state.UnfocusedSource;
        state.UnfocusedSource = null;
        if (source != null) { source.Deactivate(); source.Unequip(); }
    }

    public static void ForgetTarget(GameShip target)
    {
        OwnerState state = local;
        if (state != null && state.Clock != null && object.ReferenceEquals(state.Clock.Target, target))
            state.Clock.Cancel(); // Finalize on fixed tick, never inside native damage.
    }

    public static void Forget(GameShip owner)
    {
        OwnerState state = local;
        if (state == null || !object.ReferenceEquals(state.Owner, owner)) return;
        state.Teardown = true;
        if (state.Clock != null) state.Clock.Cancel();
        Release(state);
    }

    private static void Release(OwnerState state)
    {
        if (state.CommitInProgress || state.RoutingDamage) return;
        if (object.ReferenceEquals(local, state)) local = null;
        state.Clock = null;
        DisposeSource(state);
        state.Invocation = default(OrreryCastInvocation);
        state.Focus = default(OrreryFocusProfile.Resolved);
        state.ContextSlotItem = null;
        state.Owner = null;
    }

    public static void Reset()
    {
        if (local != null) { local.Teardown = true; Release(local); }
        generationCounter = 0u;
        warnedUnreachableCount = false;
    }

    public static bool TryGetPresentation(GameShip owner, out PresentationSnapshot snapshot)
    {
        snapshot = default(PresentationSnapshot);
        OwnerState state = local;
        if (state == null || state.Teardown || !object.ReferenceEquals(state.Owner, owner) ||
            (state.Ended && Time.time >= state.PresentUntil)) return false;
        snapshot.Generation = state.Generation;
        snapshot.RecipeSize = state.RecipeSize;
        snapshot.StrikeIndex = state.StrikeIndex;
        snapshot.Active = !state.Ended && state.Clock != null && state.Clock.IsActive;
        snapshot.TargetNetId = state.TargetNetId;
        snapshot.TargetPosition = state.LastTargetPosition;
        if (state.Clock != null && ExactTargetValid(state))
            snapshot.TargetPosition = TargetPoint(state.Clock.Target, state.Tuning);
        snapshot.StrikeAgeSeconds = state.StrikeIndex == 0 ? 0f : Mathf.Max(0f, Time.time - state.LastStrikeAt);
        return true;
    }

    internal static Profile GetProfile(byte recipeSize)
    {
        return recipeSize == 3 ? OrrerySpellCompendium.ArcResonance.LLL : OrrerySpellCompendium.ArcResonance.LL;
    }

    internal static Vector2 CasterPoint(GameShip owner, Profile p)
    {
        return (Vector2)owner.transform.position +
            (Vector2)owner.transform.right * OrreryUnits.MetersToWorld(SafeOffset(p.CasterOffsetXMeters)) +
            (Vector2)owner.transform.up * OrreryUnits.MetersToWorld(SafeOffset(p.CasterOffsetYMeters));
    }

    internal static Vector2 TargetPoint(GameShip target, Profile p)
    {
        return (Vector2)target.transform.position +
            (Vector2)target.transform.right * OrreryUnits.MetersToWorld(SafeOffset(p.TargetOffsetXMeters)) +
            (Vector2)target.transform.up * OrreryUnits.MetersToWorld(SafeOffset(p.TargetOffsetYMeters));
    }

    private static bool TryResolve(Profile p, OrreryFocusProfile.Resolved focus,
        out Schedule.Settings settings, out float rangeWorld)
    {
        float duration = p.DurationSeconds * Mathf.Max(0f, 1f + focus.DurationBonus * p.DurationBonusScale);
        float range = p.TetherRangeMeters * Mathf.Max(0f, 1f + focus.RangeBonus * p.RangeBonusScale);
        settings = new Schedule.Settings(duration, p.FirstStrikeDelaySeconds,
            p.StrikeIntervalSeconds, p.MaxStrikes, p.RangeBreakGraceSeconds);
        rangeWorld = OrreryUnits.MetersToWorld(range);
        return settings.IsValid && Finite(range) && range > 0f && range <= 10000f &&
            Finite(p.CursorAcquisitionRadiusMeters) && (p.CursorAcquisitionRadiusMeters == -1f || p.CursorAcquisitionRadiusMeters >= 0f) &&
            Nonnegative(p.IntegratedSecondsPerStrike) && Nonnegative(p.StrikeDamageMultiplier) &&
            Finite(p.AdditionalDamagePercentPerStrike) && Nonnegative(p.FinalStrikeDamageMultiplier) &&
            Nonnegative(p.ImplementDamageBonusScale) && Nonnegative(p.RangeBonusScale) && Nonnegative(p.DurationBonusScale) &&
            Finite(p.CritChanceBonusPoints) && Finite(p.CritDamageBonusPercent) && Finite(p.StatusChanceBonusPoints);
    }

    internal static float BoltLifetime(Profile p)
    {
        return Finite(p.BoltLifetimeSeconds)
            ? Mathf.Clamp(p.BoltLifetimeSeconds, 0f, MaximumBoltLifetimeSeconds) : 0f;
    }

    private static float SafeOffset(float value)
    { return Finite(value) ? Mathf.Clamp(value, -10000f, 10000f) : 0f; }

    private static bool Nonnegative(float value) { return Finite(value) && value >= 0f; }
    private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
}
